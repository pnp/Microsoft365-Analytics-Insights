using Common.Entities;
using Common.Entities.Config;
using Common.Entities.PromptCategories;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    [Authorize]
    [RoutePrefix("api/PromptCategories")]
    public sealed class PromptCategoriesAPIController : ApiController
    {
        /// <summary>
        /// How long one Table storage call may take. Unreachable storage otherwise sits behind the Azure SDK's
        /// retries and its 100-second network timeout, so the admin page would spin for minutes before failing.
        /// </summary>
        internal static TimeSpan StorageTimeout = TimeSpan.FromSeconds(10);

        private readonly PromptCategoryConfigurationStore _store;
        private readonly PromptCategoryRunStore _runStore;
        private readonly AppConfig _settings;

        public PromptCategoriesAPIController() : this(new AppConfig()) { }
        private PromptCategoriesAPIController(AppConfig settings)
        {
            _settings = settings;
            _store = PromptCategoryConfigurationStore.Open(settings);
            _runStore = PromptCategoryRunStore.Open(settings);
        }
        internal PromptCategoriesAPIController(PromptCategoryConfigurationStore store, PromptCategoryRunStore runStore = null)
        {
            _store = store;
            _runStore = runStore ?? new PromptCategoryRunStore(null);
            _settings = new AppConfig();
        }

        [HttpGet, Route("")]
        [RequirePortalPermission(PortalPermission.Administration)]
        public async Task<IHttpActionResult> Get()
        {
            using (var timeout = new CancellationTokenSource(StorageTimeout))
            {
                try
                {
                    return NoStore(new
                    {
                        configuration = await _store.GetAsync(timeout.Token),
                        storageAvailable = _store.IsDurable,
                        backendConfigured = FoundryPromptSettings.IsConfigured(_settings)
                    });
                }
                catch (Exception ex) { return Failure(ex, timeout); }
            }
        }

        [HttpPost, Route("")]
        [RequirePortalPermission(PortalPermission.Administration)]
        [RequireSameOriginXhr]
        public async Task<IHttpActionResult> Save([FromBody] PromptCategoryConfiguration configuration)
        {
            if (configuration == null) return Error(HttpStatusCode.BadRequest, "invalidTaxonomy");
            if (!_store.IsDurable) return Error(HttpStatusCode.Conflict, "storageNotConfigured");
            using (var timeout = new CancellationTokenSource(StorageTimeout))
            {
                try { return NoStore(await _store.SaveAsync(configuration, timeout.Token)); }
                catch (Exception ex) { return Failure(ex, timeout); }
            }
        }

        [HttpPost, Route("reset")]
        [RequirePortalPermission(PortalPermission.Administration)]
        [RequireSameOriginXhr]
        public async Task<IHttpActionResult> Reset()
        {
            if (!_store.IsDurable) return Error(HttpStatusCode.Conflict, "storageNotConfigured");
            using (var timeout = new CancellationTokenSource(StorageTimeout))
            {
                try { return NoStore(await _store.SaveAsync(PromptCategoryConfiguration.Defaults(), timeout.Token)); }
                catch (Exception ex) { return Failure(ex, timeout); }
            }
        }

        [HttpGet, Route("runs")]
        [RequirePortalPermission(PortalPermission.Administration)]
        public async Task<IHttpActionResult> Runs()
        {
            // Without durable storage the importer's counters live in another process, so an empty list would mislead.
            if (!_runStore.IsDurable) return Error(HttpStatusCode.ServiceUnavailable, "storageNotConfigured");
            using (var timeout = new CancellationTokenSource(StorageTimeout))
            {
                try
                {
                    var rows = await _runStore.RecentAsync(timeout.Token);
                    return NoStore(rows.Select(row => new
                    {
                        startedUtc = DateTime.SpecifyKind(row.StartedUtc, DateTimeKind.Utc),
                        counters = row.Counters
                    }).ToArray());
                }
                catch (Exception ex) { return Failure(ex, timeout); }
            }
        }

        /// <summary>
        /// A stable code the portal maps to its own wording (the API reports facts, the UI writes the sentences),
        /// so storage that is slow or down, a rejected taxonomy and a missing database upgrade read differently.
        /// Never echoes exception text, which can carry storage account names.
        /// </summary>
        private IHttpActionResult Failure(Exception ex, CancellationTokenSource timeout)
        {
            if (ex is ArgumentException) return Error(HttpStatusCode.BadRequest, "invalidTaxonomy");
            if (ex is PromptCategoryStoredConfigurationException) return Error(HttpStatusCode.InternalServerError, "storedConfigurationInvalid");
            if (ex is OperationCanceledException || timeout.IsCancellationRequested)
                return Error(HttpStatusCode.ServiceUnavailable, "storageTimeout");
            return Error(HttpStatusCode.ServiceUnavailable, "storageUnavailable");
        }

        private IHttpActionResult Error(HttpStatusCode status, string code)
        {
            var response = Request.CreateResponse(status, new { code });
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, Private = true };
            return ResponseMessage(response);
        }

        [HttpGet, Route("report")]
        public async Task<IHttpActionResult> Report(int months = 3, string version = null)
        {
            if (version != null && !System.Text.RegularExpressions.Regex.IsMatch(version, @"\A[a-f0-9]{64}\z"))
                return BadRequest();
            var resolved = await ReportScopeResolver.Default.ResolveAsync(Request, User, null, CancellationToken.None);
            var scope = resolved.IsRestricted ? resolved.Sql : ReportUserScope.Everyone;
            var from = DateTime.UtcNow.Date.AddMonths(-Math.Max(1, Math.Min(months, 6)));
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.Database.CommandTimeout = 45;
                    var versions = await QueryAsync<PromptCategoryVersionRow>(db,
                        PromptCategoryReportSql.Versions,
                        scope, new SqlParameter("@from", from));
                    version = version ?? versions.FirstOrDefault()?.Version;
                    if (version == null || !versions.Any(v => v.Version == version))
                        return NoStore(new { versions = versions.Select(v => v.Version), version, categories = new object[0], mix = new object[0], trend = new object[0] });

                    var taxonomyJson = await db.Database.SqlQuery<string>(
                        "SELECT categories_json FROM dbo.copilot_prompt_taxonomies WHERE version=@version",
                        new SqlParameter("@version", version)).SingleAsync();
                    var categories = JsonConvert.DeserializeObject<List<PromptCategoryDefinition>>(taxonomyJson);
                    var mix = await QueryAsync<PromptCategoryMixRow>(db,
                        PromptCategoryReportSql.Mix, scope,
                        new SqlParameter("@from", from), new SqlParameter("@version", version));
                    var trend = await QueryAsync<PromptCategoryTrendRow>(db,
                        PromptCategoryReportSql.Trend, scope,
                        new SqlParameter("@from", from), new SqlParameter("@version", version));
                    foreach (var point in trend) point.WeekStart = DateTime.SpecifyKind(point.WeekStart, DateTimeKind.Utc);
                    return NoStore(new { versions = versions.Select(v => v.Version), version, categories, mix, trend });
                }
            }
            catch (Exception ex)
            {
                // 207/208: a column or table the database upgrade adds is missing.
                for (var inner = ex; inner != null; inner = inner.InnerException)
                    if (inner is SqlException sql && (sql.Number == 207 || sql.Number == 208))
                        return Error(HttpStatusCode.ServiceUnavailable, "databaseNotUpgraded");
                return Error(HttpStatusCode.ServiceUnavailable, "reportUnavailable");
            }
        }

        private static Task<List<T>> QueryAsync<T>(AnalyticsEntitiesContext db, string sql, ReportUserScope scope, params object[] parameters)
        {
            var values = parameters.ToList();
            if (scope.IsRestricted)
                values.Add(new SqlParameter("@scopeUsers", SqlDbType.NVarChar, -1) { Value = scope.ToJson() });
            return db.Database.SqlQuery<T>(ReportScopeSql.Apply(sql, scope), values.ToArray()).ToListAsync();
        }

        private IHttpActionResult NoStore(object model)
        {
            var response = Request.CreateResponse(HttpStatusCode.OK, model);
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, Private = true };
            return ResponseMessage(response);
        }
    }

    public sealed class PromptCategoryVersionRow { public string Version { get; set; } }
    public class PromptCategoryMixRow
    {
        [JsonProperty("categoryId")] public string CategoryId { get; set; }
        [JsonProperty("prompts")] public long Prompts { get; set; }
    }
    public sealed class PromptCategoryTrendRow : PromptCategoryMixRow { [JsonProperty("weekStart")] public DateTime WeekStart { get; set; } }
}
