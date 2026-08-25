namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    /// <summary>
    /// Adds the three tables behind the Microsoft Graph Microsoft 365 Copilot usage reports:
    ///
    /// <list type="bullet">
    /// <item><c>copilot_user_count_log</c> - tenant enabled-vs-active user counts from
    /// getMicrosoft365CopilotUserCountSummary and getMicrosoft365CopilotUserCountTrend. Narrow/tall
    /// (one row per report type / period / date / app) rather than the ~40-column shape the CSV uses, so a
    /// new Microsoft Copilot surface becomes new rows instead of a schema migration on every customer
    /// database. Edge, Microsoft 365 Copilot, Copilot Chat (work) and Copilot Chat (web) all arrived in a
    /// single report revision, which is exactly the churn this shape avoids.</item>
    /// <item><c>copilot_usage_user_activity_log</c> - per-user detail from
    /// getMicrosoft365CopilotUsageUserDetail, following the existing <c>*_user_activity_log</c> convention
    /// (one row per user per report snapshot date, FK to <c>users</c>), keyed additionally by the report
    /// period because D7 and D28 describe the same user and date with different prompt counts, active-day
    /// counts and last-activity values.</item>
    /// <item><c>copilot_usage_report_import_log</c> - one row per report import, so the Health page can tell
    /// "no Copilot usage" apart from "the tenant conceals user identities, so the per-user report was
    /// deliberately not imported", and so a failed import is visible rather than looking like a recent
    /// healthy one.</item>
    /// </list>
    ///
    /// Indexes: each fact table gets one UNIQUE index, and both are functional rather than
    /// performance-motivated - they are the keys the importer upserts on, which is what stops a re-imported
    /// overlapping window (Graph gap-fills the most recent ~3 days) from duplicating rows.
    /// <c>copilot_usage_user_activity_log.IX_date_user_id_report_period_days</c> leads on <c>date</c>,
    /// matching the leading-date shape the other usage-report tables rely on for date-bounded queries.
    /// No additional covering index is shipped here: per the repo's rule that a performance-motivated index
    /// must arrive with a measured before/after benchmark, the covering treatment
    /// (<see cref="IndexUsageReportSnapshots"/> style) should be added and measured when a report query
    /// against these tables actually exists.
    ///
    /// All text columns are <c>nvarchar</c> (EF's default for <c>c.String</c>), so localised Microsoft app
    /// names and non-Latin display names round-trip intact.
    ///
    /// Upgrade cost: these are three brand-new, empty tables. <c>CREATE TABLE</c> is metadata-only, so the
    /// migration is effectively instant regardless of tenant size and needs no maintenance window - it does
    /// not touch <c>audit_events</c>, <c>hits</c> or any other existing table. The only lock taken is a brief
    /// schema lock on <c>dbo.users</c> to add the foreign key.
    /// </summary>
    public partial class AddCopilotUsageReports : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.copilot_usage_report_import_log",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        report_name = c.String(maxLength: 100),
                        report_refresh_date = c.DateTime(),
                        report_version = c.String(maxLength: 10),
                        report_period = c.String(maxLength: 10),
                        imported_utc = c.DateTime(nullable: false),
                        rows_read = c.Int(nullable: false),
                        rows_saved = c.Int(nullable: false),
                        is_upn_obfuscated = c.Boolean(nullable: false),
                        error = c.String(maxLength: 1000),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_usage_user_activity_log",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        report_period_days = c.Int(nullable: false),
                        prompts_all_apps = c.Int(),
                        prompts_chat_work = c.Int(),
                        prompts_chat_web = c.Int(),
                        active_usage_days = c.Int(),
                        chat_last_activity_date = c.DateTime(),
                        teams_last_activity_date = c.DateTime(),
                        word_last_activity_date = c.DateTime(),
                        excel_last_activity_date = c.DateTime(),
                        powerpoint_last_activity_date = c.DateTime(),
                        outlook_last_activity_date = c.DateTime(),
                        onenote_last_activity_date = c.DateTime(),
                        loop_last_activity_date = c.DateTime(),
                        chat_work_last_activity_date = c.DateTime(),
                        chat_web_last_activity_date = c.DateTime(),
                        m365_copilot_last_activity_date = c.DateTime(),
                        edge_last_activity_date = c.DateTime(),
                        agent_last_activity_date = c.DateTime(),
                        is_upn_obfuscated = c.Boolean(nullable: false),
                        user_id = c.Int(nullable: false),
                        date = c.DateTime(nullable: false),
                        last_activity_date = c.DateTime(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => new { t.date, t.user_id, t.report_period_days }, unique: true);
            
            CreateTable(
                "dbo.copilot_user_count_log",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        report_refresh_date = c.DateTime(nullable: false),
                        report_date = c.DateTime(nullable: false),
                        report_type = c.String(maxLength: 20),
                        report_period_days = c.Int(),
                        app_name = c.String(maxLength: 100),
                        enabled_users = c.Int(nullable: false),
                        active_users = c.Int(nullable: false),
                        prompts_submitted = c.Long(),
                        average_prompts_submitted = c.Double(),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => new { t.report_type, t.report_period_days, t.report_date, t.app_name }, unique: true);
            
        }
        
        public override void Down()
        {
            DropForeignKey("dbo.copilot_usage_user_activity_log", "user_id", "dbo.users");
            DropIndex("dbo.copilot_user_count_log", new[] { "report_type", "report_period_days", "report_date", "app_name" });
            DropIndex("dbo.copilot_usage_user_activity_log", new[] { "date", "user_id", "report_period_days" });
            DropTable("dbo.copilot_user_count_log");
            DropTable("dbo.copilot_usage_user_activity_log");
            DropTable("dbo.copilot_usage_report_import_log");
        }
    }
}
