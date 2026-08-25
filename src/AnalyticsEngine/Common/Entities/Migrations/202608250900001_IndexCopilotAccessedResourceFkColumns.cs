namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Builds the two foreign-key indexes on <c>dbo.copilot_event_accessed_resources</c> for the
    /// <c>action_id</c> and <c>list_item_unique_id_id</c> columns added by
    /// <see cref="CopilotDroppedAuditFields"/>.
    ///
    /// WHY THIS IS ITS OWN MIGRATION
    ///   These indexes were originally the last step of <see cref="CopilotDroppedAuditFields"/>, issued as
    ///   <c>Sql(..., suppressTransaction: true)</c> because on a Copilot-heavy tenant this junction table is
    ///   the largest in the schema and the build must not be held inside the migration transaction.
    ///
    ///   That made the whole migration non-resumable. EF commits every operation preceding a
    ///   transaction-suppressed statement, so an index build that failed - out of disk, log full, cancelled
    ///   session, killed connection - left five tables, nine columns and six foreign keys COMMITTED while
    ///   the migration itself was never stamped in <c>__MigrationHistory</c>. The retry then re-ran the
    ///   unconditional <c>CreateTable</c> operations and failed immediately on objects that already
    ///   existed, so the upgrade could never converge without hand repair.
    ///
    ///   Splitting the two apart fixes that without renumbering anything already applied:
    ///     * CopilotDroppedAuditFields is now a single atomic transaction - it either applies and stamps,
    ///       or rolls back completely.
    ///     * This migration contains ONLY the guarded, idempotent index SQL, so if it is interrupted a
    ///       re-run simply builds whatever is still missing and stamps.
    ///
    /// NOT PERFORMANCE-MOTIVATED, so no before/after benchmark is required by the schema-change policy.
    /// These follow the table's existing convention of one index per foreign-key column; the FK
    /// constraints themselves are created by the predecessor and do not depend on these indexes.
    ///
    /// RUNTIME
    ///   Measured at synthetic scale (offline build, buffer pool dropped before each build, medians of 3
    ///   runs) on a 3,000,000-row junction table: about 2.5 s / 40.7 MB for the action_id index and
    ///   3.1 s / 40.7 MB for list_item_unique_id_id - roughly 5.6 s and 81 MB for the pair. Extrapolating
    ///   as O(n log n) that is about 2 s / 27 MB at 1M rows and a few minutes at 100M. ONLINE is attempted
    ///   on Enterprise / Azure SQL DB / Azure SQL MI; on other editions each build briefly locks the table,
    ///   so run large upgrades in a maintenance window with the importer stopped.
    ///
    /// The EF entity model is unchanged by this migration - indexes on existing columns are physical only -
    /// so its .resx snapshot is a byte-identical copy of its predecessor's
    /// (202608210700003_IndexCopilotInteractionsDedupWindow), per the repo's migration rules. The manual
    /// upgrade script therefore stamps __MigrationHistory by copying the predecessor's row.
    /// </summary>
    public partial class IndexCopilotAccessedResourceFkColumns : DbMigration
    {
        /// <summary>
        /// Builds the two foreign-key indexes on the (potentially huge) accessed-resource junction table.
        /// Exposed as a constant so the manual upgrade script and unit tests use the exact same SQL.
        /// Idempotent, guarded, edition-aware (ONLINE attempt via sp_executesql inside TRY/CATCH - which is
        /// what makes the "ONLINE is Enterprise only" error catchable - with an offline fallback).
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexCopilotAccessedResourceFkColumns';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @tbl sysname = N'copilot_event_accessed_resources';
DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @canOnline bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8) THEN 1 ELSE 0 END;
DECLARE @onlineDone bit;
DECLARE @rowCount bigint;
DECLARE @sql nvarchar(max);
DECLARE @ix sysname;
DECLARE @col sysname;
DECLARE @i int = 1;

DECLARE @targets table (seq int NOT NULL PRIMARY KEY, ix sysname NOT NULL, col sysname NOT NULL);
INSERT INTO @targets (seq, ix, col) VALUES
    (1, N'IX_copilot_event_accessed_resources_action_id', N'action_id'),
    (2, N'IX_copilot_event_accessed_resources_list_item_unique_id_id', N'list_item_unique_id_id');

SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index builds '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).'
           ELSE N'are not supported on this edition - each build briefly locks the table, so run large upgrades in a maintenance window with the importer stopped.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist; skipping the junction indexes.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p
                     WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N'.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    WHILE @i <= 2
    BEGIN
        SELECT @ix = ix, @col = col FROM @targets WHERE seq = @i;

        IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @col)
        BEGIN
            SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' does not exist; skipping [' + @ix + N'].';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix)
        BEGIN
            SET @msg = @migration + N': [' + @ix + N'] already exists; nothing to do.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN
            SET @stepStart = SYSUTCDATETIME();
            SET @onlineDone = 0;

            IF @canOnline = 1
            BEGIN
                BEGIN TRY
                    SET @msg = @migration + N': creating [' + @ix + N'] WITH (ONLINE = ON)...';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']) WITH (ONLINE = ON);';
                    EXEC sp_executesql @sql;
                    SET @onlineDone = 1;
                END TRY
                BEGIN CATCH
                    SET @msg = @migration + N': ONLINE build of [' + @ix + N'] unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
                    RAISERROR(@msg, 0, 1) WITH NOWAIT;
                END CATCH
            END

            IF @onlineDone = 0
            BEGIN
                SET @msg = @migration + N': creating [' + @ix + N'] (offline)...';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
                SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']);';
                EXEC sp_executesql @sql;
            END

            SET @msg = @migration + N': [' + @ix + N'] created in '
                + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms ('
                + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END

        SET @i += 1;
    END
END

SET @msg = @migration + N': junction indexes finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/> for the junction indexes. Guarded and idempotent.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.copilot_event_accessed_resources', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_event_accessed_resources') AND name = N'IX_copilot_event_accessed_resources_action_id')
        DROP INDEX [IX_copilot_event_accessed_resources_action_id] ON [dbo].[copilot_event_accessed_resources];
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_event_accessed_resources') AND name = N'IX_copilot_event_accessed_resources_list_item_unique_id_id')
        DROP INDEX [IX_copilot_event_accessed_resources_list_item_unique_id_id] ON [dbo].[copilot_event_accessed_resources];
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexCopilotAccessedResourceFkColumns'. Builds the two foreign-key indexes on copilot_event_accessed_resources (action_id, list_item_unique_id_id). On Copilot-heavy tenants this is the largest table in the schema, so the build runs outside the migration transaction - ONLINE where the edition supports it, offline otherwise. Check the SQL session for live progress (RAISERROR ... WITH NOWAIT). Guarded and idempotent: an interrupted run converges on re-run.");

            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexCopilotAccessedResourceFkColumns'.");

            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
