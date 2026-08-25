namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Widens <c>IX_copilot_event_accessed_resources_dedup</c> from six key columns to eight, adding
    /// <c>action_id</c> and <c>list_item_unique_id_id</c> -&gt;
    /// <c>copilot_event_accessed_resources(copilot_chat_id, resource_id_id, resource_name_id,
    /// resource_site_url_id, resource_type_id, sensitivity_label_id, action_id, list_item_unique_id_id)</c>.
    ///
    /// (Two counts are in play and are easy to conflate: the merge's de-duplication <b>tuple</b> goes from
    /// five resource columns to seven, and the <b>index</b> goes from six key columns to eight, because it
    /// carries <c>copilot_chat_id</c> as its leading correlation key in addition to the tuple.)
    ///
    /// Why: issue #287. The Copilot merge (<c>common_upsert_copilot_agents.sql</c>) resolved accessed
    /// resources by grouping on the five-column resource tuple and then picking the two extra columns with
    /// two INDEPENDENT <c>MIN()</c>s. That was wrong twice over:
    /// <list type="bullet">
    /// <item><description><b>Actions were dropped.</b> The same resource accessed twice in one interaction
    /// with different actions (Read then Write) collapsed to one row keeping the lower id - discarding
    /// exactly what migration <see cref="CopilotDroppedAuditFields"/> added the column to keep.</description></item>
    /// <item><description><b>Pairings could be fabricated.</b> The two <c>MIN()</c>s are evaluated
    /// independently over the group, so the surviving row could pair an <c>action_id</c> from one source
    /// row with a <c>list_item_unique_id_id</c> from another - a combination that never occurred.</description></item>
    /// </list>
    /// The merge now takes the whole seven-column tuple as the row identity, so this index has to carry all
    /// seven of those columns as KEY columns alongside <c>copilot_chat_id</c> - eight in total - for the
    /// de-dup existence check to keep seeking it.
    ///
    /// The previous justification for leaving them out - that <c>list_item_unique_id_id</c> would breach the
    /// 1700-byte index-key limit - was simply incorrect: both columns are <c>int</c> foreign keys, so this
    /// adds 8 bytes to a key of ints.
    ///
    /// Measured at synthetic scale (replica junction table, 2,000,000 rows including one 500,000-row chat;
    /// medians of 6 runs discarding the cold run, <c>OPTION (RECOMPILE)</c>, one statement per connection;
    /// SQL Server 2025, all data synthetic):
    /// <code>
    ///   Commit batch of 500 resolved rows
    ///   Shape                                   Logical reads   Elapsed   Plan
    ///   5-col tuple, 6-key  (before, WRONG)             1,623      2 ms   Index Seek
    ///   7-col tuple, 6-key  (un-widened)                3,123      4 ms   Index Seek
    ///   7-col tuple, 8-key  (THIS CHANGE)               1,622      2 ms   Index Seek
    ///   7-col tuple, 6-key + 2 INCLUDE                  1,622      2 ms   Index Seek
    ///
    ///   Commit batch of 20,000 resolved rows
    ///   Shape                                   Logical reads   Elapsed   Plan
    ///   5-col tuple, 6-key  (before, WRONG)            63,889     85 ms   Index Seek
    ///   7-col tuple, 6-key  (un-widened)               11,569    530 ms   Index Scan + Hash Match
    ///   7-col tuple, 8-key  (THIS CHANGE)              63,883     94 ms   Index Seek
    ///   7-col tuple, 6-key + 2 INCLUDE                 10,904    521 ms   Index Scan + Hash Match
    /// </code>
    ///
    /// Reading that: correcting the de-duplication is essentially FREE once the index is widened - 1,622 vs
    /// 1,623 reads on a small batch, and the same read count with ~10% more time on a large one. Leaving the
    /// index at six keys is what costs: the small batch nearly doubles its reads (3,123 vs 1,622), and the
    /// large batch loses the seek altogether and takes 5.6x longer (530 ms vs 94 ms).
    ///
    /// The <c>INCLUDE</c> variant is the interesting negative result, and the reason wall-clock was measured
    /// alongside reads rather than instead of them. It has the FEWEST logical reads at 20,000 rows (10,904
    /// against 63,883) yet is 5.5x SLOWER (521 ms against 94 ms): the extra columns are only residual
    /// predicates, so the optimiser stops seeking and hash-joins a full index scan instead. Choosing on
    /// logical reads alone would have picked the slower index here.
    ///
    /// This migration changes only the SQL schema, not the EF entity model snapshot (its <c>.resx</c> Target
    /// is byte-identical to the previous migration's), so EF sees model == latest snapshot and never raises
    /// <c>AutomaticDataLossException</c>.
    ///
    /// Safety: idempotent and guarded. Re-running when the index already has the eight key columns is a
    /// no-op; a partially-applied run (index dropped, not yet recreated) converges on re-run. Attempts an
    /// <c>ONLINE</c> build on capable editions (Enterprise 3 / Azure SQL DB 5 / MI 8) and falls back to a
    /// normal offline build on any failure, via <c>sp_executesql</c> inside <c>TRY/CATCH</c> (which is what
    /// makes the "ONLINE not supported" error catchable). Runs outside the EF transaction.
    ///
    /// UPGRADE TIME: this is a drop + rebuild of one non-clustered index. On the synthetic replica the
    /// 2,000,000-row build took 5.9 s and the index occupies 83 MB, i.e. roughly 3 s and ~42 MB per million
    /// rows. Extrapolating: ~3 s at 1M rows, ~30 s at 10M, ~5 min at 100M (plus ~4 GB of index at 100M).
    /// Where <c>ONLINE</c> is unavailable (Standard, Express, LocalDB) the table is locked for the rebuild,
    /// and the de-dup index is ABSENT while it runs - so run this in a maintenance window with the importer
    /// stopped, otherwise a concurrent Copilot merge falls back to the pre-index scan behaviour.
    /// </summary>
    public partial class WidenCopilotAccessedResourceDedupIndex : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests / the manual upgrade script
        /// can re-run it directly to verify idempotency.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'WidenCopilotAccessedResourceDedupIndex';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @tbl sysname = N'copilot_event_accessed_resources';
