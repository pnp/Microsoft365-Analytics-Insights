using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    public class SqlInstallerTasks
    {
        public CancellationToken CancellationToken { get; set; }
        internal bool SchemaUpgradeDeferred { get; private set; }
        private readonly SolutionInstallConfig _config;
        private readonly FileInfo _exeFile;
        private readonly DatabasePaaSInfo _dbInfo;
        private readonly ILogger _logger;
        private readonly string _installedByUsername;
        private readonly Func<string, Task<bool>> _verifySqlCallback;

        /// <param name="configPassword">
        /// No longer used: the configuration snapshot registered in the database carries no secrets, so it is not
        /// encrypted (<see cref="SolutionInstallConfig.ToDatabaseSnapshotJson"/>).
        /// </param>
        public SqlInstallerTasks(SolutionInstallConfig config, FileInfo installerExeFileInfo, DatabasePaaSInfo dbInfo, ILogger logger, string installedByUsername, string configPassword,
            Func<string, Task<bool>> verifySqlCallback)
        {
            _config = config;
            _exeFile = installerExeFileInfo;
            _dbInfo = dbInfo;
            _logger = logger;
            _installedByUsername = installedByUsername;
            _verifySqlCallback = verifySqlCallback;
        }

        public async Task UpdateSqlDatabaseSchemaAndDataFromDownloadedInstaller(FileInfo installerExeFile, List<InstallLogEventArgs> installLogEvents)
        {
            CancellationToken.ThrowIfCancellationRequested();
            // Init DB schema with downloaded control panel app?
            if (_config.TasksConfig.UpgradeSchema)
            {
                // Run downloaded installer to init DB schema
                if (installerExeFile != null) await InitDatabaseSchema(_config.SharePointConfig.TargetSites);
                else _logger.LogError("Couldn't find installer application to initialise database with.");
            }

            CancellationToken.ThrowIfCancellationRequested();
            // Register events & config via downloaded installer
            if (_config.TasksConfig.RegisterConfig)
            {
                try
                {
                    await RegisterConfigAndStatus(installLogEvents);
                }
                catch (UnexpectedInstallException ex)
                {
                    // Shouldn't be fatal
                    _logger.LogError(ex.Message);
                }
            }
        }

        /// <summary>
        /// Upgrade/init DB via control-panel download.
        /// DB schema is control via EF migration. We have to assume the only valid model is via the downloaded build.
        /// Ergo we run the downloaded control-panel to deal with schema & run it via a specific switch
        /// </summary>
        internal async Task InitDatabaseSchema(List<string> targetSites)
        {
            var upgradeInfo = CreateDatabaseUpgradeInfo(targetSites);

            // A connection string with no login means the SQL server has SQL authentication disabled, so
            // the downloaded app has to authenticate with Microsoft Entra ID. It cannot use a managed
            // identity - it runs on the operator's machine - so hand it the installer's own service
            // principal, which is the identity assigned as the server's Entra administrator. See #117.
            var needsEntraAuth = AzureSqlTokenAuth.NeedsAccessToken(upgradeInfo.ConnectionString);
            if (needsEntraAuth)
            {
                // Stop BEFORE running a build that cannot possibly succeed. Such a build opens the
                // connection with no credentials and fails inside Entity Framework with an error about the
                // 'master' database, naming neither Entra ID nor the real cause - and there is no way to
                // make it connect, because an Entra-only server has no SQL login to fall back to.
                var capability = DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(_exeFile?.Directory);
                if (capability == DownloadedBuildCapability.NotSupported)
                {
                    throw new UnexpectedInstallException(DownloadedBuildLacksEntraSqlAuth);
                }

                if (capability == DownloadedBuildCapability.Supported)
                {
                    _logger.LogInformation(
                        "The database uses Microsoft Entra ID authentication; the downloaded control-panel app supports it and " +
                        "will sign in with the installer's service principal.");
                }
                else
                {
                    _logger.LogInformation(
                        "The database uses Microsoft Entra ID authentication, so the downloaded control-panel app will sign in with " +
                        "the installer's service principal. NOTE: could not confirm the downloaded release supports Microsoft Entra " +
                        "ID authentication - if the upgrade fails with a login error, that build predates the support and you " +
                        "should deploy from a local release override instead.");
                }
            }

            // The upgrade's payload carries the SQL administrator password or the installer's client secret, so the
            // downloaded app gets it in a file only this account can open (SendMsgToInstaller), never on its command line.
            // A release older than this installer can only take it on the command line: stop rather than put it there.
            var payloadFileSupport = DownloadedBuildCapabilityProbe.ReadsInitDbPayloadFile(_exeFile?.Directory);
            if (payloadFileSupport == DownloadedBuildCapability.NotSupported)
            {
                throw new UnexpectedInstallException(DownloadedBuildTooOldForPayloadFile);
            }
            if (payloadFileSupport == DownloadedBuildCapability.Unknown)
            {
                _logger.LogInformation(
                    "NOTE: could not confirm that the downloaded control-panel app reads the database credentials from a file. " +
                    "If the database upgrade fails as soon as it starts, that release is older than this installer: deploy it " +
                    "with the installer from the same release instead.");
            }

            _logger.LogInformation($"Calling downloaded control-panel app to init/update database. This could take a while if the existing schema needs updating.");

            var result = await SendMsgToInstaller(InstallerConstants.PARAM_INITDB, upgradeInfo.ToBase64());
            if (result.FailedToStart)
            {
                SchemaUpgradeDeferred = true;
                _logger.LogError("Database initialisation did not run because Windows could not launch the downloaded installer. " +
                    "Continuing the remaining installation tasks, but WebJobs will remain paused (WEBJOBS_STOPPED=1). " +
                    "Apply the matching release's manual SQL upgrade scripts in migration-id order, or re-run the installer from an approved machine. " +
                    "Verify the schema and matching binaries before restoring WebJobs.");
                return;
            }
            if (!result.Success)
            {
                throw new UnexpectedInstallException(
                    $"Database initialisation failed: downloaded control-panel app exited with code {result.ExitCode}. " +
                    $"Last output: {LastNonEmptyLine(result.StandardOutput, result.StandardError) ?? "(no output captured)"}. " +
                    DownloadedBuildEntraHint(needsEntraAuth, result.StandardOutput, result.StandardError) +
                    $"Full details in Windows application log (event ID {InstallerConstants.EVENT_LOG_CATEGORY_ID}).");
            }
            else
            {
                var lastLine = LastNonEmptyLine(result.StandardOutput);
                _logger.LogInformation($"Database initialisation completed (exit 0)" + (lastLine != null ? $": {lastLine}" : ".") +
                    $" Full details in Windows application log (event ID {InstallerConstants.EVENT_LOG_CATEGORY_ID}).");
            }
        }

        internal const string DownloadedBuildTooOldForPayloadFile =
            "The control-panel app downloaded for this release is older than this installer, and can only receive the " +
            "database credentials on its command line, where process auditing (for example Microsoft Defender for Endpoint, " +
            "Sysmon or Security event 4688) records them. This installer no longer passes them that way, so the schema " +
            "upgrade was not run and nothing was changed in the database. Deploy a release at least as new as this " +
            "installer (with a local release override, use the ControlPanelApp.zip from this installer's release), or " +
            "deploy the older release with the installer from that release. An older installer can't read the secrets in a " +
            "configuration file this installer has saved, and says the password is wrong: enter the secrets again there " +
            "before you save the file, or saving it will store them empty.";

        internal const string DownloadedBuildLacksEntraSqlAuth =
            "This database uses Microsoft Entra ID authentication, but the control-panel app downloaded for this " +
            "release has no support for it, so it cannot sign in to the database at all. Nothing was changed in the " +
            "database. Either install from a local release override built from a version that supports Microsoft " +
            "Entra ID authentication for Azure SQL, or use a SQL server that has SQL authentication enabled.";

        /// <summary>
        /// The refusals <see cref="InitDatabaseSchema"/> makes about the downloaded build, made before the upgrade pauses the
        /// WebJobs and stops the site (<c>ConfigureAzureComponentsTasks.RunUpgradeDeploymentAsync</c>'s preflight), so that a
        /// refusal leaves both as they were. InitDatabaseSchema still makes them too. Only a build the probe can positively
        /// identify as lacking the support is refused; one it can't read proceeds, as it does there.
        /// </summary>
        internal Task CheckDownloadedBuildBeforeUpgradeAsync()
        {
            if (!_config.TasksConfig.UpgradeSchema || _exeFile == null) return Task.CompletedTask;

            if (AzureSqlTokenAuth.NeedsAccessToken(_dbInfo?.ConnectionString)
                && DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(_exeFile.Directory) == DownloadedBuildCapability.NotSupported)
            {
                throw new UnexpectedInstallException(DownloadedBuildLacksEntraSqlAuth);
            }
            if (DownloadedBuildCapabilityProbe.ReadsInitDbPayloadFile(_exeFile.Directory) == DownloadedBuildCapability.NotSupported)
            {
                throw new UnexpectedInstallException(DownloadedBuildTooOldForPayloadFile);
            }
            return Task.CompletedTask;
        }

        internal DatabaseUpgradeInfo CreateDatabaseUpgradeInfo(List<string> targetSites)
        {
            var upgradeInfo = new DatabaseUpgradeInfo
            {
                ConnectionString = _dbInfo.ConnectionString,
                OrgURLs = targetSites
            };
            if (AzureSqlTokenAuth.NeedsAccessToken(upgradeInfo.ConnectionString))
            {
                upgradeInfo.EntraTenantId = _config.InstallerAccount?.DirectoryId;
                upgradeInfo.EntraClientId = _config.InstallerAccount?.ClientId;
                upgradeInfo.EntraClientSecret = _config.InstallerAccount?.Secret;
            }
            return upgradeInfo;
        }

        internal async Task RegisterConfigAndStatus(List<InstallLogEventArgs> installLogEvents)
        {
            var status = new InstallStatus
            {
                ConfigurationJSon = _config.ToDatabaseSnapshotJson(),
                Events = installLogEvents,
                SetupUserName = _installedByUsername,
                ConnectionString = _dbInfo.ConnectionString
            };

            // Same reasoning as the schema upgrade: the downloaded control-panel process has to authenticate
            // for itself, and a token-less connection string means Microsoft Entra ID. See issue #117.
            if (AzureSqlTokenAuth.NeedsAccessToken(status.ConnectionString))
            {
                status.EntraTenantId = _config.InstallerAccount?.DirectoryId;
                status.EntraClientId = _config.InstallerAccount?.ClientId;
                status.EntraClientSecret = _config.InstallerAccount?.Secret;
            }

            // The payload goes in a file whatever the build: --registerconfig has always taken one.
            var result = await SendMsgToInstaller(InstallerConstants.PARAM_REGISTERCONFIG, status.ToBase64());

            if (result.FailedToStart)
            {
                _logger.LogWarning("Configuration and installation status were not registered because Windows could not launch the downloaded installer. " +
                    "Continuing installation. Keep the saved installer configuration and local install log; re-run from an approved machine to register them.");
                return;
            }
            if (!result.Success)
            {
                throw new UnexpectedInstallException(
                    $"Configuration registration failed: downloaded control-panel app exited with code {result.ExitCode}. " +
                    $"See Windows application log (event ID {InstallerConstants.EVENT_LOG_CATEGORY_ID}).");
            }
            else
            {
                _logger.LogInformation($"Configuration & status successfully registered in database.");
            }
        }

        private class ChildResult
        {
            public bool FailedToStart { get; set; }
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }

        /// <summary>
        /// Runs one operation in the downloaded control-panel app, handing it <paramref name="payload"/> in a file only
        /// this Windows account can open (<see cref="InstallerChildPayloadFile"/>). Its command line carries just that
        /// file's name: the payload holds the SQL administrator password or the installer's client secret, and command
        /// lines are recorded by process auditing. The file is deleted once the app has exited, however it ended.
        /// </summary>
        async Task<ChildResult> SendMsgToInstaller(string param, string payload)
        {
            var payloadFile = InstallerChildPayloadFile.Write(payload);
            try
            {
                return await RunDownloadedInstaller(param, payloadFile.Base64Encode());
            }
            finally
            {
                if (!InstallerChildPayloadFile.Delete(payloadFile))
                {
                    _logger?.LogWarning("Could not delete '{PayloadFile}', which passed the database credentials to the downloaded " +
                        "control-panel app. Only your Windows account can open it; delete it.", payloadFile);
                }
            }
        }

        async Task<ChildResult> RunDownloadedInstaller(string param, string val)
        {
            CancellationToken.ThrowIfCancellationRequested();
            // Test
            bool sqlTestWorked = await _verifySqlCallback(_dbInfo.ConnectionString);
            if (!sqlTestWorked)
            {
                _logger.LogError("Skipping control-panel app to init/update database due to a failed SQL connection test. " +
                    "Fix the authentication or connectivity problem reported above and re-run the installer.");
                return new ChildResult { Success = false, ExitCode = -1 };
            }

            CancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Starting '{_exeFile.FullName}' with params '{param} <args-redacted>'");

            // Redirect stdout/stderr so we can echo the child's progress into the install log
            // instead of asking the operator to dig through Windows Event Viewer.
            var startInfo = new ProcessStartInfo
            {
                FileName = _exeFile.FullName,
                Arguments = $"{param} {val}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var stdout = new System.Text.StringBuilder();
            var stderr = new System.Text.StringBuilder();

            using (var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
            {
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    stdout.AppendLine(e.Data);
                    _logger.Log(ChildOutputLevel(e.Data), "  [child] {ChildOutput}", e.Data);
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    stderr.AppendLine(e.Data);
                    _logger.LogWarning($"  [child stderr] {e.Data}");
                };
                try
                {
                    StartChildProcess(proc);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception ||
                    ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    CancellationToken.ThrowIfCancellationRequested();
                    _logger.LogWarning("Downloaded installer launch failed: operation={Operation} exceptionType={ExceptionType} nativeError={NativeError}. " +
                        "The executable could not be started; check Windows application-control policy and file permissions. No command arguments are logged.",
                        param, ex.GetType().Name, (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode);
                    return new ChildResult { FailedToStart = true };
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await WaitForChildExitAsync(proc, CancellationToken, _logger);

                return new ChildResult
                {
                    Success = proc.ExitCode == 0,
                    ExitCode = proc.ExitCode,
                    StandardOutput = stdout.ToString(),
                    StandardError = stderr.ToString(),
                };
            }
        }

        internal virtual void StartChildProcess(Process process) => process.Start();

        internal static async Task WaitForChildExitAsync(Process process, CancellationToken cancellationToken, ILogger logger)
        {
            using (cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        logger.LogWarning("Stopping the downloaded installer. Database changes already committed are not rolled back; re-run the installer to finish the upgrade.");
                        process.Kill();
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    logger.LogWarning("Could not stop the downloaded installer: {Message}. Waiting for it to exit before returning.", ex.Message);
                }
            }))
            {
                await Task.Run(() => process.WaitForExit());
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal static LogLevel ChildOutputLevel(string line)
        {
            if (line != null && (line.Contains("MigrationSqlProgress:") || line.Contains("MigrationOutcome:")))
            {
                if (line.Contains("severity=Error") || line.Contains("outcome=Failed")) return LogLevel.Error;
                if (line.Contains("severity=Warning")) return LogLevel.Warning;
            }
            return LogLevel.Information;
        }

        private static string LastNonEmptyLine(params string[] sources)
        {
            foreach (var s in sources)
            {
                if (string.IsNullOrEmpty(s)) continue;
                var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0) return lines[lines.Length - 1].Trim();
            }
            return null;
        }

        /// <summary>
        /// Extra guidance appended when a schema upgrade fails against an Entra-authenticated database and
        /// the downloaded control-panel app never reported authenticating with Entra ID. Empty otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Worth stating outright because the raw failure points somewhere misleading. A build without Entra
        /// support opens the connection with no credentials at all; SQL rejects it with
        /// <c>Login failed for user ''</c>; Entity Framework reads that as "the database does not exist",
        /// tries to CREATE it, and needs the <c>master</c> database to do so - so the exception an operator
        /// finally sees complains about <c>master</c> and about credentials being "removed from the
        /// connection string", naming neither Entra ID nor the real problem.
        /// </para>
        /// <para>
        /// The signal is the announcement a supporting build makes before it opens anything
        /// (<see cref="DatabaseUpgrader.EntraAuthAnnouncement"/>), whose absence from the captured output
        /// means the downloaded build has no Entra support. Deliberately phrased as the likely cause rather
        /// than a certainty: a future build could reword the line, and the parent cannot know what wording
        /// the release it downloaded used.
        /// </para>
        /// </remarks>
        internal static string DownloadedBuildEntraHint(bool needsEntraAuth, string standardOutput, string standardError)
        {
            if (!needsEntraAuth) return string.Empty;

            var output = (standardOutput ?? string.Empty) + "\n" + (standardError ?? string.Empty);
            if (output.IndexOf(DatabaseUpgrader.EntraAuthAnnouncement, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // It did authenticate with Entra ID, so this failure is something else entirely.
                return string.Empty;
            }

            return "This database uses Microsoft Entra ID authentication, but the downloaded control-panel app did not report " +
                   "authenticating with it. That is what a release predating Entra ID support looks like: it opens the connection " +
                   "with no credentials, SQL rejects it (\"Login failed for user ''\"), and Entity Framework then assumes the " +
                   "database is missing and tries to create it, which is why the error mentions the 'master' database rather than " +
                   "a login. Install from a local release override built from a version that supports Entra ID, or use a " +
                   "SQL-authentication server. ";
        }

    }
}
