using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks.Tasks;
using Azure.Identity;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Storage;
using Azure;
using CloudInstallEngine;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Installs all backend components for solution
    /// </summary>
    public class AzurePaaSInstallJob : BaseAnalyticsSolutionInstallJob
    {
        /// <summary>
        /// Name of the Azure SQL firewall rule the installer owns. Public so the database step can repair it
        /// when Azure reports this host as blocked (issue #326) - it must be the same rule, not a second one.
        /// </summary>
        public const string INSTALLER_FIREWALL_RULE_NAME = "O365 Adv Analytics Setup Rule";

        private readonly GetOrCreateResourceGroupTask _rgCreateTask;
        private readonly AutomationAccountTask _automationAccountTask;

        private readonly SqlServerTask _sqlServerTask;
        private readonly SqlServerFirewallConfigTask _sqlServerFirewallConfigTask;
        private readonly SqlDatabaseTask _sqlDatabaseTask;
        private TaskConfig _sqlServerConfig;
        private TaskConfig _automationAccountConfig;
        private SqlAuthDecision _sqlAuthDecision;
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
            _sqlServerConfig = sqlServerConfig;
            const string FIREWALL_RULE_NAME = INSTALLER_FIREWALL_RULE_NAME;

            _sqlServerTask = new SqlServerTask(sqlServerConfig, logger, Location, tagDic, allowPublicAccess);

            var sqlDbConfig = TaskConfig.GetConfigForName(config.SQLServerDatabaseName).AddSetting(SqlDatabaseTask.CONFIG_KEY_PERF_TIER, sqlPerfTier);
            _sqlDatabaseTask = new SqlDatabaseTask(sqlDbConfig, logger, Location, tagDic);

            // Only configure SQL Server firewall (detect public IP + add client rule) when public network
            // access is enabled. With public access disabled, Azure rejects firewall rule edits with
            // 'DenyPublicEndpointEnabled' and connectivity is expected to come via private endpoint.
            if (allowPublicAccess)
            {
                _sqlServerFirewallConfigTask = new SqlServerFirewallConfigTask(TaskConfig.GetConfigForName(FIREWALL_RULE_NAME), logger, Location, config.KeyVaultName);
                this.AddTask(_sqlServerTask, _sqlServerFirewallConfigTask, _sqlDatabaseTask);
            }
            else
            {
                this.AddTask(_sqlServerTask, _sqlDatabaseTask);
            }


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

            if (!vnetEnabled && allowPublicAccess)
            {
                // Only add firewall rules when not using private endpoints and public access is enabled.
                // With public access disabled, Azure rejects firewall rule edits with 'DenyPublicEndpointEnabled'.
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
            var kvPolicyAllTask = new KeyVaultAddSecretAllPermissionsForAppRegistrationTask(kvAddInstallerAccountSecretAllPolicyConfig, logger, config.AzureLocation, tagDic);
            var kvPolicyReadTask = new KeyVaultAddSecretReadPolicyForAppRegistrationTask(kvAddRuntimeAccountSecretReadPolicyConfig, logger, config.AzureLocation, tagDic);
            var kvWebAppPermissionsTask = new KeyVaultAddWebAppPermissionsTask(kvAddInstallerWebAppPermissionsConfig, logger, config.AzureLocation, tagDic);
            var kvSecretAddTask = new KeyVaultSecretAddTask(kvSecretAddConfig, logger);

            // Enable the Key Vault firewall and allow-list the installer + App Service IPs (public
            // deployments only). It runs right after the vault is created/updated and before the secret
            // upload so the upload's data-plane call is allowed through. Private deployments keep public
            // access disabled and reach the vault via a private endpoint, so no IP rules are needed.
            // See issue #136.
            if (allowPublicAccess)
            {
                var kvFirewallConfig = TaskConfig.GetConfigForName(config.KeyVaultName)
                    .AddSetting(KeyVaultFirewallConfigTask.CONFIG_KEY_APP_SERVICE_NAME, config.AppServiceWebAppName);
                if (vnetEnabled && !string.IsNullOrWhiteSpace(config.NetworkConfig.AppServiceIntegrationSubnetName))
                {
                    var integrationSubnetId = $"/subscriptions/{config.Subscription.SubId}/resourceGroups/{config.ResourceGroupName}/providers/Microsoft.Network/virtualNetworks/{config.NetworkConfig.VNetName}/subnets/{config.NetworkConfig.AppServiceIntegrationSubnetName}";
                    kvFirewallConfig.AddSetting(KeyVaultFirewallConfigTask.CONFIG_KEY_VNET_SUBNET_ID, integrationSubnetId);
                }
                this.AddTask(_keyVaultTask,
                    new KeyVaultFirewallConfigTask(kvFirewallConfig, logger, Location),
                    kvPolicyAllTask, kvPolicyReadTask, kvWebAppPermissionsTask, kvSecretAddTask);
            }
            else
            {
                this.AddTask(_keyVaultTask, kvPolicyAllTask, kvPolicyReadTask, kvWebAppPermissionsTask, kvSecretAddTask);
            }

            // ServiceBus - enforce Premium for VNet (private endpoints require Premium SKU)
            // Service Bus is only required by the Teams calls import; skip provisioning when disabled.
            if (config.ServiceBusEnabled)
            {
                const string QUEUE_NAME = "graphcalls";
                const string RULE_NAME = "ListenAndSendPolicy";
                _serviceBusNamespaceInstallTask = new ServiceBusNamespaceInstallTask(TaskConfig.GetConfigForName(config.ServiceBusName), logger, Location, tagDic, requirePremiumSku: vnetEnabled, allowPublicAccess: allowPublicAccess);

                var queueConfig = TaskConfig.GetConfigForName(QUEUE_NAME).AddSetting(ServiceBusQueueWithPolicyInstallTask.CONFIG_KEY_RULE_NAME, RULE_NAME);
                _serviceBusQueueWithPolicyInstallTask = new ServiceBusQueueWithPolicyInstallTask(queueConfig, logger, Location);
                this.AddTask(_serviceBusNamespaceInstallTask, _serviceBusQueueWithPolicyInstallTask);
            }
            else
            {
                logger.LogInformation("Service Bus is disabled in the installer configuration; skipping Service Bus namespace and queue provisioning. Teams call imports will not be available.");
            }

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
                    .AddSetting(AutomationAccountTask.CONFIG_PARAM_NAME_USE_ENTRA_AUTH,
                        config.SqlAuthMode == SqlServerAuthMode.EntraId ? "true" : "false")
                    ;
                _automationAccountConfig = automationAccountConfig;

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
                else if (vnetEnabled && !allowPublicAccess)
                {
                    // VNet is enabled with public access disabled, but no Hybrid Worker VM was provided.
                    // Automation runbooks cannot reach SQL/Storage/Key Vault via private endpoints without
                    // a hybrid worker running inside the VNet. The Automation account is still created so
                    // the customer can complete the configuration later.
                    logger.LogWarning("VNet is enabled with public network access disabled, but no Hybrid Runbook Worker VM was specified. " +
                        "Automation runbooks (Graph usage reports) will NOT be able to run because the Automation account cannot reach SQL/Storage/Key Vault via private endpoints from the Azure-hosted sandbox. " +
                        "To enable runbooks: 1) Create a Windows VM connected to the VNet '" + (config.NetworkConfig?.VNetName ?? "<vnet>") + "' (any subnet that can reach the private endpoints), " +
                        "2) Re-run this installer, 3) On the Networking page set the Hybrid Worker VM Resource ID to that VM, and the installer will register it as a Hybrid Runbook Worker and install the required extension.");
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

                // Redis - Managed Redis is created with a new private endpoint and DNS zone here.
                // If RedisInstallTask detected and reused a pre-existing legacy classic
                // Azure Cache for Redis, RedisPrivateEndpointInstallTask / RedisPrivateDnsZoneInstallTask
                // will log a warning and skip — the legacy resource retains its own networking.
                // The Private Link group ID, target resource ID, and DNS zone name are NOT hardcoded
                // here: the Redis-aware wrappers derive them from RedisInstallTask.LastResult at
                // execution time, so the values always match the Redis kind we actually got.
                var redisPeName = peNames.GetNameOrDefault(peNames.Redis, $"pe-{config.RedisName}-redis");
                var redisPeConfig = TaskConfig.GetConfigForName(redisPeName)
                    .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_SUBNET_ID, subnetId);
                this.AddTask(new RedisPrivateEndpointInstallTask(redisPeConfig, logger, Location, tagDic, _redisTask));
                if (deployDns)
                {
                    var redisDnsConfig = TaskConfig.NoConfig
                        .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_VNET_ID, vnetId)
                        .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_PE_NAME, redisPeName);
                    this.AddTask(new RedisPrivateDnsZoneInstallTask(redisDnsConfig, logger, Location, tagDic, _redisTask));
                }

                // Storage
                var storagePeName = peNames.GetNameOrDefault(peNames.Storage, $"pe-{config.StorageAccountName}-blob");
                AddPrivateEndpointTask(storagePeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Storage/storageAccounts/{config.StorageAccountName}",
                    "blob", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.blob.core.windows.net", vnetId, storagePeName, logger, tagDic);

                // Storage - table sub-resource. The audit-import blob checkpoint (ProcessedBlobStoreFactory /
                // AzureTableProcessedBlobStore) uses Azure Table storage; without its own private endpoint the
                // table endpoint is unreachable on private deployments (403 AuthorizationFailure) and the
                // importer silently falls back to a non-durable in-memory checkpoint.
                var storageTablePeName = peNames.GetNameOrDefault(peNames.StorageTable, $"pe-{config.StorageAccountName}-table");
                AddPrivateEndpointTask(storageTablePeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.Storage/storageAccounts/{config.StorageAccountName}",
                    "table", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.table.core.windows.net", vnetId, storageTablePeName, logger, tagDic);

                // Key Vault
                var kvPeName = peNames.GetNameOrDefault(peNames.KeyVault, $"pe-{config.KeyVaultName}-vault");
                AddPrivateEndpointTask(kvPeName, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.KeyVault/vaults/{config.KeyVaultName}",
                    "vault", subnetId, logger, tagDic);
                if (deployDns) AddPrivateDnsZoneTask("privatelink.vaultcore.azure.net", vnetId, kvPeName, logger, tagDic);

                // Service Bus. Private endpoints require the Premium SKU: the wrappers skip (with a clear
                // warning) rather than fail when a pre-existing Standard namespace can't be made private.
                if (config.ServiceBusEnabled)
                {
                    var sbPeName = peNames.GetNameOrDefault(peNames.ServiceBus, $"pe-{config.ServiceBusName}-sb");
                    var sbPeConfig = TaskConfig.GetConfigForName(sbPeName)
                        .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_TARGET_RESOURCE_ID, $"/subscriptions/{subId}/resourceGroups/{rgName}/providers/Microsoft.ServiceBus/namespaces/{config.ServiceBusName}")
                        .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_GROUP_ID, "namespace")
                        .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_SUBNET_ID, subnetId);
                    this.AddTask(new ServiceBusPrivateEndpointInstallTask(sbPeConfig, logger, Location, tagDic, _serviceBusNamespaceInstallTask));

                    if (deployDns)
                    {
                        var sbDnsConfig = TaskConfig.GetConfigForName("privatelink.servicebus.windows.net")
                            .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_VNET_ID, vnetId)
                            .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_PE_NAME, sbPeName);
                        this.AddTask(new ServiceBusPrivateDnsZoneInstallTask(sbDnsConfig, logger, Location, tagDic, _serviceBusNamespaceInstallTask));
                    }
                }

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

        /// <summary>
        /// Runs the install, choosing the SQL authentication method around it: the Microsoft Entra
        /// administrator has to be resolved BEFORE the SQL server is created (it can only be set at
        /// creation, since an existing server is never reconfigured), and what the server actually
        /// supports can only be read back AFTER. See issue #117.
        /// </summary>
        public override async Task Install(object previousRunResult)
        {
            await ConfigureSqlEntraAdministrator();

            await base.Install(previousRunResult);

            await DetectSqlAuthMethod();
        }

        /// <summary>
        /// Works out which Microsoft Entra principal should be the new SQL server's administrator and feeds
        /// it to <see cref="SqlServerTask"/>. No-op unless the operator selected Entra authentication.
        /// </summary>
        /// <remarks>
        /// Defaults to the installer's own service principal. Azure allows exactly one Entra administrator
        /// per server, and the installer is the identity that has to sign in to the database to run the
        /// schema upgrade and create the App Service's contained user - so making anything else the
        /// administrator by default would break the install it is supposed to enable.
        /// </remarks>
        async Task ConfigureSqlEntraAdministrator()
        {
            if (_config.SqlAuthMode != SqlServerAuthMode.EntraId || _sqlServerConfig == null) return;

            // Re-entrancy guard: TaskConfig.AddSetting throws on a duplicate key, so a second Install()
            // call on the same job instance must not try to add these again.
            if (_sqlServerConfig.ContainsKey(SqlServerTask.CONFIG_KEY_ENTRA_ADMIN_OBJECT_ID)) return;

            var objectId = (_config.SQLEntraAdminObjectId ?? string.Empty).Trim();
            var login = (_config.SQLEntraAdminLogin ?? string.Empty).Trim();
            var principalType = string.IsNullOrWhiteSpace(_config.SQLEntraAdminPrincipalType) ? "User" : _config.SQLEntraAdminPrincipalType.Trim();

            if (string.IsNullOrWhiteSpace(objectId))
            {
                try
                {
                    objectId = await ServicePrincipalResolver.GetObjectIdFromClientCredentials(
                        _config.InstallerAccount.DirectoryId, _config.InstallerAccount.ClientId, _config.InstallerAccount.Secret);
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        "Could not resolve the installer service principal's object ID, so the SQL Server cannot be created with " +
                        $"Microsoft Entra ID authentication: {ex.Message}. Falling back to SQL Server authentication.");
                    return;
                }

                login = string.IsNullOrWhiteSpace(login) ? _config.InstallerAccount.ClientId : login;
                principalType = "Application";

                Logger.LogInformation(
                    "No Microsoft Entra administrator was configured for Azure SQL, so the installer's own service principal will be " +
                    "used. That is the identity which signs in to the database to apply schema upgrades. You can add a human " +
                    "administrator afterwards in the Azure portal (SQL Server > Microsoft Entra ID).");
            }
            else
            {
                Logger.LogWarning(
                    $"A Microsoft Entra administrator ('{login}') is configured for the SQL Server. Azure allows only ONE Entra " +
                    "administrator per server, so make sure the installer's service principal can also sign in to the database - " +
                    "typically by making the administrator a security group that contains it. Otherwise the database schema " +
                    "upgrade will fail with a login error.");
            }

            _sqlServerConfig
                .AddSetting(SqlServerTask.CONFIG_KEY_ENTRA_ADMIN_OBJECT_ID, objectId)
                .AddSetting(SqlServerTask.CONFIG_KEY_ENTRA_ADMIN_LOGIN, login ?? string.Empty)
                .AddSetting(SqlServerTask.CONFIG_KEY_ENTRA_ADMIN_TENANT_ID, _config.InstallerAccount.DirectoryId ?? string.Empty)
                .AddSetting(SqlServerTask.CONFIG_KEY_ENTRA_ADMIN_PRINCIPAL_TYPE, principalType)

                // Entra-only (SQL authentication disabled) is only ever applied to a server this run
                // creates. SqlServerTask ignores it for an existing server.
                .AddSetting(SqlServerTask.CONFIG_KEY_ENTRA_ONLY_AUTH, "true");
        }

        /// <summary>
        /// Reads back what the SQL server actually supports and decides how to connect to it.
        /// </summary>
        async Task DetectSqlAuthMethod()
        {
            var haveSqlCredentials = !string.IsNullOrWhiteSpace(_config.SQLServerAdminUsername)
                && !string.IsNullOrWhiteSpace(_config.SQLServerAdminPassword);

            SqlServerAuthState state = null;
            try
            {
                state = await SqlServerAuthReader.ReadAsync(CreatedSqlServer, Logger);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Could not read the SQL Server's authentication configuration: {ex.Message}");
            }

            _sqlAuthDecision = SqlServerAuthDetection.Decide(state, haveSqlCredentials, _config.SqlAuthMode);
            Logger.LogInformation($"SQL authentication: {_sqlAuthDecision.Reason}");

            await RepairAutomationSqlCredentialIfNeeded();
        }

        /// <summary>
        /// Creates the Automation account's SQL credential when the install turned out to need one after all.
        /// </summary>
        /// <remarks>
        /// The Automation account is provisioned before the SQL server's real capabilities are known, so it
        /// is told what the operator asked for. When Entra ID was selected but detection fell back to a SQL
        /// login - an install pointed at an existing SQL-authentication server, which is never reconfigured -
        /// the credential the runbooks need was skipped. Put it back rather than leaving Graph usage-report
        /// automation silently broken. See issue #117.
        /// </remarks>
        async Task RepairAutomationSqlCredentialIfNeeded()
        {
            if (_automationAccountTask == null || _automationAccountConfig == null) return;
            if (_sqlAuthDecision == null || _sqlAuthDecision.UsesEntraId) return;
            if (_config.SqlAuthMode != SqlServerAuthMode.EntraId) return;   // credential was already created

            AutomationAccountResource automationAccount;
            try
            {
                automationAccount = CreatedAutomationAccount;
            }
            catch (Exception)
            {
                return;
            }
            if (automationAccount == null) return;

            if (string.IsNullOrWhiteSpace(_config.SQLServerAdminUsername) || string.IsNullOrWhiteSpace(_config.SQLServerAdminPassword))
            {
                Logger.LogWarning(
                    "This install fell back to SQL Server authentication, but no SQL administrator username/password is " +
                    $"configured, so the Automation account's '{AutomationAccountTask.CRED_SQL_NAME}' credential could not be " +
                    "created. The Graph usage-report maintenance runbooks will not be able to connect to the database.");
                return;
            }

            try
            {
                Logger.LogInformation(
                    $"Creating the Automation account's '{AutomationAccountTask.CRED_SQL_NAME}' credential: this install fell back " +
                    "to SQL Server authentication, so the runbooks need a SQL login after all.");

                var credential = new Azure.ResourceManager.Automation.Models.AutomationCredentialCreateOrUpdateContent(
                    AutomationAccountTask.CRED_SQL_NAME, _config.SQLServerAdminUsername, _config.SQLServerAdminPassword);

                await automationAccount.GetAutomationCredentials()
                    .CreateOrUpdateAsync(WaitUntil.Completed, AutomationAccountTask.CRED_SQL_NAME, credential);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Could not create the Automation account's SQL credential: {ex.Message}");
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
        public AppServicePlanResource CreatedAppServicePlan => GetTaskResult<AppServicePlanResource>(_appServicePlanTask);
        public WebSiteResource CreatedWebSiteResource => GetTaskResult<WebSiteResource>(_appServiceWebsiteTask);

        /// <summary>
        /// How the installer decided to authenticate to SQL for this run, and why. Null until the job has
        /// run. See issue #117.
        /// </summary>
        public SqlAuthDecision SqlAuthDecision => _sqlAuthDecision;

        public DatabasePaaSInfo DatabasePaaSInfo => new DatabasePaaSInfo(CreatedSqlServer, CreatedSqlDatabase, _config)
        {
            AuthMethod = _sqlAuthDecision != null ? _sqlAuthDecision.Method : SqlConnectionAuthMethod.SqlLogin
        };
        public RedisInstallResult Redis => GetTaskResult<RedisInstallResult>(_redisTask);
        public StorageAccountResource Storage => GetTaskResult<StorageAccountResource>(_storageAccountInstallTask);
        public AppInsightsInfo AppInsights => GetTaskResult<AppInsightsInfo>(_appInsightsInstallTask);
        public CognitiveServicesInfo CognitiveServicesInfo => _cognitiveServicesInstallTask != null ? GetTaskResult<CognitiveServicesInfo>(_cognitiveServicesInstallTask) : new CognitiveServicesInfo();
        public ServiceBusQueueResourceWithConnectionString SBQueueWithConnectionString => _serviceBusQueueWithPolicyInstallTask != null ? GetTaskResult<ServiceBusQueueResourceWithConnectionString>(_serviceBusQueueWithPolicyInstallTask) : null;
        public KeyVaultResource KeyVault => GetTaskResult<KeyVaultResource>(_keyVaultTask);
        public VirtualNetworkResource VNet => _vnetInstallTask != null ? GetTaskResult<VirtualNetworkResource>(_vnetInstallTask) : null;
        public string HybridWorkerGroupName => _hybridWorkerGroupName;
    }
}
