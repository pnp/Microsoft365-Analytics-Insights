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

        // Runbook runtime kind we standardise on. Azure Automation forbids changing a runbook's kind in
        // place, so an existing runbook of a different kind is deleted and recreated as this type below.
        const string RUNBOOK_TYPE = "PowerShell72";

        const int MAX_ATTEMPTS = 3;
        static readonly TimeSpan RETRY_DELAY = TimeSpan.FromSeconds(5);

        public RunbookUploadTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        // Deploying the profiling runbooks is a secondary feature: a failure here must never abort the
        // rest of the install. The job loop logs the failure and continues (SequentialTaskListInstallJob).
        public override bool IsCritical => false;

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

            var runbooks = automationAccount.GetAutomationRunbooks();

            for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
            {
                try
                {
                    // Azure Automation forbids changing a runbook's kind in place ("Update runbook with
                    // definition of different runbook kind is not allowed"). If an older install left this
                    // runbook as a different kind, delete it first so it can be recreated as RUNBOOK_TYPE.
                    await DeleteIfDifferentKindAsync(runbooks);

                    // 1. Create/update the runbook metadata with an empty draft (no PublishContentLink).
                    //    Setting Draft = new AutomationRunbookDraft() tells the API to allocate a fresh empty draft
                    //    that we'll then PUT bytes into via the next call.
                    var newRunbookInfo = new AutomationRunbookCreateOrUpdateContent(new AutomationRunbookType(RUNBOOK_TYPE))
                    {
                        Location = base.AzureLocation,
                        Name = _config.ResourceName,
                        Draft = new AutomationRunbookDraft(),
                        Description = "Profiling automation script",
                    };
                    base.EnsureTagsOnNew(newRunbookInfo.Tags);

                    var createReq = await runbooks.CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newRunbookInfo);
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
                catch (RequestFailedException ex) when (IsDifferentKindError(ex) && attempt < MAX_ATTEMPTS)
                {
                    // Safety net for a race the proactive check above missed: the existing runbook is a
                    // different kind. Delete it and let the loop retry with a clean create.
                    _logger.LogWarning($"Attempt {attempt}/{MAX_ATTEMPTS}: runbook '{_config.ResourceName}' exists with a different kind; deleting so it can be recreated as {RUNBOOK_TYPE}.");
                    await TryDeleteRunbookAsync(runbooks);
                    await Task.Delay(RETRY_DELAY);
                }
                catch (RequestFailedException ex) when (attempt < MAX_ATTEMPTS)
                {
                    _logger.LogWarning($"Attempt {attempt}/{MAX_ATTEMPTS}: Failed to create/update runbook '{_config.ResourceName}' - {ex.Message}. Retrying in {RETRY_DELAY.TotalSeconds:0}s...");
                    await Task.Delay(RETRY_DELAY);
                }
            }

            // All attempts exhausted. This task is non-critical (see IsCritical), so the job loop logs this
            // and continues the install rather than aborting.
            throw new UnexpectedInstallException($"Failed to create/update runbook '{_config.ResourceName}' after {MAX_ATTEMPTS} attempts.");
        }

        static bool IsDifferentKindError(RequestFailedException ex)
            => ex.Status == 400 && ex.Message.IndexOf("different runbook kind", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Azure Automation forbids changing a runbook's kind in place. If a runbook with this name already
        /// exists with a kind other than <c>RUNBOOK_TYPE</c>, delete it so it can be recreated. Deleting a
        /// runbook also removes its job history and any manually-created schedule-to-runbook links, so the
        /// operator must re-link the recreated runbook to its schedule afterwards.
        /// </summary>
        async Task DeleteIfDifferentKindAsync(AutomationRunbookCollection runbooks)
        {
            var existing = await runbooks.GetIfExistsAsync(_config.ResourceName);
            if (!existing.HasValue)
            {
                return;
            }

            var existingKind = existing.Value.Data.RunbookType?.ToString();
            if (string.Equals(existingKind, RUNBOOK_TYPE, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _logger.LogWarning($"Runbook '{_config.ResourceName}' already exists as kind '{existingKind}', which can't be updated in place to {RUNBOOK_TYPE}. Deleting and recreating it. NOTE: its job history and any manually-created schedule-to-runbook links will be lost - re-link the runbook to its schedule after the install completes.");
            await existing.Value.DeleteAsync(WaitUntil.Completed);
        }

        async Task TryDeleteRunbookAsync(AutomationRunbookCollection runbooks)
        {
            try
            {
                var existing = await runbooks.GetIfExistsAsync(_config.ResourceName);
                if (existing.HasValue)
                {
                    await existing.Value.DeleteAsync(WaitUntil.Completed);
                }
            }
            catch (RequestFailedException delEx)
            {
                _logger.LogWarning($"Could not delete existing runbook '{_config.ResourceName}' before retry: {delEx.Message}");
            }
        }
    }
}
