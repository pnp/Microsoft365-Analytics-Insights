using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Resources;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Secures resources in the resource group by assigning RBAC roles.
    /// </summary>
    /// <remarks>
    /// Every data-plane role is assigned on the one resource the runtime uses, never on the resource group: the resource
    /// group can hold other workloads (the installer accepts an existing one), and a role there would reach their storage
    /// accounts, Service Bus namespaces and AI accounts too. Reader stays on the resource group: it is control-plane
    /// read access only. Older installers assigned the data-plane roles on the resource group; a re-run adds the
    /// resource-scoped assignment and reports the old one, which an administrator can then remove (see
    /// <see cref="RoleAssignmentTask"/>).
    /// </remarks>
    public class ResourceSecurityInstallJob : BaseAnalyticsSolutionInstallJob
    {
        public const string FOUNDRY_OPENAI_USER_ROLE_NAME = "Cognitive Services OpenAI User";

        private readonly RoleAssignmentTask _appInsightsReaderRoleTask;
        private readonly RoleAssignmentTask _storageBlobDataContributorRoleTask;
        private readonly RoleAssignmentTask _storageTableDataContributorRoleTask;
        private readonly RoleAssignmentTask _cognitiveServicesUserRoleTask;
        private readonly RoleAssignmentTask _foundryOpenAIUserRoleTask;
        private readonly RoleAssignmentTask _serviceBusDataOwnerRoleTask;

        public ResourceSecurityInstallJob(ILogger logger, SolutionInstallConfig config, SubscriptionResource subscription) : base(logger, config, subscription)
        {
            var tagDic = config.Tags.ToDictionary();

            // Assign Reader role to the runtime account on the resource group (covers App Insights and all resources).
            // Control-plane read only: it grants no data access. The App Insights query API needs read access on the
            // Application Insights resource.
            _appInsightsReaderRoleTask = new RoleAssignmentTask(RuntimeRoleConfig(config, "Reader", null), logger, Location, tagDic);
            this.AddTask(_appInsightsReaderRoleTask);

            // Assign Storage Blob Data Contributor role to the runtime account so it can create containers and upload blobs.
            // Only on the solution's storage account: every blob the runtime reads or writes (audit checkpoint, message tracing)
            // is in the account named by its Storage connection string.
            _storageBlobDataContributorRoleTask = new RoleAssignmentTask(
                RuntimeRoleConfig(config, "Storage Blob Data Contributor", StorageAccountScope(config)), logger, Location, tagDic);
            this.AddTask(_storageBlobDataContributorRoleTask);

            // Assign "Storage Table Data Contributor" to the runtime account for Table data-plane access.
            // The runtime state table (Common.Entities.State: import schedule, delta tokens, Teams authorisation tokens)
            // and the audit-import blob checkpoint live in Azure Table storage. When the storage account denies the
            // connection string's credentials - accounts hardened with allowSharedKeyAccess = false (increasingly the
            // default under enterprise governance policy) answer "403 KeyBasedAuthenticationNotPermitted"; a rotated key
            // answers "403 AuthenticationFailed" - the runtime retries with RBAC via ClientSecretCredential.
            // "Storage Blob Data Contributor" above does NOT cover the Table service, so without this role that retry
            // fails too: the user import is deferred every cycle and the checkpoint degrades to non-durable in-memory.
            _storageTableDataContributorRoleTask = new RoleAssignmentTask(
                RuntimeRoleConfig(config, "Storage Table Data Contributor", StorageAccountScope(config)), logger, Location, tagDic);
            this.AddTask(_storageTableDataContributorRoleTask);

            // Assign "Cognitive Services User" to the runtime account for data-plane access
            // (sentiment/language/key-phrase calls). Required when the Azure AI Language
            // resource has key auth disabled (disableLocalAuth=true) - the runtime falls back
            // to RBAC via ClientSecretCredential and would otherwise hit
            // "401 PermissionDenied: Principal does not have access to API/Operation".
            // Only assigned when cognitive services are part of the install, and only on the Language resource.
            if (config.CognitiveServicesEnabled)
            {
                _cognitiveServicesUserRoleTask = new RoleAssignmentTask(
                    RuntimeRoleConfig(config, "Cognitive Services User", CognitiveAccountScope(config.CognitiveServiceName)), logger, Location, tagDic);
                this.AddTask(_cognitiveServicesUserRoleTask);
            }

            // The runtime calls Azure OpenAI with its client-secret credential when no key is configured.
            // This built-in role grants inference access without granting resource management permissions.
            if (config.FoundryPromptEnabled)
            {
                _foundryOpenAIUserRoleTask = new RoleAssignmentTask(
                    RuntimeRoleConfig(config, FOUNDRY_OPENAI_USER_ROLE_NAME, CognitiveAccountScope(config.FoundryPromptResourceName)), logger, Location, tagDic);
                this.AddTask(_foundryOpenAIUserRoleTask);
            }

            // Assign "Azure Service Bus Data Owner" to the runtime account for data-plane access now that Service Bus
            // authenticates with RBAC instead of a SAS key (issue #138). Only on the calls queue: the webhook sends to it,
            // the importer receives from it, and the portal's Health page reads its message counts
            // (ServiceBusAdministrationClient.GetQueueRuntimePropertiesAsync). Reading a queue's description needs the
            // Manage right, which only Data Owner carries - Data Sender + Data Receiver would break that check.
            if (config.ServiceBusEnabled)
            {
                _serviceBusDataOwnerRoleTask = new RoleAssignmentTask(
                    RuntimeRoleConfig(config, "Azure Service Bus Data Owner", CallsQueueScope(config)), logger, Location, tagDic);
                this.AddTask(_serviceBusDataOwnerRoleTask);
            }
        }

        /// <summary>The solution storage account, relative to the resource group.</summary>
        public static string StorageAccountScope(SolutionInstallConfig config) =>
            "providers/Microsoft.Storage/storageAccounts/" + config.StorageAccountName;

        /// <summary>An Azure AI (Language or OpenAI) account, relative to the resource group.</summary>
        public static string CognitiveAccountScope(string accountName) =>
            "providers/Microsoft.CognitiveServices/accounts/" + accountName;

        /// <summary>The Teams calls queue, relative to the resource group.</summary>
        public static string CallsQueueScope(SolutionInstallConfig config) =>
            "providers/Microsoft.ServiceBus/namespaces/" + config.ServiceBusName + "/queues/" + AzurePaaSInstallJob.CALLS_QUEUE_NAME;

        /// <summary>
        /// The role-assignment settings for the runtime account. <paramref name="scopeSuffix"/> null assigns on the
        /// resource group.
        /// </summary>
        public static TaskConfig RuntimeRoleConfig(SolutionInstallConfig config, string roleName, string scopeSuffix)
        {
            var roleConfig = TaskConfig.GetConfigForPropAndVal(RoleAssignmentTask.CONFIG_KEY_ROLE_NAME, roleName)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_CLIENT_ID, config.RuntimeAccountOffice365.ClientId)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_CLIENT_SECRET, config.RuntimeAccountOffice365.Secret)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_TENANT_ID, config.RuntimeAccountOffice365.DirectoryId)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_PRINCIPAL_TYPE, "ServicePrincipal");
            if (scopeSuffix != null)
                roleConfig.AddSetting(RoleAssignmentTask.CONFIG_KEY_SCOPE_SUFFIX, scopeSuffix);
            return roleConfig;
        }

        /// <summary>Every role task this job runs, for tests.</summary>
        internal IReadOnlyList<RoleAssignmentTask> RoleTasks => new List<RoleAssignmentTask>
        {
            _appInsightsReaderRoleTask, _storageBlobDataContributorRoleTask, _storageTableDataContributorRoleTask,
            _cognitiveServicesUserRoleTask, _foundryOpenAIUserRoleTask, _serviceBusDataOwnerRoleTask
        }.FindAll(t => t != null);

        public RoleAssignmentResource AppInsightsReaderRole => GetTaskResult<RoleAssignmentResource>(_appInsightsReaderRoleTask);
        public RoleAssignmentResource StorageBlobDataContributorRole => GetTaskResult<RoleAssignmentResource>(_storageBlobDataContributorRoleTask);
        public RoleAssignmentResource StorageTableDataContributorRole => GetTaskResult<RoleAssignmentResource>(_storageTableDataContributorRoleTask);
        public RoleAssignmentResource CognitiveServicesUserRole =>
            _cognitiveServicesUserRoleTask == null ? null : GetTaskResult<RoleAssignmentResource>(_cognitiveServicesUserRoleTask);
        public RoleAssignmentResource FoundryOpenAIUserRole =>
            _foundryOpenAIUserRoleTask == null ? null : GetTaskResult<RoleAssignmentResource>(_foundryOpenAIUserRoleTask);
        public RoleAssignmentResource ServiceBusDataOwnerRole =>
            _serviceBusDataOwnerRoleTask == null ? null : GetTaskResult<RoleAssignmentResource>(_serviceBusDataOwnerRoleTask);
    }
}
