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

        /// <summary>How long to keep polling Kudu for the deployment outcome after the upload is accepted.</summary>
        internal static readonly TimeSpan DeploymentCompletionTimeout = TimeSpan.FromMinutes(30);

        /// <summary>Gap between deployment-status polls.</summary>
        internal static readonly TimeSpan DeploymentPollInterval = TimeSpan.FromSeconds(5);

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
            var handler = CreateHttpClientHandler(proxyConfig);
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

                    // The upload is only accepted at this point, not finished. Azure's App Service front end
                    // aborts ANY single request at ~230 seconds, so a synchronous publish of a package this
                    // size fails with an HTML "500 - The request timed out" even when the deployment itself
                    // is progressing fine server-side. We therefore publish with async=true and poll Kudu for
                    // the real outcome.
                    var statusUri = ResolveDeploymentStatusUri(response.Headers.Location, publishInfo.RootUrl);
                    await WaitForDeploymentAsync(client, statusUri);
                }
            }
        }

        /// <summary>
        /// Where to poll for the outcome of an async publish. Kudu returns the deployment's own status URL in
        /// the Location header; fall back to the "latest deployment" endpoint when it is absent or relative.
        /// </summary>
        internal static Uri ResolveDeploymentStatusUri(Uri locationHeader, string publishUrl)
        {
            if (locationHeader != null && locationHeader.IsAbsoluteUri)
            {
                return locationHeader;
            }

            var absoluteUrl = publishUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? publishUrl
                : "https://" + publishUrl;
            return new Uri(new Uri(absoluteUrl).GetLeftPart(UriPartial.Authority) + "/api/deployments/latest");
        }

        /// <summary>
        /// Polls Kudu until the deployment completes, fails, or we give up. Transient read failures are
        /// tolerated (the SCM site can briefly drop requests while the site restarts mid-deploy) - only the
        /// overall deadline is fatal.
        /// </summary>
        private async Task WaitForDeploymentAsync(HttpClient client, Uri statusUri)
        {
            var deadline = DateTime.UtcNow.Add(DeploymentCompletionTimeout);
            string lastProgressLogged = null;
            string lastTransientError = null;

            while (true)
            {
                KuduDeploymentStatus status = null;
                try
                {
                    var statusResponse = await client.GetAsync(statusUri);
                    using (statusResponse)
                    {
                        var body = await statusResponse.Content.ReadAsStringAsync();
                        if (statusResponse.IsSuccessStatusCode)
                        {
                            status = ParseDeploymentStatus(body);
                        }
                        else
                        {
                            lastTransientError = $"HTTP {(int)statusResponse.StatusCode} ({statusResponse.ReasonPhrase})";
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    lastTransientError = CloudInstallEngine.ExceptionMessages.Format(ex);
                }

                if (status != null)
                {
                    if (status.IsFailed)
                    {
                        var detail = string.IsNullOrWhiteSpace(status.StatusText) ? status.Message : status.StatusText;
                        throw new InstallException(
                            $"App Service deployment failed on the server after the package uploaded successfully. " +
                            $"Kudu reported status {status.Status} ({detail}). " +
                            $"Deployment log: {status.LogUrl ?? statusUri.ToString()}");
                    }

                    if (status.IsSuccess)
                    {
                        _logger.LogInformation("App Service deployment completed successfully.");
                        return;
                    }

                    var progress = status.DescribeProgress();
                    if (progress != lastProgressLogged)
                    {
                        _logger.LogInformation($"App Service deployment in progress: {progress}");
                        lastProgressLogged = progress;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    var trailing = status == null && lastTransientError != null
                        ? $" Last error reading deployment status: {lastTransientError}."
                        : string.Empty;
                    throw new InstallException(
                        $"App Service deployment did not report completion within {DeploymentCompletionTimeout.TotalMinutes:N0} minutes. " +
                        $"The package uploaded successfully, so the deployment may still finish on its own - check {statusUri}.{trailing}");
                }

                await Task.Delay(DeploymentPollInterval);
            }
        }

        internal static KuduDeploymentStatus ParseDeploymentStatus(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return Newtonsoft.Json.JsonConvert.DeserializeObject<KuduDeploymentStatus>(json);
        }

        /// <summary>Kudu's deployment status document, as returned by /api/deployments/latest.</summary>
        internal class KuduDeploymentStatus
        {
            internal const int StatusFailed = 3;

            [Newtonsoft.Json.JsonProperty("id")]
            public string Id { get; set; }

            [Newtonsoft.Json.JsonProperty("status")]
            public int? Status { get; set; }

            [Newtonsoft.Json.JsonProperty("status_text")]
            public string StatusText { get; set; }

            [Newtonsoft.Json.JsonProperty("progress")]
            public string Progress { get; set; }

            [Newtonsoft.Json.JsonProperty("complete")]
            public bool Complete { get; set; }

            [Newtonsoft.Json.JsonProperty("log_url")]
            public string LogUrl { get; set; }

            [Newtonsoft.Json.JsonProperty("message")]
            public string Message { get; set; }

            internal bool IsFailed => Complete && Status == StatusFailed;

            /// <summary>Complete and not explicitly failed. Kudu uses 4 for success.</summary>
            internal bool IsSuccess => Complete && Status != StatusFailed;

            internal string DescribeProgress()
            {
                if (!string.IsNullOrWhiteSpace(Progress)) return Progress;
                if (!string.IsNullOrWhiteSpace(StatusText)) return StatusText;
                return $"status {Status?.ToString() ?? "unknown"}";
            }
        }

        internal static HttpClientHandler CreateHttpClientHandler(InstallerProxyConfig proxyConfig)
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
            return handler;
        }

        internal static Uri BuildKuduPublishUri(string publishUrl)
        {
            if (string.IsNullOrWhiteSpace(publishUrl))
                throw new ArgumentOutOfRangeException(nameof(publishUrl));

            var absoluteUrl = publishUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? publishUrl
                : "https://" + publishUrl;
            var profileUri = new Uri(absoluteUrl);
            // async=true is required: Azure's front end kills any single request at ~230s, so a large
            // package cannot be published synchronously. Kudu accepts the upload and reports progress
            // separately - see WaitForDeploymentAsync.
            return new Uri(profileUri.GetLeftPart(UriPartial.Authority) + "/api/publish?type=zip&async=true");
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
