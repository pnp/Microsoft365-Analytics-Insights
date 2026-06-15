namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Shrinks <c>dbo.urls.full_url</c> from an (n)varchar(max) LOB column to
    /// <c>nvarchar(850)</c> and adds a supporting non-clustered index
    /// <c>IX_urls_full_url</c>.
    ///
    /// Why: <c>full_url</c> is the join / de-duplication key used by the staging-table
    /// merges (e.g. "Migrate Hits Import into Hits.sql" and
    /// "Insert Activity from Staging Table.sql", which both do
    /// <c>JOIN urls ON urls.full_url = imports.url/object_id</c>). As a <c>(n)varchar(max)</c>
    /// column it cannot be a B-tree index key, so every one of those joins is forced into a
    /// full scan of the (largest dimension) <c>urls</c> table with LOB string comparisons.
    /// 1700 bytes is the SQL Server single-column non-clustered index key limit, and the column
    /// MUST be <c>nvarchar</c> (not <c>varchar</c>) because SharePoint URLs can contain any
    /// Unicode character (e.g. Greek) that a single-byte <c>varchar</c> code page would corrupt
    /// to '?'. <c>nvarchar</c> is 2 bytes/char, so <c>nvarchar(850)</c> (= 1700 bytes) is the
    /// widest indexable Unicode URL column. 850 chars comfortably exceeds SharePoint Online's
    /// documented worst-case URL length (~486 chars). See issue #122.
    ///
    /// Source types this converts (all lossless): <c>nvarchar(max)</c> (legacy v1-0-5 upgrade
    /// path and current fresh installs), <c>varchar(max)</c> (older fresh installs) and
    /// <c>varchar(1700)</c> (databases that applied the original, now-superseded, varchar form
    /// of this migration - see <see cref="UrlFullUrlNvarchar"/>). Widening varchar -> nvarchar
    /// never loses data.
    ///
    /// Safety: this script runs against a wide range of customer databases, some with very
    /// large <c>urls</c> tables, so it is defensive:
    ///   * Skips silently if <c>dbo.urls</c> or <c>full_url</c> are missing.
    ///   * Idempotent: if the column is already <c>nvarchar(850)</c> and the index already
    ///     exists it is a no-op; each step (ALTER COLUMN, CREATE INDEX) is independently
    ///     guarded so a partially-applied state converges cleanly on re-run.
    ///   * FAILS FAST, before any schema change, if any row would be damaged by the shrink:
    ///       - rows whose URL is longer than 850 characters (would be truncated).
    ///     It lists the offending <c>id</c> + <c>full_url</c> values and raises a fatal error so
    ///     the operator can fix the data and re-run the upgrade. Because the check runs before
    ///     the ALTER and the migration runs outside the EF transaction (suppressTransaction:
    ///     true), a failure leaves the database unchanged. (There is no lossy-conversion check
    ///     any more: <c>nvarchar</c> represents every Unicode character, so the conversion is
    ///     always faithful.)
    ///   * Logs row counts, edition, online/offline choice and per-step timing via
    ///     RAISERROR ... WITH NOWAIT so operators can watch live progress.
    ///
    /// This migration does NOT change the EF entity model snapshot itself; the matching
    /// <see cref="Common.Entities.Url.FullUrl"/> nvarchar(850) mapping snapshot is refreshed by
    /// the later <see cref="UrlFullUrlNvarchar"/> migration (which also re-runs this idempotent
    /// converter to fix databases already on the varchar form). The actual SQL column type is
    /// narrowed below.
    /// </summary>
    public partial class ShrinkUrlsFullUrlColumn : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests can re-run
        /// the script directly against a test database to verify the guards (idempotency,
        /// too-long failure) and the success path. It is also replayed verbatim by the later
        /// <see cref="UrlFullUrlNvarchar"/> migration to convert databases still on the
        /// superseded varchar(1700) form.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'ShrinkUrlsFullUrlColumn';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @maxLen int = 850;
DECLARE @indexName sysname = N'IX_urls_full_url';

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.urls', N'U') IS NULL
BEGIN
    SET @msg = @migration + N': dbo.urls does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = N'full_url'
)
BEGIN
    SET @msg = @migration + N': dbo.urls.full_url does not exist, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- Current type of full_url. nvarchar(max) (legacy v1-0-5 path and current fresh installs),
