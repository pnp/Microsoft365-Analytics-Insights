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
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.JobTasks
{
    // Specialist runbook upload tasks for the profiling automation scripts
    public class ProfilingScriptWeeklyPSRunbookUploadTask : RunbookUploadTask
    {
        public ProfilingScriptWeeklyPSRunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override Task<RunbookFileLocalLocations> ExecuteTaskReturnResult(object contextArg)
        {
            // Get right script from the context passed from previous task. Set the config for the script to be uploaded
            var context = base.EnsureContextArgType<RunbookFileLocalLocations>(contextArg);

            // Name and script location
            _config.Add(TaskConfig.GetConfigForName("Weekly_Update"));
            _config.Add(CONFIG_PARAM_FILE_LOCATION, context.WeeklyPS);
            return base.ExecuteTaskReturnResult(contextArg);
        }
    }

    public class ProfilingScriptAggregationStatusPSRunbookUploadTask : RunbookUploadTask
    {
        public ProfilingScriptAggregationStatusPSRunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override Task<RunbookFileLocalLocations> ExecuteTaskReturnResult(object contextArg)
        {
            // Get right script from the context passed from previous task. Set the config for the script to be uploaded
            var context = base.EnsureContextArgType<RunbookFileLocalLocations>(contextArg);

            // Name and script location
            _config.Add(TaskConfig.GetConfigForName("Aggregation_Status"));
            _config.Add(CONFIG_PARAM_FILE_LOCATION, context.AggregationStatusPS);
            return base.ExecuteTaskReturnResult(contextArg);
        }
    }

    public class ProfilingScriptDatabaseMaintenancePSRunbookUploadTask : RunbookUploadTask
    {
        public ProfilingScriptDatabaseMaintenancePSRunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override Task<RunbookFileLocalLocations> ExecuteTaskReturnResult(object contextArg)
        {
            // Get right script from the context passed from previous task. Set the config for the script to be uploaded
            var context = base.EnsureContextArgType<RunbookFileLocalLocations>(contextArg);

            // Name and script location
            _config.Add(TaskConfig.GetConfigForName("Database_Maintenance"));
            _config.Add(CONFIG_PARAM_FILE_LOCATION, context.DatabaseMaintenancePS);
            return base.ExecuteTaskReturnResult(contextArg);
        }
    }

    /// <summary>
    /// Creates (or updates) a runbook in an Azure Automation account using the runbook draft content API:
    ///   1. PUT runbook metadata with an empty draft (no PublishContentLink).
    ///   2. PUT the script body into the draft via <see cref="AutomationRunbookResource.ReplaceContentRunbookDraftAsync"/>.
    ///   3. POST publish so the draft becomes the live version.
    ///
    /// This deliberately avoids <see cref="AutomationContentLink"/> because the Automation control plane fetches the
    /// URL from outside the customer VNet and cannot route through storage private endpoints - which makes content
    /// links fail with "Validation errors while reading content link" whenever the storage account has public network
    /// access disabled. The draft-content path is pure ARM control plane and works regardless of storage network ACLs.
    /// </summary>
    public abstract class RunbookUploadTask : InstallTaskInAzResourceGroup<RunbookFileLocalLocations>
    {
        public const string CONFIG_PARAM_FILE_LOCATION = "FileName";
        public const string CONFIG_PARAM_AUTOMATION_ACCOUNT_NAME = "AutomationAccount";

        const int MAX_ATTEMPTS = 3;
        static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5);

        public RunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override async Task<RunbookFileLocalLocations> ExecuteTaskReturnResult(object contextArg)
        {
            var context = base.EnsureContextArgType<RunbookFileLocalLocations>(contextArg);

            var automationAccount = Container.GetAutomationAccounts().SingleOrDefault(s => s.Data.Name == _config[CONFIG_PARAM_AUTOMATION_ACCOUNT_NAME]);
            if (automationAccount == null)
            {
                throw new UnexpectedInstallException($"Automation account '{_config[CONFIG_PARAM_AUTOMATION_ACCOUNT_NAME]}' not found.");
            }

            var localPath = _config[CONFIG_PARAM_FILE_LOCATION];
            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {
                throw new UnexpectedInstallException($"Runbook source PowerShell file '{localPath}' not found for runbook '{_config.ResourceName}'.");
            }

            _logger.LogInformation($"Uploading runbook '{_config.ResourceName}' from '{localPath}' via Automation draft-content API.");

            for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
            {
                try
                {
                    // 1. Create/update the runbook metadata with an empty draft (no PublishContentLink).
                    //    Setting Draft = new AutomationRunbookDraft() tells the API to allocate a fresh empty draft
                    //    that we'll then PUT bytes into via the next call.
                    var newRunbookInfo = new AutomationRunbookCreateOrUpdateContent(new AutomationRunbookType("PowerShell72"))
                    {
                        Location = base.AzureLocation,
                        Name = _config.ResourceName,
                        Draft = new AutomationRunbookDraft(),
                        Description = "Profiling automation script",
                    };
                    base.EnsureTagsOnNew(newRunbookInfo.Tags);

                    var createReq = await automationAccount.GetAutomationRunbooks().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newRunbookInfo);
                    var runbook = createReq.Value;

                    // 2. Upload the script body into the draft.
                    using (var fileStream = File.OpenRead(localPath))
                    {
                        await runbook.ReplaceContentRunbookDraftAsync(WaitUntil.Completed, fileStream);
                    }

                    // 3. Publish the draft so it becomes the live version that Hybrid Runbook Workers can invoke.
                    await runbook.PublishAsync(WaitUntil.Completed);

                    _logger.LogInformation($"Created/updated runbook '{runbook.Data.Name}' successfully");
                    await base.EnsureTagsOnExisting(runbook.Data.Tags, runbook.GetTagResource());
                    return context;
                }
                catch (RequestFailedException ex) when (attempt < MAX_ATTEMPTS)
                {
                    _logger.LogWarning($"Attempt {attempt}/{MAX_ATTEMPTS}: Failed to create/update runbook '{_config.ResourceName}' - {ex.Message}. Retrying in {RETRY_DELAY.TotalSeconds:0}s...");
                    await Task.Delay(RETRY_DELAY);
                }
            }

            // All attempts exhausted - fail loudly so the install doesn't silently leave the Automation account in a broken state.
            throw new UnexpectedInstallException($"Failed to create/update runbook '{_config.ResourceName}' after {MAX_ATTEMPTS} attempts.");
        }
    }
}
