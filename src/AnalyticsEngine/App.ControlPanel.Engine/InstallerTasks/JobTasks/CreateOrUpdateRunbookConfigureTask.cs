using Azure;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Automation.Models;
using CloudInstallEngine;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Tasks
{
    /// <summary>
    /// Create or update automation runbook in an existing automation account
    /// </summary>
    public class CreateOrUpdateRunbookConfigureTask : ResourceInstallTask<AutomationAccountResource>
    {
        const string CONFIG_PARAM_NAME_SCRIPT_URL = "scriptUrlWeekly";

        public CreateOrUpdateRunbookConfigureTask(TaskConfig config, ILogger logger) : base(config, logger)
        {
        }

        public override async Task<AutomationAccountResource> ExecuteTaskReturnResult(object contextArg)
        {
            var account = base.EnsureContextArgType<AutomationAccountResource>(contextArg);

            _logger.LogInformation($"Creating/updating automation runbooks '{_config.ResourceName}'...");

            var runbooks = account.GetAutomationRunbooks();
            await runbooks.CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, new AutomationRunbookCreateOrUpdateContent(AutomationRunbookType.PowerShellWorkflow)
            {
                PublishContentLink = new AutomationContentLink() { Uri = new Uri(_config[CONFIG_PARAM_NAME_SCRIPT_URL]) }
            });

            return account;
        }
    }
}
