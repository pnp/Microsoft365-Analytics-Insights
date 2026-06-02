using App.ControlPanel.Engine.Entities;
using Azure.Core;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Extracts the downloaded WebJob.Activity zip and locates the profiling automation PowerShell scripts on disk.
    /// The script paths are then passed to <see cref="JobTasks.RunbookUploadTask"/>s, which upload the bodies straight
    /// into the Automation account via the runbook draft content API.
    /// 
    /// We deliberately do NOT upload the scripts to a storage account first. The Automation control plane fetches
    /// PublishContentLink URLs from outside the customer VNet, which fails when the storage account has public
    /// network access disabled - and there is no way to route that fetch through a private endpoint.
    /// </summary>
    public class ProfilingScriptsLocateTask : InstallTaskInAzResourceGroup<RunbookFileLocalLocations>
    {
        public ProfilingScriptsLocateTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override Task<RunbookFileLocalLocations> ExecuteTaskReturnResult(object contextArg)
        {
            // Previous task should send LocalStorageBlobInfo of downloaded solution
            var localStorageBlobInfo = base.EnsureContextArgType<LocalStorageInstallSourceInfo>(contextArg);

            var psFiles = GetRunbookFileLocalLocations(localStorageBlobInfo.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity));

            if (psFiles == null || !IsValid(psFiles))
            {
                // Error already reported in GetRunbookFileLocalLocations
                return Task.FromResult<RunbookFileLocalLocations>(null);
            }

            _logger.LogInformation("Located profiling automation PowerShell scripts on disk; ready to upload as runbook drafts.");
            return Task.FromResult(psFiles);
        }

        /// <summary>
        /// All three PowerShell script paths have been filled in and exist on disk.
        /// </summary>
        static bool IsValid(RunbookFileLocalLocations files) =>
            !string.IsNullOrEmpty(files.AggregationStatusPS) &&
            !string.IsNullOrEmpty(files.DatabaseMaintenancePS) &&
            !string.IsNullOrEmpty(files.WeeklyPS) &&
            File.Exists(files.AggregationStatusPS) &&
            File.Exists(files.DatabaseMaintenancePS) &&
            File.Exists(files.WeeklyPS);

        /// <summary>
        /// Find PS files in the control-panel zip file
        /// </summary>
        RunbookFileLocalLocations GetRunbookFileLocalLocations(LocalStorageBlobInfo localStorageBlobInfo)
        {
            DirectoryInfo zipContentsDir;
            try
            {
                zipContentsDir = ZipFileTasks.Unzip(localStorageBlobInfo, _logger);
            }
            catch (Exception ex)
            {
                // Give context to the error
                throw new ApplicationException($"Could not extract control-panel app: '{ex.Message}'");
            }

            var profilingPowerShellScripts = new RunbookFileLocalLocations();

            // Find the PS files in the expected sub-directory
            var psSubDir = Path.Combine(zipContentsDir.FullName, InstallerConstants.FILENAME_PS_PROFILING_SUB_DIR);
            if (Directory.Exists(psSubDir))
            {
                var subDirInfo = new DirectoryInfo(psSubDir);
                var psFiles = subDirInfo.GetFiles("*.ps1");
                foreach (var psFile in psFiles)
                {
                    if (string.Equals(psFile.Name, InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_AggregationStatus, StringComparison.OrdinalIgnoreCase))
                        profilingPowerShellScripts.AggregationStatusPS = psFile.FullName;
                    else if (string.Equals(psFile.Name, InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_DatabaseMaintenance, StringComparison.OrdinalIgnoreCase))
                        profilingPowerShellScripts.DatabaseMaintenancePS = psFile.FullName;
                    else if (string.Equals(psFile.Name, InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_Weekly, StringComparison.OrdinalIgnoreCase))
                        profilingPowerShellScripts.WeeklyPS = psFile.FullName;
                }
            }
            else
            {
                _logger.LogError($"Could not find the expected PowerShell files (" +
                    $"{InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_AggregationStatus}, " +
                    $"{InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_DatabaseMaintenance}, " +
                    $"{InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_Weekly}" +
                    $") in the expected directory ({psSubDir}), in activity webjob zip file. Try a newer build?");
            }

            return profilingPowerShellScripts;
        }
    }

    /// <summary>
    /// Local file-system locations of the profiling automation PowerShell scripts.
    /// </summary>
    public class RunbookFileLocalLocations
    {
        public string AggregationStatusPS { get; set; }
        public string DatabaseMaintenancePS { get; set; }
        public string WeeklyPS { get; set; }
    }
}
