namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class AddAuditEventsOperationFK : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests can
        /// re-run the script directly to verify idempotency and orphan cleanup.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'AddAuditEventsOperationFK';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.audit_events', N'U') IS NULL
    OR OBJECT_ID(N'dbo.event_operations', N'U') IS NULL
BEGIN
    SET @msg = @migration + N': required table missing, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'operation_id'
)
BEGIN
    SET @msg = @migration + N': audit_events.operation_id does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- Surface row count up front so an operator running this against a customer
-- DB with 100M+ audit_events rows knows the scale of the FK validation that's
-- about to run.
DECLARE @rowCount bigint = (
    SELECT ISNULL(SUM(p.rows), 0)
    FROM sys.partitions p
    WHERE p.object_id = OBJECT_ID(N'dbo.audit_events')
      AND p.index_id IN (0, 1)
);
SET @msg = @migration + N': dbo.audit_events row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N'.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- 1. Bail out if ANY FK from audit_events.operation_id to event_operations is
--    already present. Different install paths used different names (e.g.
--    [FK_events_event_operations] from Create DB.sql), so we check by columns,
--    not by name.
IF EXISTS (
    SELECT 1
    FROM sys.foreign_keys fk
    INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
    INNER JOIN sys.columns pc ON fkc.parent_object_id = pc.object_id AND fkc.parent_column_id = pc.column_id
    INNER JOIN sys.columns rc ON fkc.referenced_object_id = rc.object_id AND fkc.referenced_column_id = rc.column_id
    WHERE fk.parent_object_id = OBJECT_ID(N'dbo.audit_events')
      AND fk.referenced_object_id = OBJECT_ID(N'dbo.event_operations')
      AND pc.name = N'operation_id'
      AND rc.name = N'id'
)
BEGIN
    SET @msg = @migration + N': FK from audit_events.operation_id to event_operations already exists, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- 2. Align column nullability with the entity model. The entity declares
--    int? OperationId, but DBs created by the original Create DB.sql have the
--    column as NOT NULL. ALTER COLUMN to nullable is a metadata-only operation
--    on SQL Server (no table rewrite), so it is fast even on huge tables - but
--    SQL Server refuses ALTER COLUMN while any index references the column, so
--    we drop the supporting index(es) first and recreate the canonical one
--    after the change. This branch is only ever taken on the rare legacy
--    fresh-install DBs that never went through Audit Log Migration.sql.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.audit_events')
      AND name = N'operation_id'
      AND is_nullable = 0
)
BEGIN
    SET @stepStart = SYSUTCDATETIME();
    SET @msg = @migration + N': operation_id is NOT NULL - relaxing to NULL.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'IX_operation_id'
    )
    BEGIN
        RAISERROR('   dropping [IX_operation_id] before ALTER COLUMN...', 0, 1) WITH NOWAIT;
        DROP INDEX [IX_operation_id] ON [dbo].[audit_events];
    END

    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.audit_events') AND name = N'IX_FK_events_event_operations'
    )
    BEGIN
        RAISERROR('   dropping legacy [IX_FK_events_event_operations] before ALTER COLUMN...', 0, 1) WITH NOWAIT;
        DROP INDEX [IX_FK_events_event_operations] ON [dbo].[audit_events];
    END

    ALTER TABLE [dbo].[audit_events] ALTER COLUMN [operation_id] int NULL;

    -- Recreate the supporting index under the canonical name. (Any legacy-named
    -- duplicate has been dropped above, so on these DBs we converge to a single
    -- canonical [IX_operation_id].)
    RAISERROR('   recreating [IX_operation_id] after ALTER COLUMN...', 0, 1) WITH NOWAIT;
    CREATE NONCLUSTERED INDEX [IX_operation_id]
        ON [dbo].[audit_events] ([operation_id]);

    SET @msg = @migration + N': nullability change + index recycle done in '
        + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

