namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Shrinks <c>dbo.urls.full_url</c> from an (n)varchar(max) LOB column to
    /// <c>varchar(1700)</c> and adds a supporting non-clustered index
    /// <c>IX_urls_full_url</c>.
    ///
    /// Why: <c>full_url</c> is the join / de-duplication key used by the staging-table
    /// merges (e.g. "Migrate Hits Import into Hits.sql" and
    /// "Insert Activity from Staging Table.sql", which both do
    /// <c>JOIN urls ON urls.full_url = imports.url/object_id</c>). As a <c>(n)varchar(max)</c>
    /// column it cannot be a B-tree index key, so every one of those joins is forced into a
    /// full scan of the (largest dimension) <c>urls</c> table with LOB string comparisons.
    /// 1700 bytes is the SQL Server single-column non-clustered index key limit, so
    /// <c>varchar(1700)</c> is the widest indexable URL column.
    ///
    /// Safety: this script runs against a wide range of customer databases, some with very
    /// large <c>urls</c> tables, so it is defensive:
    ///   * Skips silently if <c>dbo.urls</c> or <c>full_url</c> are missing.
    ///   * Idempotent: if the column is already <c>varchar(1700)</c> and the index already
    ///     exists it is a no-op; each step (ALTER COLUMN, CREATE INDEX) is independently
    ///     guarded so a partially-applied state converges cleanly on re-run.
    ///   * FAILS FAST, before any schema change, if any row would be damaged by the shrink:
    ///       - rows whose URL is longer than 1700 characters (would be truncated), or
    ///       - rows whose URL contains characters that cannot be represented in the column's
    ///         (single-byte) code page once converted from Unicode to <c>varchar</c> (would
    ///         be silently corrupted to '?').
    ///     In either case it lists the offending <c>id</c> + <c>full_url</c> values and raises
    ///     a fatal error so the operator can fix the data and re-run the upgrade. Because the
    ///     check runs before the ALTER and the migration runs outside the EF transaction
    ///     (suppressTransaction: true), a failure leaves the database unchanged.
    ///   * Logs row counts, edition, online/offline choice and per-step timing via
    ///     RAISERROR ... WITH NOWAIT so operators can watch live progress.
    ///
    /// This migration does NOT change the EF entity model (<see cref="Common.Entities.Url"/>
    /// still maps <c>FullUrl</c> as a string), so it carries the same model snapshot as the
    /// previous migration - exactly like AddAuditEventsOperationIndex did. The actual SQL
    /// column type is narrowed below.
    /// </summary>
    public partial class ShrinkUrlsFullUrlColumn : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests can re-run
        /// the script directly against a test database to verify the guards (idempotency,
        /// too-long failure, lossy-conversion failure) and the success path.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'ShrinkUrlsFullUrlColumn';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @maxLen int = 1700;
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

-- Current type of full_url. Both nvarchar(max) (legacy v1-0-5 upgrade path) and
-- varchar(max) (fresh installs from Create DB.sql) report max_length = -1 and need shrinking.
DECLARE @typeName sysname, @maxLength smallint;
SELECT @typeName = t.name, @maxLength = c.max_length
FROM sys.columns c
INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID(N'dbo.urls') AND c.name = N'full_url';

DECLARE @alreadyShrunk bit = CASE WHEN @typeName = N'varchar' AND @maxLength = @maxLen THEN 1 ELSE 0 END;
DECLARE @indexExists bit = CASE WHEN EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.urls') AND name = @indexName
) THEN 1 ELSE 0 END;

