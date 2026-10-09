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
        private readonly PromptCategoryConfigurationStore _store;
        private readonly AppConfig _settings;

        public PromptCategoriesAPIController() : this(new AppConfig()) { }
        private PromptCategoriesAPIController(AppConfig settings)
        {
            _settings = settings;
            _store = PromptCategoryConfigurationStore.Open(settings);
        }
        internal PromptCategoriesAPIController(PromptCategoryConfigurationStore store)
        {
            _store = store;
            _settings = new AppConfig();
        }

        [HttpGet, Route("")]
        [RequirePortalPermission(PortalPermission.Administration)]
        public async Task<IHttpActionResult> Get()
        {
            try
            {
                return NoStore(new
                {
                    configuration = await _store.GetAsync(),
                    storageAvailable = _store.IsDurable,
                    backendConfigured = FoundryPromptSettings.IsConfigured(_settings)
                });
            }
            catch { return StatusCode(HttpStatusCode.ServiceUnavailable); }
        }

        [HttpPost, Route("")]
        [RequirePortalPermission(PortalPermission.Administration)]
        [RequireSameOriginXhr]
        public async Task<IHttpActionResult> Save([FromBody] PromptCategoryConfiguration configuration)
        {
            if (configuration == null) return BadRequest();
            try { return NoStore(await _store.SaveAsync(configuration)); }
            catch (ArgumentException) { return BadRequest(); }
            catch { return StatusCode(HttpStatusCode.ServiceUnavailable); }
        }

        [HttpPost, Route("reset")]
        [RequirePortalPermission(PortalPermission.Administration)]
        [RequireSameOriginXhr]
        public async Task<IHttpActionResult> Reset()
        {
            try { return NoStore(await _store.SaveAsync(PromptCategoryConfiguration.Defaults())); }
            catch { return StatusCode(HttpStatusCode.ServiceUnavailable); }
        }

        [HttpGet, Route("runs")]
        [RequirePortalPermission(PortalPermission.Administration)]
        public async Task<IHttpActionResult> Runs()
        {
            try
            {
                var rows = await PromptCategoryRunStore.Open(_settings).RecentAsync();
                return NoStore(rows.Select(row => new
                {
                    startedUtc = DateTime.SpecifyKind(row.StartedUtc, DateTimeKind.Utc),
                    counters = row.Counters
                }).ToArray());
            }
            catch { return StatusCode(HttpStatusCode.ServiceUnavailable); }
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
            catch { return StatusCode(HttpStatusCode.ServiceUnavailable); }
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
