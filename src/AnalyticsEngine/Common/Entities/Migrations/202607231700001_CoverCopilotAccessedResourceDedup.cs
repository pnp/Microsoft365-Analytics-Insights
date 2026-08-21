namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds a composite key index on
    /// <c>copilot_event_accessed_resources(copilot_chat_id, resource_id_id, resource_name_id,
    /// resource_site_url_id, resource_type_id, sensitivity_label_id)</c> -&gt;
    /// <c>IX_copilot_event_accessed_resources_dedup</c>.
    ///
    /// SUPERSEDED (comment only - the SQL below is frozen and still correct as the step it was):
    /// <see cref="WidenCopilotAccessedResourceDedupIndex"/> later rebuilds this index with
    /// <c>action_id</c> and <c>list_item_unique_id_id</c> added as key columns, because treating those two
    /// as payload rather than identity dropped distinct actions and could fabricate action / list-item
    /// pairings (issue #287). The "five resolved lookup foreign keys" described below is therefore the
    /// tuple as it stood at THIS migration, not as the merge uses it today.
    ///
    /// Note for anyone upgrading from a build older than this migration: both run, so the index is created
    /// here with six key columns and then immediately dropped and rebuilt with eight. That doubled build is
    /// unavoidable without editing shipped migrations; sites already on current stable only pay for the
    /// rebuild.
    ///
    /// Why: the shared Copilot merge (<c>common_upsert_copilot_agents.sql</c>) de-duplicates the accessed-resource
    /// junction with a keyed anti-join that compares the full resolved tuple for a chat:
    /// <code>
    ///   WHERE NOT EXISTS (SELECT 1 FROM copilot_event_accessed_resources x
    ///                     WHERE x.copilot_chat_id = r.event_id
    ///                       AND EXISTS (SELECT x.resource_id_id, x.resource_name_id, x.resource_site_url_id,
    ///                                          x.resource_type_id, x.sensitivity_label_id
    ///                                   INTERSECT
    ///                                   SELECT r.resource_id_id, ...))
    /// </code>
    /// The table only had single-column indexes (the EF foreign-key index on <c>copilot_chat_id</c> and one per
    /// FK column), none of which lets this existence check seek the whole <c>(chat, tuple)</c>. Without a
    /// seekable composite access path the optimiser falls back to a nested-loop plan that RE-SCANS the
    /// (multi-million-row) junction table for every resolved row - O(resolved x table) - so on a large Copilot
    /// tenant the <c>insert_junction</c> step becomes the dominant Copilot-merge cost (minutes per commit batch),
    /// even when the table is well maintained and no single chat is unusually large (i.e. a plan/index problem,
    /// not fragmentation or data skew). This composite index gives the existence check an exact
    /// <c>(chat_id, tuple)</c> seek. Validated with a synthetic-scale reproduction: on a 500k-row chat the dedup
    /// dropped from 375 ms to 7 ms, and it removes the per-row full scan entirely.
    ///
    /// This migration changes only the SQL schema, not the EF entity model snapshot (its <c>.resx</c> Target is
    /// byte-identical to the previous migration's), so EF sees model == latest snapshot and never raises
    /// <c>AutomaticDataLossException</c>. Same raw-SQL-only precedent as <see cref="IndexSitesSiteId"/> /
    /// <see cref="IndexAuditEventsTimeStamp"/> / <see cref="IndexCopilotAccessedResourceLookups"/>.
    ///
    /// Safety: idempotent and guarded (table + columns + index checked, missing objects skipped, an existing
    /// index is a no-op). Attempts an <c>ONLINE</c> (non-blocking) build on capable editions (Enterprise 3 /
    /// Azure SQL DB 5 / MI 8) and falls back to a normal offline build on any failure (via <c>sp_executesql</c>
    /// inside <c>TRY/CATCH</c>, which makes the "ONLINE not supported" error catchable). Runs outside the EF
    /// transaction (<c>suppressTransaction: true</c>). NOTE: this junction table is large on Copilot-heavy
    /// tenants, so where <c>ONLINE</c> is unavailable the build briefly locks the table - run the upgrade in a
    /// maintenance window with the importer stopped.
    /// </summary>
    public partial class CoverCopilotAccessedResourceDedup : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests / the manual upgrade script
        /// can re-run it directly to verify idempotency and the index-create success path.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'CoverCopilotAccessedResourceDedup';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @tbl sysname = N'copilot_event_accessed_resources';
DECLARE @ix  sysname = N'IX_copilot_event_accessed_resources_dedup';
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit;

-- The full de-dup tuple, in the order the NOT EXISTS / INTERSECT compares it: chat first (the correlation
-- key), then the five resolved lookup foreign keys. A composite KEY index (not INCLUDE) lets the existence
-- check seek the exact (chat_id, tuple) instead of scanning all of a chat's rows.
DECLARE @keyCols nvarchar(400) = N'[copilot_chat_id], [resource_id_id], [resource_name_id], [resource_site_url_id], [resource_type_id], [sensitivity_label_id]';

-- Decide once whether ONLINE index operations are attemptable: Enterprise (3), Azure SQL DB (5) or MI (8).
-- Express/Standard/LocalDB (dev) do NOT support them; the attempt is still wrapped in TRY/CATCH (via
-- sp_executesql, which makes the edition error catchable) so we always fall back to an offline build.
SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline (briefly locks the table; run large upgrades in a maintenance window).' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- The table must have all six index columns (it is created as one unit by migration CopilotExtendedData;
-- guard anyway so a partial/older schema is skipped rather than erroring).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'copilot_chat_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'resource_id_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'resource_name_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'resource_site_url_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'resource_type_id')
    OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = N'sensitivity_label_id')
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' is missing an expected column, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix)
BEGIN
    SET @msg = @migration + N': [' + @ix + N'] already exists on ' + @tbl + N', nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
SET @msg = @migration + N': ' + @tbl + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N' (this can be a large build).';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @stepStart = SYSUTCDATETIME();
SET @onlineDone = 0;

IF @canOnline = 1
BEGIN
    BEGIN TRY
        SET @msg = @migration + N': creating composite index [' + @ix + N'] on ' + @tbl + N'(copilot_chat_id + 5 FK cols) WITH (ONLINE = ON)...';
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
    SET @msg = @migration + N': creating composite index [' + @ix + N'] on ' + @tbl + N'(copilot_chat_id + 5 FK cols) (offline)...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] (' + @keyCols + N');';
    EXEC sp_executesql @sql;
END

SET @msg = @migration + N': [' + @ix + N'] created in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20))
    + N'ms (' + CASE WHEN @onlineDone = 1 THEN N'online' ELSE N'offline' END + N').';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Drops the composite index if present. Idempotent and guarded.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.copilot_event_accessed_resources', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.copilot_event_accessed_resources') AND name = N'IX_copilot_event_accessed_resources_dedup')
    DROP INDEX [IX_copilot_event_accessed_resources_dedup] ON [dbo].[copilot_event_accessed_resources];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CoverCopilotAccessedResourceDedup'. Adds a composite index on copilot_event_accessed_resources(copilot_chat_id + the 5 resolved FK columns) so the Copilot merge's accessed-resource de-duplication (insert_junction) seeks the exact tuple instead of re-scanning the whole junction table per row - turning an O(resolved x table) scan into a seek (minutes to seconds at scale). This can be a large index build on Copilot-heavy tenants; check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CoverCopilotAccessedResourceDedup'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