-- varchar(max) (older fresh installs) all report max_length = -1; varchar(1700) (databases on
-- the original varchar form of this migration) reports 1700. All need converting to nvarchar(850).
DECLARE @typeName sysname, @maxLength smallint;
SELECT @typeName = t.name, @maxLength = c.max_length
FROM sys.columns c
INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url';

-- nvarchar(850) stores 850 chars in 1700 bytes, so sys.columns.max_length = 1700 when already shrunk.
DECLARE @alreadyShrunk bit = CASE WHEN @typeName = N'nvarchar' AND @maxLength = @maxLen * 2 THEN 1 ELSE 0 END;
DECLARE @indexExists bit = CASE WHEN EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = @indexName
) THEN 1 ELSE 0 END;

IF @alreadyShrunk = 1 AND @indexExists = 1
BEGIN
    SET @msg = @migration + N': full_url is already nvarchar(' + CAST(@maxLen AS nvarchar(10))
        + N') and [' + @indexName + N'] already exists, nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

DECLARE @rowCount bigint = (
    SELECT ISNULL(SUM(p.rows), 0)
    FROM sys.partitions p
    WHERE p.object_id = OBJECT_ID(N'dbo.urls') AND p.index_id IN (0, 1)
);
SET @msg = @migration + N': dbo.urls row estimate = ' + CAST(@rowCount AS nvarchar(20))
    + N' (current full_url type ' + @typeName
    + CASE WHEN @maxLength = -1 THEN N'(max)' ELSE N'(' + CAST(@maxLength AS nvarchar(10)) + N')' END + N').';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- =========================================================================================
-- Pre-flight data check. Only meaningful while the column is still a (max) or varchar(1700)
-- column - once it is nvarchar(850) the data is already guaranteed to fit. We run this BEFORE
-- any ALTER so that, if it fails, the database is left completely unchanged and the operator
-- can fix the data and simply re-run the upgrade. There is no lossy-conversion check: the
-- target is nvarchar, which represents every Unicode character (e.g. Greek), so widening from
-- (n)varchar is always faithful.
-- =========================================================================================
IF @alreadyShrunk = 0
BEGIN
    -- Rows whose URL is longer than the new limit would be silently truncated.
    DECLARE @tooLong bigint = (
        SELECT COUNT_BIG(*) FROM dbo.urls WHERE LEN(full_url) > @maxLen
    );

    IF @tooLong > 0
    BEGIN
        SET @msg = @migration + N': ABORTING - ' + CAST(@tooLong AS nvarchar(20))
            + N' row(s) in dbo.urls have a full_url longer than ' + CAST(@maxLen AS nvarchar(10))
            + N' characters and would be truncated by the shrink.';
        RAISERROR(@msg, 16, 1) WITH NOWAIT;

        RAISERROR('The offending rows (showing up to 50; id, length, url) are:', 0, 1) WITH NOWAIT;

        DECLARE @id int, @len int, @url nvarchar(max);
        DECLARE offenders CURSOR LOCAL FAST_FORWARD FOR
            SELECT TOP (50) id, LEN(full_url) AS len, full_url
            FROM dbo.urls
            WHERE LEN(full_url) > @maxLen
            ORDER BY LEN(full_url) DESC;
        OPEN offenders;
        FETCH NEXT FROM offenders INTO @id, @len, @url;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            -- Keep within the 2000-char RAISERROR message limit; the full value is available
            -- via the diagnostic query below.
            SET @msg = N'   id=' + CAST(@id AS nvarchar(20)) + N', length=' + CAST(@len AS nvarchar(20))
                + N', url=' + LEFT(@url, 1500);
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            FETCH NEXT FROM offenders INTO @id, @len, @url;
        END
        CLOSE offenders;
        DEALLOCATE offenders;

        RAISERROR('Run: SELECT id, LEN(full_url) AS length, full_url FROM dbo.urls WHERE LEN(full_url) > 850 ORDER BY length DESC;', 0, 1) WITH NOWAIT;
        RAISERROR('Fix or remove these URLs (and the hits / events that reference them) then re-run the upgrade.', 0, 1) WITH NOWAIT;

        -- Fatal: abort the migration without having changed anything.
        SET @msg = @migration + N': aborted - full_url values exceed ' + CAST(@maxLen AS nvarchar(10)) + N' characters.';
        RAISERROR(@msg, 16, 1);
        RETURN;
    END
