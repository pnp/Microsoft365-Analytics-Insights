using App.ControlPanel.Engine.InstallerTasks.JobTasks;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Resources;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Install job for the Runbooks solution.
    ///
    /// Locates the profiling PowerShell scripts on disk (from the extracted webjob zip) and uploads each one into the
    /// Automation account via the runbook draft-content API. No blob storage hop, no SAS URL, no PublishContentLink -
    /// see <see cref="JobTasks.RunbookUploadTask"/> for why we avoid the content-link path.
    /// </summary>
    public class RunbooksInstallJob : InstallJobInContainerJob<ResourceGroupResource>
    {
        public RunbooksInstallJob(ILogger logger, SolutionInstallConfig config, SubscriptionResource subscription,
            AutomationAccountResource automationAccount)
            : base(logger, new ResourceGroupContainerLoader(TaskConfig.GetConfigForName(config.ResourceGroupName), logger, subscription, config.AzureLocation, config.Tags.ToDictionary()))
        {
            var tagsDic = config.Tags.ToDictionary();

            // Locate the profiling PS files in the unzipped webjob package on the installer host.
            var profilingScriptsLocate = new ProfilingScriptsLocateTask(TaskConfig.NoConfig, logger, config.AzureLocation, tagsDic);

            // Publish the runbooks straight into the Automation account via the draft content API.
            var commonConfig = TaskConfig.GetConfigForPropAndVal(RunbookUploadTask.CONFIG_PARAM_AUTOMATION_ACCOUNT_NAME, automationAccount.Data.Name);

            var runbookCreateOrUpdateAggregationStatusPS = new ProfilingScriptAggregationStatusPSRunbookUploadTask(commonConfig.Clone(), logger, config.AzureLocation, tagsDic);
            var runbookCreateOrUpdateDatabaseMaintenancePS = new ProfilingScriptDatabaseMaintenancePSRunbookUploadTask(commonConfig.Clone(), logger, config.AzureLocation, tagsDic);
            var runbookCreateOrUpdateWeeklyPS = new ProfilingScriptWeeklyPSRunbookUploadTask(commonConfig.Clone(), logger, config.AzureLocation, tagsDic);

            // First task forwards the LocalStorageInstallSourceInfo passed in from the parent job into the locate task.
            AddTask(new PassResultOnlyTask(logger));
            AddTasks(new List<BaseInstallTask>() { profilingScriptsLocate, runbookCreateOrUpdateAggregationStatusPS, runbookCreateOrUpdateDatabaseMaintenancePS, runbookCreateOrUpdateWeeklyPS });
        }
    }
}
