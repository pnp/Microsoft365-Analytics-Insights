using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.InstallerTasks.Adoptify;
using App.ControlPanel.Engine.Models;
using CloudInstallEngine.Models;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Top-level installer class. Executes a full solution install based on a SolutionInstallConfig values
    /// </summary>
    public class SolutionInstaller : BaseInstallProcessWithFtp
    {
        public SolutionInstaller(SolutionInstallConfig config, ILogger logger, SoftwareReleaseConfig softwareConfig, InstallerFtpConfig ftpConfig,
            string installingUsername, string configPassword) : base(config, logger, ftpConfig)
        {
            _softwareConfig = softwareConfig;
            this.InstalledByUsername = installingUsername;
            _configPassword = configPassword;
        }

        /// <summary>
        /// Installed by who?
        /// </summary>
        public string InstalledByUsername { get; set; }

        private readonly SoftwareReleaseConfig _softwareConfig;
        private readonly string _configPassword;

        /// <summary>
        /// Main execution entrypoint
        /// </summary>
        public async Task InstallOrUpdate()
        {
            _logger.LogInformation($"Starting install. Authenticating & selecting subscription '{this.Config.Subscription.DisplayName}'...");

            // Setup the things. Catch as specific exceptions as possible; Azure & our own exceptions
            var azureSub = BaseAnalyticsSolutionInstallJob.FromConfig(this.Config);
            try
            {
                // Get/create AppService + SQL + Redis. Binaries installed post-create.
                var azureBackeEndCreationJob = new AzurePaaSInstallJob(_logger, Config, azureSub);
                await azureBackeEndCreationJob.Install();

                // Run stuff now everything in Azure is created
                var tasks = new ConfigureAzureComponentsTasks(Config, _logger, _ftpConfig, InstalledByUsername, _softwareConfig, _configPassword);
                await tasks.RunPostCreatePaaSTasks(
                    azureBackeEndCreationJob.CreatedWebSiteResource,
                    azureBackeEndCreationJob.DatabasePaaSInfo,
                    azureBackeEndCreationJob.Storage,
                    azureBackeEndCreationJob.CreatedAutomationAccount,
                    azureBackeEndCreationJob.AppInsights,
                    azureBackeEndCreationJob.Redis,
                    azureBackeEndCreationJob.CognitiveServicesInfo,
                    azureBackeEndCreationJob.KeyVault,
                    azureBackeEndCreationJob.SBQueueWithConnectionString.ConnectionString, azureBackeEndCreationJob.Subscription
                );

                // Warm-up app-service
                var adminSiteUrl = $"https://{azureBackeEndCreationJob.CreatedWebSiteResource.Data.HostNames.First()}/";
                await WarmupAppServiceSite(adminSiteUrl);

                _logger.LogInformation($"Reminder: Ensure Azure AD app registration for the runtime account has correct authentication configuration (see 'Configure Reply URLs' of deployment guide).");

                // Install Adoptify components
                if (Config.SolutionConfig.SolutionTargeted == SolutionImportType.Adoptify)
                {
                    await InstallAdoptifyComponents(azureSub);
                }

                // Open admin site?
                if (Config.TasksConfig.OpenAdminSitePostInstall)
                {
                    System.Diagnostics.Process.Start(adminSiteUrl);
                }

                // Warn if no sites configured for import (and needed)
                var needSiteFilter = Config.SolutionConfig.ImportTaskSettings.WebTraffic || Config.SolutionConfig.ImportTaskSettings.ActivityLog;
                if (needSiteFilter && Config.SharePointConfig.TargetSites.Count == 0)
                {
                    _logger.LogInformation($"IMPORTANT! There are no configured SharePoint urls specified. Please add manually at least one URL to allow site data import. " +
                        $"See 'Configure Filtered URLs' in deployment guide for more info.");
                }
            }
            catch (InstallException ex)       // General API error
            {
                _logger.LogError(ex.Message);
                return;
            }
            catch (Exception ex)            // Anything else
            {
                // Anything else. Log error as fatal
                _logger.LogError($"FATAL: Unexpected error of type '{ex.GetType().Name}': " + ex.Message);
                Console.WriteLine(ex);
                InstallerLogs.AddToWindowsEventLog($"FATAL: Unexpected error of type '{ex.GetType().Name}': " + ex.Message, true);
                InstallerLogs.AddToWindowsEventLog(ex.ToString(), true);
                return;
            }

            _logger.LogInformation("All tasks completed.");
        }

        private async Task InstallAdoptifyComponents(Azure.ResourceManager.Resources.SubscriptionResource azureSub)
        {
            _logger.LogInformation($"Launching web login pop-up for existing Adoptify site '{Config.SolutionConfig.Adoptify.ExistingSiteUrl}'...");
            var authManager = new OfficeDevPnP.Core.AuthenticationManager();
            using (var ctx = authManager.GetWebLoginClientContext(Config.SolutionConfig.Adoptify.ExistingSiteUrl))
            {
                var adoptifyInstallJob = new AdoptifyInstallJob(_logger, Config, azureSub, ctx);

                // Install SPSite content and Azure components
                await adoptifyInstallJob.Install();

                _logger.LogInformation("Adoptify back-end setup complete. Remember to authorize the API connections in the portal.");
            }
        }

        private async Task WarmupAppServiceSite(string adminSiteUrl)
        {
            _logger.LogInformation($"Warming-up web-application '{adminSiteUrl}'...");
            await Task.Delay(5000);     // 5 seconds

            var httpClient = new HttpClient();
            httpClient.Timeout = new TimeSpan(0, 5, 0);     // 5 mins
            var response = await httpClient.GetAsync(adminSiteUrl);
            if (response.StatusCode == System.Net.HttpStatusCode.OK || response.StatusCode == System.Net.HttpStatusCode.Moved)
            {
                _logger.LogInformation($"Got expected response from web-app {response.StatusCode}");
            }
            else
            {
                _logger.LogError($"Got unexpected response from web-app {response.StatusCode} - check manually the app service is started", true);
            }
        }
    }
}
