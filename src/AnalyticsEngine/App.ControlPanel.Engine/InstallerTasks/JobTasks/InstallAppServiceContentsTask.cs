using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.Core;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Installs solution contents to App Service via Kudu ZIP deployment. Does not configure the web-app settings - this is done in SolutionInstaller.ConfigureWebApp.
    /// </summary>
    public class InstallAppServiceContentsTask : InstallTaskInAzResourceGroup<LocalStorageInstallSourceInfo>
    {
        private readonly InstallerProxyConfig _proxyConfig;
        private readonly VNetConfig _networkConfig;

        public InstallAppServiceContentsTask(InstallerProxyConfig proxyConfig, TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, VNetConfig networkConfig)
            : base(config, logger, azureLocation, tags)
        {
            _proxyConfig = proxyConfig ?? throw new ArgumentNullException(nameof(proxyConfig));
            _networkConfig = networkConfig;
        }

        public override async Task<LocalStorageInstallSourceInfo> ExecuteTaskReturnResult(object contextArg)
        {
            var localSources = EnsureContextArgType<LocalStorageInstallSourceInfo>(contextArg);

            var webApp = Container.GetWebSites().Get(_config.ResourceName);
            if (webApp.Value == null) throw new InstallException($"Can't find web-app with name '{_config.ResourceName}'");

            _logger.LogInformation("Configuring web-jobs in App Service ...");

            var publishingProfile = webApp.Value.GetPublishingProfileXmlWithSecrets(
                new CsmPublishingProfile { Format = PublishingProfileFormat.WebDeploy });
            using (var ms = new StreamReader(publishingProfile.Value))
            {
                var profileData = publishData.FromXml(ms);
                var kuduDetails = profileData.GetKuduPublishInfo();

                _logger.LogInformation("Found latest stable release packages:");
                _logger.LogInformation("- " + localSources.GetSolutionComponentLocation(SoftwareComponent.AITracker).FileLocation);
                _logger.LogInformation("- " + localSources.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity).FileLocation);
                _logger.LogInformation("- " + localSources.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights).FileLocation);
                _logger.LogInformation("- " + localSources.GetSolutionComponentLocation(SoftwareComponent.WebSite).FileLocation);

                _logger.LogInformation("Building App Service deployment package...");
                var deploymentPackage = BuildDeploymentPackage(localSources, _logger);
                _logger.LogInformation("Deploying web-jobs and website to App Service over HTTPS...");
                await PublishZipAsync(kuduDetails, deploymentPackage, _proxyConfig);
            }

            var url = $"https://{webApp.Value.Data.HostNames.First()}/";
            _logger.LogInformation($"App Service configured & running selected release. URL: {url}");

            return localSources;
        }

        internal static FileInfo BuildDeploymentPackage(LocalStorageInstallSourceInfo localSources, ILogger logger)
        {
            var deploymentRootPath = Path.Combine(DataUtils.StringUtils.TempDirPath, "AppServiceDeployment");
            ResetDirectory(deploymentRootPath);

            var websiteContents = ZipFileTasks.Unzip(
                localSources.GetSolutionComponentLocation(SoftwareComponent.WebSite), logger);
            CopyDirectory(websiteContents.FullName, deploymentRootPath);

            AddWebJobToPackage(localSources, SoftwareComponent.WebJobActivity, deploymentRootPath, logger);
            AddWebJobToPackage(localSources, SoftwareComponent.WebJobAppInsights, deploymentRootPath, logger);

            var packagePath = Path.Combine(DataUtils.StringUtils.TempDirPath, "AppServiceDeployment.zip");
            if (File.Exists(packagePath))
                File.Delete(packagePath);

            ZipFile.CreateFromDirectory(deploymentRootPath, packagePath, CompressionLevel.Optimal, false);
            return new FileInfo(packagePath);
        }

        private static void AddWebJobToPackage(
            LocalStorageInstallSourceInfo localSources,
            SoftwareComponent component,
            string deploymentRootPath,
            ILogger logger)
        {
            var webJobContents = ZipFileTasks.Unzip(localSources.GetSolutionComponentLocation(component), logger);
            var webJobPath = Path.Combine(
                deploymentRootPath,
                "app_data",
                "jobs",
                "continuous",
                webJobContents.Name);
            CopyDirectory(webJobContents.FullName, webJobPath);
        }

        private async Task PublishZipAsync(KuduPublishInfo publishInfo, FileInfo deploymentPackage, InstallerProxyConfig proxyConfig)
        {
            var handler = new HttpClientHandler();
            if (proxyConfig.UseProxy)
            {
                var proxy = new WebProxy(proxyConfig.Host, proxyConfig.Port);
                proxy.Credentials = proxyConfig.IntegratedAuth
                    ? CredentialCache.DefaultCredentials
                    : new NetworkCredential(proxyConfig.Username, proxyConfig.Password);
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }

            using (handler)
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) })
            using (var packageStream = deploymentPackage.OpenRead())
            using (var content = new StreamContent(packageStream))
            {
                var credentials = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{publishInfo.Username}:{publishInfo.Password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

                var publishUri = BuildKuduPublishUri(publishInfo.RootUrl);
                HttpResponseMessage response;
                try
                {
                    response = await client.PostAsync(publishUri, content);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError($"App Service HTTPS deployment could not reach '{publishUri.GetLeftPart(UriPartial.Authority)}'. " +
                        $"Check installer proxy and SCM access settings. Details: {CloudInstallEngine.ExceptionMessages.Format(ex)}");
                    LogPrivateNetworkGuidanceIfApplicable();
                    throw;
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogError($"App Service HTTPS deployment to '{publishUri.GetLeftPart(UriPartial.Authority)}' timed out after 30 minutes. " +
                        $"Check installer proxy and SCM access settings. Details: {CloudInstallEngine.ExceptionMessages.Format(ex)}");
                    LogPrivateNetworkGuidanceIfApplicable();
                    throw;
                }

                using (response)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = string.IsNullOrWhiteSpace(responseBody)
                            ? response.ReasonPhrase
                            : responseBody.Substring(0, Math.Min(responseBody.Length, 2000));
                        throw new InstallException(
                            $"App Service HTTPS deployment failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}");
                    }
                }
            }
        }

        internal static Uri BuildKuduPublishUri(string publishUrl)
        {
            if (string.IsNullOrWhiteSpace(publishUrl))
                throw new ArgumentOutOfRangeException(nameof(publishUrl));

            var absoluteUrl = publishUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? publishUrl
                : "https://" + publishUrl;
            var profileUri = new Uri(absoluteUrl);
            return new Uri(profileUri.GetLeftPart(UriPartial.Authority) + "/api/publish?type=zip");
        }

        private static void ResetDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            Directory.CreateDirectory(path);
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (var filePath in Directory.GetFiles(sourcePath))
            {
                File.Copy(filePath, Path.Combine(destinationPath, Path.GetFileName(filePath)), true);
            }
            foreach (var directoryPath in Directory.GetDirectories(sourcePath))
            {
                CopyDirectory(directoryPath, Path.Combine(destinationPath, Path.GetFileName(directoryPath)));
            }
        }

        void LogPrivateNetworkGuidanceIfApplicable()
        {
            if (PrivateNetworkGuidance.IsPrivateNetworkOnly(_networkConfig))
            {
                _logger.LogError(PrivateNetworkGuidance.BuildVmOnVNetGuidance("the App Service SCM HTTPS release upload", _networkConfig?.VNetName));
            }
        }
    }
}
