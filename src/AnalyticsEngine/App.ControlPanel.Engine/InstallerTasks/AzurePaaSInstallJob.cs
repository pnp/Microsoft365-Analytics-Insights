using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks.Tasks;
using Azure.Identity;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.RedisEnterprise;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Storage;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Installs all backend components for solution
    /// </summary>
    public class AzurePaaSInstallJob : BaseAnalyticsSolutionInstallJob
    {
        private readonly GetOrCreateResourceGroupTask _rgCreateTask;
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
        private readonly TextAnalyticsInstallTask _cognitiveServicesInstallTask;

        private readonly VNetInstallTask _vnetInstallTask;
        private readonly HybridWorkerGroupTask _hybridWorkerGroupTask;
        private string _hybridWorkerGroupName;

        /// <summary>
        /// Add tasks in order for execution, some being chained
        /// </summary>
        public AzurePaaSInstallJob(ILogger logger, SolutionInstallConfig config, SubscriptionResource subscription) : base(logger, config, subscription)
        {

            var tagDic = config.Tags.ToDictionary();
            var vnetEnabled = config.NetworkConfig != null && config.NetworkConfig.Enabled;
            // When VNet is disabled we always allow public access (legacy/default behaviour).
            // When VNet is enabled, honour the AllowPublicAccess flag (some customer Azure policies
            // disallow public access on PaaS resources).
            var allowPublicAccess = !vnetEnabled || (config.NetworkConfig != null && config.NetworkConfig.AllowPublicAccess);

            if (!allowPublicAccess)
            {
                logger.LogWarning("Public network access will be disabled on Azure PaaS resources (SQL, Storage, Key Vault, Redis, Service Bus, App Service, Automation, Cognitive Services). " +
                    "If this installer is NOT running on a machine connected to the private network (VNet, peered network, VPN/ExpressRoute, or Azure Bastion-attached host), the following steps will fail: " +
                    "Key Vault secret upload (appsecret), SQL connectivity test and database initialization, and the App Service warm-up request. " +
                    "These failures are non-fatal — the resources are still created and configured — but you must re-run the installer from inside the private network (or temporarily re-enable public access on Key Vault and SQL) to complete those steps.");
            }

            _rgCreateTask = new GetOrCreateResourceGroupTask(TaskConfig.GetConfigForName(config.ResourceGroupName), logger, Location, tagDic, subscription);
            this.AddTask(_rgCreateTask);

            // Performance levels - enforce higher tiers when VNet/private endpoints are needed
            var appPerfTier = AppServicePlanTask.PERF_TIER_BASIC1;
            var sqlPerfTier = SqlDatabaseTask.PERF_TIER_BASIC;

            if (_config.EnvironmentType == Models.EnvironmentTypeEnum.Production)
            {
                sqlPerfTier = SqlDatabaseTask.PERF_TIER_S2;
                appPerfTier = AppServicePlanTask.PERF_TIER_BASIC2;
            }

            // VNet integration requires certain minimum SKUs:
            // - Redis: Standard (Basic does not support VNet/PE)
            // - Service Bus: Premium (private endpoints require Premium SKU)
            // - SQL: S0+ recommended (Basic works for PE but S0 for production)
            // - App Service: Basic+ (B1 supports VNet integration)
            // - Storage: Standard_LRS supports PE at all tiers
            // - Key Vault: Standard supports PE at all tiers
            if (vnetEnabled && sqlPerfTier == SqlDatabaseTask.PERF_TIER_BASIC)
            {
                // SQL Basic doesn't have issues with PE, but S2 is safer for VNet scenarios
                sqlPerfTier = SqlDatabaseTask.PERF_TIER_S2;
            }

            // VNet - create before other resources if enabled
            if (vnetEnabled)
            {
                var vnetConfig = TaskConfig.GetConfigForName(config.NetworkConfig.VNetName)
                    .AddSetting(VNetInstallTask.CONFIG_KEY_ADDRESS_PREFIX, config.NetworkConfig.AddressPrefix)
                    .AddSetting(VNetInstallTask.CONFIG_KEY_SUBNET_NAME, config.NetworkConfig.SubnetName)
                    .AddSetting(VNetInstallTask.CONFIG_KEY_SUBNET_ADDRESS_PREFIX, config.NetworkConfig.SubnetAddressPrefix)
                    .AddSetting(VNetInstallTask.CONFIG_KEY_APP_INTEGRATION_SUBNET_NAME, config.NetworkConfig.AppServiceIntegrationSubnetName ?? string.Empty)
                    .AddSetting(VNetInstallTask.CONFIG_KEY_APP_INTEGRATION_SUBNET_ADDRESS_PREFIX, config.NetworkConfig.AppServiceIntegrationSubnetAddressPrefix ?? string.Empty);
                _vnetInstallTask = new VNetInstallTask(vnetConfig, logger, Location, tagDic);
                this.AddTask(_vnetInstallTask);
            }

            // Web 
            var appServicePlanConfig = TaskConfig.GetConfigForName(config.AppServiceWebAppName).AddSetting(AppServicePlanTask.CONFIG_KEY_PERF_TIER, appPerfTier);
            _appServicePlanTask = new AppServicePlanTask(appServicePlanConfig, logger, Location, tagDic);

            var appServiceConfig = TaskConfig.GetConfigForName(config.AppServiceWebAppName);
            if (vnetEnabled && !string.IsNullOrWhiteSpace(config.NetworkConfig.AppServiceIntegrationSubnetName))
            {
                var integrationSubnetId = $"/subscriptions/{config.Subscription.SubId}/resourceGroups/{config.ResourceGroupName}/providers/Microsoft.Network/virtualNetworks/{config.NetworkConfig.VNetName}/subnets/{config.NetworkConfig.AppServiceIntegrationSubnetName}";
                appServiceConfig.AddSetting(AppServiceWebsiteTask.CONFIG_KEY_VNET_INTEGRATION_SUBNET_ID, integrationSubnetId);
            }
            _appServiceWebsiteTask = new AppServiceWebsiteTask(appServiceConfig, logger, Location, tagDic, allowPublicAccess);
            this.AddTask(_appServicePlanTask, _appServiceWebsiteTask);

            // SQL 
            var sqlServerConfig = TaskConfig.GetConfigForName(config.SQLServerName)
                .AddSetting(SqlServerTask.CONFIG_KEY_USERNAME, config.SQLServerAdminUsername)
                .AddSetting(SqlServerTask.CONFIG_KEY_PASSWORD, config.SQLServerAdminPassword);
            const string FIREWALL_RULE_NAME = "O365 Adv Analytics Setup Rule";

            _sqlServerTask = new SqlServerTask(sqlServerConfig, logger, Location, tagDic, allowPublicAccess);
            _sqlServerFirewallConfigTask = new SqlServerFirewallConfigTask(TaskConfig.GetConfigForName(FIREWALL_RULE_NAME), logger, Location);

            var sqlDbConfig = TaskConfig.GetConfigForName(config.SQLServerDatabaseName).AddSetting(SqlDatabaseTask.CONFIG_KEY_PERF_TIER, sqlPerfTier);
            _sqlDatabaseTask = new SqlDatabaseTask(sqlDbConfig, logger, Location, tagDic);

            this.AddTask(_sqlServerTask, _sqlServerFirewallConfigTask, _sqlDatabaseTask);


            // Redis - enforce Standard SKU for VNet
            _redisTask = new RedisInstallTask(TaskConfig.GetConfigForName(config.RedisName), logger, Location, tagDic, vnetEnabled, allowPublicAccess);

            // Redis access policy assignment for data-plane RBAC access (required when key-based auth is disabled)
            var redisAccessPolicyConfig = TaskConfig.GetConfigForName(config.RedisName)
                .AddSetting(RedisAccessPolicyAssignmentTask.CONFIG_KEY_CLIENT_ID, config.RuntimeAccountOffice365.ClientId)
                .AddSetting(RedisAccessPolicyAssignmentTask.CONFIG_KEY_CLIENT_SECRET, config.RuntimeAccountOffice365.Secret)
                .AddSetting(RedisAccessPolicyAssignmentTask.CONFIG_KEY_TENANT_ID, config.RuntimeAccountOffice365.DirectoryId)
                .AddSetting(RedisAccessPolicyAssignmentTask.CONFIG_KEY_INSTALLER_CLIENT_ID, config.InstallerAccount.ClientId)
                .AddSetting(RedisAccessPolicyAssignmentTask.CONFIG_KEY_INSTALLER_CLIENT_SECRET, config.InstallerAccount.Secret)
                .AddSetting(RedisAccessPolicyAssignmentTask.CONFIG_KEY_INSTALLER_TENANT_ID, config.InstallerAccount.DirectoryId);
            var _redisAccessPolicyTask = new RedisAccessPolicyAssignmentTask(redisAccessPolicyConfig, logger, Location, tagDic);

            if (!vnetEnabled)
            {
                // Only add firewall rules when not using private endpoints
                var redisFirewallConfig = TaskConfig.GetConfigForName(config.RedisName)
                    .AddSetting(RedisFirewallConfigTask.CONFIG_KEY_APP_SERVICE_NAME, config.AppServiceWebAppName);
                var _redisFirewallTask = new RedisFirewallConfigTask(redisFirewallConfig, logger, Location);
                this.AddTask(_redisTask, _redisAccessPolicyTask, _redisFirewallTask);
            }
            else
            {
                this.AddTask(_redisTask, _redisAccessPolicyTask);
            }

            // Key vault
            var kvConfig = TaskConfig.GetConfigForName(config.KeyVaultName).AddSetting(KeyVaultTask.CONFIG_KEY_TENANT_ID, config.InstallerAccount.DirectoryId);
            _keyVaultTask = new KeyVaultTask(kvConfig, logger, Location, tagDic, allowPublicAccess);

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

            // ServiceBus - enforce Premium for VNet (private endpoints require Premium SKU)
            const string QUEUE_NAME = "graphcalls";
            const string RULE_NAME = "ListenAndSendPolicy";
            _serviceBusNamespaceInstallTask = new ServiceBusNamespaceInstallTask(TaskConfig.GetConfigForName(config.ServiceBusName), logger, Location, tagDic, requirePremiumSku: vnetEnabled, allowPublicAccess: allowPublicAccess);

            var queueConfig = TaskConfig.GetConfigForName(QUEUE_NAME).AddSetting(ServiceBusQueueWithPolicyInstallTask.CONFIG_KEY_RULE_NAME, RULE_NAME);
            _serviceBusQueueWithPolicyInstallTask = new ServiceBusQueueWithPolicyInstallTask(queueConfig, logger, Location);
            this.AddTask(_serviceBusNamespaceInstallTask, _serviceBusQueueWithPolicyInstallTask);

            // Storage
            _storageAccountInstallTask = new StorageAccountInstallTask(TaskConfig.GetConfigForName(config.StorageAccountName), logger, Location, tagDic, allowPublicAccess);
            this.AddTask(_storageAccountInstallTask);

            // AppInsights
            // Note: Log Analytics and Application Insights are intentionally always created with public
            // network access enabled, even when the user has selected "no public access". Making these
            // private requires an Azure Monitor Private Link Scope (AMPLS) plus the Azure Monitor private
            // DNS zones, which can affect Azure Monitor connectivity for every VNet that resolves those
            // zones (potentially across unrelated workloads/subscriptions). Customers that need these
            // resources to be private must configure AMPLS manually after install.
            if (!allowPublicAccess)
            {
                logger.LogWarning("Log Analytics and Application Insights will be created with public network access enabled. " +
                    "To make them private, configure an Azure Monitor Private Link Scope (AMPLS) manually after install. " +
                    "See https://learn.microsoft.com/azure/azure-monitor/logs/private-link-security for details.");
            }
            _logAnalyticsInstallTask = new LogAnalyticsInstallTask(TaskConfig.GetConfigForName(config.AppInsightsWorkspaceName), logger, Location, tagDic);

            var creds = new ClientSecretCredential(config.InstallerAccount.DirectoryId, config.InstallerAccount.ClientId, config.InstallerAccount.Secret);
            var appInsightsConfig = TaskConfig.GetConfigForName(config.AppInsightsName);
            _appInsightsInstallTask = new AppInsightsInstallTask(appInsightsConfig, logger, Location, tagDic, ResourceGroupName, config.Subscription.SubId, creds);
            this.AddTask(_logAnalyticsInstallTask, _appInsightsInstallTask);

            // Cognitive
            if (config.CognitiveServicesEnabled)
            {
                _cognitiveServicesInstallTask = new TextAnalyticsInstallTask(TaskConfig.GetConfigForName(config.CognitiveServiceName), logger, Location, tagDic, allowPublicAccess);
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

                _automationAccountTask = new AutomationAccountTask(automationAccountConfig, logger, Location, tagDic, allowPublicAccess);
                this.AddTask(_automationAccountTask);

                // Hybrid Worker Group - when VNet is enabled and a VM resource ID is configured,
                // create a hybrid worker group so runbooks can run inside the VNet
                if (vnetEnabled && !string.IsNullOrWhiteSpace(config.NetworkConfig?.HybridWorkerVmResourceId))
                {
                    _hybridWorkerGroupName = $"{config.AutomationAccountName}-vnet-workers";
                    var hwgConfig = TaskConfig.GetConfigForName(_hybridWorkerGroupName)
                        .AddSetting(HybridWorkerGroupTask.CONFIG_KEY_AUTOMATION_ACCOUNT_NAME, config.AutomationAccountName)
                        .AddSetting(HybridWorkerGroupTask.CONFIG_KEY_VM_RESOURCE_ID, config.NetworkConfig.HybridWorkerVmResourceId);
                    _hybridWorkerGroupTask = new HybridWorkerGroupTask(hwgConfig, logger, Location, tagDic, creds);
                    this.AddTask(_hybridWorkerGroupTask);
                }
            }

            // Private endpoints and DNS zones for all resources when VNet is enabled
            if (vnetEnabled)
            {
                var subId = config.Subscription.SubId;
                var rgName = config.ResourceGroupName;
                var subnetId = $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Network/virtualNetworks/{config.NetworkConfig.VNetName}/subnets/{config.NetworkConfig.SubnetName}";
                var vnetId = $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Network/virtualNetworks/{config.NetworkConfig.VNetName}";
                var deployDns = config.NetworkConfig.DeployDnsZones;
                var peNames = config.NetworkConfig.CustomEndpointNames ?? new PrivateEndpointNames();

                // SQL Server
                var sqlPeName = peNames.GetNameOrDefault(peNames.SqlServer, $"pe-{config.SQLServerName}-sql");
                AddPrivateEndpointTask(sqlPeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Sql/servers/{config.SQLServerName}",
                    "sqlServer", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.database.windows.net", vnetId, sqlPeName, logger, tagDic);

                // App Service
                var appPeName = peNames.GetNameOrDefault(peNames.AppService, $"pe-{config.AppServiceWebAppName}-app");
                AddPrivateEndpointTask(appPeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Web/sites/{config.AppServiceWebAppName}",
                    "sites", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.azurewebsites.net", vnetId, appPeName, logger, tagDic);

                // Redis
                var redisPeName = peNames.GetNameOrDefault(peNames.Redis, $"pe-{config.RedisName}-redis");
                AddPrivateEndpointTask(redisPeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Cache/redisEnterprise/{config.RedisName}",
                    "redisEnterprise", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.redisenterprise.cache.azure.net", vnetId, redisPeName, logger, tagDic);

                // Storage
                var storagePeName = peNames.GetNameOrDefault(peNames.Storage, $"pe-{config.StorageAccountName}-blob");
                AddPrivateEndpointTask(storagePeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Storage/storageAccounts/{config.StorageAccountName}",
                    "blob", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.blob.core.windows.net", vnetId, storagePeName, logger, tagDic);

                // Key Vault
                var kvPeName = peNames.GetNameOrDefault(peNames.KeyVault, $"pe-{config.KeyVaultName}-vault");
                AddPrivateEndpointTask(kvPeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.KeyVault/vaults/{config.KeyVaultName}",
                    "vault", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.vaultcore.azure.net", vnetId, kvPeName, logger, tagDic);

                // Service Bus
                var sbPeName = peNames.GetNameOrDefault(peNames.ServiceBus, $"pe-{config.ServiceBusName}-sb");
                AddPrivateEndpointTask(sbPeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.ServiceBus/namespaces/{config.ServiceBusName}",
                    "namespace", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.servicebus.windows.net", vnetId, sbPeName, logger, tagDic);

                // Cognitive Services (Language/Text Analytics)
                if (config.CognitiveServicesEnabled)
                {
                    var cognitivePeName = peNames.GetNameOrDefault(peNames.CognitiveServices, $"pe-{config.CognitiveServiceName}-cognitive");
                    AddPrivateEndpointTask(cognitivePeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.CognitiveServices/accounts/{config.CognitiveServiceName}",
                        "account", subnetId, logger, tagDic);
                    if (deployDns) AddPrivateDnsZoneTask("privatelink.cognitiveservices.azure.com", vnetId, cognitivePeName, logger, tagDic);
                }

                // Automation Account
                if (config.SolutionConfig.ImportTaskSettings.GraphUsageReports && !string.IsNullOrWhiteSpace(config.AutomationAccountName))
                {
                    var automationPeName = peNames.GetNameOrDefault(peNames.AutomationAccount, $"pe-{config.AutomationAccountName}-automation");
                    AddPrivateEndpointTask(automationPeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Automation/automationAccounts/{config.AutomationAccountName}",
                        "DSCAndHybridWorker", subnetId, logger, tagDic);
                    if (deployDns) AddPrivateDnsZoneTask("privatelink.azure-automation.net", vnetId, automationPeName, logger, tagDic);
                }
            }
        }

        private void AddPrivateEndpointTask(string peName, string targetResourceId, string groupId, string subnetId, ILogger logger, Dictionary<string, string> tags)
        {
            var peConfig = TaskConfig.GetConfigForName(peName)
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_TARGET_RESOURCE_ID, targetResourceId)
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_GROUP_ID, groupId)
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_SUBNET_ID, subnetId);
            this.AddTask(new PrivateEndpointInstallTask(peConfig, logger, Location, tags));
        }

        private void AddPrivateDnsZoneTask(string zoneName, string vnetId, string peName, ILogger logger, Dictionary<string, string> tags)
        {
            var dnsConfig = TaskConfig.GetConfigForName(zoneName)
                .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_VNET_ID, vnetId)
                .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_PE_NAME, peName);
            this.AddTask(new PrivateDnsZoneInstallTask(dnsConfig, logger, Location, tags));
        }

        // Task results, typed
        public AutomationAccountResource CreatedAutomationAccount => GetTaskResult<AutomationAccountResource>(_automationAccountTask);

        public SqlServerResource CreatedSqlServer => GetTaskResult<SqlServerResource>(_sqlServerTask);
        public SqlDatabaseResource CreatedSqlDatabase => GetTaskResult<SqlDatabaseResource>(_sqlDatabaseTask);
        public WebSiteResource CreatedWebSiteResource => GetTaskResult<WebSiteResource>(_appServiceWebsiteTask);
        public DatabasePaaSInfo DatabasePaaSInfo => new DatabasePaaSInfo(CreatedSqlServer, CreatedSqlDatabase, _config);
        public RedisEnterpriseDatabaseResource Redis => GetTaskResult<RedisEnterpriseDatabaseResource>(_redisTask);
        public StorageAccountResource Storage => GetTaskResult<StorageAccountResource>(_storageAccountInstallTask);
        public AppInsightsInfo AppInsights => GetTaskResult<AppInsightsInfo>(_appInsightsInstallTask);
        public CognitiveServicesInfo CognitiveServicesInfo => _cognitiveServicesInstallTask != null ? GetTaskResult<CognitiveServicesInfo>(_cognitiveServicesInstallTask) : new CognitiveServicesInfo();
        public ServiceBusQueueResourceWithConnectionString SBQueueWithConnectionString => GetTaskResult<ServiceBusQueueResourceWithConnectionString>(_serviceBusQueueWithPolicyInstallTask);
        public KeyVaultResource KeyVault => GetTaskResult<KeyVaultResource>(_keyVaultTask);
        public VirtualNetworkResource VNet => _vnetInstallTask != null ? GetTaskResult<VirtualNetworkResource>(_vnetInstallTask) : null;
        public string HybridWorkerGroupName => _hybridWorkerGroupName;
    }
}
