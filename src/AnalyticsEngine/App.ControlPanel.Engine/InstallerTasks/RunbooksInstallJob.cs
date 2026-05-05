using App.ControlPanel.Engine.InstallerTasks.JobTasks;
using Azure.Identity;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Install job for the Runbooks solution.
    /// </summary>
    public class RunbooksInstallJob : InstallJobInContainerJob<ResourceGroupResource>
    {
        public RunbooksInstallJob(ILogger logger, SolutionInstallConfig config, SubscriptionResource subscription, StorageAccountResource storageAccount,
            AutomationAccountResource automationAccount)
            : base(logger, new ResourceGroupContainerLoader(TaskConfig.GetConfigForName(config.ResourceGroupName), logger, subscription, config.AzureLocation, config.Tags.ToDictionary()))
        {
            // Upload automation PS files to storage account using RBAC
            var runtimeAccount = config.RuntimeAccountOffice365;
            var credential = new ClientSecretCredential(runtimeAccount.DirectoryId, runtimeAccount.ClientId, runtimeAccount.Secret);
            var storageAccountUrl = $"https://{storageAccount.Data.Name}.blob.core.windows.net";
            var storageUploadConfig = TaskConfig.GetConfigForPropAndVal(ProfilingScriptsUploadToBlobStorageTask.CFG_STORAGE_ACCOUNT_URL, storageAccountUrl)
                .AddSetting(ProfilingScriptsUploadToBlobStorageTask.CFG_STORAGE_NAME, storageAccount.Data.Name);

            var tagsDic = config.Tags.ToDictionary();
            var automationPowerShellScriptsUploader = new ProfilingScriptsUploadToBlobStorageTask(storageUploadConfig, logger, config.AzureLocation, tagsDic, credential, storageAccount);

            // Publish the runbooks
            var commonConfig = TaskConfig.GetConfigForPropAndVal(RunbookUploadTask<RunbookFileLocalLocations>.CONFIG_PARAM_AUTOMATION_ACCOUNT_NAME,
                automationAccount.Data.Name);

            var runbookCreateOrUpdateAggregationStatusPS = new ProfilingScriptAggregationStatusPSRunbookUploadTask(commonConfig.Clone(), logger, config.AzureLocation, tagsDic);
            var runbookCreateOrUpdateDatabaseMaintenancePS = new ProfilingScriptDatabaseMaintenancePSRunbookUploadTask(commonConfig.Clone(), logger, config.AzureLocation, tagsDic);
            var runbookCreateOrUpdateWeeklyPS = new ProfilingScriptWeeklyPSRunbookUploadTask(commonConfig.Clone(), logger, config.AzureLocation, tagsDic);

            // ProfilingAutomationUploaderTask needs a LocalStorageInstallSourceInfo object
            AddTask(new PassResultOnlyTask(logger));
            AddTasks(new List<BaseInstallTask>() { automationPowerShellScriptsUploader, runbookCreateOrUpdateAggregationStatusPS, runbookCreateOrUpdateDatabaseMaintenancePS, runbookCreateOrUpdateWeeklyPS });
        }
    }
}
