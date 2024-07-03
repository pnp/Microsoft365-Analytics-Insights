using App.ControlPanel.Engine.Entities;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Automation.Models;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.JobTasks
{
    // Specialist runbook upload tasks for the profiling automation scripts
    public class ProfilingScriptWeeklyPSRunbookUploadTask : RunbookUploadTask<AzStorageRunbookFileLocations>
    {
        public ProfilingScriptWeeklyPSRunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override Task<AzStorageRunbookFileLocations> ExecuteTaskReturnResult(object contextArg)
        {
            // Get right script from the context passed from previous task. Set the config for the script to be uploaded
            var context = base.EnsureContextArgType<AzStorageRunbookFileLocations>(contextArg);

            // Name and script location
            _config.Add(TaskConfig.GetConfigForName("Weekly_Update"));
            _config.Add(CONFIG_PARAM_FILE_LOCATION, context.WeeklyPS);
            _config.Add(CONFIG_PARAM_FILE_HASH_SHA256, context.WeeklyFileHash);
            return base.ExecuteTaskReturnResult(contextArg);
        }
    }
    public class ProfilingScriptAggregationStatusPSRunbookUploadTask : RunbookUploadTask<AzStorageRunbookFileLocations>
    {
        public ProfilingScriptAggregationStatusPSRunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override Task<AzStorageRunbookFileLocations> ExecuteTaskReturnResult(object contextArg)
        {
            // Get right script from the context passed from previous task. Set the config for the script to be uploaded
            var context = base.EnsureContextArgType<AzStorageRunbookFileLocations>(contextArg);

            // Name and script location
            _config.Add(TaskConfig.GetConfigForName("Aggregation_Status"));
            _config.Add(CONFIG_PARAM_FILE_LOCATION, context.AggregationStatusPS);
            _config.Add(CONFIG_PARAM_FILE_HASH_SHA256, context.AggregationStatusFileHash);
            return base.ExecuteTaskReturnResult(contextArg);
        }
    }
    public class ProfilingScriptDatabaseMaintenancePSRunbookUploadTask : RunbookUploadTask<AzStorageRunbookFileLocations>
    {
        public ProfilingScriptDatabaseMaintenancePSRunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override Task<AzStorageRunbookFileLocations> ExecuteTaskReturnResult(object contextArg)
        {
            // Get right script from the context passed from previous task. Set the config for the script to be uploaded
            var context = base.EnsureContextArgType<AzStorageRunbookFileLocations>(contextArg);

            // Name and script location
            _config.Add(TaskConfig.GetConfigForName("Database_Maintenance"));
            _config.Add(CONFIG_PARAM_FILE_LOCATION, context.DatabaseMaintenancePS);
            _config.Add(CONFIG_PARAM_FILE_HASH_SHA256, context.DatabaseMaintenanceFileHash);
            return base.ExecuteTaskReturnResult(contextArg);
        }
    }

    /// <summary>
    /// Run a task to upload a runbook to an automation account. Return same result as previous task gave so can be used for various uploads using same metadata object. 
    /// </summary>
    public abstract class RunbookUploadTask<METADATA> : InstallTaskInAzResourceGroup<METADATA>
    {
        public const string CONFIG_PARAM_FILE_LOCATION = "FileName";
        public const string CONFIG_PARAM_FILE_HASH_SHA256 = "SHA256";
        public const string CONFIG_PARAM_AUTOMATION_ACCOUNT_NAME = "AutomationAccount";
        public RunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override async Task<METADATA> ExecuteTaskReturnResult(object contextArg)
        {
            var context = base.EnsureContextArgType<METADATA>(contextArg);
            var automationAccount = Container.GetAutomationAccounts().Where(s => s.Data.Name == _config[CONFIG_PARAM_AUTOMATION_ACCOUNT_NAME]).SingleOrDefault();
            if (automationAccount == null)
            {
                throw new UnexpectedInstallException($"Automation account '{_config[CONFIG_PARAM_AUTOMATION_ACCOUNT_NAME]}' not found.");
            }
            else
            {
                var fileUrl = _config[CONFIG_PARAM_FILE_LOCATION];

                Console.WriteLine($"Uploading runbook with hash '{_config[CONFIG_PARAM_FILE_HASH_SHA256]}' from '{fileUrl}'.");

                var retry = true;
                var retryCount = 0;
                while (retry)
                {
                    var newRunbookInfo = new AutomationRunbookCreateOrUpdateContent(new AutomationRunbookType("PowerShell72"))
                    {
                        Location = base.AzureLocation,
                        Name = _config.ResourceName,
                        PublishContentLink = new AutomationContentLink
                        {
                            Uri = new Uri(fileUrl),
                            ContentHash = new AutomationContentHash("SHA256", _config[CONFIG_PARAM_FILE_HASH_SHA256]),
                        },
                        Description = "Profiling automation script",
                    };
                    base.EnsureTagsOnNew(newRunbookInfo.Tags);

                    try
                    {
                        var newRunbookReq = await automationAccount.GetAutomationRunbooks().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newRunbookInfo);
                        var runbook = newRunbookReq.Value;
                        _logger.LogInformation($"Created/updated runbook '{runbook.Data.Name}' successfully");
                        retry = false;
                        await base.EnsureTagsOnExisting(runbook.Data.Tags, runbook.GetTagResource());
                    }
                    catch (RequestFailedException ex)
                    {
                        if (ex.Message.Contains("Validation errors while reading content link."))
                        {
                            // Retry if the content link is not valid. 
                            // This seems to happen with URLs that have SAS tokens in. Retrying works. It's not clear why.
                            retryCount++;
                            retry = retryCount < 5;
                            await Task.Delay(5000);
                        }
                        _logger.LogError($"Attempt {1}: Failed to create/update runbook '{_config.ResourceName}' - {ex.Message}");
                    }
                }
            }
            return context;
        }
    }
}
