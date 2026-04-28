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

namespace App.ControlPanel.Engine.InstallerTasks.Tasks
{
    /// <summary>
    /// Creates a Hybrid Runbook Worker Group on an automation account and registers a VM as a hybrid worker.
    /// This allows automation runbooks to run on the VM inside the VNet, enabling access to private-endpoint resources.
    /// The VM must have the Hybrid Worker extension (Microsoft.Azure.Automation.HybridWorker) installed.
    /// </summary>
    public class HybridWorkerGroupTask : InstallTaskInAzResourceGroup<HybridRunbookWorkerGroupResource>
    {
        public const string CONFIG_KEY_AUTOMATION_ACCOUNT_NAME = "AutomationAccountName";
        public const string CONFIG_KEY_VM_RESOURCE_ID = "VmResourceId";

        public HybridWorkerGroupTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags)
            : base(config, logger, azureLocation, tags)
        {
        }

        public override string TaskName => "get/create hybrid worker group";

        public override async Task<HybridRunbookWorkerGroupResource> ExecuteTaskReturnResult(object contextArg)
        {
            var groupName = _config.ResourceName;
            var automationAccountName = _config[CONFIG_KEY_AUTOMATION_ACCOUNT_NAME];
            var vmResourceId = _config[CONFIG_KEY_VM_RESOURCE_ID];

            // Find automation account
            var automationAccount = Container.GetAutomationAccounts().Where(s => s.Data.Name == automationAccountName).SingleOrDefault();
            if (automationAccount == null)
            {
                _logger.LogError($"Automation account '{automationAccountName}' not found. Cannot create hybrid worker group.");
                return null;
            }

            // Get or create hybrid worker group
            HybridRunbookWorkerGroupResource group = null;
            try
            {
                var response = await automationAccount.GetHybridRunbookWorkerGroupAsync(groupName);
                group = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found
            }

            if (group == null)
            {
                _logger.LogInformation($"Creating hybrid worker group '{groupName}' on automation account '{automationAccountName}'...");
                var createContent = new HybridRunbookWorkerGroupCreateOrUpdateContent()
                {
                    Name = groupName
                };
                try
                {
                    var operation = await automationAccount.GetHybridRunbookWorkerGroups().CreateOrUpdateAsync(WaitUntil.Completed, groupName, createContent);
                    group = operation.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 201)
                {
                    // Azure SDK bug: 201 Created is a valid success response but the SDK throws.
                    // Retrieve the newly created group instead.
                    var response = await automationAccount.GetHybridRunbookWorkerGroupAsync(groupName);
                    group = response.Value;
                }
                _logger.LogInformation($"Created hybrid worker group '{group.Data.Name}'.");
            }
            else
            {
                _logger.LogInformation($"Found existing hybrid worker group '{group.Data.Name}'.");
            }

            // Register VM as hybrid worker in the group (if not already registered)
            var existingWorkers = group.GetHybridRunbookWorkers();
            bool vmAlreadyRegistered = false;
            foreach (var worker in existingWorkers)
            {
                if (worker.Data.VmResourceId != null &&
                    string.Equals(worker.Data.VmResourceId.ToString(), vmResourceId, StringComparison.OrdinalIgnoreCase))
                {
                    vmAlreadyRegistered = true;
                    _logger.LogInformation($"VM '{vmResourceId}' is already registered as hybrid worker '{worker.Data.Name}' in group '{groupName}'.");
                    break;
                }
            }

            if (!vmAlreadyRegistered)
            {
                var workerName = Guid.NewGuid().ToString();
                _logger.LogInformation($"Registering VM '{vmResourceId}' as hybrid worker in group '{groupName}'...");
                var workerContent = new HybridRunbookWorkerCreateOrUpdateContent()
                {
                    VmResourceId = new ResourceIdentifier(vmResourceId),
                    Name = workerName
                };

                try
                {
                    await group.GetHybridRunbookWorkers().CreateOrUpdateAsync(WaitUntil.Completed, workerName, workerContent);
                    _logger.LogInformation($"Registered VM as hybrid worker '{workerName}' in group '{groupName}'.");
                }
                catch (RequestFailedException ex) when (ex.Status == 201)
                {
                    // 201 Created is a success response — some SDK versions incorrectly throw on this status
                    _logger.LogInformation($"Registered VM as hybrid worker '{workerName}' in group '{groupName}'.");
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogError($"Failed to register VM as hybrid worker: {ex.Message}");
                    _logger.LogInformation($"Ensure the Hybrid Worker extension (Microsoft.Azure.Automation.HybridWorker) is installed on the VM. " +
                        $"Install it with: az vm extension set --resource-group <vm-rg> --vm-name <vm-name> " +
                        $"--name HybridWorkerForWindows --publisher Microsoft.Azure.Automation.HybridWorker --version 1.1 " +
                        $"--settings '{{\"AutomationAccountURL\": \"<automation-account-url>\"}}'");
                }
            }

            return group;
        }
    }
}
