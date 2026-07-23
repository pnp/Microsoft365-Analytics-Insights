namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds a non-clustered index on <c>sites.site_id</c> -> <c>IX_sites_site_id</c>.
    ///
    /// Why: the importer resolves a SharePoint site by its <c>site_id</c> on the save path
    /// (<c>WHERE site_id = @p</c>). <c>sites</c> only had a clustered PK on the identity <c>id</c> and a
    /// unique index on <c>url_base</c> - nothing on <c>site_id</c> - so every lookup scanned the table. On a
    /// mature tenant this was the top missing-index recommendation (hundreds of thousands of executions, each
    /// a full scan of the sites table). The index turns those scans into seeks. (The EF context also now sets
    /// <c>UseDatabaseNullSemantics</c> so the predicate is SARGable, which is what lets this index be used.)
    ///
    /// This migration changes only the SQL schema, not the EF entity model snapshot (its <c>.resx</c> Target
    /// is byte-identical to the previous migration's), so EF sees model == latest snapshot and never raises
    /// <c>AutomaticDataLossException</c>. Same raw-SQL-only precedent as
    /// <see cref="IndexAuditEventsTimeStamp"/> / <see cref="IndexCopilotAccessedResourceLookups"/>.
    ///
    /// Safety: idempotent and guarded - the (table, column, index) is checked, missing objects are skipped,
    /// and an already-present index is a no-op. The build attempts <c>ONLINE</c> (non-blocking) on capable
    /// editions (Enterprise 3 / Azure SQL DB 5 / MI 8) and falls back to a normal offline build on any failure
    /// (via <c>sp_executesql</c> inside <c>TRY/CATCH</c>, which makes the "ONLINE not supported" error
    /// catchable). <c>sites</c> is small, so the build is quick either way. Runs outside the EF transaction
    /// (<c>suppressTransaction: true</c>); an interrupted upgrade converges cleanly on re-run.
    /// </summary>
    public partial class IndexSitesSiteId : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests / the manual upgrade script
        /// can re-run it directly to verify idempotency and the index-create success path.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexSitesSiteId';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @tbl sysname = N'sites';
DECLARE @col sysname = N'site_id';
DECLARE @ix  sysname = N'IX_sites_site_id';
DECLARE @rowCount bigint, @edition int;
DECLARE @sql nvarchar(max);
DECLARE @canOnline bit, @onlineDone bit;

-- Decide once whether ONLINE index operations are attemptable: Enterprise (3), Azure SQL DB (5) or MI (8).
-- Express/Standard/LocalDB (dev) do NOT support them; the attempt is still wrapped in TRY/CATCH (via
-- sp_executesql, which makes the edition error catchable) so we always fall back to an offline build.
SET @edition = CAST(SERVERPROPERTY('EngineEdition') AS int);
SET @canOnline = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10)) + N'; ONLINE index build '
    + CASE WHEN @canOnline = 1 THEN N'will be attempted (with offline fallback).' ELSE N'not supported on this edition - using offline.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.' + @tbl, N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N' does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @col)
BEGIN
    SET @msg = @migration + N': dbo.' + @tbl + N'.' + @col + N' does not exist, skipping.';
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
SET @msg = @migration + N': ' + @tbl + N'.' + @col + N' row estimate = ' + CAST(@rowCount AS nvarchar(20)) + N'.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

SET @stepStart = SYSUTCDATETIME();
SET @onlineDone = 0;

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

SET @msg = @migration + N': finished in ' + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Drops the index if present. Idempotent and guarded.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.sites', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.sites') AND name = N'IX_sites_site_id')
    DROP INDEX [IX_sites_site_id] ON [dbo].[sites];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexSitesSiteId'. Indexes sites.site_id so the importer's per-event 'resolve site by site_id' lookup seeks instead of full-scanning the sites table. sites is small so the build is quick; check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexSitesSiteId'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
