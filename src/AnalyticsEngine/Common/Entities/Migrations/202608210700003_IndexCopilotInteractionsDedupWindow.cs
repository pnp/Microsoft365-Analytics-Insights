namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds <c>IX_copilot_interactions_dedup_window</c> on
    /// <c>copilot_interactions(session_id, created_utc) INCLUDE (graph_interaction_id)</c>.
    ///
    /// Why: issue #294. The interaction-history import de-duplicates a batch against what is already
    /// stored by loading the existing <c>(session_id, graph_interaction_id)</c> keys for every session the
    /// batch touches (<c>LoadExistingInteractionKeysAsync</c>). That read had no time bound, so it pulled
    /// a thread's ENTIRE history on every cycle even though the incremental window is only a few minutes
    /// of new data - and a persistent BizChat thread never ends, so the cost grew for the life of the
    /// thread.
    ///
    /// The importer now bounds the read to the batch's own time range less a safety margin
    /// (<c>CopilotInteractionHistoryImporter.DedupLookbackMarginDays</c>). This index is what makes the
    /// bounded form seekable: <c>(session_id, created_utc)</c> serves the session predicate and the date
    /// range, and <c>graph_interaction_id</c> is INCLUDEd so the projection needs no key lookup. INCLUDE is
    /// right here - unlike a full-tuple existence check, this query seeks on the key columns and only needs
    /// the extra column returned, which is exactly what INCLUDE is for.
    ///
    /// The pre-existing unique index <c>(session_id, graph_interaction_id)</c> cannot serve it: it has no
    /// <c>created_utc</c>, so bounding by date against it means reading all of a session's rows anyway and
    /// then a lookup per row.
    ///
    /// Measured at synthetic scale (replica: 2,250,000 interactions over 50,000 sessions spanning two
    /// years, including 50 long-lived threads of 5,000 turns each; the query loads one 1,000-session chunk,
    /// exactly as <c>LoadExistingInteractionKeysAsync</c> issues it; medians of 6 runs discarding the cold
    /// run, <c>OPTION (RECOMPILE)</c>, one statement per connection; SQL Server 2025, all data synthetic):
    /// <code>
    ///   Shape                                        Rows loaded   Logical reads   Elapsed   Plan
    ///   BEFORE unbounded, existing unique index          288,000           5,484     45 ms   Index Seek
    ///   bounded, existing unique index only               12,467          15,555    337 ms   Scan + Hash
    ///   AFTER  bounded + this index                       12,467           3,050      6 ms   Index Seek
    ///   control unbounded + this index                   288,000           5,484     47 ms   Index Seek
    /// </code>
    ///
    /// So the fix is 7.5x faster and reads 1.8x fewer pages - but the number that matters most is the
    /// first column: 288,000 rows pulled into the in-memory de-dup HashSet every cycle becomes 12,467, a
    /// 23x reduction in the allocation and GC pressure this step puts on the importer, and one that no
    /// longer grows for the life of a thread.
    ///
    /// The second row is why the code change and this index must ship TOGETHER: bounding the read without
    /// the index is markedly WORSE than not bounding it at all (337 ms against 45 ms), because
    /// <c>created_utc</c> is absent from the existing unique index, so adding the date predicate makes the
    /// optimiser abandon the seek and hash-join a full scan.
    ///
    /// This migration changes only the SQL schema, not the EF entity model snapshot (its <c>.resx</c> Target
    /// is byte-identical to the previous migration's).
    ///
    /// UPGRADE TIME: <c>copilot_interactions</c> is created by this same release, so on an upgrade from the
    /// previous stable build the table is EMPTY and the build is instant. On a database already running the
    /// import from a dev build, the 2,250,000-row synthetic build took 5.8 s and 97 MB, i.e. ~2.6 s and
    /// ~43 MB per million interactions. Attempts <c>ONLINE</c> on capable editions with an offline fallback.
    /// </summary>
    public partial class IndexCopilotInteractionsDedupWindow : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests / the manual upgrade script
        /// can re-run it directly to verify idempotency.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexCopilotInteractionsDedupWindow';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @tbl sysname = N'copilot_interactions';
DECLARE @ix  sysname = N'IX_copilot_interactions_dedup_window';
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit;
DECLARE @objId int;

SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @objId = OBJECT_ID(N'dbo.' + @tbl, N'U');

IF @objId IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'session_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'created_utc')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'graph_interaction_id')
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' is missing an expected column, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- IF/ELSE that falls through rather than an early RETURN, so a by-hand run against an already-indexed
-- database still reaches the __MigrationHistory stamp in the manual script.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = @ix)
BEGIN
    SET @msg = @migration + N': [' + @ix + N'] already exists on ' + @tbl + N', nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = @objId AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20))
        + N' (this table is new in this release, so it is normally empty here and the build is instant).';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(session_id, created_utc) INCLUDE (graph_interaction_id) WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([session_id], [created_utc]) INCLUDE ([graph_interaction_id]) WITH (ONLINE = ON);';
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
        SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(session_id, created_utc) INCLUDE (graph_interaction_id) (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([session_id], [created_utc]) INCLUDE ([graph_interaction_id]);';
        EXEC sp_executesql @sql;
    END

    SET @msg = @migration + N': [' + @ix + N'] created in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20))
        + N'ms (' + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>SQL executed by <see cref="Down"/>. Drops the index if present. Guarded and idempotent.</summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.copilot_interactions', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_interactions') AND name = N'IX_copilot_interactions_dedup_window')
    DROP INDEX [IX_copilot_interactions_dedup_window] ON [dbo].[copilot_interactions];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexCopilotInteractionsDedupWindow'. Adds (session_id, created_utc) INCLUDE (graph_interaction_id) so the interaction-history de-duplication can read a bounded time window instead of a thread's entire history (issue #294). Normally instant - the table is new in this release.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexCopilotInteractionsDedupWindow'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
