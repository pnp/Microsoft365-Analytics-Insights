using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Resources;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Secures resources in the resource group by assigning RBAC roles.
    /// </summary>
    public class ResourceSecurityInstallJob : BaseAnalyticsSolutionInstallJob
    {
        private readonly RoleAssignmentTask _appInsightsReaderRoleTask;
        private readonly RoleAssignmentTask _storageBlobDataContributorRoleTask;
        private readonly RoleAssignmentTask _redisCacheContributorRoleTask;

        public ResourceSecurityInstallJob(ILogger logger, SolutionInstallConfig config, SubscriptionResource subscription) : base(logger, config, subscription)
        {
            var tagDic = config.Tags.ToDictionary();

            // Assign Reader role to the runtime account on the resource group (covers App Insights and all resources)
            var readerRoleConfig = TaskConfig.GetConfigForPropAndVal(RoleAssignmentTask.CONFIG_KEY_ROLE_NAME, "Reader")
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_CLIENT_ID, config.RuntimeAccountOffice365.ClientId)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_CLIENT_SECRET, config.RuntimeAccountOffice365.Secret)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_TENANT_ID, config.RuntimeAccountOffice365.DirectoryId)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_PRINCIPAL_TYPE, "ServicePrincipal");

            _appInsightsReaderRoleTask = new RoleAssignmentTask(readerRoleConfig, logger, Location, tagDic);
            this.AddTask(_appInsightsReaderRoleTask);

            // Assign Storage Blob Data Contributor role to the runtime account so it can create containers and upload blobs
            var storageBlobContributorConfig = TaskConfig.GetConfigForPropAndVal(RoleAssignmentTask.CONFIG_KEY_ROLE_NAME, "Storage Blob Data Contributor")
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_CLIENT_ID, config.RuntimeAccountOffice365.ClientId)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_CLIENT_SECRET, config.RuntimeAccountOffice365.Secret)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_TENANT_ID, config.RuntimeAccountOffice365.DirectoryId)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_PRINCIPAL_TYPE, "ServicePrincipal");

            _storageBlobDataContributorRoleTask = new RoleAssignmentTask(storageBlobContributorConfig, logger, Location, tagDic);
            this.AddTask(_storageBlobDataContributorRoleTask);

            // Assign Redis Cache Contributor role to the runtime account for RBAC-based Redis access (when keys are disabled)
            var redisCacheContributorConfig = TaskConfig.GetConfigForPropAndVal(RoleAssignmentTask.CONFIG_KEY_ROLE_NAME, "Redis Cache Contributor")
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_CLIENT_ID, config.RuntimeAccountOffice365.ClientId)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_CLIENT_SECRET, config.RuntimeAccountOffice365.Secret)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_TENANT_ID, config.RuntimeAccountOffice365.DirectoryId)
                .AddSetting(RoleAssignmentTask.CONFIG_KEY_PRINCIPAL_TYPE, "ServicePrincipal");

            _redisCacheContributorRoleTask = new RoleAssignmentTask(redisCacheContributorConfig, logger, Location, tagDic);
            this.AddTask(_redisCacheContributorRoleTask);
        }

        public RoleAssignmentResource AppInsightsReaderRole => GetTaskResult<RoleAssignmentResource>(_appInsightsReaderRoleTask);
        public RoleAssignmentResource StorageBlobDataContributorRole => GetTaskResult<RoleAssignmentResource>(_storageBlobDataContributorRoleTask);
        public RoleAssignmentResource RedisCacheContributorRole => GetTaskResult<RoleAssignmentResource>(_redisCacheContributorRoleTask);
    }
}
