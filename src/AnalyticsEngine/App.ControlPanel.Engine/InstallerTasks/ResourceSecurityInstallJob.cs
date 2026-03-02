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
        }

        public RoleAssignmentResource AppInsightsReaderRole => GetTaskResult<RoleAssignmentResource>(_appInsightsReaderRoleTask);
    }
}
