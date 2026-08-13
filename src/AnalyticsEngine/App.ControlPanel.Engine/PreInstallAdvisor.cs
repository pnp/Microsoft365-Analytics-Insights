using Common.Entities.Installer;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Pre-install checks that warn about configurations which would install "successfully" but leave part of
    /// the solution silently broken. Kept out of the UI so the rules can be unit tested.
    /// </summary>
    public static class PreInstallAdvisor
    {
        /// <summary>
        /// Private deployments disable public network access on every PaaS resource, but Service Bus can only be
        /// reached privately on the Premium SKU. If the namespace can't be Premium the Teams calls import fails at
        /// runtime with an opaque "Ip has been prevented to connect to the endpoint" 401, so the operator needs to
        /// know up front. See issue #228.
        ///
        /// Returns null when there is nothing to warn about.
        /// </summary>
        public static string GetServiceBusPrivateDeploymentWarning(BaseSolutionInstallConfig config)
        {
            if (config == null) return null;

            var vnetEnabled = config.NetworkConfig != null && config.NetworkConfig.Enabled;
            if (!vnetEnabled) return null;

            // Public access still allowed = the namespace stays reachable whatever its SKU.
            if (config.NetworkConfig.AllowPublicAccess) return null;

            // Service Bus is only used by the Teams calls import.
            if (!config.ServiceBusEnabled) return null;

            return "Private deployment + Teams calls import: Service Bus must be on the PREMIUM SKU."
                + "\r\n\r\nPublic network access will be disabled, and a Service Bus namespace can only be reached privately "
                + "through a private endpoint, which requires Premium. Azure cannot upgrade an existing Standard namespace "
                + "to Premium in place."
                + "\r\n\r\nIf the namespace '" + config.ServiceBusName + "' is not Premium, the Teams calls import will fail at "
                + "runtime with 'Put token failed. status-code: 401 ... Ip has been prevented to connect to the endpoint'."
                + "\r\n\r\nOptions:"
                + "\r\n  (a) Migrate the namespace to Premium and re-run the installer."
                + "\r\n  (b) Keep public network access enabled on Service Bus."
                + "\r\n  (c) Disable the Teams calls import (untick Service Bus on the Azure Storage page)."
                + "\r\n\r\nThe installer will NOT disable public access on a namespace that cannot be made private - it stays "
                + "reachable and this warning is repeated in the install summary."
                + "\r\n\r\nContinue with the install?";
        }
    }
}
