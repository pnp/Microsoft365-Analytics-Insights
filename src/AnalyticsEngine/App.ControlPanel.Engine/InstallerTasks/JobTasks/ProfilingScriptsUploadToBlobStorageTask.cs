using App.ControlPanel.Engine.Entities;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
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
    /// Uploads the usage profiling automation PS files to a storage account and returns the read-only sharable links
    /// </summary>
    public class ProfilingScriptsUploadToBlobStorageTask : InstallTaskInAzResourceGroup<RunbookFileLocalLocations>
    {
        public const string CFG_CONNECTION_STRING = "StorageConnectionString";
        public const string CFG_STORAGE_NAME = "StorageName";

        public ProfilingScriptsUploadToBlobStorageTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override async Task<RunbookFileLocalLocations> ExecuteTaskReturnResult(object contextArg)
        {
            _logger.LogInformation("Uploading Automation PS files to storage account");

            // Previous task should send LocalStorageBlobInfo of downloaded solution
            var localStorageBlobInfo = base.EnsureContextArgType<LocalStorageInstallSourceInfo>(contextArg);

            var psRunbookFileLocalLocations = GetRunbookFileLocalLocations(localStorageBlobInfo.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity));

            // Check that the PS files really exist locally
            if (!psRunbookFileLocalLocations.IsValid)
            {
                // Error reported in GetRunbookFileLocalLocations
                return null;
            }
            else
            {
                // Found the PS files, upload them to the storage account and create sharable links
                var azUrls = new AzStorageRunbookFileLocations();

                // Retrieve storage account information from connection string
                var blobServiceClient = new BlobServiceClient(_config[CFG_CONNECTION_STRING]);

                // Get a reference to a container named "sample-container" in the storage account
                var containerClient = blobServiceClient.GetBlobContainerClient("automation");
                await containerClient.CreateIfNotExistsAsync();

                // Get a reference to a blob named "sample-blob" in the container
                var blobClientWeekly = containerClient.GetBlobClient(GetAzBlobRunbookFileName(InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_Weekly));
                var blobClientAggregationStatus = containerClient.GetBlobClient(GetAzBlobRunbookFileName(InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_AggregationStatus));
                var blobClientDatabaseMaintenance = containerClient.GetBlobClient(GetAzBlobRunbookFileName(InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_DatabaseMaintenance));

                // Upload the files to the blob storage account

                // Workaround: the automation account doesn't like the SAS URL, so we use the direct URL
                // https://github.com/Azure/bicep/issues/8234
                var weeklyPsUploadResult = await blobClientWeekly.UploadAsync(psRunbookFileLocalLocations.WeeklyPS, true);
                var aggregationStatusPSUploadResult = await blobClientAggregationStatus.UploadAsync(psRunbookFileLocalLocations.AggregationStatusPS, true);
                var databaseMaintenancePSUploadResult = await blobClientDatabaseMaintenance.UploadAsync(psRunbookFileLocalLocations.DatabaseMaintenancePS, true);

                azUrls.WeeklyPS = GetSharableUrl(blobClientWeekly).ToString();
                azUrls.WeeklyFileHash = BitConverter.ToString(weeklyPsUploadResult.Value.ContentHash).Replace("-", string.Empty);

                azUrls.AggregationStatusPS = GetSharableUrl(blobClientAggregationStatus).ToString();
                azUrls.AggregationStatusFileHash = BitConverter.ToString(aggregationStatusPSUploadResult.Value.ContentHash).Replace("-", string.Empty);

                azUrls.DatabaseMaintenancePS = GetSharableUrl(blobClientDatabaseMaintenance).ToString();
                azUrls.DatabaseMaintenanceFileHash = BitConverter.ToString(databaseMaintenancePSUploadResult.Value.ContentHash).Replace("-", string.Empty);

                _logger.LogInformation($"Automation PS files uploaded to blob storage account '{_config[CFG_STORAGE_NAME]}' and read-only sharable links generated");
                return azUrls;
            }
        }
        string GetAzBlobRunbookFileName(string runbookName) => $"profiling/{runbookName}";

        /// <summary>
        /// Create a read-only sharable URL for the blob. Permissions are set to read-only but may need to be revisited
        /// </summary>
        Uri GetSharableUrl(BlobClient blob)
        {
            var sasBuilder = new BlobSasBuilder()
            {
                StartsOn = DateTimeOffset.UtcNow,
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(2),
            };

            // Specify the permissions for the SAS
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            // Generate the SAS URI
            return blob.GenerateSasUri(sasBuilder);
        }

        /// <summary>
        /// Find PS files in the control-panel zip file
        /// </summary>
        LocalRunbookFileLocalLocations GetRunbookFileLocalLocations(LocalStorageBlobInfo localStorageBlobInfo)
        {
            // Get control-panel
            DirectoryInfo zipContentsDir = null;
            try
            {
                zipContentsDir = ZipFileTasks.Unzip(localStorageBlobInfo, _logger);
            }
            catch (Exception ex)
            {
                // Give context to the error
                throw new ApplicationException($"Could not extract control-panel app: '{ex.Message}'");
            }

            var profilingPowerShellScripts = new LocalRunbookFileLocalLocations();

            // Find the PS files in the expected sub-directory
            var psSubDir = Path.Combine(zipContentsDir.FullName, InstallerConstants.FILENAME_PS_PROFILING_SUB_DIR);
            if (Directory.Exists(psSubDir))
            {
                var subDirInfo = new DirectoryInfo(psSubDir);

                var psFiles = subDirInfo.GetFiles("*.ps1");
                foreach (var psFile in psFiles)
                {
                    if (psFile.Name.ToLower() == InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_AggregationStatus.ToLower()) profilingPowerShellScripts.AggregationStatusPS = psFile.FullName;
                    else if (psFile.Name.ToLower() == InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_DatabaseMaintenance.ToLower()) profilingPowerShellScripts.DatabaseMaintenancePS = psFile.FullName;
                    else if (psFile.Name.ToLower() == InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_Weekly.ToLower()) profilingPowerShellScripts.WeeklyPS = psFile.FullName;
                }
            }
            else
            {
                _logger.LogError($"Could not find the expected PowerShell files (" +
                    $"{InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_AggregationStatus}, " +
                    $"{InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_DatabaseMaintenance}, {InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_Weekly}" +
                    $") in the expected directory ({psSubDir}), in activity webjob zip file. Try a newer build?");
            }

            return profilingPowerShellScripts;
        }
    }

    /// <summary>
    /// The file locations of the runbook files, either online or local
    /// </summary>
    public abstract class RunbookFileLocalLocations
    {
        public string AggregationStatusPS { get; set; }
        public string DatabaseMaintenancePS { get; set; }
        public string WeeklyPS { get; set; }
    }

    public class AzStorageRunbookFileLocations : RunbookFileLocalLocations
    {
        public string AggregationStatusFileHash { get; set; }
        public string DatabaseMaintenanceFileHash { get; set; }
        public string WeeklyFileHash { get; set; }
    }

    /// <summary>
    /// Checks that the local files exist on the file-system
    /// </summary>
    public class LocalRunbookFileLocalLocations : RunbookFileLocalLocations
    {
        /// <summary>
        /// Not empty and exists on the file-system
        /// </summary>
        public bool IsValid =>
            !string.IsNullOrEmpty(AggregationStatusPS) &&
            !string.IsNullOrEmpty(DatabaseMaintenancePS) &&
            !string.IsNullOrEmpty(WeeklyPS) &&
            File.Exists(AggregationStatusPS) &&
            File.Exists(DatabaseMaintenancePS) &&
            File.Exists(WeeklyPS);
    }
}
