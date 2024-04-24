using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks.Tasks;
using Azure.Identity;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Redis;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Storage;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Installs all backend components for solution
    /// </summary>
    public class AzurePaaSInstallJob : BaseAnalyticsSolutionInstallJob
    {
        private readonly AutomationAccountTask _automationAccountTask;

        private readonly SqlServerTask _sqlServerTask;
        private readonly SqlServerFirewallConfigTask _sqlServerFirewallConfigTask;
        private readonly SqlDatabaseTask _sqlDatabaseTask;
        private readonly KeyVaultTask _keyVaultTask;

        private readonly AppServicePlanTask _appServicePlanTask;
        private readonly AppServiceWebsiteTask _appServiceWebsiteTask;
        private readonly RedisInstallTask _redisTask;
        private readonly ServiceBusNamespaceInstallTask _serviceBusNamespaceInstallTask;
        private readonly ServiceBusQueueWithPolicyInstallTask _serviceBusQueueWithPolicyInstallTask;
        private readonly StorageAccountInstallTask _storageAccountInstallTask;

        private readonly LogAnalyticsInstallTask _logAnalyticsInstallTask;
        private readonly AppInsightsInstallTask _appInsightsInstallTask;
        private readonly AppInsightsConfigureApiTask _appInsightsConfigureApiTask;
        private readonly TextAnalyticsInstallTask _cognitiveServicesInstallTask;

        /// <summary>
        /// Add tasks in order for execution, some being chained
        /// </summary>
        public AzurePaaSInstallJob(ILogger logger, SolutionInstallConfig config, SubscriptionResource subscription) : base(logger, config, subscription)
        {
            // Performance levels
            var appPerfTier = AppServicePlanTask.PERF_TIER_BASIC1;
            var sqlPerfTier = SqlDatabaseTask.PERF_TIER_BASIC;

            if (_config.EnvironmentType == Models.EnvironmentTypeEnum.Production)
            {
                sqlPerfTier = SqlDatabaseTask.PERF_TIER_S2;
                appPerfTier = AppServicePlanTask.PERF_TIER_BASIC2;
            }

            var tagDic = config.Tags.ToDictionary();

            // Web 
            var appServicePlanConfig = TaskConfig.GetConfigForName(config.AppServiceWebAppName).AddSetting(AppServicePlanTask.CONFIG_KEY_PERF_TIER, appPerfTier);
            _appServicePlanTask = new AppServicePlanTask(appServicePlanConfig, logger, Location, tagDic);

            _appServiceWebsiteTask = new AppServiceWebsiteTask(TaskConfig.GetConfigForName(config.AppServiceWebAppName), logger, Location, tagDic);
            this.AddTask(_appServicePlanTask, _appServiceWebsiteTask);

            // SQL 
            var sqlServerConfig = TaskConfig.GetConfigForName(config.SQLServerName)
                .AddSetting(SqlServerTask.CONFIG_KEY_USERNAME, config.SQLServerAdminUsername)
                .AddSetting(SqlServerTask.CONFIG_KEY_PASSWORD, config.SQLServerAdminPassword);
            const string FIREWALL_RULE_NAME = "O365 Adv Analytics Setup Rule";

            _sqlServerTask = new SqlServerTask(sqlServerConfig, logger, Location, tagDic);
            _sqlServerFirewallConfigTask = new SqlServerFirewallConfigTask(TaskConfig.GetConfigForName(FIREWALL_RULE_NAME), logger, Location);

            var sqlDbConfig = TaskConfig.GetConfigForName(config.SQLServerDatabaseName).AddSetting(SqlDatabaseTask.CONFIG_KEY_PERF_TIER, sqlPerfTier);
            _sqlDatabaseTask = new SqlDatabaseTask(sqlDbConfig, logger, Location, tagDic);

            this.AddTask(_sqlServerTask, _sqlServerFirewallConfigTask, _sqlDatabaseTask);


            // Redis
            _redisTask = new RedisInstallTask(TaskConfig.GetConfigForName(config.RedisName), logger, Location, tagDic);
            this.AddTask(_redisTask);

            // Key vault
            var kvConfig = TaskConfig.GetConfigForName(config.KeyVaultName).AddSetting(KeyVaultTask.CONFIG_KEY_TENANT_ID, config.InstallerAccount.DirectoryId);
            _keyVaultTask = new KeyVaultTask(kvConfig, logger, Location, tagDic);

            // Allow installer account all permissions
            var kvAddRuntimeAccountSecretReadPolicyConfig = TaskConfig.GetConfigForPropAndVal(BaseKeyVaultAddPolicyTask.CONFIG_KEY_CLIENT_ID, config.RuntimeAccountOffice365.ClientId)
                .AddSetting(BaseKeyVaultAddPolicyTask.CONFIG_KEY_TENANT_ID, config.RuntimeAccountOffice365.DirectoryId)
                .AddSetting(BaseKeyVaultAddPolicyTask.CONFIG_KEY_SECRET, config.RuntimeAccountOffice365.Secret);

            // Allow read for runtime account
            var kvAddInstallerAccountSecretAllPolicyConfig = TaskConfig.GetConfigForPropAndVal(BaseKeyVaultAddPolicyTask.CONFIG_KEY_CLIENT_ID, config.InstallerAccount.ClientId)
                .AddSetting(BaseKeyVaultAddPolicyTask.CONFIG_KEY_TENANT_ID, config.InstallerAccount.DirectoryId)
                .AddSetting(BaseKeyVaultAddPolicyTask.CONFIG_KEY_SECRET, config.InstallerAccount.Secret);


            // Allow read for runtime account
            var kvAddInstallerWebAppPermissionsConfig = TaskConfig.GetConfigForPropAndVal(BaseKeyVaultAddPolicyTask.CONFIG_KEY_WEB_APP_NAME, config.AppServiceWebAppName)
                .AddSetting(BaseKeyVaultAddPolicyTask.CONFIG_KEY_TENANT_ID, config.InstallerAccount.DirectoryId);

            var kvSecretAddConfig = TaskConfig.GetConfigForName("appsecret")
                .AddSetting(KeyVaultSecretAddTask.CONFIG_KEY_SECRET_VAL, config.RuntimeAccountOffice365.Secret)     // Add runtime account secret to vault
                .AddSetting(KeyVaultSecretAddTask.CONFIG_KEY_CRED_TENANT_ID, config.InstallerAccount.DirectoryId)
                .AddSetting(KeyVaultSecretAddTask.CONFIG_KEY_CRED_CLIENT_ID, config.InstallerAccount.ClientId)
                .AddSetting(KeyVaultSecretAddTask.CONFIG_KEY_CRED_SECRET, config.InstallerAccount.Secret);
            this.AddTask(_keyVaultTask,
                new KeyVaultAddSecretAllPermissionsForAppRegistrationTask(kvAddInstallerAccountSecretAllPolicyConfig, logger, config.AzureLocation, tagDic),
                new KeyVaultAddSecretReadPolicyForAppRegistrationTask(kvAddRuntimeAccountSecretReadPolicyConfig, logger, config.AzureLocation, tagDic),
                new KeyVaultAddWebAppPermissionsTask(kvAddInstallerWebAppPermissionsConfig, logger, config.AzureLocation, tagDic),
                new KeyVaultSecretAddTask(kvSecretAddConfig, logger));

            // ServiceBus
            const string QUEUE_NAME = "graphcalls";
            const string RULE_NAME = "ListenAndSendPolicy";
            _serviceBusNamespaceInstallTask = new ServiceBusNamespaceInstallTask(TaskConfig.GetConfigForName(config.ServiceBusName), logger, Location, tagDic);

            var queueConfig = TaskConfig.GetConfigForName(QUEUE_NAME).AddSetting(ServiceBusQueueWithPolicyInstallTask.CONFIG_KEY_RULE_NAME, RULE_NAME);
            _serviceBusQueueWithPolicyInstallTask = new ServiceBusQueueWithPolicyInstallTask(queueConfig, logger, Location);
            this.AddTask(_serviceBusNamespaceInstallTask, _serviceBusQueueWithPolicyInstallTask);

            // Storage
            _storageAccountInstallTask = new StorageAccountInstallTask(TaskConfig.GetConfigForName(config.StorageAccountName), logger, Location, tagDic);
            this.AddTask(_storageAccountInstallTask);

            // AppInsights
            _logAnalyticsInstallTask = new LogAnalyticsInstallTask(TaskConfig.GetConfigForName(config.AppInsightsWorkspaceName), logger, Location, tagDic);

            var creds = new ClientSecretCredential(config.InstallerAccount.DirectoryId, config.InstallerAccount.ClientId, config.InstallerAccount.Secret);
            var appInsightsConfig = TaskConfig.GetConfigForName(config.AppInsightsName);
            _appInsightsInstallTask = new AppInsightsInstallTask(appInsightsConfig, logger, Location, tagDic, ResourceGroupName, config.Subscription.SubId, creds);
            _appInsightsConfigureApiTask = new AppInsightsConfigureApiTask(appInsightsConfig, logger, Location, creds, _config.Subscription.SubId, ResourceGroupName);
            this.AddTask(_logAnalyticsInstallTask, _appInsightsInstallTask, _appInsightsConfigureApiTask);

            // Cognitive
            if (config.CognitiveServicesEnabled)
            {
                _cognitiveServicesInstallTask = new TextAnalyticsInstallTask(TaskConfig.GetConfigForName(config.CognitiveServiceName), logger, Location, tagDic);
                this.AddTask(_cognitiveServicesInstallTask);
            }

            if (config.SolutionConfig.ImportTaskSettings.GraphUsageReports)
            {
                // Deploy Automation account. Later, post PaaS install, we will deploy the runbooks
                var automationAccountConfig = TaskConfig.GetConfigForName(config.AutomationAccountName)
                    .AddSetting(AutomationAccountTask.CONFIG_PARAM_NAME_SQL_SERVER, $"{config.SQLServerName}.database.windows.net")
                    .AddSetting(AutomationAccountTask.CONFIG_PARAM_NAME_SQL_DB, config.SQLServerDatabaseName)
                    .AddSetting(AutomationAccountTask.CONFIG_PARAM_NAME_SQL_USERNAME, config.SQLServerAdminUsername)
                    .AddSetting(AutomationAccountTask.CONFIG_PARAM_NAME_SQL_PASSWORD, config.SQLServerAdminPassword)
                    ;

                _automationAccountTask = new AutomationAccountTask(automationAccountConfig, logger, Location, tagDic);
                this.AddTask(_automationAccountTask);
            }
        }

        // Task results, typed
        public AutomationAccountResource CreatedAutomationAccount => GetTaskResult<AutomationAccountResource>(_automationAccountTask);

        public SqlServerResource CreatedSqlServer => GetTaskResult<SqlServerResource>(_sqlServerTask);
        public SqlDatabaseResource CreatedSqlDatabase => GetTaskResult<SqlDatabaseResource>(_sqlDatabaseTask);
        public WebSiteResource CreatedWebSiteResource => GetTaskResult<WebSiteResource>(_appServiceWebsiteTask);
        public DatabasePaaSInfo DatabasePaaSInfo => new DatabasePaaSInfo(CreatedSqlServer, CreatedSqlDatabase, _config);
        public RedisResource Redis => GetTaskResult<RedisResource>(_redisTask);
        public StorageAccountResource Storage => GetTaskResult<StorageAccountResource>(_storageAccountInstallTask);
        public AppInsightsInfoWithApiAccess AppInsights => GetTaskResult<AppInsightsInfoWithApiAccess>(_appInsightsConfigureApiTask);
        public CognitiveServicesInfo CognitiveServicesInfo => _cognitiveServicesInstallTask != null ? GetTaskResult<CognitiveServicesInfo>(_cognitiveServicesInstallTask) : new CognitiveServicesInfo();
        public ServiceBusQueueResourceWithConnectionString SBQueueWithConnectionString => GetTaskResult<ServiceBusQueueResourceWithConnectionString>(_serviceBusQueueWithPolicyInstallTask);
        public KeyVaultResource KeyVault => GetTaskResult<KeyVaultResource>(_keyVaultTask);
    }
}
