using Microsoft.Extensions.Logging;
using System;

namespace App.ControlPanel.Engine.InstallerTasks.Tasks
{
    /// <summary>
    /// Whether the installer is running on the Hybrid Worker VM itself.
    /// </summary>
    internal enum InstallerHostCheck
    {
        /// <summary>Couldn't tell. Treated as "it might be", so the VM is never restarted.</summary>
        Unknown,
        ThisVm,
        OtherMachine
    }

    internal enum HybridWorkerExtensionAction
    {
        /// <summary>Change nothing on the VM.</summary>
        LeaveAlone,

        /// <summary>The VM has no Hybrid Worker extension: clear what an earlier one left behind, then install it. No restart.</summary>
        Install,

        /// <summary>
        /// The extension is healthy, but the VM had to be registered in the group again: re-run the extension so it
        /// registers. Nothing is removed and the VM is not restarted.
        /// </summary>
        Reapply,

        /// <summary>The extension is broken: remove it, clear its leftovers, restart the VM and install it again.</summary>
        RepairWithRestart,

        /// <summary>The extension is broken, but restarting the VM isn't safe: change nothing and tell the admin how to repair it.</summary>
        ManualRepair
    }

    /// <summary>What the installer found on the Hybrid Worker VM and in the hybrid worker group.</summary>
    internal sealed class HybridWorkerVmFacts
    {
        public string VmName { get; set; }
        public string GroupName { get; set; }

        /// <summary>Whether the VM has a Hybrid Worker extension, whatever it is called.</summary>
        public bool ExtensionFound { get; set; }

        /// <summary>The extension's ARM provisioning state, e.g. "Succeeded" or "Failed".</summary>
        public string ExtensionProvisioningState { get; set; }

        /// <summary>AutomationAccountURL in the extension's public settings; null when it couldn't be read.</summary>
        public string ExtensionAutomationAccountUrl { get; set; }

        /// <summary>The Automation account's hybrid service URL, which this installer registers workers with.</summary>
        public string ExpectedAutomationAccountUrl { get; set; }

        /// <summary>Whether the VM was already a worker in the group before this run registered it.</summary>
        public bool WorkerWasRegistered { get; set; }

        public InstallerHostCheck InstallerHost { get; set; }
    }

    internal sealed class HybridWorkerExtensionDecision
    {
        public HybridWorkerExtensionDecision(HybridWorkerExtensionAction action, LogLevel logLevel, string reason)
        {
            Action = action;
            LogLevel = logLevel;
            Reason = reason;
        }

        public HybridWorkerExtensionAction Action { get; }

        /// <summary>How to log <see cref="Reason"/>: an error for anything the admin has to fix by hand.</summary>
        public LogLevel LogLevel { get; }

        /// <summary>What the installer is doing to the VM, and why, for the install log.</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// Decides what, if anything, the installer does to the Hybrid Worker VM on a run. Kept free of Azure calls so
    /// every branch can be tested.
    ///
    /// The rules: a healthy extension is never removed and its VM never restarted; only a broken one is repaired by
    /// restarting, and only when the installer is known to be running somewhere else, because restarting the machine
    /// the installer runs on kills the install. Installing the extension when it is missing needs no restart.
    /// </summary>
    internal static class HybridWorkerExtensionPolicy
    {
        public const string SUCCEEDED = "Succeeded";

