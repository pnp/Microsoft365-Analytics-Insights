using Azure.Identity;
using Microsoft.Azure.Cosmos;
using UsageReporting;
using Web.Config;
using Web.Dashboard;

namespace Web.Startup
{
    /// <summary>
    /// Composition root for the telemetry service. Extracted from <c>Program.cs</c> so the exact
    /// production object graph can be built inside tests without duplicating the wiring.
    /// </summary>
    public static class TelemetryServiceCollectionExtensions
    {
        public static IServiceCollection AddTelemetryServices(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var config = new WebAppConfig(configuration);
            services.AddSingleton(config);

            // Backing store for the dashboard read cache (see DashboardService).
            services.AddMemoryCache();

            if (string.IsNullOrWhiteSpace(config.CosmosDb.AccountEndpoint))
            {
                throw new InvalidOperationException(
                    "CosmosDb:AccountEndpoint is not configured. Set it to the Cosmos account URL, " +
                    "e.g. https://<account>.documents.azure.com:443/");
            }

            services.AddSingleton(_ => CreateCosmosClient(configuration, config));

            // One Cosmos adaptor instance serves both the writer interface (called by the importer-side
            // POST handler) and the reader interface (called by the dashboard). Registered as the concrete
            // type first, then bound to each interface via a factory so we get a single singleton, not two.
            services.AddSingleton(sp => new CosmosTelemetrySaveAdaptor(
                sp.GetRequiredService<CosmosClient>(), config.CosmosDb));
            services.AddSingleton<ITelemetrySaveAdaptor>(sp => sp.GetRequiredService<CosmosTelemetrySaveAdaptor>());
            services.AddSingleton<ITelemetryQueryAdaptor>(sp => sp.GetRequiredService<CosmosTelemetrySaveAdaptor>());

            services.AddTelemetryDomainServices(config);

            // Container creation used to run inline before the host was built, which meant a Cosmos
            // blip stopped the app booting outright - including the anonymous health endpoint. It now
            // runs as a hosted service so startup is never blocked by it.
            services.AddHostedService<CosmosSchemaInitializer>();

            return services;
        }

        /// <summary>
        /// Registers the parts that have no Azure dependency. Split out so tests can register fake
        /// adaptors and still exercise the real services.
        /// </summary>
        public static IServiceCollection AddTelemetryDomainServices(this IServiceCollection services, WebAppConfig config)
        {
            services.AddSingleton(sp => new StatsSaveService(
                sp.GetRequiredService<ITelemetrySaveAdaptor>(),
                sp.GetRequiredService<ILogger<StatsSaveService>>()));

            services.AddSingleton(sp => new DashboardService(
                sp.GetRequiredService<ITelemetryQueryAdaptor>(),
                sp.GetRequiredService<ILogger<DashboardService>>(),
                sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                config.GetMaxDashboardItems(),
                config.GetDashboardCacheDuration()));

            return services;
        }

        private static CosmosClient CreateCosmosClient(IConfiguration configuration, WebAppConfig config)
        {
            // Microsoft Entra ID (AAD) authentication for Cosmos DB. The account has local (key)
            // authorization disabled, so DefaultAzureCredential picks up the developer's Visual Studio /
            // Azure CLI credentials locally and the managed identity when running in Azure.
            //
            // The Cosmos account only trusts tokens issued by its home tenant. If the default Azure
            // tenant differs (e.g. signed into a corp tenant while the account lives in a lab tenant),
            // set AZURE_TENANT_ID in configuration / environment variables.
            var tenantId = configuration["AZURE_TENANT_ID"];
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                TenantId = tenantId,
                // Allow the chained credentials (VS, Azure CLI, etc.) to silently re-auth against
                // the tenant required by the resource, instead of failing with an authority mismatch.
                AdditionallyAllowedTenants = { "*" }
            });

            return new CosmosClient(config.CosmosDb.AccountEndpoint, credential);
        }
    }
}
