namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Narrows the value columns of the Copilot accessed-resource lookup tables from
    /// <c>nvarchar(max)</c> to <c>nvarchar(850)</c> and adds supporting non-clustered indexes:
    ///   * <c>copilot_event_accessed_resource_ids.resource_id</c>
    ///   * <c>copilot_event_accessed_resource_names.[name]</c>
    ///   * <c>copilot_event_accessed_resource_site_urls.site_url</c>
    ///
    /// Why: these columns are the join / de-duplication keys used by the Copilot staging merge
    /// (<c>common_upsert_copilot_agents.sql</c>): every batch resolves each accessed resource by
    /// <c>LEFT JOIN ... ON lookup.value = parsed.value</c> and inserts new distinct values via
    /// <c>NOT EXISTS</c>. As <c>nvarchar(max)</c> (LOB) columns they cannot be B-tree index keys,
    /// so those joins/NOT EXISTS were forced into full scans of the two largest, ever-growing
    /// lookup tables (resource_ids / names) on every import batch. On a large tenant (hundreds of
    /// thousands of Copilot events, each grounding on many resources) these tables reach millions
    /// of rows and the per-batch scan cost dominates import time - one large customer's audit-log
    /// import was projected to take several days, the large majority of it in this merge. Profiling
    /// (synthetic data) showed the resolve join alone at ~2.1s/200-event batch; indexing dropped it ~12x.
    ///
    /// 850 nvarchar chars = 1700 bytes = the SQL Server single-column non-clustered index-key
    /// limit, and the columns MUST stay <c>nvarchar</c> (not <c>varchar</c>) so non-Latin
    /// SharePoint values (e.g. Greek file names / URLs) are not corrupted to '?'. Same rationale
    /// and precedent as <see cref="ShrinkUrlsFullUrlColumn"/> (dbo.urls.full_url, issue #122).
    /// The Copilot merge trims each parsed value with <c>LEFT(..., 850)</c> before staging, so no
    /// over-width value is ever inserted after this migration.
    ///
    /// Like <see cref="ShrinkUrlsFullUrlColumn"/>, this migration changes only the SQL schema, not
    /// the EF entity model snapshot (its <c>.resx</c> Target is identical to the previous
    /// migration's). The EF model still maps these columns as <c>nvarchar(max)</c>; that is
    /// harmless because they are written exclusively by the raw merge SQL (which trims to 850) and
    /// read back as strings. EF compares the model to the migration snapshot (unchanged), never to
    /// the live column type, so no <c>AutomaticDataLossException</c> is raised.
    ///
    /// Safety: runs against a wide range of customer databases so it is defensive and idempotent -
    /// each (table, column, index) is independently guarded, skips missing objects, is a no-op when
    /// already <c>nvarchar(850)</c> with the index present, truncates any (extremely rare)
    /// over-width value to 850 before the ALTER (a truncated value still de-duplicates to the same
    /// key, so we trim rather than abort the customer's upgrade), and builds the index <c>ONLINE</c>
    /// on editions that support it. Runs outside the EF transaction (suppressTransaction: true) and
    /// Configuration.cs sets CommandTimeout = 0 so a large table's ALTER/index build won't time out.
    /// </summary>
    public partial class IndexCopilotAccessedResourceLookups : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests can re-run it
        /// directly to verify idempotency and the narrow+index success path.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexCopilotAccessedResourceLookups';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @maxLen int = 850;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @targets TABLE (seq int IDENTITY(1,1), tbl sysname, col sysname, ix sysname);
INSERT @targets (tbl, col, ix) VALUES
 (N'copilot_event_accessed_resource_ids',       N'resource_id', N'IX_copilot_event_accessed_resource_ids_resource_id'),
 (N'copilot_event_accessed_resource_names',     N'name',        N'IX_copilot_event_accessed_resource_names_name'),
 (N'copilot_event_accessed_resource_site_urls', N'site_url',    N'IX_copilot_event_accessed_resource_site_urls_site_url');

