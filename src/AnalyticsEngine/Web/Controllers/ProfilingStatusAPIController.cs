using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Linq;
using System.Runtime.Caching;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Profiling state for the SPA's Profiling tab. Admins frequently ask whether the profiling
    /// runbooks have run, how fresh the data is, or whether there's been an error - this surfaces
    /// the earliest/latest dates per profiling &amp; source table, plus the profiling trace log.
    ///
    /// The profiling tables (<c>profiling.*</c>) are created by the runbook SQL, not by EF, so they
    /// aren't entities - we query them with raw SQL. Every query is wrapped so that a missing table
    /// (e.g. the runbooks have never run) shows up as a per-row error rather than failing the whole
    /// request: that "table doesn't exist" state IS the diagnostic an admin is looking for.
    /// </summary>
    [Authorize]
    [RoutePrefix("api/ProfilingStatus")]
    public class ProfilingStatusAPIController : ApiController
    {
        private const int DefaultPageSize = 50;
        private const int MaxPageSize = 200;
        private const string FreshnessCacheKey = "ProfilingStatus::Freshness";

        // Per-query SQL timeout for the freshness MIN/MAX queries. AnalyticsEntitiesContext sets an
        // infinite command timeout (for long importer/migration work), but here a single slow scan
        // would otherwise run until Azure App Service kills the HTTP request (~230s) -> 500. Cap each
        // query so it degrades to a per-row error instead, and run them in parallel so one heavy
        // table (e.g. the Copilot/audit_events join) can't push the whole page past the limit.
        private const int FreshnessQueryTimeoutSecs = 20;

        // Raw activity-log tables that feed the profiling compile. All inherit a [date] column with
        // an IX_date index, so MIN/MAX are cheap index seeks even on a ~200k-user tenant. The first
        // six are the headline workloads; teams_user_device_usage_log and platform_user_activity_log
        // also feed the compile (see Profiling-03-CreateSchema.sql) so they're included too.
        private static readonly DateRangeStatSource[] ActivityTables =
        {
            new DateRangeStatSource("teams-user-activity", "Teams user activity", "[dbo].[teams_user_activity_log]"),
            new DateRangeStatSource("teams-device-usage", "Teams user device usage", "[dbo].[teams_user_device_usage_log]"),
            new DateRangeStatSource("outlook-user-activity", "Outlook (email) user activity", "[dbo].[outlook_user_activity_log]"),
            new DateRangeStatSource("onedrive-user-activity", "OneDrive user activity", "[dbo].[onedrive_user_activity_log]"),
            new DateRangeStatSource("sharepoint-user-activity", "SharePoint user activity", "[dbo].[sharepoint_user_activity_log]"),
            new DateRangeStatSource("yammer-user-activity", "Viva Engage (Yammer) user activity", "[dbo].[yammer_user_activity_log]"),
            new DateRangeStatSource("yammer-device-activity", "Viva Engage (Yammer) device activity", "[dbo].[yammer_device_activity_log]"),
            new DateRangeStatSource("platform-user-activity", "Microsoft 365 apps platform user activity", "[dbo].[platform_user_activity_log]"),
        };

        // GET: api/ProfilingStatus
        // Earliest/latest dates for the compiled profiling tables and the source activity tables.
        // Cached briefly (like the home-page counts) because some of these MIN/MAX can scan on big
        // tenants and the freshness only needs to be roughly current.
        [HttpGet]
        [Route("")]
        public async Task<IHttpActionResult> Get()
        {
            if (MemoryCache.Default.Get(FreshnessCacheKey) is ProfilingStatusModel cached)
            {
                return Ok(cached);
            }

            var model = new ProfilingStatusModel();

            // Each query opens its own short-timeout context and they run in parallel: EF6 contexts
            // aren't thread-safe so we can't share one across concurrent queries, and parallelism
            // keeps the worst case ~FreshnessQueryTimeoutSecs (not the sum of all of them).

            // Compiled profiling output tables (built by the runbooks). Note the date column differs:
            // ActivitiesWeekly uses MetricDate, the other two use [date].
            var compiledTasks = new[]
            {
                GetRangeAsync("weekly-activities", "Weekly activities (rows)", "[profiling].[ActivitiesWeekly]", "[MetricDate]"),
                GetRangeAsync("weekly-activity-columns", "Weekly activities (columns)", "[profiling].[ActivitiesWeeklyColumns]", "[date]"),
                GetRangeAsync("weekly-usage", "Weekly usage", "[profiling].[UsageWeekly]", "[date]"),
            };

            var activityTasks = ActivityTables
                .Select(t => GetRangeAsync(t.Key, t.Label, t.Table, "[date]"))
                .ToList();

            // Copilot interactions feed the compile too (usp_UpsertCopilot) but have no date column of
            // their own - the runbook dates them by the joined audit event's time_stamp, so we mirror
            // that. This join MIN/MAX is the heaviest of these queries on a big tenant (audit_events is
            // large), which is exactly why the timeout above matters: it degrades to a per-row error.
            var copilotTask = RunRangeAsync("copilot-interactions", "Copilot interactions",
                "dbo.copilot_chats (via dbo.audit_events)",
                "SELECT MIN(au.time_stamp) AS MinDate, MAX(au.time_stamp) AS MaxDate " +
                "FROM dbo.copilot_chats AS c JOIN dbo.audit_events AS au ON c.event_id = au.id;");

            await Task.WhenAll(compiledTasks.Concat(activityTasks).Concat(new[] { copilotTask }));

            model.CompiledProfiling.AddRange(compiledTasks.Select(t => t.Result));
            model.ActivityTables.AddRange(activityTasks.Select(t => t.Result));
            model.ActivityTables.Add(copilotTask.Result);

            // Cache even when some rows errored/timed out so reloads are instant rather than
            // re-running the slow scans; the per-row error itself is the diagnostic.
            MemoryCache.Default.Set(FreshnessCacheKey, model, DateTimeOffset.UtcNow.AddSeconds(60));
            return Ok(model);
        }

        // GET: api/ProfilingStatus/tracelogs?page=0&pageSize=50
        // A page of profiling.TraceLogs (the runbooks' own trace output), newest first.
        [HttpGet]
        [Route("tracelogs")]
        public async Task<IHttpActionResult> TraceLogs(int page = 0, int pageSize = DefaultPageSize)
        {
            if (page < 0) page = 0;
            if (pageSize < 1) pageSize = DefaultPageSize;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;

            var model = new TraceLogPageModel { Page = page, PageSize = pageSize };

            using (var db = new AnalyticsEntitiesContext())
            {
                try
                {
                    model.TotalCount = (await db.Database
                        .SqlQuery<int>("SELECT COUNT(*) FROM profiling.TraceLogs;")
                        .ToListAsync()).FirstOrDefault();

                    // Use a long offset so a large page index can't overflow int.
                    var offset = new SqlParameter("@offset", (long)page * pageSize);
                    var fetch = new SqlParameter("@fetch", pageSize);

                    // ORDER BY Id DESC = newest first (Id is a monotonic IDENTITY); OFFSET/FETCH pages it.
                    var rows = await db.Database.SqlQuery<TraceLogRow>(
                        "SELECT Id, [Datetime], Message FROM profiling.TraceLogs ORDER BY Id DESC OFFSET @offset ROWS FETCH NEXT @fetch ROWS ONLY;",
                        offset, fetch).ToListAsync();

                    model.Rows = rows
                        .Select(r => new TraceLogEntryModel { Id = r.Id, Datetime = r.Datetime, Message = r.Message })
                        .ToList();
                }
                catch (Exception ex)
                {
                    // Most likely the profiling schema doesn't exist yet (runbooks never ran).
                    model.Error = InnermostMessage(ex);
                }
            }

            return Ok(model);
        }

        /// <summary>
        /// Runs a MIN/MAX-date query against one table and packages the result (or the error). The
        /// table/column names are compile-time constants, not user input, so there's no injection risk.
        /// </summary>
        private static Task<DateRangeStatModel> GetRangeAsync(string key, string label, string table, string dateColumn)
        {
            var sql = $"SELECT MIN({dateColumn}) AS MinDate, MAX({dateColumn}) AS MaxDate FROM {table};";
            return RunRangeAsync(key, label, table, sql);
        }

        /// <summary>
        /// Runs an arbitrary "SELECT MIN(...) AS MinDate, MAX(...) AS MaxDate ..." query (used for
        /// sources whose date isn't a plain column, e.g. Copilot, which is dated via a joined audit
        /// event) and packages the result or the error. SQL is built from constants, not user input.
        /// Opens its own context with a short command timeout so a slow scan can't hang the request.
        /// </summary>
        private static async Task<DateRangeStatModel> RunRangeAsync(string key, string label, string table, string sql)
        {
            var stat = new DateRangeStatModel { Key = key, Label = label, Table = table, Sql = sql };

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.Database.CommandTimeout = FreshnessQueryTimeoutSecs;
                    var result = (await db.Database.SqlQuery<DateRangeResult>(sql).ToListAsync()).FirstOrDefault();
                    if (result != null)
                    {
                        stat.From = result.MinDate;
                        stat.To = result.MaxDate;
                    }
                }
            }
            catch (Exception ex)
            {
                stat.Error = InnermostMessage(ex);
            }

            return stat;
        }

        /// <summary>EF wraps SQL errors; the innermost message (the SqlException) is the useful one.</summary>
        private static string InnermostMessage(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e.Message;
        }

        /// <summary>A profiling/activity table to report a date range for.</summary>
        private sealed class DateRangeStatSource
        {
            public DateRangeStatSource(string key, string label, string table)
            {
                Key = key;
                Label = label;
                Table = table;
            }

            public string Key { get; }
            public string Label { get; }
            public string Table { get; }
        }

        /// <summary>Shape for the MIN/MAX-date raw SQL result (columns aliased MinDate/MaxDate).</summary>
        private sealed class DateRangeResult
        {
            public DateTime? MinDate { get; set; }
            public DateTime? MaxDate { get; set; }
        }

        /// <summary>Shape for a raw profiling.TraceLogs row.</summary>
        private sealed class TraceLogRow
        {
            public long Id { get; set; }
            public DateTime Datetime { get; set; }
            public string Message { get; set; }
        }
    }
}
