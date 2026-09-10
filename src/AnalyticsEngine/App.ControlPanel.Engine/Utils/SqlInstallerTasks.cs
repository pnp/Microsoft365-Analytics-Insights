using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using DataUtils;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    public class SqlInstallerTasks
    {
        private readonly SolutionInstallConfig _config;
        private readonly FileInfo _exeFile;
        private readonly DatabasePaaSInfo _dbInfo;
        private readonly ILogger _logger;
        private readonly string _installedByUsername;
        private readonly string _configPassword;
        private readonly Func<string, Task<bool>> _verifySqlCallback;

        public SqlInstallerTasks(SolutionInstallConfig config, FileInfo installerExeFileInfo, DatabasePaaSInfo dbInfo, ILogger logger, string installedByUsername, string configPassword,
            Func<string, Task<bool>> verifySqlCallback)
        {
            _config = config;
            _exeFile = installerExeFileInfo;
            _dbInfo = dbInfo;
            _logger = logger;
            _installedByUsername = installedByUsername;
            _configPassword = configPassword;
            _verifySqlCallback = verifySqlCallback;
        }

        public async Task UpdateSqlDatabaseSchemaAndDataFromDownloadedInstaller(FileInfo installerExeFile, List<InstallLogEventArgs> installLogEvents)
        {
            // Init DB schema with downloaded control panel app?
            if (_config.TasksConfig.UpgradeSchema)
            {
                // Run downloaded installer to init DB schema
                if (installerExeFile != null) await InitDatabaseSchema(_config.SharePointConfig.TargetSites);
                else _logger.LogError("Couldn't find installer application to initialise database with.");
            }

            // Register events & config via downloaded installer
            if (_config.TasksConfig.RegisterConfig)
            {
                try
                {
                    await RegisterConfigAndStatus(installLogEvents, _configPassword);
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
            // Create DB migration info
            var upgradeInfo = new DatabaseUpgradeInfo();
            upgradeInfo.ConnectionString = _dbInfo.ConnectionString;
            upgradeInfo.OrgURLs = targetSites;

            // A connection string with no login means the SQL server has SQL authentication disabled, so
            // the downloaded app has to authenticate with Microsoft Entra ID. It cannot use a managed
            // identity - it runs on the operator's machine - so hand it the installer's own service
            // principal, which is the identity assigned as the server's Entra administrator. See #117.
            var needsEntraAuth = AzureSqlTokenAuth.NeedsAccessToken(upgradeInfo.ConnectionString);
            if (needsEntraAuth)
            {
                upgradeInfo.EntraTenantId = _config.InstallerAccount?.DirectoryId;
                upgradeInfo.EntraClientId = _config.InstallerAccount?.ClientId;
                upgradeInfo.EntraClientSecret = _config.InstallerAccount?.Secret;

                // Stop BEFORE running a build that cannot possibly succeed. Such a build opens the
                // connection with no credentials and fails inside Entity Framework with an error about the
                // 'master' database, naming neither Entra ID nor the real cause - and there is no way to
                // make it connect, because an Entra-only server has no SQL login to fall back to.
                var capability = DownloadedBuildCapabilityProbe.SupportsEntraSqlAuth(_exeFile?.Directory);
                if (capability == DownloadedBuildCapability.NotSupported)
                {
                    throw new UnexpectedInstallException(
                        "This database uses Microsoft Entra ID authentication, but the control-panel app downloaded for this " +
                        "release has no support for it, so it cannot sign in to the database at all. Nothing was changed in the " +
                        "database. Either install from a local release override built from a version that supports Microsoft " +
                        "Entra ID authentication for Azure SQL, or use a SQL server that has SQL authentication enabled.");
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

            _logger.LogInformation($"Calling downloaded control-panel app to init/update database. This could take a while if the existing schema needs updating.");

            var result = await SendMsgToInstaller(InstallerConstants.PARAM_INITDB, upgradeInfo.ToBase64());
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

        internal async Task RegisterConfigAndStatus(List<InstallLogEventArgs> installLogEvents, string configPassword)
        {
            var status = new InstallStatus
            {
                ConfigurationJSon = _config.ToJson(configPassword),
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

            // Write a temp file to pass to control-panel
            var tempFileName = Path.GetTempFileName();
            File.WriteAllText(tempFileName, status.ToBase64());

            var result = await SendMsgToInstaller(InstallerConstants.PARAM_REGISTERCONFIG, tempFileName.Base64Encode());

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
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string StandardOutput { get; set; }
            public string StandardError { get; set; }
        }

        async Task<ChildResult> SendMsgToInstaller(string param, string val)
        {
            // Test
            bool sqlTestWorked = await _verifySqlCallback(_dbInfo.ConnectionString);
            if (!sqlTestWorked)
            {
                _logger.LogInformation("Skipping control-panel app to init/update database due to failed connectivity test. Verify your current IP address is correct in the SQL Server firewall settings", true);
                return new ChildResult { Success = false, ExitCode = -1 };
            }

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
                    _logger.LogInformation($"  [child] {e.Data}");
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    stderr.AppendLine(e.Data);
                    _logger.LogWarning($"  [child stderr] {e.Data}");
                };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await Task.Run(() => proc.WaitForExit());

                return new ChildResult
                {
                    Success = proc.ExitCode == 0,
                    ExitCode = proc.ExitCode,
                    StandardOutput = stdout.ToString(),
                    StandardError = stderr.ToString(),
                };
            }
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
