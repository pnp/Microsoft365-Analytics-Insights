using App.ControlPanel.Engine.Entities;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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
        public const string CFG_STORAGE_ACCOUNT_URL = "StorageAccountUrl";
        public const string CFG_STORAGE_NAME = "StorageName";

        private readonly TokenCredential _credential;
        private readonly StorageAccountResource _storageAccount;

        public ProfilingScriptsUploadToBlobStorageTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, TokenCredential credential, StorageAccountResource storageAccount) : base(config, logger, azureLocation, tags)
        {
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
            _storageAccount = storageAccount ?? throw new ArgumentNullException(nameof(storageAccount));
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

                // Check if public network access is disabled and temporarily enable it for the upload
                var publicAccessWasDisabled = await EnsurePublicNetworkAccessEnabled();
                try
                {
                    // Authenticate to storage account using RBAC via Entra ID credentials
                    var blobServiceClient = new BlobServiceClient(new Uri(_config[CFG_STORAGE_ACCOUNT_URL]), _credential);

                    // Get a reference to a container named "automation" in the storage account
                    var containerClient = blobServiceClient.GetBlobContainerClient("automation");

                    // RBAC role assignments can take several minutes to propagate to the storage data plane.
                    // Retry with backoff if we get a 403 AuthorizationFailure on the first data-plane call.
                    const int maxRetries = 5;
                    const int initialDelaySeconds = 15;
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await containerClient.CreateIfNotExistsAsync();
                            break;
                        }
                        catch (RequestFailedException ex) when (ex.Status == 403 && attempt < maxRetries)
                        {
                            var delay = TimeSpan.FromSeconds(initialDelaySeconds * Math.Pow(2, attempt));
                            _logger.LogWarning($"Storage authorization failed (attempt {attempt + 1}/{maxRetries + 1}). " +
                                $"RBAC role or network change may still be propagating. Retrying in {delay.TotalSeconds:0}s...");
                            await Task.Delay(delay);
                        }
                    }

                    // Get a reference to each blob in the container
                    var blobClientWeekly = containerClient.GetBlobClient(GetAzBlobRunbookFileName(InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_Weekly));
                    var blobClientAggregationStatus = containerClient.GetBlobClient(GetAzBlobRunbookFileName(InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_AggregationStatus));
                    var blobClientDatabaseMaintenance = containerClient.GetBlobClient(GetAzBlobRunbookFileName(InstallerConstants.FILENAME_PS_PROFILING_AUTOMATION_DatabaseMaintenance));

                    // Upload the files to the blob storage account

                    // Workaround: the automation account doesn't like the SAS URL, so we use the direct URL
                    // https://github.com/Azure/bicep/issues/8234
                    var weeklyPsUploadResult = await blobClientWeekly.UploadAsync(psRunbookFileLocalLocations.WeeklyPS, true);
                    var aggregationStatusPSUploadResult = await blobClientAggregationStatus.UploadAsync(psRunbookFileLocalLocations.AggregationStatusPS, true);
                    var databaseMaintenancePSUploadResult = await blobClientDatabaseMaintenance.UploadAsync(psRunbookFileLocalLocations.DatabaseMaintenancePS, true);

                    // Generate a user delegation key for SAS URLs
                    var userDelegationKey = await blobServiceClient.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(2));

                    azUrls.WeeklyPS = GetSharableUrl(blobClientWeekly, userDelegationKey, blobServiceClient.AccountName).ToString();
                    azUrls.WeeklyFileHash = BitConverter.ToString(weeklyPsUploadResult.Value.ContentHash).Replace("-", string.Empty);

                    azUrls.AggregationStatusPS = GetSharableUrl(blobClientAggregationStatus, userDelegationKey, blobServiceClient.AccountName).ToString();
                    azUrls.AggregationStatusFileHash = BitConverter.ToString(aggregationStatusPSUploadResult.Value.ContentHash).Replace("-", string.Empty);

                    azUrls.DatabaseMaintenancePS = GetSharableUrl(blobClientDatabaseMaintenance, userDelegationKey, blobServiceClient.AccountName).ToString();
                    azUrls.DatabaseMaintenanceFileHash = BitConverter.ToString(databaseMaintenancePSUploadResult.Value.ContentHash).Replace("-", string.Empty);

                    _logger.LogInformation($"Automation PS files uploaded to blob storage account '{_config[CFG_STORAGE_NAME]}' and read-only sharable links generated");
                }
                finally
                {
                    // Restore public network access to disabled if we enabled it
                    if (publicAccessWasDisabled)
                    {
                        await SetPublicNetworkAccess(StoragePublicNetworkAccess.Disabled);
                    }
                }
                return azUrls;
            }
        }
        string GetAzBlobRunbookFileName(string runbookName) => $"profiling/{runbookName}";

        /// <summary>
        /// Checks if public network access is disabled on the storage account and enables it if needed.
        /// Returns true if public network access was disabled and has been enabled (and needs restoring later).
        /// </summary>
        async Task<bool> EnsurePublicNetworkAccessEnabled()
        {
            var currentData = (await _storageAccount.GetAsync()).Value.Data;
            if (currentData.PublicNetworkAccess == StoragePublicNetworkAccess.Disabled)
            {
                _logger.LogInformation($"Public network access is disabled on storage account '{_storageAccount.Data.Name}'. Enabling temporarily for upload...");
                await SetPublicNetworkAccess(StoragePublicNetworkAccess.Enabled);
                return true;
            }
            return false;
        }

        async Task SetPublicNetworkAccess(StoragePublicNetworkAccess access)
        {
            var patch = new StorageAccountPatch { PublicNetworkAccess = access };
            await _storageAccount.UpdateAsync(patch);
            _logger.LogInformation($"Storage account '{_storageAccount.Data.Name}' public network access set to '{access}'.");
        }

        /// <summary>
        /// Create a read-only sharable URL for the blob using a User Delegation SAS.
        /// Permissions are set to read-only but may need to be revisited.
        /// </summary>
        Uri GetSharableUrl(BlobClient blob, UserDelegationKey userDelegationKey, string accountName)
        {
            var sasBuilder = new BlobSasBuilder()
            {
                BlobContainerName = blob.BlobContainerName,
                BlobName = blob.Name,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow,
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(2),
            };

            // Specify the permissions for the SAS
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            // Generate the User Delegation SAS URI
            var blobUriBuilder = new BlobUriBuilder(blob.Uri)
            {
                Sas = sasBuilder.ToSasQueryParameters(userDelegationKey, accountName)
            };
            return blobUriBuilder.ToUri();
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