-- 3. Surface and NULL-out any orphan operation_id values so the FK validation
--    in step 5 will pass. Audit data is preserved (we keep the row); only the
--    broken reference is cleared. Batched so the transaction log doesn't blow
--    up on huge tables, and so concurrent activity isn't blocked for long.
SET @stepStart = SYSUTCDATETIME();
DECLARE @orphanCount bigint = (
    SELECT COUNT_BIG(*)
    FROM [dbo].[audit_events] ae
    WHERE ae.operation_id IS NOT NULL
      AND NOT EXISTS (
          SELECT 1 FROM [dbo].[event_operations] eo WHERE eo.id = ae.operation_id
      )
);
SET @msg = @migration + N': orphan operation_id rows to clean = ' + CAST(@orphanCount AS nvarchar(20)) + N'.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @rows int = 1;
DECLARE @totalCleaned bigint = 0;
WHILE @rows > 0
BEGIN
    UPDATE TOP (10000) ae
    SET operation_id = NULL
    FROM [dbo].[audit_events] ae
    WHERE ae.operation_id IS NOT NULL
      AND NOT EXISTS (
          SELECT 1 FROM [dbo].[event_operations] eo
          WHERE eo.id = ae.operation_id
      );
    SET @rows = @@ROWCOUNT;
    SET @totalCleaned = @totalCleaned + @rows;
    IF @rows > 0
    BEGIN
        SET @msg = @migration + N':   batch cleared ' + CAST(@rows AS nvarchar(20))
            + N' (total ' + CAST(@totalCleaned AS nvarchar(20)) + N').';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
END
IF @totalCleaned > 0
BEGIN
    SET @msg = @migration + N': orphan cleanup completed in '
        + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

-- 4. Add the FK WITH NOCHECK. This is the SUPPORTED Microsoft pattern for
--    large tables: WITH NOCHECK is metadata-only and instant, so we avoid the
--    long Sch-M lock that WITH CHECK would hold while scanning the table. The
--    constraint at this point is ""not trusted"" - we will fix that immediately
--    in step 5. (Leaving a constraint NOCHECK permanently is what's bad; using
--    NOCHECK transiently before re-validating is the recommended approach.)
SET @stepStart = SYSUTCDATETIME();
RAISERROR('AddAuditEventsOperationFK: adding FK WITH NOCHECK (metadata only)...', 0, 1) WITH NOWAIT;
ALTER TABLE [dbo].[audit_events] WITH NOCHECK
    ADD CONSTRAINT [FK_audit_events_event_operations]
    FOREIGN KEY ([operation_id])
    REFERENCES [dbo].[event_operations] ([id]);
SET @msg = @migration + N': FK added (untrusted) in '
    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- 5. Validate the constraint against existing rows and mark it trusted
--    (sys.foreign_keys.is_not_trusted = 0). After this the query optimiser can
--    use the FK for join elimination and similar optimisations. On very large
--    audit_events tables this is the slowest step - it scans every row and
--    will hold table locks during the scan. Consumer apps should not write to
--    audit_events while this runs; a maintenance window is recommended for
--    tables with hundreds of millions of rows.
SET @stepStart = SYSUTCDATETIME();
RAISERROR('AddAuditEventsOperationFK: validating FK (WITH CHECK CHECK CONSTRAINT). This is the slow step on large tables.', 0, 1) WITH NOWAIT;
ALTER TABLE [dbo].[audit_events] WITH CHECK
    CHECK CONSTRAINT [FK_audit_events_event_operations];
SET @msg = @migration + N': FK validated and now TRUSTED in '
    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Only drops the FK we created; the
        /// nullability change and orphan cleanup are not reversed.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'AddAuditEventsOperationFK (Down)';
DECLARE @msg nvarchar(2000);

IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id = OBJECT_ID(N'dbo.audit_events')
      AND name = N'FK_audit_events_event_operations'
)
BEGIN
    SET @msg = @migration + N': dropping FK_audit_events_event_operations...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    ALTER TABLE [dbo].[audit_events] DROP CONSTRAINT [FK_audit_events_event_operations];
    SET @msg = @migration + N': dropped.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @msg = @migration + N': FK_audit_events_event_operations not present, nothing to drop.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
";

        public override void Up()
        {
            // See Up_Sql for the full rationale. Runs outside the EF migration
            // transaction so the orphan-cleanup batches and the FK validation each
            // commit on their own and don't hold locks for the whole migration.
            //
            // On customer DBs with very large audit_events tables (100M+ rows), step 5
            // (WITH CHECK CHECK CONSTRAINT) is the slowest - it scans every row to
            // validate the FK. The default EF command timeout in Configuration.cs
            // applies; if your DB is exceptionally large you may want to run the
            // SQL manually with a longer timeout instead of relying on auto-apply.
            Console.WriteLine("DB SCHEMA: Applying 'AddAuditEventsOperationFK'. On large audit_events tables this can take minutes to hours due to FK validation; check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'AddAuditEventsOperationFK'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