        public static HybridWorkerExtensionDecision Decide(HybridWorkerVmFacts facts)
        {
            if (facts is null) throw new ArgumentNullException(nameof(facts));

            var vm = facts.VmName;
            if (!facts.ExtensionFound)
            {
                return new HybridWorkerExtensionDecision(HybridWorkerExtensionAction.Install, LogLevel.Information,
                    $"Hybrid Worker VM '{vm}' has no Hybrid Worker extension, so the installer is installing it. The VM is not restarted.");
            }

            if (IsRegisteredWithAnotherAccount(facts.ExtensionAutomationAccountUrl, facts.ExpectedAutomationAccountUrl))
            {
                return new HybridWorkerExtensionDecision(HybridWorkerExtensionAction.LeaveAlone, LogLevel.Error,
                    $"The Hybrid Worker extension on VM '{vm}' is registered with a different Automation account, so the installer has left it alone. " +
                    "A VM is a hybrid worker for one Automation account only, and changing it would stop that account's runbooks running there. " +
                    "Choose another VM, or, if that Automation account no longer uses this VM, uninstall the Hybrid Worker extension from the VM " +
                    "(Extensions + applications in the Azure portal) and run the installer again.");
            }

            var state = string.IsNullOrWhiteSpace(facts.ExtensionProvisioningState) ? "unknown" : facts.ExtensionProvisioningState.Trim();
            if (string.Equals(state, SUCCEEDED, StringComparison.OrdinalIgnoreCase))
            {
                if (facts.WorkerWasRegistered)
                {
                    return new HybridWorkerExtensionDecision(HybridWorkerExtensionAction.LeaveAlone, LogLevel.Information,
                        $"The Hybrid Worker extension on VM '{vm}' is healthy (provisioning state '{state}') and the VM is registered in hybrid worker group '{facts.GroupName}', " +
                        "so the VM has been left as it is: nothing was removed, cleaned up or restarted.");
                }
                return new HybridWorkerExtensionDecision(HybridWorkerExtensionAction.Reapply, LogLevel.Information,
                    $"The Hybrid Worker extension on VM '{vm}' is healthy, but the VM wasn't registered in hybrid worker group '{facts.GroupName}' until now, " +
                    "so the installer is re-running the extension to make it register. The extension is not removed and the VM is not restarted.");
            }

            if (IsOperationInProgress(state))
            {
                return new HybridWorkerExtensionDecision(HybridWorkerExtensionAction.LeaveAlone, LogLevel.Warning,
                    $"The Hybrid Worker extension on VM '{vm}' is busy (provisioning state '{state}'), so the installer has left it alone. " +
                    "Check it under Extensions + applications in the Azure portal once that finishes, and run the installer again if it doesn't reach 'Succeeded'.");
            }

            switch (facts.InstallerHost)
            {
                case InstallerHostCheck.OtherMachine:
                    return new HybridWorkerExtensionDecision(HybridWorkerExtensionAction.RepairWithRestart, LogLevel.Warning,
                        $"The Hybrid Worker extension on VM '{vm}' is in provisioning state '{state}', so the installer is repairing it: it removes the extension, " +
                        "clears the worker's leftover registry keys and service, RESTARTS the VM and installs the extension again. " +
                        "Anything running on the VM is interrupted by the restart. The installer is not running on this VM.");
                case InstallerHostCheck.ThisVm:
                    return new HybridWorkerExtensionDecision(HybridWorkerExtensionAction.ManualRepair, LogLevel.Error,
                        $"The Hybrid Worker extension on VM '{vm}' is in provisioning state '{state}' and needs repairing, which restarts the VM. " +
                        "The installer is running on that VM, so it has changed nothing. " + ManualRepairSteps);
                default:
                    return new HybridWorkerExtensionDecision(HybridWorkerExtensionAction.ManualRepair, LogLevel.Error,
                        $"The Hybrid Worker extension on VM '{vm}' is in provisioning state '{state}' and needs repairing, which restarts the VM. " +
                        "The installer couldn't confirm that it isn't running on that VM (it asks the Azure Instance Metadata Service, which only answers on an Azure VM), " +
                        "so it has changed nothing. " + ManualRepairSteps);
            }
        }

        const string ManualRepairSteps = "To repair it: uninstall the Hybrid Worker extension from the VM (Extensions + applications in the Azure portal), " +
            "restart the VM, then run the installer again. It then installs the extension without restarting the VM.";

        /// <summary>
        /// Whether the installer is running on the Hybrid Worker VM, from what the Azure Instance Metadata Service (IMDS)
        /// says about the machine the installer runs on. IMDS only answers on an Azure VM, so on any other machine, or on a
        /// VM where it is blocked, the answer is <see cref="InstallerHostCheck.Unknown"/>.
        /// </summary>
        /// <param name="targetVmResourceId">The Hybrid Worker VM's resource ID, as configured.</param>
        /// <param name="targetVmId">The Hybrid Worker VM's vmId, as ARM reports it.</param>
        /// <param name="imdsResourceId">compute.resourceId from IMDS, or null.</param>
        /// <param name="imdsVmId">compute.vmId from IMDS, or null.</param>
        public static InstallerHostCheck CheckInstallerHost(string targetVmResourceId, string targetVmId, string imdsResourceId, string imdsVmId)
        {
            if (!string.IsNullOrWhiteSpace(imdsResourceId) && !string.IsNullOrWhiteSpace(targetVmResourceId)
                && string.Equals(NormaliseResourceId(imdsResourceId), NormaliseResourceId(targetVmResourceId), StringComparison.OrdinalIgnoreCase))
            {
                return InstallerHostCheck.ThisVm;
            }

            var haveImdsVmId = Guid.TryParse(imdsVmId?.Trim(), out var installerVmId);
            var haveTargetVmId = Guid.TryParse(targetVmId?.Trim(), out var workerVmId);
            if (haveImdsVmId && haveTargetVmId)
            {
                return installerVmId == workerVmId ? InstallerHostCheck.ThisVm : InstallerHostCheck.OtherMachine;
            }
            return InstallerHostCheck.Unknown;
        }

        /// <summary>
        /// A provisioning state of "Creating", "Updating" or "Deleting" means ARM is still working on the extension;
        /// changing it now would conflict with that operation.
        /// </summary>
        static bool IsOperationInProgress(string provisioningState)
        {
            return string.Equals(provisioningState, "Creating", StringComparison.OrdinalIgnoreCase)
                || string.Equals(provisioningState, "Updating", StringComparison.OrdinalIgnoreCase)
                || string.Equals(provisioningState, "Deleting", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Only a URL that was read and differs counts: an unreadable setting proves nothing.</summary>
        static bool IsRegisteredWithAnotherAccount(string extensionUrl, string expectedUrl)
        {
            if (string.IsNullOrWhiteSpace(extensionUrl) || string.IsNullOrWhiteSpace(expectedUrl)) return false;
            return !string.Equals(extensionUrl.Trim().TrimEnd('/'), expectedUrl.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        static string NormaliseResourceId(string resourceId) => resourceId.Trim().TrimEnd('/');
    }
}
