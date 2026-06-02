namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class AddAuditEventsOperationIndex : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests can
        /// re-run the script directly against a test database to verify the defensive
        /// guards (idempotency, legacy index detection, missing table/column).
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'AddAuditEventsOperationIndex';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.audit_events', N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.audit_events does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.audit_events')
      AND name = N'operation_id'
)
BEGIN
    SET @msg = @migration + N': dbo.audit_events.operation_id does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- Log table size so an operator running this against a large customer DB can
-- see the scale before the (potentially long) index build starts.
DECLARE @rowCount bigint = (
    SELECT ISNULL(SUM(p.rows), 0)
    FROM sys.partitions p
    WHERE p.object_id = OBJECT_ID(N'dbo.audit_events')
      AND p.index_id IN (0, 1)
);
SET @msg = @migration + N': dbo.audit_events row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N'.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.audit_events')
      AND name IN (N'IX_operation_id', N'IX_FK_events_event_operations')
)
BEGIN
    SET @msg = @migration + N': supporting index for operation_id already exists, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @sql nvarchar(max) = N'CREATE NONCLUSTERED INDEX [IX_operation_id] ON [dbo].[audit_events] ([operation_id])';

IF @canOnline = 1
    SET @sql = @sql + N' WITH (ONLINE = ON)';

SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10))
    + N', ONLINE=' + CAST(@canOnline AS nvarchar(1))
    + N'. Creating [IX_operation_id]...';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @indexStart datetime2(3) = SYSUTCDATETIME();
EXEC sp_executesql @sql;

SET @msg = @migration + N': [IX_operation_id] created in '
    + CAST(DATEDIFF(MILLISECOND, @indexStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. See <see cref="Up_Sql"/> for rationale.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'AddAuditEventsOperationIndex (Down)';
DECLARE @msg nvarchar(2000);

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.audit_events')
      AND name = N'IX_operation_id'
)
BEGIN
    DECLARE @canOnline bit = CASE
        WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN 1
        ELSE 0
    END;
    DECLARE @sql nvarchar(max) = N'DROP INDEX [IX_operation_id] ON [dbo].[audit_events]';
    IF @canOnline = 1
        SET @sql = @sql + N' WITH (ONLINE = ON)';

    SET @msg = @migration + N': dropping [IX_operation_id]...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    EXEC sp_executesql @sql;
    SET @msg = @migration + N': dropped.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @msg = @migration + N': [IX_operation_id] not present, nothing to drop.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
";

        public override void Up()
        {
            // Some databases (those upgraded via the old "Audit Log Migration.sql" path that
            // rebuilt dbo.audit_events from audit_events_new) are missing the supporting
            // non-clustered index on audit_events.operation_id that backs the FK to
            // dbo.event_operations(id). Fresh installs created by the v1 migration
            // (Create DB.sql) already have it as [IX_FK_events_event_operations], so we
            // accept either name when deciding whether the index already exists.
            //
            // This script is defensive because it runs against a large variety of customer
            // databases, some with very large audit_events tables (100M+ rows):
            //   * Skips silently if dbo.audit_events or the operation_id column don't exist
            //     (e.g. pre-v1 or hand-modified schemas).
            //   * Uses ONLINE = ON on editions that support it (Enterprise, Azure SQL DB,
            //     Azure SQL MI) so existing readers/writers aren't blocked while the index
            //     is built. Falls back to an offline build on Standard / Web / Express.
            //   * Runs outside the EF migration transaction (suppressTransaction: true) so
            //     the schema-modification lock is released as soon as the index build
            //     completes, rather than being held until the migration commits.
            //   * Logs row counts, edition, online vs offline choice, and timing via
            //     RAISERROR ... WITH NOWAIT so operators can see real-time progress.
            Console.WriteLine("DB SCHEMA: Applying 'AddAuditEventsOperationIndex'. On large audit_events tables this may take a while; check the SQL session for live progress.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'AddAuditEventsOperationIndex'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}


