namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds <c>IX_copilot_interaction_keywords_keyword_id</c> on
    /// <c>copilot_interaction_keywords(keyword_id, interaction_id)</c>.
    ///
    /// Why: issue #296. The table is created by <see cref="AddCopilotInteractionHistory"/> with a single
    /// index leading on <c>interaction_id</c> (<c>(interaction_id, keyword_id)</c>, unique). Both
    /// orphan-keyword cleanups probe it the other way round:
    /// <code>
    ///   DELETE k FROM keywords k
    ///   WHERE NOT EXISTS (SELECT 1 FROM copilot_interaction_keywords ck WHERE ck.keyword_id = k.id)
    ///     AND NOT EXISTS (SELECT 1 FROM teams_channel_stats_log_keywords tk WHERE tk.keyword_id = k.id);
    /// </code>
    /// (<c>Clean Old Data Data.sql</c> and the per-user purge in <c>Clean Data By User StoredProc.sql</c>.)
    /// <c>keyword_id</c> is not the leading column of any index, so that existence check has no seekable
    /// path and SQL Server scans the whole link index once per candidate keyword. At ten key phrases per
    /// scored prompt, a million prompts produce up to ten million link rows - read on every cleanup run.
    /// The equivalent Teams table already has its own <c>keyword_id</c> index, so only the Copilot link
    /// table was exposed.
    ///
    /// <c>interaction_id</c> is carried as the second key column so the index also covers the join back to
    /// the interaction without a lookup.
    ///
    /// Measured at synthetic scale (replica: 500,000 keywords, 5,000,000 Copilot link rows of which
    /// 100,000 keywords are genuine orphans, plus a Teams link table with its own keyword_id index;
    /// medians of 6 runs discarding the cold run, <c>OPTION (RECOMPILE)</c>, one statement per connection;
    /// SQL Server 2025, all data synthetic):
    /// <code>
    ///   Targeted per-user purge (Clean Data By User StoredProc.sql - ~5,000 candidate phrases)
    ///                                 Logical reads   Elapsed   Plan
    ///   before (no keyword_id index)         11,256    155 ms   Index Scan + Hash Match
    ///   after  (this index)                     137     16 ms   Index Seek
    ///
    ///   Bulk retention sweep (Clean Old Data Data.sql - every keyword is a candidate)
    ///                                 Logical reads   Elapsed   Plan
    ///   before (no keyword_id index)         11,256    797 ms   Index Scan + Hash Match
    ///   after  (this index)                  11,281    437 ms   Index Scan + Hash Match
    /// </code>
    ///
    /// The targeted purge is where this really pays: 82x fewer logical reads and ~10x faster, because the
    /// existence check finally has a seekable path instead of scanning the whole link index once per
    /// candidate. The bulk sweep is honestly a smaller win - when EVERY keyword is a candidate, scanning
    /// the link index once is the right plan either way, so the read count is unchanged (11,281 vs 11,256)
    /// and the ~1.8x time saving comes from the scan arriving already ordered by <c>keyword_id</c>. Both
    /// are improvements and neither regresses, so the index is kept - but the headline number belongs to
    /// the per-user purge, not the nightly sweep.
    ///
    /// This migration changes only the SQL schema, not the EF entity model snapshot (its <c>.resx</c> Target
    /// is byte-identical to the previous migration's).
    ///
    /// UPGRADE TIME: <c>copilot_interaction_keywords</c> is created by this same release, so on an upgrade
    /// from the previous stable build the table is EMPTY and the build is instant. It only takes real time
    /// on a database that has already been running the Copilot interaction-history import from a dev build:
    /// the 5,000,000-row synthetic build took 7.4 s and 87 MB, i.e. ~1.5 s and ~17 MB per million link
    /// rows. Attempts <c>ONLINE</c> on capable editions with an offline fallback.
    /// </summary>
    public partial class IndexCopilotInteractionKeywordsByKeyword : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests / the manual upgrade script
        /// can re-run it directly to verify idempotency.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexCopilotInteractionKeywordsByKeyword';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @tbl sysname = N'copilot_interaction_keywords';
DECLARE @ix  sysname = N'IX_copilot_interaction_keywords_keyword_id';
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

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'keyword_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = @objId AND name = N'interaction_id')
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
            SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(keyword_id, interaction_id) WITH (ONLINE = ON)...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([keyword_id], [interaction_id]) WITH (ONLINE = ON);';
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
        SET @msg = @migration + N': creating [' + @ix + N'] on ' + @tbl + N'(keyword_id, interaction_id) (offline)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([keyword_id], [interaction_id]);';
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
IF OBJECT_ID(N'dbo.copilot_interaction_keywords', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_interaction_keywords') AND name = N'IX_copilot_interaction_keywords_keyword_id')
    DROP INDEX [IX_copilot_interaction_keywords_keyword_id] ON [dbo].[copilot_interaction_keywords];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexCopilotInteractionKeywordsByKeyword'. Adds an index leading on keyword_id so the orphan-keyword cleanup seeks instead of scanning the whole Copilot keyword link table (issue #296). Normally instant - the table is new in this release.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexCopilotInteractionKeywordsByKeyword'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
