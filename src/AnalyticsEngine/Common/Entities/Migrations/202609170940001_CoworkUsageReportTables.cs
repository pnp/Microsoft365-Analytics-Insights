namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the Cowork usage-report fact table.
    ///
    /// Classification: additive schema. The table is new and empty at upgrade time; no existing tenant data
    /// is read, rewritten or deleted. The unique index is functional (the importer upsert key), not a
    /// performance-motivated tuning index, so no before/after benchmark applies.
    ///
    /// Text policy: the table intentionally stores no customer free text. User identity remains the existing
    /// FK to dbo.users; the usage report's UPN is resolved during import and is not duplicated here.
    ///
    /// Upgrade cost: metadata-only CREATE TABLE plus an empty unique index and a brief FK schema lock on
    /// dbo.users. It is effectively instant regardless of tenant size.
    /// </summary>
    public partial class CoworkUsageReportTables : DbMigration
    {
        public const string UpSql = @"IF OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U') IS NULL
BEGIN
    RAISERROR(N'Creating dbo.cowork_usage_user_activity_log.', 0, 1) WITH NOWAIT;
    CREATE TABLE dbo.cowork_usage_user_activity_log
    (
        id int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_dbo.cowork_usage_user_activity_log] PRIMARY KEY,
        user_id int NOT NULL,
        [date] datetime NOT NULL,
        report_period_days int NOT NULL,
        total_tasks int NULL,
        scheduled_tasks int NULL,
        user_initiated_tasks int NULL,
        active_days int NULL,
        last_activity_date datetime NULL,
        retained_user bit NULL,
        CONSTRAINT [FK_dbo.cowork_usage_user_activity_log_dbo.users_user_id]
            FOREIGN KEY (user_id) REFERENCES dbo.users(id) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U')
      AND name = N'IX_cowork_usage_user_activity_log_date_user_period')
BEGIN
    RAISERROR(N'Creating IX_cowork_usage_user_activity_log_date_user_period.', 0, 1) WITH NOWAIT;
    CREATE UNIQUE INDEX IX_cowork_usage_user_activity_log_date_user_period
        ON dbo.cowork_usage_user_activity_log([date], user_id, report_period_days);
END;";

        public override void Up()
        {
            Sql(UpSql, suppressTransaction: true);
        }

        public override void Down()
        {
            Sql(@"IF OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.cowork_usage_user_activity_log;
END", suppressTransaction: true);
        }
    }
}
