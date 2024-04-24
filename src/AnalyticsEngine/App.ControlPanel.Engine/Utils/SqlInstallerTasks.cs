using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Common.DataUtils;
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

            var success = await SendMsgToInstaller(InstallerConstants.PARAM_INITDB, upgradeInfo.ToBase64());
            if (!success)
            {
                throw new UnexpectedInstallException($"Unexpected result from downloaded control-panel app. Database initialisation will need to be done manually- see event log ID {InstallerConstants.EVENT_LOG_CATEGORY_ID}");
            }
            else
            {
                _logger.LogInformation($"Database initialisation process exited. RECOMMENDED: check Windows application log for event ID '{InstallerConstants.EVENT_LOG_CATEGORY_ID}' to verify success.");
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

            var success = await SendMsgToInstaller(InstallerConstants.PARAM_REGISTERCONFIG, tempFileName.Base64Encode());

            if (!success)
            {
                throw new UnexpectedInstallException($"Unexpected result from downloaded control-panel app. Configuration registration failed - see event log ID {InstallerConstants.EVENT_LOG_CATEGORY_ID}");
            }
            else
            {
                _logger.LogInformation($"Configuration & status successfully registered in database.");
            }
        }

        async Task<bool> SendMsgToInstaller(string param, string val)
        {
            // Test
            bool sqlTestWorked = await _verifySqlCallback(_dbInfo.ConnectionString);
            if (!sqlTestWorked)
            {
                _logger.LogInformation("Skipping control-panel app to init/update database due to failed connectivity test. Verify your current IP address is correct in the SQL Server firewall settings", true);
                return false;
            }

            Console.WriteLine($"Starting '{_exeFile.FullName}' with params '{param} {val}'");

            ProcessStartInfo controlPanelStartInfo = new ProcessStartInfo();

            // --initdb "hash"
            controlPanelStartInfo.Arguments = $"{param} {val}";
            controlPanelStartInfo.FileName = _exeFile.FullName;
            controlPanelStartInfo.WindowStyle = ProcessWindowStyle.Normal;
            int exitCode;


            // Run the external process & wait for it to finish
            using (var proc = Process.Start(controlPanelStartInfo))
            {
                proc.WaitForExit();

                // Retrieve the app's exit code
                exitCode = proc.ExitCode;

                return exitCode == 0;
            }
        }

    }
}
