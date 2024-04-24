using App.ControlPanel.Engine.Entities;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Storage account creation & zip tasks
    /// </summary>
    public class AzStorageInstallerTasks
    {
        /// <summary>
        /// Gets blob info for the latest build in the container
        /// </summary>
        internal static AzureStorageInstallSourceInfo GetSoftwareInfoFromContainer(BlobContainerClient container)
        {
            var azStorageInfo = new AzureStorageInstallSourceInfo();

            // Find all components of a software installation; get latest versions
            foreach (var blob in container.GetBlobs(BlobTraits.None, BlobStates.None, InstallerConstants.MasterBuildBlobPrefix))
            {
                if (blob.Name.EndsWith(InstallerConstants.FILENAME_ZIP_WEBJOB_APPINSIGHTS))
                {
                    UpdateComponentInfoIfLatestBlob(azStorageInfo.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights), blob);
                }
                if (blob.Name.EndsWith(InstallerConstants.FILENAME_ZIP_WEBJOB_ACTIVITY))
                {
                    UpdateComponentInfoIfLatestBlob(azStorageInfo.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity), blob);
                }
                if (blob.Name.EndsWith(InstallerConstants.FILENAME_ZIP_AITRACKER))
                {
                    UpdateComponentInfoIfLatestBlob(azStorageInfo.GetSolutionComponentLocation(SoftwareComponent.AITracker), blob);
                }
                if (blob.Name.EndsWith(InstallerConstants.FILENAME_ZIP_CONTROL_PANEL))
                {
                    UpdateComponentInfoIfLatestBlob(azStorageInfo.GetSolutionComponentLocation(SoftwareComponent.ControlPanel), blob);
                }
                if (blob.Name.EndsWith(InstallerConstants.FILENAME_ZIP_WEBSITE))
                {
                    UpdateComponentInfoIfLatestBlob(azStorageInfo.GetSolutionComponentLocation(SoftwareComponent.WebSite), blob);
                }
            }

            return azStorageInfo;
        }

        static void UpdateComponentInfoIfLatestBlob(BlobItemInfo component, BlobItem blob)
        {
            if (component.LastModified < blob.Properties.CreatedOn.Value.DateTime)
            {
                component.Blob = blob;
                component.LastModified = blob.Properties.LastModified.Value.DateTime;
            }
        }
    }
}
