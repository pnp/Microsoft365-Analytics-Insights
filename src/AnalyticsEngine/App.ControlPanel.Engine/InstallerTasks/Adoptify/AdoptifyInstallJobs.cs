using App.ControlPanel.Engine.InstallerTasks.Adoptify.Models;
using Azure.ResourceManager.Resources;
using CloudInstallEngine;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify
{
    /// <summary>
    /// Main job for installing Adoptify back-end
    /// </summary>
    public class AdoptifyInstallJob : BaseAnalyticsSolutionInstallJob
    {
        public AdoptifyInstallJob(ILogger logger, SolutionInstallConfig config, SubscriptionResource subscription, ClientContext clientContext) : base(logger, config, subscription)
        {
            // Create lists & then ARM resources that depend on them
            var sitesSetupTask = new AdoptifySiteProvisionTask(TaskConfig.GetConfigForPropAndVal("site", config.SolutionConfig.Adoptify.ExistingSiteUrl), logger, clientContext);
            var siteSchemaUpdateTask = new AdoptifySiteFieldUpdatesTask(TaskConfig.GetConfigForPropAndVal("site", config.SolutionConfig.Adoptify.ExistingSiteUrl), logger, clientContext);

            var adoptifyInstallerTasks = new List<BaseInstallTask>();
            if (config.SolutionConfig.Adoptify.ProvisionSchema)
            {
                adoptifyInstallerTasks.AddRange(new BaseInstallTask[] { sitesSetupTask, siteSchemaUpdateTask });
            }
            else
            {
                logger.LogInformation($"Skipping deploy of Adoptify SPO site");
            }

            var azSetupTask = new AdoptifyArmResourcesInstallTask(config, Logger, _subscription);
            adoptifyInstallerTasks.Add(new AdoptifyLoadSiteSchemaTask(logger, clientContext));      // Task to just load site schema
            adoptifyInstallerTasks.Add(azSetupTask);      // Wire up Azure resources

            if (config.SolutionConfig.Adoptify.CreateDefaultData)
            {
                var addAssetsTask = new AssetsInstallTask(TaskConfig.NoConfig, logger, clientContext);
                var addContentTask = new ListItemsInstallTask(TaskConfig.GetConfigForPropAndVal("lang", config.SolutionConfig.SolutionLanguageCode), logger, clientContext);

                adoptifyInstallerTasks.AddRange(new BaseInstallTask[] { addAssetsTask, addContentTask });
            }
            else
            {
                logger.LogInformation($"Skipping install of Adoptify default data");
            }
            AddTasks(adoptifyInstallerTasks);
        }
    }

    /// <summary>
    /// Runs AdoptifyArmResourcesInstallJob
    /// </summary>
    public class AdoptifyArmResourcesInstallTask : BaseInstallTask
    {
        private readonly SolutionInstallConfig _solutionConfig;
        private readonly SubscriptionResource _subscription;

        public AdoptifyArmResourcesInstallTask(SolutionInstallConfig config, ILogger logger, SubscriptionResource subscription) : base(TaskConfig.NoConfig, logger)        // AdoptifyArmResourcesInstallJob uses contextArg confg
        {
            _solutionConfig = config;
            _subscription = subscription;
        }

        public override async Task<object> ExecuteTask(object contextArg)
        {
            base.EnsureContextArgType<AdoptifySiteListInfo>(contextArg);

            var siteInfo = (AdoptifySiteListInfo)contextArg;
            _logger.LogInformation($"Installing Adoptify Azure resources");

            var adoptifyConfig = siteInfo.ToConfig();

            var adoptifyArmResourcesInstallJob = new AdoptifyArmResourcesInstallJob(_logger, _solutionConfig, adoptifyConfig, _subscription);
            await adoptifyArmResourcesInstallJob.Install();
            return siteInfo;
        }
    }

    /// <summary>
    /// Install Adoptify ARM/Azure components
    /// </summary>
    public class AdoptifyArmResourcesInstallJob : BaseAnalyticsSolutionInstallJob
    {
        public AdoptifyArmResourcesInstallJob(ILogger logger, SolutionInstallConfig config, TaskConfig adoptifyConfig, SubscriptionResource subscription) : base(logger, config, subscription)
        {
            // Concat config with lists
            adoptifyConfig
                .AddSetting("sqlservername", $"{config.SQLServerName}.database.windows.net")
                .AddSetting("sqldbname", config.SQLServerDatabaseName)
                .AddSetting("sqlusername", config.SQLServerAdminUsername)
                .AddSetting("sqlpassword", config.SQLServerAdminPassword)
                .AddSetting("resourceGroupName", config.ResourceGroupName)
                .AddSetting("subscriptionId", config.Subscription.SubId)
                .AddSetting("location", config.AzureLocation.Name)
                .AddSetting("insightsAppId", config.RuntimeAccountOffice365.ClientId)
                .AddSetting("tenantId", config.RuntimeAccountOffice365.DirectoryId)
                .AddSetting("appId", config.RuntimeAccountOffice365.ClientId)
                .AddSetting("appSecret", config.RuntimeAccountOffice365.Secret)
                .AddSetting("keyvaultName", config.KeyVaultName);

            var tagsDic = config.Tags.ToDictionary();

            var apiConnections = new ApiConnectionsAppInstallTask(adoptifyConfig, logger, Location, tagsDic);

            var processbadges = new ProcessReactionsInstallTask("Adoptify-ProcessReactions", adoptifyConfig, logger, Location, tagsDic);
            var processcallmodality = new ProcessCallModalityTask("Adoptify-ProcessCallModality", adoptifyConfig, logger, Location, tagsDic);
            var processchats = new ProcessChatsInstallTask("Adoptify-ProcessChats", adoptifyConfig, logger, Location, tagsDic);
            var processcounts = new ProcessCountsInstallTask("Adoptify-ProcessCounts", adoptifyConfig, logger, Location, tagsDic);
            var processdeviceusage = new ProcessDeviceUsageInstallTask("Adoptify-ProcessDeviceUsage", adoptifyConfig, logger, Location, tagsDic);
            var syncTeamsApps = new SyncTeamsAppsInstallTask("Adoptify-SyncTeamsApps", adoptifyConfig, logger, Location, tagsDic);
            var processmeetings = new ProcessMeetingsInstallTask("Adoptify-ProcessMeetings", adoptifyConfig, logger, Location, tagsDic);
            var processredeemedrewards = new ProcessRedeemedRewardsInstallTask("Adoptify-ProcessRedeemedRewards", adoptifyConfig, logger, Location, tagsDic);
            var processusagereminders = new ProcessUsageRemindersInstallTask("Adoptify-ProcessUsageReminders", adoptifyConfig, logger, Location, tagsDic);
            var questnotification = new ProcessQuestNotificationsInstallTask("Adoptify-QuestNotification", adoptifyConfig, logger, Location, tagsDic);
            var processTeamsAppsInstallTask = new ProcessTeamsAppsInstallTask("Adoptify-ProcessApps", adoptifyConfig, logger, Location, tagsDic);

            this.AddTask(apiConnections);
            this.AddTask(processbadges);
            this.AddTask(processcallmodality);
            this.AddTask(processchats);
            this.AddTask(processcounts);
            this.AddTask(processdeviceusage);
            this.AddTask(syncTeamsApps);
            this.AddTask(processTeamsAppsInstallTask);
            this.AddTask(processmeetings);
            this.AddTask(processredeemedrewards);
            this.AddTask(processusagereminders);
            this.AddTask(questnotification);
        }
    }
}
