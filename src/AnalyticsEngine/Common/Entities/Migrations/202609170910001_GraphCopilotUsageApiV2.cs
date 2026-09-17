namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Additive Copilot usage-report provenance fields for issue #541. The importer now reads the GA
    /// Graph v1.0 /copilot report stream and stores the report schema version with each per-user snapshot.
    ///
    /// Migration classification: additive. The column is nullable metadata on an existing report table. No
    /// existing rows are rewritten, no large fact-table index is built, and no performance claim is made.
    /// Upgrade cost is metadata-only and effectively instant regardless of tenant size.
    /// </summary>
    public partial class GraphCopilotUsageApiV2 : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.copilot_usage_user_activity_log", "report_version", c => c.String(maxLength: 10));
        }

        public override void Down()
        {
            DropColumn("dbo.copilot_usage_user_activity_log", "report_version");
        }
    }
}
