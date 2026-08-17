using Common.Entities;
using Common.Entities.Config;
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
    /// Powers the SPA's "Reports" tab - a lightweight, in-app version of the Power BI reports that
    /// needs no extra deployment. Each report area maps to an import: the area is only offered when
    /// that import is enabled (<see cref="Areas"/>), and each area returns a small set of weekly
    /// time-series / bar charts over a configurable window (default 3 months).
    ///
    /// Design notes:
    /// - Queries hit the BASE tables directly (never the vw* views, which don't scale and are being
    ///   removed). All aggregation (weekly bucketing, distinct counts) happens in SQL.
    /// - Every chart runs on its own short-timeout context, in parallel, and degrades to a per-chart
    ///   error rather than failing the whole area - the heavy tables (hits, audit_events) have no
    ///   dedicated date index, so a weekly GROUP BY can scan on a large tenant. The result is cached
    ///   briefly (like the home-page counts) so reloads are instant.
    /// - Weeks are bucketed to their Monday (matching the C# spine) with day arithmetic:
    ///   DATEADD(DAY, -(DATEDIFF(DAY, 0, col) % 7), CAST(col AS date)). 1900-01-01 is a Monday,
    ///   so "days since then % 7" is 0 on Mondays; this is independent of DATEFIRST / language.
    ///   (SQL Server's DATEDIFF(WEEK, ...) instead splits weeks on Sunday, which would push each
    ///   Sunday's rows into the next week's bucket, so we avoid it.)
    /// </summary>
    [Authorize]
    [RoutePrefix("api/Reports")]
    public class ReportsAPIController : ApiController
    {
        // A single slow weekly scan would otherwise run until Azure App Service kills the HTTP
        // request (~230s) -> 500. Cap each query so it degrades to a per-chart error instead.
        private const int QueryTimeoutSecs = 25;

        private const int DefaultMonths = 3;

        // This is a "basic activity" view, not a full analytics tool, so the window is capped at 6
        // months. That keeps the charts light and, once the base-table date columns are indexed,
        // makes the window a real cost lever (a shorter period then reads proportionally less).
        private const int MaxMonths = 6;

        private const string CacheKeyPrefix = "Reports::Area::";

        // Microsoft 365 usage reports are published a couple of days in arrears, and Graph keeps
        // back-filling a report date for a short while after it first appears. Snapshots newer than
        // this many days are therefore ignored by the usage charts: including a half-populated
        // snapshot makes the most recent week look like a sudden collapse in activity.
        private const int UsageReportLagDays = 3;

        // The Copilot charts join copilot_chats to audit_events. Both are clustered on GUIDs, so an
        // INNER MERGE JOIN looks attractive for wide windows - but it was measured and it is not.
        //
        // Benchmarked at 4m audit_events rows (synthetic, 2 years), medians of 4 warm runs with the plan
        // cache cleared, comparing the natural join against a forced INNER MERGE JOIN:
        //
        //   window | natural reads | natural ms | forced MERGE reads | forced MERGE ms
        //   -------+---------------+------------+--------------------+----------------
        //    15d   |         4,206 |        729 |              4,206 |            959
        //    30d   |         5,210 |        816 |             73,857 |            952
        //    45d   |         6,009 |        945 |             73,857 |          1,051
        //    90d   |         7,884 |      1,134 |             73,857 |          1,177
        //   180d   |        10,639 |      1,454 |             73,857 |          1,182
        //   365d   |        14,799 |      1,899 |             73,857 |          1,372
        //
        // The forced merge join degrades to a FULL SCAN of audit_events at every window (a flat 73,857
        // reads), because a merge needs both inputs sorted on the join key and the index seek returns
        // time_stamp order, not GUID order. It buys at most ~0.5s of elapsed time beyond ~180 days and
        // costs 5-12x the logical reads to get it. Logical reads are the primary signal here (stable
        // across machines, and what actually bites under concurrent report load), so the hint loses.
        //
        // Hence: always use the natural join and let the optimiser pick, with OPTION (RECOMPILE) so the
        // real window drives the plan. Do not reintroduce a join hint without re-measuring.
        internal const string CopilotAuditNaturalJoin =
            "INNER JOIN dbo.audit_events AS au ON c.event_id = au.id";

        // GET: api/Reports/areas
        // Which report areas are available, based on the enabled imports.
        [HttpGet]
        [Route("areas")]
        public IHttpActionResult Areas()
        {
            var s = new AppConfig().ImportJobSettings ?? new ImportTaskSettings();

            return Ok(new ReportAreasModel
            {
                Copilot = s.Copilot,
                Usage = s.GraphUsageReports,
                SpoAudit = s.ActivityLog,
                WebTraffic = s.WebTraffic,
                Calls = s.Calls,
                Emails = s.SentEmails,
            });
        }

        // GET: api/Reports/copilot?months=3
        [HttpGet]
        [Route("copilot")]
        public Task<IHttpActionResult> Copilot(int months = DefaultMonths) => AreaAsync("copilot", months);

        // GET: api/Reports/copilot-agents?months=3
        [HttpGet]
        [Route("copilot-agents")]
        public Task<IHttpActionResult> CopilotAgents(
            int months = DefaultMonths,
            int top = 8,
            string agentName = null) => AreaAsync("copilot-agents", months, top, agentName);

        // GET: api/Reports/usage?months=3
        [HttpGet]
        [Route("usage")]
        public Task<IHttpActionResult> Usage(int months = DefaultMonths) => AreaAsync("usage", months);

        // GET: api/Reports/spo-audit?months=3
        [HttpGet]
        [Route("spo-audit")]
        public Task<IHttpActionResult> SpoAudit(int months = DefaultMonths) => AreaAsync("spo-audit", months);

        // GET: api/Reports/web-traffic?months=3
        [HttpGet]
        [Route("web-traffic")]
        public Task<IHttpActionResult> WebTraffic(int months = DefaultMonths) => AreaAsync("web-traffic", months);

        // GET: api/Reports/calls?months=3
        [HttpGet]
        [Route("calls")]
        public Task<IHttpActionResult> Calls(int months = DefaultMonths) => AreaAsync("calls", months);

        // GET: api/Reports/emails?months=3
        [HttpGet]
        [Route("emails")]
        public Task<IHttpActionResult> Emails(int months = DefaultMonths) => AreaAsync("emails", months);

        /// <summary>
        /// Builds (or serves from cache) the charts for one area over the requested window.
        /// </summary>
        private async Task<IHttpActionResult> AreaAsync(
            string area,
            int months,
            int topAgents = 8,
            string agentName = null)
        {
            if (months < 1) months = DefaultMonths;
            if (months > MaxMonths) months = MaxMonths;

            if (topAgents < 1) topAgents = 1;
            if (topAgents > 20) topAgents = 20;

            var trimmedAgentName = agentName?.Trim();
            var normalizedAgentName = string.IsNullOrEmpty(trimmedAgentName)
                ? null
                : trimmedAgentName.Substring(0, Math.Min(trimmedAgentName.Length, 100));

            var cacheKey = CacheKeyPrefix + area + "::" + months;
            if (area == "copilot-agents")
            {
                cacheKey += $"::top={topAgents}::agent={normalizedAgentName ?? "(all)"}";
            }

            if (MemoryCache.Default.Get(cacheKey) is ReportAreaData cached)
            {
                return Ok(cached);
            }

            // Monday-aligned window: the first week is the Monday on/before "months ago", and every
            // SQL bucket is Monday-aligned too, so the spine and the data line up exactly.
            var today = DateTime.UtcNow.Date;
            var firstMonday = MondayOf(today.AddMonths(-months));
            // Usage reports arrive a couple of days late, so the current week is always incomplete;
            // plotting it would draw a sharp fall to zero. UsageCharts trims further from the end
            // when the settled report for a week has not reached every workload yet.
            var lastMonday = area == "usage"
                ? MondayOf(today).AddDays(-7)
                : MondayOf(today);
            var weekSpine = WeekSpine(firstMonday, lastMonday);

            var model = new ReportAreaData { Area = area, Months = months, FromWeek = firstMonday };

            List<Task<ReportChart>> chartTasks;
            switch (area)
            {
                case "copilot":
                    chartTasks = CopilotCharts(firstMonday, weekSpine);
                    break;
                case "copilot-agents":
                    chartTasks = CopilotAgentCharts(firstMonday, weekSpine, topAgents, normalizedAgentName);
                    break;
                case "usage":
                    chartTasks = UsageCharts(firstMonday, weekSpine);
                    break;
                case "spo-audit":
                    chartTasks = SpoAuditCharts(firstMonday, weekSpine);
                    break;
                case "web-traffic":
                    chartTasks = WebTrafficCharts(firstMonday, weekSpine);
                    break;
                case "calls":
                    chartTasks = CallsCharts(firstMonday, weekSpine);
                    break;
                case "emails":
                    chartTasks = EmailsCharts(firstMonday, weekSpine);
                    break;
                default:
                    return NotFound();
            }

            await Task.WhenAll(chartTasks);
            model.Charts = chartTasks.Select(t => t.Result).ToList();

            // Cache even when some charts errored so reloads are instant rather than re-running the
            // heavy scans; the per-chart error is itself the diagnostic.
            MemoryCache.Default.Set(cacheKey, model, DateTimeOffset.UtcNow.AddSeconds(60));
            return Ok(model);
        }

        #region Per-area chart definitions

        // Copilot interactions are dated via the joined audit event's time_stamp (copilot_chats has
        // no date column of its own), mirroring the profiling compile.
        private static List<Task<ReportChart>> CopilotCharts(DateTime from, List<DateTime> weekSpine)
        {
            var join = "FROM dbo.copilot_chats AS c "
                + SelectCopilotAuditJoin(from, hasAgentFilter: false)
                + " WHERE au.time_stamp >= @from";

            var wb = WeekBucket("au.time_stamp");

            var interactions =
                $"SELECT {wb} AS WeekStart, CAST(COUNT(*) AS float) AS Value\r\n" +
                join + "\r\n" +
                $"GROUP BY {wb} ORDER BY WeekStart\r\n" +
                "OPTION (RECOMPILE);";

            var users = BuildCopilotUsersQuery(from);

            var hosts =
                "SELECT TOP 8 ISNULL(c.app_host, '(unknown)') AS Label, CAST(COUNT(*) AS float) AS Value\r\n" +
                join + "\r\n" +
                "GROUP BY ISNULL(c.app_host, '(unknown)') ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";

            return new List<Task<ReportChart>>
            {
                RunTimeSeriesAsync("copilot-interactions", "Copilot interactions per week",
                    "Total Microsoft 365 Copilot interactions each week.", "Interactions", "Interactions", interactions, from, weekSpine),
                RunTimeSeriesAsync("copilot-users", "Active Copilot users per week",
                    "Distinct users with at least one Copilot interaction each week.", "Users", "Active users", users, from, weekSpine),
                RunCategoryAsync("copilot-hosts", "Interactions by app",
                    "Where Copilot is being used across the window (top apps).", "Interactions", hosts, from),
            };
        }

        internal static string BuildCopilotUsersQuery(DateTime from, DateTime? today = null)
        {
            var wb = WeekBucket("au.time_stamp");
            return
                $"SELECT {wb} AS WeekStart, CAST(COUNT(DISTINCT au.user_id) AS float) AS Value\r\n" +
                "FROM dbo.copilot_chats AS c "
                + SelectCopilotAuditJoin(from, hasAgentFilter: false, today)
                + " WHERE au.time_stamp >= @from\r\n" +
                $"GROUP BY {wb} ORDER BY WeekStart\r\n" +
                "OPTION (RECOMPILE);";
        }

        internal static string SelectCopilotAuditJoin(
            DateTime from,
            bool hasAgentFilter,
            DateTime? today = null)
        {
            // Always the natural join - see the measurements on CopilotAuditNaturalJoin. The parameters
            // are kept so callers (and tests) still express intent, and so a future, re-measured rule
            // has somewhere to live.
            return CopilotAuditNaturalJoin;
        }

        private static List<Task<ReportChart>> CopilotAgentCharts(
            DateTime from,
            List<DateTime> weekSpine,
            int topAgents,
            string agentName)
        {
            var wb = WeekBucket("au.time_stamp");
            var auditJoin = SelectCopilotAuditJoin(
                from,
                hasAgentFilter: !string.IsNullOrEmpty(agentName));
            var agents =
                "WITH EligibleAgents AS (\r\n" +
                "    SELECT id\r\n" +
                "    FROM dbo.copilot_agents\r\n" +
                "    WHERE @agentName IS NULL OR CHARINDEX(@agentName, ISNULL(name, '')) > 0\r\n" +
                "),\r\n" +
                "AgentWeeks AS (\r\n" +
                "    SELECT c.agent_id AS AgentKey,\r\n" +
                $"           {wb} AS WeekStart,\r\n" +
                "           COUNT_BIG(*) AS InteractionCount\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    " + auditJoin + "\r\n" +
                "    JOIN EligibleAgents AS eligible ON c.agent_id = eligible.id\r\n" +
                "    WHERE au.time_stamp >= @from\r\n" +
                $"    GROUP BY c.agent_id, {wb}\r\n" +
                "),\r\n" +
                "AgentWeeksWithTotals AS (\r\n" +
                "    SELECT AgentKey, WeekStart, InteractionCount,\r\n" +
                "           SUM(InteractionCount) OVER (PARTITION BY AgentKey) AS TotalInteractions\r\n" +
                "    FROM AgentWeeks\r\n" +
                "),\r\n" +
                "RankedAgentWeeks AS (\r\n" +
                "    SELECT AgentKey, WeekStart, InteractionCount,\r\n" +
                "           DENSE_RANK() OVER (ORDER BY TotalInteractions DESC, AgentKey) AS PopularityRank\r\n" +
                "    FROM AgentWeeksWithTotals\r\n" +
                ")\r\n" +
                "SELECT ranked.AgentKey AS SeriesKey,\r\n" +
                "       COALESCE(NULLIF(LTRIM(RTRIM(agent.name)), ''), NULLIF(LTRIM(RTRIM(agent.agent_id)), ''), '(unnamed agent)') AS SeriesName,\r\n" +
                "       ranked.WeekStart,\r\n" +
                "       CAST(ranked.InteractionCount AS float) AS Value\r\n" +
                "FROM RankedAgentWeeks AS ranked\r\n" +
                "JOIN dbo.copilot_agents AS agent ON ranked.AgentKey = agent.id\r\n" +
                $"WHERE ranked.PopularityRank <= {topAgents}\r\n" +
                "ORDER BY SeriesName, WeekStart\r\n" +
                "OPTION (RECOMPILE);";

            var displaySql = DisplaySql(agents, from, agentName);
            var rowsTask = QueryNamedWeeksAsync(agents, from, agentName, 60);

            return new List<Task<ReportChart>>
            {
                RunGroupedCategoryAsync("copilot-agent-totals", "Top Copilot agents by total executions",
                    "Total executions for each selected agent across the reporting period.",
                    "Executions", rowsTask, displaySql),
                RunGroupedTimeSeriesAsync("copilot-agent-interactions", "Copilot agent interactions per week",
                    "Weekly interactions for the most-used Copilot agents matching the current filters.",
                    "Interactions", rowsTask, displaySql, weekSpine),
            };
        }

        // One line per workload. Each user activity-log table holds one row per user per report
        // date, so a single report date is a complete snapshot of "who was active recently".
        //
        // For each week we therefore read ONE snapshot rather than every daily snapshot in the week:
        // the most recent report date that falls inside the week AND is old enough for Graph to have
        // settled it (@settled). A user counts as active in that week when their last_activity_date
        // falls inside the week.
        //
        // Picking the LATEST snapshot in the week (rather than requiring the week's final day) keeps
        // the chart working when an individual daily import is missed - a week has seven chances to
        // supply a snapshot instead of one. Weeks with no settled snapshot at all return no row, and
        // are rendered as a gap rather than as zero.
        private static List<Task<ReportChart>> UsageCharts(DateTime from, List<DateTime> weekSpine)
        {
            var workloads = new[]
            {
                new { Table = "dbo.teams_user_activity_log", Name = "Teams" },
                new { Table = "dbo.outlook_user_activity_log", Name = "Outlook" },
                new { Table = "dbo.onedrive_user_activity_log", Name = "OneDrive" },
                new { Table = "dbo.sharepoint_user_activity_log", Name = "SharePoint" },
                new { Table = "dbo.yammer_user_activity_log", Name = "Viva Engage" },
            };

            var series = workloads.Select(w => new SeriesQuery
            {
                Name = w.Name,
                Body =
                    "WITH WeekStarts AS (\r\n" +
                    "    SELECT @from AS WeekStart\r\n" +
                    "    UNION ALL\r\n" +
                    "    SELECT DATEADD(DAY, 7, WeekStart)\r\n" +
                    "    FROM WeekStarts\r\n" +
                    "    WHERE WeekStart < @through\r\n" +
                    "),\r\n" +
                    "-- The latest settled report date inside each week; NULL when that week never\r\n" +
                    "-- got a usage report, in which case the week is omitted (drawn as a gap).\r\n" +
                    "WeekSnapshots AS (\r\n" +
                    "    SELECT weeks.WeekStart, snapshot.SnapshotDate\r\n" +
                    "    FROM WeekStarts AS weeks\r\n" +
                    "    CROSS APPLY (\r\n" +
                    $"        SELECT MAX(available.[date]) AS SnapshotDate\r\n" +
                    $"        FROM {w.Table} AS available\r\n" +
                    "        WHERE available.[date] >= weeks.WeekStart\r\n" +
                    "          AND available.[date] < DATEADD(DAY, 7, weeks.WeekStart)\r\n" +
                    "          AND available.[date] <= @settled\r\n" +
                    "    ) AS snapshot\r\n" +
                    "    WHERE snapshot.SnapshotDate IS NOT NULL\r\n" +
                    ")\r\n" +
                    "SELECT weeks.WeekStart, CAST(COUNT(DISTINCT activity.user_id) AS float) AS Value\r\n" +
                    "FROM WeekSnapshots AS weeks\r\n" +
                    $"LEFT JOIN {w.Table} AS activity\r\n" +
                    "    ON activity.[date] = weeks.SnapshotDate\r\n" +
                    "   AND activity.last_activity_date >= weeks.WeekStart\r\n" +
                    "   AND activity.last_activity_date < DATEADD(DAY, 7, weeks.WeekStart)\r\n" +
                    "GROUP BY weeks.WeekStart\r\n" +
                    "ORDER BY weeks.WeekStart\r\n" +
                    // The recursion is bounded by the @through predicate above, so no arbitrary
                    // depth cap is needed (a cap silently errors if the window is ever widened).
                    "OPTION (MAXRECURSION 0);"
            }).ToList();

            return new List<Task<ReportChart>>
            {
                RunMultiTimeSeriesAsync("usage-active-users", "Weekly active users by workload",
                    "Distinct users active in each completed week. Weeks with no usage report yet are left blank rather than shown as zero.",
                    "Active users", series, from, weekSpine),
            };
        }

        // SharePoint / OneDrive audit events = audit_events joined to event_meta_sharepoint (which is
        // what makes an audit event a SharePoint one; audit_events also holds other workloads).
        private static List<Task<ReportChart>> SpoAuditCharts(DateTime from, List<DateTime> weekSpine)
        {
            const string join = "FROM dbo.audit_events AS au JOIN dbo.event_meta_sharepoint AS sp ON au.id = sp.event_id WHERE au.time_stamp >= @from";

            var wb = WeekBucket("au.time_stamp");
            var ops =
                $"SELECT {wb} AS WeekStart, CAST(COUNT(*) AS float) AS Value\r\n" +
                join + "\r\n" +
                $"GROUP BY {wb} ORDER BY WeekStart;";

            // Aggregate by the operation FOREIGN KEY first, then resolve the ten winning names from
            // the small lookup table. Joining event_operations up-front instead would resolve a name
            // for every audit event in the window (millions of lookups) just to group them again.
            var byType =
                "WITH OperationTotals AS (\r\n" +
                "    SELECT TOP 10 au.operation_id, COUNT_BIG(*) AS Operations\r\n" +
                "    FROM dbo.audit_events AS au\r\n" +
                "    JOIN dbo.event_meta_sharepoint AS sp ON au.id = sp.event_id\r\n" +
                "    WHERE au.time_stamp >= @from\r\n" +
                "    GROUP BY au.operation_id\r\n" +
                "    ORDER BY Operations DESC\r\n" +
                ")\r\n" +
                "SELECT ISNULL(eo.operation_name, '(unknown)') AS Label, CAST(totals.Operations AS float) AS Value\r\n" +
                "FROM OperationTotals AS totals\r\n" +
                "LEFT JOIN dbo.event_operations AS eo ON totals.operation_id = eo.id\r\n" +
                "ORDER BY Value DESC;";

            return new List<Task<ReportChart>>
            {
                RunTimeSeriesAsync("spo-operations", "File activity per week",
                    "SharePoint & OneDrive audit operations each week.", "Operations", "Operations", ops, from, weekSpine),
                RunCategoryAsync("spo-by-type", "Activity by operation",
                    "The most common SharePoint & OneDrive operations across the window.", "Operations", byType, from),
            };
        }

        // Web traffic captured by the page tracker: hits (page views), joined to sessions -> users
        // for distinct visitors.
        private static List<Task<ReportChart>> WebTrafficCharts(DateTime from, List<DateTime> weekSpine)
        {
            var wbHits = WeekBucket("hit_timestamp");
            var views =
                $"SELECT {wbHits} AS WeekStart, CAST(COUNT(*) AS float) AS Value\r\n" +
                "FROM dbo.hits WHERE hit_timestamp >= @from\r\n" +
                $"GROUP BY {wbHits} ORDER BY WeekStart;";

            var wbH = WeekBucket("h.hit_timestamp");
            var visitors =
                $"SELECT {wbH} AS WeekStart, CAST(COUNT(DISTINCT s.user_id) AS float) AS Value\r\n" +
                "FROM dbo.hits AS h JOIN dbo.sessions AS s ON h.session_id = s.id WHERE h.hit_timestamp >= @from\r\n" +
                $"GROUP BY {wbH} ORDER BY WeekStart;";

            return new List<Task<ReportChart>>
            {
                RunTimeSeriesAsync("web-page-views", "Page views per week",
                    "Total tracked page views each week.", "Page views", "Page views", views, from, weekSpine),
                RunTimeSeriesAsync("web-visitors", "Unique visitors per week",
                    "Distinct users seen on tracked pages each week.", "Visitors", "Unique visitors", visitors, from, weekSpine),
            };
        }

        private static List<Task<ReportChart>> CallsCharts(DateTime from, List<DateTime> weekSpine)
        {
            var wb = WeekBucket("[start]");
            var calls =
                $"SELECT {wb} AS WeekStart, CAST(COUNT(*) AS float) AS Value\r\n" +
                "FROM dbo.call_records WHERE [start] >= @from\r\n" +
                $"GROUP BY {wb} ORDER BY WeekStart;";

            var minutes =
                $"SELECT {wb} AS WeekStart, CAST(SUM(CAST(DATEDIFF(SECOND, [start], [end]) AS bigint)) / 60.0 AS float) AS Value\r\n" +
                "FROM dbo.call_records WHERE [start] >= @from\r\n" +
                $"GROUP BY {wb} ORDER BY WeekStart;";

            return new List<Task<ReportChart>>
            {
                RunTimeSeriesAsync("calls-count", "Teams calls per week",
                    "Number of Teams calls started each week.", "Calls", "Calls", calls, from, weekSpine),
                RunTimeSeriesAsync("calls-minutes", "Call minutes per week",
                    "Total Teams call duration each week (minutes).", "Minutes", "Minutes", minutes, from, weekSpine),
            };
        }

        private static List<Task<ReportChart>> EmailsCharts(DateTime from, List<DateTime> weekSpine)
        {
            var wb = WeekBucket("sent_date");
            var emails =
                $"SELECT {wb} AS WeekStart, CAST(COUNT(*) AS float) AS Value\r\n" +
                "FROM dbo.sent_emails WHERE sent_date >= @from\r\n" +
                $"GROUP BY {wb} ORDER BY WeekStart;";

            return new List<Task<ReportChart>>
            {
                RunTimeSeriesAsync("emails-sent", "Emails sent per week",
                    "Sent emails imported from mailboxes each week.", "Emails", "Emails sent", emails, from, weekSpine),
            };
        }

        #endregion

        #region Query runners

        /// <summary>Runs a single-series weekly query and gap-fills missing weeks with zero.</summary>
        private static async Task<ReportChart> RunTimeSeriesAsync(string key, string title, string description,
            string valueLabel, string seriesName, string body, DateTime from, List<DateTime> weekSpine)
        {
            var chart = new ReportChart
            {
                Key = key,
                Title = title,
                Description = description,
                Type = "timeseries",
                ValueLabel = valueLabel,
                Sql = DisplaySql(body, from),
            };

            try
            {
                var rows = await QueryWeeksAsync(body, from);
                chart.Series = new List<ReportSeries>
                {
                    new ReportSeries { Name = seriesName, Points = FillWeeks(weekSpine, rows) },
                };
            }
            catch (Exception ex)
            {
                chart.Error = InnermostMessage(ex);
            }

            return chart;
        }

        /// <summary>Runs one weekly query per series and combines them into a single multi-line chart.</summary>
        private static async Task<ReportChart> RunMultiTimeSeriesAsync(string key, string title, string description,
            string valueLabel, List<SeriesQuery> series, DateTime from, List<DateTime> weekSpine)
        {
            var chart = new ReportChart
            {
                Key = key,
                Title = title,
                Description = description,
                Type = "timeseries",
                ValueLabel = valueLabel,
            };

            if (weekSpine.Count == 0)
            {
                chart.Series = series.Select(s => new ReportSeries { Name = s.Name }).ToList();
                return chart;
            }

            var lastWeek = weekSpine[weekSpine.Count - 1];
            var settled = SettledThrough();

            // Show one representative query; each series uses the same shape against its own table.
            chart.Sql = "-- One query per workload (below is representative); the chart runs one per series.\r\n" +
                        DisplaySql(series.First().Body, from, lastWeek, settled);

            var rowsBySeries = new List<KeyValuePair<string, List<WeekValueRow>>>();
            var warnings = new List<string>();

            // Sequentially per series keeps a bounded number of concurrent contexts (the area itself
            // already runs in parallel with other areas' charts); each performs indexed seeks into
            // one snapshot per week.
            foreach (var sq in series)
            {
                try
                {
                    var rows = await QueryWeeksAsync(sq.Body, from, lastWeek, settled);
                    rowsBySeries.Add(new KeyValuePair<string, List<WeekValueRow>>(sq.Name, rows));
                }
                catch (Exception ex)
                {
                    warnings.Add($"{sq.Name}: {InnermostMessage(ex)}");
                    if (IsQueryTimeout(ex))
                    {
                        warnings.Add("Remaining workloads were not attempted after the database timeout");
                        break;
                    }
                }
            }

            return CompleteMultiTimeSeries(chart, rowsBySeries, warnings, weekSpine);
        }

        internal static ReportChart CompleteMultiTimeSeries(
            ReportChart chart,
            List<KeyValuePair<string, List<WeekValueRow>>> rowsBySeries,
            List<string> warnings,
            List<DateTime> weekSpine)
        {
            var populatedSeries = rowsBySeries
                .Where(seriesRows => seriesRows.Value.Count > 0)
                .ToList();

            warnings.AddRange(rowsBySeries
                .Where(seriesRows => seriesRows.Value.Count == 0)
                .Select(seriesRows => $"{seriesRows.Key}: no settled usage data"));

            if (populatedSeries.Count == 0)
            {
                chart.Error = warnings.Count == 0
                    ? "No completed usage-report weeks are available."
                    : "No workload series could be loaded. " + string.Join("; ", warnings);
                return chart;
            }

            // Trailing weeks are trimmed once, across every workload, so the chart ends where the
            // slowest populated report has caught up. Workloads with no settled data are omitted
            // rather than preventing every other workload from rendering. Interior weeks are NOT
            // trimmed: they keep their place on the spine and any workload missing that week gets a
            // null (a gap in its line) rather than a zero, which would draw a false collapse.
            var lastCompleteWeekIndex = weekSpine.Count - 1;
            while (lastCompleteWeekIndex >= 0)
            {
                var week = weekSpine[lastCompleteWeekIndex];
                if (populatedSeries.All(s => s.Value.Any(r => r.WeekStart.Date == week)))
                {
                    break;
                }
                lastCompleteWeekIndex--;
            }

            if (lastCompleteWeekIndex < 0)
            {
                chart.Error = "No completed usage-report weeks are available for workloads with data.";
                return chart;
            }

            var completeWeekSpine = weekSpine.Take(lastCompleteWeekIndex + 1).ToList();
            chart.Series = populatedSeries
                .Select(s => new ReportSeries
                {
                    Name = s.Key,
                    Points = FillWeeks(completeWeekSpine, s.Value, missingValue: null),
                })
                .ToList();

            if (warnings.Count > 0)
            {
                chart.Warning = "Some workload series are unavailable: " + string.Join("; ", warnings) + ".";
            }

            return chart;
        }

        /// <summary>Runs one query that returns multiple named weekly series.</summary>
        private static async Task<ReportChart> RunGroupedTimeSeriesAsync(string key, string title, string description,
            string valueLabel, Task<List<NamedWeekValueRow>> rowsTask, string sql, List<DateTime> weekSpine)
        {
            var chart = new ReportChart
            {
                Key = key,
                Title = title,
                Description = description,
                Type = "timeseries",
                ValueLabel = valueLabel,
                Sql = sql,
            };

            try
            {
                var rows = await rowsTask;
                chart.Series = rows
                    .GroupBy(r => new { r.SeriesKey, r.SeriesName })
                    .OrderByDescending(g => g.Sum(r => r.Value))
                    .Select(g => new ReportSeries
                    {
                        Name = g.Key.SeriesName,
                        Points = FillWeeks(
                            weekSpine,
                            g.Select(r => new WeekValueRow { WeekStart = r.WeekStart, Value = r.Value }).ToList()),
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                chart.Error = InnermostMessage(ex);
            }

            return chart;
        }

        /// <summary>Builds a categorical totals chart from the same shared weekly query result.</summary>
        private static async Task<ReportChart> RunGroupedCategoryAsync(string key, string title, string description,
            string valueLabel, Task<List<NamedWeekValueRow>> rowsTask, string sql)
        {
            var chart = new ReportChart
            {
                Key = key,
                Title = title,
                Description = description,
                Type = "bar",
                ValueLabel = valueLabel,
                Sql = sql,
            };

            try
            {
                var rows = await rowsTask;
                chart.Categories = rows
                    .GroupBy(r => new { r.SeriesKey, r.SeriesName })
                    .Select(g => new ReportCategory
                    {
                        Label = g.Key.SeriesName,
                        Value = g.Sum(r => r.Value),
                    })
                    .OrderByDescending(category => category.Value)
                    .ToList();
            }
            catch (Exception ex)
            {
                chart.Error = InnermostMessage(ex);
            }

            return chart;
        }

        private static async Task<List<NamedWeekValueRow>> QueryNamedWeeksAsync(
            string body,
            DateTime from,
            string agentName,
            int queryTimeoutSecs)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.CommandTimeout = queryTimeoutSecs;
                return await db.Database
                    .SqlQuery<NamedWeekValueRow>(
                        body,
                        new SqlParameter("@from", from),
                        new SqlParameter("@agentName", System.Data.SqlDbType.NVarChar, 100)
                        {
                            Value = (object)agentName ?? DBNull.Value,
                        })
                    .ToListAsync();
            }
        }

        /// <summary>Runs a categorical (bar) query - label + value rows, already ordered by the query.</summary>
        private static async Task<ReportChart> RunCategoryAsync(string key, string title, string description,
            string valueLabel, string body, DateTime from)
        {
            var chart = new ReportChart
            {
                Key = key,
                Title = title,
                Description = description,
                Type = "bar",
                ValueLabel = valueLabel,
                Sql = DisplaySql(body, from),
            };

            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.Database.CommandTimeout = QueryTimeoutSecs;
                    var rows = await db.Database
                        .SqlQuery<CategoryRow>(body, new SqlParameter("@from", from))
                        .ToListAsync();
                    chart.Categories = rows
                        .Select(r => new ReportCategory { Label = r.Label, Value = r.Value })
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                chart.Error = InnermostMessage(ex);
            }

            return chart;
        }

        /// <summary>Executes a weekly (WeekStart, Value) query on its own short-timeout context.</summary>
        private static async Task<List<WeekValueRow>> QueryWeeksAsync(string body, DateTime from)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.CommandTimeout = QueryTimeoutSecs;
                return await db.Database
                    .SqlQuery<WeekValueRow>(body, new SqlParameter("@from", from))
                    .ToListAsync();
            }
        }

        /// <summary>Executes a bounded weekly query for the snapshot-based usage charts.</summary>
        private static async Task<List<WeekValueRow>> QueryWeeksAsync(
            string body,
            DateTime from,
            DateTime through,
            DateTime settled)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.CommandTimeout = QueryTimeoutSecs;
                return await db.Database
                    .SqlQuery<WeekValueRow>(
                        body,
                        DateParameter("@from", from),
                        DateParameter("@through", through),
                        DateParameter("@settled", settled))
                    .ToListAsync();
            }
        }

        /// <summary>
        /// A date-only parameter. The usage tables store report dates at midnight, so comparing them
        /// against a <c>date</c> keeps the predicate an index seek and matches the SQL shown in the
        /// popover (which declares these as <c>date</c>).
        /// </summary>
        private static SqlParameter DateParameter(string name, DateTime value)
        {
            return new SqlParameter(name, System.Data.SqlDbType.Date) { Value = value.Date };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// SQL expression that buckets a datetime column to the Monday on/before it (matching the
        /// C# <see cref="MondayOf"/> spine). Uses day arithmetic, not DATEDIFF(WEEK, ...), because
        /// SQL Server's week grouping starts on Sunday and would push Sunday rows into the next
        /// week. 1900-01-01 is a Monday, so DATEDIFF(DAY, 0, col) % 7 == 0 exactly on Mondays.
        /// The column name is a compile-time constant, so there is no injection surface.
        /// </summary>
        private static string WeekBucket(string col)
        {
            return $"DATEADD(DAY, -(DATEDIFF(DAY, 0, {col}) % 7), CAST({col} AS date))";
        }

        /// <summary>The Monday of the week containing <paramref name="d"/> (weeks start Monday).</summary>
        private static DateTime MondayOf(DateTime d)
        {
            // DayOfWeek: Sunday=0..Saturday=6. (+6)%7 maps Monday->0, Sunday->6, so we step back to Monday.
            return d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        }

        /// <summary>All Mondays from <paramref name="firstMonday"/> to <paramref name="lastMonday"/> inclusive.</summary>
        private static List<DateTime> WeekSpine(DateTime firstMonday, DateTime lastMonday)
        {
            var weeks = new List<DateTime>();
            for (var w = firstMonday; w <= lastMonday; w = w.AddDays(7))
            {
                weeks.Add(w);
            }
            return weeks;
        }

        /// <summary>Projects query rows onto the full week spine, filling gaps with zero.</summary>
        private static List<ReportTimePoint> FillWeeks(List<DateTime> weekSpine, List<WeekValueRow> rows)
        {
            return FillWeeks(weekSpine, rows, missingValue: 0);
        }

        /// <summary>
        /// Projects query rows onto the full week spine. <paramref name="missingValue"/> is what a
        /// week with no row means: zero for charts that count events (no rows really is "nothing
        /// happened"), or null for the usage snapshot charts, where a missing week means the report
        /// never arrived and the value is unknown.
        /// </summary>
        private static List<ReportTimePoint> FillWeeks(
            List<DateTime> weekSpine,
            List<WeekValueRow> rows,
            double? missingValue)
        {
            var byWeek = new Dictionary<DateTime, double>();
            foreach (var r in rows)
            {
                byWeek[r.WeekStart.Date] = r.Value;
            }

            return weekSpine
                .Select(w => new ReportTimePoint
                {
                    WeekStart = w,
                    Value = byWeek.TryGetValue(w, out var v) ? v : missingValue,
                })
                .ToList();
        }

        /// <summary>
        /// The most recent report date Graph can be trusted to have finished populating. The usage
        /// reports lag real time by a couple of days, so a snapshot newer than this may exist but be
        /// half-filled - counting it would understate that week's active users.
        /// </summary>
        private static DateTime SettledThrough()
        {
            return DateTime.UtcNow.Date.AddDays(-UsageReportLagDays);
        }

        /// <summary>
        /// Wraps the executed query body with a runnable <c>DECLARE @from</c> so the SQL shown in the
        /// popover can be pasted straight into SSMS. The executed query uses a SqlParameter, not this
        /// string, so there is no injection surface (the body is built from compile-time constants).
        /// </summary>
        private static string DisplaySql(string body, DateTime from)
        {
            return $"DECLARE @from datetime = '{from:yyyy-MM-dd}';\r\n" + body;
        }

        private static string DisplaySql(string body, DateTime from, DateTime through, DateTime settled)
        {
            return $"DECLARE @from date = '{from:yyyy-MM-dd}';\r\n" +
                   $"DECLARE @through date = '{through:yyyy-MM-dd}';\r\n" +
                   $"DECLARE @settled date = '{settled:yyyy-MM-dd}';\r\n" +
                   body;
        }

        private static string DisplaySql(string body, DateTime from, string agentName)
        {
            var agentNameSql = agentName == null
                ? "NULL"
                : $"N'{agentName.Replace("'", "''")}'";
            return $"DECLARE @from datetime = '{from:yyyy-MM-dd}';\r\n" +
                   $"DECLARE @agentName nvarchar(100) = {agentNameSql};\r\n" +
                   body;
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

        internal static bool IsQueryTimeout(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is TimeoutException)
                {
                    return true;
                }
                if (current is SqlException sqlException && sqlException.Number == -2)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A named series and the weekly query that produces it (used by the usage chart).</summary>
        private sealed class SeriesQuery
        {
            public string Name { get; set; }
            public string Body { get; set; }
        }

        /// <summary>Shape for a weekly (WeekStart, Value) raw SQL result.</summary>
        internal sealed class WeekValueRow
        {
            public DateTime WeekStart { get; set; }
            public double Value { get; set; }
        }

        /// <summary>Shape for a named weekly series raw SQL result.</summary>
        private sealed class NamedWeekValueRow
        {
            public int SeriesKey { get; set; }
            public string SeriesName { get; set; }
            public DateTime WeekStart { get; set; }
            public double Value { get; set; }
        }

        /// <summary>Shape for a categorical (Label, Value) raw SQL result.</summary>
        private sealed class CategoryRow
        {
            public string Label { get; set; }
            public double Value { get; set; }
        }

        #endregion
    }
}
