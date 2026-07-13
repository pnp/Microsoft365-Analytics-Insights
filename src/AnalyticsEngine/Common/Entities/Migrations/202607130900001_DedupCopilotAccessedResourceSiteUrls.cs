namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// De-duplicates the <c>copilot_event_accessed_resource_site_urls</c> lookup table.
    ///
    /// Why: the Copilot audit <c>SiteUrl</c> carries a volatile per-access token (e.g. <c>?xsdata=...</c>),
    /// so every access to the same site arrives as a different string and the merge stored a near-unique
    /// row per access. On a real tenant this ballooned the table to millions of rows (far larger than the
    /// resource-id / name dimensions), bloated the junction/index maintenance on every import batch, and
    /// made <c>site_url</c> useless as a reporting dimension (one site fragmented across millions of
    /// tokenised variants). The merge (<c>common_upsert_copilot_agents.sql</c>) now normalises SiteUrl to
    /// its path before de-dup, so NEW imports collapse correctly; this migration cleans up the EXISTING rows.
    ///
    /// What it does, all set-based and batched:
    ///   1. Computes each row's normalised path (everything before the first <c>?</c> or <c>#</c>) and picks
    ///      one canonical row per path (<c>MIN(id)</c>).
    ///   2. Re-points every junction row (<c>copilot_event_accessed_resources.resource_site_url_id</c>) from
    ///      a duplicate row to its canonical row (so no FK is orphaned).
    ///   3. Normalises the surviving canonical rows' <c>site_url</c> to the path.
    ///   4. Deletes the now-unreferenced duplicate rows.
    ///
    /// Safety: idempotent (a no-op once the table is clean), guarded (skips if the table is missing), and
    /// every UPDATE/DELETE is done in small batches in a WHILE loop so no single statement takes a large
    /// lock or a long transaction - the whole thing runs with <c>suppressTransaction: true</c> so each batch
    /// commits independently and a partial run converges on re-run. <c>Configuration.cs</c> sets
    /// <c>CommandTimeout = 0</c> so the one-time pass over a multi-million-row table won't time out. This is
    /// DML (no schema change), so it reuses the previous migration's model snapshot. Rows with a NULL
    /// <c>site_url</c> are left untouched (the merge never inserts them). Runs via the installer /
    /// <c>DatabaseUpgrader.CheckDbUpgraded</c> - schedule large upgrades in a maintenance window with the
    /// importer stopped. It does not de-duplicate junction rows that become identical after re-pointing (an
    /// event that referenced the same resource via two tokenised URLs) - that is a rare, non-integrity edge
    /// case and the merge's NOT EXISTS prevents new occurrences.
    ///
    /// There is no meaningful <c>Down</c>: the volatile tokens are discarded and cannot be reconstructed.
    /// </summary>
    public partial class DedupCopilotAccessedResourceSiteUrls : DbMigration
    {
        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so unit tests can re-run it directly and
        /// the shipped manual upgrade script can reuse it verbatim. Idempotent, guarded and batched.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'DedupCopilotAccessedResourceSiteUrls';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @batch int = 4000;
DECLARE @done bigint;
DECLARE @dupCount bigint;
DECLARE @normCount bigint;
DECLARE @lastId int;
DECLARE @curMax int;
DECLARE @nextLog bigint;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121) + N' UTC.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF OBJECT_ID(N'dbo.copilot_event_accessed_resource_site_urls', N'U') IS NULL
   OR OBJECT_ID(N'dbo.copilot_event_accessed_resources', N'U') IS NULL
BEGIN
    SET @msg = @migration + N': lookup/junction table missing, skipping.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    RETURN;
END

