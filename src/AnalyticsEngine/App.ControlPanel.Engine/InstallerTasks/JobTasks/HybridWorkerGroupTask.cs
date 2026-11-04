using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Automation.Models;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
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
    /// Automatically installs the Hybrid Worker extension on the VM if not already present.
    /// </summary>
    public class HybridWorkerGroupTask : InstallTaskInAzResourceGroup<HybridRunbookWorkerGroupResource>
    {
        public const string CONFIG_KEY_AUTOMATION_ACCOUNT_NAME = "AutomationAccountName";
        public const string CONFIG_KEY_VM_RESOURCE_ID = "VmResourceId";

        private readonly TokenCredential _credential;

        public HybridWorkerGroupTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, TokenCredential credential)
            : base(config, logger, azureLocation, tags)
        {
            _credential = credential;
        }

        public override string TaskName => "get/create hybrid worker group";

        public override async Task<HybridRunbookWorkerGroupResource> ExecuteTaskReturnResult(object contextArg)
        {
            var groupName = _config.ResourceName;
            var automationAccountName = _config[CONFIG_KEY_AUTOMATION_ACCOUNT_NAME];
            var vmResourceId = _config[CONFIG_KEY_VM_RESOURCE_ID];

            try
            {
                // Find automation account
                var automationAccount = Container.GetAutomationAccounts().GetAll(cancellationToken: CancellationToken).Where(s => s.Data.Name == automationAccountName).SingleOrDefault();
                if (automationAccount == null)
                {
                    _logger.LogError($"Automation account '{automationAccountName}' not found. Cannot create hybrid worker group.");
                    return null;
                }

                // Get or create hybrid worker group
                HybridRunbookWorkerGroupResource group = null;
                try
                {
                    var response = await automationAccount.GetHybridRunbookWorkerGroupAsync(groupName, cancellationToken: CancellationToken);
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
                        var operation = await automationAccount.GetHybridRunbookWorkerGroups().CreateOrUpdateAsync(WaitUntil.Completed, groupName, createContent, cancellationToken: CancellationToken);
                        group = operation.Value;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 201)
                    {
                        // Azure SDK bug: 201 Created is a valid success response but the SDK throws.
                        // Retrieve the newly created group instead.
                        var response = await automationAccount.GetHybridRunbookWorkerGroupAsync(groupName, cancellationToken: CancellationToken);
                        group = response.Value;
                    }
                    _logger.LogInformation($"Created hybrid worker group '{group.Data.Name}'.");
                }
                else
                {
                    _logger.LogInformation($"Found existing hybrid worker group '{group.Data.Name}'.");
                }

                // Ensure Hybrid Worker extension is installed on the VM with correct registration.
                // The extension requires the JRDS hybrid service URL (not the ARM resource ID).
                // The SDK's AutomationHybridServiceUri is often null, so fetch via REST API.
                var automationAccountUrl = await GetAutomationHybridServiceUrl(automationAccount);
                if (string.IsNullOrEmpty(automationAccountUrl))
                {
                    _logger.LogError($"Could not determine Automation Hybrid Service URL for account '{automationAccountName}'. " +
                        "The Hybrid Worker extension cannot be installed without a valid JRDS URL. " +
                        "Skipping extension installation and worker registration.");
                    return group;
                }

                // Register VM as hybrid worker in the group BEFORE installing the extension.
                // The extension contacts the automation account on startup and expects the VM
                // to already be associated. If we install the extension first, it fails with:
                // "Specified machineId is not associated with automation account"
                var existingWorkers = group.GetHybridRunbookWorkers();
                bool vmAlreadyRegistered = false;
                foreach (var worker in existingWorkers)
                {
                    if (worker.Data.VmResourceId != null &&
                        string.Equals(worker.Data.VmResourceId.ToString(), vmResourceId, StringComparison.OrdinalIgnoreCase))
                    {
                        vmAlreadyRegistered = true;
                        _logger.LogInformation($"VM '{ShortVmName(vmResourceId)}' is already registered as hybrid worker '{worker.Data.Name}' in group '{groupName}'.");
                        break;
                    }
                }

                if (!vmAlreadyRegistered)
                {
                    var workerName = Guid.NewGuid().ToString();
                    _logger.LogInformation($"Registering VM '{ShortVmName(vmResourceId)}' as hybrid worker in group '{groupName}'...");
                    var workerContent = new HybridRunbookWorkerCreateOrUpdateContent()
                    {
                        VmResourceId = new ResourceIdentifier(vmResourceId),
                        Name = workerName
                    };

                    try
                    {
                        await group.GetHybridRunbookWorkers().CreateOrUpdateAsync(WaitUntil.Completed, workerName, workerContent, cancellationToken: CancellationToken);
                        _logger.LogInformation($"Registered VM as hybrid worker '{workerName}' in group '{groupName}'.");
                    }
                    catch (RequestFailedException ex) when (ex.Status == 201)
                    {
                        // 201 Created is a success response — some SDK versions incorrectly throw on this status
                        _logger.LogInformation($"Registered VM as hybrid worker '{workerName}' in group '{groupName}'.");
                    }
                    catch (RequestFailedException ex)
                    {
                        _logger.LogError($"Failed to register VM as hybrid worker: {FirstLine(ex.Message)} (HTTP {ex.Status} {ex.ErrorCode}).");
                        return group;
                    }
                }

                // Now check the extension — the VM is already associated with the automation account.
                // EnsureHybridWorkerExtensionInstalled logs what it did and why, or its own specific error on
                // failure (VM not running, extension install rejected, etc.); we deliberately don't pile a generic
                // "Hybrid Worker extension was not installed successfully." line on top because it
                // would just duplicate the root-cause entry in the install summary.
                await EnsureHybridWorkerExtensionInstalled(vmResourceId, automationAccountUrl, groupName, vmAlreadyRegistered);

                return group;
            }
            catch (Exception ex) when (!CancellationToken.IsCancellationRequested)
            {
                _logger.LogError($"Hybrid worker group setup failed (non-fatal): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Makes sure the VM has a working Hybrid Worker extension, changing as little as possible. A healthy extension is
        /// left alone, so a re-run of the installer doesn't touch the VM; only a broken one is repaired by restarting the
        /// VM, and never when the installer may be running on that VM. <see cref="HybridWorkerExtensionPolicy"/> decides.
        /// </summary>
        /// <param name="workerWasRegistered">Whether the VM was already in the group before this run registered it.</param>
        internal async Task<bool> EnsureHybridWorkerExtensionInstalled(string vmResourceId, string automationAccountUrl, string groupName, bool workerWasRegistered)
        {
            const string EXTENSION_NAME = "HybridWorkerForWindows";

            if (string.IsNullOrEmpty(automationAccountUrl))
            {
                _logger.LogError("Automation account resource ID is empty. Cannot install Hybrid Worker extension with registration.");
                return false;
            }

            var client = CreateArmClient();
            var vmResource = client.GetVirtualMachineResource(new ResourceIdentifier(vmResourceId));

            try
            {
                vmResource = await vmResource.GetAsync(cancellationToken: CancellationToken);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError($"Cannot access VM '{vmResourceId}': {FirstLine(ex.Message)} (HTTP {ex.Status} {ex.ErrorCode}).");
                return false;
            }

            var vmShortName = ShortVmName(vmResourceId);
            VirtualMachineExtensionResource existingExtension;
            try
            {
                existingExtension = FindHybridWorkerExtension(vmResource);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError($"Couldn't read the extensions on Hybrid Worker VM '{vmShortName}': {FirstLine(ex.Message)} (HTTP {ex.Status} {ex.ErrorCode}). " +
                    "The VM has been left as it is.");
                return false;
            }

            var facts = new HybridWorkerVmFacts
            {
                VmName = vmShortName,
                GroupName = groupName,
                ExtensionFound = existingExtension != null,
                ExtensionProvisioningState = existingExtension?.Data.ProvisioningState?.ToString(),
                ExtensionAutomationAccountUrl = ReadAutomationAccountUrl(existingExtension?.Data.Settings),
                ExpectedAutomationAccountUrl = automationAccountUrl,
                WorkerWasRegistered = workerWasRegistered,
                InstallerHost = InstallerHostCheck.Unknown
            };
            var decision = HybridWorkerExtensionPolicy.Decide(facts);
            if (decision.Action == HybridWorkerExtensionAction.ManualRepair)
            {
                // Only a repair restarts the VM, so only then is it worth asking where the installer is running
                facts.InstallerHost = await DetectInstallerHostAsync(vmResourceId, vmResource.Data.VmId?.ToString());
                decision = HybridWorkerExtensionPolicy.Decide(facts);
            }
            _logger.Log(decision.LogLevel, decision.Reason);

            if (decision.Action == HybridWorkerExtensionAction.LeaveAlone || decision.Action == HybridWorkerExtensionAction.ManualRepair)
            {
                return decision.LogLevel == LogLevel.Information;
            }

            // Pre-flight: VM must be running before we attempt cleanup / restart / extension install.
            // Without this check, all three subsequent ARM calls fail with the same 409
            // OperationNotAllowed cascade, which dumps a lot of noise into the install log and
            // hides the real root cause. Fail fast with a single actionable line for terminal
            // power states (stopped/deallocated), but tolerate transient "starting" with a short
            // retry — the VM is probably just mid-boot from a previous step.
            string powerState = await ReadVmPowerStateAsync(vmResource, vmShortName);
            if (powerState == null)
            {
                return false;
            }

            if (string.Equals(powerState, "starting", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation($"Hybrid Worker VM '{vmShortName}' is in 'starting' state; waiting up to 60s for it to finish booting before installing the extension...");
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken: CancellationToken);
                    powerState = await ReadVmPowerStateAsync(vmResource, vmShortName);
                    if (powerState == null) return false;
                    if (!string.Equals(powerState, "starting", StringComparison.OrdinalIgnoreCase)) break;
                }
            }

            if (!string.Equals(powerState, "running", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError($"Hybrid Worker VM '{vmShortName}' is in '{powerState ?? "unknown"}' state. " +
                    "The Hybrid Worker extension can only be installed on a running VM — start the VM in the Azure portal (or 'az vm start') and re-run the installer. " +
                    "Skipping cleanup / restart / extension install for this run.");
                return false;
            }

            if (decision.Action == HybridWorkerExtensionAction.Reapply)
            {
                return await RerunExtension(existingExtension, automationAccountUrl, vmShortName);
            }

            var extensionName = EXTENSION_NAME;
            if (decision.Action == HybridWorkerExtensionAction.RepairWithRestart)
            {
                // A stale/partial extension (e.g. from a previous failed run) can leave the HybridWorkerPackage
                // directory without a working HybridWorkerService, causing "Cannot find any service" errors,
                // so remove it before installing it again. Keep its name and any other public settings.
                extensionName = existingExtension.Data.Name;
                try
                {
                    _logger.LogInformation($"Removing the broken Hybrid Worker extension from VM '{vmShortName}'...");
                    await existingExtension.DeleteAsync(WaitUntil.Completed, cancellationToken: CancellationToken);
                    _logger.LogInformation($"Existing extension removed.");
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Already gone
                }
            }

            await CleanUpWorkerLeftovers(vmResource);

            if (decision.Action == HybridWorkerExtensionAction.RepairWithRestart)
            {
                // Restart the VM to release any file locks held by leftover processes from previous
                // extension installs (e.g. Orchestrator ETW manifest DLLs).
                _logger.LogInformation($"Restarting Hybrid Worker VM '{vmShortName}' to release what the broken extension left running...");
                try
                {
                    await vmResource.RestartAsync(WaitUntil.Completed, cancellationToken: CancellationToken);
                    _logger.LogInformation($"Hybrid Worker VM '{vmShortName}' restarted.");
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogWarning($"VM restart failed (will attempt extension install anyway): {FirstLine(ex.Message)} (HTTP {ex.Status} {ex.ErrorCode}).");
                }
                catch (Exception ex) when (!CancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning($"VM restart failed (will attempt extension install anyway): {FirstLine(ex.Message)}");
                }
            }

            _logger.LogInformation($"Installing Hybrid Worker extension on VM '{vmShortName}' with Automation Account registration...");

            var extensionData = new VirtualMachineExtensionData(AzureLocation)
            {
                Publisher = EXTENSION_PUBLISHER,
                ExtensionType = EXTENSION_TYPE,
                TypeHandlerVersion = "1.1",
                AutoUpgradeMinorVersion = true,
                // ForceUpdateTag forces the extension to re-run even if settings haven't changed.
                // Without this, the extension sees the same sequence number and skips execution.
                ForceUpdateTag = Guid.NewGuid().ToString(),
                Settings = WithAutomationAccountUrl(existingExtension?.Data.Settings, automationAccountUrl),
            };

            try
            {
                await vmResource.GetVirtualMachineExtensions().CreateOrUpdateAsync(WaitUntil.Completed, extensionName, extensionData, cancellationToken: CancellationToken);
                _logger.LogInformation($"Hybrid Worker extension installed/updated successfully on VM '{vmShortName}'.");
                return true;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError($"Failed to install Hybrid Worker extension on VM '{vmShortName}': {FirstLine(ex.Message)} (HTTP {ex.Status} {ex.ErrorCode}).");
                return false;
            }
        }

        const string EXTENSION_PUBLISHER = "Microsoft.Azure.Automation.HybridWorker";
        const string EXTENSION_TYPE = "HybridWorkerForWindows";

        /// <summary>
        /// The VM's Hybrid Worker extension, found by publisher and type rather than by name: one added from the
        /// Azure portal is called "HybridWorkerExtension", and a VM can't have two of them.
        /// </summary>
        private VirtualMachineExtensionResource FindHybridWorkerExtension(VirtualMachineResource vmResource)
        {
            foreach (var extension in vmResource.GetVirtualMachineExtensions().GetAll(cancellationToken: CancellationToken))
            {
                if (string.Equals(extension.Data.Publisher, EXTENSION_PUBLISHER, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(extension.Data.ExtensionType, EXTENSION_TYPE, StringComparison.OrdinalIgnoreCase))
                {
                    return extension;
                }
            }
            return null;
        }

        /// <summary>
        /// Re-runs a healthy extension so it registers with the group again. A PATCH carrying a new force-update tag,
        /// so nothing is removed, the protected settings are not touched and the VM is not restarted.
        /// </summary>
        private async Task<bool> RerunExtension(VirtualMachineExtensionResource extension, string automationAccountUrl, string vmShortName)
        {
            var patch = new VirtualMachineExtensionPatch { ForceUpdateTag = Guid.NewGuid().ToString() };
            if (string.IsNullOrWhiteSpace(ReadAutomationAccountUrl(extension.Data.Settings)))
            {
                patch.Settings = WithAutomationAccountUrl(extension.Data.Settings, automationAccountUrl);
            }

            try
            {
                await extension.UpdateAsync(WaitUntil.Completed, patch, CancellationToken);
                _logger.LogInformation($"Re-ran the Hybrid Worker extension on VM '{vmShortName}'. The VM was not restarted.");
                return true;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError($"Failed to re-run the Hybrid Worker extension on VM '{vmShortName}': {FirstLine(ex.Message)} (HTTP {ex.Status} {ex.ErrorCode}).");
                return false;
            }
        }

        /// <summary>
        /// Clean up stale hybrid worker registry keys on the VM. Previous failed installs can
        /// leave behind HKLM:\SOFTWARE\Microsoft\HybridRunbookWorkerV2 entries that trick the
        /// extension into thinking the machine is already registered. It then tries to stop a
        /// HybridWorkerService that was never created, and fails. Only run when the VM has no
        /// Hybrid Worker extension, or after a broken one has been removed.
        /// </summary>
        private async Task CleanUpWorkerLeftovers(VirtualMachineResource vmResource)
        {
            _logger.LogInformation("Cleaning stale hybrid worker registry and service on VM...");
            try
            {
                var cleanupScript = @"
                    # Remove stale hybrid worker registry entries
                    $regPath = 'HKLM:\SOFTWARE\Microsoft\HybridRunbookWorkerV2'
                    if (Test-Path $regPath) {
                        Remove-Item -Path $regPath -Recurse -Force
                        Write-Output 'Removed stale HybridRunbookWorkerV2 registry key.'
                    }
                    # Also remove stale extension status registry
                    $extRegPath = 'HKLM:\SOFTWARE\Microsoft\Azure\HybridWorker'
                    if (Test-Path $extRegPath) {
                        Remove-Item -Path $extRegPath -Recurse -Force
                        Write-Output 'Removed stale HybridWorker extension registry key.'
                    }
                    # Stop and remove the service if it exists in a broken state
                    $svc = Get-Service -Name 'HybridWorkerService' -ErrorAction SilentlyContinue
                    if ($svc) {
                        Stop-Service -Name 'HybridWorkerService' -Force -ErrorAction SilentlyContinue
                        sc.exe delete 'HybridWorkerService' | Out-Null
                        Write-Output 'Removed stale HybridWorkerService.'
                    }
                    Write-Output 'Cleanup complete.'
                ";

                var runCommandInput = new RunCommandInput("RunPowerShellScript");
                runCommandInput.Script.Add(cleanupScript);
                await vmResource.RunCommandAsync(WaitUntil.Completed, runCommandInput, cancellationToken: CancellationToken);
                _logger.LogInformation("VM cleanup script completed.");
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning($"VM cleanup script failed (will attempt extension install anyway): {FirstLine(ex.Message)} (HTTP {ex.Status} {ex.ErrorCode}).");
            }
            catch (Exception ex) when (!CancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning($"VM cleanup script failed (will attempt extension install anyway): {FirstLine(ex.Message)}");
            }
        }

        /// <summary>
        /// Asks the Azure Instance Metadata Service which VM the installer is running on. It only answers on an Azure
        /// VM, so anywhere else the result is <see cref="InstallerHostCheck.Unknown"/>, which never allows a restart.
        /// </summary>
        private async Task<InstallerHostCheck> DetectInstallerHostAsync(string vmResourceId, string targetVmId)
        {
            var imds = await ReadImdsComputeIdentityAsync();
            return HybridWorkerExtensionPolicy.CheckInstallerHost(vmResourceId, targetVmId, imds.ResourceId, imds.VmId);
        }

        /// <summary>
        /// compute.resourceId and compute.vmId of the machine the installer runs on, from the Azure Instance Metadata
        /// Service; nulls when it doesn't answer (any machine that isn't an Azure VM, or one where it is blocked).
        /// https://learn.microsoft.com/azure/virtual-machines/instance-metadata-service
        /// </summary>
        protected virtual async Task<(string ResourceId, string VmId)> ReadImdsComputeIdentityAsync()
        {
            try
            {
                // IMDS must be called directly, never through a proxy
                using (var handler = new System.Net.Http.HttpClientHandler { UseProxy = false })
                using (var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) })
                using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01"))
                {
                    request.Headers.Add("Metadata", "true");
                    using (var response = await http.SendAsync(request, CancellationToken))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            var compute = Newtonsoft.Json.Linq.JObject.Parse(await response.Content.ReadAsStringAsync());
                            return (compute["resourceId"]?.ToString(), compute["vmId"]?.ToString());
                        }
                    }
                }
            }
            catch (Exception ex) when (!CancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug($"The Azure Instance Metadata Service didn't answer, so this is probably not an Azure VM: {ex.Message}");
            }
            return (null, null);
        }

        /// <summary>Overridden by tests to talk to a fake Azure Resource Manager.</summary>
        protected virtual ArmClient CreateArmClient() => new ArmClient(_credential);

        /// <summary>AutomationAccountURL from an extension's public settings, or null when it isn't there or can't be read.</summary>
        internal static string ReadAutomationAccountUrl(BinaryData settings)
        {
            var json = ParseSettings(settings);
            return json?.GetValue("AutomationAccountURL", StringComparison.OrdinalIgnoreCase)?.ToString();
        }

        /// <summary>The extension's public settings with AutomationAccountURL set, keeping anything else in them (proxy settings, say).</summary>
        internal static BinaryData WithAutomationAccountUrl(BinaryData settings, string automationAccountUrl)
        {
            var json = ParseSettings(settings) ?? new Newtonsoft.Json.Linq.JObject();
            var existingKey = json.Properties().FirstOrDefault(p => string.Equals(p.Name, "AutomationAccountURL", StringComparison.OrdinalIgnoreCase));
            existingKey?.Remove();
            json["AutomationAccountURL"] = automationAccountUrl;
            return BinaryData.FromString(json.ToString(Newtonsoft.Json.Formatting.None));
        }

        static Newtonsoft.Json.Linq.JObject ParseSettings(BinaryData settings)
        {
            if (settings == null) return null;
            try
            {
                return Newtonsoft.Json.Linq.JToken.Parse(settings.ToString()) as Newtonsoft.Json.Linq.JObject;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads VM power state via InstanceView. Returns null and logs an error if the call fails;
        /// otherwise returns the lowercase state string (e.g. "running", "starting", "deallocated").
        /// </summary>
        private async Task<string> ReadVmPowerStateAsync(VirtualMachineResource vmResource, string vmShortName)
        {
            try
            {
                var iv = await vmResource.InstanceViewAsync(cancellationToken: CancellationToken);
                return iv.Value.Statuses
                    .Where(s => s.Code != null && s.Code.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Code.Substring("PowerState/".Length))
                    .FirstOrDefault();
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError($"Could not read Hybrid Worker VM '{vmShortName}' power state: {FirstLine(ex.Message)} (HTTP {ex.Status} {ex.ErrorCode}). Cannot install extension.");
                return null;
            }
        }

        /// <summary>
        /// Azure.RequestFailedException.Message contains the full HTTP response (status, headers, body).
        /// For install-log readability, keep only the first line — the actual human-readable error.
        /// </summary>
        private static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            var nl = message.IndexOfAny(new[] { '\r', '\n' });
            return nl < 0 ? message : message.Substring(0, nl).TrimEnd();
        }

        private static string ShortVmName(string vmResourceId)
        {
            if (string.IsNullOrEmpty(vmResourceId)) return "<unknown>";
            var idx = vmResourceId.LastIndexOf('/');
            return idx < 0 ? vmResourceId : vmResourceId.Substring(idx + 1);
        }

        /// <summary>
        /// Fetches the automationHybridServiceUrl from the Automation Account via a generic ARM GET.
        /// The SDK's AutomationHybridServiceUri property is often null for older/existing accounts,
        /// but the REST API (2023-11-01) reliably returns it.
        /// </summary>
        private async Task<string> GetAutomationHybridServiceUrl(AutomationAccountResource automationAccount)
        {
            // Try SDK property first
            var sdkUrl = automationAccount.Data.AutomationHybridServiceUri?.ToString();
            if (!string.IsNullOrEmpty(sdkUrl))
            {
                _logger.LogDebug($"Using Automation Hybrid Service URL from SDK: {sdkUrl}");
                return sdkUrl;
            }

            // Fall back to REST API call with a newer API version
            try
            {
                var resourceId = automationAccount.Data.Id;

                var tokenRequest = new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" });
                var token = await _credential.GetTokenAsync(tokenRequest, CancellationToken);

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
                    var restUrl = $"https://management.azure.com{resourceId}?api-version=2023-11-01";
                    using (var response = await httpClient.GetAsync(restUrl, CancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                        var restResponse = await response.Content.ReadAsStringAsync();

                        var json = Newtonsoft.Json.Linq.JObject.Parse(restResponse);
                        var hybridUrl = json.SelectToken("properties.automationHybridServiceUrl")?.ToString();

                        if (!string.IsNullOrEmpty(hybridUrl))
                        {
                            // Internal/debug detail — only useful when troubleshooting an automation
                            // hybrid worker registration. Suppress from normal install logs.
                            _logger.LogDebug($"Retrieved Automation Hybrid Service URL via REST: {hybridUrl}");
                            return hybridUrl;
                        }
                    }
                }
            }
            catch (Exception ex) when (!CancellationToken.IsCancellationRequested)
            {
                _logger.LogError($"Failed to retrieve Automation Hybrid Service URL: {ex.Message}");
            }

            _logger.LogError("Could not determine Automation Hybrid Service URL. Hybrid Worker extension cannot be registered.");
            return string.Empty;
        }
    }
}
