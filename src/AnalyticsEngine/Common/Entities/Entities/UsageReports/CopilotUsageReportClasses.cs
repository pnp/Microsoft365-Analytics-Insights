using Common.Entities.ActivityReports;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.UsageReports
{
    /// <summary>
    /// Which of the two Microsoft 365 Copilot user-count reports a <see cref="CopilotUserCountLog"/> row came from.
    /// Stored as a string so the value is self-describing in Power BI and in ad-hoc SQL.
    /// </summary>
    public static class CopilotUserCountReportTypes
    {
        /// <summary>
        /// getMicrosoft365CopilotUserCountSummary - one roll-up per requested period (D7/D28/...), so
        /// <see cref="CopilotUserCountLog.ReportPeriodDays"/> is always set and identifies the window.
        /// </summary>
        public const string Summary = "Summary";

        /// <summary>
        /// getMicrosoft365CopilotUserCountTrend - one row per calendar day. The requested period only decides
        /// how far back Graph returns days; the per-day numbers themselves are the same whichever window asked
        /// for them, so <see cref="CopilotUserCountLog.ReportPeriodDays"/> is deliberately left NULL. That keeps
        /// a D7 refresh updating the same rows a D180 backfill created instead of duplicating every day.
        /// </summary>
        public const string Trend = "Trend";
    }

    /// <summary>
    /// Canonical app names used in <see cref="CopilotUserCountLog.AppName"/>. These match the Graph CSV column
    /// prefixes ("&lt;app&gt; Enabled Users" / "&lt;app&gt; Active Users") so a new Microsoft app appears
    /// automatically as new rows rather than needing a new column and a migration.
    /// </summary>
    public static class CopilotAppNames
    {
        /// <summary>Tenant-wide roll-up across every Copilot app ("Any App" in the Graph report).</summary>
        public const string AnyApp = "Any App";
    }

    /// <summary>
    /// Names of the three Graph Microsoft 365 Copilot usage report functions. Lives here rather than in the
    /// importer because it is the value stored in <see cref="CopilotUsageReportImportLog.ReportName"/>, which
    /// the web app's Health page also reads.
    /// </summary>
    public static class CopilotUsageReportNames
    {
        /// <summary>Tenant aggregate: enabled vs active users per app, rolled up over the requested period.</summary>
        public const string UserCountSummary = "getMicrosoft365CopilotUserCountSummary";

        /// <summary>Tenant aggregate: enabled vs active users per app, one row per calendar day.</summary>
        public const string UserCountTrend = "getMicrosoft365CopilotUserCountTrend";

        /// <summary>Per-user detail. Licensed users only, and affected by the tenant's concealed-user-information setting.</summary>
        public const string UsageUserDetail = "getMicrosoft365CopilotUsageUserDetail";
    }

    /// <summary>
    /// Tenant-level Microsoft 365 Copilot enabled-vs-active user counts, from the Graph
    /// getMicrosoft365CopilotUserCountSummary and getMicrosoft365CopilotUserCountTrend reports.
    ///
    /// Deliberately narrow/tall - one row per (report type, period, date, app) - rather than the ~40-column
    /// wide shape the CSV uses. Microsoft adds Copilot surfaces regularly (Edge, Copilot Chat work/web and
    /// Copilot agents all arrived in report version 2), and each one would otherwise mean two more columns
    /// and another schema migration on every customer database.
    ///
    /// Counts here are Microsoft's own definition of "active" (a user-initiated action; merely opening the
    /// Copilot pane does not count) over licensed users only, which is what the Microsoft 365 admin centre
    /// shows. Our audit-log-derived figures answer a different question and will not match exactly - that is
    /// expected, and having both is the point.
    /// </summary>
    [Table("copilot_user_count_log")]
    public class CopilotUserCountLog : AbstractEFEntity
    {
        /// <summary>
        /// The date Graph last refreshed the report (its "Report Refresh Date"). Copilot usage data runs
        /// roughly 48 hours behind, so this is normally two days before today.
        /// </summary>
        [Column("report_refresh_date")]
        public DateTime ReportRefreshDate { get; set; }

        /// <summary>
        /// The day these counts describe. For <see cref="CopilotUserCountReportTypes.Trend"/> this is the
        /// report's "Report Date". For <see cref="CopilotUserCountReportTypes.Summary"/> the report has no
        /// per-day column, so this is the refresh date - the day the period roll-up ends on.
        /// </summary>
        [Column("report_date")]
        public DateTime ReportDate { get; set; }

        /// <summary>See <see cref="CopilotUserCountReportTypes"/>.</summary>
        [Column("report_type")]
        [MaxLength(20)]
        public string ReportType { get; set; }

        /// <summary>
        /// Length of the aggregation window in days (7/28/90/180) for summary rows; NULL for trend rows,
        /// which are daily and therefore period-independent. See <see cref="CopilotUserCountReportTypes.Trend"/>.
        /// </summary>
        [Column("report_period_days")]
        public int? ReportPeriodDays { get; set; }

        /// <summary>
        /// Copilot surface the counts are for, e.g. "Microsoft Teams", "Word", "Copilot Chat (work)", or
        /// <see cref="CopilotAppNames.AnyApp"/> for the tenant-wide roll-up. Unicode-safe: Microsoft has
        /// localised product names in the past.
        /// </summary>
        [Column("app_name")]
        [MaxLength(100)]
        public string AppName { get; set; }

        /// <summary>Licensed users enabled for this app.</summary>
        [Column("enabled_users")]
        public int EnabledUsers { get; set; }

        /// <summary>Enabled users who actually used this app in the window.</summary>
        [Column("active_users")]
        public int ActiveUsers { get; set; }

        /// <summary>
        /// Prompts submitted across the whole tenant (report version 2 only). This is a tenant-level figure,
        /// not a per-app one, so it is only populated on the <see cref="CopilotAppNames.AnyApp"/> row and is
        /// NULL on every other row. Keeping it here avoids a second table for two scalars.
        /// </summary>
        [Column("prompts_submitted")]
        public long? PromptsSubmitted { get; set; }

        /// <summary>
        /// Average prompts submitted per active user (report version 2 summary report only). As with
        /// <see cref="PromptsSubmitted"/>, only populated on the <see cref="CopilotAppNames.AnyApp"/> row.
        /// </summary>
        [Column("average_prompts_submitted")]
        public double? AveragePromptsSubmitted { get; set; }

        public override string ToString()
        {
            return $"{ReportType} {ReportDate:yyyy-MM-dd} {AppName}: {ActiveUsers}/{EnabledUsers} active";
        }
    }

    /// <summary>
    /// Per-user Microsoft 365 Copilot usage from the Graph getMicrosoft365CopilotUsageUserDetail report.
    ///
    /// Follows the existing per-user usage-report convention (<c>*_user_activity_log</c>, one row per user per
    /// report snapshot date, keyed on the inherited <c>date</c> + <c>user_id</c>) so it inherits the same
    /// <c>IX_date</c> index treatment the other usage tables get.
    ///
    /// Licensed users only. Unlicensed Copilot Chat usage never appears here - Microsoft excludes it from the
    /// reports APIs entirely - but it does appear in our audit-log import, so the two together identify
    /// unlicensed users who are actively using Copilot Chat.
    /// </summary>
    [Table("copilot_usage_user_activity_log")]
    public class CopilotUsageUserActivityLog : UserRelatedAbstractUsageActivity
    {
        /// <summary>Length of the aggregation window in days (7/28/90/180) this snapshot was requested for.</summary>
        [Column("report_period_days")]
        public int ReportPeriodDays { get; set; }

        #region Report version 2 counters

        /// <summary>Prompts this user submitted across all Copilot apps. NULL when Graph returned a version 1 report.</summary>
        [Column("prompts_all_apps")]
        public int? PromptsAllApps { get; set; }

        /// <summary>Prompts this user submitted in Copilot Chat (work). NULL on a version 1 report.</summary>
        [Column("prompts_chat_work")]
        public int? PromptsChatWork { get; set; }

        /// <summary>Prompts this user submitted in Copilot Chat (web). NULL on a version 1 report.</summary>
        [Column("prompts_chat_web")]
        public int? PromptsChatWeb { get; set; }

        /// <summary>
        /// Number of days in the window on which this user was active in any Copilot app. NULL on a version 1
        /// report. This is the figure behind a real adoption funnel - "licensed" and "ever active" say nothing
        /// about habit, this does.
        /// </summary>
        [Column("active_usage_days")]
        public int? ActiveUsageDays { get; set; }

        #endregion

        #region Per-app last activity dates

        [Column("chat_last_activity_date")]
        public DateTime? ChatLastActivityDate { get; set; }

        [Column("teams_last_activity_date")]
        public DateTime? TeamsLastActivityDate { get; set; }

        [Column("word_last_activity_date")]
        public DateTime? WordLastActivityDate { get; set; }

        [Column("excel_last_activity_date")]
        public DateTime? ExcelLastActivityDate { get; set; }

        [Column("powerpoint_last_activity_date")]
        public DateTime? PowerPointLastActivityDate { get; set; }

        [Column("outlook_last_activity_date")]
        public DateTime? OutlookLastActivityDate { get; set; }

        [Column("onenote_last_activity_date")]
        public DateTime? OneNoteLastActivityDate { get; set; }

        [Column("loop_last_activity_date")]
        public DateTime? LoopLastActivityDate { get; set; }

        /// <summary>Report version 2 only.</summary>
        [Column("chat_work_last_activity_date")]
        public DateTime? ChatWorkLastActivityDate { get; set; }

        /// <summary>Report version 2 only.</summary>
        [Column("chat_web_last_activity_date")]
        public DateTime? ChatWebLastActivityDate { get; set; }

        /// <summary>Report version 2 only.</summary>
        [Column("m365_copilot_last_activity_date")]
        public DateTime? Microsoft365CopilotLastActivityDate { get; set; }

        /// <summary>Report version 2 only.</summary>
        [Column("edge_last_activity_date")]
        public DateTime? EdgeLastActivityDate { get; set; }

        /// <summary>
        /// Report version 2 only, and the only Copilot agent signal available in any Graph usage report.
        /// </summary>
        [Column("agent_last_activity_date")]
        public DateTime? AgentLastActivityDate { get; set; }

        #endregion

        /// <summary>
        /// True when Graph returned this user's identity as a hash rather than a real UPN, because the tenant
        /// has "concealed user information" (report anonymisation) switched on. We do not import rows in that
        /// state - a hash cannot be joined to a user and would otherwise create a junk user per licensed
        /// account - so in practice this is false on every stored row and exists to make the intent explicit
        /// and to keep the column available if Microsoft ever ships a reversible pseudonym.
        /// See <see cref="CopilotUsageReportImportLog.IsUpnObfuscated"/> for the signal that is actually
        /// recorded and surfaced on the Health page.
        /// </summary>
        [Column("is_upn_obfuscated")]
        public bool IsUpnObfuscated { get; set; }
    }

    /// <summary>
    /// One row per Copilot usage-report import, so the Health page can answer "did this actually work, and can
    /// we trust the per-user numbers?" without scanning the fact tables.
    ///
    /// This exists mainly for <see cref="IsUpnObfuscated"/>: when a tenant enables concealed user information,
    /// the per-user report still returns 200 OK with a full row per licensed user - just with hashed
    /// identities. Without an explicit record, that failure mode is invisible: the import "succeeds" and simply
    /// stores nothing, which is indistinguishable from a tenant with no Copilot usage.
    /// </summary>
    [Table("copilot_usage_report_import_log")]
    public class CopilotUsageReportImportLog : AbstractEFEntity
    {
        /// <summary>Graph report function name, e.g. "getMicrosoft365CopilotUsageUserDetail".</summary>
        [Column("report_name")]
        [MaxLength(100)]
        public string ReportName { get; set; }

        /// <summary>The report's own refresh date, or NULL if Graph returned no rows at all.</summary>
        [Column("report_refresh_date")]
        public DateTime? ReportRefreshDate { get; set; }

        /// <summary>Report version actually requested ("v1" / "v2").</summary>
        [Column("report_version")]
        [MaxLength(10)]
        public string ReportVersion { get; set; }

        /// <summary>Aggregation window requested, e.g. "D28".</summary>
        [Column("report_period")]
        [MaxLength(10)]
        public string ReportPeriod { get; set; }

        [Column("imported_utc")]
        public DateTime ImportedUtc { get; set; }

        /// <summary>Rows parsed out of the CSV Graph returned.</summary>
        [Column("rows_read")]
        public int RowsRead { get; set; }

        /// <summary>Rows actually inserted or updated in SQL.</summary>
        [Column("rows_saved")]
        public int RowsSaved { get; set; }

        /// <summary>
        /// True when the tenant's "concealed user information" setting hashed the user identities in this
        /// report, so per-user rows could not be joined to users and were not imported. The audit-log-based
        /// Copilot import is not affected by that setting, so Copilot reporting still works - it just comes
        /// from the audit source rather than from Microsoft's own usage report.
        /// </summary>
        [Column("is_upn_obfuscated")]
        public bool IsUpnObfuscated { get; set; }

        /// <summary>Populated when the import failed outright, so the Health page can show the reason.</summary>
        [Column("error")]
        [MaxLength(1000)]
        public string Error { get; set; }

        public override string ToString()
        {
            return $"{ReportName} ({ReportVersion}/{ReportPeriod}) @ {ImportedUtc:u}: read {RowsRead}, saved {RowsSaved}";
        }
    }
}
