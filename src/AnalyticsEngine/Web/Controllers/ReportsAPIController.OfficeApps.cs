using Common.Entities;
using Common.Entities.Config;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text;
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
    /// (<c>App.ControlPanel.Engine/SqlExtentions/Profiling-03-CreateSchema.sql</c>). These charts use
    /// <c>COUNT(DISTINCT CASE WHEN bit = 1 ...)</c>, which is the same statement counted rather than
    /// stored.
    /// </para>
    /// <para>
    /// Deliberately NOT the snapshot pattern used by <c>UsageCharts</c>. That one picks the latest
    /// settled report date in each week and filters on <c>last_activity_date</c>, because the tables it
    /// reads (<c>teams_user_activity_log</c> and friends) carry running COUNTS that would be
    /// double-counted if several report dates in a week were added together. Here the columns are
    /// booleans and <c>COUNT(DISTINCT user_id)</c> is already idempotent across report dates, so the
    /// extra machinery would buy nothing and would throw away every user whose single report date in a
    /// week happened to be the unsettled one.
    /// </para>
    /// <para>
    /// <b>What is counted is a person, never an action.</b> The source has no volume - it cannot say
    /// whether someone sent one mail or four hundred. Every figure in this area is therefore a
    /// headcount, and the labels say "users" for that reason. Nothing here should ever be described as
    /// "usage" in the sense of intensity.
    /// </para>
    /// <para>
    /// <b>Scale.</b> At the ~200k-user tenant this product is designed against, this table grows by
    /// ~200k rows a day (~73m a year), so a 6-month window touches ~36m rows. Every query below is a
    /// single set-based pass with all aggregation in SQL - there is no per-user query anywhere - and
    /// each one fans the row out with <c>CROSS APPLY (VALUES ...)</c> so that six apps are answered by
    /// one read of the table rather than six. They all carry <c>OPTION (RECOMPILE)</c> so the real
    /// window drives the plan rather than a cached plan from a different one.
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
            var copilotUsageImported = CopilotUsageReportsEnabled();

            var charts = new List<Task<ReportChart>>
            {
                // ---- Headline: what is used, and is it moving? -------------------------------
                RunCategoryAsync("office-apps-popularity",
                    "Most used apps",
                    "People who used each app at least once in the period. Someone who uses both Word and Excel is counted in both, so these do not add up to your headcount.",
                    "People", AppPopularityQuery(), from),

                RunGroupedTimeSeriesAsync("office-apps-trend",
                    "App users each week",
                    "Distinct people using each app, week by week. The current part-week is left off because the Microsoft usage report for it has not finished arriving.",
                    "People",
                    QueryNamedWeeksAsync(AppWeeklyQuery(), from),
                    DisplaySql(AppWeeklyQuery(), from), weekSpine),

                WithValueSuffix(RunCategoryAsync("office-apps-breadth",
                    "How much of the suite people use",
                    "How many different Office apps each person used in the period. A workforce clustered on one or two apps has bought a suite and is using a product - that gap is usually the cheapest adoption win available.",
                    "People", AppBreadthQuery(), from), null, showShare: true),

                // ---- Platforms: where the work happens ---------------------------------------
                RunCategoryAsync("office-apps-platform-mix",
                    "Platforms people work on",
                    "People who connected from each platform at least once. Most people appear under more than one.",
                    "People", PlatformPopularityQuery(), from),

                RunGroupedTimeSeriesAsync("office-apps-platform-trend",
                    "Platform users each week",
                    "Distinct people on each platform, week by week. A rising web or mobile line against a flat desktop line is the shift to browser and phone working, and it changes what your endpoint and licensing assumptions should be.",
                    "People",
                    QueryNamedWeeksAsync(PlatformWeeklyQuery(), from),
                    DisplaySql(PlatformWeeklyQuery(), from), weekSpine),

                RunMatrixAsync("office-apps-platform-matrix",
                    "Which apps are used on which platforms",
                    "People using each app on each platform. Shaded across each row, because Outlook dwarfs OneNote and a single shared scale would leave the smaller rows blank.",
                    "People", AppPlatformMatrixQuery(), from,
                    rowLabel: "App", columnLabel: "Platform",
                    rows: OfficeAppCatalogue.Select(a => a.Name).ToList(),
                    columns: OfficePlatformCatalogue.Select(p => p.Key).ToList(),
                    shadeByRow: true),

                // ---- The business cut ---------------------------------------------------------
                RunMatrixAsync("office-apps-by-department",
                    "App use by department",
                    $"People using each app, in the {MatrixColumnLimit} departments with the most active people. Shaded across each row, so you are comparing departments within an app rather than apps against each other. Department comes from your directory; people with none are grouped as {NoDepartmentLabel}.",
                    "People", AppByDepartmentQuery(), from,
                    rowLabel: "App", columnLabel: "Department",
                    rows: OfficeAppCatalogue.Select(a => a.Name).ToList(),
                    columns: null,
                    shadeByRow: true),

                WithValueSuffix(RunCategoryAsync("office-apps-department-adoption",
                    "Departments least likely to use the apps",
                    $"Share of each department's people who used any Office app in the period, lowest first. Unlike the counts above this is a rate, so a big department cannot top it just for being big. Departments with fewer than {MinDepartmentSizeForRate} people are left out, because at that size the percentage says more about the size than the adoption.",
                    "Adoption", DepartmentAdoptionRateQuery(), from), "%"),

                RunMatrixAsync("office-apps-by-domain",
                    "App use by email domain",
                    $"People using each app, split by the domain of their sign-in address - useful where subsidiaries, brands or an unfinished migration share one tenant. Top {MatrixColumnLimit} domains by active people.",
                    "People", AppByDomainQuery(), from,
                    rowLabel: "App", columnLabel: "Domain",
                    rows: OfficeAppCatalogue.Select(a => a.Name).ToList(),
                    columns: null,
                    shadeByRow: true),

                RunCategoryAsync("office-apps-web-only",
                    "People who only ever use the browser version",
                    "People who used an app on the web and never on Windows, Mac or mobile in the period. Often unmanaged or personal devices, VDI users, or people who simply never had the desktop app installed - each of which needs a different response.",
                    "People", WebOnlyQuery(), from),
            };

            if (copilotUsageImported)
            {
                charts.Add(WithValueSuffix(RunCategoryAsync("office-apps-copilot-attach",
                    "Copilot take-up inside each app",
                    "Of the people who use an app, the share who used Copilot in that same app during the period. This is the question a Copilot business case turns on: not how many licences were bought, but whether Copilot reached people where they already work. A low bar against a tall bar in \"Most used apps\" is your largest untouched audience.",
                    "Take-up", CopilotAttachRateQuery(), from), "%"));

                charts.Add(RunGroupedTimeSeriesAsync("office-apps-copilot-trend",
                    "Copilot users by app each week",
                    "People whose most recent Copilot activity in each app fell in that week. Derived from the per-app last-activity dates on the Copilot usage report, so it shows the weeks someone was active rather than how much they did.",
                    "People",
                    QueryNamedWeeksAsync(CopilotWeeklyQuery(), from),
                    DisplaySql(CopilotWeeklyQuery(), from), weekSpine));
            }
            else
            {
                charts.Add(Task.FromResult(new ReportChart
                {
                    Key = "office-apps-copilot-attach",
                    Title = "Copilot take-up inside each app",
                    Type = "bar",
                    ValueLabel = "Take-up",
                    Description = "Of the people who use an app, the share who used Copilot in that same app.",
                    Categories = new List<ReportCategory>(),
                    Sql = DisplaySql(CopilotAttachRateQuery(), from),
                    Warning =
                        "The Copilot usage report import is switched off, so there is nothing to compare app use against. "
                        + "Enable 'Copilot usage reports (Graph)' in the installer to fill this chart in. The app and "
                        + "platform charts above do not need it.",
                }));
            }

            return charts;
        }

        /// <summary>
        /// Whether the Graph Copilot usage-report import is on.
        /// </summary>
        /// <remarks>
        /// Read once per request rather than per chart so the two Copilot charts and the explanatory
        /// warning can never disagree about whether the source exists.
        /// </remarks>
        private static bool CopilotUsageReportsEnabled()
        {
            var settings = new AppConfig().ImportJobSettings ?? new ImportTaskSettings();
            return settings.GraphCopilotUsageReports;
        }

        #region Query builders

        /// <summary>
        /// A <c>CROSS APPLY (VALUES ...)</c> body that turns one activity row into one row per app.
        /// </summary>
        /// <remarks>
        /// This shape is why the area costs one table read rather than six. Written as six correlated
        /// subqueries - or six separate chart queries - a 36m-row window would be scanned six times;
        /// fanning the row out in the apply lets a single pass answer every app at once, and the
        /// <c>Used = 1</c> predicate discards the unset bits immediately so the expansion never
        /// materialises for apps a person does not use.
        /// </remarks>
        private static string AppValuesClause(string alias, bool numbered)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < OfficeAppCatalogue.Length; i++)
            {
                var app = OfficeAppCatalogue[i];
                var prefix = numbered ? (i + 1) + ", " : string.Empty;
                builder.Append("        (").Append(prefix)
                    .Append("N'").Append(app.Name).Append("', ")
                    .Append(alias).Append(".").Append(app.Column).Append(")");
                builder.Append(i == OfficeAppCatalogue.Length - 1 ? "\r\n" : ",\r\n");
            }
            return builder.ToString();
        }

        private static string PlatformValuesClause(string alias, bool numbered)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < OfficePlatformCatalogue.Length; i++)
            {
                var platform = OfficePlatformCatalogue[i];
                var prefix = numbered ? (i + 1) + ", " : string.Empty;
                builder.Append("        (").Append(prefix)
                    .Append("N'").Append(platform.Key).Append("', ")
                    .Append(alias).Append(".").Append(platform.Value).Append(")");
                builder.Append(i == OfficePlatformCatalogue.Length - 1 ? "\r\n" : ",\r\n");
            }
            return builder.ToString();
        }

        /// <summary>People who used each app at least once in the window.</summary>
        internal static string AppPopularityQuery()
        {
            return
                "SELECT app.Label, CAST(COUNT(DISTINCT a.user_id) AS float) AS Value\r\n" +
                "FROM dbo.platform_user_activity_log AS a\r\n" +
                "CROSS APPLY (VALUES\r\n" + AppValuesClause("a", numbered: false) +
                ") AS app(Label, Used)\r\n" +
                "WHERE a.[date] >= @from AND app.Used = 1\r\n" +
                "GROUP BY app.Label\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>People who connected from each platform at least once in the window.</summary>
        internal static string PlatformPopularityQuery()
        {
            return
                "SELECT platform.Label, CAST(COUNT(DISTINCT a.user_id) AS float) AS Value\r\n" +
                "FROM dbo.platform_user_activity_log AS a\r\n" +
                "CROSS APPLY (VALUES\r\n" + PlatformValuesClause("a", numbered: false) +
                ") AS platform(Label, Used)\r\n" +
                "WHERE a.[date] >= @from AND platform.Used = 1\r\n" +
                "GROUP BY platform.Label\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>Distinct people per app per Monday-aligned week.</summary>
        internal static string AppWeeklyQuery()
        {
            var week = WeekBucket("a.[date]");
            return
                $"SELECT app.SeriesKey, app.SeriesName, {week} AS WeekStart,\r\n" +
                "       CAST(COUNT(DISTINCT a.user_id) AS float) AS Value\r\n" +
                "FROM dbo.platform_user_activity_log AS a\r\n" +
                "CROSS APPLY (VALUES\r\n" + AppValuesClause("a", numbered: true) +
                ") AS app(SeriesKey, SeriesName, Used)\r\n" +
                "WHERE a.[date] >= @from AND app.Used = 1\r\n" +
                $"GROUP BY app.SeriesKey, app.SeriesName, {week}\r\n" +
                "ORDER BY WeekStart\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>Distinct people per platform per Monday-aligned week.</summary>
        internal static string PlatformWeeklyQuery()
        {
            var week = WeekBucket("a.[date]");
            return
                $"SELECT platform.SeriesKey, platform.SeriesName, {week} AS WeekStart,\r\n" +
                "       CAST(COUNT(DISTINCT a.user_id) AS float) AS Value\r\n" +
                "FROM dbo.platform_user_activity_log AS a\r\n" +
                "CROSS APPLY (VALUES\r\n" + PlatformValuesClause("a", numbered: true) +
                ") AS platform(SeriesKey, SeriesName, Used)\r\n" +
                "WHERE a.[date] >= @from AND platform.Used = 1\r\n" +
                $"GROUP BY platform.SeriesKey, platform.SeriesName, {week}\r\n" +
                "ORDER BY WeekStart\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// How many distinct apps each person used, bucketed into "1 app", "2 apps", ...
        /// </summary>
        /// <remarks>
        /// <c>MAX(1 * bit)</c> per app then summed is the same expression <c>usp_UpsertM365Apps</c>
        /// uses to collapse a person's report dates down to "did they, at any point". People with a
        /// row but no app bit set are excluded rather than shown as "0 apps": the report emits a row
        /// for every licensed person whether or not they did anything, so a zero bucket would be
        /// dominated by accounts that are simply dormant and would swamp the shape of the chart.
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
            var builder = new StringBuilder();
            var pairs = new List<string>();
            foreach (var app in OfficeAppCatalogue)
            {
                foreach (var platform in OfficePlatformCatalogue)
                {
                    pairs.Add($"        (N'{app.Name}', N'{platform.Key}', a.{app.Column}_{platform.Value})");
                }
            }
            builder.Append(string.Join(",\r\n", pairs)).Append("\r\n");

            return
                "SELECT combo.RowLabel, combo.ColumnLabel,\r\n" +
                "       CAST(COUNT(DISTINCT a.user_id) AS float) AS Value\r\n" +
                "FROM dbo.platform_user_activity_log AS a\r\n" +
                "CROSS APPLY (VALUES\r\n" + builder +
                ") AS combo(RowLabel, ColumnLabel, Used)\r\n" +
                "WHERE a.[date] >= @from AND combo.Used = 1\r\n" +
                "GROUP BY combo.RowLabel, combo.ColumnLabel\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>People using each app, by directory department, for the biggest departments.</summary>
        /// <remarks>
        /// The top-N is chosen with a window function over the already-aggregated counts rather than
        /// by a second CTE that re-reads the activity data. That is not a style preference: SQL Server
        /// does not materialise a CTE, so referencing the join-heavy CTE twice made it run twice.
        /// Measured at 18m rows, the two-reference shape of the sibling domain query cost 55.5 MILLION
        /// logical reads and 53 seconds for a 30-day window, because the optimiser chose a nested-loops
        /// seek into <c>dbo.users</c> and then did the whole thing again for the ranking pass.
        /// Aggregating once and ranking the (tiny) result with <c>DENSE_RANK</c> reads the activity
        /// data exactly once.
        /// </remarks>
        internal static string AppByDepartmentQuery()
        {
            return
                "WITH ActiveApp AS (\r\n" +
                "    SELECT app.AppName, a.user_id\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    CROSS APPLY (VALUES\r\n" + AppValuesClause("a", numbered: false) +
                "    ) AS app(AppName, Used)\r\n" +
                "    WHERE a.[date] >= @from AND app.Used = 1\r\n" +
                "    GROUP BY app.AppName, a.user_id\r\n" +
                "),\r\n" +
                "Counts AS (\r\n" +
                "    SELECT act.AppName,\r\n" +
                $"           ISNULL(dep.[name], N'{NoDepartmentLabel}') AS DepartmentName,\r\n" +
                "           COUNT(DISTINCT act.user_id) AS People\r\n" +
                "    FROM ActiveApp AS act\r\n" +
                "    INNER JOIN dbo.users AS u ON u.id = act.user_id\r\n" +
                "    LEFT JOIN dbo.user_departments AS dep ON dep.id = u.department_id\r\n" +
                $"    GROUP BY act.AppName, ISNULL(dep.[name], N'{NoDepartmentLabel}')\r\n" +
                "),\r\n" +
                "Ranked AS (\r\n" +
                "    SELECT AppName, DepartmentName, People,\r\n" +
                "           DENSE_RANK() OVER (ORDER BY DepartmentTotal DESC, DepartmentName) AS DepartmentRank\r\n" +
                "    FROM (\r\n" +
                "        SELECT AppName, DepartmentName, People,\r\n" +
                "               SUM(People) OVER (PARTITION BY DepartmentName) AS DepartmentTotal\r\n" +
                "        FROM Counts\r\n" +
                "    ) AS totals\r\n" +
                ")\r\n" +
                "SELECT AppName AS RowLabel, DepartmentName AS ColumnLabel, CAST(People AS float) AS Value\r\n" +
                "FROM Ranked\r\n" +
                $"WHERE DepartmentRank <= {MatrixColumnLimit}\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Share of each department's people who used any Office app in the window.
        /// </summary>
        /// <remarks>
        /// The denominator is every person in <c>dbo.users</c> carrying that department, NOT just the
        /// people who appear in the activity table. That distinction is the whole point of the chart:
        /// dividing active people by active people would return 100% for every department and say
        /// nothing. It does mean the rate is only as good as the directory - a department full of
        /// stale or unlicensed accounts will read low, which is itself usually worth knowing.
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
                "ORDER BY Value ASC, PeopleInDepartment DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// People using each app, by the domain part of their sign-in address.
        /// </summary>
        /// <remarks>
        /// The domain is taken after the LAST <c>@</c>, matching
        /// <c>CopilotUsageReportPolicy.DomainOf</c>, so an address that somehow contains more than one
        /// is split the same way here as it is on the import side. <c>users.user_name</c> is written
        /// lower-cased by the importer, so no further folding is applied - doing it anyway would imply
        /// the column is unreliable and would cost a scan-wide function call for nothing.
        /// <para>
        /// Aggregated once and then ranked with a window function, for the reason documented on
        /// <see cref="AppByDepartmentQuery"/>: this query is where the two-reference CTE shape was
        /// caught costing 55.5 million logical reads and 53 seconds at 18m rows.
        /// </para>
        /// </remarks>
        internal static string AppByDomainQuery()
        {
            return
                "WITH ActiveApp AS (\r\n" +
                "    SELECT app.AppName, a.user_id\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    CROSS APPLY (VALUES\r\n" + AppValuesClause("a", numbered: false) +
                "    ) AS app(AppName, Used)\r\n" +
                "    WHERE a.[date] >= @from AND app.Used = 1\r\n" +
                "    GROUP BY app.AppName, a.user_id\r\n" +
                "),\r\n" +
                "Counts AS (\r\n" +
                "    SELECT act.AppName,\r\n" +
                "           CASE WHEN CHARINDEX('@', u.user_name) > 0\r\n" +
                "                THEN RIGHT(u.user_name, CHARINDEX('@', REVERSE(u.user_name)) - 1)\r\n" +
                $"                ELSE N'{NoDomainLabel}'\r\n" +
                "           END AS DomainName,\r\n" +
                "           COUNT(DISTINCT act.user_id) AS People\r\n" +
                "    FROM ActiveApp AS act\r\n" +
                "    INNER JOIN dbo.users AS u ON u.id = act.user_id\r\n" +
                "    GROUP BY act.AppName,\r\n" +
                "             CASE WHEN CHARINDEX('@', u.user_name) > 0\r\n" +
                "                  THEN RIGHT(u.user_name, CHARINDEX('@', REVERSE(u.user_name)) - 1)\r\n" +
                $"                  ELSE N'{NoDomainLabel}'\r\n" +
                "             END\r\n" +
                "),\r\n" +
                "Ranked AS (\r\n" +
                "    SELECT AppName, DomainName, People,\r\n" +
                "           DENSE_RANK() OVER (ORDER BY DomainTotal DESC, DomainName) AS DomainRank\r\n" +
                "    FROM (\r\n" +
                "        SELECT AppName, DomainName, People,\r\n" +
                "               SUM(People) OVER (PARTITION BY DomainName) AS DomainTotal\r\n" +
                "        FROM Counts\r\n" +
                "    ) AS totals\r\n" +
                ")\r\n" +
                "SELECT AppName AS RowLabel, DomainName AS ColumnLabel, CAST(People AS float) AS Value\r\n" +
                "FROM Ranked\r\n" +
                $"WHERE DomainRank <= {MatrixColumnLimit}\r\n" +
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
            var selects = new List<string>();
            foreach (var app in OfficeAppCatalogue)
            {
                foreach (var platform in OfficePlatformCatalogue)
                {
                    selects.Add(
                        $"           MAX(1 * a.{app.Column}_{platform.Value}) AS {app.Name}_{platform.Key}");
                }
            }

            var cases = OfficeAppCatalogue.Select(app =>
            {
                var others = OfficePlatformCatalogue
                    .Where(p => p.Key != "Web")
                    .Select(p => $"p.{app.Name}_{p.Key} = 0");
                return $"        (N'{app.Name}', CASE WHEN p.{app.Name}_Web = 1 AND "
                       + string.Join(" AND ", others) + " THEN 1 ELSE 0 END)";
            });

            return
                "WITH PerUser AS (\r\n" +
                "    SELECT a.user_id,\r\n" +
                string.Join(",\r\n", selects) + "\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    WHERE a.[date] >= @from\r\n" +
                "    GROUP BY a.user_id\r\n" +
                ")\r\n" +
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
        /// </remarks>
        internal static string CopilotAttachRateQuery()
        {
            var copilotValues = new StringBuilder();
            for (var i = 0; i < OfficeAppCatalogue.Length; i++)
            {
                var app = OfficeAppCatalogue[i];
                copilotValues.Append($"        (N'{app.Name}', r.{app.CopilotColumn})");
                copilotValues.Append(i == OfficeAppCatalogue.Length - 1 ? "\r\n" : ",\r\n");
            }

            return
                "WITH AppUsers AS (\r\n" +
                "    SELECT app.AppName, a.user_id\r\n" +
                "    FROM dbo.platform_user_activity_log AS a\r\n" +
                "    CROSS APPLY (VALUES\r\n" + AppValuesClause("a", numbered: false) +
                "    ) AS app(AppName, Used)\r\n" +
                "    WHERE a.[date] >= @from AND app.Used = 1\r\n" +
                "    GROUP BY app.AppName, a.user_id\r\n" +
                "),\r\n" +
                "CopilotUsers AS (\r\n" +
                "    SELECT copilot.AppName, r.user_id\r\n" +
                "    FROM dbo.copilot_usage_user_activity_log AS r\r\n" +
                "    CROSS APPLY (VALUES\r\n" + copilotValues +
                "    ) AS copilot(AppName, LastActivity)\r\n" +
                "    WHERE r.[date] >= @from AND copilot.LastActivity >= @from\r\n" +
                "    GROUP BY copilot.AppName, r.user_id\r\n" +
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

        /// <summary>
        /// Distinct people with Copilot activity in each app, per Monday-aligned week.
        /// </summary>
        /// <remarks>
        /// Bucketed on the per-app last-activity date rather than the report date, which is what makes
        /// this a real weekly series. The report is emitted daily and the date is sticky, so a person
        /// who used Copilot in Word on a Tuesday carries that Tuesday on every later snapshot until
        /// they use it again: they are counted exactly once in that week by the <c>DISTINCT</c>, and
        /// again in a later week only if they were genuinely active again. It cannot show a second
        /// session in the same week, which is why the label counts people and not activity.
        /// </remarks>
        internal static string CopilotWeeklyQuery()
        {
            var copilotValues = new StringBuilder();
            for (var i = 0; i < OfficeAppCatalogue.Length; i++)
            {
                var app = OfficeAppCatalogue[i];
                copilotValues.Append($"        ({i + 1}, N'{app.Name}', r.{app.CopilotColumn})");
                copilotValues.Append(i == OfficeAppCatalogue.Length - 1 ? "\r\n" : ",\r\n");
            }

            var week = WeekBucket("copilot.LastActivity");
            return
                $"SELECT copilot.SeriesKey, copilot.SeriesName, {week} AS WeekStart,\r\n" +
                "       CAST(COUNT(DISTINCT r.user_id) AS float) AS Value\r\n" +
                "FROM dbo.copilot_usage_user_activity_log AS r\r\n" +
                "CROSS APPLY (VALUES\r\n" + copilotValues +
                ") AS copilot(SeriesKey, SeriesName, LastActivity)\r\n" +
                "WHERE r.[date] >= @from AND copilot.LastActivity >= @from\r\n" +
                $"GROUP BY copilot.SeriesKey, copilot.SeriesName, {week}\r\n" +
                "ORDER BY WeekStart\r\n" +
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
        /// Fixed column order, or null to rank columns by total descending - which is what the
        /// department and domain matrices want, since their columns are discovered from the data.
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
                    Rows = rows ?? RankLabels(result, r => r.RowLabel),
                    Columns = columns ?? RankLabels(result, r => r.ColumnLabel),
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

        /// <summary>Distinct labels ordered by their total value, largest first, then by name.</summary>
        private static List<string> RankLabels(List<MatrixRow> rows, Func<MatrixRow, string> selector)
        {
            return rows
                .GroupBy(selector)
                .OrderByDescending(g => g.Sum(r => r.Value))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .ToList();
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
