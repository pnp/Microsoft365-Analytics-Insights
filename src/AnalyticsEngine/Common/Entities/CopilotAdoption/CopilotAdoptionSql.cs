using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Every query behind the Copilot licence-adoption tool, built as strings so each one can be
    /// asserted in a unit test and shown to the admin in the SQL popover the rest of the admin site
    /// uses. No query here is assembled from user input: the only values interpolated are integer
    /// licence-type ids that this code read out of <c>dbo.license_types</c> itself, and constants from
    /// <see cref="CopilotAdoptionOptions"/>. Everything a caller supplies is a real SQL parameter.
    ///
    /// Performance notes (the target is a ~200,000-user tenant):
    ///
    /// <list type="bullet">
    ///   <item>Queries hit base tables, never the <c>vw*</c> views, matching the Reports area.</item>
    ///   <item>The licensed population is derived from <c>user_license_type_lookups</c> filtered by
    ///   licence-type id. That table has a unique index on <c>(license_type_id, user_id)</c>, so this
    ///   is an index-only seek on the leading column - not a scan of <c>users</c>.</item>
    ///   <item>The Copilot audit history is read <b>once</b> per report, over a bounded date range,
    ///   with the window-versus-history split done by <c>CASE</c> inside a single aggregate. Running
    ///   separate "in the window" and "ever" passes would double the cost of the most expensive join
    ///   in the product.</item>
    ///   <item>Microsoft 365 usage figures come from <b>one</b> snapshot date, resolved up-front, so
    ///   each usage table is an equality seek on its <c>IX_date</c> index rather than a range scan.</item>
    ///   <item>The unlicensed candidate list is driven <i>from the activity tables</i> and anti-joined
    ///   against the licensed set. Driving it from <c>users</c> would scan every account in the tenant
    ///   to discard the overwhelming majority that have no activity at all.</item>
    ///   <item><c>OPTION (RECOMPILE)</c> throughout, so the real window drives the plan - the same
    ///   reasoning (and the same measurements) as the Reports area.</item>
    /// </list>
    /// </summary>
    public static class CopilotAdoptionSql
    {
        /// <summary>
        /// The join from <c>copilot_chats</c> to its audit event. Copilot interactions have no date of
        /// their own; the audit event carries the timestamp and the user. Deliberately un-hinted: a
        /// forced MERGE join was measured on this join in the Reports area and degraded to a full scan
        /// of <c>audit_events</c> at every window. Do not add a hint here without re-measuring.
        /// </summary>
        public const string AuditJoin = "INNER JOIN dbo.audit_events AS au ON c.event_id = au.id";

        /// <summary>
        /// Microsoft 365 Copilot Cowork surfaces itself in the audit log as the app host "cowork" and,
        /// where the agent was dimensioned, as a first-party agent whose id starts
        /// <c>Copilot.M365Copilot.Cowork</c>. Both are checked: the app host is present on every
        /// interaction, while the agent id only exists once the agent has been imported.
        /// </summary>
        public const string CoworkAppHost = "cowork";

        /// <summary>Finds the <c>copilot_agents</c> rows that represent Cowork.</summary>
        public const string CoworkAgentIdsSql =
            "SELECT ag.id AS Value\r\n" +
            "FROM dbo.copilot_agents AS ag\r\n" +
            "WHERE ag.agent_id LIKE 'Copilot.M365Copilot.Cowork%'\r\n" +
            "   OR ag.name LIKE '%Cowork%';";

        /// <summary>
        /// Every licence type with how many users hold it, so the tool can classify them and show the
        /// admin exactly which SKUs it counted as Copilot seats.
        /// </summary>
        public const string LicenceTypesSql =
            "SELECT lt.id AS Id,\r\n" +
            "       lt.name AS Name,\r\n" +
            "       lt.sku_id AS SkuPartNumber,\r\n" +
            "       COUNT(ul.user_id) AS AssignedUsers\r\n" +
            "FROM dbo.license_types AS lt\r\n" +
            "LEFT JOIN dbo.user_license_type_lookups AS ul ON ul.license_type_id = lt.id\r\n" +
            "GROUP BY lt.id, lt.name, lt.sku_id\r\n" +
            "ORDER BY AssignedUsers DESC, lt.name;";

        /// <summary>
        /// The most recent per-user Copilot usage-report snapshot that Microsoft has settled.
        /// Resolved separately so the detail query can seek one date instead of scanning a range.
        /// </summary>
        public const string LatestCopilotReportDateSql =
            "SELECT MAX(r.[date]) AS Value\r\n" +
            "FROM dbo.copilot_usage_user_activity_log AS r\r\n" +
            "WHERE r.[date] <= @settled;";

        /// <summary>
        /// The report period to read for a given snapshot date, chosen as the one closest to the analysis
        /// window.
        /// </summary>
        /// <remarks>
        /// <c>copilot_usage_user_activity_log</c> is unique on <c>(date, user_id, report_period_days)</c>:
        /// Graph publishes D7, D28, D90 and D180, and a user has a separate row per period on the same date
        /// with different prompt and active-day counts. Selecting a snapshot by date alone therefore returns
        /// several rows per user, and joining that to <c>users</c> multiplies every licensed user by the
        /// number of stored periods - inflating adoption counts (which can then exceed the licensed
        /// population) and burning the row cap on duplicates. The period has to be pinned as well.
        /// Returns 0 when the column is NULL for every row on that date, which is how reports imported
        /// before the period was recorded are handled.
        /// </remarks>
        public const string LatestCopilotReportPeriodSql =
            "SELECT TOP (1) ISNULL(r.report_period_days, 0) AS Value\r\n" +
            "FROM dbo.copilot_usage_user_activity_log AS r\r\n" +
            "WHERE r.[date] = @copilotReportDate\r\n" +
            "GROUP BY r.report_period_days\r\n" +
            "ORDER BY CASE WHEN r.report_period_days IS NULL THEN 1 ELSE 0 END,\r\n" +
            "         ABS(ISNULL(r.report_period_days, 0) - @windowDays),\r\n" +
            "         ISNULL(r.report_period_days, 0);";

        /// <summary>
        /// The most recent settled Microsoft 365 usage-report snapshot. Teams is used as the reference
        /// workload because every tenant that imports usage reports at all imports Teams, and mixing
        /// snapshot dates across workloads would compare a user's Monday against someone else's Friday.
        /// </summary>
        public const string LatestM365ReportDateSql =
            "SELECT MAX(t.[date]) AS Value\r\n" +
            "FROM dbo.teams_user_activity_log AS t\r\n" +
            "WHERE t.[date] <= @settled;";

        /// <summary>
        /// Whether the last per-user Copilot usage-report import came back with hashed identities
        /// (the tenant's "concealed user information" setting). That makes Microsoft's per-user numbers
        /// unusable while leaving the audit-derived ones untouched, which is worth saying out loud
        /// rather than letting the two sources appear to contradict each other.
        /// </summary>
        public const string CopilotReportObfuscatedSql =
            "SELECT TOP 1 CAST(l.is_upn_obfuscated AS int) AS Value\r\n" +
            "FROM dbo.copilot_usage_report_import_log AS l\r\n" +
            "WHERE l.report_name = 'getMicrosoft365CopilotUsageUserDetail'\r\n" +
            "ORDER BY l.imported_utc DESC;";

        /// <summary>Cheap existence probe: has any Copilot interaction been imported in the window?</summary>
        public const string HasCopilotAuditDataSql =
            "SELECT CASE WHEN EXISTS (\r\n" +
            "    SELECT 1 FROM dbo.copilot_chats AS c\r\n" +
            "    " + AuditJoin + "\r\n" +
            "    WHERE au.time_stamp >= @from\r\n" +
            ") THEN 1 ELSE 0 END AS Value;";

        #region Licensed users

        /// <summary>
        /// One row per (user, Copilot seat SKU). Used both to name the seats a user holds and to count
        /// the licensed population exactly - the detail query is capped, so it cannot be trusted for a
        /// headline figure, whereas this index-only read can.
        /// </summary>
        public static string SeatAssignmentsSql(IEnumerable<int> seatLicenceTypeIds)
        {
            return
                "SELECT ul.user_id AS UserId,\r\n" +
                "       lt.name AS LicenceName\r\n" +
                "FROM dbo.user_license_type_lookups AS ul\r\n" +
                "JOIN dbo.license_types AS lt ON lt.id = ul.license_type_id\r\n" +
                $"WHERE ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                "ORDER BY ul.user_id;";
        }

        /// <summary>
        /// The core query: every Copilot-licensed user with their metadata, their audit-derived usage
        /// split into "inside the reporting window" and "earlier", and Microsoft's own per-user figures
        /// from the latest settled snapshot.
        ///
        /// The window/history split is what separates a user who has never touched Copilot from one who
        /// tried it and stopped - the single most valuable distinction this tool makes, and the reason
        /// the aggregate reaches back to <c>@historyFrom</c> rather than only <c>@from</c>.
        ///
        /// No scoring happens here. Ranking, banding and percentages are all applied in
        /// <see cref="CopilotAdoptionScoring"/> so there is exactly one implementation of the rules.
        /// </summary>
        /// <param name="seatLicenceTypeIds">Licence-type ids classified as Copilot seats.</param>
        /// <param name="coworkAgentIds">Agent ids identified as Cowork; may be empty.</param>
        /// <param name="includeCopilotReport">
        /// False when Microsoft's per-user report has no usable snapshot, in which case its joins are
        /// omitted entirely rather than joined against a NULL date.
        /// </param>
        public static string LicensedUsersSql(
            IEnumerable<int> seatLicenceTypeIds,
            IEnumerable<int> coworkAgentIds,
            bool includeCopilotReport)
        {
            var cowork = CoworkPredicate(coworkAgentIds);

            var sql =
                "WITH SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                "    FROM dbo.user_license_type_lookups AS ul\r\n" +
                $"    WHERE ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                "),\r\n" +
                "-- One bounded pass over the Copilot audit history, projected to just the four columns\r\n" +
                "-- the aggregates need. The reporting window and the earlier history are separated with\r\n" +
                "-- CASE rather than by running the join twice.\r\n" +
                "CopilotWindow AS (\r\n" +
                "    SELECT au.user_id AS user_id,\r\n" +
                "           au.time_stamp AS time_stamp,\r\n" +
                "           c.app_host AS app_host,\r\n" +
                "           c.agent_id AS agent_id\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    " + AuditJoin + "\r\n" +
                "    JOIN SeatUsers AS seats ON seats.user_id = au.user_id\r\n" +
                "    WHERE au.time_stamp >= @historyFrom\r\n" +
                "),\r\n" +
                "-- The counting totals: cheap, because none of them is a DISTINCT.\r\n" +
                "CopilotTotals AS (\r\n" +
                "    SELECT c.user_id AS user_id,\r\n" +
                "           SUM(CASE WHEN c.time_stamp >= @from THEN 1 ELSE 0 END) AS Interactions,\r\n" +
                "           SUM(CASE WHEN c.time_stamp <  @from THEN 1 ELSE 0 END) AS PriorInteractions,\r\n" +
                $"           SUM(CASE WHEN c.time_stamp >= @from AND ({cowork}) THEN 1 ELSE 0 END) AS CoworkInteractions,\r\n" +
                "           MIN(c.time_stamp) AS FirstInteractionUtc,\r\n" +
                "           MAX(c.time_stamp) AS LastInteractionUtc\r\n" +
                "    FROM CopilotWindow AS c\r\n" +
                "    GROUP BY c.user_id\r\n" +
                "),\r\n" +
                "-- Each distinct count comes from its own pre-projected DISTINCT set.\r\n" +
                "--\r\n" +
                "-- Asking for these three as distinct aggregates inside CopilotTotals is the obvious way to\r\n" +
                "-- write it and is catastrophically slow: SQL Server can stream a single distinct aggregate,\r\n" +
                "-- but two or more in one grouping force it to fan the input out through a spool and process\r\n" +
                "-- each distinct separately. Measured on a 200k-user / 12M-interaction synthetic tenant, that\r\n" +
                "-- spool alone was 25M of the query's 115M logical reads and the whole query took 281s (28-day\r\n" +
                "-- window) - past the 90s command timeout, so the report degraded to a warning. Split this way\r\n" +
                "-- it is 772k reads and 73s. Full numbers are in the pull request and the wiki.\r\n" +
                "CopilotActiveDays AS (\r\n" +
                "    SELECT user_id, COUNT(*) AS ActiveDays\r\n" +
                "    FROM (SELECT DISTINCT user_id, CAST(time_stamp AS date) AS active_date\r\n" +
                "          FROM CopilotWindow WHERE time_stamp >= @from) AS d\r\n" +
                "    GROUP BY user_id\r\n" +
                "),\r\n" +
                "CopilotApps AS (\r\n" +
                "    SELECT user_id, COUNT(*) AS AppsUsed\r\n" +
                "    FROM (SELECT DISTINCT user_id, app_host\r\n" +
                "          FROM CopilotWindow WHERE time_stamp >= @from AND app_host IS NOT NULL) AS a\r\n" +
                "    GROUP BY user_id\r\n" +
                "),\r\n" +
                "CopilotAgents AS (\r\n" +
                "    SELECT user_id, COUNT(*) AS AgentsUsed\r\n" +
                "    FROM (SELECT DISTINCT user_id, agent_id\r\n" +
                "          FROM CopilotWindow WHERE time_stamp >= @from AND agent_id IS NOT NULL) AS g\r\n" +
                "    GROUP BY user_id\r\n" +
                "),\r\n" +
                "-- Reassembled under the original name and shape, so everything downstream is unchanged.\r\n" +
                "CopilotUsage AS (\r\n" +
                "    SELECT t.user_id AS user_id,\r\n" +
                "           t.Interactions AS Interactions,\r\n" +
                "           t.PriorInteractions AS PriorInteractions,\r\n" +
                "           ISNULL(d.ActiveDays, 0) AS ActiveDays,\r\n" +
                "           ISNULL(a.AppsUsed, 0) AS AppsUsed,\r\n" +
                "           ISNULL(g.AgentsUsed, 0) AS AgentsUsed,\r\n" +
                "           t.CoworkInteractions AS CoworkInteractions,\r\n" +
                "           t.FirstInteractionUtc AS FirstInteractionUtc,\r\n" +
                "           t.LastInteractionUtc AS LastInteractionUtc\r\n" +
                "    FROM CopilotTotals AS t\r\n" +
                "    LEFT JOIN CopilotActiveDays AS d ON d.user_id = t.user_id\r\n" +
                "    LEFT JOIN CopilotApps AS a ON a.user_id = t.user_id\r\n" +
                "    LEFT JOIN CopilotAgents AS g ON g.user_id = t.user_id\r\n" +
                ")";

            if (includeCopilotReport)
            {
                sql +=
                    ",\r\n" +
                    "-- Microsoft's own per-user figures, from a single settled snapshot date.\r\n" +
                    "ReportSnapshot AS (\r\n" +
                    "    SELECT r.user_id AS user_id,\r\n" +
                    "           r.prompts_all_apps AS ReportPrompts,\r\n" +
                    "           r.active_usage_days AS ReportActiveDays,\r\n" +
                    "           " + ReportAppsUsedExpression() + " AS ReportAppsUsed,\r\n" +
                    "           " + ReportLastActivityExpression() + " AS ReportLastActivityUtc,\r\n" +
                    "           r.agent_last_activity_date AS ReportAgentLastActivityUtc\r\n" +
                    "    FROM dbo.copilot_usage_user_activity_log AS r\r\n" +
                    // Both the date AND the period are pinned: the table holds one row per
                    // (date, user, period), so filtering on date alone returns D7/D28/D90/D180 rows for the
                    // same user and fans every licensed user out by the number of stored periods.
                    "    WHERE r.[date] = @copilotReportDate\r\n" +
                    "      AND ((@copilotReportPeriodDays > 0 AND r.report_period_days = @copilotReportPeriodDays)\r\n" +
                    "           OR (@copilotReportPeriodDays = 0 AND r.report_period_days IS NULL))\r\n" +
                    ")";
            }

            sql +=
                "\r\n" +
                "SELECT TOP (@maxRows)\r\n" +
                "       u.id AS UserId,\r\n" +
                "       u.user_name AS UserPrincipalName,\r\n" +
                "       u.mail AS Mail,\r\n" +
                "       dept.name AS Department,\r\n" +
                "       title.name AS JobTitle,\r\n" +
                "       country.name AS Country,\r\n" +
                "       office.name AS OfficeLocation,\r\n" +
                "       company.name AS CompanyName,\r\n" +
                "       manager.user_name AS ManagerUserPrincipalName,\r\n" +
                "       u.account_enabled AS AccountEnabled,\r\n" +
                "       CAST(ISNULL(chats.Interactions, 0) AS bigint) AS Interactions,\r\n" +
                "       CAST(ISNULL(chats.PriorInteractions, 0) AS bigint) AS PriorInteractions,\r\n" +
                "       ISNULL(chats.ActiveDays, 0) AS ActiveDays,\r\n" +
                "       ISNULL(chats.AppsUsed, 0) AS AppsUsed,\r\n" +
                "       ISNULL(chats.AgentsUsed, 0) AS AgentsUsed,\r\n" +
                "       CAST(ISNULL(chats.CoworkInteractions, 0) AS bigint) AS CoworkInteractions,\r\n" +
                "       chats.FirstInteractionUtc AS FirstInteractionUtc,\r\n" +
                "       chats.LastInteractionUtc AS LastInteractionUtc,\r\n" +
                (includeCopilotReport
                    ? "       report.ReportPrompts AS ReportPrompts,\r\n" +
                      "       report.ReportActiveDays AS ReportActiveDays,\r\n" +
                      "       report.ReportAppsUsed AS ReportAppsUsed,\r\n" +
                      "       report.ReportLastActivityUtc AS ReportLastActivityUtc,\r\n" +
                      "       report.ReportAgentLastActivityUtc AS ReportAgentLastActivityUtc\r\n"
                    : "       CAST(NULL AS int) AS ReportPrompts,\r\n" +
                      "       CAST(NULL AS int) AS ReportActiveDays,\r\n" +
                      "       CAST(NULL AS int) AS ReportAppsUsed,\r\n" +
                      "       CAST(NULL AS datetime) AS ReportLastActivityUtc,\r\n" +
                      "       CAST(NULL AS datetime) AS ReportAgentLastActivityUtc\r\n") +
                "FROM SeatUsers AS seats\r\n" +
                "JOIN dbo.users AS u ON u.id = seats.user_id\r\n" +
                "LEFT JOIN dbo.user_departments AS dept ON dept.id = u.department_id\r\n" +
                "LEFT JOIN dbo.user_job_titles AS title ON title.id = u.job_title_id\r\n" +
                "LEFT JOIN dbo.user_country_or_region AS country ON country.id = u.country_or_region_id\r\n" +
                "LEFT JOIN dbo.user_office_locations AS office ON office.id = u.office_location_id\r\n" +
                "LEFT JOIN dbo.user_company_name AS company ON company.id = u.company_name_id\r\n" +
                "LEFT JOIN dbo.users AS manager ON manager.id = u.manager_id\r\n" +
                "LEFT JOIN CopilotUsage AS chats ON chats.user_id = u.id\r\n" +
                (includeCopilotReport ? "LEFT JOIN ReportSnapshot AS report ON report.user_id = u.id\r\n" : string.Empty) +
                // Ordered by id so the cap truncates deterministically: the same users are dropped on
                // every run, which makes a capped report reproducible instead of randomly different.
                "ORDER BY u.id\r\n" +
                "OPTION (RECOMPILE);";

            return sql;
        }

        /// <summary>
        /// How many distinct Copilot surfaces Microsoft's report shows activity in during the window.
        /// Only used when the audit import is unavailable, where it is the only breadth signal.
        ///
        /// The three chat columns are collapsed into one surface on purpose: report version 1 reported
        /// a single "chat" date and version 2 split it into work and web, so counting all three would
        /// make a v2 tenant look broader than a v1 tenant purely because Microsoft changed the CSV.
        /// </summary>
        private static string ReportAppsUsedExpression()
        {
            var singleApps = new[]
            {
                "r.teams_last_activity_date",
                "r.word_last_activity_date",
                "r.excel_last_activity_date",
                "r.powerpoint_last_activity_date",
                "r.outlook_last_activity_date",
                "r.onenote_last_activity_date",
                "r.loop_last_activity_date",
                "r.edge_last_activity_date",
                "r.m365_copilot_last_activity_date",
            };

            var parts = singleApps
                .Select(col => $"CASE WHEN {col} >= @from THEN 1 ELSE 0 END")
                .ToList();

            parts.Add(
                "CASE WHEN r.chat_last_activity_date >= @from "
                + "OR r.chat_work_last_activity_date >= @from "
                + "OR r.chat_web_last_activity_date >= @from THEN 1 ELSE 0 END");

            return "(" + string.Join("\r\n              + ", parts) + ")";
        }

        /// <summary>
        /// The latest of the report's per-app activity dates. Written as a MAX over a VALUES list
        /// rather than GREATEST() because GREATEST needs SQL Server 2022, and customers run this on
        /// everything from SQL Server 2016 upwards.
        /// </summary>
        private static string ReportLastActivityExpression()
        {
            var columns = new[]
            {
                "r.last_activity_date",
                "r.chat_last_activity_date",
                "r.chat_work_last_activity_date",
                "r.chat_web_last_activity_date",
                "r.teams_last_activity_date",
                "r.word_last_activity_date",
                "r.excel_last_activity_date",
                "r.powerpoint_last_activity_date",
                "r.outlook_last_activity_date",
                "r.onenote_last_activity_date",
                "r.loop_last_activity_date",
                "r.edge_last_activity_date",
                "r.m365_copilot_last_activity_date",
                "r.agent_last_activity_date",
            };

            var values = string.Join(", ", columns.Select(c => $"({c})"));
            return $"(SELECT MAX(activity.dt) FROM (VALUES {values}) AS activity(dt))";
        }

        #endregion

        #region Licence opportunities (unlicensed users)

        /// <summary>
        /// Ranks unlicensed users as candidates for a Copilot seat and returns the strongest
        /// <c>@maxRows</c>.
        ///
        /// Driven from the activity tables and anti-joined against the licensed set, so the cost scales
        /// with the number of <i>active</i> users rather than with the size of the directory - the
        /// difference between a report that returns on a 200,000-user tenant and one that does not.
        ///
        /// The ranking expression is generated from the same <see cref="CopilotAdoptionOptions"/> the
        /// C# scorer uses (see <see cref="CopilotAdoptionScoring.BuildOpportunityScoreSql"/>), and every
        /// returned row is re-scored in C# before it is shown. This decides which users come back, not
        /// what their published score is.
        /// </summary>
        public static string LicenceOpportunitiesSql(
            IEnumerable<int> seatLicenceTypeIds,
            CopilotAdoptionOptions options,
            bool includeCopilotAudit,
            bool includeM365Usage)
        {
            var o = options ?? CopilotAdoptionOptions.Default;

            if (!includeCopilotAudit && !includeM365Usage)
            {
                // With neither source there is nothing to rank against, and an empty candidate CTE
                // would be a syntax error. Callers check availability first; this is the backstop.
                throw new ArgumentException(
                    "At least one of the Copilot audit import or the Microsoft 365 usage reports must be available to rank licence opportunities.",
                    nameof(includeCopilotAudit));
            }

            // The ranking expression must only reference CTEs that are actually in the query: when a
            // data source is unavailable its CTE (and its join) is omitted entirely, so its component
            // has to collapse to a literal zero rather than to an unbound column reference.
            var score = CopilotAdoptionScoring.BuildOpportunityScoreSql(
                o,
                copilotColumn: includeCopilotAudit ? "ISNULL(copilot.Interactions, 0)" : "0",
                teamsColumn: includeM365Usage ? "ISNULL(teams.Messages, 0)" : "0",
                meetingsColumn: includeM365Usage ? "ISNULL(teams.Meetings, 0)" : "0",
                emailSentColumn: includeM365Usage ? "ISNULL(mail.EmailsSent, 0)" : "0",
                emailReadColumn: includeM365Usage ? "ISNULL(mail.EmailsRead, 0)" : "0",
                filesColumn: includeM365Usage ? "ISNULL(files.ViewedOrEdited, 0)" : "0");

            var ctes = new List<string>
            {
                "SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                "    FROM dbo.user_license_type_lookups AS ul\r\n" +
                $"    WHERE ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                ")"
            };

            if (includeCopilotAudit)
            {
                ctes.Add(
                    "-- Copilot use by people who do not hold a seat: unlicensed Copilot Chat. The\r\n" +
                    "-- strongest possible signal, because it is evidence rather than inference.\r\n" +
                    "CopilotUsage AS (\r\n" +
                    "    SELECT au.user_id AS user_id,\r\n" +
                    "           COUNT_BIG(*) AS Interactions,\r\n" +
                    "           COUNT(DISTINCT CAST(au.time_stamp AS date)) AS ActiveDays,\r\n" +
                    "           MAX(au.time_stamp) AS LastInteractionUtc\r\n" +
                    "    FROM dbo.copilot_chats AS c\r\n" +
                    "    " + AuditJoin + "\r\n" +
                    "    WHERE au.time_stamp >= @from AND au.user_id IS NOT NULL\r\n" +
                    "    GROUP BY au.user_id\r\n" +
                    ")");
            }

            if (includeM365Usage)
            {
                ctes.Add(
                    "-- One settled snapshot per workload: an equality seek on IX_date, not a range scan.\r\n" +
                    "TeamsUsage AS (\r\n" +
                    "    SELECT t.user_id AS user_id,\r\n" +
                    "           t.private_chat_count + t.team_chat_count + t.post_messages + t.reply_messages AS Messages,\r\n" +
                    "           t.meetings_attended_count + t.meetings_organized_count AS Meetings,\r\n" +
                    "           t.last_activity_date AS LastActivity\r\n" +
                    "    FROM dbo.teams_user_activity_log AS t\r\n" +
                    "    WHERE t.[date] = @m365ReportDate\r\n" +
                    ")");

                ctes.Add(
                    "MailUsage AS (\r\n" +
                    "    SELECT o.user_id AS user_id,\r\n" +
                    // Named EmailsSent/EmailsRead, not Sent/Read: READ is a reserved word in T-SQL and
                    // an unbracketed alias produces a syntax error the moment it is referenced.
                    "           o.email_send_count AS EmailsSent,\r\n" +
                    "           o.email_read_count AS EmailsRead,\r\n" +
                    "           o.last_activity_date AS LastActivity\r\n" +
                    "    FROM dbo.outlook_user_activity_log AS o\r\n" +
                    "    WHERE o.[date] = @m365ReportDate\r\n" +
                    ")");

                ctes.Add(
                    "-- SharePoint and OneDrive are one signal (\"works with documents\"), so they are\r\n" +
                    "-- summed rather than shown as two weak ones.\r\n" +
                    "FileUsage AS (\r\n" +
                    "    SELECT f.user_id AS user_id,\r\n" +
                    "           SUM(f.viewed_or_edited) AS ViewedOrEdited,\r\n" +
                    "           MAX(f.last_activity_date) AS LastActivity\r\n" +
                    "    FROM (\r\n" +
                    "        SELECT sp.user_id, sp.viewed_or_edited, sp.last_activity_date\r\n" +
                    "        FROM dbo.sharepoint_user_activity_log AS sp WHERE sp.[date] = @m365ReportDate\r\n" +
                    "        UNION ALL\r\n" +
                    "        SELECT od.user_id, od.viewed_or_edited, od.last_activity_date\r\n" +
                    "        FROM dbo.onedrive_user_activity_log AS od WHERE od.[date] = @m365ReportDate\r\n" +
                    "    ) AS f\r\n" +
                    "    GROUP BY f.user_id\r\n" +
                    ")");
            }

            var candidateSources = new List<string>();
            if (includeCopilotAudit) candidateSources.Add("    SELECT user_id FROM CopilotUsage");
            if (includeM365Usage)
            {
                candidateSources.Add("    SELECT user_id FROM TeamsUsage");
                candidateSources.Add("    SELECT user_id FROM MailUsage");
                candidateSources.Add("    SELECT user_id FROM FileUsage");
            }

            ctes.Add(
                "-- UNION (not UNION ALL) so a user active in several workloads is one candidate.\r\n" +
                "Candidates AS (\r\n" +
                string.Join("\r\n    UNION\r\n", candidateSources) + "\r\n" +
                ")");

            var copilotSelect = includeCopilotAudit
                ? "       CAST(ISNULL(copilot.Interactions, 0) AS bigint) AS UnlicensedCopilotInteractions,\r\n" +
                  "       ISNULL(copilot.ActiveDays, 0) AS UnlicensedCopilotActiveDays,\r\n" +
                  "       copilot.LastInteractionUtc AS LastCopilotInteractionUtc,\r\n"
                : "       CAST(0 AS bigint) AS UnlicensedCopilotInteractions,\r\n" +
                  "       0 AS UnlicensedCopilotActiveDays,\r\n" +
                  "       CAST(NULL AS datetime) AS LastCopilotInteractionUtc,\r\n";

            var m365Select = includeM365Usage
                ? "       CAST(ISNULL(teams.Messages, 0) AS bigint) AS TeamsMessages,\r\n" +
                  "       CAST(ISNULL(teams.Meetings, 0) AS bigint) AS TeamsMeetings,\r\n" +
                  "       CAST(ISNULL(mail.EmailsSent, 0) AS bigint) AS EmailsSent,\r\n" +
                  "       CAST(ISNULL(mail.EmailsRead, 0) AS bigint) AS EmailsRead,\r\n" +
                  "       CAST(ISNULL(files.ViewedOrEdited, 0) AS bigint) AS FilesViewedOrEdited,\r\n" +
                  "       (SELECT MAX(activity.dt) FROM (VALUES (teams.LastActivity), (mail.LastActivity), (files.LastActivity)) AS activity(dt)) AS LastM365ActivityUtc,\r\n"
                : "       CAST(0 AS bigint) AS TeamsMessages,\r\n" +
                  "       CAST(0 AS bigint) AS TeamsMeetings,\r\n" +
                  "       CAST(0 AS bigint) AS EmailsSent,\r\n" +
                  "       CAST(0 AS bigint) AS EmailsRead,\r\n" +
                  "       CAST(0 AS bigint) AS FilesViewedOrEdited,\r\n" +
                  "       CAST(NULL AS datetime) AS LastM365ActivityUtc,\r\n";

            return
                "WITH " + string.Join(",\r\n", ctes) + "\r\n" +
                "SELECT TOP (@maxRows)\r\n" +
                "       u.id AS UserId,\r\n" +
                "       u.user_name AS UserPrincipalName,\r\n" +
                "       u.mail AS Mail,\r\n" +
                "       dept.name AS Department,\r\n" +
                "       title.name AS JobTitle,\r\n" +
                "       country.name AS Country,\r\n" +
                "       office.name AS OfficeLocation,\r\n" +
                "       company.name AS CompanyName,\r\n" +
                "       manager.user_name AS ManagerUserPrincipalName,\r\n" +
                copilotSelect +
                m365Select +
                $"       CAST({score} AS float) AS RankScore\r\n" +
                "FROM Candidates AS cand\r\n" +
                "JOIN dbo.users AS u ON u.id = cand.user_id\r\n" +
                "LEFT JOIN dbo.user_departments AS dept ON dept.id = u.department_id\r\n" +
                "LEFT JOIN dbo.user_job_titles AS title ON title.id = u.job_title_id\r\n" +
                "LEFT JOIN dbo.user_country_or_region AS country ON country.id = u.country_or_region_id\r\n" +
                "LEFT JOIN dbo.user_office_locations AS office ON office.id = u.office_location_id\r\n" +
                "LEFT JOIN dbo.user_company_name AS company ON company.id = u.company_name_id\r\n" +
                "LEFT JOIN dbo.users AS manager ON manager.id = u.manager_id\r\n" +
                (includeCopilotAudit ? "LEFT JOIN CopilotUsage AS copilot ON copilot.user_id = u.id\r\n" : string.Empty) +
                (includeM365Usage
                    ? "LEFT JOIN TeamsUsage AS teams ON teams.user_id = u.id\r\n" +
                      "LEFT JOIN MailUsage AS mail ON mail.user_id = u.id\r\n" +
                      "LEFT JOIN FileUsage AS files ON files.user_id = u.id\r\n"
                    : string.Empty) +
                "WHERE NOT EXISTS (SELECT 1 FROM SeatUsers AS seats WHERE seats.user_id = u.id)\r\n" +
                // A disabled account cannot use a licence, so proposing one would discredit the list.
                "  AND (u.account_enabled IS NULL OR u.account_enabled = 1)\r\n" +
                $"ORDER BY RankScore DESC, u.id\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// How many users used Copilot inside the window without holding a seat. Counted separately
        /// from the candidate list because it is a headline figure - proven, unmet demand for Copilot -
        /// and must not be limited by the candidate list's row cap.
        /// </summary>
        public static string UnlicensedActiveUsersSql(IEnumerable<int> seatLicenceTypeIds)
        {
            return
                "SELECT COUNT(*) AS Value\r\n" +
                "FROM (\r\n" +
                "    SELECT DISTINCT au.user_id\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    " + AuditJoin + "\r\n" +
                "    WHERE au.time_stamp >= @from\r\n" +
                "      AND au.user_id IS NOT NULL\r\n" +
                "      AND NOT EXISTS (\r\n" +
                "          SELECT 1 FROM dbo.user_license_type_lookups AS ul\r\n" +
                "          WHERE ul.user_id = au.user_id\r\n" +
                $"            AND ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                "      )\r\n" +
                ") AS unlicensed\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Every Copilot agent that was actually used in the window, with the figures needed to decide
        /// whether to keep, review or retire it.
        ///
        /// Counted across the whole tenant rather than licensed users only: agents are used by
        /// unlicensed Copilot Chat users too, and an agent's value to the organisation does not depend
        /// on the licence status of the people using it. The licensed share is returned separately so
        /// the two populations can still be told apart.
        ///
        /// "Versatility" is the number of distinct Copilot surfaces the agent was invoked from. An
        /// agent that only ever runs in one host is doing a narrower job than its interaction count
        /// suggests, which is exactly the sort of thing an inventory review needs to see.
        /// </summary>
        public static string AgentUsageSql(IEnumerable<int> seatLicenceTypeIds)
        {
            var seats = IdList(seatLicenceTypeIds);

            return
                "WITH SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                $"    FROM dbo.user_license_type_lookups AS ul WHERE ul.license_type_id IN ({seats})\r\n" +
                ")\r\n" +
                "SELECT TOP (@maxRows)\r\n" +
                "       ag.id AS AgentId,\r\n" +
                "       ISNULL(ag.name, '(unnamed agent)') AS Name,\r\n" +
                "       ag.agent_id AS AgentKey,\r\n" +
                "       CAST(ISNULL(ag.is_custom_agent, 0) AS bit) AS IsCustomAgent,\r\n" +
                "       COUNT_BIG(*) AS Interactions,\r\n" +
                // Window-scoped as well as history-scoped. The inventory needs the long view to spot a
                // dormant agent, but the headline "interactions per agent user" divides by a user count
                // that is scoped to the reporting period - mixing the two inflates that KPI by the ratio
                // of the windows (roughly 4x at the defaults).
                "       COUNT_BIG(CASE WHEN au.time_stamp >= @from THEN 1 END) AS WindowInteractions,\r\n" +
                "       COUNT(DISTINCT au.user_id) AS Users,\r\n" +
                "       COUNT(DISTINCT seats.user_id) AS LicensedUsers,\r\n" +
                "       COUNT(DISTINCT CAST(au.time_stamp AS date)) AS ActiveDays,\r\n" +
                "       COUNT(DISTINCT ISNULL(c.app_host, '(unknown)')) AS AppsUsed,\r\n" +
                "       MIN(au.time_stamp) AS FirstUsedUtc,\r\n" +
                "       MAX(au.time_stamp) AS LastUsedUtc\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "" + AuditJoin + "\r\n" +
                "JOIN dbo.copilot_agents AS ag ON ag.id = c.agent_id\r\n" +
                "LEFT JOIN SeatUsers AS seats ON seats.user_id = au.user_id\r\n" +
                "WHERE au.time_stamp >= @historyFrom\r\n" +
                // Redundant against the inner join to copilot_agents, but it lets the optimiser
                // eliminate the (usually large) majority of Copilot interactions that carry no agent
                // before it does any joining, rather than discovering it during the join.
                "  AND c.agent_id IS NOT NULL\r\n" +
                "GROUP BY ag.id, ag.name, ag.agent_id, ag.is_custom_agent\r\n" +
                "ORDER BY Interactions DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Agent interactions per department in the window, so agent adoption can be read the same way
        /// as seat adoption. Departments come from the imported user metadata.
        /// </summary>
        public static string AgentUsageByDepartmentSql()
        {
            return
                "SELECT TOP (@top) ISNULL(NULLIF(LTRIM(RTRIM(dept.name)), ''), '(no department)') AS Label,\r\n" +
                "       CAST(COUNT_BIG(*) AS float) AS Value\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "" + AuditJoin + "\r\n" +
                "JOIN dbo.users AS u ON u.id = au.user_id\r\n" +
                "LEFT JOIN dbo.user_departments AS dept ON dept.id = u.department_id\r\n" +
                "WHERE au.time_stamp >= @from\r\n" +
                "  AND c.agent_id IS NOT NULL\r\n" +
                "GROUP BY ISNULL(NULLIF(LTRIM(RTRIM(dept.name)), ''), '(no department)')\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// One row per unlicensed user who used Copilot in the window, with the same shape of figures
        /// the licensed population is scored from.
        ///
        /// Separate from <see cref="LicenceOpportunitiesSql"/> on purpose: that query ranks candidates
        /// and is capped and ordered by score, so its rows are a biased sample and must never be used
        /// to describe the population. This one is "everyone who actually used it", which is what a
        /// habit distribution needs.
        /// </summary>
        public static string UnlicensedUsageRowsSql(IEnumerable<int> seatLicenceTypeIds)
        {
            return
                "SELECT TOP (@maxRows)\r\n" +
                "       au.user_id AS UserId,\r\n" +
                "       ISNULL(NULLIF(LTRIM(RTRIM(dept.name)), ''), '') AS Department,\r\n" +
                "       COUNT_BIG(*) AS Interactions,\r\n" +
                "       COUNT(DISTINCT CAST(au.time_stamp AS date)) AS ActiveDays,\r\n" +
                "       COUNT(DISTINCT ISNULL(c.app_host, '(unknown)')) AS AppsUsed,\r\n" +
                "       COUNT(DISTINCT c.agent_id) AS AgentsUsed,\r\n" +
                "       MAX(au.time_stamp) AS LastInteractionUtc\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "" + AuditJoin + "\r\n" +
                "LEFT JOIN dbo.users AS u ON u.id = au.user_id\r\n" +
                "LEFT JOIN dbo.user_departments AS dept ON dept.id = u.department_id\r\n" +
                "WHERE au.time_stamp >= @from\r\n" +
                "  AND au.user_id IS NOT NULL\r\n" +
                "  AND NOT EXISTS (\r\n" +
                "      SELECT 1 FROM dbo.user_license_type_lookups AS ul\r\n" +
                "      WHERE ul.user_id = au.user_id\r\n" +
                $"        AND ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                "  )\r\n" +
                "GROUP BY au.user_id, ISNULL(NULLIF(LTRIM(RTRIM(dept.name)), ''), '')\r\n" +
                "ORDER BY Interactions DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// The kinds of tenant content Copilot actually grounded its answers in (documents, meetings,
        /// chats...). The clearest evidence that Copilot is doing work on the organisation's own data
        /// rather than answering generic questions any free chatbot could.
        /// </summary>
        public static string TopResourceTypesSql()
        {
            return
                "SELECT TOP (@top) ISNULL(rt.name, '(unknown)') AS Label,\r\n" +
                "       CAST(COUNT_BIG(*) AS float) AS Value\r\n" +
                "FROM dbo.copilot_event_accessed_resources AS ar\r\n" +
                "JOIN dbo.copilot_chats AS c ON c.event_id = ar.copilot_chat_id\r\n" +
                "" + AuditJoin + "\r\n" +
                "LEFT JOIN dbo.copilot_event_accessed_resource_types AS rt ON rt.id = ar.resource_type_id\r\n" +
                "WHERE au.time_stamp >= @from\r\n" +
                "GROUP BY ISNULL(rt.name, '(unknown)')\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        #endregion

        #region Charts

        /// <summary>
        /// Where licensed users actually use Copilot. Answers "we bought it for Word and they only use
        /// it in Teams", which usually changes the enablement plan more than the headline rate does.
        /// </summary>
        public static string UsageByAppSql(IEnumerable<int> seatLicenceTypeIds)
        {
            return
                "WITH SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                "    FROM dbo.user_license_type_lookups AS ul\r\n" +
                $"    WHERE ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                ")\r\n" +
                "SELECT TOP (@top) ISNULL(c.app_host, '(unknown)') AS Label,\r\n" +
                "       CAST(COUNT_BIG(*) AS float) AS Value\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "" + AuditJoin + "\r\n" +
                "JOIN SeatUsers AS seats ON seats.user_id = au.user_id\r\n" +
                "WHERE au.time_stamp >= @from\r\n" +
                "GROUP BY ISNULL(c.app_host, '(unknown)')\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// The same breakdown for people with no Copilot seat. Kept as its own query rather than a flag
        /// on <see cref="UsageByAppSql"/> so the two can be shown side by side, which is where the
        /// interesting difference usually is: unlicensed use concentrates in Teams and Copilot Chat,
        /// while seats are normally sold on the promise of Word and Outlook.
        /// </summary>
        public static string UnlicensedUsageByAppSql(IEnumerable<int> seatLicenceTypeIds)
        {
            return
                "SELECT TOP (@top) ISNULL(c.app_host, '(unknown)') AS Label,\r\n" +
                "       CAST(COUNT_BIG(*) AS float) AS Value\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "" + AuditJoin + "\r\n" +
                "WHERE au.time_stamp >= @from\r\n" +
                "  AND au.user_id IS NOT NULL\r\n" +
                "  AND NOT EXISTS (\r\n" +
                "      SELECT 1 FROM dbo.user_license_type_lookups AS ul\r\n" +
                "      WHERE ul.user_id = au.user_id\r\n" +
                $"        AND ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                "  )\r\n" +
                "GROUP BY ISNULL(c.app_host, '(unknown)')\r\n" +
                "ORDER BY Value DESC\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Weekly active licensed users, and how many of them used Cowork. A single point-in-time
        /// adoption rate cannot show whether an enablement programme is working; this can.
        ///
        /// Weeks are bucketed to their Monday with day arithmetic rather than DATEDIFF(WEEK, ...),
        /// which splits weeks on Sunday and would push Sunday's rows into the following week. The same
        /// bucketing as the Reports area, so the two agree.
        /// </summary>
        /// <remarks>
        /// Both series are computed in ONE pass over the range (issue #295). They used to be two
        /// <c>SELECT</c>s joined by <c>UNION ALL</c>, each repeating the same <c>copilot_chats</c> to
        /// <c>audit_events</c> join over the same six months - so a range holding R Copilot rows was
        /// processed twice, and the join performed twice, for two numbers that come from the same rows.
        /// <para>
        /// The Cowork figure is now a conditional <c>COUNT(DISTINCT ...)</c>: <c>COUNT</c> ignores NULLs,
        /// so the <c>CASE</c> yielding NULL for non-Cowork rows counts exactly the users the second query
        /// used to select.
        /// </para>
        /// <para>
        /// Unpivoted with <c>CROSS APPLY (VALUES ...)</c> rather than by selecting from the CTE twice.
        /// A CTE referenced twice can be expanded and evaluated twice, which would reintroduce the very
        /// double scan this removes; referencing it once cannot.
        /// </para>
        /// <para>
        /// Output is deliberately identical to before, including the absence of a Cowork point in weeks
        /// with no Cowork usage. Emitting explicit zeros would arguably suit a trend chart better - a
        /// missing week is ambiguous between "no data" and "no usage" - but that is a reporting decision,
        /// not something to slip in with a performance fix.
        /// </para>
        /// </remarks>
        public static string WeeklyAdoptionTrendSql(IEnumerable<int> seatLicenceTypeIds, IEnumerable<int> coworkAgentIds)
        {
            var week = WeekBucket("au.time_stamp");
            var cowork = CoworkPredicate(coworkAgentIds);
            var seats = IdList(seatLicenceTypeIds);

            return
                "WITH SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                "    FROM dbo.user_license_type_lookups AS ul\r\n" +
                $"    WHERE ul.license_type_id IN ({seats})\r\n" +
                "),\r\n" +
                "Weekly AS (\r\n" +
                $"    SELECT {week} AS WeekStart,\r\n" +
                "           COUNT(DISTINCT CASE WHEN seats.user_id IS NOT NULL THEN au.user_id END) AS ActiveUsers,\r\n" +
                "           COUNT(DISTINCT CASE WHEN seats.user_id IS NOT NULL\r\n" +
                $"                                AND ({cowork}) THEN au.user_id END) AS CoworkUsers,\r\n" +
                "           COUNT(DISTINCT CASE WHEN seats.user_id IS NULL THEN au.user_id END) AS UnlicensedUsers,\r\n" +
                "           COUNT(DISTINCT CASE WHEN c.agent_id IS NOT NULL THEN au.user_id END) AS AgentUsers,\r\n" +
                "           COUNT_BIG(CASE WHEN seats.user_id IS NOT NULL THEN 1 END) AS LicensedInteractions,\r\n" +
                "           COUNT_BIG(CASE WHEN seats.user_id IS NULL THEN 1 END) AS UnlicensedInteractions\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    " + AuditJoin + "\r\n" +
                "    LEFT JOIN SeatUsers AS seats ON seats.user_id = au.user_id\r\n" +
                "    WHERE au.time_stamp >= @trendFrom\r\n" +
                "      AND au.user_id IS NOT NULL\r\n" +
                $"    GROUP BY {week}\r\n" +
                ")\r\n" +
                "SELECT v.SeriesName AS SeriesName,\r\n" +
                "       w.WeekStart AS WeekStart,\r\n" +
                "       CAST(v.Value AS float) AS Value\r\n" +
                "FROM Weekly AS w\r\n" +
                "CROSS APPLY (VALUES\r\n" +
                "    ('Active licensed users', w.ActiveUsers),\r\n" +
                "    ('Cowork users', w.CoworkUsers),\r\n" +
                "    ('Active unlicensed users', w.UnlicensedUsers),\r\n" +
                "    ('Agent users', w.AgentUsers),\r\n" +
                "    ('Licensed interactions', w.LicensedInteractions),\r\n" +
                "    ('Unlicensed interactions', w.UnlicensedInteractions)\r\n" +
                ") AS v(SeriesName, Value)\r\n" +
                "WHERE NOT (v.SeriesName IN ('Cowork users', 'Agent users', 'Active unlicensed users')\r\n" +
                "           AND v.Value = 0)\r\n" +
                "ORDER BY SeriesName, WeekStart\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// The series from <see cref="WeeklyAdoptionTrendSql"/> that count interactions rather than
        /// people. Split into their own chart because a volume line and a headcount line share no
        /// sensible axis - plotted together, the headcount flattens to nothing.
        /// </summary>
        public static readonly string[] VolumeTrendSeries =
        {
            "Licensed interactions",
            "Unlicensed interactions",
        };

        #endregion

        #region Helpers

        /// <summary>
        /// The predicate that identifies a Cowork interaction. <c>app_host</c> is compared directly
        /// rather than through LOWER(): SQL Server's default collation here is case-insensitive, and
        /// wrapping the column in a function would make the predicate non-SARGable for no benefit.
        /// </summary>
        internal static string CoworkPredicate(IEnumerable<int> coworkAgentIds)
        {
            var ids = (coworkAgentIds ?? Enumerable.Empty<int>()).Distinct().ToList();
            var host = $"c.app_host = '{CoworkAppHost}'";

            return ids.Count == 0
                ? host
                : $"{host} OR c.agent_id IN ({IdList(ids)})";
        }

        /// <summary>
        /// Buckets a datetime to the Monday on or before it. 1900-01-01 was a Monday, so "days since
        /// then modulo 7" is zero exactly on Mondays - independent of DATEFIRST and language settings.
        /// </summary>
        internal static string WeekBucket(string column)
        {
            return $"DATEADD(DAY, -(DATEDIFF(DAY, 0, {column}) % 7), CAST({column} AS date))";
        }

        /// <summary>
        /// Renders integer ids as a SQL <c>IN</c> list.
        ///
        /// These ids always come from this application's own query against <c>dbo.license_types</c> /
        /// <c>dbo.copilot_agents</c> and are typed <c>int</c>, so there is no injection surface - but an
        /// empty list would produce <c>IN ()</c>, which is a syntax error, so it yields a predicate that
        /// is simply always false. A tenant that has bought no Copilot licences then gets an empty
        /// report rather than a 500.
        /// </summary>
        internal static string IdList(IEnumerable<int> ids)
        {
            var list = (ids ?? Enumerable.Empty<int>()).Distinct().ToList();

            return list.Count == 0
                ? "-1"
                : string.Join(", ", list.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Replaces the parameters with literals for display only, so the SQL popover shows a query an
        /// admin can paste straight into SQL Server Management Studio. Never used for execution.
        /// </summary>
        public static string ForDisplay(string sql, IDictionary<string, object> parameters)
        {
            if (string.IsNullOrEmpty(sql)) return sql;

            var declarations = new List<string>();
            foreach (var parameter in parameters ?? new Dictionary<string, object>())
            {
                declarations.Add($"DECLARE {parameter.Key} {SqlLiteralType(parameter.Value)} = {SqlLiteral(parameter.Value)};");
            }

            return declarations.Count == 0
                ? sql
                : string.Join("\r\n", declarations) + "\r\n\r\n" + sql;
        }

        private static string SqlLiteralType(object value)
        {
            switch (value)
            {
                case DateTime _: return "datetime";
                case int _: return "int";
                case long _: return "bigint";
                case double _: return "float";
                default: return "nvarchar(200)";
            }
        }

        private static string SqlLiteral(object value)
        {
            switch (value)
            {
                case null:
                    return "NULL";
                case DateTime dt:
                    return $"'{dt:yyyy-MM-dd HH:mm:ss}'";
                case int i:
                    return i.ToString(CultureInfo.InvariantCulture);
                case long l:
                    return l.ToString(CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString(CultureInfo.InvariantCulture);
                default:
                    return "N'" + value.ToString().Replace("'", "''") + "'";
            }
        }

        #endregion
    }
}
