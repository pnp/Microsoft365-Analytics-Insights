using System;
using System.Collections.Generic;

namespace Common.Entities.Installer
{
    /// <summary>
    /// A base model for solution install config. Installer app uses a more concrete version but we need to parse some settings in the web-jobs too.
    /// On which class each property should be should probably be reviewed. For now, anything uncomplicated stays here. 
    /// </summary>
    public class BaseSolutionInstallConfig : BaseConfig
    {
        const string CONFIG_VERSION = "1.8.0";

        public BaseSolutionInstallConfig()
        {
            this.ResourceGroupName = string.Empty;
            this.StorageAccountName = string.Empty;
            this.SQLServerDatabaseName = string.Empty;
            this.SQLServerName = string.Empty;
            this.CognitiveServiceName = string.Empty;
            this.CognitiveServicesEnabled = true;
            this.AllowTelemetry = true;

            this.ConfigSchemaVersion = new Version(CONFIG_VERSION);
        }

        /// <summary>
        /// Specifics of what a target solution needs configuring
        /// </summary>
        public TargetSolutionConfig SolutionConfig { get; set; } = new TargetSolutionConfig();

        public bool AllowTelemetry { get; set; } = true;

        public string ResourceGroupName { get; set; } = string.Empty;

        public string AzureLocationName { get; set; } = null;

        public string ServiceBusName { get; set; } = string.Empty;

        public string StorageAccountName { get; set; } = string.Empty;

        public string SQLServerName { get; set; } = string.Empty;
        public string SQLServerDatabaseName { get; set; } = string.Empty;
        public string SQLServerAdminUsername { get; set; } = string.Empty;

        public bool CognitiveServicesEnabled { get; set; } = true;
        public string CognitiveServiceName { get; set; } = string.Empty;

        public string RedisName { get; set; } = string.Empty;

        public bool DownloadLatestStable { get; set; } = true;

        public string SQLServerAdminPasswordHash { get; set; } = string.Empty;

        public string AppInsightsName { get; set; } = string.Empty;
        public string AppInsightsWorkspaceName { get; set; } = string.Empty;

        public string AppServiceWebAppName { get; set; } = string.Empty;
        public string AppServicePlanName { get; set; } = string.Empty;

        public string KeyVaultName { get; set; } = string.Empty;
        public string AutomationAccountName { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonProperty("ConfigSchemaVersion")]
        public string ConfigSchemaVersionString { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonIgnore]
        public Version ConfigSchemaVersion
        {
            get
            {
                var v = new Version(CONFIG_VERSION);
                if (string.IsNullOrEmpty(this.ConfigSchemaVersionString))
                {
                    return v;
                }
                else
                {
                    return Version.Parse(this.ConfigSchemaVersionString);
                }
            }
            set
            {
                if (value == null)
                {
                    this.ConfigSchemaVersionString = new Version(CONFIG_VERSION).ToString();
                }
                else
                {
                    this.ConfigSchemaVersionString = value.ToString();
                }
            }
        }

        public List<AzTag> Tags { get; set; } = new List<AzTag>();

        public VNetConfig NetworkConfig { get; set; } = new VNetConfig();
    }

    /// <summary>
    /// Configuration for private VNet integration of Azure PaaS resources.
    /// </summary>
    public class VNetConfig : BaseConfig
    {
        public bool Enabled { get; set; } = false;

        public string VNetName { get; set; } = string.Empty;

        public string SubnetName { get; set; } = "default";

        public string AddressPrefix { get; set; } = "10.0.0.0/16";

        public string SubnetAddressPrefix { get; set; } = "10.0.0.0/24";

        /// <summary>
        /// Subnet name for App Service regional VNet integration.
        /// Must be a dedicated subnet (not shared with private endpoints).
        /// </summary>
        public string AppServiceIntegrationSubnetName { get; set; } = "app-integration";

        /// <summary>
        /// Address prefix for the App Service integration subnet (e.g. 10.0.1.0/24).
        /// </summary>
        public string AppServiceIntegrationSubnetAddressPrefix { get; set; } = "10.0.2.0/24";

