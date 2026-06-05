using System;
using UsageReporting;

namespace Web.Config
{
    public class WebAppConfig : PropertyBoundConfig
    {
        public WebAppConfig(IConfiguration config) : base(config)
        {
        }

        [ConfigValue] public string TelemetrySecret { get; set; } = string.Empty;

        /// <summary>
        /// Optional. Hard cap on how many client records the dashboard endpoints will pull from
        /// Cosmos in a single request, to bound a future scan if many tenants report in.
        /// Defaults to 5000 if unset / unparseable.
        /// </summary>
        [ConfigValue(optional: true)] public string MaxDashboardItems { get; set; } = string.Empty;

        public int GetMaxDashboardItems()
        {
            if (int.TryParse(MaxDashboardItems, out var value) && value > 0)
            {
                return value;
            }
            return 5000;
        }

        /// <summary>
        /// Optional. How long the dashboard endpoints cache the underlying Cosmos read for.
        /// Defaults to 60 seconds. Set to 0 to disable caching (useful for local debugging).
        /// </summary>
        [ConfigValue(optional: true)] public string DashboardCacheSeconds { get; set; } = string.Empty;

        public TimeSpan GetDashboardCacheDuration()
        {
            if (int.TryParse(DashboardCacheSeconds, out var seconds) && seconds >= 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
            return TimeSpan.FromSeconds(60);
        }

        [ConfigSection("CosmosDb")] public CosmosConfig CosmosDb { get; set; } = null!;

    }

    public class CosmosConfig : PropertyBoundConfig, IStatsServiceCosmosConfig
    {
        public CosmosConfig(IConfiguration config) : base(config)
        {
        }

        /// <summary>
        /// Cosmos DB account endpoint URL, e.g. https://myaccount.documents.azure.com:443/
        /// Used when authenticating with Microsoft Entra ID (DefaultAzureCredential) instead of an account key.
        /// </summary>
        [ConfigValue] public string AccountEndpoint { get; set; } = string.Empty;
        [ConfigValue] public string DatabaseName { get; set; } = string.Empty;
        [ConfigValue] public string ContainerNameCurrent { get; set; } = string.Empty;
        [ConfigValue] public string ContainerNameHistory { get; set; } = string.Empty;
    }
}