END

-- =========================================================================================
-- Shrink the column. ALTER COLUMN rewrites every row (LOB -> in-row) and takes a
-- schema-modification lock for its duration, so on large tables this is the slow step and a
-- maintenance window is recommended. SQL Server refuses ALTER COLUMN while an index
-- references the column, so we defensively drop any such index first (none is expected on
-- a stock schema - urls only has its clustered PK on id).
-- =========================================================================================
IF @alreadyShrunk = 0
BEGIN
    IF @indexExists = 1
    BEGIN
        SET @msg = @migration + N': dropping [' + @indexName + N'] before ALTER COLUMN...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        EXEC(N'DROP INDEX [' + @indexName + N'] ON [dbo].[urls];');
        SET @indexExists = 0;
    END

    SET @stepStart = SYSUTCDATETIME();
    SET @msg = @migration + N': altering full_url to nvarchar(' + CAST(@maxLen AS nvarchar(10)) + N') NOT NULL...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    ALTER TABLE [dbo].[urls] ALTER COLUMN [full_url] nvarchar(850) NOT NULL;

    SET @msg = @migration + N': ALTER COLUMN completed in '
        + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

-- =========================================================================================
-- Create the supporting index. Use ONLINE = ON on editions that support it (Enterprise,
-- Azure SQL DB, Azure SQL MI) so existing readers/writers aren't blocked; offline elsewhere.
-- Built via sp_executesql so the ONLINE clause isn't parsed on editions that reject it.
-- =========================================================================================
IF @indexExists = 0
BEGIN
    DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
    DECLARE @canOnline bit = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;
    DECLARE @sql nvarchar(max) = N'CREATE NONCLUSTERED INDEX [' + @indexName + N'] ON [dbo].[urls] ([full_url])';
    IF @canOnline = 1
        SET @sql = @sql + N' WITH (ONLINE = ON)';

    SET @msg = @migration + N': EngineEdition=' + CAST(@edition AS nvarchar(10))
        + N', ONLINE=' + CAST(@canOnline AS nvarchar(1)) + N'. Creating [' + @indexName + N']...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @stepStart = SYSUTCDATETIME();
    EXEC sp_executesql @sql;

    SET @msg = @migration + N': [' + @indexName + N'] created in '
        + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Drops the index and widens the column back to
        /// nvarchar(max) (the legacy v1-0-5 type). Idempotent and guarded.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'ShrinkUrlsFullUrlColumn (Down)';
DECLARE @msg nvarchar(2000);
DECLARE @indexName sysname = N'IX_urls_full_url';

IF OBJECT_ID(N'dbo.urls', N'U') IS NULL
    RETURN;

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = @indexName
)
BEGIN
    SET @msg = @migration + N': dropping [' + @indexName + N']...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    EXEC(N'DROP INDEX [' + @indexName + N'] ON [dbo].[urls];');
END

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url'
      AND NOT (t.name = N'nvarchar' AND c.max_length = -1)
)
BEGIN
    SET @msg = @migration + N': widening full_url back to nvarchar(max) NOT NULL...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    ALTER TABLE [dbo].[urls] ALTER COLUMN [full_url] nvarchar(max) NOT NULL;
END
";

        public override void Up()
        {
            // See Up_Sql for the full rationale. Runs outside the EF migration transaction
            // (suppressTransaction: true) so the schema-modification lock from ALTER COLUMN
            // is released as soon as it completes, and so a pre-flight failure leaves the DB
            // untouched. Configuration.cs sets CommandTimeout = 0 (infinite) so the shrink of
            // a very large urls table will not time out.
            Console.WriteLine("DB SCHEMA: Applying 'ShrinkUrlsFullUrlColumn'. On large urls tables the ALTER COLUMN can take a while and holds a schema lock; check the SQL session for live progress (RAISERROR ... WITH NOWAIT). full_url becomes nvarchar(850) (Unicode-safe, e.g. Greek URLs). If any URL is longer than 850 chars the migration aborts and lists the offending id + url so you can fix the data and re-run.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'ShrinkUrlsFullUrlColumn'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
