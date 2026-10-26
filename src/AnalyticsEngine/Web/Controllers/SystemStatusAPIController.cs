using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models;
using Web.AnalyticsWeb.Models.Health;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Serves the system-status data that used to be the server-rendered home page, now consumed
    /// by the SPA's Home page.
    /// </summary>
    /// <remarks>
    /// Shared by both areas: the Insights overview needs the record counts, and the Administration area's
    /// Service configuration page needs the rest. A reader without the Administration permission gets the
    /// counts only, and the admin detail - connection targets, the call-records webhook and its Graph
    /// subscription - is never even loaded for them (#660).
    /// </remarks>
    [Authorize]
    [Route("api/SystemStatus")]
    public class SystemStatusAPIController  : ControllerBase
    {
        private readonly Func<IReadOnlyList<DataCountDefinition>, CancellationToken, Task<List<NamedCountModel>>> _countFigures;
        private readonly SharedComputation<List<NamedCountModel>> _sharedCounts;

        public SystemStatusAPIController()
            : this(CountFiguresAsync, SharedDataCounts)
        {
        }

        /// <summary>For tests: how the figures are counted, and the cache and in-flight computations they share.</summary>
        internal SystemStatusAPIController(
            Func<IReadOnlyList<DataCountDefinition>, CancellationToken, Task<List<NamedCountModel>>> countFigures,
            SharedComputation<List<NamedCountModel>> sharedCounts)
        {
            _countFigures = countFigures ?? throw new ArgumentNullException(nameof(countFigures));
            _sharedCounts = sharedCounts ?? throw new ArgumentNullException(nameof(sharedCounts));
        }

        // GET: api/SystemStatus
        [HttpGet]
        [Route("")]
        public async Task<IActionResult> Get(CancellationToken cancellationToken)
        {
            var administration = PortalAccess.Evaluate(Request, User).Administration;

            var appConfig = new AppConfig();
            var imports = appConfig.ImportJobSettings;
            var dataCounts = await BuildDataCountsAsync(imports, cancellationToken);

            if (!administration)
            {
                return Ok(SystemStatusApiModel.ForInsights(
                    Common.Entities.BuildConstants.BuildLabel,
                    dataCounts,
                    HealthService.DescribeEnabledImports(imports),
                    importSettingsKnown: imports != null));
            }

            using (var db = new AnalyticsEntitiesContext())
            {
                var s = await SystemStatus.LoadFrom(db);

                var model = new SystemStatusApiModel
                {
                    BuildLabel = s.BuildLabel,
                    HasValidConfig = s.HasValidConfig,
                    DataCounts = dataCounts,
                    EnabledImports = HealthService.DescribeEnabledImports(imports),
                    ImportSettingsKnown = imports != null,
                    WebhookEndpointUrl = (s.WebAppBaseURL ?? string.Empty) + "api/CallRecordWebhook",
                    CallsImportEnabled = s.CallsImportEnabled,
                    CallWebhookState = s.CallWebhookState.ToString(),
                    CallWebhookExpiry = s.CallWebhookExpiry,
                    CallWebhookStatusDetail = s.CallWebhookStatusDetail,
                    WebAppConfigSQL = s.WebAppConfigSQL,
                    WebAppConfigStorage = s.WebAppConfigStorage,
                    WebAppConfigCognitive = s.WebAppConfigCognitive,
                    CognitiveServiceEnabled = s.CognitiveServiceEnabled,
                    WebAppConfigServiceBus = s.WebAppConfigServiceBus,
                };

                return Ok(model);
            }
        }

        private const string DataCountsCacheKeyPrefix = "SystemStatus::DataCounts::";

        /// <summary>
        /// SQL timeout for each statement behind the figures. The one catalog read takes milliseconds; this bounds the
        /// exact counts used when it cannot run.
        /// </summary>
        internal const int CountCommandTimeoutSeconds = 15;

        /// <summary>
        /// How long the exact counts (used only when the catalog read fails) may take between them. Inside the shared
        /// deadline, so the figures counted by then are still returned rather than lost to a timeout.
        /// </summary>
        private static readonly TimeSpan ExactCountBudget = TimeSpan.FromSeconds(20);

        /// <summary>
        /// The figures for every concurrent home-page load, counted once and reused for a minute (the counts only need to
        /// be roughly current). An answer with a figure missing, or a count that failed outright, is reused for only 15
        /// seconds, and the whole count gives up after 30, so the landing page cannot hang on it.
        /// </summary>
        private static readonly SharedComputation<List<NamedCountModel>> SharedDataCounts =
            new SharedComputation<List<NamedCountModel>>(
                MemoryCache.Default, DataCountsCacheKeyPrefix,
                successTtl: TimeSpan.FromSeconds(60), failureTtl: TimeSpan.FromSeconds(15), deadline: TimeSpan.FromSeconds(30),
                isDegraded: AnyFigureUncounted);

        private static readonly Lazy<AnalyticsLogger> Logger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "SystemStatus"));

        /// <summary>
        /// One headline figure on the home page: a stable key, its label, a one-line explanation of where
        /// the data comes from, the import switches that make it meaningful, and how to count it.
        /// </summary>
        internal sealed class DataCountDefinition
        {
            public string Key { get; }
            public string Name { get; }
            public string Hint { get; }

            /// <summary>
            /// The figure is shown when <b>any</b> of these imports is switched on. Empty means "always"
            /// (the figure is filled by several imports, so it is meaningful whatever is enabled).
            /// </summary>
            public Func<ImportTaskSettings, bool>[] EnabledBy { get; }

            /// <summary>
            /// The whole table, its row count read from the sys.partitions catalog view rather than scanned; null for a figure that
            /// is a filtered count (<see cref="ExactCountAsync"/>).
            /// </summary>
            public string Table { get; }

            /// <summary>A filtered count of a small table, run as a query; null for a whole-table figure.</summary>
            public Func<AnalyticsEntitiesContext, CancellationToken, Task<int>> ExactCountAsync { get; }

            public DataCountDefinition(
                string key,
                string name,
                string hint,
                string table,
                params Func<ImportTaskSettings, bool>[] enabledBy)
                : this(key, name, hint, enabledBy)
            {
                Table = table ?? throw new ArgumentNullException(nameof(table));
            }

            public DataCountDefinition(
                string key,
                string name,
                string hint,
                Func<AnalyticsEntitiesContext, CancellationToken, Task<int>> exactCountAsync,
                params Func<ImportTaskSettings, bool>[] enabledBy)
                : this(key, name, hint, enabledBy)
            {
                ExactCountAsync = exactCountAsync ?? throw new ArgumentNullException(nameof(exactCountAsync));
            }

            private DataCountDefinition(string key, string name, string hint, Func<ImportTaskSettings, bool>[] enabledBy)
            {
                Key = key;
                Name = name;
                Hint = hint;
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
        /// The table behind one of the context's sets, from the entity's <c>[Table]</c> mapping - so a figure is tied
        /// to the set it counts, and a renamed table cannot leave it reading another table's row count.
        /// </summary>
        internal static string TableOf(string dbSetProperty)
        {
            var entity = typeof(AnalyticsEntitiesContext).GetProperty(dbSetProperty).PropertyType.GetGenericArguments()[0];

            // Read by name: the attribute's assembly (System.ComponentModel.DataAnnotations) is not referenced here.
            var mapping = entity.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "TableAttribute");
            return mapping?.GetType().GetProperty("Name")?.GetValue(mapping) as string
                ?? throw new InvalidOperationException($"{entity.Name} has no [Table] mapping, so its row count cannot be read.");
        }

        /// <summary>
        /// The home page's figures, in display order. Each is tied to the import(s) that populate it so a
        /// deployment is never shown a permanent zero for a workload it deliberately never turned on -
        /// which is indistinguishable from "the import is broken" and is exactly the wrong first impression.
        /// </summary>
        /// <remarks>
        /// Every whole-table figure comes from one read of the <c>sys.partitions</c> catalog view - the same source as
        /// the Health page's Data section - not a <c>COUNT(*)</c> per table. Those scans ran one after another on
        /// the landing page, on the largest tables in the database (<c>audit_events</c>, <c>hits</c>) with no
        /// timeout, by every request that found the cache cold.
        /// </remarks>
        private static readonly DataCountDefinition[] DataCountCatalogue =
        {
            new DataCountDefinition("users", "Users", "People discovered by any import",
                TableOf(nameof(AnalyticsEntitiesContext.users))),

            new DataCountDefinition("auditEvents", "Audit events", "Activity from the unified audit log",
                TableOf(nameof(AnalyticsEntitiesContext.AuditEventsCommon)),
                s => s.ActivityLog),

            new DataCountDefinition("copilotInteractions", "Copilot interactions", "Copilot chats from the audit feed",
                TableOf(nameof(AnalyticsEntitiesContext.CopilotChats)),
                s => s.Copilot),

            new DataCountDefinition("copilotAiInteractions", "Copilot AI interactions", "From Graph AI interaction history",
                TableOf(nameof(AnalyticsEntitiesContext.CopilotInteractions)),
                s => s.CopilotInteractionHistory),

            new DataCountDefinition("webHits", "Web page hits", "Page views from the SharePoint tracker",
                TableOf(nameof(AnalyticsEntitiesContext.hits)),
                s => s.WebTraffic),

            new DataCountDefinition("trackedUrls", "Tracked URLs", "Distinct pages seen by the tracker",
                TableOf(nameof(AnalyticsEntitiesContext.urls)),
                s => s.WebTraffic),

            new DataCountDefinition("sharePointSites", "SharePoint sites", "Sites seen in activity or web traffic",
                TableOf(nameof(AnalyticsEntitiesContext.sites)),
                s => s.ActivityLog, s => s.WebTraffic),

            new DataCountDefinition("sentEmails", "Sent emails", "Mail sent, imported from Graph",
                TableOf(nameof(AnalyticsEntitiesContext.SentEmails)),
                s => s.SentEmails),

            new DataCountDefinition("teams", "Teams discovered", "Teams found in the tenant",
                TableOf(nameof(AnalyticsEntitiesContext.Teams)),
                s => s.GraphTeams),

            // Filtered, so it cannot come from the catalog - but teams is a small table (thousands of rows).
            new DataCountDefinition("teamsTracked", "Teams with deep tracking", "Teams that granted channel-level analytics",
                (db, ct) => db.Teams.Where(t => t.HasRefreshToken).CountAsync(ct),
                s => s.GraphTeams),

            new DataCountDefinition("teamsCalls", "Teams calls", "Call records from Graph",
                TableOf(nameof(AnalyticsEntitiesContext.CallRecords)),
                s => s.Calls),

            new DataCountDefinition("powerApps", "Power Apps", "Apps seen in Power Platform activity",
                TableOf(nameof(AnalyticsEntitiesContext.power_apps)),
                s => s.ImportPowerPlatform),

            new DataCountDefinition("dlpMatches", "DLP rule matches", "Purview DLP rules triggered",
                TableOf(nameof(AnalyticsEntitiesContext.dlp_rule_matches)),
                s => s.ImportDlp),

            new DataCountDefinition("licenceTypes", "Licence SKUs", "Licence types assigned in the tenant",
                TableOf(nameof(AnalyticsEntitiesContext.LicenseTypes)),
                s => s.GraphUsersMetadata),

            new DataCountDefinition("copilotStudioCreditDays", "Copilot Studio credit days", "Billed agent credits, per agent per day",
                TableOf(nameof(AnalyticsEntitiesContext.CopilotStudioCreditDaily)),
                s => s.CopilotStudioCredits),

            new DataCountDefinition("azureCostDays", "Azure cost days", "Daily Azure spend from Cost Management",
                TableOf(nameof(AnalyticsEntitiesContext.AzureCostDaily)),
                s => s.AzureCostManagement),
        };

        /// <summary>
        /// The keys of the figures that apply to a given set of import settings, in display order. Exposed
        /// so a test can prove the gating without a database.
        /// </summary>
        internal static List<string> ApplicableCountKeys(ImportTaskSettings settings)
            => DataCountCatalogue.Where(d => d.AppliesTo(settings)).Select(d => d.Key).ToList();

        /// <summary>Every figure the home page can show, for tests.</summary>
        internal static IReadOnlyList<DataCountDefinition> AllCountDefinitions => DataCountCatalogue;

        /// <summary>
        /// Record counts for the figures that apply to this deployment, shared by every concurrent request and cached
        /// briefly. Counts for imports that are switched off are not merely hidden - they are never queried, so turning a
        /// workload off also removes its count from the home page.
        /// </summary>
        private Task<List<NamedCountModel>> BuildDataCountsAsync(ImportTaskSettings imports, CancellationToken cancellationToken)
        {
            var applicable = DataCountCatalogue.Where(d => d.AppliesTo(imports)).ToList();

            // The shape of the result now depends on configuration, so the cache key has to as well -
            // otherwise toggling an import would keep serving the previous shape for up to a minute.
            var key = string.Join(",", applicable.Select(d => d.Key));
            return _sharedCounts.GetAsync(key, ct => _countFigures(applicable, ct), cancellationToken);
        }

        /// <summary>
        /// Counts the figures: the whole-table ones in one read of the <c>sys.partitions</c> catalog view, the filtered
        /// ones as bounded queries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The catalog view needs nothing beyond the permission to read the tables (see
        /// <see cref="SqlHealthDataSource.LoadApproxRowCounts"/>), so it works for the web app's managed identity on an
        /// Entra-only server. Should it still fail, each table is counted exactly instead - each with a short timeout,
        /// and only within <see cref="ExactCountBudget"/>.
        /// </para>
        /// <para>
        /// A figure that cannot be counted keeps its place with a null count. Leaving it out would read as "this import
        /// is switched off", and the page keys more than the tile off a figure being there (the freshness badges for
        /// audit events and web hits); a zero would read as "this import is broken". If not one figure could be counted
        /// the database is unreachable, and that is an error, as it has always been.
        /// </para>
        /// </remarks>
        internal static async Task<List<NamedCountModel>> CountFiguresAsync(IReadOnlyList<DataCountDefinition> applicable, CancellationToken cancellationToken)
        {
            var counts = new List<NamedCountModel>(applicable.Count);
            Exception firstFailure = null;

            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var db = new AnalyticsEntitiesContext())
            {
                budget.CancelAfter(ExactCountBudget);
                db.Database.CommandTimeout = CountCommandTimeoutSeconds;

                Dictionary<string, long> statistics = null;
                if (applicable.Any(d => d.Table != null))
                {
                    try
                    {
                        statistics = await SqlHealthDataSource.LoadApproxRowCounts(db, cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        Log($"Home page figures - couldn't read the tables' row counts from sys.partitions ({ex.GetType().Name}), so tables are counted one by one: {ex.Message}");
                    }
                }

                foreach (var definition in applicable)
                {
                    if (definition.Table != null && statistics != null)
                    {
                        // A table missing from the catalog does not exist yet in this database: nothing imported.
                        counts.Add(Figure(definition, statistics.TryGetValue(definition.Table, out var rows) ? rows : 0));
                        continue;
                    }

                    if (budget.IsCancellationRequested)
                    {
                        Log($"Home page figure '{definition.Key}' is shown without a count: the time for counting ran out.");
                        counts.Add(Figure(definition, null));
                        continue;
                    }

                    try
                    {
                        var count = definition.Table != null
                            ? await ExactRowCountAsync(db, definition.Table, budget.Token)
                            : await definition.ExactCountAsync(db, budget.Token);
                        counts.Add(Figure(definition, count));
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        firstFailure = firstFailure ?? ex;
                        Log($"Home page figure '{definition.Key}' couldn't be counted, so it is shown without a count ({ex.GetType().Name}): {ex.Message}");
                        counts.Add(Figure(definition, null));
                    }
                }
            }

            if (applicable.Count > 0 && counts.All(c => c.Count == null))
            {
                // Not even one figure: the database is unreachable or hopelessly slow, which is an error - as it was before
                // a figure could go uncounted on its own - not a home page with nothing imported.
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("None of the home page figures could be counted.", firstFailure);
            }

            return counts;
        }

        /// <summary>Whether any figure went uncounted, so the answer is reused only briefly.</summary>
        internal static bool AnyFigureUncounted(List<NamedCountModel> counts) => counts != null && counts.Any(c => c.Count == null);

        private static NamedCountModel Figure(DataCountDefinition definition, long? count)
            => new NamedCountModel(definition.Key, definition.Name, count, definition.Hint);

        private static async Task<long> ExactRowCountAsync(AnalyticsEntitiesContext db, string table, CancellationToken cancellationToken)
        {
            // The name is from an entity's [Table] mapping, never from a request.
            var rows = await db.Database.SqlQuery<long>("SELECT COUNT_BIG(*) FROM [dbo].[" + table + "]").ToListAsync(cancellationToken);
            return rows.FirstOrDefault();
        }

        private static void Log(string message)
        {
            try
            {
                Logger.Value.LogWarning(message);
            }
            catch (Exception)
            {
                // The figures are best-effort; logging them must not turn a missing figure into a failed page.
            }
        }
    }
}