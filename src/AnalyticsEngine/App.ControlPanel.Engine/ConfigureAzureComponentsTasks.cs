using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Models;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Things to do once Azure components are created
    /// </summary>
    public class ConfigureAzureComponentsTasks : BaseInstallProcessWithFtp
    {
        private readonly string _installedByUsername;
        private readonly SoftwareReleaseConfig _softwareConfig;
        private readonly string _configPassword;

        public ConfigureAzureComponentsTasks(SolutionInstallConfig config, ILogger logger, InstallerFtpConfig ftpConfig, string installedByUsername,
            SoftwareReleaseConfig softwareConfig, string configPassword) : base(config, logger, ftpConfig)
        {
            _installedByUsername = installedByUsername;
            _softwareConfig = softwareConfig;
            _configPassword = configPassword;
        }

        /// <summary>
        /// Install configure & software on App Service, update target DB. 
        /// </summary>
        public async Task RunPostCreatePaaSTasks(WebSiteResource webApp, DatabasePaaSInfo dbInfo, StorageAccountResource storage, AutomationAccountResource automationAccount,
            AppInsightsInfo appInsights,
            RedisInstallResult redis, CognitiveServicesInfo cognitiveServicesInfo,
            KeyVaultResource keyVault, string serviceBusConnectionString, SubscriptionResource subscription)
        {
            // Configure app-service connection-strings, etc
            await ConfigureWebApp(webApp, dbInfo, storage, redis, cognitiveServicesInfo, appInsights, serviceBusConnectionString, keyVault);

            // Stop app for later?
            if (this.Config.TasksConfig.InstallLatestSolutionContent)
            {
                _logger.LogInformation("Stopping app-service during binaries deployment...");
                await webApp.StopAsync();
            }

            // Get solution sources either from Azure storage or local sources
            var solutionSources = await GetSolutionFromSource(subscription, automationAccount);

            // Find downloaded installer app
            var installerExeFile = GetInstallerExe(solutionSources.GetSolutionComponentLocation(SoftwareComponent.ControlPanel));

            var sqlInstallerTasks = new SqlInstallerTasks(Config, installerExeFile, dbInfo, _logger, _installedByUsername, _configPassword, async (connectionString) => await VerifySQL(connectionString));
            await sqlInstallerTasks.UpdateSqlDatabaseSchemaAndDataFromDownloadedInstaller(installerExeFile, _installLogEvents);

            if (this.Config.TasksConfig.InstallLatestSolutionContent)
            {
                await webApp.StartAsync();
                _logger.LogInformation("App Service started again after release copied");
            }

            if (this.Config.SolutionConfig.ImportTaskSettings.WebTraffic)
            {
                if (this.Config.TasksConfig.InstallLatestSolutionContent)
                {
                    // Install AITracker from downloaded source
                    var aiTrackerDownload = solutionSources.GetSolutionComponentLocation(SoftwareComponent.AITracker);

                    var spTasks = new SharePointWebComponentsInstallJob(Config, _logger, webApp.Data.DefaultHostName);
                    await spTasks.InstallAITracker(this.Config.SharePointConfig, aiTrackerDownload, appInsights.ConnectionString);
                }
                else
                {
                    _logger.LogInformation("Skipping SharePoint web components (AITracker / SPFx) install because 'Update solution with latest release' is not selected.");
                }
            }
        }

        FileInfo GetInstallerExe(LocalStorageBlobInfo localStorageBlobInfo)
        {
            // Get control-panel
            FileInfo installerExeFile = null;
            DirectoryInfo zipContentsDirControlPanel = null;
            try
            {
                zipContentsDirControlPanel = ZipFileTasks.Unzip(localStorageBlobInfo, _logger);
            }
            catch (Exception ex)
            {
                // Give context to the error
                throw new ApplicationException($"Could not extract control-panel app: '{ex.Message}'");
            }


            // Try and find new EXE name 1st
            foreach (var item in zipContentsDirControlPanel.GetFiles(InstallerConstants.FILENAME_EXE_INSTALLER))
            {
                if (item.Name.ToLower() == InstallerConstants.FILENAME_EXE_INSTALLER.ToLower()) installerExeFile = item;
            }

            if (installerExeFile == null)
            {
                throw new ApplicationException($"Could not find installer EXE in control-panel app");
            }
            return installerExeFile;
        }

        async Task<LocalStorageInstallSourceInfo> GetSolutionFromSource(SubscriptionResource subscription, AutomationAccountResource automationAccount)
        {
            AppServiceContentInstallJob appServiceContentInstallJob = null;
            if (this.Config.DownloadLatestStable)
            {
                // Download webjobs from blob storage. Optionally install.
                appServiceContentInstallJob = new DownloadLatestAppServiceContentInstallJob(_logger, subscription, _softwareConfig, _ftpConfig, this.Config, !this.Config.TasksConfig.InstallLatestSolutionContent, automationAccount);
            }
            else
            {
                // Use local sources. Optionally install.
                appServiceContentInstallJob = new UseLocalAppServiceContentInstallJob(_logger, subscription, this.Config.LocalSourceOverride, _ftpConfig, this.Config, !this.Config.TasksConfig.InstallLatestSolutionContent, automationAccount);
            }

            // Install or just download, depending on config above
            await appServiceContentInstallJob.Install();

            return appServiceContentInstallJob.LocalStorageInstallSourceInfo;
        }

        async Task ConfigureWebApp(WebSiteResource webApp, DatabasePaaSInfo backendInfo,
            StorageAccountResource storage,
            RedisInstallResult redis,
            CognitiveServicesInfo cognitiveServicesInfo,
            AppInsightsInfo appInsights, string serviceBusConnectionString, KeyVaultResource keyVault)
        {
            // App settings
            var url = $"https://{webApp.Data.HostNames.First()}/";

            var appSettings = new AppServiceConfigurationDictionary();
            appSettings.Properties.Add("WebAppURL", url);
            appSettings.Properties.Add("ClientID", this.Config.RuntimeAccountOffice365.ClientId);
            appSettings.Properties.Add("ClientSecret", this.Config.RuntimeAccountOffice365.Secret);
            appSettings.Properties.Add("TenantGUID", this.Config.RuntimeAccountOffice365.DirectoryId);
            appSettings.Properties.Add("KeyVaultURL", keyVault.Data.Properties.VaultUri.ToString());
            appSettings.Properties.Add("WEBSITE_LOAD_USER_PROFILE", "1");       // So certificate loading works - https://learn.microsoft.com/en-us/azure/app-service/reference-app-settings?tabs=kudu%2Cdotnet#build-automation

            // App Insights REST calls have sometimes failed. If they did & we have no config, just don't update this bit of the config & they'll have to do it manually
            if (!string.IsNullOrEmpty(appInsights?.ConnectionString))
            {
                appSettings.Properties.Add("AppInsightsConnectionString", appInsights.ConnectionString);
            }

            if (this.Config.CognitiveServicesEnabled)
            {
                appSettings.Properties.Add("CognitiveEndpoint", cognitiveServicesInfo.Endpoint);
                appSettings.Properties.Add("CognitiveKey", cognitiveServicesInfo.Key);
            }
            else
            {
                appSettings.Properties.Add("CognitiveEndpoint", string.Empty);
                appSettings.Properties.Add("CognitiveKey", string.Empty);
            }

            appSettings.Properties.Add("ImportJobSettings", this.Config.SolutionConfig.ImportTaskSettings.ToSettingsString());

            // When private VNet is enabled, ensure the app service routes all outbound traffic through
            // the VNet so that private DNS zones resolve Azure PaaS hostnames to private endpoint IPs.
            if (this.Config.NetworkConfig?.Enabled == true)
            {
                appSettings.Properties.Add("WEBSITE_VNET_ROUTE_ALL", "1");
                appSettings.Properties.Add("WEBSITE_DNS_SERVER", "168.63.129.16"); // Azure DNS for private DNS zone resolution
                _logger.LogInformation("Private VNet enabled: app service will route all traffic through VNet for private endpoint DNS resolution.");
            }

            // Connection strings
            // Build the Redis connection string from the install-task result, which abstracts over
            // both Azure Managed Redis (port 10000) and pre-existing legacy classic Azure Cache for
            // Redis (port 6380) that the installer chose to reuse.
            //
            // When the cache is RBAC-only (no access keys), we deliberately omit the password
            // segment so that CacheConnectionManager skips its key-based attempt and authenticates
            // via Entra ID using the runtime service principal credentials.
            string redisConnectionString;
            if (redis.UseRbacAuth)
            {
                redisConnectionString = $"{redis.HostName}:{redis.Port},ssl=True,abortConnect=False";
                _logger.LogInformation("Redis connection string built for RBAC/Entra ID auth (no access key).");
            }
            else
            {
                redisConnectionString = $"{redis.HostName}:{redis.Port},password={redis.PrimaryKey},ssl=True,abortConnect=False";
                _logger.LogInformation("Redis connection string built for key-based auth.");
            }

            var storageInfo = new AzStorageConnectionInfo(storage);
            var connectionStrings = new ConnectionStringDictionary();
            connectionStrings.Properties.Add("SPOInsightsEntities", new ConnStringValueTypePair(backendInfo.ConnectionString, ConnectionStringType.SqlAzure));
            connectionStrings.Properties.Add("AzureWebJobsDashboard", new ConnStringValueTypePair(storageInfo.StorageConnectionString, ConnectionStringType.Custom));
            connectionStrings.Properties.Add("AzureWebJobsStorage", new ConnStringValueTypePair(storageInfo.StorageConnectionString, ConnectionStringType.Custom));
            connectionStrings.Properties.Add("Storage", new ConnStringValueTypePair(storageInfo.StorageConnectionString, ConnectionStringType.Custom));
            if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
            {
                connectionStrings.Properties.Add("ServiceBus", new ConnStringValueTypePair(serviceBusConnectionString, ConnectionStringType.Custom));
            }
            else
            {
                _logger.LogInformation("Service Bus is disabled; skipping 'ServiceBus' connection-string on the App Service.");
            }
            connectionStrings.Properties.Add("Redis", new ConnStringValueTypePair(redisConnectionString, ConnectionStringType.Custom));

            await webApp.UpdateAsync(new SitePatchInfo { SiteConfig = new SiteConfigProperties { Use32BitWorkerProcess = false, IsAlwaysOn = true } });
            await webApp.UpdateApplicationSettingsAsync(appSettings);
            await webApp.UpdateConnectionStringsAsync(connectionStrings);

            _logger.LogInformation("App Service connection-strings & app-settings configured");
        }
    }
}