-- Build the id -> normalised-path -> canonical-id map (only non-null site_url rows). The path is
-- everything before the first '?' or '#'; NULLIF turns CHARINDEX's 0 (not found) into NULL so MIN
-- ignores it, and when neither is present the whole value is kept. Mirrors the merge normalisation.
IF OBJECT_ID('tempdb..#norm') IS NOT NULL DROP TABLE #norm;
SELECT s.id,
       LEFT(s.site_url,
            ISNULL((SELECT MIN(pos) FROM (VALUES
                       (NULLIF(CHARINDEX('?', s.site_url), 0)),
                       (NULLIF(CHARINDEX('#', s.site_url), 0))) q(pos)) - 1,
                   LEN(s.site_url))) AS norm_path
INTO #norm
FROM dbo.copilot_event_accessed_resource_site_urls s
WHERE s.site_url IS NOT NULL;

IF OBJECT_ID('tempdb..#map') IS NOT NULL DROP TABLE #map;
SELECT n.id,
       n.norm_path,
       MIN(n.id) OVER (PARTITION BY n.norm_path) AS canonical_id
INTO #map
FROM #norm n;
CREATE UNIQUE CLUSTERED INDEX IX_map_id ON #map(id);

SELECT @dupCount = COUNT_BIG(*) FROM #map WHERE id <> canonical_id;
SELECT @normCount = COUNT_BIG(*) FROM #map m
    INNER JOIN dbo.copilot_event_accessed_resource_site_urls s ON s.id = m.id
    WHERE m.id = m.canonical_id AND s.site_url <> m.norm_path;

SET @msg = @migration + N': ' + CAST(@dupCount AS nvarchar(20)) + N' duplicate site_url row(s) to collapse, '
    + CAST(@normCount AS nvarchar(20)) + N' canonical row(s) to normalise.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF @dupCount = 0 AND @normCount = 0
BEGIN
    SET @msg = @migration + N': already clean, nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    DROP TABLE #norm; DROP TABLE #map;
    RETURN;
END

-- Materialise each phase's exact work-set ONCE, keyed so the loops below batch by a seek instead of
-- re-scanning #map / the junction on every iteration. The previous version re-scanned the target on
-- every batch (depleting UPDATE/DELETE TOP), which is O(N^2) - the junction re-point dominated a
-- multi-hour run at scale. Each loop now walks its work-set in key order and touches only the matching
-- target rows by their clustered PK: O(N) overall.

-- Junction rows to re-point, with their target canonical id (one pass, seeking via IX_resource_site_url_id).
IF OBJECT_ID('tempdb..#repoint') IS NOT NULL DROP TABLE #repoint;
SELECT j.id AS junction_id, m.canonical_id
INTO #repoint
FROM dbo.copilot_event_accessed_resources j
INNER JOIN #map m ON j.resource_site_url_id = m.id
WHERE m.id <> m.canonical_id;
CREATE UNIQUE CLUSTERED INDEX IX_repoint_id ON #repoint(junction_id);

-- Canonical rows whose stored value still carries the token (need rewriting to the path).
IF OBJECT_ID('tempdb..#normwork') IS NOT NULL DROP TABLE #normwork;
SELECT m.id, m.norm_path
INTO #normwork
FROM #map m
INNER JOIN dbo.copilot_event_accessed_resource_site_urls s ON s.id = m.id
WHERE m.id = m.canonical_id AND s.site_url <> m.norm_path;
CREATE UNIQUE CLUSTERED INDEX IX_normwork_id ON #normwork(id);

-- Duplicate rows to delete once the junction no longer references them.
IF OBJECT_ID('tempdb..#dupids') IS NOT NULL DROP TABLE #dupids;
SELECT m.id
INTO #dupids
FROM #map m
WHERE m.id <> m.canonical_id;
CREATE UNIQUE CLUSTERED INDEX IX_dupids_id ON #dupids(id);

DROP TABLE #norm;
DROP TABLE #map;