DECLARE @ix  sysname = N'IX_copilot_event_accessed_resources_dedup';
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit, @needsRebuild bit;
DECLARE @objId int;

-- The full de-dup tuple in the order the NOT EXISTS / INTERSECT compares it: chat first (the correlation
-- key), then the five resolved resource lookups, then the two columns that were wrongly treated as
-- payload. All eight are ints, so the whole key is 32 bytes.
DECLARE @keyCols nvarchar(400) = N'[copilot_chat_id], [resource_id_id], [resource_name_id], [resource_site_url_id], [resource_type_id], [sensitivity_label_id], [action_id], [list_item_unique_id_id]';

SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline (locks the table; run large upgrades in a maintenance window with the importer stopped).' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @objId = OBJECT_ID(N'dbo.' + @tbl, N'U');

IF @objId IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- All eight key columns must be present. action_id / list_item_unique_id_id arrive with migration
-- CopilotDroppedAuditFields; guard so an older/partial schema is skipped rather than erroring.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'copilot_chat_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'resource_id_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'resource_name_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'resource_site_url_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'resource_type_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'sensitivity_label_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'action_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'list_item_unique_id_id')
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' is missing an expected column, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- Already the right shape? Eight KEY columns, including the two being added. Structured as IF/ELSE that
-- falls through rather than an early RETURN, so a by-hand run against an up-to-date database still
-- reaches the __MigrationHistory stamp in the manual script.
SET @needsRebuild = 1;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = @ix)
BEGIN
    IF (SELECT COUNT(*)
        FROM sys.index_columns ic
        INNER JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        WHERE i.object_id = @objId AND i.name = @ix AND ic.is_included_column = 0) = 8
       AND EXISTS (SELECT 1
                   FROM sys.index_columns ic
                   INNER JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                   INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE i.object_id = @objId AND i.name = @ix AND ic.is_included_column = 0 AND c.name = N'action_id')
       AND EXISTS (SELECT 1
                   FROM sys.index_columns ic
                   INNER JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                   INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE i.object_id = @objId AND i.name = @ix AND ic.is_included_column = 0 AND c.name = N'list_item_unique_id_id')
        SET @needsRebuild = 0;
END

IF @needsRebuild = 0
BEGIN
    SET @msg = @migration + N': [' + @ix + N'] already carries all 8 key columns, nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = @objId AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N' (this can be a large rebuild).';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = @objId AND name = @ix)
    BEGIN
        SET @msg = @migration + N': dropping the 6-key [' + @ix + N'] before recreating it with 8 keys...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'DROP INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'];';
        EXEC sp_executesql @sql;
    END

    SET @stepStart = SYSUTCDATETIME();
    SET @onlineDone = 0;

    IF @canOnline = 1
    BEGIN
        BEGIN TRY
            SET @msg = @migration + N': creating composite index [' + @ix + N'] on ' + @tbl + N'(chat + 5 resource FKs + action_id + list_item_unique_id_id) WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] (' + @keyCols + N') WITH (ONLINE = ON);';
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
        SET @msg = @migration + N': creating composite index [' + @ix + N'] on ' + @tbl + N'(chat + 5 resource FKs + action_id + list_item_unique_id_id) (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] (' + @keyCols + N');';
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
        /// SQL executed by <see cref="Down"/>. Puts the index back to its previous six-key shape. Guarded
        /// and idempotent. NOTE: reverting the index alone leaves the merge SQL de-duplicating on the full
        /// tuple against a narrower index - correct, but slower - so revert the code with it.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.copilot_event_accessed_resources', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_event_accessed_resources') AND name = N'IX_copilot_event_accessed_resources_dedup')
        DROP INDEX [IX_copilot_event_accessed_resources_dedup] ON [dbo].[copilot_event_accessed_resources];

    CREATE NONCLUSTERED INDEX [IX_copilot_event_accessed_resources_dedup]
        ON [dbo].[copilot_event_accessed_resources]
        ([copilot_chat_id], [resource_id_id], [resource_name_id], [resource_site_url_id], [resource_type_id], [sensitivity_label_id]);
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'WidenCopilotAccessedResourceDedupIndex'. Rebuilds IX_copilot_event_accessed_resources_dedup with action_id and list_item_unique_id_id as key columns, so the Copilot merge can de-duplicate accessed resources on the FULL tuple (issue #287) and still seek. This is an index rebuild on a table that is large on Copilot-heavy tenants; check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'WidenCopilotAccessedResourceDedupIndex'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