DECLARE @seq int = 1;
DECLARE @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @col sysname, @ix sysname;
DECLARE @typeName sysname, @maxLength smallint;
DECLARE @alreadyNarrow bit, @indexExists bit;
DECLARE @rowCount bigint, @trunc int, @edition int;
DECLARE @sql nvarchar(max), @online nvarchar(40);

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

    SELECT @typeName = t.name, @maxLength = c.max_length
    FROM sys.columns c INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.' + @tbl) AND c.name = @col;

    -- nvarchar(850) stores 850 chars in 1700 bytes, so sys.columns.max_length = 1700 when already narrowed.
    SET @alreadyNarrow = CASE WHEN @typeName = N'nvarchar' AND @maxLength = @maxLen * 2 THEN 1 ELSE 0 END;
    SET @indexExists = CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix) THEN 1 ELSE 0 END;

    IF @alreadyNarrow = 1 AND @indexExists = 1
    BEGIN
        SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' is already nvarchar(' + CAST(@maxLen AS nvarchar(10)) + N') and [' + @ix + N'] exists, nothing to do.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' row estimate = ' + CAST(@rowCount AS nvarchar(20))
        + N' (current type ' + @typeName + CASE WHEN @maxLength = -1 THEN N'(max)' ELSE N'(' + CAST(@maxLength AS nvarchar(10)) + N')' END + N').';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    -- Trim any over-width value BEFORE the shrink (rare; SharePoint resource ids / file names / site URLs are
    -- realistically < 850). The merge applies the same LEFT(...,850) going forward, so a trimmed value still
    -- de-duplicates to the same key. We trim rather than abort so a pathological value can't block the upgrade.
    IF @alreadyNarrow = 0
    BEGIN
        SET @sql = N'UPDATE dbo.[' + @tbl + N'] SET [' + @col + N'] = LEFT([' + @col + N'], ' + CAST(@maxLen AS nvarchar(10))
            + N') WHERE [' + @col + N'] IS NOT NULL AND LEN([' + @col + N']) > ' + CAST(@maxLen AS nvarchar(10)) + N';';
        EXEC sp_executesql @sql;
        SET @trunc = @@ROWCOUNT;
        IF @trunc > 0
        BEGIN
            SET @msg = @migration + N': trimmed ' + CAST(@trunc AS nvarchar(20)) + N' over-width value(s) in ' + @tbl + N'.' + @col + N' to ' + CAST(@maxLen AS nvarchar(10)) + N' chars.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
    END

    -- SQL Server blocks ALTER COLUMN while an index references the column, so drop ours first if present.
    IF @indexExists = 1
    BEGIN
        EXEC(N'DROP INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'];');
        SET @indexExists = 0;
    END

    IF @alreadyNarrow = 0
    BEGIN
        SET @stepStart = SYSUTCDATETIME();
        SET @msg = @migration + N': altering ' + @tbl + N'.' + @col + N' to nvarchar(' + CAST(@maxLen AS nvarchar(10)) + N') NULL...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @sql = N'ALTER TABLE [dbo].[' + @tbl + N'] ALTER COLUMN [' + @col + N'] nvarchar(850) NULL;';
        EXEC sp_executesql @sql;
        SET @msg = @migration + N': ALTER COLUMN completed in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    -- Create the supporting index. ONLINE = ON on editions that support it (Enterprise=3, Azure SQL DB=5,
    -- Azure SQL MI=8) so existing readers/writers aren't blocked; offline elsewhere.
    SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
    SET @online = CASE WHEN @edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;
    SET @stepStart = SYSUTCDATETIME();
    SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'. Creating [' + @ix + N']...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @sql = N'CREATE NONCLUSTERED INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'] ([' + @col + N'])' + @online + N';';
    EXEC sp_executesql @sql;
    SET @msg = @migration + N': [' + @ix + N'] created in ' + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Drops the indexes and widens the columns back to
        /// <c>nvarchar(max)</c>. Idempotent and guarded.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexCopilotAccessedResourceLookups (Down)';
DECLARE @msg nvarchar(2000);

DECLARE @targets TABLE (seq int IDENTITY(1,1), tbl sysname, col sysname, ix sysname);
INSERT @targets (tbl, col, ix) VALUES
 (N'copilot_event_accessed_resource_ids',       N'resource_id', N'IX_copilot_event_accessed_resource_ids_resource_id'),
 (N'copilot_event_accessed_resource_names',     N'name',        N'IX_copilot_event_accessed_resource_names_name'),
 (N'copilot_event_accessed_resource_site_urls', N'site_url',    N'IX_copilot_event_accessed_resource_site_urls_site_url');

DECLARE @seq int = 1;
DECLARE @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @col sysname, @ix sysname, @sql nvarchar(max);

WHILE @seq <= @maxSeq
BEGIN
    SELECT @tbl = tbl, @col = col, @ix = ix FROM @targets WHERE seq = @seq;
    SET @seq = @seq + 1;

    IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL CONTINUE;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix)
        EXEC(N'DROP INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'];');

    IF EXISTS (SELECT 1 FROM sys.columns c INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
               WHERE c.object_id = OBJECT_ID(N'dbo.' + @tbl) AND c.name = @col AND NOT (t.name = N'nvarchar' AND c.max_length = -1))
    BEGIN
        SET @sql = N'ALTER TABLE [dbo].[' + @tbl + N'] ALTER COLUMN [' + @col + N'] nvarchar(max) NULL;';
        EXEC sp_executesql @sql;
    END
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexCopilotAccessedResourceLookups'. Narrows the Copilot accessed-resource lookup value columns (resource_id / name / site_url) to nvarchar(850) (Unicode-safe) and indexes them so the Copilot import merge seeks instead of full-scanning these ever-growing tables. On large tables the ALTER COLUMN / index build can take a while; check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexCopilotAccessedResourceLookups'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