-- 1) Re-point junction rows from duplicate site_url ids to their canonical id (FK-safe). Batched by the
--    #repoint key range so each batch seeks the junction by its clustered PK instead of scanning it.
SET @stepStart = SYSUTCDATETIME();
SET @done = 0; SET @lastId = -1; SET @nextLog = 200000;
WHILE 1 = 1
BEGIN
    SET @curMax = NULL;
    SELECT @curMax = MAX(junction_id) FROM
        (SELECT TOP (@batch) junction_id FROM #repoint WHERE junction_id > @lastId ORDER BY junction_id) x;
    IF @curMax IS NULL BREAK;

    UPDATE j
        SET j.resource_site_url_id = r.canonical_id
    FROM dbo.copilot_event_accessed_resources j
    INNER JOIN #repoint r ON r.junction_id = j.id
    WHERE r.junction_id > @lastId AND r.junction_id <= @curMax;

    SET @done = @done + @@ROWCOUNT;
    SET @lastId = @curMax;
    IF @done >= @nextLog
    BEGIN
        SET @msg = @migration + N': re-pointed ' + CAST(@done AS nvarchar(20)) + N' junction row(s)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @nextLog = @nextLog + 200000;
    END
END
SET @msg = @migration + N': junction re-point done (' + CAST(@done AS nvarchar(20)) + N' rows) in '
    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- 2) Normalise the surviving canonical rows' value to the path. Batched by the #normwork key range.
SET @stepStart = SYSUTCDATETIME();
SET @done = 0; SET @lastId = -1;
WHILE 1 = 1
BEGIN
    SET @curMax = NULL;
    SELECT @curMax = MAX(id) FROM
        (SELECT TOP (@batch) id FROM #normwork WHERE id > @lastId ORDER BY id) x;
    IF @curMax IS NULL BREAK;

    UPDATE s
        SET s.site_url = w.norm_path
    FROM dbo.copilot_event_accessed_resource_site_urls s
    INNER JOIN #normwork w ON w.id = s.id
    WHERE w.id > @lastId AND w.id <= @curMax;

    SET @done = @done + @@ROWCOUNT;
    SET @lastId = @curMax;
END
SET @msg = @migration + N': normalised ' + CAST(@done AS nvarchar(20)) + N' canonical row(s) in '
    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- 3) Delete the now-unreferenced duplicate rows. Batched by the #dupids key range. Safe: every junction
--    row was re-pointed above.
SET @stepStart = SYSUTCDATETIME();
SET @done = 0; SET @lastId = -1; SET @nextLog = 200000;
WHILE 1 = 1
BEGIN
    SET @curMax = NULL;
    SELECT @curMax = MAX(id) FROM
        (SELECT TOP (@batch) id FROM #dupids WHERE id > @lastId ORDER BY id) x;
    IF @curMax IS NULL BREAK;

    DELETE s
    FROM dbo.copilot_event_accessed_resource_site_urls s
    INNER JOIN #dupids d ON d.id = s.id
    WHERE d.id > @lastId AND d.id <= @curMax;

    SET @done = @done + @@ROWCOUNT;
    SET @lastId = @curMax;
    IF @done >= @nextLog
    BEGIN
        SET @msg = @migration + N': deleted ' + CAST(@done AS nvarchar(20)) + N' duplicate site_url row(s)...';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        SET @nextLog = @nextLog + 200000;
    END
END
SET @msg = @migration + N': deleted ' + CAST(@done AS nvarchar(20)) + N' duplicate row(s) in '
    + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DROP TABLE #repoint;
DROP TABLE #normwork;
DROP TABLE #dupids;

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'DedupCopilotAccessedResourceSiteUrls'. Collapses the Copilot site_url lookup table (removing the volatile per-access token that made it balloon to millions of near-duplicate rows), re-pointing the junction to the surviving canonical rows. Idempotent and batched; on a large table run in a maintenance window with the importer stopped. Watch the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            // The volatile tokens are intentionally discarded and cannot be reconstructed, so this is a no-op.
            Console.WriteLine("DB SCHEMA: Reverting 'DedupCopilotAccessedResourceSiteUrls' is a no-op (the collapsed token data cannot be restored).");
        }
    }
}
