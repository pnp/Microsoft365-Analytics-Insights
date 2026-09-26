using System;
using Web.Storage;

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
        [ConfigSection("AzureAd")] public AzureAdConfig AzureAd { get; set; } = null!;

    }

    public class CosmosConfig : PropertyBoundConfig, IStatsServiceCosmosConfig, IClientAnnotationCosmosConfig
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

        /// <summary>
        /// Container for maintainer annotations (which anonymous client is which customer).
        ///
        /// Optional so an existing deployment keeps starting after an upgrade. It must be its own
        /// container - the "current" container's documents are replaced wholesale on every client
        /// upload, so an annotation stored there would be lost.
        /// </summary>
        [ConfigValue(optional: true)] public string ContainerNameAnnotations { get; set; } = string.Empty;

        /// <summary>Container name actually used, falling back to <see cref="DefaultAnnotationsContainer"/>.</summary>
        /// <remarks>
        /// Implemented explicitly rather than defaulting on the property above, because
        /// <see cref="PropertyBoundConfig"/> assigns every <c>[ConfigValue]</c> property
        /// unconditionally - so an absent key overwrites a property initialiser with null. The same
        /// trap is why <c>MaxDashboardItems</c> and <c>DashboardCacheSeconds</c> resolve their defaults
        /// in a method instead of on the property. An explicit implementation is also invisible to that
        /// reflection loop, which only looks at public properties.
        /// </remarks>
        string IClientAnnotationCosmosConfig.ContainerNameAnnotations
        {
            get => string.IsNullOrWhiteSpace(ContainerNameAnnotations)
                ? DefaultAnnotationsContainer
                : ContainerNameAnnotations;
            set => ContainerNameAnnotations = value;
        }

        public const string DefaultAnnotationsContainer = "annotations";
    }

    public class AzureAdConfig : PropertyBoundConfig
    {
        public AzureAdConfig(IConfiguration config) : base(config)
        {
        }

        [ConfigValue] public string Instance { get; set; } = string.Empty;
        [ConfigValue] public string TenantId { get; set; } = string.Empty;
        [ConfigValue] public string ClientId { get; set; } = string.Empty;
        [ConfigValue] public string Scopes { get; set; } = string.Empty;

        public string Authority => $"{Instance.TrimEnd('/')}/{TenantId}";
        public string ApiScope => $"api://{ClientId}/{Scopes}";
    }
}
