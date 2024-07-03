using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using FluentFTP;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Installs solution contents to app-service via FTPS. Does not configure the web-app settings - this is done in SolutionInstaller.ConfigureWebApp
    /// </summary>
    public class InstallAppServiceContentsTask : InstallTaskInAzResourceGroup<LocalStorageInstallSourceInfo>
    {
        private readonly InstallerFtpConfig _ftpConfig;

        public InstallAppServiceContentsTask(InstallerFtpConfig ftpConfig, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
            _ftpConfig = ftpConfig;
        }

        public override async Task<LocalStorageInstallSourceInfo> ExecuteTaskReturnResult(object contextArg)
        {
            var localSources = EnsureContextArgType<LocalStorageInstallSourceInfo>(contextArg);

            var webApp = Container.GetWebSites().Get(_config.ResourceName);
            if (webApp.Value == null) throw new InstallException($"Can't find web-app with name '{_config.ResourceName}'");

            _logger.LogInformation("Configuring web-jobs in App Service ...");

            var ftp = webApp.Value.GetPublishingProfileXmlWithSecrets(new CsmPublishingProfile() { Format = PublishingProfileFormat.Ftp });
            using (var ms = new StreamReader(ftp.Value))
            {
                var profileData = publishData.FromXml(ms);
                var ftpDetails = profileData.GetPublishFtpsUrl();

                _logger.LogInformation("Found latest stable release packages:");
                _logger.LogInformation("- " + localSources.GetSolutionComponentLocation(SoftwareComponent.AITracker).FileLocation);
                _logger.LogInformation("- " + localSources.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity).FileLocation);
                _logger.LogInformation("- " + localSources.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights).FileLocation);
                _logger.LogInformation("- " + localSources.GetSolutionComponentLocation(SoftwareComponent.WebSite).FileLocation);

                const string PATH_WEBJOB = "/site/wwwroot/app_data/jobs/continuous/", PATH_WEBSITE = "/site/wwwroot/";

                // Upload the web-jobs & app-service contents
                _logger.LogInformation("Installing Office 365 import web-job...");
                await UnpackAndUpload(localSources.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity), PATH_WEBJOB, true, ftpDetails, _ftpConfig);
                _logger.LogInformation("Installing Application Insights import web-job...");
                await UnpackAndUpload(localSources.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights), PATH_WEBJOB, true, ftpDetails, _ftpConfig);
                _logger.LogInformation("Installing App-Service website contents...");
                await UnpackAndUpload(localSources.GetSolutionComponentLocation(SoftwareComponent.WebSite), PATH_WEBSITE, false, ftpDetails, _ftpConfig);
            }

            var url = $"https://{webApp.Value.Data.HostNames.First()}/";
            _logger.LogInformation($"App Service configured & running selected release. URL: {url}");

            return localSources;
        }

        internal async Task UnpackAndUpload(LocalStorageBlobInfo localStorageBlobInfo, string relativePath, bool createZipFolderName, FtpPublishInfo appServiceFtp, InstallerFtpConfig ftpConfig)
        {
            var zipContentsDir = ZipFileTasks.Unzip(localStorageBlobInfo, _logger);

            // Generate URL
            var relativeThisJobSubDir = relativePath;
            if (createZipFolderName)
            {
                relativeThisJobSubDir = relativePath + zipContentsDir.Name;
            }

            // https://github.com/robinrodricks/FluentFTP/wiki/Quick-Start-Example
            using (var client = FtpClientFactory.GetFtpClient(GetHostnameFromPublishingProfile(appServiceFtp.RootUrl), appServiceFtp.Username, appServiceFtp.Password, ftpConfig))
            {
                try
                {
                    await client.Connect();
                }
                catch (SocketException)
                {
                    _logger.LogError($"Couldn't connect to {appServiceFtp.RootUrl} - check installer proxy settings (proxy enabled = {ftpConfig.UseFtpProxy})");
                    throw;
                }
                catch (IOException)
                {
                    _logger.LogError($"Couldn't connect to {appServiceFtp.RootUrl} - check installer proxy settings (proxy enabled = {ftpConfig.UseFtpProxy}) and ensure app-service has 'FTP state' configured to 'FTPS only'");
                    throw;
                }

                await Upload(client, zipContentsDir, relativeThisJobSubDir);

                await client.Disconnect();
            }
        }
        string GetHostnameFromPublishingProfile(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                throw new ArgumentOutOfRangeException(nameof(s));
            }

            var uri = new Uri(s);
            return uri.Host;

            throw new ArgumentOutOfRangeException(nameof(s));
        }

        async Task Upload(AsyncFtpClient client, DirectoryInfo zipContentsDir, string relativeThisJobSubDir)
        {
            await client.UploadFiles(zipContentsDir.GetFiles().Select(d => d.FullName), relativeThisJobSubDir, FtpRemoteExists.Overwrite, true, FtpVerify.Retry, FtpError.Throw);

            // Process sub-folders too
            foreach (var subDir in zipContentsDir.GetDirectories())
            {
                var absoluteThisJobFtpDir = $"{relativeThisJobSubDir}{subDir.Name}/";

                await Upload(client, subDir, absoluteThisJobFtpDir);
            }
        }
    }
}