IF @alreadyShrunk = 1 AND @indexExists = 1
BEGIN
    SET @msg = @migration + N': full_url is already varchar(' + CAST(@maxLen AS nvarchar(10))
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
-- Pre-flight data checks. Only meaningful while the column is still a (max) column - once
-- it is varchar(1700) the data is already guaranteed to fit. We run these BEFORE any ALTER
-- so that, if they fail, the database is left completely unchanged and the operator can fix
-- the data and simply re-run the upgrade.
-- =========================================================================================
IF @alreadyShrunk = 0
BEGIN
    -- 1. Rows whose URL is longer than the new limit would be silently truncated.
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

        RAISERROR('Run: SELECT id, LEN(full_url) AS length, full_url FROM dbo.urls WHERE LEN(full_url) > 1700 ORDER BY length DESC;', 0, 1) WITH NOWAIT;
        RAISERROR('Fix or remove these URLs (and the hits / events that reference them) then re-run the upgrade.', 0, 1) WITH NOWAIT;

        -- Fatal: abort the migration without having changed anything.
        SET @msg = @migration + N': aborted - full_url values exceed ' + CAST(@maxLen AS nvarchar(10)) + N' characters.';
        RAISERROR(@msg, 16, 1);
        RETURN;
    END

    -- 2. Rows that fit length-wise but contain characters that cannot survive the
    --    Unicode -> code-page conversion to varchar (e.g. CJK characters in a CP1252
    --    database). These would be silently corrupted to '?', changing the join key.
    DECLARE @lossy bigint = (
        SELECT COUNT_BIG(*) FROM dbo.urls
        WHERE LEN(full_url) <= @maxLen
          AND full_url <> CONVERT(nvarchar(max), CONVERT(varchar(1700), full_url))
    );

    IF @lossy > 0
    BEGIN
        SET @msg = @migration + N': ABORTING - ' + CAST(@lossy AS nvarchar(20))
            + N' row(s) in dbo.urls contain characters that cannot be represented in the column''s '
            + N'code page and would be corrupted by the conversion to varchar.';
        RAISERROR(@msg, 16, 1) WITH NOWAIT;

        RAISERROR('The offending rows (showing up to 50; id, url) are:', 0, 1) WITH NOWAIT;

        DECLARE @lid int, @lurl nvarchar(max);
        DECLARE lossyOffenders CURSOR LOCAL FAST_FORWARD FOR
            SELECT TOP (50) id, full_url
            FROM dbo.urls
            WHERE LEN(full_url) <= @maxLen
              AND full_url <> CONVERT(nvarchar(max), CONVERT(varchar(1700), full_url))
            ORDER BY id;
        OPEN lossyOffenders;
        FETCH NEXT FROM lossyOffenders INTO @lid, @lurl;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @msg = N'   id=' + CAST(@lid AS nvarchar(20)) + N', url=' + LEFT(@lurl, 1500);
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
            FETCH NEXT FROM lossyOffenders INTO @lid, @lurl;
        END
        CLOSE lossyOffenders;
        DEALLOCATE lossyOffenders;

        RAISERROR('Run: SELECT id, full_url FROM dbo.urls WHERE full_url <> CONVERT(nvarchar(max), CONVERT(varchar(1700), full_url));', 0, 1) WITH NOWAIT;
        RAISERROR('These URLs contain non-code-page characters. Fix or remove them (and their hits / events) then re-run the upgrade.', 0, 1) WITH NOWAIT;

        SET @msg = @migration + N': aborted - full_url values would be corrupted by the varchar conversion.';
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
    SET @msg = @migration + N': altering full_url to varchar(' + CAST(@maxLen AS nvarchar(10)) + N') NOT NULL...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    ALTER TABLE [dbo].[urls] ALTER COLUMN [full_url] varchar(1700) NOT NULL;

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
            Console.WriteLine("DB SCHEMA: Applying 'ShrinkUrlsFullUrlColumn'. On large urls tables the ALTER COLUMN can take a while and holds a schema lock; check the SQL session for live progress (RAISERROR ... WITH NOWAIT). If any URL is longer than 1700 chars (or not representable as varchar) the migration aborts and lists the offending id + url so you can fix the data and re-run.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'ShrinkUrlsFullUrlColumn'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
