using Common.Entities.Installer;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Centralised guidance for installer steps that need data-plane access (SQL, HTTP, secret upload) to
    /// PaaS resources whose public network access has been disabled.
    ///
    /// In that mode the only way for those data-plane calls to succeed is for the installer host to have private
    /// network line-of-sight to the resource - i.e. it must be joined to the same VNet (or a peered VNet, or
    /// reachable through VPN / ExpressRoute / Bastion) so DNS resolves the public hostnames to the resource's
    /// private endpoint IP addresses. When that's not the case, the failure surfaces as a generic network /
    /// auth / DNS error and the operator has no way to know what the remediation is.
    /// </summary>
    public static class PrivateNetworkGuidance
    {
        /// <summary>
        /// True when VNet integration is enabled AND public network access on the PaaS resources is disabled,
        /// i.e. the installer host must be on the private network for data-plane calls to succeed.
        /// </summary>
        public static bool IsPrivateNetworkOnly(SolutionInstallConfig config) =>
            config?.NetworkConfig != null && config.NetworkConfig.Enabled && !config.NetworkConfig.AllowPublicAccess;

        /// <summary>
        /// True when VNet integration is enabled AND public network access on the PaaS resources is disabled.
        /// </summary>
        public static bool IsPrivateNetworkOnly(VNetConfig networkConfig) =>
            networkConfig != null && networkConfig.Enabled && !networkConfig.AllowPublicAccess;

        /// <summary>
        /// Returns an operator-facing remediation hint to append to a failure log when the installer has just
        /// failed a data-plane operation against a PaaS resource whose public network access is disabled.
        /// </summary>
        /// <param name="operation">Short human description of what was being attempted, e.g. "SQL connectivity test", "App Service HTTPS deployment", "App Service warm-up request".</param>
        /// <param name="vnetName">Name of the VNet the resources are attached to, or null if unknown.</param>
        public static string BuildVmOnVNetGuidance(string operation, string vnetName)
        {
            var vnet = string.IsNullOrEmpty(vnetName) ? "<vnet>" : vnetName;
            return $"Public network access is disabled on the target PaaS resources, so {operation} can only succeed " +
                $"from a host with private-network line-of-sight to them. " +
                $"Re-run the installer from a Windows VM joined to the VNet '{vnet}' " +
                $"(or a peered VNet, VPN/ExpressRoute, or Azure Bastion-attached host) " +
                $"so DNS resolves the resource hostnames to the private endpoint IPs. " +
                $"Alternatively, temporarily re-enable public network access on the affected resource(s) and re-run the installer.";
        }
    }
}
