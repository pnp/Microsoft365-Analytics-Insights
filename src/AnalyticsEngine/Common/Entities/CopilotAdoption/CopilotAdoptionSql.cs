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
    ///   <item><b>No Copilot query joins <c>dbo.audit_events</c>.</b> A Copilot interaction used to have no
    ///   date or user of its own - both lived on the parent audit event - so every query here began
    ///   <c>copilot_chats INNER JOIN audit_events AS au ON c.event_id = au.id</c>. <c>copilot_chats</c> now
    ///   carries denormalised <c>user_id</c> / <c>time_stamp</c> columns, written on the same row by the
    ///   same statement that inserts the chat (<c>common_upsert_copilot_agents.sql</c>) and indexed by
    ///   <c>IX_copilot_chats_time_stamp_user_id</c>.
    ///   <br/>Measured on a synthetic bench sized for a large tenant (~10M audit_events at ~1.7 KB/row,
    ///   ~6M copilot_chats, Copilot a large share of them): <c>LicensedUsers</c> at a 28-day window went
    ///   13.0s -&gt; 5.6s
    ///   (2.3x). A covering index on <c>copilot_chats(event_id)</c> - which needs no duplication - only
    ///   reached 10.4s, because an index key must be a column of the table it indexes, so no index on
    ///   <c>copilot_chats</c> can be date-ordered unless the date is ON <c>copilot_chats</c>. See migration
    ///   <c>DenormaliseCopilotChatUserAndTime</c> for the full option comparison and issue #360.
    ///   <br/>Semantics are unchanged: the old <c>INNER JOIN</c> dropped any chat whose audit event was
    ///   missing, and <c>NULL</c> fails <c>time_stamp &gt;= @from</c>, so the same rows are excluded.</item>
    ///   <item>The licensed population is derived from <c>user_license_type_lookups</c> filtered by
    ///   licence-type id. That table has a unique index on <c>(license_type_id, user_id)</c>, so this
    ///   is an index-only seek on the leading column - not a scan of <c>users</c>.</item>
    ///   <item>The Copilot audit history is read over a bounded date range, with the window-versus-history
    ///   split done by <c>CASE</c> inside a single aggregate. Running separate "in the window" and "ever"
    ///   passes would double the cost of the largest read in the report.</item>
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
        /// Microsoft 365 Copilot Cowork surfaces itself in the audit log as the app host "cowork" and,
        /// where the agent was dimensioned, as a first-party agent whose id starts
        /// <c>Copilot.M365Copilot.Cowork</c>. Both are checked: the app host is present on every
        /// interaction, while the agent id only exists once the agent has been imported.
        /// </summary>
        public const string CoworkAppHost = "cowork";

        /// <summary>
        /// Finds the <c>copilot_agents</c> rows that represent Cowork.
        ///
        /// <para>Matched on the first-party agent id ONLY, never on the display name. The name is
        /// customer-controlled free text, so <c>name LIKE '%Cowork%'</c> matches a tenant's own Copilot
        /// Studio agent called anything like "Cowork Helper" or "Coworking bot". That is not a cosmetic
        /// mismatch: a false agent match makes the user's interactions read as <i>observed</i> Cowork
        /// use, and observed use is tested first in <see cref="CopilotAdoptionScoring.CoworkTierFor"/>
        /// and wins outright over every inferred tier. A custom agent would therefore promote its users
        /// into the evidence tiers and onto the recommended spending-policy list on the strength of a
        /// substring of a name somebody typed.</para>
        ///
        /// <para>Nothing is lost by dropping the name match: <c>app_host = 'cowork'</c> is present on
        /// <i>every</i> Cowork interaction (see <see cref="CoworkAppHost"/>), and
        /// <see cref="CoworkPredicate"/> tests it with OR, so the agent id is a supplementary signal for
        /// dimensioned agents rather than the only way a Cowork interaction is found.</para>
        /// </summary>
        public const string CoworkAgentIdsSql =
            "SELECT ag.id AS Value\r\n" +
            "FROM dbo.copilot_agents AS ag\r\n" +
            "WHERE ag.agent_id LIKE 'Copilot.M365Copilot.Cowork%';";

        /// <summary>
        /// Every licence type with how many users hold it, so the tool can classify them and show the
        /// admin exactly which SKUs it counted as Copilot seats.
        /// </summary>
        public const string LicenceTypesSql =
            "SELECT lt.id AS Id,\r\n" +
            "       lt.name AS Name,\r\n" +
            "       lt.sku_id AS SkuPartNumber,\r\n" +
            "       COUNT(ul.user_id) AS AssignedUsers,\r\n" +
            "       CASE WHEN lt.subscribed_sku_refreshed_utc IS NULL\r\n" +
            "                 OR lt.prepaid_enabled_units IS NULL\r\n" +
            "                 OR lt.prepaid_warning_units IS NULL\r\n" +
            "                 OR lt.prepaid_suspended_units IS NULL\r\n" +
            "            THEN NULL ELSE lt.prepaid_enabled_units + lt.prepaid_warning_units + lt.prepaid_suspended_units END AS PurchasedUnits,\r\n" +
            "       lt.subscribed_sku_refreshed_utc AS PurchasedUnitsRefreshedUtc\r\n" +
            "FROM dbo.license_types AS lt\r\n" +
            "LEFT JOIN dbo.user_license_type_lookups AS ul ON ul.license_type_id = lt.id\r\n" +
            "GROUP BY lt.id, lt.name, lt.sku_id, lt.prepaid_enabled_units, lt.prepaid_warning_units, lt.prepaid_suspended_units, lt.subscribed_sku_refreshed_utc\r\n" +
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

        public const string LatestCoworkReportDateSql =
            "SELECT MAX(r.[date]) AS Value\r\n" +
            "FROM dbo.cowork_usage_user_activity_log AS r\r\n" +
            "WHERE r.[date] <= @settled;";

        public const string LatestCoworkReportPeriodSql =
            "SELECT TOP (1) r.report_period_days AS Value\r\n" +
            "FROM dbo.cowork_usage_user_activity_log AS r\r\n" +
            "WHERE r.[date] = @coworkReportDate\r\n" +
            "GROUP BY r.report_period_days\r\n" +
            "ORDER BY ABS(r.report_period_days - @windowDays), r.report_period_days;";

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
            "    WHERE c.time_stamp >= @from\r\n" +
            "      AND c.time_stamp < @toExclusive\r\n" +
            ") THEN 1 ELSE 0 END AS Value;";

        /// <summary>
        /// Whether any Copilot interaction is still missing its denormalised <c>time_stamp</c>, and could be
        /// repaired from its audit event.
        ///
        /// <para>
        /// Every query here filters <c>c.time_stamp &gt;= @from</c>, so a row that has not been backfilled yet
        /// is invisible - it silently lowers every figure on the page. That can happen for a short while
        /// after the upgrade: migration <c>DenormaliseCopilotChatUserAndTime</c> backfills existing rows, but
        /// an OLD importer still running during the upgrade window can insert more NULLs behind it. The
        /// importer repairs them on every commit (<c>repair_denormalised_copilot_columns.sql</c>), so this is
        /// transient - but "transient" is not "invisible", and
        /// reporting confident numbers that are quietly too low is the exact defect issue #360 was raised for.
        /// </para>
        /// <para>
        /// Deliberately excludes orphans (a chat whose audit event no longer exists): they can never be
        /// repaired, they were invisible to the previous <c>INNER JOIN</c> reports too, and counting them
        /// would leave the page permanently claiming to be incomplete.
        /// </para>
        /// <para>
        /// Cheap: NULLs sort first in <c>IX_copilot_chats_time_stamp_user_id</c>, so this is a seek to the
        /// head of that index plus one primary-key probe, and it stops at the first row.
        /// </para>
        /// </summary>
        public const string PendingCopilotBackfillSql =
            "SELECT CASE WHEN EXISTS (\r\n" +
            "    SELECT 1 FROM dbo.copilot_chats AS c\r\n" +
            "    WHERE c.time_stamp IS NULL\r\n" +
            "      AND EXISTS (SELECT 1 FROM dbo.audit_events AS ae WHERE ae.id = c.event_id)\r\n" +
            ") THEN 1 ELSE 0 END AS Value;";

        /// <summary>
        /// Completed weeks where the Audit.General feed has any imported event, used to distinguish a
        /// genuine zero-Copilot week from a week whose import coverage cannot be verified.
        /// </summary>
        /// <remarks>
        /// This is intentionally a coverage probe, not an activity count. It reads the same Audit.General
        /// source that supplies Copilot interactions, but does not require a Copilot row to exist: if the
        /// feed produced some General workload event that week and the Copilot trend has no row, the safest
        /// interim interpretation is a real zero. If the feed has no evidence at all, the chart leaves a
        /// gap until persisted closed-period facts can use as-of seat state.
        /// </remarks>
        public static readonly string WeeklyCopilotAuditCoverageSql =
            "WITH WeekSpine AS (\r\n" +
            "    SELECT CAST(" + WeekBucket("CAST(@trendFrom AS date)") + " AS date) AS WeekStart\r\n" +
            "    UNION ALL\r\n" +
            "    SELECT CAST(DATEADD(DAY, 7, WeekStart) AS date)\r\n" +
            "    FROM WeekSpine\r\n" +
            "    WHERE WeekStart < DATEADD(DAY, -7, CAST(" + WeekBucket("CAST(@trendTo AS date)") + " AS date))\r\n" +
            ")\r\n" +
            "SELECT w.WeekStart\r\n" +
            "FROM WeekSpine AS w\r\n" +
            "CROSS APPLY (\r\n" +
            "    SELECT TOP (1) 1 AS Covered\r\n" +
            "    FROM dbo.audit_events AS ae\r\n" +
            "    WHERE ae.time_stamp >= w.WeekStart\r\n" +
            "      AND ae.time_stamp < DATEADD(DAY, 7, w.WeekStart)\r\n" +
            "      AND ae.time_stamp < @trendTo\r\n" +
            "      AND EXISTS (SELECT 1 FROM dbo.event_meta_general AS g WHERE g.event_id = ae.id)\r\n" +
            ") AS c\r\n" +
            "ORDER BY WeekStart\r\n" +
            "OPTION (MAXRECURSION 100, RECOMPILE);";

        #region Copilot app host

        /// <summary>
        /// The width <c>app_host</c> is bounded to whenever it is used as a GROUPING or DISTINCT key.
        /// Generous: a real tenant's longest value is well under half this, and the point is only to
        /// give SQL Server a fixed-width key, not to shorten the label.
        /// </summary>
        private const int AppHostKeyWidth = 100;

        /// <summary>
        /// <c>app_host</c> as a fixed-width grouping key.
        ///
        /// <para>
        /// <b>Why this cast exists.</b> <c>dbo.copilot_chats.app_host</c> is <c>nvarchar(max)</c> - the EF
        /// property carries no <c>MaxLength</c>, so the column is a LOB (see the <c>CopilotEvents</c>
        /// migration). Every value it actually holds is a short product-surface name, but SQL Server does
        /// not know that: it sizes hash tables, sort buffers and - above all - the query's MEMORY GRANT
        /// from the column's DECLARED width, not its contents. Using the raw column as one of the
        /// grouping keys of a multi-million-row aggregate therefore asks for an enormous workspace grant
        /// and burns CPU hashing a LOB per row, which is what pushed the 90- and 180-day windows of the
        /// Copilot Adoption page past the command timeout on a large tenant.
        /// </para>
        /// <para>
        /// Measured on a synthetic 200k-user tenant (26M <c>copilot_chats</c>, 100k unlicensed active
        /// users, 20k Copilot seats, the (user, day, app, agent) grain compressing 2.4x), running the
        /// SQL this class actually emits, medians of three after a discarded cold run:
        /// <list type="table">
        /// <item><description><b>UnlicensedUsageRows</b> 28d: 3,259ms -&gt; 1,903ms, CPU 13.5s -&gt; 8.3s</description></item>
        /// <item><description><b>UnlicensedUsageRows</b> 90d: 8,018ms -&gt; 5,122ms, CPU 42.9s -&gt; 23.7s (1.8x)</description></item>
        /// <item><description><b>UnlicensedUsageRows</b> 180d: 14,890ms -&gt; 9,559ms, CPU 83.7s -&gt; 46.3s (1.8x)</description></item>
        /// <item><description><b>AgentUsage</b> 90d: 2,482ms -&gt; 1,751ms, CPU 13.8s -&gt; 8.1s (1.7x)</description></item>
        /// <item><description><b>AgentUsage</b> 180d: 3,425ms -&gt; 2,152ms, CPU 21.2s -&gt; 10.3s (2.1x)</description></item>
        /// <item><description><b>LicensedUsers</b>: NEUTRAL (0.93x - 1.02x, i.e. noise) at every window</description></item>
        /// </list>
        /// Logical reads are unchanged (within 0.2%) - the same index pages are read either way, so the
        /// saving is aggregation CPU and workspace memory, which is the resource that actually runs out
        /// on a tier-capped database shared with the importer. The win lands where the GROUPING OUTPUT
        /// is large (the two grain tables, millions of rows); <c>LicensedUsers</c> only ever distincts
        /// down to users x surfaces, so its hash table is small whatever the key width - the cast is
        /// applied there for consistency, not because it was shown to help.
        /// </para>
        /// <para>
        /// The memory grant is the part that turns this from "slower" into "fails". On a grain with
        /// higher cardinality the optimiser was observed asking for 65GB (90-day) and 130GB (180-day)
        /// of workspace for the LOB shape against 3.3GB and 6.6GB for this one - a ~20x request that no
        /// database can grant, so it is capped and the query spills, and under concurrent load it
        /// queues on <c>RESOURCE_SEMAPHORE</c> behind everything else. That is the reported symptom:
        /// the 28-day window completes while 90- and 180-day windows hit the command timeout and the
        /// unlicensed section silently degrades to a warning.
        /// </para>
        /// <para>
        /// Verified row-for-row identical (same rows, same order, same values) against the previous SQL
        /// at 28, 90 and 180 days for all three queries. Giving the grain temp table a clustered index
        /// on <c>user_id</c> was also measured and REJECTED: neutral at 90 days and 17% WORSE at 180.
        /// </para>
        /// <para>
        /// The cast truncates, so it must never be applied where the FULL value is needed - only where
        /// <c>app_host</c> is a grouping or DISTINCT key, or the label of a small categorical roll-up.
        /// It is deliberately NOT applied to <see cref="CoworkPredicate"/>, which compares the base
        /// column and would otherwise stop being a plain equality against the stored value.
        /// </para>
        /// </summary>
        /// <param name="column">The <c>app_host</c> column reference, e.g. <c>c.app_host</c>.</param>
        /// <param name="nullLabel">
        /// When supplied, NULL is bucketed to this label first, matching the existing
        /// <c>ISNULL(app_host, '(unknown)')</c> behaviour. When null, NULL stays NULL.
        /// </param>
        private static string AppHostKey(string column, string nullLabel = null)
        {
            var value = nullLabel == null ? column : $"ISNULL({column}, '{nullLabel}')";
            return $"CAST({value} AS nvarchar({AppHostKeyWidth}))";
        }

        #endregion

        #region Guest (external) accounts

        /// <summary>
        /// How an external guest is recognised. Entra writes guests into the directory with a UPN of the
        /// form <c>someone_contoso.com#EXT#@tenant.onmicrosoft.com</c>, and the user import stores them
        /// alongside members with nothing else to tell them apart - there is no <c>userType</c> column
        /// anywhere in this schema, so the UPN marker is the only signal available.
        /// </summary>
        public const string GuestUserNamePattern = "'%#EXT#@%'";

        /// <summary>
        /// Excludes guests from a query that already has <c>dbo.users</c> joined as <paramref name="userAlias"/>.
        /// </summary>
        private static string ExcludeGuests(string userAlias)
        {
            return $"  AND {userAlias}.user_name NOT LIKE {GuestUserNamePattern}\r\n";
        }

        /// <summary>
        /// Excludes guests from a query that only has a user id to hand.
        /// <para>
        /// Written as <c>NOT EXISTS</c> rather than a join so that a user id with no row in
        /// <c>dbo.users</c> is KEPT, matching how <c>account_enabled</c> is treated: an account the
        /// directory import has not caught up with must not silently vanish from the figures.
        /// </para>
        /// <para>
        /// This must stay consistent across every query that feeds a SEAT DECISION. Excluding guests
        /// from the ranked candidate list but not from the headline "unmet demand" count would let the
        /// page report demand that cannot appear in the list it tells you to act on.
        /// </para>
        /// </summary>
        private static string ExcludeGuestsByUserId(string userIdExpression, string indent)
        {
            return
                $"{indent}AND NOT EXISTS (\r\n" +
                $"{indent}    SELECT 1 FROM dbo.users AS guest_check\r\n" +
                $"{indent}    WHERE guest_check.id = {userIdExpression}\r\n" +
                $"{indent}      AND guest_check.user_name LIKE {GuestUserNamePattern}\r\n" +
                $"{indent})\r\n";
        }

        #endregion

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
                "       ul.license_type_id AS LicenceTypeId,\r\n" +
                "       lt.sku_id AS SkuPartNumber,\r\n" +
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
            bool includeCopilotReport,
            bool includeCoworkReport = false)
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
                "    SELECT c.user_id AS user_id,\r\n" +
                "           c.time_stamp AS time_stamp,\r\n" +
                // Bounded, because CopilotApps below uses this as a DISTINCT key - see AppHostKey.
                $"           {AppHostKey("c.app_host")} AS app_host,\r\n" +
                "           c.agent_id AS agent_id\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    JOIN SeatUsers AS seats ON seats.user_id = c.user_id\r\n" +
                "    WHERE c.time_stamp >= @historyFrom\r\n" +
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

            if (includeCoworkReport)
            {
                sql +=
                    ",\r\n" +
                    "CoworkReportSnapshot AS (\r\n" +
                    "    SELECT r.user_id AS user_id,\r\n" +
                    "           r.total_tasks AS CoworkReportTotalTasks,\r\n" +
                    "           r.scheduled_tasks AS CoworkReportScheduledTasks,\r\n" +
                    "           r.user_initiated_tasks AS CoworkReportUserInitiatedTasks,\r\n" +
                    "           r.active_days AS CoworkReportActiveDays,\r\n" +
                    "           r.last_activity_date AS CoworkReportLastActivityDate,\r\n" +
                    "           r.retained_user AS CoworkReportRetainedUser\r\n" +
                    "    FROM dbo.cowork_usage_user_activity_log AS r\r\n" +
                    "    WHERE r.[date] = @coworkReportDate\r\n" +
                    "      AND r.report_period_days = @coworkReportPeriodDays\r\n" +
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
                "       u.created_utc AS AccountCreatedUtc,\r\n" +
                // A blank reason must still count as an exclusion. The tier is decided by whether this
                // column has text, so an exclusion row saved with an empty reason would otherwise be
                // silently ignored and the seat would be offered for reclaim - the exact opposite of
                // what the administrator recorded.
                "       ISNULL(NULLIF(LTRIM(RTRIM(exclusion.reason)), N''), CASE WHEN exclusion.excluded_utc IS NULL THEN NULL ELSE N'(no reason recorded)' END) AS ReclaimExclusionReason,\r\n" +
                "       exclusion.note AS ReclaimExclusionNote,\r\n" +
                "       exclusion.excluded_by AS ReclaimExcludedBy,\r\n" +
                "       exclusion.excluded_utc AS ReclaimExcludedUtc,\r\n" +
                "       exclusion.review_after_utc AS ReclaimExclusionReviewAfterUtc,\r\n" +
                // Expired only when there is no ACTIVE exclusion. The expired lookup matches any row
                // whose review date has passed, so a user who was excluded, allowed to lapse, and then
                // excluded again would otherwise be reported as both currently excluded and expired -
                // inflating the "needs re-review" count with cases an admin has already dealt with.
                // Keyed on excluded_utc (NOT NULL in the table) rather than reason, so presence is
                // tested rather than content.
                "       CAST(CASE WHEN expired.user_id IS NOT NULL AND exclusion.excluded_utc IS NULL THEN 1 ELSE 0 END AS bit) AS ReclaimExclusionExpired,\r\n" +
                "       CAST(ISNULL(chats.Interactions, 0) AS bigint) AS Interactions,\r\n" +
                "       CAST(ISNULL(chats.PriorInteractions, 0) AS bigint) AS PriorInteractions,\r\n" +
                "       ISNULL(chats.ActiveDays, 0) AS ActiveDays,\r\n" +
                "       ISNULL(chats.AppsUsed, 0) AS AppsUsed,\r\n" +
                "       ISNULL(chats.AgentsUsed, 0) AS AgentsUsed,\r\n" +
                "       CAST(ISNULL(chats.CoworkInteractions, 0) AS bigint) AS CoworkInteractions,\r\n" +
                (includeCoworkReport
                    ? "       coworkReport.CoworkReportTotalTasks AS CoworkReportTotalTasks,\r\n" +
                      "       coworkReport.CoworkReportScheduledTasks AS CoworkReportScheduledTasks,\r\n" +
                      "       coworkReport.CoworkReportUserInitiatedTasks AS CoworkReportUserInitiatedTasks,\r\n" +
                      "       coworkReport.CoworkReportActiveDays AS CoworkReportActiveDays,\r\n" +
                      "       coworkReport.CoworkReportLastActivityDate AS CoworkReportLastActivityDate,\r\n" +
                      "       coworkReport.CoworkReportRetainedUser AS CoworkReportRetainedUser,\r\n"
                    : "       CAST(NULL AS int) AS CoworkReportTotalTasks,\r\n" +
                      "       CAST(NULL AS int) AS CoworkReportScheduledTasks,\r\n" +
                      "       CAST(NULL AS int) AS CoworkReportUserInitiatedTasks,\r\n" +
                      "       CAST(NULL AS int) AS CoworkReportActiveDays,\r\n" +
                      "       CAST(NULL AS datetime) AS CoworkReportLastActivityDate,\r\n" +
                      "       CAST(NULL AS bit) AS CoworkReportRetainedUser,\r\n") +
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
                (includeCoworkReport ? "LEFT JOIN CoworkReportSnapshot AS coworkReport ON coworkReport.user_id = u.id\r\n" : string.Empty) +
                "OUTER APPLY (\r\n" +
                "    SELECT TOP (1) e.reason, e.note, e.excluded_by, e.excluded_utc, e.review_after_utc\r\n" +
                "    FROM dbo.copilot_adoption_reclaim_exclusions AS e\r\n" +
                "    WHERE e.user_id = u.id\r\n" +
                "      AND (e.review_after_utc IS NULL OR e.review_after_utc > SYSUTCDATETIME())\r\n" +
                "    ORDER BY e.excluded_utc DESC, e.id DESC\r\n" +
                ") AS exclusion\r\n" +
                "OUTER APPLY (\r\n" +
                "    SELECT TOP (1) e.user_id\r\n" +
                "    FROM dbo.copilot_adoption_reclaim_exclusions AS e\r\n" +
                "    WHERE e.user_id = u.id\r\n" +
                "      AND e.review_after_utc <= SYSUTCDATETIME()\r\n" +
                "    ORDER BY e.review_after_utc DESC, e.excluded_utc DESC, e.id DESC\r\n" +
                ") AS expired\r\n" +
                // Ordered by id so the drill-down cap truncates deterministically: the same users are
                // dropped on every run, which makes a capped report reproducible instead of randomly
                // different. That is still a biased sample - oldest user records are over-represented -
                // so the service warns explicitly if the cap ever bites.
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

            var derived = "(" + string.Join("\r\n              + ", parts) + ")";
            return derived;
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

        #region Cowork readiness

        /// <summary>
        /// Every Copilot seat holder with the two signals Cowork readiness is judged on: how much Cowork
        /// they already use, and how much delegable coordination work they carry.
        ///
        /// <para>Run as its own step rather than by extending <see cref="LicensedUsersSql"/>. That query is
        /// the hottest path in the whole report, and bolting four more workload CTEs onto it would make
        /// every existing figure pay for a feature on a different tab. Here the two run concurrently, so the
        /// page costs the slower of them rather than the sum, and a failure degrades this tab alone.</para>
        ///
        /// <para>The Copilot engagement score is deliberately <b>not</b> computed here. It is carried across
        /// in memory from the already-scored licensed-user set, because a second implementation of the
        /// engagement formula would eventually disagree with the first - and a user shown as Established on
        /// one tab and "not fluent enough" on another is exactly the contradiction
        /// <see cref="CopilotAdoptionScoring"/> exists to prevent.</para>
        ///
        /// <para>The Microsoft 365 workload figures are read across the whole window and reduced to a
        /// per-active-day average by <see cref="PerActiveDay"/>, for the same correctness reason as
        /// <see cref="LicenceOpportunitiesSql"/>: these are Graph's <i>daily</i> user-detail reports, so
        /// seeking a single date would make the answer "whoever happened to be working last Tuesday".</para>
        ///
        /// <para><b>No new index is required, and that was measured rather than assumed.</b> At synthetic
        /// scale (6,000,000 interactions, 200,000 users, 180 days of history, 15,000 seats, Cowork ~9% of
        /// rows; medians with the buffer pool and plan cache cleared between runs) the Cowork aggregate
        /// rides the existing <c>IX_copilot_chats_time_stamp_user_id</c> - key <c>(time_stamp, user_id)</c>
        /// with <c>app_host</c> and <c>agent_id</c> INCLUDEd - from
        /// <c>DenormaliseCopilotChatUserAndTime</c>:</para>
        ///
        /// <list type="table">
        ///   <listheader><term>Shape</term><description>28-day window / 180-day window</description></listheader>
        ///   <item>
        ///     <term>No supporting index</term>
        ///     <description>43,811 reads, 1,789 ms / 43,811 reads, 3,488 ms (clustered scan)</description>
        ///   </item>
        ///   <item>
        ///     <term>Existing production index</term>
        ///     <description><b>6,466 reads, 502 ms</b> / 41,525 reads, 3,305 ms (index seek)</description>
        ///   </item>
        ///   <item>
        ///     <term>Added filtered <c>WHERE app_host = 'cowork'</c> index</term>
        ///     <description>6,466 reads, 590 ms - <b>identical reads, slightly slower</b></description>
        ///   </item>
        /// </list>
        ///
        /// <para>So the existing index gives a 6.8x reads and 3.6x elapsed improvement on the default
        /// 28-day window, and <b>the Cowork-specific index was measured and rejected</b>: it produced
        /// exactly the same logical reads because the optimiser keeps choosing the index that is already
        /// there, while costing another ~320 MB on the largest table in the schema and an offline build on
        /// every customer's upgrade. A negative result is still a result - shipping it would have been an
        /// "optimisation" that made nothing faster.</para>
        ///
        /// <para>The 180-day column is close to the no-index numbers on purpose, and is not a regression:
        /// at that width the window covers essentially the whole table, so a scan is the correct plan. It
        /// is reported here because the repo requires more than one selectivity - a shape that only ever
        /// gets tested at its best window is not proven.</para>
        /// </summary>
        /// <param name="seatLicenceTypeIds">Licence-type ids classified as Copilot seats.</param>
        /// <param name="coworkAgentIds">Agent ids identified as Cowork; may be empty.</param>
        /// <param name="options">Tuning, used to build the ranking expression.</param>
        /// <param name="includeCopilotAudit">
        /// False when the Copilot audit import has nothing for this window, in which case Cowork usage is
        /// unobservable and its columns collapse to zero rather than joining an absent CTE.
        /// </param>
        /// <param name="includeM365Usage">
        /// False when the Microsoft 365 usage reports have no settled snapshot, in which case the
        /// coordination-load signals are unobservable and their CTEs are omitted entirely.
        /// </param>
        public static string CoworkReadinessSql(
            IEnumerable<int> seatLicenceTypeIds,
            IEnumerable<int> coworkAgentIds,
            CopilotAdoptionOptions options,
            bool includeCopilotAudit,
            bool includeM365Usage,
            bool includeCoworkReport = false)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var cowork = CoworkPredicate(coworkAgentIds);

            // The ranking expression may only reference CTEs that are actually present: when the usage
            // reports are unavailable their CTEs are omitted, so each component has to collapse to a
            // literal zero rather than to an unbound column reference.
            var loadScore = CopilotAdoptionScoring.BuildCoworkLoadScoreSql(
                o,
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
                    "-- Cowork use, per seat holder. Rides IX_copilot_chats_time_stamp_user_id, whose key is\r\n" +
                    "-- (time_stamp, user_id) with app_host and agent_id INCLUDEd - so the window seek, the\r\n" +
                    "-- grouping and the Cowork predicate are all served without touching the base table.\r\n" +
                    "--\r\n" +
                    "-- ActiveDays is the column the 'regular use' verdict rests on: an interaction count\r\n" +
                    "-- cannot tell habitual use apart from one long afternoon of experimenting.\r\n" +
                    "CoworkUsage AS (\r\n" +
                    "    SELECT c.user_id AS user_id,\r\n" +
                    "           COUNT_BIG(*) AS Interactions,\r\n" +
                    "           COUNT(DISTINCT CAST(c.time_stamp AS date)) AS ActiveDays,\r\n" +
                    "           MAX(c.time_stamp) AS LastInteractionUtc\r\n" +
                    "    FROM dbo.copilot_chats AS c\r\n" +
                    "    WHERE c.time_stamp >= @from\r\n" +
                    "      AND c.user_id IS NOT NULL\r\n" +
                    $"      AND ({cowork})\r\n" +
                    "    GROUP BY c.user_id\r\n" +
                    ")");
            }

            if (includeCoworkReport)
            {
                ctes.Add(
                    "CoworkReportUsage AS (\r\n" +
                    "    SELECT r.user_id AS user_id,\r\n" +
                    "           r.total_tasks AS TotalTasks,\r\n" +
                    "           r.scheduled_tasks AS ScheduledTasks,\r\n" +
                    "           r.user_initiated_tasks AS UserInitiatedTasks,\r\n" +
                    "           r.active_days AS ActiveDays,\r\n" +
                    "           r.last_activity_date AS LastActivityDate,\r\n" +
                    "           r.retained_user AS RetainedUser\r\n" +
                    "    FROM dbo.cowork_usage_user_activity_log AS r\r\n" +
                    "    WHERE r.[date] = @coworkReportDate\r\n" +
                    "      AND r.report_period_days = @coworkReportPeriodDays\r\n" +
                    "      AND EXISTS (SELECT 1 FROM SeatUsers AS s WHERE s.user_id = r.user_id)\r\n" +
                    ")");
            }

            if (includeM365Usage)
            {
                // Each workload CTE is restricted to seat holders. Without the semi-join these would
                // aggregate the entire directory's activity and then throw almost all of it away at the
                // final join - on a 200,000-user tenant with a few thousand Copilot seats that is most of
                // the query's cost spent on rows that cannot appear in the result.
                ctes.Add(
                    "TeamsUsage AS (\r\n" +
                    "    SELECT t.user_id AS user_id,\r\n" +
                    "           " + PerActiveDay("t.private_chat_count + t.team_chat_count + t.post_messages + t.reply_messages", "t.[date]") + " AS Messages,\r\n" +
                    "           " + PerActiveDay("t.meetings_attended_count + t.meetings_organized_count", "t.[date]") + " AS Meetings,\r\n" +
                    "           MAX(t.last_activity_date) AS LastActivity\r\n" +
                    "    FROM dbo.teams_user_activity_log AS t\r\n" +
                    "    WHERE t.[date] >= @m365From AND t.[date] <= @m365ReportDate\r\n" +
                    "      AND EXISTS (SELECT 1 FROM SeatUsers AS s WHERE s.user_id = t.user_id)\r\n" +
                    "    GROUP BY t.user_id\r\n" +
                    ")");

                ctes.Add(
                    // Named EmailsSent/EmailsRead, not Sent/Read: READ is a reserved word in T-SQL and an
                    // unbracketed alias produces a syntax error the moment it is referenced.
                    "MailUsage AS (\r\n" +
                    "    SELECT o.user_id AS user_id,\r\n" +
                    "           " + PerActiveDay("o.email_send_count", "o.[date]") + " AS EmailsSent,\r\n" +
                    "           " + PerActiveDay("o.email_read_count", "o.[date]") + " AS EmailsRead,\r\n" +
                    "           MAX(o.last_activity_date) AS LastActivity\r\n" +
                    "    FROM dbo.outlook_user_activity_log AS o\r\n" +
                    "    WHERE o.[date] >= @m365From AND o.[date] <= @m365ReportDate\r\n" +
                    "      AND EXISTS (SELECT 1 FROM SeatUsers AS s WHERE s.user_id = o.user_id)\r\n" +
                    "    GROUP BY o.user_id\r\n" +
                    ")");

                ctes.Add(
                    "-- SharePoint and OneDrive are one signal (\"works with documents\"), so they are summed\r\n" +
                    "-- rather than shown as two weak ones. A day the user touched both counts once.\r\n" +
                    "FileUsage AS (\r\n" +
                    "    SELECT f.user_id AS user_id,\r\n" +
                    "           " + PerActiveDay("f.viewed_or_edited", "f.[date]") + " AS ViewedOrEdited,\r\n" +
                    "           MAX(f.last_activity_date) AS LastActivity\r\n" +
                    "    FROM (\r\n" +
                    "        SELECT sp.user_id, sp.[date], sp.viewed_or_edited, sp.last_activity_date\r\n" +
                    "        FROM dbo.sharepoint_user_activity_log AS sp\r\n" +
                    "        WHERE sp.[date] >= @m365From AND sp.[date] <= @m365ReportDate\r\n" +
                    "          AND EXISTS (SELECT 1 FROM SeatUsers AS s WHERE s.user_id = sp.user_id)\r\n" +
                    "        UNION ALL\r\n" +
                    "        SELECT od.user_id, od.[date], od.viewed_or_edited, od.last_activity_date\r\n" +
                    "        FROM dbo.onedrive_user_activity_log AS od\r\n" +
                    "        WHERE od.[date] >= @m365From AND od.[date] <= @m365ReportDate\r\n" +
                    "          AND EXISTS (SELECT 1 FROM SeatUsers AS s WHERE s.user_id = od.user_id)\r\n" +
                    "    ) AS f\r\n" +
                    "    GROUP BY f.user_id\r\n" +
                    ")");
            }

            var coworkSelect = includeCopilotAudit
                ? "       CAST(ISNULL(cw.Interactions, 0) AS bigint) AS CoworkInteractions,\r\n" +
                  "       ISNULL(cw.ActiveDays, 0) AS CoworkActiveDays,\r\n" +
                  "       cw.LastInteractionUtc AS LastCoworkInteractionUtc,\r\n"
                : "       CAST(0 AS bigint) AS CoworkInteractions,\r\n" +
                  "       0 AS CoworkActiveDays,\r\n" +
                  "       CAST(NULL AS datetime) AS LastCoworkInteractionUtc,\r\n";

            var coworkReportSelect = includeCoworkReport
                ? "       report.TotalTasks AS CoworkReportTotalTasks,\r\n" +
                  "       report.ScheduledTasks AS CoworkReportScheduledTasks,\r\n" +
                  "       report.UserInitiatedTasks AS CoworkReportUserInitiatedTasks,\r\n" +
                  "       report.ActiveDays AS CoworkReportActiveDays,\r\n" +
                  "       report.LastActivityDate AS CoworkReportLastActivityDate,\r\n" +
                  "       report.RetainedUser AS CoworkReportRetainedUser,\r\n"
                : "       CAST(NULL AS int) AS CoworkReportTotalTasks,\r\n" +
                  "       CAST(NULL AS int) AS CoworkReportScheduledTasks,\r\n" +
                  "       CAST(NULL AS int) AS CoworkReportUserInitiatedTasks,\r\n" +
                  "       CAST(NULL AS int) AS CoworkReportActiveDays,\r\n" +
                  "       CAST(NULL AS datetime) AS CoworkReportLastActivityDate,\r\n" +
                  "       CAST(NULL AS bit) AS CoworkReportRetainedUser,\r\n";

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

            // Existing Cowork users must survive the row cap. The ranking expression is coordination load
            // only, and load says nothing about whether someone already uses Cowork - so on a large tenant
            // a current Cowork user with modest measured workload could be ranked below thousands of busier
            // colleagues and truncated away. That would drop them from the spending-policy scoping list and
            // silently REVOKE the access of the very people proving the capability works.
            //
            // Sorting them into the first block costs nothing when the cap is not reached and guarantees
            // they are present when it is. Omitted without the audit import, where Cowork use is
            // unobservable and a constant in ORDER BY is a SQL Server error.
            var coworkFirstOrder = includeCopilotAudit
                ? "ORDER BY CASE WHEN ISNULL(cw.Interactions, 0) > 0 THEN 0 ELSE 1 END,\r\n" +
                  "         LoadScore DESC, u.id\r\n"
                : "ORDER BY LoadScore DESC, u.id\r\n";

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
                "       u.account_enabled AS AccountEnabled,\r\n" +
                coworkSelect +
                coworkReportSelect +
                m365Select +
                $"       CAST({loadScore} AS float) AS LoadScore\r\n" +
                "FROM SeatUsers AS seats\r\n" +
                "JOIN dbo.users AS u ON u.id = seats.user_id\r\n" +
                "LEFT JOIN dbo.user_departments AS dept ON dept.id = u.department_id\r\n" +
                "LEFT JOIN dbo.user_job_titles AS title ON title.id = u.job_title_id\r\n" +
                "LEFT JOIN dbo.user_country_or_region AS country ON country.id = u.country_or_region_id\r\n" +
                "LEFT JOIN dbo.user_office_locations AS office ON office.id = u.office_location_id\r\n" +
                "LEFT JOIN dbo.user_company_name AS company ON company.id = u.company_name_id\r\n" +
                "LEFT JOIN dbo.users AS manager ON manager.id = u.manager_id\r\n" +
                (includeCopilotAudit ? "LEFT JOIN CoworkUsage AS cw ON cw.user_id = u.id\r\n" : string.Empty) +
                (includeCoworkReport ? "LEFT JOIN CoworkReportUsage AS report ON report.user_id = u.id\r\n" : string.Empty) +
                (includeM365Usage
                    ? "LEFT JOIN TeamsUsage AS teams ON teams.user_id = u.id\r\n" +
                      "LEFT JOIN MailUsage AS mail ON mail.user_id = u.id\r\n" +
                      "LEFT JOIN FileUsage AS files ON files.user_id = u.id\r\n"
                    : string.Empty) +
                // Guests are excluded for the same reason as on the opportunity list: a guest cannot be
                // put in a Cowork spending policy, so proposing one would discredit the list. Disabled
                // accounts are deliberately KEPT - a disabled account still holding a Copilot seat is a
                // finding the reclaim figures rely on, and hiding it here would contradict them.
                "WHERE 1 = 1\r\n" +
                ExcludeGuests("u") +
                coworkFirstOrder +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// Total Copilot Credits billed per user inside the window, for the seat holders on the Cowork tab.
        ///
        /// <para><b>This is not a Cowork figure and must never be labelled as one.</b> Microsoft meters
        /// Cowork against the shared Copilot Credits pool and publishes no per-row workload discriminator,
        /// so this is the user's total consumption across every credit-billed Copilot workload. It is worth
        /// showing anyway - a rollout conversation needs to know who is already consuming credits - but only
        /// with that caveat attached.</para>
        ///
        /// <para>Users with no row are absent from the result rather than returned as zero, so the caller
        /// can render "not attributable" instead of claiming the user costs nothing. The converse matters
        /// just as much and is why there is no <c>HAVING SUM(...) &gt; 0</c> here: a user whose imported
        /// rows genuinely total zero is returned <i>as zero</i>. Filtering those out would have collapsed
        /// them into the same "not attributable" bucket as a user the importer never saw, which is the
        /// exact conflation of "we do not know" with "it is nothing" that the rest of this tab exists to
        /// avoid.</para>
        ///
        /// <para>A plain <c>SUM</c> is safe here: the per-user importer keys its rows on
        /// (usage date, user, environment) with no agent grain - <c>CopilotStudioCreditImporter.MapUserRows</c>
        /// never sets <c>agent_id</c> on this table - so there is no per-agent breakdown sitting alongside a
        /// per-user total waiting to be double counted. Rows whose Entra object id could not be resolved to
        /// a <c>dbo.users</c> row are excluded, because an unattributable credit cannot be shown against a
        /// person; they remain visible on the Agent Costs page, which reports by object id.</para>
        /// </summary>
        public static string CoworkUserCreditsSql(IEnumerable<int> seatLicenceTypeIds)
        {
            return
                "WITH SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                "    FROM dbo.user_license_type_lookups AS ul\r\n" +
                $"    WHERE ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                ")\r\n" +
                "SELECT cu.user_id AS UserId,\r\n" +
                "       CAST(SUM(cu.billed_credits) AS decimal(18,4)) AS BilledCredits\r\n" +
                "FROM dbo.copilot_studio_credit_user_daily AS cu\r\n" +
                "WHERE cu.usage_date >= @from\r\n" +
                "  AND cu.user_id IS NOT NULL\r\n" +
                "  AND EXISTS (SELECT 1 FROM SeatUsers AS s WHERE s.user_id = cu.user_id)\r\n" +
                "GROUP BY cu.user_id\r\n" +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// The tenant's latest Copilot Credit capacity snapshot, used as rollout headroom on the Cowork tab.
        ///
        /// <para><b>This is the shared Copilot Credits pool, not Cowork-only spend.</b> Cowork genuinely
        /// consumes from it, which is what makes it valid headroom for a rollout decision - but Copilot
        /// Studio and other credit-billed workloads draw on the same pool and Microsoft publishes no way to
        /// separate them, so every label built from this must say so.</para>
        ///
        /// <para>Latest snapshot regardless of the reporting window, matching the Agent Costs page: it is a
        /// point-in-time tenant total, and an admin planning a rollout wants today's headroom rather than
        /// headroom as at the end of an arbitrary report period.</para>
        /// </summary>
        public const string CoworkCreditCapacitySql =
            "SELECT TOP 1\r\n" +
            "       cap.snapshot_utc AS SnapshotUtc,\r\n" +
            "       cap.entitled AS Entitled,\r\n" +
            "       cap.consumed AS Consumed,\r\n" +
            "       cap.available AS AvailableCredits,\r\n" +
            "       cap.pay_as_you_go_consumed AS PayAsYouGoConsumed,\r\n" +
            "       cap.status AS Status\r\n" +
            "FROM dbo.copilot_studio_credit_capacity AS cap\r\n" +
            "ORDER BY cap.snapshot_utc DESC;";

        /// <summary>
        /// Whether the Copilot Studio credit tables exist at all.
        ///
        /// <para>The credit figures on the Cowork tab are optional decoration from a <i>separate</i>
        /// import, and a database that predates the agent-cost migration simply does not have these
        /// tables. Probing for them is not defensive clutter: without it, every Cowork analysis on such
        /// a database would surface "Could not load Copilot Credit capacity: Invalid object name..." as
        /// a warning, which reads as a fault on a page whose credibility depends on its warnings meaning
        /// something. A missing optional import is a legitimate state, and is reported by the absence of
        /// the credit block rather than by an error.</para>
        /// </summary>
        public const string HasCreditTablesSql =
            "SELECT CASE WHEN OBJECT_ID('dbo.copilot_studio_credit_user_daily') IS NOT NULL\r\n" +
            "             AND OBJECT_ID('dbo.copilot_studio_credit_capacity') IS NOT NULL\r\n" +
            "            THEN 1 ELSE 0 END AS Value;";

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
        ///
        /// <para>
        /// The Microsoft 365 workload figures are read across the whole analysis window and reduced to a
        /// per-active-day average, NOT from a single report date. That is not a refinement, it is a
        /// correctness requirement: <c>teams_user_activity_log</c> and friends are Graph's <i>daily</i>
        /// user-detail reports (<c>getTeamsUserActivityUserDetail(date=...)</c>), which return only the
        /// users who did something on that one day. Seeking a single <c>[date]</c> therefore made the
        /// candidate list "everyone who happened to be active last Tuesday" - a settled snapshot landing
        /// on a weekend or a public holiday emptied the tab completely, and anyone on leave that day was
        /// invisible however heavy a user they normally are. Averaging per active day (rather than
        /// summing) keeps the values in the same units the
        /// <see cref="CopilotAdoptionOptions.OpportunityCollaborationTarget"/> family of targets is
        /// calibrated in, so the score means the same thing it always did.
        /// </para>
        /// <para>
        /// Cost: none. Measured at synthetic scale (14.4m rows, 200k users, 120 days of history, medians
        /// of 4 warm runs with the plan cache cleared), the single-date seek and the window aggregate
        /// produce the <b>same</b> plan and the same 306,689 logical reads - the metric columns are not
        /// in <c>IX_date</c>, so SQL Server already chose a clustered-index scan for the one-day version.
        /// Only CPU differs: 146 ms -> 249 ms elapsed at a 28-day window, 130 ms -> 622 ms at 90 days,
        /// while covering 200,000 users instead of 120,000. A <c>ROW_NUMBER()</c> "latest row per user"
        /// shape was measured too and rejected: identical reads but 441 ms / 996 ms elapsed, because the
        /// windowed sort costs far more than the hash aggregate and degrades faster as the window widens.
        /// </para>
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
                    "    SELECT c.user_id AS user_id,\r\n" +
                    "           COUNT_BIG(*) AS Interactions,\r\n" +
                    "           COUNT(DISTINCT CAST(c.time_stamp AS date)) AS ActiveDays,\r\n" +
                    "           MAX(c.time_stamp) AS LastInteractionUtc\r\n" +
                    "    FROM dbo.copilot_chats AS c\r\n" +
                    "    WHERE c.time_stamp >= @from AND c.user_id IS NOT NULL\r\n" +
                    "    GROUP BY c.user_id\r\n" +
                    ")");
            }

            if (includeM365Usage)
            {
                ctes.Add(
                    "-- Graph's daily user-detail reports: one row per user per day they did something, so a\r\n" +
                    "-- single [date] only ever sees that day's active users. Read the whole window and reduce\r\n" +
                    "-- to a per-active-day average, which is the unit the opportunity targets are set in.\r\n" +
                    "TeamsUsage AS (\r\n" +
                    "    SELECT t.user_id AS user_id,\r\n" +
                    "           " + PerActiveDay("t.private_chat_count + t.team_chat_count + t.post_messages + t.reply_messages", "t.[date]") + " AS Messages,\r\n" +
                    "           " + PerActiveDay("t.meetings_attended_count + t.meetings_organized_count", "t.[date]") + " AS Meetings,\r\n" +
                    "           MAX(t.last_activity_date) AS LastActivity\r\n" +
                    "    FROM dbo.teams_user_activity_log AS t\r\n" +
                    "    WHERE t.[date] >= @m365From AND t.[date] <= @m365ReportDate\r\n" +
                    "    GROUP BY t.user_id\r\n" +
                    ")");

                ctes.Add(
                    "MailUsage AS (\r\n" +
                    "    SELECT o.user_id AS user_id,\r\n" +
                    // Named EmailsSent/EmailsRead, not Sent/Read: READ is a reserved word in T-SQL and
                    // an unbracketed alias produces a syntax error the moment it is referenced.
                    "           " + PerActiveDay("o.email_send_count", "o.[date]") + " AS EmailsSent,\r\n" +
                    "           " + PerActiveDay("o.email_read_count", "o.[date]") + " AS EmailsRead,\r\n" +
                    "           MAX(o.last_activity_date) AS LastActivity\r\n" +
                    "    FROM dbo.outlook_user_activity_log AS o\r\n" +
                    "    WHERE o.[date] >= @m365From AND o.[date] <= @m365ReportDate\r\n" +
                    "    GROUP BY o.user_id\r\n" +
                    ")");

                ctes.Add(
                    "-- SharePoint and OneDrive are one signal (\"works with documents\"), so they are\r\n" +
                    "-- summed rather than shown as two weak ones. A day the user touched both counts once.\r\n" +
                    "FileUsage AS (\r\n" +
                    "    SELECT f.user_id AS user_id,\r\n" +
                    "           " + PerActiveDay("f.viewed_or_edited", "f.[date]") + " AS ViewedOrEdited,\r\n" +
                    "           MAX(f.last_activity_date) AS LastActivity\r\n" +
                    "    FROM (\r\n" +
                    "        SELECT sp.user_id, sp.[date], sp.viewed_or_edited, sp.last_activity_date\r\n" +
                    "        FROM dbo.sharepoint_user_activity_log AS sp\r\n" +
                    "        WHERE sp.[date] >= @m365From AND sp.[date] <= @m365ReportDate\r\n" +
                    "        UNION ALL\r\n" +
                    "        SELECT od.user_id, od.[date], od.viewed_or_edited, od.last_activity_date\r\n" +
                    "        FROM dbo.onedrive_user_activity_log AS od\r\n" +
                    "        WHERE od.[date] >= @m365From AND od.[date] <= @m365ReportDate\r\n" +
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

            // Proven demand must survive the row cap. The list is TOP (@maxRows) ORDER BY the composite
            // score, and that score cannot express proven demand: the Copilot weight sits below the
            // recommendation bar while general Microsoft 365 volume sits above it. So on a large tenant
            // a person already using Copilot Chat daily could be ranked below thousands of merely busy
            // users and truncated away before the C# scorer ever sees them - which would silently
            // reinstate the very defect the tier was added to fix.
            //
            // Sorting them into the first block costs nothing when the cap is not reached, and
            // guarantees they are present when it is. Omitted entirely without the audit import: proven
            // demand is unobservable without it, and a constant in ORDER BY is a SQL Server error.
            var provenDemandOrder = includeCopilotAudit
                ? "ORDER BY CASE WHEN ISNULL(copilot.ActiveDays, 0) >= "
                  + $"{Math.Max(1, o.OpportunityProvenDemandMinActiveDays)} THEN 0 ELSE 1 END,\r\n"
                  + "         RankScore DESC, u.id\r\n"
                : "ORDER BY RankScore DESC, u.id\r\n";

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
                // Neither can an external guest. On a real tenant guests were a meaningful share of the
                // directory, every one of them ranked as a licence candidate it is impossible to act on.
                // See issue #360.
                ExcludeGuests("u") +
                provenDemandOrder +
                "OPTION (RECOMPILE);";
        }

        /// <summary>
        /// A user's average of <paramref name="metric"/> per day they actually appear in a Graph daily
        /// usage report, over whatever window the surrounding query filtered to.
        ///
        /// Divided by the user's own active days rather than by the length of the window on purpose:
        /// the opportunity targets (<see cref="CopilotAdoptionOptions.OpportunityCollaborationTarget"/>
        /// and friends) describe what a heavy user does on a working day, so dividing by calendar days
        /// would dilute every user by their weekends and public holidays and quietly halve the score of
        /// a perfectly normal knowledge worker. <c>COUNT(DISTINCT ...)</c> is free here - it rides the
        /// same <c>GROUP BY</c> as the sums.
        /// </summary>
        private static string PerActiveDay(string metric, string dateColumn)
        {
            return $"CAST(ROUND(SUM(CAST({metric} AS float)) "
                 + $"/ NULLIF(COUNT(DISTINCT CAST({dateColumn} AS date)), 0), 0) AS bigint)";
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
                "    SELECT DISTINCT c.user_id\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    WHERE c.time_stamp >= @from\r\n" +
                "      AND c.user_id IS NOT NULL\r\n" +
                "      AND NOT EXISTS (\r\n" +
                "          SELECT 1 FROM dbo.user_license_type_lookups AS ul\r\n" +
                "          WHERE ul.user_id = c.user_id\r\n" +
                $"            AND ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                "      )\r\n" +
                // Same population as the candidate list, which also excludes guests - otherwise this
                // headline reports unmet demand that can never appear in the list it points you to.
                ExcludeGuestsByUserId("c.user_id", "      ") +
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
                "SET NOCOUNT ON;\r\n" +
                "IF OBJECT_ID('tempdb..#agent_grain') IS NOT NULL DROP TABLE #agent_grain;\r\n" +
                "\r\n" +
                "WITH SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                $"    FROM dbo.user_license_type_lookups AS ul WHERE ul.license_type_id IN ({seats})\r\n" +
                ")\r\n" +
                // ONE pass over copilot_chats, collapsed to one row per (agent, user, day, app) and
                // MATERIALISED, because everything below needs to read it four different ways.
                //
                // This was a CTE called AgentUse that four aggregates then selected from, with a comment
                // claiming it was "one projected pass". It was not: SQL Server does not materialise a CTE,
                // it expands it at every reference, so the 120-day scan happened four times over. A
                // representative large-data A/B showed fewer fact-table reads, no spool, lower CPU and
                // lower elapsed time. Exact environment measurements remain out-of-band. Row-for-row
                // equivalence was proven with EXCEPT in both directions.
                //
                // A temp table rather than another CTE on purpose. Rolling the four aggregates up with
                // COUNT(DISTINCT) over a single CTE was also tried and was 4.6x WORSE: it reintroduced
                // exactly the spool the original four-CTE shape had been written to avoid. Materialising
                // once is what gets both - no repeated scan AND no spool.
                //
                // Caveat for whoever benchmarks this next: the current synthetic generator produces
                // roughly one grain row per interaction. When the grain does not compress there is nothing
                // to gain from materialising it, so fix that data shape before using the synthetic bench
                // to judge this query.
                "SELECT c.agent_id AS agent_id,\r\n" +
                "       c.user_id AS user_id,\r\n" +
                "       CAST(c.time_stamp AS date) AS active_date,\r\n" +
                $"       {AppHostKey("c.app_host", "(unknown)")} AS app_host,\r\n" +
                // Licensing is a property of the user, so it is constant within the group.
                "       MAX(CASE WHEN seats.user_id IS NOT NULL THEN 1 ELSE 0 END) AS IsLicensed,\r\n" +
                "       COUNT_BIG(*) AS Interactions,\r\n" +
                // Window-scoped as well as history-scoped. The inventory needs the long view to spot a
                // dormant agent, but the headline "interactions per agent user" divides by a user count
                // that is scoped to the reporting period - mixing the two inflates that KPI by the ratio
                // of the windows (roughly 4x at the defaults).
                "       COUNT_BIG(CASE WHEN c.time_stamp >= @from THEN 1 END) AS WindowInteractions,\r\n" +
                "       MIN(c.time_stamp) AS FirstUsedUtc,\r\n" +
                "       MAX(c.time_stamp) AS LastUsedUtc\r\n" +
                "INTO #agent_grain\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "LEFT JOIN SeatUsers AS seats ON seats.user_id = c.user_id\r\n" +
                "WHERE c.time_stamp >= @historyFrom\r\n" +
                // Redundant against the inner join to copilot_agents below, but it lets the optimiser
                // eliminate the (usually large) majority of Copilot interactions that carry no agent
                // before it does any joining, rather than discovering it during the join.
                "  AND c.agent_id IS NOT NULL\r\n" +
                $"GROUP BY c.agent_id, c.user_id, CAST(c.time_stamp AS date), {AppHostKey("c.app_host", "(unknown)")}\r\n" +
                "OPTION (RECOMPILE);\r\n" +
                "\r\n" +
                // Every aggregate below now reads the small grain table instead of copilot_chats.
                "SELECT TOP (@maxRows)\r\n" +
                "       ag.id AS AgentId,\r\n" +
                "       ISNULL(ag.name, '(unnamed agent)') AS Name,\r\n" +
                "       ag.agent_id AS AgentKey,\r\n" +
                "       CAST(ISNULL(ag.is_custom_agent, 0) AS bit) AS IsCustomAgent,\r\n" +
                "       t.Interactions AS Interactions,\r\n" +
                "       t.WindowInteractions AS WindowInteractions,\r\n" +
                "       ISNULL(u.Users, 0) AS Users,\r\n" +
                "       ISNULL(u.LicensedUsers, 0) AS LicensedUsers,\r\n" +
                "       ISNULL(d.ActiveDays, 0) AS ActiveDays,\r\n" +
                "       ISNULL(a.AppsUsed, 0) AS AppsUsed,\r\n" +
                "       t.FirstUsedUtc AS FirstUsedUtc,\r\n" +
                "       t.LastUsedUtc AS LastUsedUtc\r\n" +
                "FROM (SELECT agent_id,\r\n" +
                "             SUM(Interactions) AS Interactions,\r\n" +
                "             SUM(WindowInteractions) AS WindowInteractions,\r\n" +
                "             MIN(FirstUsedUtc) AS FirstUsedUtc,\r\n" +
                "             MAX(LastUsedUtc) AS LastUsedUtc\r\n" +
                "      FROM #agent_grain GROUP BY agent_id) AS t\r\n" +
                "JOIN dbo.copilot_agents AS ag ON ag.id = t.agent_id\r\n" +
                // COUNT(user_id), not COUNT(*). Grouping by (agent_id, user_id) puts unattributed events
                // into their own NULL group, which COUNT(*) would count as a person. That matters because
                // Users is not just displayed - it is the adoption threshold (AgentMinUsers, default 3),
                // so a single unattributed interaction could flip an agent from Review to Keep.
                "LEFT JOIN (SELECT agent_id, COUNT(user_id) AS Users, SUM(IsLicensed) AS LicensedUsers\r\n" +
                "           FROM (SELECT agent_id, user_id, MAX(IsLicensed) AS IsLicensed\r\n" +
                "                 FROM #agent_grain GROUP BY agent_id, user_id) AS x\r\n" +
                "           GROUP BY agent_id) AS u ON u.agent_id = t.agent_id\r\n" +
                "LEFT JOIN (SELECT agent_id, COUNT(*) AS ActiveDays\r\n" +
                "           FROM (SELECT DISTINCT agent_id, active_date FROM #agent_grain) AS y\r\n" +
                "           GROUP BY agent_id) AS d ON d.agent_id = t.agent_id\r\n" +
                "LEFT JOIN (SELECT agent_id, COUNT(*) AS AppsUsed\r\n" +
                "           FROM (SELECT DISTINCT agent_id, app_host FROM #agent_grain) AS z\r\n" +
                "           GROUP BY agent_id) AS a ON a.agent_id = t.agent_id\r\n" +
                // ag.id breaks ties deterministically. Without it, TOP truncates the tie region at the cap
                // arbitrarily, so which agents survive varies between runs - the sibling capped queries in this
                // file (LicensedUsersSql, LicenceOpportunitiesSql) both carry a unique tie-break for the same reason.
                "ORDER BY Interactions DESC, ag.id\r\n" +
                "OPTION (RECOMPILE);\r\n" +
                "\r\n" +
                "DROP TABLE #agent_grain;";
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
                "JOIN dbo.users AS u ON u.id = c.user_id\r\n" +
                "LEFT JOIN dbo.user_departments AS dept ON dept.id = u.department_id\r\n" +
                "WHERE c.time_stamp >= @from\r\n" +
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
                // The window's unlicensed interactions, read ONCE. This used to be a CTE that four
                // separate aggregates selected from - see the note on the grain table below for what that
                // cost and what it was measured at.
                "SET NOCOUNT ON;\r\n" +
                "IF OBJECT_ID('tempdb..#unlicensed_grain') IS NOT NULL DROP TABLE #unlicensed_grain;\r\n" +
                "\r\n" +
                "WITH Unlicensed AS (\r\n" +
                "    SELECT c.user_id AS user_id,\r\n" +
                "           c.time_stamp AS time_stamp,\r\n" +
                $"           {AppHostKey("c.app_host", "(unknown)")} AS app_host,\r\n" +
                "           c.agent_id AS agent_id\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    WHERE c.time_stamp >= @from\r\n" +
                "      AND c.user_id IS NOT NULL\r\n" +
                "      AND NOT EXISTS (\r\n" +
                "          SELECT 1 FROM dbo.user_license_type_lookups AS ul\r\n" +
                "          WHERE ul.user_id = c.user_id\r\n" +
                $"            AND ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                "      )\r\n" +
                // Kept in step with UnlicensedActiveUsersSql - this is the detail behind that count.
                ExcludeGuestsByUserId("c.user_id", "      ") +
                ")\r\n" +
                // ONE pass over copilot_chats, collapsed to one row per (user, day, app, agent) and
                // MATERIALISED, because the four aggregates below each need to read it differently.
                //
                // The Unlicensed CTE was previously selected from by four separate aggregates. SQL Server
                // does not materialise a CTE - it expands it at every reference - so the window scan, and
                // both of the NOT EXISTS lookups above, ran four times over. A representative large-data
                // A/B showed substantially fewer reads and lower elapsed time; exact environment
                // measurements remain out-of-band. The improvement was larger than the repeated reference
                // alone accounts for because the repeated shape also pushed the optimiser into a worse
                // plan for the licence and guest lookups. The new shape was verified row-for-row identical
                // with EXCEPT in both directions.
                //
                // See AgentUsageSql for why this is a temp table rather than another CTE.
                "SELECT user_id,\r\n" +
                "       CAST(time_stamp AS date) AS active_date,\r\n" +
                "       app_host,\r\n" +
                "       agent_id,\r\n" +
                "       COUNT_BIG(*) AS Interactions,\r\n" +
                "       MAX(time_stamp) AS LastInteractionUtc\r\n" +
                "INTO #unlicensed_grain\r\n" +
                "FROM Unlicensed\r\n" +
                "GROUP BY user_id, CAST(time_stamp AS date), app_host, agent_id\r\n" +
                "OPTION (RECOMPILE);\r\n" +
                "\r\n" +
                "SELECT TOP (@maxRows)\r\n" +
                "       t.user_id AS UserId,\r\n" +
                // Carried so this population can be grouped and filtered by email domain like the
                // licensed one. The users join below already exists for the department lookup, so this
                // column is free.
                "       u.user_name AS UserPrincipalName,\r\n" +
                "       ISNULL(NULLIF(LTRIM(RTRIM(dept.name)), ''), '') AS Department,\r\n" +
                "       t.Interactions AS Interactions,\r\n" +
                "       ISNULL(d.ActiveDays, 0) AS ActiveDays,\r\n" +
                "       ISNULL(a.AppsUsed, 0) AS AppsUsed,\r\n" +
                "       ISNULL(g.AgentsUsed, 0) AS AgentsUsed,\r\n" +
                "       t.LastInteractionUtc AS LastInteractionUtc\r\n" +
                "FROM (SELECT user_id, SUM(Interactions) AS Interactions,\r\n" +
                "             MAX(LastInteractionUtc) AS LastInteractionUtc\r\n" +
                "      FROM #unlicensed_grain GROUP BY user_id) AS t\r\n" +
                "LEFT JOIN (SELECT user_id, COUNT(*) AS ActiveDays\r\n" +
                "           FROM (SELECT DISTINCT user_id, active_date FROM #unlicensed_grain) AS x\r\n" +
                "           GROUP BY user_id) AS d ON d.user_id = t.user_id\r\n" +
                "LEFT JOIN (SELECT user_id, COUNT(*) AS AppsUsed\r\n" +
                "           FROM (SELECT DISTINCT user_id, app_host FROM #unlicensed_grain) AS y\r\n" +
                "           GROUP BY user_id) AS a ON a.user_id = t.user_id\r\n" +
                "LEFT JOIN (SELECT user_id, COUNT(*) AS AgentsUsed\r\n" +
                "           FROM (SELECT DISTINCT user_id, agent_id FROM #unlicensed_grain\r\n" +
                "                 WHERE agent_id IS NOT NULL) AS z\r\n" +
                "           GROUP BY user_id) AS g ON g.user_id = t.user_id\r\n" +
                "LEFT JOIN dbo.users AS u ON u.id = t.user_id\r\n" +
                "LEFT JOIN dbo.user_departments AS dept ON dept.id = u.department_id\r\n" +
                // Deterministic truncation - see the note in AgentUsageSql. This one matters more: when the cap
                // bites, FinaliseUnlicensed derives the habit distribution from whichever rows survived.
                "ORDER BY Interactions DESC, t.user_id\r\n" +
                "OPTION (RECOMPILE);\r\n" +
                "\r\n" +
                "DROP TABLE #unlicensed_grain;";
        }

        /// <summary>
        /// How Microsoft's audit log typed the resources Copilot referenced when answering.
        ///
        /// Deliberately NOT described as "the kinds of tenant content Copilot grounded its answers
        /// in". <c>AccessedResources[].Type</c> is one field carrying several unrelated taxonomies at
        /// once, and two of its commonest values - CITATION and WebSearchQuery - are respectively a
        /// usage role and grounding from outside the tenant. The caller classifies each value; see
        /// <see cref="Common.Entities.Copilot.CopilotAccessedResourceTaxonomy"/> and issue #468.
        /// </summary>
        public static string TopResourceTypesSql()
        {
            return
                "SELECT TOP (@top) ISNULL(rt.name, '" + Copilot.CopilotAccessedResourceTaxonomy.UnknownTypeLabel + "') AS Label,\r\n" +
                "       CAST(COUNT_BIG(*) AS float) AS Value\r\n" +
                "FROM dbo.copilot_event_accessed_resources AS ar\r\n" +
                "JOIN dbo.copilot_chats AS c ON c.event_id = ar.copilot_chat_id\r\n" +
                "LEFT JOIN dbo.copilot_event_accessed_resource_types AS rt ON rt.id = ar.resource_type_id\r\n" +
                "WHERE c.time_stamp >= @from\r\n" +
                "GROUP BY ISNULL(rt.name, '" + Copilot.CopilotAccessedResourceTaxonomy.UnknownTypeLabel + "')\r\n" +
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
                $"SELECT TOP (@top) {AppHostKey("c.app_host", "(unknown)")} AS Label,\r\n" +
                "       CAST(COUNT_BIG(*) AS float) AS Value\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "JOIN SeatUsers AS seats ON seats.user_id = c.user_id\r\n" +
                "WHERE c.time_stamp >= @from\r\n" +
                $"GROUP BY {AppHostKey("c.app_host", "(unknown)")}\r\n" +
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
                $"SELECT TOP (@top) {AppHostKey("c.app_host", "(unknown)")} AS Label,\r\n" +
                "       CAST(COUNT_BIG(*) AS float) AS Value\r\n" +
                "FROM dbo.copilot_chats AS c\r\n" +
                "WHERE c.time_stamp >= @from\r\n" +
                "  AND c.user_id IS NOT NULL\r\n" +
                "  AND NOT EXISTS (\r\n" +
                "      SELECT 1 FROM dbo.user_license_type_lookups AS ul\r\n" +
                "      WHERE ul.user_id = c.user_id\r\n" +
                $"        AND ul.license_type_id IN ({IdList(seatLicenceTypeIds)})\r\n" +
                "  )\r\n" +
                // Kept in step with UnlicensedActiveUsersSql - this breaks that same population down by app.
                ExcludeGuestsByUserId("c.user_id", "  ") +
                $"GROUP BY {AppHostKey("c.app_host", "(unknown)")}\r\n" +
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
        /// <para>
        /// The seat and guest lookups are joined AFTER the aggregate, not per chat row. Both answer a
        /// question about the user rather than the interaction, so per-row evaluation recomputed the same
        /// answer for every interaction a user had in the week. Measured on a synthetic customer-shaped
        /// bench, at a step concurrency of 1, comparing the two shapes over the same data:
        /// <list type="table">
        /// <item><description>6-month window:  5,551ms -&gt; 2,322ms elapsed (2.4x), CPU 21.1s -&gt; 12.5s</description></item>
        /// <item><description>12-month window: 11,523ms -&gt; 4,643ms elapsed (2.5x), CPU 44.7s -&gt; 24.7s</description></item>
        /// </list>
        /// Logical reads are unchanged (within 1%) because the same index pages are read either way - the
        /// saving is join and aggregation CPU, which is the resource that actually runs out on a
        /// tier-capped database. Verified row-for-row identical against the previous query with EXCEPT in
        /// both directions.
        /// </para>
        /// </remarks>
        public static string WeeklyAdoptionTrendSql(IEnumerable<int> seatLicenceTypeIds, IEnumerable<int> coworkAgentIds)
        {
            var week = WeekBucket("c.time_stamp");
            var cowork = CoworkPredicate(coworkAgentIds);
            var seats = IdList(seatLicenceTypeIds);

            return
                "WITH SeatUsers AS (\r\n" +
                "    SELECT DISTINCT ul.user_id AS user_id\r\n" +
                "    FROM dbo.user_license_type_lookups AS ul\r\n" +
                $"    WHERE ul.license_type_id IN ({seats})\r\n" +
                "),\r\n" +
                // Collapsed to one row per (week, user) FIRST, with the per-user facts carried as flags.
                // Every headline series here is a distinct *user* count under a different filter, and
                // asking for four of those as COUNT(DISTINCT ...) in one grouping makes SQL Server spool
                // the input and process each separately - 24.2M of this query's 24.5M logical reads were
                // that spool, and it ran for 315s against a 90s timeout. Grouping by (week, user) once
                // removes every distinct: a user is licensed or not for the whole week, so MAX() over the
                // flag is exactly the same answer, and the roll-up below is then a trivial SUM.
                //
                // This first pass touches NOTHING but copilot_chats. Whether a user holds a seat and
                // whether they are a guest are properties of the USER, not of the interaction, so joining
                // those two lookups here - as this query used to - evaluated them once per chat row to
                // produce an answer that is identical for every row the user appears in. The joins moved
                // below the aggregate instead, where they run once per (week, user).
                "ChatWeekUser AS (\r\n" +
                $"    SELECT {week} AS WeekStart,\r\n" +
                "           c.user_id AS user_id,\r\n" +
                $"           MAX(CASE WHEN ({cowork}) THEN 1 ELSE 0 END) AS CoworkRow,\r\n" +
                "           MAX(CASE WHEN c.agent_id IS NOT NULL THEN 1 ELSE 0 END) AS UsedAgent,\r\n" +
                "           COUNT_BIG(*) AS Interactions\r\n" +
                "    FROM dbo.copilot_chats AS c\r\n" +
                "    WHERE c.time_stamp >= @trendFrom\r\n" +
                "      AND c.time_stamp < @trendTo\r\n" +
                "      AND c.user_id IS NOT NULL\r\n" +
                $"    GROUP BY {week}, c.user_id\r\n" +
                "),\r\n" +
                // The per-user lookups, now once per (week, user) instead of once per interaction.
                "WeekUser AS (\r\n" +
                "    SELECT wu.WeekStart AS WeekStart,\r\n" +
                "           CASE WHEN seats.user_id IS NOT NULL THEN 1 ELSE 0 END AS IsLicensed,\r\n" +
                // Carried as a flag rather than filtered in the WHERE clause: the licensed series must
                // keep counting a guest that somehow holds a seat (that is a real licence being spent),
                // while the unlicensed series must exclude guests to agree with the headline count and
                // the candidate list.
                "           CASE WHEN guest_check.user_name LIKE " + GuestUserNamePattern +
                " THEN 1 ELSE 0 END AS IsGuest,\r\n" +
                // Cowork is "a licensed user who used Cowork". Licensing is constant across the week, so
                // ANDing it after the aggregate gives the same answer as testing it on every row.
                "           CASE WHEN seats.user_id IS NOT NULL\r\n" +
                "                 AND wu.CoworkRow = 1 THEN 1 ELSE 0 END AS IsCowork,\r\n" +
                "           wu.UsedAgent AS UsedAgent,\r\n" +
                // Likewise the two interaction counts: the old conditional COUNT_BIGs tested a condition
                // that is the same for every row of the group, so they are just the group's count or zero.
                "           CASE WHEN seats.user_id IS NOT NULL THEN wu.Interactions ELSE 0 END AS LicensedInteractions,\r\n" +
                "           CASE WHEN seats.user_id IS NULL THEN wu.Interactions ELSE 0 END AS UnlicensedInteractions\r\n" +
                "    FROM ChatWeekUser AS wu\r\n" +
                "    LEFT JOIN SeatUsers AS seats ON seats.user_id = wu.user_id\r\n" +
                "    LEFT JOIN dbo.users AS guest_check ON guest_check.id = wu.user_id\r\n" +
                "),\r\n" +
                "Weekly AS (\r\n" +
                "    SELECT WeekStart,\r\n" +
                "           SUM(IsLicensed) AS ActiveUsers,\r\n" +
                "           SUM(IsCowork) AS CoworkUsers,\r\n" +
                "           SUM(CASE WHEN IsLicensed = 0 AND IsGuest = 0 THEN 1 ELSE 0 END) AS UnlicensedUsers,\r\n" +
                "           SUM(UsedAgent) AS AgentUsers,\r\n" +
                "           SUM(LicensedInteractions) AS LicensedInteractions,\r\n" +
                "           SUM(CASE WHEN IsGuest = 0 THEN UnlicensedInteractions ELSE 0 END) AS UnlicensedInteractions\r\n" +
                "    FROM WeekUser\r\n" +
                "    GROUP BY WeekStart\r\n" +
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
