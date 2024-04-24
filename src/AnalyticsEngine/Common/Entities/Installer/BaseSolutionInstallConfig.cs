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
