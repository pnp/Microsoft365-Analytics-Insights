namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds non-clustered indexes on the two datetime columns the O365 audit-log importer filters on every
    /// batch:
    ///   * <c>audit_events.time_stamp</c>            -> <c>IX_audit_events_time_stamp</c>
    ///   * <c>ignored_audit_events.processed_timestamp</c> -> <c>IX_ignored_audit_events_processed_timestamp</c>
    ///
    /// Why: <see cref="WebJob.Office365ActivityImporter.Engine.ActivityImportCache"/> (via
    /// <c>GetAndBuildNewCache</c>) rebuilds a per-batch "already processed" cache by querying
    /// <c>WHERE time_stamp BETWEEN @oldest AND @newest</c> on <c>audit_events</c> (and the equivalent on
    /// <c>ignored_audit_events.processed_timestamp</c>) for EVERY commit batch. <c>audit_events</c> only had a
    /// clustered PK on <c>id</c> (a GUID) plus FK/operation indexes - nothing on <c>time_stamp</c> - so each
    /// batch's window query full-scanned the entire, ever-growing table. On a mature tenant (tens of millions
    /// of audit events) that is tens of full scans per import cycle. A synthetic load test (2,000,000 rows,
    /// 50 commit batches) measured the WARM re-run - which imports nothing and is therefore almost pure
    /// window-scan - at ~22.6s; the index turns each scan into a range seek so that collapses to ~1-2s, and it
    /// removes the same scan overhead from the COLD path.
    ///
    /// This migration changes only the SQL schema, not the EF entity model snapshot (its <c>.resx</c> Target
    /// is byte-identical to the previous migration's), so EF sees model == latest snapshot and never raises
    /// <c>AutomaticDataLossException</c>. Same raw-SQL-only precedent as
    /// <see cref="IndexCopilotAccessedResourceLookups"/> / <see cref="ShrinkUrlsFullUrlColumn"/>.
    ///
    /// Safety: idempotent and guarded - each (table, column, index) is independently checked, missing objects
    /// are skipped, and an already-present index is a no-op. The index build attempts <c>ONLINE</c>
    /// (non-blocking) on capable editions (Enterprise 3 / Azure SQL DB 5 / MI 8) and falls back to a normal
    /// offline build on any failure (via <c>sp_executesql</c> inside <c>TRY/CATCH</c>, which makes the
    /// "ONLINE not supported" error catchable) so it also completes on Express / Standard / LocalDB (dev),
    /// where an offline index build briefly locks the table (run large upgrades in a maintenance window with
    /// the importer stopped). Runs outside the EF transaction (<c>suppressTransaction: true</c>) and
    /// <c>Configuration.cs</c> sets <c>CommandTimeout = 0</c> so a large table's index build won't time out;
    /// an interrupted upgrade converges cleanly on re-run.
    /// </summary>
    public partial class IndexAuditEventsTimeStamp : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests / the manual upgrade script
        /// can re-run it directly to verify idempotency and the index-create success path.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexAuditEventsTimeStamp';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @targets TABLE (seq int IDENTITY(1,1), tbl sysname, col sysname, ix sysname);
INSERT @targets (tbl, col, ix) VALUES
 (N'audit_events',         N'time_stamp',          N'IX_audit_events_time_stamp'),
 (N'ignored_audit_events', N'processed_timestamp', N'IX_ignored_audit_events_processed_timestamp');

DECLARE @seq int = 1;
DECLARE @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @col sysname, @ix sysname;
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit, @indexExists bit;

-- Decide once whether ONLINE index operations are attemptable: Enterprise (3), Azure SQL DB (5) or MI (8).
-- Express/Standard/LocalDB (dev) do NOT support them; each attempt is still wrapped in TRY/CATCH (via
-- sp_executesql, which makes the edition error catchable) so we always fall back to an offline build.
SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline (briefly locks the table; run large upgrades in a maintenance window).' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

WHILE @seq <= @maxSeq
BEGIN
    SELECT @tbl = tbl, @col = col, @ix = ix FROM @targets WHERE seq = @seq;
    SET @seq = @seq + 1;

    IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @col)
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N'.' + @col + N' does not exist, skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @indexExists = CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix) THEN 1 ELSE 0 END;
    IF @indexExists = 1
    BEGIN
        SET @msg = @migration + N': [' + @ix + N'] already exists on ' + @tbl + N', nothing to do.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N'.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    -- Attempt a non-blocking ONLINE build first on capable editions; on any failure fall back to offline
    -- below (which also re-surfaces a genuine, non-ONLINE error instead of masking it).
    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(' + @col + N') WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']) WITH (ONLINE = ON);';
            EXEC sp_executesql @sql;
            SET @onlineDone = 1;
        END TRY
        BEGIN CATCH
            SET @msg = @migration + N': ONLINE index build of [' + @ix + N'] unavailable (' + ERROR_MESSAGE() + N'); retrying offline.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END

    IF @onlineDone = 0
    BEGIN
        SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(' + @col + N') (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N']);';
        EXEC sp_executesql @sql;
    END

    SET @msg = @migration + N': [' + @ix + N'] created in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20))
        + N'ms (' + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Drops the two indexes if present. Idempotent and guarded.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;

DECLARE @targets TABLE (seq int IDENTITY(1,1), tbl sysname, ix sysname);
INSERT @targets (tbl, ix) VALUES
 (N'audit_events',         N'IX_audit_events_time_stamp'),
 (N'ignored_audit_events', N'IX_ignored_audit_events_processed_timestamp');

DECLARE @seq int = 1;
DECLARE @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @ix sysname;

WHILE @seq <= @maxSeq
BEGIN
    SELECT @tbl = tbl, @ix = ix FROM @targets WHERE seq = @seq;
    SET @seq = @seq + 1;

    IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL CONTINUE;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix)
        EXEC(N'DROP INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'];');
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexAuditEventsTimeStamp'. Indexes audit_events.time_stamp and ignored_audit_events.processed_timestamp so the O365 audit importer's per-batch 'already processed' window query seeks instead of full-scanning these ever-growing tables. On large tables the index build can take a while; check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexAuditEventsTimeStamp'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