        /// <summary>
        /// Whether to deploy Azure Private DNS zones for each private endpoint.
        /// Set to false if using custom DNS management (e.g. on-premises DNS or Azure DNS Private Resolver).
        /// </summary>
        public bool DeployDnsZones { get; set; } = true;

        /// <summary>
        /// Whether to allow public network access on Azure PaaS resources created/updated by the installer.
        /// Only honoured when <see cref="Enabled"/> is true (VNet integration enabled). When VNet is disabled,
        /// resources are always created with public access enabled (legacy/default behaviour).
        /// Some customers' Azure policies disallow creation of PaaS resources with public access, so this can
        /// be turned off to require all data-plane access to flow over private endpoints.
        /// </summary>
        public bool AllowPublicAccess { get; set; } = true;

        /// <summary>
        /// Custom private endpoint names. Leave empty/null to use auto-generated defaults (pe-{resourceName}-{suffix}).
        /// </summary>
        public PrivateEndpointNames CustomEndpointNames { get; set; } = new PrivateEndpointNames();

        /// <summary>
        /// Optional: Azure Resource ID of a VM to use as a Hybrid Runbook Worker for the automation account.
        /// When set, the installer will create a hybrid worker group, register the VM, and install the Hybrid Worker extension.
        /// This allows automation runbooks to execute inside the VNet for private endpoint connectivity.
        /// </summary>
        public string HybridWorkerVmResourceId { get; set; } = string.Empty;

        public override List<string> ValidatInputAndGetErrors()
        {
            var errs = new List<string>();
            if (!Enabled) return errs;

            if (string.IsNullOrWhiteSpace(VNetName))
                errs.Add("Provide a VNet name when networking is enabled.");
            if (string.IsNullOrWhiteSpace(SubnetName))
                errs.Add("Provide a subnet name when networking is enabled.");
            if (string.IsNullOrWhiteSpace(AddressPrefix))
                errs.Add("Provide a VNet address prefix (e.g. 10.0.0.0/16).");
            if (string.IsNullOrWhiteSpace(SubnetAddressPrefix))
                errs.Add("Provide a subnet address prefix (e.g. 10.0.0.0/24).");
            if (!string.IsNullOrWhiteSpace(HybridWorkerVmResourceId) && !HybridWorkerVmResourceId.Contains("/providers/Microsoft.Compute/virtualMachines/"))
                errs.Add("Hybrid Worker VM Resource ID must be a valid Azure VM resource ID (e.g. /subscriptions/.../providers/Microsoft.Compute/virtualMachines/myVM).");

            return errs;
        }
    }

    /// <summary>
    /// Custom names for private endpoints. Empty or null values will use auto-generated defaults.
    /// </summary>
    public class PrivateEndpointNames
    {
        public string SqlServer { get; set; } = string.Empty;
        public string AppService { get; set; } = string.Empty;
        public string Redis { get; set; } = string.Empty;
        public string Storage { get; set; } = string.Empty;
        public string KeyVault { get; set; } = string.Empty;
        public string ServiceBus { get; set; } = string.Empty;
        public string CognitiveServices { get; set; } = string.Empty;
        public string AutomationAccount { get; set; } = string.Empty;

        /// <summary>
        /// Gets the endpoint name, falling back to the default if the custom name is empty.
        /// </summary>
        public string GetNameOrDefault(string customName, string defaultName)
        {
            return string.IsNullOrWhiteSpace(customName) ? defaultName : customName.Trim();
        }
    }

    public class AzTag
    {
        public AzTag(string name, string val)
        {
            this.Name = name;
            this.Value = val;
        }

        public string Name { get; set; } = null;
        public string Value { get; set; } = null;
    }

    public static class AzTagExtensions
    {
        public static Dictionary<string, string> ToDictionary(this IEnumerable<AzTag> tags)
        {
            var dict = new Dictionary<string, string>();
            foreach (var tag in tags)
            {
                dict.Add(tag.Name, tag.Value);
            }
            return dict;
        }
    }
}
