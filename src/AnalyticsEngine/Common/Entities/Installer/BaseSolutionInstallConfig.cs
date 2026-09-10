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
        // Schema version of the installer's SolutionInstallConfig (the saved *.json config the installer
        // reads/writes). BUMP THIS whenever the SolutionInstallConfig schema changes - i.e. any add/remove/
        // rename of a persisted property on BaseSolutionInstallConfig / SolutionInstallConfig /
        // TargetSolutionConfig / ImportTaskSettings (a new import toggle, a new resource field, etc.). Use
        // Major.Minor.Patch: minor for additive changes, major for breaking ones.
        // History: 1.8.0 -> 1.9.0 added ImportTaskSettings.ImportPowerPlatform (opt-in Power Platform workload).
        //          1.9.0 -> 1.10.0 added ImportTaskSettings.GraphCopilotUsageReports (opt-in Graph Copilot usage reports).
        //          1.10.0 -> 2.0.0 BREAKING: removed ImportTaskSettings.GraphUserApps - Teams add-on / app-install
        //          tracking is deprecated and the import no longer exists. Older configs still load; the
        //          property is simply ignored.
        //          2.0.0 -> 2.1.0 added ImportTaskSettings.CopilotInteractionHistory (opt-in Copilot AI
        //          interaction history import).
        //          2.1.0 -> 2.2.0 added SharePointConfig.AuthClientId / AuthTenantId (optional Entra ID app
        //          registration for the interactive SharePoint sign-in, replacing the old cookie web-login).
        //          2.2.0 -> 2.3.0 added SqlAuthMode + SQLEntraAdminLogin / SQLEntraAdminObjectId /
        //          SQLEntraAdminPrincipalType (Microsoft Entra ID authentication for Azure SQL). Additive
        //          and back-compatible: SQLServerAdminUsername / SQLServerAdminPasswordHash are retained
        //          (deprecated) and an existing config with neither new field keeps using SQL auth exactly
        //          as before. See issue #117.
        //          2.3.0 -> 2.4.0 added ImportTaskSettings.CopilotStudioCredits and
        //          ImportTaskSettings.AzureCostManagement (opt-in agent cost imports: billed Copilot Studio
        //          Copilot Credits, and daily Azure spend from Microsoft Cost Management).
        //          2.4.0 -> 2.5.0 added ImportTaskSettings.ImportDlp (opt-in Microsoft Purview DLP import
        //          from the DLP.All content type; needs the separate ActivityFeed.ReadDlp permission).
        const string CONFIG_VERSION = "2.5.0";

        public BaseSolutionInstallConfig()
        {
            this.ResourceGroupName = string.Empty;
            this.StorageAccountName = string.Empty;
            this.SQLServerDatabaseName = string.Empty;
            this.SQLServerName = string.Empty;
            this.CognitiveServiceName = string.Empty;
            this.CognitiveServicesEnabled = true;
            this.ServiceBusEnabled = true;
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

        /// <summary>
        /// Whether to provision Azure Service Bus and configure related runtime connection strings.
        /// Service Bus is only required by the Teams calls/call-records import.
        /// </summary>
        public bool ServiceBusEnabled { get; set; } = true;

        public string StorageAccountName { get; set; } = string.Empty;

        public string SQLServerName { get; set; } = string.Empty;
        public string SQLServerDatabaseName { get; set; } = string.Empty;

        /// <summary>
        /// DEPRECATED. SQL Server authentication admin login for the Azure SQL server.
        /// </summary>
        /// <remarks>
        /// Kept for backwards compatibility: existing deployments were created with SQL authentication and
        /// must keep working untouched. New deployments should use <see cref="SqlAuthMode"/> =
        /// <see cref="SqlServerAuthMode.EntraId"/>, which authenticates with Microsoft Entra ID and stores
        /// no long-lived secret. See issue #117.
        /// </remarks>
        public string SQLServerAdminUsername { get; set; } = string.Empty;

        /// <summary>
        /// How the solution authenticates to Azure SQL.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="SqlServerAuthMode.SqlLogin"/> so a config file written by an older
        /// installer - which has no such property - deserialises to exactly the behaviour it had before.
        /// Only ever applied when the installer CREATES the SQL server; an existing server's authentication
        /// configuration is never changed, and the mode actually used at install time is detected from the
        /// server itself (see <c>SqlServerAuthDetection</c>).
        /// </remarks>
        public SqlServerAuthMode SqlAuthMode { get; set; } = SqlServerAuthMode.SqlLogin;

        /// <summary>
        /// Display name / UPN of the Microsoft Entra ID principal to set as the SQL server's Entra
        /// administrator. Optional: when blank the installer uses its own service principal, which is the
        /// identity that has to run the schema upgrade anyway.
        /// </summary>
        public string SQLEntraAdminLogin { get; set; } = string.Empty;

        /// <summary>
        /// Object (principal) ID of <see cref="SQLEntraAdminLogin"/>. Azure requires the object ID, not the
        /// UPN, to set a server Entra administrator.
        /// </summary>
        public string SQLEntraAdminObjectId { get; set; } = string.Empty;

        /// <summary>
        /// Principal type of the configured Entra administrator - "User", "Group" or "Application".
        /// </summary>
        public string SQLEntraAdminPrincipalType { get; set; } = "User";

        public bool CognitiveServicesEnabled { get; set; } = true;
        public string CognitiveServiceName { get; set; } = string.Empty;

        public string RedisName { get; set; } = string.Empty;

        public bool DownloadLatestStable { get; set; } = true;

        /// <summary>
        /// DEPRECATED. Encrypted SQL Server authentication admin password.
        /// </summary>
        /// <remarks>
        /// Retained so existing config files keep loading and existing SQL-authentication servers keep
        /// working. Not written when <see cref="SqlAuthMode"/> is <see cref="SqlServerAuthMode.EntraId"/>.
        /// See issue #117.
        /// </remarks>
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
    /// How the solution authenticates to its Azure SQL database.
    /// </summary>
    /// <remarks>
    /// Serialised by name so saved config files stay readable and stay valid if members are ever
    /// reordered. <see cref="SqlLogin"/> is deliberately the zero/default value so a config file written
    /// before this property existed loads as "SQL authentication", which is what those deployments use.
    /// </remarks>
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum SqlServerAuthMode
    {
        /// <summary>
        /// DEPRECATED for new deployments. SQL Server authentication with an admin login and password
        /// stored in the connection string. Still fully supported: existing servers are never migrated.
        /// </summary>
        SqlLogin = 0,

        /// <summary>
        /// Microsoft Entra ID authentication. No password is stored anywhere; the App Service uses its
        /// system-assigned managed identity and the installer uses its own service principal.
        /// </summary>
        EntraId = 1,
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
        public string StorageTable { get; set; } = string.Empty;
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
