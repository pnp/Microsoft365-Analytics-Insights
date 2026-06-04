using UsageReporting;

namespace Web.Config
{
    public class WebAppConfig : PropertyBoundConfig
    {
        public WebAppConfig(IConfiguration config) : base(config)
        {
        }

        [ConfigValue] public string TelemetrySecret { get; set; } = string.Empty;

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
