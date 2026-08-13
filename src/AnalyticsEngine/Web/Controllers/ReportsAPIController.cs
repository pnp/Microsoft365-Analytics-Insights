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
            // Usage reports arrive a couple of days late. Never plot the current, necessarily
            // incomplete week as a sharp fall to zero; UsageCharts trims further when a completed
            // Sunday's snapshot is not yet available for every workload.
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
            const string join = "FROM dbo.copilot_chats AS c JOIN dbo.audit_events AS au ON c.event_id = au.id WHERE au.time_stamp >= @from";

            var wb = WeekBucket("au.time_stamp");

            var interactions =
                $"SELECT {wb} AS WeekStart, CAST(COUNT(*) AS float) AS Value\r\n" +
                join + "\r\n" +
                $"GROUP BY {wb} ORDER BY WeekStart;";

            var users =
                $"SELECT {wb} AS WeekStart, CAST(COUNT(DISTINCT au.user_id) AS float) AS Value\r\n" +
                join + "\r\n" +
                $"GROUP BY {wb} ORDER BY WeekStart;";

            var hosts =
                "SELECT TOP 8 ISNULL(c.app_host, '(unknown)') AS Label, CAST(COUNT(*) AS float) AS Value\r\n" +
                join + "\r\n" +
                "GROUP BY ISNULL(c.app_host, '(unknown)') ORDER BY Value DESC;";

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

        private static List<Task<ReportChart>> CopilotAgentCharts(
            DateTime from,
            List<DateTime> weekSpine,
            int topAgents,
            string agentName)
        {
            var wb = WeekBucket("au.time_stamp");
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
                "    JOIN dbo.audit_events AS au ON c.event_id = au.id\r\n" +
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

        // One line per workload. Each user activity-log table has one row per user per report date.
        // Use the completed week's Sunday snapshot to count users whose last activity falls in that
        // week. Reading one snapshot per week avoids scanning every repeated daily snapshot (tens of
        // millions of rows on a large tenant) and avoids the false dips caused by reconstructing a
        // week from whichever daily snapshots happened to import successfully.
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
                    "    SELECT CAST(@from AS date) AS WeekStart\r\n" +
                    "    UNION ALL\r\n" +
                    "    SELECT DATEADD(DAY, 7, WeekStart)\r\n" +
                    "    FROM WeekStarts\r\n" +
                    "    WHERE WeekStart < @through\r\n" +
                    "),\r\n" +
                    "AvailableWeeks AS (\r\n" +
                    "    SELECT weeks.WeekStart\r\n" +
                    "    FROM WeekStarts AS weeks\r\n" +
                    $"    WHERE EXISTS (SELECT 1 FROM {w.Table} AS snapshot\r\n" +
                    "                  WHERE snapshot.[date] = DATEADD(DAY, 6, weeks.WeekStart))\r\n" +
                    ")\r\n" +
                    "SELECT weeks.WeekStart, CAST(COUNT(DISTINCT activity.user_id) AS float) AS Value\r\n" +
                    "FROM AvailableWeeks AS weeks\r\n" +
                    $"LEFT JOIN {w.Table} AS activity\r\n" +
                    "    ON activity.[date] = DATEADD(DAY, 6, weeks.WeekStart)\r\n" +
                    "   AND activity.last_activity_date >= weeks.WeekStart\r\n" +
                    "   AND activity.last_activity_date < DATEADD(DAY, 7, weeks.WeekStart)\r\n" +
                    "GROUP BY weeks.WeekStart\r\n" +
                    "ORDER BY weeks.WeekStart\r\n" +
                    "OPTION (MAXRECURSION 100);"
            }).ToList();

            return new List<Task<ReportChart>>
            {
                RunMultiTimeSeriesAsync("usage-active-users", "Weekly active users by workload",
                    "Distinct users active in each completed week. Recent weeks appear once every workload's usage report is available.",
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

            var byType =
                "SELECT TOP 10 ISNULL(eo.operation_name, '(unknown)') AS Label, CAST(COUNT(*) AS float) AS Value\r\n" +
                "FROM dbo.audit_events AS au JOIN dbo.event_meta_sharepoint AS sp ON au.id = sp.event_id\r\n" +
                "LEFT JOIN dbo.event_operations AS eo ON au.operation_id = eo.id WHERE au.time_stamp >= @from\r\n" +
                "GROUP BY ISNULL(eo.operation_name, '(unknown)') ORDER BY Value DESC;";

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
                // Show one representative query; each series uses the same shape against its own table.
                Sql = "-- One query per workload (below is representative); the chart runs one per series.\r\n" +
                      DisplaySql(series.First().Body, from, weekSpine.Last()),
            };

            var rowsBySeries = new List<KeyValuePair<string, List<WeekValueRow>>>();

            // Sequentially per series keeps a bounded number of concurrent contexts (the area itself
            // already runs in parallel with other areas' charts); each performs indexed seeks into
            // one Sunday snapshot per week.
            foreach (var sq in series)
            {
                try
                {
                    var rows = await QueryWeeksAsync(sq.Body, from, weekSpine.Last());
                    rowsBySeries.Add(new KeyValuePair<string, List<WeekValueRow>>(sq.Name, rows));
                }
                catch (Exception ex)
                {
                    chart.Error = $"{sq.Name}: {InnermostMessage(ex)}";
                    return chart;
                }
            }

            // A report can still be arriving after the week has ended. Trim trailing weeks until
            // every workload has its Sunday snapshot rather than gap-filling missing snapshots with
            // zero and drawing a misleading decrease.
            var lastCompleteWeekIndex = weekSpine.Count - 1;
            while (lastCompleteWeekIndex >= 0)
            {
                var week = weekSpine[lastCompleteWeekIndex];
                if (rowsBySeries.All(s => s.Value.Any(r => r.WeekStart.Date == week)))
                {
                    break;
                }
                lastCompleteWeekIndex--;
            }

            if (lastCompleteWeekIndex < 0)
            {
                chart.Error = "No completed usage-report weeks are available for every workload.";
                return chart;
            }

            var completeWeekSpine = weekSpine.Take(lastCompleteWeekIndex + 1).ToList();
            chart.Series = rowsBySeries
                .Select(s => new ReportSeries { Name = s.Key, Points = FillWeeks(completeWeekSpine, s.Value) })
                .ToList();

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

        /// <summary>Executes a bounded weekly query with an explicit last completed week.</summary>
        private static async Task<List<WeekValueRow>> QueryWeeksAsync(
            string body,
            DateTime from,
            DateTime through)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.CommandTimeout = QueryTimeoutSecs;
                return await db.Database
                    .SqlQuery<WeekValueRow>(
                        body,
                        new SqlParameter("@from", from),
                        new SqlParameter("@through", through))
                    .ToListAsync();
            }
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
            var byWeek = new Dictionary<DateTime, double>();
            foreach (var r in rows)
            {
                byWeek[r.WeekStart.Date] = r.Value;
            }

            return weekSpine
                .Select(w => new ReportTimePoint
                {
                    WeekStart = w,
                    Value = byWeek.TryGetValue(w, out var v) ? v : 0,
                })
                .ToList();
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

        private static string DisplaySql(string body, DateTime from, DateTime through)
        {
            return $"DECLARE @from date = '{from:yyyy-MM-dd}';\r\n" +
                   $"DECLARE @through date = '{through:yyyy-MM-dd}';\r\n" +
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

        /// <summary>A named series and the weekly query that produces it (used by the usage chart).</summary>
        private sealed class SeriesQuery
        {
            public string Name { get; set; }
            public string Body { get; set; }
        }

        /// <summary>Shape for a weekly (WeekStart, Value) raw SQL result.</summary>
        private sealed class WeekValueRow
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
