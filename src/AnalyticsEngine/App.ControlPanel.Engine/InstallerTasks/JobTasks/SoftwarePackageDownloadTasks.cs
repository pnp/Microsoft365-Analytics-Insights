using App.ControlPanel.Engine.Entities;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CloudInstallEngine;
using Common.DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{

    public class LatestStableSoftwarePackageDownloadTask : ResourceInstallTask<LocalStorageInstallSourceInfo>
    {
        public const string CFG_KEY_AccountBaseUrl = "AccountBaseUrl", CFG_KEY_SAS = "SAS", CFG_KEY_ContainerName = "ContainerName";
        public LatestStableSoftwarePackageDownloadTask(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        public override async Task<LocalStorageInstallSourceInfo> ExecuteTaskReturnResult(object contextArg)
        {
            // Get sources
            var serviceClient = new BlobServiceClient(new Uri(_config[CFG_KEY_AccountBaseUrl]), new AzureSasCredential(_config[CFG_KEY_SAS]));
            var containerClient = serviceClient.GetBlobContainerClient(_config[CFG_KEY_ContainerName]);

            var sourceInfo = AzStorageInstallerTasks.GetSoftwareInfoFromContainer(containerClient);

            if (!sourceInfo.IsValid)
            {
                throw new UnexpectedInstallException("Couldn't locate source packages correctly");
            }
            _logger.LogInformation("Downloading latest stable release...");

            var locallyDownloadedRelease = new LocalStorageInstallSourceInfo();
            var tempDir = StringUtils.TempDirPath;
            Directory.CreateDirectory(tempDir);

            // Get each local file in parrallel
            var localWebJobActivityTask = Task.Run(() => DownloadAzureBlobToDir(containerClient, sourceInfo.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity).Blob, tempDir));
            var localWebJobAppInsightsTask = Task.Run(() => DownloadAzureBlobToDir(containerClient, sourceInfo.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights).Blob, tempDir));
            var localAITrackerTask = Task.Run(() => DownloadAzureBlobToDir(containerClient, sourceInfo.GetSolutionComponentLocation(SoftwareComponent.AITracker).Blob, tempDir));
            var localControlPanelTask = Task.Run(() => DownloadAzureBlobToDir(containerClient, sourceInfo.GetSolutionComponentLocation(SoftwareComponent.ControlPanel).Blob, tempDir));
            var localWebsiteTask = Task.Run(() => DownloadAzureBlobToDir(containerClient, sourceInfo.GetSolutionComponentLocation(SoftwareComponent.WebSite).Blob, tempDir));

            // Wait for all jobs
            await Task.WhenAll(localWebJobActivityTask, localWebJobActivityTask, localWebJobAppInsightsTask, localWebsiteTask);

            var localWebJobActivity = localWebJobActivityTask.Result;
            var localWebJobAppInsights = localWebJobAppInsightsTask.Result;
            var localAITracker = localAITrackerTask.Result;
            var localControlPanel = localControlPanelTask.Result;
            var localWebsite = localWebsiteTask.Result;

            // Build return structure
            locallyDownloadedRelease.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity).FileLocation = localWebJobActivity;
            locallyDownloadedRelease.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights).FileLocation = localWebJobAppInsights;
            locallyDownloadedRelease.GetSolutionComponentLocation(SoftwareComponent.AITracker).FileLocation = localAITracker;
            locallyDownloadedRelease.GetSolutionComponentLocation(SoftwareComponent.ControlPanel).FileLocation = localControlPanel;
            locallyDownloadedRelease.GetSolutionComponentLocation(SoftwareComponent.WebSite).FileLocation = localWebsite;

            return locallyDownloadedRelease;
        }


        async Task<string> DownloadAzureBlobToDir(BlobContainerClient containerClient, BlobItem blob, string baseDir)
        {
            if (blob == null)
            {
                throw new ArgumentNullException("blob");
            }

            // Read file data from Azure Storage
            byte[] fileBytes = null;
            using (var blobMemoryStream = new MemoryStream())
            {
                var b = containerClient.GetBlobClient(blob.Name);
                await b.DownloadToAsync(blobMemoryStream);
                fileBytes = blobMemoryStream.ToArray();
            }

            // Generate file name in temp dir
            var fileName = baseDir + @"\" + blob.Name.Replace(@"/".ToCharArray()[0], @"\".ToCharArray()[0]);

            // Check parent dir exists
            var fileDir = new FileInfo(fileName).Directory;
            if (!fileDir.Exists)
            {
                fileDir.Create();
            }

            // Save zip-file
            File.WriteAllBytes(fileName, fileBytes);

            return fileName;
        }
    }

    public class UseLocalOverrideDownloadTask : ResourceInstallTask<LocalStorageInstallSourceInfo>
    {
        private readonly LocalStorageInstallSourceInfo _localOverrideSources;

        public UseLocalOverrideDownloadTask(LocalStorageInstallSourceInfo localOverrideSources, TaskConfig config, ILogger logger) : base(config, logger)
        {
            _localOverrideSources = localOverrideSources ?? throw new ArgumentNullException(nameof(localOverrideSources));
        }

        public override Task<LocalStorageInstallSourceInfo> ExecuteTaskReturnResult(object contextArg)
        {
            _logger.LogInformation("Using local solution files instead of downloading latest stable release");
            return Task.FromResult(_localOverrideSources);
        }
    }
}
