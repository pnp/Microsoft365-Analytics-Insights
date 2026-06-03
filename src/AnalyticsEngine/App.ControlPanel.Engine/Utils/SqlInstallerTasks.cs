using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using DataUtils;
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

            _logger.LogInformation($"Calling downloaded control-panel app to init/update database. This could take a while if the existing schema needs updating.");

            var result = await SendMsgToInstaller(InstallerConstants.PARAM_INITDB, upgradeInfo.ToBase64());
            if (!result.Success)
            {
                throw new UnexpectedInstallException(
                    $"Database initialisation failed: downloaded control-panel app exited with code {result.ExitCode}. " +
                    $"Last output: {LastNonEmptyLine(result.StandardOutput, result.StandardError) ?? "(no output captured)"}. " +
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

    }
}
