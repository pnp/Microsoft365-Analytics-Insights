-- Manual database upgrade for 202609170940001_CoworkUsageReportTables.
-- Additive schema: creates the empty Cowork usage-report fact table and its functional upsert key.
-- Safe to re-run. The pre-stamp guard checks schema only, never data state.

IF OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U') IS NULL
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
END;

IF OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U') IS NULL
    RAISERROR(N'202609170940001_CoworkUsageReportTables NOT stamped - dbo.cowork_usage_user_activity_log is missing.', 16, 1);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U') AND name = N'IX_cowork_usage_user_activity_log_date_user_period')
    RAISERROR(N'202609170940001_CoworkUsageReportTables NOT stamped - IX_cowork_usage_user_activity_log_date_user_period is missing.', 16, 1);

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609170920001_CopilotSubscribedSkuCapacity')
    RAISERROR(N'202609170940001_CoworkUsageReportTables NOT stamped - predecessor migration 202609170920001_CopilotSubscribedSkuCapacity is missing.', 16, 1);

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609170940001_CoworkUsageReportTables')
BEGIN
    INSERT dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
    SELECT N'202609170940001_CoworkUsageReportTables', ContextKey, Model, ProductVersion
    FROM dbo.__MigrationHistory
    WHERE MigrationId = N'202609170920001_CopilotSubscribedSkuCapacity';
END;
