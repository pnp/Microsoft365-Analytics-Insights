using Common.Entities;
using Common.Entities.Config;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// The "Microsoft 365 apps" report area: which Office apps people actually use, on which
    /// platforms, in which parts of the business, and how much of that usage Copilot has reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Source and its exact meaning.</b> Every chart here reads <c>dbo.platform_user_activity_log</c>,
    /// which the importer fills from the Graph <c>getM365AppUserDetail(date=...)</c> report - one row
    /// per user per report date, carrying a <c>bit</c> per app (<c>outlook</c>, <c>word</c>, ...), a
    /// bit per platform (<c>windows</c>, <c>mac</c>, <c>mobile</c>, <c>web</c>) and a bit for each of
    /// the 24 app-on-platform combinations.
    /// </para>
    /// <para>
    /// "Did this person use Word in this period?" is therefore the OR of that person's <c>word</c> bit
    /// across the report dates in the period. That is not an invention here - it is exactly what the
    /// product's own profiling compile already does: <c>profiling.usp_UpsertM365Apps</c> selects
    /// <c>MAX(1 * word)</c> grouped by <c>user_id</c> over a <c>@StartDate</c>/<c>@EndDate</c> range
    /// (<c>App.ControlPanel.Engine/SqlExtentions/Profiling-03-CreateSchema.sql</c>).
    /// </para>
    /// <para>
    /// Deliberately NOT the snapshot pattern used by <c>UsageCharts</c>. That one picks the latest
    /// settled report date in each week and filters on <c>last_activity_date</c>, because the tables it
    /// reads (<c>teams_user_activity_log</c> and friends) carry running COUNTS that would be
    /// double-counted if several report dates in a week were added together. Here the columns are
    /// booleans, so collapsing a person's rows with <c>MAX</c> is already idempotent across report
    /// dates and the extra machinery would buy nothing.
    /// </para>
    /// <para>
    /// <b>What is counted is a person, never an action.</b> The source has no volume - it cannot say
    /// whether someone sent one mail or four hundred. Every figure in this area is a headcount, which
    /// is why the value labels read "People". Nothing here should ever be described as "usage" in the
    /// sense of intensity.
    /// </para>
    /// <para>
    /// <b>Scale, and why every query collapses per person first.</b> At the ~200k-user tenant this
    /// product is designed against, this table grows by ~200k rows a day (~73m a year), so a 6-month
    /// window touches ~36m rows. Every query below therefore aggregates to ONE ROW PER PERSON (or per
    /// person per week) BEFORE fanning out with <c>CROSS APPLY (VALUES ...)</c>. The apply genuinely
    /// does emit one row per listed value for every input row, so the order matters enormously:
    /// measured on an 18m-row synthetic table over a 180-day window, running the app-on-platform
    /// matrix as a fan-out over raw activity rows took 91s, and collapsing to one row per person first
    /// took 17s for identical output. The other charts moved similarly (weekly app users 53s -> 8s),
    /// which is the difference between fitting inside the per-chart timeout and not. All queries carry
    /// <c>OPTION (RECOMPILE)</c> so the requested window drives the plan.
    /// </para>
    /// </remarks>
    public partial class ReportsAPIController
    {
        /// <summary>
        /// Departments / domains shown as columns in a matrix. Twenty is about the most a reader can
        /// scan across; beyond that the grid stops being legible and becomes a table nobody reads.
        /// </summary>
        private const int MatrixColumnLimit = 20;

        /// <summary>
        /// Departments ranked in the adoption-rate chart. Larger than the matrix limit because a
        /// ranked list stays readable where a grid does not.
        /// </summary>
        private const int AdoptionRankLimit = 25;

        /// <summary>
        /// Smallest department that gets an adoption percentage.
        /// </summary>
        /// <remarks>
        /// A department of two people is either 0%, 50% or 100%, and whichever it lands on it will top
        /// or bottom the ranking for reasons that have nothing to do with adoption. Suppressing them
        /// keeps the chart pointing at places where a change-management effort would actually pay.
        /// </remarks>
        private const int MinDepartmentSizeForRate = 5;

        /// <summary>The label used where a user has no department in the directory.</summary>
        private const string NoDepartmentLabel = "(No department)";

        /// <summary>The label used where a sign-in address has no usable domain part.</summary>
        private const string NoDomainLabel = "(No domain)";

        /// <summary>
        /// Sentinel app name carrying each department's / domain's DISTINCT active headcount.
        /// </summary>
        /// <remarks>
        /// It rides along as a seventh pseudo-app in the same aggregate so the matrix queries can rank
        /// columns by real headcount without reading the activity data a second time. Filtered out
        /// before the rows are returned. Cannot collide with a real column value because the app names
        /// all come from <see cref="OfficeAppCatalogue"/>.
        /// </remarks>
        private const string AnyAppSentinel = "(Any app)";

        /// <summary>
        /// How many of this area's queries may be in flight at once, across all callers.
        /// </summary>
        /// <remarks>
        /// The area has eleven charts and each opens its own <see cref="AnalyticsEntitiesContext"/>.
        /// Started all at once they are eleven concurrent range aggregates over the same
        /// tens-of-millions-of-rows index, and a handful of admins refreshing together would also take
        /// a large share of the default 100-connection pool for up to <see cref="QueryTimeoutSecs"/>
        /// seconds each. The sibling <c>UsageCharts</c> already declined that shape, running its series
        /// sequentially for the same reason. Three keeps the page responsive while bounding what one
        /// page load can do to the database.
        /// </remarks>
        private static readonly SemaphoreSlim OfficeAppsQueryGate = new SemaphoreSlim(3, 3);

        /// <summary>
        /// Longest a chart will wait for its turn at the gate before giving up.
        /// </summary>
        /// <remarks>
        /// Waiting is not covered by the SQL command timeout, so without a bound a queue of callers
        /// could push the whole HTTP request past the ~230s App Service limit and return a 500 rather
        /// than a page with a per-chart message on it.
        /// </remarks>
        private static readonly TimeSpan OfficeAppsQueueTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// One Office app, and the columns that describe it in each of the two source tables.
        /// </summary>
        /// <remarks>
        /// Single source of truth for the whole area. Every query below is generated from this list,
        /// so an app cannot appear in the popularity bar chart but be missing from the department
        /// matrix, and the app names in the Copilot attach chart are guaranteed to be the same strings
        /// as the app names it is compared against.
        /// </remarks>
        internal sealed class OfficeAppColumn
        {
            public OfficeAppColumn(string name, string column, string copilotColumn)
            {
                Name = name;
                Column = column;
                CopilotColumn = copilotColumn;
            }

            /// <summary>Display name, and the row/series key used across every chart in the area.</summary>
            public string Name { get; }

            /// <summary>The <c>bit</c> column on <c>dbo.platform_user_activity_log</c>.</summary>
            public string Column { get; }

            /// <summary>The matching per-app date column on <c>dbo.copilot_usage_user_activity_log</c>.</summary>
            public string CopilotColumn { get; }
        }

        /// <summary>
        /// The apps, in the order they are shown.
        /// </summary>
        /// <remarks>
        /// Ordered by how broadly they are used in a typical tenant rather than alphabetically, so the
        /// matrices read top-to-bottom from "everyone" to "a specialist minority" and an unexpected
        /// gap stands out. Loop is absent because <c>getM365AppUserDetail</c> has no Loop column - the
        /// Copilot report does, but a Copilot attach rate needs a denominator from the apps report, so
        /// including it would produce a rate with nothing to divide by.
        /// </remarks>
        internal static readonly OfficeAppColumn[] OfficeAppCatalogue =
        {
            new OfficeAppColumn("Outlook", "outlook", "outlook_last_activity_date"),
            new OfficeAppColumn("Teams", "teams", "teams_last_activity_date"),
            new OfficeAppColumn("Word", "word", "word_last_activity_date"),
            new OfficeAppColumn("Excel", "excel", "excel_last_activity_date"),
            new OfficeAppColumn("PowerPoint", "powerpoint", "powerpoint_last_activity_date"),
            new OfficeAppColumn("OneNote", "onenote", "onenote_last_activity_date"),
        };

        /// <summary>
        /// The platforms, in the order they are shown, with the suffix used by the cross columns.
        /// </summary>
        internal static readonly KeyValuePair<string, string>[] OfficePlatformCatalogue =
        {
            new KeyValuePair<string, string>("Windows", "windows"),
            new KeyValuePair<string, string>("Mac", "mac"),
            new KeyValuePair<string, string>("Mobile", "mobile"),
            new KeyValuePair<string, string>("Web", "web"),
        };

        /// <summary>
        /// Every chart in the area.
        /// </summary>
        /// <remarks>
        /// Grouped for two readers who want different things from the same data. The first block
        /// answers an executive's questions - what do people use, is that changing, how deeply have we
        /// landed the suite, and is Copilot reaching the apps we actually work in. The second answers
        /// an adoption analyst's - where exactly is the gap, and who do I go and talk to.
        /// </remarks>
        private static List<Task<ReportChart>> OfficeAppsCharts(DateTime from, List<DateTime> weekSpine)
        {
            var config = new AppConfig();
            var settings = config.ImportJobSettings ?? new ImportTaskSettings();
            var copilotUsageImported = settings.GraphCopilotUsageReports;

            // A filter of '*' matches every group, so it narrows nothing and the directory is still
            // the right denominator. Treating any non-empty value as a narrowing would suppress the
            // adoption chart on a deployment whose scope is in fact the whole tenant.
            var groupFilter = new UserGroupsFilterModel(config.UserGroupsFilter);
            var groupFiltered = groupFilter.Patterns.Count > 0 && !groupFilter.MatchesEverything;

            return new List<Task<ReportChart>>
            {
                // ---- Headline: what is used, and is it moving? -------------------------------
                Gated("office-apps-popularity", "Most used apps",
                    () => RunCategoryAsync("office-apps-popularity",
                    "Most used apps",
                    "People who used each app at least once in the period. Someone who uses both Word and Excel is counted in both, so these do not add up to your headcount.",
                    "People", AppPopularityQuery(), from)),

                Gated("office-apps-trend", "App users each week",
                    () => RunGroupedTimeSeriesAsync("office-apps-trend",
                    "App users each week",
                    "Distinct people using each app, week by week. The current part-week is left off because the Microsoft usage report for it has not finished arriving.",
                    "People",
                    QueryNamedWeeksAsync(AppWeeklyQuery(), from),
                    DisplaySql(AppWeeklyQuery(), from), weekSpine)),

                Gated("office-apps-breadth", "How much of the suite people use",
                    () => WithValueSuffix(RunCategoryAsync("office-apps-breadth",
                    "How much of the suite people use",
                    "How many different Office apps each person used in the period. A workforce clustered on one or two apps has bought a suite and is using a product - that gap is usually the cheapest adoption win available.",
                    "People", AppBreadthQuery(), from), null, showShare: true)),

                // ---- Platforms: where the work happens ---------------------------------------
                Gated("office-apps-platform-mix", "Platforms people work on",
                    () => RunCategoryAsync("office-apps-platform-mix",
                    "Platforms people work on",
                    "People who connected from each platform at least once. Most people appear under more than one.",
                    "People", PlatformPopularityQuery(), from)),

                Gated("office-apps-platform-trend", "Platform users each week",
                    () => RunGroupedTimeSeriesAsync("office-apps-platform-trend",
                    "Platform users each week",
                    "Distinct people on each platform, week by week. A rising web or mobile line against a flat desktop line is the shift to browser and phone working, and it changes what your endpoint and licensing assumptions should be.",
                    "People",
                    QueryNamedWeeksAsync(PlatformWeeklyQuery(), from),
                    DisplaySql(PlatformWeeklyQuery(), from), weekSpine)),

                Gated("office-apps-platform-matrix", "Which apps are used on which platforms",
                    () => RunMatrixAsync("office-apps-platform-matrix",
                    "Which apps are used on which platforms",
                    "People using each app on each platform. Shaded across each row, because Outlook dwarfs OneNote and a single shared scale would leave the smaller rows blank.",
                    "People", AppPlatformMatrixQuery(), from,
                    rowLabel: "App", columnLabel: "Platform",
                    rows: OfficeAppCatalogue.Select(a => a.Name).ToList(),
                    columns: OfficePlatformCatalogue.Select(p => p.Key).ToList(),
                    shadeByRow: true)),

                // ---- The business cut ---------------------------------------------------------
                Gated("office-apps-by-department", "App use by department",
                    () => RunMatrixAsync("office-apps-by-department",
                    "App use by department",
                    $"People using each app, in the {MatrixColumnLimit} departments with the most active people. Shaded across each row, so you are comparing departments within an app rather than apps against each other. Department comes from your directory; people with none are grouped as {NoDepartmentLabel}.",
                    "People", AppByDepartmentQuery(), from,
                    rowLabel: "App", columnLabel: "Department",
                    rows: OfficeAppCatalogue.Select(a => a.Name).ToList(),
                    columns: null,
                    shadeByRow: true)),

                DepartmentAdoptionChart(from, groupFiltered),

                Gated("office-apps-by-domain", "App use by email domain",
                    () => RunMatrixAsync("office-apps-by-domain",
                    "App use by email domain",
                    $"People using each app, split by the domain of their sign-in address - useful where subsidiaries, brands or an unfinished migration share one tenant. Top {MatrixColumnLimit} domains by active people.",
                    "People", AppByDomainQuery(), from,
                    rowLabel: "App", columnLabel: "Domain",
                    rows: OfficeAppCatalogue.Select(a => a.Name).ToList(),
                    columns: null,
                    shadeByRow: true)),

                Gated("office-apps-web-only", "People who only ever use the browser version",
                    () => RunCategoryAsync("office-apps-web-only",
                    "People who only ever use the browser version",
                    "People who used an app on the web and never on Windows, Mac or mobile in the period. Often unmanaged or personal devices, VDI users, or people who simply never had the desktop app installed - each of which needs a different response.",
                    "People", WebOnlyQuery(), from)),

                // ---- Copilot ------------------------------------------------------------------
                CopilotAttachChart(from, copilotUsageImported),
            };
        }

        /// <summary>
        /// Share of each department's people who used any Office app.
        /// </summary>
        /// <remarks>
        /// Suppressed entirely when a user-groups filter is configured, and this is the important part.
        /// The numerator comes from the activity import, which the loaders restrict to the configured
        /// groups; the denominator comes from <c>dbo.users</c>, which the user-metadata import fills
        /// from the whole directory. Divide one by the other on a group-filtered deployment and a
        /// department where every single in-scope person uses Office can be reported at a few percent.
        /// No correct denominator is available here, so the chart says so rather than printing a number
        /// that is confidently wrong.
        /// </remarks>
        private static Task<ReportChart> DepartmentAdoptionChart(DateTime from, bool groupFiltered)
        {
            const string title = "Departments least likely to use the apps";

            if (groupFiltered)
            {
                return Task.FromResult(new ReportChart
                {
                    Key = "office-apps-department-adoption",
                    Title = title,
                    Type = "bar",
                    ValueLabel = "Adoption",
                    Description = "Share of each department's people who used any Office app in the period.",
                    Categories = new List<ReportCategory>(),
                    Sql = DisplaySql(DepartmentAdoptionRateQuery(), from),
                    Warning =
                        "This deployment restricts the usage-report import to selected user groups, but directory "
                        + "details are imported for everyone. The share of a department that uses Office would "
                        + "therefore be divided by people the import was never asked to look at, understating every "
                        + "department - so it is not shown. The counts in the other charts are unaffected.",
                });
            }

            return Gated("office-apps-department-adoption", title,
                () => WithValueSuffix(RunCategoryAsync("office-apps-department-adoption",
                title,
                $"Share of each department's people who used any Office app in the period, lowest first. Unlike the counts above this is a rate, so a big department cannot top it just for being big. Departments with fewer than {MinDepartmentSizeForRate} people are left out, because at that size the percentage says more about the size than the adoption.",
                "Adoption", DepartmentAdoptionRateQuery(), from), "%"));
        }

        /// <summary>
        /// Copilot take-up inside each app, or an explanation of why it cannot be shown.
        /// </summary>
        /// <remarks>
        /// There are two different "no data" cases and they must not look alike. If the import is
        /// switched off there is nothing to say. If it is switched on but no report rows landed in the
        /// window - a failed or not-yet-run import, or a tenant whose reports are concealed - then
        /// every app would otherwise be drawn at a confident 0%, which reads as "nobody uses Copilot"
        /// rather than "we do not know". The existence check only runs when the rate is zero
        /// everywhere, so the normal path pays nothing for it.
        /// </remarks>
        private static Task<ReportChart> CopilotAttachChart(DateTime from, bool copilotUsageImported)
        {
            const string key = "office-apps-copilot-attach";
            const string title = "Copilot take-up inside each app";
            const string description =
                "Of the people who use an app, the share who used Copilot in that same app during the period. "
                + "This is the question a Copilot business case turns on: not how many licences were bought, but "
                + "whether Copilot reached people where they already work. A low bar against a tall bar in "
                + "\"Most used apps\" is your largest untouched audience.";

            if (!copilotUsageImported)
            {
                return Task.FromResult(new ReportChart
                {
                    Key = key,
                    Title = title,
                    Type = "bar",
                    ValueLabel = "Take-up",
                    Description = description,
                    Categories = new List<ReportCategory>(),
                    Sql = DisplaySql(CopilotAttachRateQuery(), from),
                    Warning =
                        "The Copilot usage report import is switched off, so there is nothing to compare app use "
                        + "against. Enable 'Copilot usage reports (Graph)' in the installer to fill this chart in. "
                        + "The app and platform charts above do not need it.",
                });
            }

            return Gated(key, title, async () =>
            {
                var chart = await WithValueSuffix(
                    RunCategoryAsync(key, title, description,
                        "Take-up", CopilotAttachRateQuery(), from), "%").ConfigureAwait(false);

                if (chart.Error != null) return chart;

                // A non-zero rate anywhere proves the report arrived, so there is nothing to check.
                if (chart.Categories != null && chart.Categories.Any(c => c.Value > 0)) return chart;

                // Otherwise "0% everywhere" and "no report arrived" are indistinguishable from this
                // result alone, and the difference matters enormously to the reader. Resolve it, and
                // if it cannot be resolved, say so rather than leaving a row of confident 0% bars.
                bool anyRows;
                try
                {
                    anyRows = await AnyCopilotUsageRowsAsync(from).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    chart.Categories = new List<ReportCategory>();
                    chart.Warning =
                        "Every app came back at 0% take-up, but whether a Copilot usage report actually arrived for "
                        + "this period could not be confirmed (" + InnermostMessage(ex) + "), so the figures are not "
                        + "shown. Refresh to try again.";
                    return chart;
                }

                if (!anyRows)
                {
                    // Clearing the categories is the point: leaving them would draw a labelled 0% bar
                    // per app beneath the warning, which reads as an authoritative measurement of
                    // "nobody uses Copilot" - the exact false result this check exists to prevent.
                    chart.Categories = new List<ReportCategory>();
                    chart.Warning =
                        "The Copilot usage report import is switched on, but no Copilot usage report has landed for "
                        + "this period yet, so take-up cannot be measured. That is not the same as nobody using "
                        + "Copilot. Check the Copilot usage report import on the Service health page.";
                }

                return chart;
            });
        }

        /// <summary>
        /// Whether any Copilot usage-report row exists in the window.
        /// </summary>
        /// <remarks>
        /// Exposed as a query builder like the chart queries so the same contract tests cover it -
        /// it is a query this area runs against a customer database, and an untested one is exactly
        /// how the area's "every query is windowed and recompiles" invariant quietly stops being true.
        /// </remarks>
        internal static string CopilotDataPresenceQuery()
        {
            return
                "SELECT TOP (1) CAST(1 AS int) AS Value\r\n" +
                "FROM dbo.copilot_usage_user_activity_log\r\n" +
                "WHERE [date] >= @from\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>Whether any Copilot usage-report row exists in the window.</summary>
        private static async Task<bool> AnyCopilotUsageRowsAsync(DateTime from)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.CommandTimeout = QueryTimeoutSecs;
                var rows = await db.Database
                    .SqlQuery<int>(CopilotDataPresenceQuery(), new SqlParameter("@from", from))
                    .ToListAsync()
                    .ConfigureAwait(false);
                return rows.Count > 0;
            }
        }

        /// <summary>
        /// Runs a chart's work only once a slot on <see cref="OfficeAppsQueryGate"/> is free.
        /// </summary>
        /// <remarks>
        /// The factory is invoked after the wait, not before, so the query does not start until the
        /// slot is held. A caller that waits too long gets a per-chart message rather than stalling
        /// the whole request - and that message keeps the chart's own key and title, because the SPA
        /// uses the key as a React key and two timed-out charts sharing one would collide.
        /// </remarks>
        private static async Task<ReportChart> Gated(string key, string title, Func<Task<ReportChart>> chart)
        {
            if (!await OfficeAppsQueryGate.WaitAsync(OfficeAppsQueueTimeout).ConfigureAwait(false))
            {
                return new ReportChart
                {
                    Key = key,
                    Title = title,
                    Type = "bar",
                    ValueLabel = "People",
                    Categories = new List<ReportCategory>(),
                    Error = "The database was too busy to build this chart. Refresh in a few seconds.",
                };
            }

            try
            {
                return await chart().ConfigureAwait(false);
            }
            finally
            {
                OfficeAppsQueryGate.Release();
            }
        }

        #region Query builders

        /// <summary>App display name -> bit column on the activity table.</summary>
        private static List<KeyValuePair<string, string>> AppColumns() =>
            OfficeAppCatalogue.Select(a => new KeyValuePair<string, string>(a.Name, a.Column)).ToList();

        /// <summary>Platform display name -> bit column.</summary>
        private static List<KeyValuePair<string, string>> PlatformColumns() =>
            OfficePlatformCatalogue.ToList();

        /// <summary>App-on-platform alias (<c>Word_Windows</c>) -> cross bit column.</summary>
        private static List<KeyValuePair<string, string>> CrossColumns() =>
            (from app in OfficeAppCatalogue
             from platform in OfficePlatformCatalogue
             select new KeyValuePair<string, string>(
                 app.Name + "_" + platform.Key, app.Column + "_" + platform.Value)).ToList();

        /// <summary>
        /// The <c>MAX(1 * bit)</c> projections that collapse a person's report rows to one row.
        /// </summary>
        /// <remarks>
        /// <c>MAX(1 * bit)</c> is the idiom <c>profiling.usp_UpsertM365Apps</c> uses for exactly this,
        /// and it is what makes "used at any point in the period" a single pass rather than a
        /// <c>COUNT(DISTINCT)</c> over a fanned-out row set.
        /// </remarks>
        private static string CollapseProjections(List<KeyValuePair<string, string>> columns, string alias)
        {
            return string.Join(",\r\n", columns.Select(c =>
                $"           MAX(1 * {alias}.{c.Value}) AS {c.Key}"));
        }

        /// <summary>
        /// A <c>CROSS APPLY (VALUES ...)</c> body over COLLAPSED per-person columns.
        /// </summary>
        /// <remarks>
        /// The apply emits one row per listed label for every input row, so it is always applied to
        /// the collapsed set (one row per person, or per person per week) rather than to raw activity
        /// rows - see the type-level remarks for the measured cost of getting that order wrong.
        /// </remarks>
        private static string ValuesClause(List<string> labels, string source, bool numbered, string columnPrefix = "")
        {
            var builder = new StringBuilder();
            for (var i = 0; i < labels.Count; i++)
            {
                var prefix = numbered ? (i + 1) + ", " : string.Empty;
                builder.Append("        (").Append(prefix)
                    .Append("N'").Append(labels[i]).Append("', ")
                    .Append(source).Append(".").Append(columnPrefix).Append(labels[i]).Append(")");
                builder.Append(i == labels.Count - 1 ? "\r\n" : ",\r\n");
            }
            return builder.ToString();
        }

        /// <summary>The per-person collapse CTE over a set of bit columns.</summary>
        private static string PerUserCte(List<KeyValuePair<string, string>> columns)
        {
            return
                "WITH PerUser AS (\r\n" +
                "    SELECT a.user_id,\r\n" +
                CollapseProjections(columns, "a") + "\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    WHERE a.[date] >= @from\r\n" +
                "    GROUP BY a.user_id\r\n" +
                ")\r\n";
        }

        /// <summary>People who used each app at least once in the window.</summary>
        internal static string AppPopularityQuery()
        {
            return
                PerUserCte(AppColumns()) +
                "SELECT app.Label, CAST(SUM(app.Used) AS float) AS Value\r\n" +
                "FROM PerUser AS p\r\n" +
                "CROSS APPLY (VALUES\r\n" +
                ValuesClause(OfficeAppCatalogue.Select(a => a.Name).ToList(), "p", numbered: false) +
                ") AS app(Label, Used)\r\n" +
                "WHERE app.Used = 1\r\n" +
                "GROUP BY app.Label\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>People who connected from each platform at least once in the window.</summary>
        internal static string PlatformPopularityQuery()
        {
            return
                PerUserCte(PlatformColumns()) +
                "SELECT platform.Label, CAST(SUM(platform.Used) AS float) AS Value\r\n" +
                "FROM PerUser AS p\r\n" +
                "CROSS APPLY (VALUES\r\n" +
                ValuesClause(OfficePlatformCatalogue.Select(x => x.Key).ToList(), "p", numbered: false) +
                ") AS platform(Label, Used)\r\n" +
                "WHERE platform.Used = 1\r\n" +
                "GROUP BY platform.Label\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>The per-person-per-week collapse CTE.</summary>
        private static string PerUserWeekCte(List<KeyValuePair<string, string>> columns)
        {
            var week = WeekBucket("a.[date]");
            return
                "WITH PerUserWeek AS (\r\n" +
                $"    SELECT a.user_id, {week} AS WeekStart,\r\n" +
                CollapseProjections(columns, "a") + "\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    WHERE a.[date] >= @from\r\n" +
                $"    GROUP BY a.user_id, {week}\r\n" +
                ")\r\n";
        }

        /// <summary>Distinct people per app per Monday-aligned week.</summary>
        internal static string AppWeeklyQuery()
        {
            return
                PerUserWeekCte(AppColumns()) +
                "SELECT app.SeriesKey, app.SeriesName, p.WeekStart,\r\n" +
                "       CAST(SUM(app.Used) AS float) AS Value\r\n" +
                "FROM PerUserWeek AS p\r\n" +
                "CROSS APPLY (VALUES\r\n" +
                ValuesClause(OfficeAppCatalogue.Select(a => a.Name).ToList(), "p", numbered: true) +
                ") AS app(SeriesKey, SeriesName, Used)\r\n" +
                "WHERE app.Used = 1\r\n" +
                "GROUP BY app.SeriesKey, app.SeriesName, p.WeekStart\r\n" +
                "ORDER BY p.WeekStart\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>Distinct people per platform per Monday-aligned week.</summary>
        internal static string PlatformWeeklyQuery()
        {
            return
                PerUserWeekCte(PlatformColumns()) +
                "SELECT platform.SeriesKey, platform.SeriesName, p.WeekStart,\r\n" +
                "       CAST(SUM(platform.Used) AS float) AS Value\r\n" +
                "FROM PerUserWeek AS p\r\n" +
                "CROSS APPLY (VALUES\r\n" +
                ValuesClause(OfficePlatformCatalogue.Select(x => x.Key).ToList(), "p", numbered: true) +
                ") AS platform(SeriesKey, SeriesName, Used)\r\n" +
                "WHERE platform.Used = 1\r\n" +
                "GROUP BY platform.SeriesKey, platform.SeriesName, p.WeekStart\r\n" +
                "ORDER BY p.WeekStart\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// How many distinct apps each person used, bucketed into "1 app", "2 apps", ...
        /// </summary>
        /// <remarks>
        /// People with a row but no app bit set are excluded rather than shown as "0 apps": the report
        /// emits a row for every licensed person whether or not they did anything, so a zero bucket
        /// would be dominated by accounts that are simply dormant and would swamp the shape of the
        /// chart.
        /// </remarks>
        internal static string AppBreadthQuery()
        {
            var sums = string.Join(" + ", OfficeAppCatalogue.Select(a => $"MAX(1 * a.{a.Column})"));
            return
                "WITH PerUser AS (\r\n" +
                "    SELECT a.user_id,\r\n" +
                $"           {sums} AS AppCount\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    WHERE a.[date] >= @from\r\n" +
                "    GROUP BY a.user_id\r\n" +
                ")\r\n" +
                "SELECT CAST(AppCount AS varchar(2))\r\n" +
                "       + CASE WHEN AppCount = 1 THEN N' app' ELSE N' apps' END AS Label,\r\n" +
                "       CAST(COUNT(*) AS float) AS Value\r\n" +
                "FROM PerUser\r\n" +
                "WHERE AppCount > 0\r\n" +
                "GROUP BY AppCount\r\n" +
                "ORDER BY AppCount\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>People using each app on each platform, from the 24 cross columns.</summary>
        internal static string AppPlatformMatrixQuery()
        {
            var pairs = new List<string>();
            foreach (var app in OfficeAppCatalogue)
            {
                foreach (var platform in OfficePlatformCatalogue)
                {
                    pairs.Add($"        (N'{app.Name}', N'{platform.Key}', p.{app.Name}_{platform.Key})");
                }
            }

            return
                PerUserCte(CrossColumns()) +
                "SELECT combo.RowLabel, combo.ColumnLabel, CAST(SUM(combo.Used) AS float) AS Value\r\n" +
                "FROM PerUser AS p\r\n" +
                "CROSS APPLY (VALUES\r\n" + string.Join(",\r\n", pairs) + "\r\n" +
                ") AS combo(RowLabel, ColumnLabel, Used)\r\n" +
                "WHERE combo.Used = 1\r\n" +
                "GROUP BY combo.RowLabel, combo.ColumnLabel\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// People using each app, split by a per-user dimension taken from the directory.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The activity data is read ONCE. The first draft referenced a join-heavy CTE twice - SQL
        /// Server does not materialise a CTE, so it ran twice - and a 30-day domain query cost 55.5
        /// million logical reads. Everything after <c>PerUserDimension</c> here is one row per person,
        /// and the only CTE referenced more than once (<c>Counts</c>) has at most seven rows per
        /// department.
        /// </para>
        /// <para>
        /// Columns are ranked by DISTINCT active headcount, carried along as the
        /// <see cref="AnyAppSentinel"/> pseudo-app in the same aggregate. Ranking on the sum of the
        /// per-app counts instead would count a person once per app they use, so a small
        /// many-app department could displace a larger single-app one from the grid - every cell shown
        /// would still be correct, but the choice of which columns to show would not match the
        /// "departments with the most active people" the chart promises.
        /// </para>
        /// </remarks>
        private static string PerUserDimensionMatrix(string dimensionExpression, string extraJoin)
        {
            var appNames = OfficeAppCatalogue.Select(a => a.Name).ToList();
            var anyApp = string.Join(" OR ", appNames.Select(n => $"d.{n} = 1"));

            return
                "WITH PerUser AS (\r\n" +
                "    SELECT a.user_id,\r\n" +
                CollapseProjections(AppColumns(), "a") + "\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    WHERE a.[date] >= @from\r\n" +
                "    GROUP BY a.user_id\r\n" +
                "),\r\n" +
                "PerUserDimension AS (\r\n" +
                "    SELECT p.user_id,\r\n" +
                $"           {dimensionExpression} AS DimensionName,\r\n" +
                string.Join(",\r\n", appNames.Select(n => $"           p.{n}")) + "\r\n" +
                "    FROM PerUser AS p\r\n" +
                "    INNER JOIN dbo.users AS u ON u.id = p.user_id\r\n" +
                extraJoin +
                "),\r\n" +
                "Counts AS (\r\n" +
                "    SELECT app.AppName, d.DimensionName, SUM(app.Used) AS People\r\n" +
                "    FROM PerUserDimension AS d\r\n" +
                "    CROSS APPLY (VALUES\r\n" +
                ValuesClause(appNames, "d", numbered: false) +
                $"      , (N'{AnyAppSentinel}', CASE WHEN {anyApp} THEN 1 ELSE 0 END)\r\n" +
                "    ) AS app(AppName, Used)\r\n" +
                "    WHERE app.Used = 1\r\n" +
                "    GROUP BY app.AppName, d.DimensionName\r\n" +
                "),\r\n" +
                "WithTotals AS (\r\n" +
                "    SELECT AppName, DimensionName, People,\r\n" +
                $"           MAX(CASE WHEN AppName = N'{AnyAppSentinel}' THEN People END)\r\n" +
                "               OVER (PARTITION BY DimensionName) AS ActivePeople\r\n" +
                "    FROM Counts\r\n" +
                "),\r\n" +
                "Ranked AS (\r\n" +
                "    SELECT AppName, DimensionName, People,\r\n" +
                "           DENSE_RANK() OVER (ORDER BY ActivePeople DESC, DimensionName) AS DimensionRank\r\n" +
                "    FROM WithTotals\r\n" +
                ")\r\n" +
                "SELECT AppName AS RowLabel, DimensionName AS ColumnLabel, CAST(People AS float) AS Value\r\n" +
                "FROM Ranked\r\n" +
                $"WHERE DimensionRank <= {MatrixColumnLimit} AND AppName <> N'{AnyAppSentinel}'\r\n" +
                // Ordered by the same rank the WHERE selected on, so the column order the UI shows is
                // the headcount order the SQL chose rather than a second, different ranking applied in
                // C# over the returned cells.
                "ORDER BY DimensionRank, AppName\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>People using each app, by directory department, for the biggest departments.</summary>
        internal static string AppByDepartmentQuery()
        {
            return PerUserDimensionMatrix(
                $"ISNULL(dep.[name], N'{NoDepartmentLabel}')",
                "    LEFT JOIN dbo.user_departments AS dep ON dep.id = u.department_id\r\n");
        }

        /// <summary>
        /// People using each app, by the domain part of their sign-in address.
        /// </summary>
        /// <remarks>
        /// The domain is taken after the LAST <c>@</c>, matching
        /// <c>CopilotUsageReportPolicy.DomainOf</c>, so an address that somehow contains more than one
        /// is split the same way here as it is on the import side. <c>users.user_name</c> is written
        /// lower-cased by the importer, so no further folding is applied.
        /// </remarks>
        internal static string AppByDomainQuery()
        {
            return PerUserDimensionMatrix(
                "CASE WHEN CHARINDEX('@', u.user_name) > 0\r\n" +
                "                THEN RIGHT(u.user_name, CHARINDEX('@', REVERSE(u.user_name)) - 1)\r\n" +
                $"                ELSE N'{NoDomainLabel}'\r\n" +
                "           END",
                string.Empty);
        }

        /// <summary>
        /// Share of each department's people who used any Office app in the window.
        /// </summary>
        /// <remarks>
        /// The denominator is every person in <c>dbo.users</c> carrying that department, NOT just the
        /// people who appear in the activity table. That distinction is the whole point of the chart:
        /// dividing active people by active people would return 100% for every department and say
        /// nothing. It is only valid when the activity import covers the same population as the
        /// directory, which is why the caller suppresses the chart on a group-filtered deployment.
        /// </remarks>
        internal static string DepartmentAdoptionRateQuery()
        {
            var anyApp = string.Join(" OR ", OfficeAppCatalogue.Select(a => $"a.{a.Column} = 1"));
            return
                "WITH ActiveUsers AS (\r\n" +
                "    SELECT DISTINCT a.user_id\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    WHERE a.[date] >= @from\r\n" +
                $"      AND ({anyApp})\r\n" +
                "),\r\n" +
                "ByDepartment AS (\r\n" +
                $"    SELECT ISNULL(dep.[name], N'{NoDepartmentLabel}') AS DepartmentName,\r\n" +
                "           COUNT(*) AS PeopleInDepartment,\r\n" +
                "           COUNT(act.user_id) AS ActivePeople\r\n" +
                "    FROM dbo.users AS u\r\n" +
                "    LEFT JOIN dbo.user_departments AS dep ON dep.id = u.department_id\r\n" +
                "    LEFT JOIN ActiveUsers AS act ON act.user_id = u.id\r\n" +
                $"    GROUP BY ISNULL(dep.[name], N'{NoDepartmentLabel}')\r\n" +
                ")\r\n" +
                $"SELECT TOP ({AdoptionRankLimit}) DepartmentName AS Label,\r\n" +
                "       CAST(ROUND(100.0 * ActivePeople / PeopleInDepartment, 1) AS float) AS Value\r\n" +
                "FROM ByDepartment\r\n" +
                $"WHERE PeopleInDepartment >= {MinDepartmentSizeForRate}\r\n" +
                // Ordered on the UNROUNDED ratio, then size, then name. Ordering on the rounded value
                // alone leaves the cutoff between equal-looking departments to the plan, so which 25
                // appear could change between runs for no visible reason.
                "ORDER BY (100.0 * ActivePeople / PeopleInDepartment) ASC, PeopleInDepartment DESC, DepartmentName ASC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Per app, people who used it on the web and on no other platform in the window.
        /// </summary>
        /// <remarks>
        /// Collapsed per person first, then tested, so someone who used Excel on the web in January
        /// and on Windows in March is correctly NOT web-only. Testing row by row would count that
        /// person's January rows as web-only and overstate the figure.
        /// </remarks>
        internal static string WebOnlyQuery()
        {
            var cases = OfficeAppCatalogue.Select(app =>
            {
                var others = OfficePlatformCatalogue
                    .Where(pf => pf.Key != "Web")
                    .Select(pf => $"p.{app.Name}_{pf.Key} = 0");
                return $"        (N'{app.Name}', CASE WHEN p.{app.Name}_Web = 1 AND "
                       + string.Join(" AND ", others) + " THEN 1 ELSE 0 END)";
            });

            return
                PerUserCte(CrossColumns()) +
                "SELECT webonly.Label, CAST(SUM(webonly.IsWebOnly) AS float) AS Value\r\n" +
                "FROM PerUser AS p\r\n" +
                "CROSS APPLY (VALUES\r\n" +
                string.Join(",\r\n", cases) + "\r\n" +
                ") AS webonly(Label, IsWebOnly)\r\n" +
                "GROUP BY webonly.Label\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Per app, the share of that app's users who also used Copilot in the same app.
        /// </summary>
        /// <remarks>
        /// Two different sources meet here, and the join is the honest part. The denominator comes from
        /// <c>platform_user_activity_log</c> (everyone who used the app) and the numerator from
        /// <c>copilot_usage_user_activity_log</c> (whose per-app last-activity date fell inside the
        /// window). A <c>LEFT JOIN</c> on <c>user_id</c> keeps people who use the app and have no
        /// Copilot row at all in the denominator - an <c>INNER JOIN</c> would quietly restrict the
        /// whole calculation to Copilot-licensed people and report something close to 100% for every
        /// app, which is the exact opposite of what the chart is asked to show.
        /// <para>
        /// BOTH sides collapse to one row per person before fanning out. The Copilot report is also
        /// one row per user per snapshot date, so fanning it out first would expand every snapshot six
        /// ways - the same mistake the activity side was measured making. Taking <c>MAX</c> of each
        /// per-app last-activity date is the right collapse: the dates only move forwards, so the
        /// latest one answers "did they use Copilot in this app during the window".
        /// </para>
        /// </remarks>
        internal static string CopilotAttachRateQuery()
        {
            var copilotCollapse = string.Join(",\r\n", OfficeAppCatalogue.Select(a =>
                $"               MAX(r.{a.CopilotColumn}) AS {a.Name}"));

            var copilotValues = new StringBuilder();
            for (var i = 0; i < OfficeAppCatalogue.Length; i++)
            {
                var app = OfficeAppCatalogue[i];
                copilotValues.Append($"        (N'{app.Name}', c.{app.Name})");
                copilotValues.Append(i == OfficeAppCatalogue.Length - 1 ? "\r\n" : ",\r\n");
            }

            return
                PerUserCte(AppColumns()).TrimEnd('\r', '\n') + ",\r\n" +
                "AppUsers AS (\r\n" +
                "    SELECT app.AppName, p.user_id\r\n" +
                "    FROM PerUser AS p\r\n" +
                "    CROSS APPLY (VALUES\r\n" +
                ValuesClause(OfficeAppCatalogue.Select(a => a.Name).ToList(), "p", numbered: false) +
                "    ) AS app(AppName, Used)\r\n" +
                "    WHERE app.Used = 1\r\n" +
                "),\r\n" +
                "PerCopilotUser AS (\r\n" +
                "    SELECT r.user_id,\r\n" +
                copilotCollapse + "\r\n" +
                "    FROM dbo.copilot_usage_user_activity_log AS r\r\n" +
                "    WHERE r.[date] >= @from\r\n" +
                "    GROUP BY r.user_id\r\n" +
                "),\r\n" +
                "CopilotUsers AS (\r\n" +
                "    SELECT copilot.AppName, c.user_id\r\n" +
                "    FROM PerCopilotUser AS c\r\n" +
                "    CROSS APPLY (VALUES\r\n" + copilotValues +
                "    ) AS copilot(AppName, LastActivity)\r\n" +
                "    WHERE copilot.LastActivity >= @from\r\n" +
                ")\r\n" +
                "SELECT au.AppName AS Label,\r\n" +
                "       CAST(ROUND(100.0 * COUNT(cu.user_id) / COUNT(*), 1) AS float) AS Value\r\n" +
                "FROM AppUsers AS au\r\n" +
                "LEFT JOIN CopilotUsers AS cu\r\n" +
                "    ON cu.AppName = au.AppName AND cu.user_id = au.user_id\r\n" +
                "GROUP BY au.AppName\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        #endregion

        #region Runners

        /// <summary>
        /// Executes a weekly (SeriesKey, SeriesName, WeekStart, Value) query that takes only
        /// <c>@from</c>.
        /// </summary>
        /// <remarks>
        /// The existing overload also binds <c>@agentName</c> for the Copilot agent charts. Passing a
        /// parameter a query never mentions is legal but misleading, so this area gets its own
        /// overload rather than binding a permanently-null one.
        /// </remarks>
        private static async Task<List<NamedWeekValueRow>> QueryNamedWeeksAsync(string body, DateTime from)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.CommandTimeout = QueryTimeoutSecs;
                return await db.Database
                    .SqlQuery<NamedWeekValueRow>(body, new SqlParameter("@from", from))
                    .ToListAsync();
            }
        }

        /// <summary>Runs a (RowLabel, ColumnLabel, Value) query and shapes it into a matrix chart.</summary>
        /// <param name="rows">
        /// Fixed row order, or null to rank rows by total. Passing the catalogue keeps an app that
        /// nobody used visible as an empty row, which is usually the most interesting row on the grid.
        /// </param>
        /// <param name="columns">
        /// Fixed column order, or null to derive it from the returned cells. The SQL has already chosen
        /// WHICH columns come back, by distinct active headcount; this only puts them in order.
        /// </param>
        private static async Task<ReportChart> RunMatrixAsync(string key, string title, string description,
            string valueLabel, string body, DateTime from, string rowLabel, string columnLabel,
            List<string> rows, List<string> columns, bool shadeByRow)
        {
            var chart = new ReportChart
            {
                Key = key,
                Title = title,
                Description = description,
                Type = "matrix",
                ValueLabel = valueLabel,
                Sql = DisplaySql(body, from),
            };

            try
            {
                List<MatrixRow> result;
                using (var db = new AnalyticsEntitiesContext())
                {
                    db.Database.CommandTimeout = QueryTimeoutSecs;
                    result = await db.Database
                        .SqlQuery<MatrixRow>(body, new SqlParameter("@from", from))
                        .ToListAsync();
                }

                chart.Matrix = new ReportMatrix
                {
                    RowLabel = rowLabel,
                    ColumnLabel = columnLabel,
                    ShadeByRow = shadeByRow,
                    Rows = rows ?? DistinctInResultOrder(result, r => r.RowLabel),
                    Columns = columns ?? DistinctInResultOrder(result, r => r.ColumnLabel),
                    Cells = result
                        .Select(r => new ReportMatrixCell
                        {
                            Row = r.RowLabel,
                            Column = r.ColumnLabel,
                            Value = r.Value,
                        })
                        .ToList(),
                };
            }
            catch (Exception ex)
            {
                chart.Error = InnermostMessage(ex);
            }

            return chart;
        }

        /// <summary>Distinct labels in the order the query returned them.</summary>
        /// <remarks>
        /// Deliberately preserves the SQL's order rather than re-ranking here. The dimension matrices
        /// already choose AND order their columns by distinct active headcount and return them
        /// <c>ORDER BY DimensionRank</c>; re-ranking those same cells in C# would sort by the sum of
        /// the per-app counts, which is a different measure - so the grid would be ordered by
        /// something other than the figure its description says it is ordered by.
        /// </remarks>
        private static List<string> DistinctInResultOrder(List<MatrixRow> rows, Func<MatrixRow, string> selector)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            foreach (var row in rows)
            {
                var label = selector(row);
                if (label != null && seen.Add(label)) ordered.Add(label);
            }
            return ordered;
        }

        /// <summary>
        /// Post-processes a chart task to attach presentation-only flags the shared runners do not set.
        /// </summary>
        private static async Task<ReportChart> WithValueSuffix(
            Task<ReportChart> task, string valueSuffix, bool showShare = false)
        {
            var chart = await task;
            chart.ValueSuffix = valueSuffix;
            chart.ShowShare = showShare;
            return chart;
        }

        private sealed class MatrixRow
        {
            public string RowLabel { get; set; }
            public string ColumnLabel { get; set; }
            public double Value { get; set; }
        }

        #endregion
    }
}
