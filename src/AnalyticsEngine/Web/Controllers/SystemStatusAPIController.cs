using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Redis;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.Health;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Serves the system-status data that used to be the server-rendered home page, now consumed
    /// by the SPA's Home page.
    /// </summary>
    [Authorize]
    [RoutePrefix("api/SystemStatus")]
    public class SystemStatusAPIController : ApiController
    {
        // GET: api/SystemStatus
        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> Get()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var appConfig = new AppConfig();
                // Redis is optional for the web app, so tolerate it not being configured.
                var cache = CacheConnectionManager.TryGetConnectionManager(appConfig.ConnectionStrings.RedisConnectionString, tenantId: appConfig.TenantGUID.ToString(), clientId: appConfig.ClientID, clientSecret: appConfig.ClientSecret);
                var s = await SystemStatus.LoadFrom(db, cache);

                var imports = appConfig.ImportJobSettings;

                var model = new SystemStatusApiModel
                {
                    BuildLabel = s.BuildLabel,
                    HasValidConfig = s.HasValidConfig,
                    DataCounts = await BuildDataCountsAsync(db, imports),
                    EnabledImports = HealthService.DescribeEnabledImports(imports),
                    ImportSettingsKnown = imports != null,
                    WebhookEndpointUrl = (s.WebAppBaseURL ?? string.Empty) + "api/CallRecordWebhook",
                    CallsImportEnabled = s.CallsImportEnabled,
                    CallWebhookState = s.CallWebhookState.ToString(),
                    CallWebhookExpiry = s.CallWebhookExpiry,
                    CallWebhookStatusDetail = s.CallWebhookStatusDetail,
                    WebAppConfigSQL = s.WebAppConfigSQL,
                    WebAppConfigRedis = s.WebAppConfigRedis,
                    WebAppConfigCognitive = s.WebAppConfigCognitive,
                    CognitiveServiceEnabled = s.CognitiveServiceEnabled,
                    WebAppConfigServiceBus = s.WebAppConfigServiceBus,
                };

                return Ok(model);
            }
        }

        private const string DataCountsCacheKeyPrefix = "SystemStatus::DataCounts::";

        /// <summary>
        /// One headline figure on the home page: a stable key, its label, a one-line explanation of where
        /// the data comes from, the import switches that make it meaningful, and how to count it.
        /// </summary>
        private sealed class DataCountDefinition
        {
            public string Key { get; }
            public string Name { get; }
            public string Hint { get; }

            /// <summary>
            /// The figure is shown when <b>any</b> of these imports is switched on. Empty means "always"
            /// (the figure is filled by several imports, so it is meaningful whatever is enabled).
            /// </summary>
            public Func<ImportTaskSettings, bool>[] EnabledBy { get; }

            public Func<AnalyticsEntitiesContext, Task<int>> CountAsync { get; }

            public DataCountDefinition(
                string key,
                string name,
                string hint,
                Func<AnalyticsEntitiesContext, Task<int>> countAsync,
                params Func<ImportTaskSettings, bool>[] enabledBy)
            {
                Key = key;
                Name = name;
                Hint = hint;
                CountAsync = countAsync;
                EnabledBy = enabledBy ?? new Func<ImportTaskSettings, bool>[0];
            }

            /// <summary>Whether this figure is worth showing, given the deployment's import settings.</summary>
            public bool AppliesTo(ImportTaskSettings settings)
            {
                // Unknown settings (no saved config yet) show everything rather than an empty home page.
                if (settings == null || EnabledBy.Length == 0) return true;
                return EnabledBy.Any(predicate => predicate(settings));
            }
        }

        /// <summary>
        /// The home page's figures, in display order. Each is tied to the import(s) that populate it so a
        /// deployment is never shown a permanent zero for a workload it deliberately never turned on -
        /// which is indistinguishable from "the import is broken" and is exactly the wrong first impression.
        /// </summary>
        private static readonly DataCountDefinition[] DataCountCatalogue =
        {
            new DataCountDefinition("users", "Users", "People discovered by any import",
                db => db.users.CountAsync()),

            new DataCountDefinition("auditEvents", "Audit events", "Activity from the unified audit log",
                db => db.AuditEventsCommon.CountAsync(),
                s => s.ActivityLog),

            new DataCountDefinition("copilotInteractions", "Copilot interactions", "Copilot chats from the audit feed",
                db => db.CopilotChats.CountAsync(),
                s => s.Copilot),

            new DataCountDefinition("copilotAiInteractions", "Copilot AI interactions", "From Graph AI interaction history",
                db => db.CopilotInteractions.CountAsync(),
                s => s.CopilotInteractionHistory),

            new DataCountDefinition("webHits", "Web page hits", "Page views from the SharePoint tracker",
                db => db.hits.CountAsync(),
                s => s.WebTraffic),

            new DataCountDefinition("trackedUrls", "Tracked URLs", "Distinct pages seen by the tracker",
                db => db.urls.CountAsync(),
                s => s.WebTraffic),

            new DataCountDefinition("sharePointSites", "SharePoint sites", "Sites seen in activity or web traffic",
                db => db.sites.CountAsync(),
                s => s.ActivityLog, s => s.WebTraffic),

            new DataCountDefinition("sentEmails", "Sent emails", "Mail sent, imported from Graph",
                db => db.SentEmails.CountAsync(),
                s => s.SentEmails),

            new DataCountDefinition("teams", "Teams discovered", "Teams found in the tenant",
                db => db.Teams.CountAsync(),
                s => s.GraphTeams),

            new DataCountDefinition("teamsTracked", "Teams with deep tracking", "Teams that granted channel-level analytics",
                db => db.Teams.Where(t => t.HasRefreshToken).CountAsync(),
                s => s.GraphTeams),

            new DataCountDefinition("teamsCalls", "Teams calls", "Call records from Graph",
                db => db.CallRecords.CountAsync(),
                s => s.Calls),

            new DataCountDefinition("powerApps", "Power Apps", "Apps seen in Power Platform activity",
                db => db.power_apps.CountAsync(),
                s => s.ImportPowerPlatform),

            new DataCountDefinition("dlpMatches", "DLP rule matches", "Purview DLP rules triggered",
                db => db.dlp_rule_matches.CountAsync(),
                s => s.ImportDlp),

            new DataCountDefinition("licenceTypes", "Licence SKUs", "Licence types assigned in the tenant",
                db => db.LicenseTypes.CountAsync(),
                s => s.GraphUsersMetadata),

            new DataCountDefinition("copilotStudioCreditDays", "Copilot Studio credit days", "Billed agent credits, per agent per day",
                db => db.CopilotStudioCreditDaily.CountAsync(),
                s => s.CopilotStudioCredits),

            new DataCountDefinition("azureCostDays", "Azure cost days", "Daily Azure spend from Cost Management",
                db => db.AzureCostDaily.CountAsync(),
                s => s.AzureCostManagement),
        };

        /// <summary>
        /// The keys of the figures that apply to a given set of import settings, in display order. Exposed
        /// so a test can prove the gating without a database.
        /// </summary>
        internal static List<string> ApplicableCountKeys(ImportTaskSettings settings)
            => DataCountCatalogue.Where(d => d.AppliesTo(settings)).Select(d => d.Key).ToList();

        /// <summary>
        /// Record counts for the figures that apply to this deployment. Several of these are full-table
        /// COUNT(*) on large tables (hits, audit_events), so the result is cached briefly to keep the home
        /// page fast on large tenants (the counts only need to be roughly current). Counts for imports that
        /// are switched off are not merely hidden - they are never queried, so turning a workload off also
        /// removes its scan from the home page.
        /// </summary>
        private static async Task<List<NamedCountModel>> BuildDataCountsAsync(AnalyticsEntitiesContext db, ImportTaskSettings imports)
        {
            var applicable = DataCountCatalogue.Where(d => d.AppliesTo(imports)).ToList();

            // The shape of the result now depends on configuration, so the cache key has to as well -
            // otherwise toggling an import would keep serving the previous shape for up to a minute.
            var cacheKey = DataCountsCacheKeyPrefix + string.Join(",", applicable.Select(d => d.Key));
            if (MemoryCache.Default.Get(cacheKey) is List<NamedCountModel> cached)
            {
                return cached;
            }

            var counts = new List<NamedCountModel>(applicable.Count);
            foreach (var definition in applicable)
            {
                counts.Add(new NamedCountModel(definition.Key, definition.Name, await definition.CountAsync(db), definition.Hint));
            }

            MemoryCache.Default.Set(cacheKey, counts, DateTimeOffset.UtcNow.AddSeconds(60));
            return counts;
        }
    }
}
