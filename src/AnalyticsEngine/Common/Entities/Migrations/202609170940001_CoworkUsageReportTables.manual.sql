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

-- Schema-only gate (never a data-state gate). A severity-16 RAISERROR does NOT abort the batch, so
-- without this IF/ELSE a failed CREATE TABLE / CREATE INDEX above would still fall through to the
-- INSERT and stamp the migration as applied - after which EF never retries it and the partial apply
-- becomes permanent. Structured so the "already applied" case falls through to the stamp check
-- rather than returning early.
IF OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.cowork_usage_user_activity_log', N'U')
                 AND name = N'IX_cowork_usage_user_activity_log_date_user_period')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609170940001_CoworkUsageReportTables')
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609170920001_CopilotSubscribedSkuCapacity')
        BEGIN
            INSERT dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
            SELECT N'202609170940001_CoworkUsageReportTables', ContextKey, Model, ProductVersion
            FROM dbo.__MigrationHistory
            WHERE MigrationId = N'202609170920001_CopilotSubscribedSkuCapacity';
            RAISERROR(N'CoworkUsageReportTables: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN
            RAISERROR(N'202609170940001_CoworkUsageReportTables NOT stamped - predecessor migration 202609170920001_CopilotSubscribedSkuCapacity is missing.', 16, 1) WITH NOWAIT;
        END
    END
    ELSE
    BEGIN
        RAISERROR(N'CoworkUsageReportTables: already recorded in __MigrationHistory, nothing to do.', 0, 1) WITH NOWAIT;
    END
END
ELSE
BEGIN
    RAISERROR(N'202609170940001_CoworkUsageReportTables NOT stamped - required schema objects are missing.', 16, 1) WITH NOWAIT;
END;
