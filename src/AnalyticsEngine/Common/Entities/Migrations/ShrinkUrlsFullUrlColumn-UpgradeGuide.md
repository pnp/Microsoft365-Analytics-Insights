# Upgrade guide: `urls.full_url` → `nvarchar(850)` (issues #109 / #122)

**Migrations involved:**

| Migration id | Role |
|--------------|------|
| `202606011710001_ShrinkUrlsFullUrlColumn` | Converts `dbo.urls.full_url` to **`nvarchar(850)`** and creates the non‑clustered index `IX_urls_full_url`. Idempotent. |
| `202606011739254_UrlFullUrlVarcharMapping` | **Superseded.** Metadata‑only snapshot refresh from the original (now‑replaced) `varchar(1700)` form. Empty `Up`/`Down`. Kept only so existing `__MigrationHistory` chains stay valid. |
| `202606141000001_UrlFullUrlNvarchar` | Catch‑up migration. Replays the corrected `ShrinkUrlsFullUrlColumn` converter (so databases already on `varchar(1700)` are converted to `nvarchar(850)`) and refreshes the EF model snapshot to `nvarchar(850)`. |

**Schema change:** **Yes** – `dbo.urls.full_url` becomes `nvarchar(850) NOT NULL` with a supporting non‑clustered index `IX_urls_full_url`. Unicode‑safe.

---

## ⚠️ Why this exists — the Greek‑URL corruption bug

An earlier release shipped `ShrinkUrlsFullUrlColumn` / `UrlFullUrlVarcharMapping` that made
`full_url` a **`varchar(1700)`** column. `varchar` stores a single code page, so it **corrupts
any non‑Latin character to `?`** — e.g. the Greek SharePoint URL

```
https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.pdf
```

That release **should never have been published as stable**. The column must be **`nvarchar`**
(2 bytes/char, full Unicode). `nvarchar(850)` = 1700 bytes = the SQL Server non‑clustered
index‑key byte limit, so it is the widest Unicode URL column that can still be the
`IX_urls_full_url` key. 850 characters comfortably exceeds SharePoint Online's documented
worst‑case URL length (~486 chars).

## Why an index at all

`urls.full_url` is the join / de‑duplication key for the staging‑table merges
(`Migrate Hits Import into Hits.sql`, `Insert Activity from Staging Table.sql`,
`Migrate clicks from staging.sql`, `insert_sp_copilot_events_from_staging_table.sql`), which all
do `JOIN urls ON urls.full_url = imports.url/object_id`. As an `(n)varchar(max)` LOB column it
**cannot be an index key**, so every one of those joins is forced into a full scan of the `urls`
table (the largest dimension table). The staging join columns are declared `nvarchar(850)` (via
`SqlTypeOverride`) so they match the target type exactly and the merges can **seek**
`IX_urls_full_url` instead of falling back to an implicit conversion that defeats the index.

---

## Supported upgrade‑from states (both end at `nvarchar(850)` losslessly)

There are two published releases customers may be upgrading **from**. Both reach `nvarchar(850)`
without losing data:

### State B — previous stable release (last migration `RemoveDataverseTables`)

- `full_url` is still `(n)varchar(max)`; `ShrinkUrlsFullUrlColumn` has **not** been applied.
- On upgrade EF applies the **corrected** `ShrinkUrlsFullUrlColumn` (→ `nvarchar(850)`), then the
  empty `UrlFullUrlVarcharMapping`, then `UrlFullUrlNvarchar` (a no‑op, already `nvarchar(850)`).
- `nvarchar(max)` → `nvarchar(850)` is a pure Unicode‑preserving widen, so **Greek URLs are kept
  intact**.
- These are the customers (e.g. the Greek bank that reported this) who were previously **blocked**
  from the `varchar` release by its lossy pre‑flight check. They can now upgrade.

### State C — current stable release (last migration `UrlFullUrlVarcharMapping`)

- `full_url` is already `varchar(1700)` (the bad form).
- On upgrade EF applies only the new `UrlFullUrlNvarchar`, which converts
  `varchar(1700)` → `nvarchar(850)`. Widening `varchar` → `nvarchar` **never loses data**.
- **Why this is safe:** the original `varchar` migration had a pre‑flight that *aborted* on any
  URL not representable in the column's code page. So any database that actually **reached**
  State C contains only code‑page‑representable URLs — either plain ASCII, or (on a Greek DB
  collation) Greek stored intact in CP1253. In both cases the `varchar(1700)` → `nvarchar(850)`
  conversion is faithful. A customer whose Greek URLs *would* have been corrupted was blocked at
  State B and never got here.
- Caveat: if a State C database somehow already contains `?`‑corrupted URLs (data damaged before
  it reached State C), this migration **cannot recover** the original characters — that data was
  destroyed by the earlier `varchar` conversion. It only prevents further corruption and makes the
  column Unicode‑safe going forward.

Fresh installs run `Create DB.sql` (`full_url nvarchar(max)`) then **all** migrations, ending at
`nvarchar(850)`.

---

## What `ShrinkUrlsFullUrlColumn` does (in order)

1. **Skips** if `dbo.urls` / `full_url` are absent, or if already applied (column already
   `nvarchar(850)` **and** `IX_urls_full_url` exists). Idempotent and safe to re‑run — this is
   exactly what makes `UrlFullUrlNvarchar` a no‑op on databases that are already converted.
2. **Pre‑flight data check** (runs *before* any schema change, so a failure leaves the DB
   untouched): any `full_url` **longer than 850 characters** (would be truncated). If found, it
   **lists the offending `id` + `full_url`** (up to 50) plus the exact diagnostic query, then
   **aborts**. There is **no** lossy/representability check any more — `nvarchar` represents every
   Unicode character, so the conversion is always faithful.
3. `ALTER COLUMN full_url nvarchar(850) NOT NULL` — the slow step (rewrites every row); drops
   `IX_urls_full_url` first if present (SQL Server blocks `ALTER COLUMN` on an indexed column).
4. `CREATE NONCLUSTERED INDEX IX_urls_full_url ON urls(full_url)` — `ONLINE = ON` on
   Enterprise / Azure SQL DB / Azure SQL MI, offline on other editions.

Progress (row counts, edition, online/offline, per‑step timings) is streamed live via
`RAISERROR ... WITH NOWAIT`; watch it in the SSMS / Azure Data Studio *Messages* tab.

---

## How long will it take? (≈10,000,000 URLs)

**Order‑of‑magnitude estimates** to size a maintenance window, not guarantees. Actual time is
dominated by storage throughput / service tier and average URL length. **Always validate on a
restored copy of the customer database first** — the migration logs real per‑step durations.

Assuming ~10M rows, average URL ~150 characters:

| Step | Work | Rough estimate @ 10M rows |
|------|------|---------------------------|
| Pre‑flight length scan | One full scan reading the LOB data | ~1–3 min |
| `ALTER COLUMN` → `nvarchar(850)` | **Size‑of‑data row rewrite, fully logged, holds a Sch‑M lock (offline)** | **~5–20 min** (dominant cost) |
| `CREATE INDEX` (≈2–3 GB B‑tree) | Sort + build; `ONLINE` on Enterprise/Azure | ~2–10 min |
| **Total** | | **~10–35 min** |

Scaling is roughly linear with row count / data size: a 50–100M‑row `urls` table can take **well
over an hour**, so plan a maintenance window accordingly. (Note `nvarchar` is 2 bytes/char, so the
rewritten column and its index are somewhat larger than the old `varchar` form — budget a little
more log and index space.)

For State C databases the work is identical (`varchar(1700)` → `nvarchar(850)` is still a
size‑of‑data rewrite). For databases already at `nvarchar(850)`, `UrlFullUrlNvarchar` is an instant
no‑op.

### Resource requirements / pre‑deploy steps

- **Pause the importers** (App Insights + Activity WebJobs) during the upgrade. The `ALTER COLUMN`
  takes a schema‑modification lock on `urls`, which will block — and be blocked by — the staging
  merges.
- **Transaction log space:** `ALTER COLUMN` is fully logged; ensure free log space on the order of
  the table size (several GB for 10M rows). On Azure SQL ensure the tier has headroom.
- **Backups:** take a backup / snapshot before upgrading (standard practice for schema changes).
- `Configuration.CommandTimeout` is already `0` (infinite), so the migration will not time out even
  on a multi‑hour rewrite.
- Both `ShrinkUrlsFullUrlColumn` and `UrlFullUrlNvarchar` run **outside** the EF migration
  transaction (`suppressTransaction: true`), so each step commits independently and a pre‑flight
  failure rolls back nothing (no change was made yet).

---

## If the migration aborts

If a customer DB has URLs longer than 850 characters, the migration **aborts before changing
anything** and prints the offending rows.

### 1. How many URLs are there, and how many are oversized?

First get the scale of the problem — the **total** row count and how many are actually over the
limit (the abort only tells you about the oversized ones):

```sql
SELECT
    COUNT(*)                                                              AS total_urls,
    SUM(CASE WHEN LEN(full_url) > 850 THEN 1 ELSE 0 END)                  AS oversize_urls,
    SUM(CASE WHEN LEN(full_url) > 850
              AND (full_url LIKE N'%?xsdata=%' OR full_url LIKE N'%&xsdata=%')
             THEN 1 ELSE 0 END)                                           AS oversize_with_xsdata,
    SUM(CASE WHEN LEN(full_url) > 850
              AND NOT (full_url LIKE N'%?xsdata=%' OR full_url LIKE N'%&xsdata=%')
             THEN 1 ELSE 0 END)                                           AS oversize_other
FROM dbo.urls;
```

Then list the offending rows themselves:

```sql
SELECT id, LEN(full_url) AS length, full_url
FROM dbo.urls
WHERE LEN(full_url) > 850
ORDER BY length DESC;
```

In practice most of the `oversize_with_xsdata` rows can be fixed automatically (next section). Any
`oversize_other` rows (over 850 even without an `xsdata` parameter) must be resolved by hand —
delete the obsolete records or shorten the stored URL (and the `hits` / `event_meta_sharepoint`
rows that reference them via `url_id`) — then re‑run the upgrade. The migration is idempotent, so
re‑running after a fix simply continues.

> There is no longer a "not representable as varchar" abort: the target is `nvarchar`, which holds
> every Unicode character, so Greek (and any other script) is preserved rather than rejected.

---

## Cleaning up oversized Teams deep‑link (`xsdata`) URLs

A large share of real‑world oversized URLs are SharePoint links opened from Microsoft Teams. Teams
appends a big, **transient** `xsdata=` token (plus `sdata`, `ovuser`, `clickparams`, …) to the URL.
The `xsdata` value alone is typically **600–1100 characters** of opaque base64 and carries **no
analytic value** — it just records *how* the link was opened. Example (line‑wrapped for clarity):

```
https://contoso.sharepoint.com/sites/Marketing/Lists/Announcements/AllItems.aspx
    ?xsdata=<opaque base64 token, ~700 chars>&sdata=…&ovuser=…&clickparams=…&viewid=…
```

Removing just the `xsdata` parameter brings these URLs back under 850 characters in the vast
majority of cases (observed: real samples of **1213–1515** chars drop to **601–935** chars). The
remainder of the URL — path, all other query parameters and any `#fragment` — is preserved.

> **Note** — going forward the importers no longer store these tokens: when a URL exceeds the
> column limit (`Url.FullUrlMaxLength` = 850) the activity / click / Copilot import paths strip the
> `xsdata` parameter **at insert time** — and, as a last resort, hard‑truncate to 850 if the URL is
> *still* too long — via `StringUtils.EnsureUrlWithinLength`. This clean‑up script is for databases
> that already accumulated oversized rows before that change.

### 2. Clean up the oversized `xsdata` URLs (transaction defaults to ROLLBACK)

The script below **defaults to `ROLLBACK`** so it is safe to run as a *preview*: it does all the
work, prints how many rows it would clean and how many would still be oversized, then throws it all
away. **Verify those numbers, then — and only then — change the final `ROLLBACK TRANSACTION;` to
`COMMIT TRANSACTION;` and re‑run to apply the changes for real.** Back up / snapshot the database
first, as with any data fix.

It only touches rows that are **over 850 characters** and contain a real `xsdata` parameter, only
applies the cleaned value when it **fits** (≤ 850) **and does not already exist** on another row
(so it never creates a duplicate `full_url`), and collapses multiple oversized rows that clean to
the same value down to one.

```sql
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 1. Collect oversize URLs that carry a real xsdata parameter, with the parameter's position.
--    Materialising into #fix first stops the query optimiser from evaluating the string-slicing
--    below against rows that have no xsdata parameter (which would pass a negative length to
--    LEFT/SUBSTRING). Every #fix row is guaranteed to have sep >= 1.
IF OBJECT_ID('tempdb..#fix') IS NOT NULL DROP TABLE #fix;
SELECT id, full_url,
       sep = CASE WHEN CHARINDEX(N'?xsdata=', full_url) > 0 THEN CHARINDEX(N'?xsdata=', full_url)
                  ELSE CHARINDEX(N'&xsdata=', full_url) END,
       cleaned_url = CAST(NULL AS nvarchar(max))
INTO #fix
FROM dbo.urls
WHERE LEN(full_url) > 850
  AND (full_url LIKE N'%?xsdata=%' OR full_url LIKE N'%&xsdata=%');

-- 2. Build the cleaned URL (xsdata parameter removed, everything else - including any #fragment -
--    preserved). The value ends at the next '&' or '#', or end-of-string.
UPDATE f
SET cleaned_url = CASE
        WHEN term = 0 THEN LEFT(full_url, sep - 1)
        WHEN sepChar = N'?' AND SUBSTRING(full_url, term, 1) = N'&'
             THEN LEFT(full_url, sep) + SUBSTRING(full_url, term + 1, 4000)
        WHEN sepChar = N'?' AND SUBSTRING(full_url, term, 1) = N'#'
             THEN LEFT(full_url, sep - 1) + SUBSTRING(full_url, term, 4000)
        ELSE LEFT(full_url, sep - 1) + SUBSTRING(full_url, term, 4000)
    END
FROM #fix f
CROSS APPLY (SELECT sepChar = SUBSTRING(full_url, sep, 1),
                    endAmp  = CHARINDEX(N'&', full_url, sep + 8),
                    endHash = CHARINDEX(N'#', full_url, sep + 8)) a
CROSS APPLY (SELECT term = CASE WHEN a.endAmp = 0 AND a.endHash = 0 THEN 0
                               WHEN a.endAmp = 0 THEN a.endHash
                               WHEN a.endHash = 0 THEN a.endAmp
                               WHEN a.endAmp < a.endHash THEN a.endAmp
                               ELSE a.endHash END) b;

-- 3. Apply: only rows that now fit (<= 850) AND whose cleaned value does not already exist on
--    another row (so we never create a duplicate full_url). ROW_NUMBER collapses any oversize
--    rows that clean to the same value, updating just one of them.
;WITH ranked AS (
    SELECT id, cleaned_url,
           rn = ROW_NUMBER() OVER (PARTITION BY CAST(cleaned_url AS nvarchar(850)) ORDER BY id)
    FROM #fix
    WHERE LEN(cleaned_url) <= 850
)
UPDATE u
   SET u.full_url = r.cleaned_url
FROM dbo.urls u
INNER JOIN ranked r ON r.id = u.id
WHERE r.rn = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.urls e WHERE e.full_url = r.cleaned_url AND e.id <> r.id);

DECLARE @cleaned int = @@ROWCOUNT;
RAISERROR('Rows cleaned: %d', 0, 1, @cleaned) WITH NOWAIT;

-- 4. What still exceeds 850 (xsdata couldn't shrink it enough, or its cleaned form already exists)?
SELECT remaining_oversize    = COUNT(*),
       of_which_still_xsdata = SUM(CASE WHEN full_url LIKE N'%?xsdata=%' OR full_url LIKE N'%&xsdata=%' THEN 1 ELSE 0 END)
FROM dbo.urls WHERE LEN(full_url) > 850;

DROP TABLE #fix;

-- VERIFY the two result sets above, then change ROLLBACK to COMMIT and re-run to apply.
ROLLBACK TRANSACTION;
-- COMMIT TRANSACTION;
```

### 3. What's left after the clean‑up — classify it

`remaining_oversize` counts the rows the `xsdata` strip could **not** auto‑fix. In practice the strip
resolves the large majority of oversized URLs (the `xsdata` token is usually the bulk of the bloat);
only a handful remain. Each remaining row falls into one of these buckets:

- **Still > 850 after removing `xsdata`** — long because of *other* junk (e.g. a big `FilterValues1=…`
  list, or a `SafelinksUrl=` that embeds a whole second copy of the URL, often with duplicated
  comma‑merged Teams parameters). Removing `xsdata` isn't enough.
- **Cleaned value already exists** — the de‑duplicated URL is already tracked by another `urls` row,
  so the oversized duplicate was left untouched to avoid creating a duplicate `full_url`.
- **No `xsdata` at all** — oversized for some unrelated reason.

This read‑only query classifies every remaining oversized row so you know which bucket each is in
(it materialises the `xsdata` rows into `#fix` first so the slicing never sees a row without the
parameter):

```sql
SET NOCOUNT ON;
IF OBJECT_ID('tempdb..#over') IS NOT NULL DROP TABLE #over;
SELECT id, full_url INTO #over FROM dbo.urls WHERE LEN(full_url) > 850;

IF OBJECT_ID('tempdb..#fix') IS NOT NULL DROP TABLE #fix;
SELECT id, full_url,
       sep = CASE WHEN CHARINDEX(N'?xsdata=', full_url) > 0 THEN CHARINDEX(N'?xsdata=', full_url)
                  ELSE CHARINDEX(N'&xsdata=', full_url) END,
       cleaned_url = CAST(NULL AS nvarchar(max))
INTO #fix
FROM #over
WHERE full_url LIKE N'%?xsdata=%' OR full_url LIKE N'%&xsdata=%';

UPDATE f
SET cleaned_url = CASE
        WHEN term = 0 THEN LEFT(full_url, sep - 1)
        WHEN sepChar = N'?' AND SUBSTRING(full_url, term, 1) = N'&'
             THEN LEFT(full_url, sep) + SUBSTRING(full_url, term + 1, 4000)
        WHEN sepChar = N'?' AND SUBSTRING(full_url, term, 1) = N'#'
             THEN LEFT(full_url, sep - 1) + SUBSTRING(full_url, term, 4000)
        ELSE LEFT(full_url, sep - 1) + SUBSTRING(full_url, term, 4000)
    END
FROM #fix f
CROSS APPLY (SELECT sepChar = SUBSTRING(full_url, sep, 1),
                    endAmp  = CHARINDEX(N'&', full_url, sep + 8),
                    endHash = CHARINDEX(N'#', full_url, sep + 8)) a
CROSS APPLY (SELECT term = CASE WHEN a.endAmp = 0 AND a.endHash = 0 THEN 0
                               WHEN a.endAmp = 0 THEN a.endHash
                               WHEN a.endHash = 0 THEN a.endAmp
                               WHEN a.endAmp < a.endHash THEN a.endAmp
                               ELSE a.endHash END) b;

SELECT
    o.id,
    original_length = LEN(o.full_url),
    length_after_removing_xsdata = LEN(f.cleaned_url),
    reason = CASE
        WHEN f.id IS NULL THEN N'No xsdata parameter - long for other reasons'
        WHEN LEN(f.cleaned_url) > 850 THEN N'Still > 850 after removing xsdata (other bloat)'
        WHEN EXISTS (SELECT 1 FROM dbo.urls e WHERE e.full_url = f.cleaned_url AND e.id <> o.id)
             THEN N'Cleaned value already exists on another row'
        ELSE N'Would clean to <= 850 - re-run the clean-up script (with COMMIT)'
    END,
    o.full_url
FROM #over o
LEFT JOIN #fix f ON f.id = o.id
ORDER BY original_length DESC;
DROP TABLE #over; DROP TABLE #fix;
```

> If any row shows **"Would clean to <= 850 - re-run the clean-up script (with COMMIT)"**, you have
> only run the section‑2 preview — the `xsdata` strip will fix it once you flip `ROLLBACK` → `COMMIT`.
> Only rows in the other three buckets are genuinely stuck.

### 4. Reduce the genuinely‑stuck rows to their path (dedup merge)

For the stuck rows, the only meaningful part is the **page path** — everything after `?` is volatile
Teams junk (`xsdata`, `sdata`, `ovuser`, `clickparams`, `SafelinksUrl`, filters, `viewid`). The
script below reduces each genuinely‑stuck row (still > 850 after the `xsdata` strip, *or* with no
`xsdata` at all) to its path. Because several stuck rows can share the same page — and a clean copy
of that page may already exist — it does a proper **de‑duplicating merge**: it picks one canonical
`urls` row per path, **repoints every foreign key that references `urls`** (discovered from the
catalog, so no referencing table — `hits`, `event_meta_sharepoint`, comments, likes, Copilot files,
file metadata, … — is missed) onto the canonical row, then deletes the duplicates. It therefore
never creates a duplicate `full_url` and never loses the referenced facts.

It leaves every row that the section‑2 `xsdata` strip can already fix **untouched** (it only acts on
rows whose `xsdata`‑stripped length is still > 850), so run it **after** section 2 has been
committed. Like the others it **defaults to `ROLLBACK`** — verify `remaining_oversize = 0` and
`duplicate_full_urls = 0`, then flip to `COMMIT`. Back up / snapshot first.

```sql
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- 1. Oversize rows.
IF OBJECT_ID('tempdb..#over') IS NOT NULL DROP TABLE #over;
SELECT id, full_url INTO #over FROM dbo.urls WHERE LEN(full_url) > 850;

-- 2. Of those, the ones carrying xsdata, with the length they'd have AFTER removing it.
IF OBJECT_ID('tempdb..#fix') IS NOT NULL DROP TABLE #fix;
SELECT id, full_url,
       sep = CASE WHEN CHARINDEX(N'?xsdata=', full_url) > 0 THEN CHARINDEX(N'?xsdata=', full_url)
                  ELSE CHARINDEX(N'&xsdata=', full_url) END,
       cleaned_len = CAST(NULL AS int)
INTO #fix
FROM #over
WHERE full_url LIKE N'%?xsdata=%' OR full_url LIKE N'%&xsdata=%';

UPDATE f
SET cleaned_len = LEN(CASE
        WHEN term = 0 THEN LEFT(full_url, sep - 1)
        WHEN sepChar = N'?' AND SUBSTRING(full_url, term, 1) = N'&' THEN LEFT(full_url, sep) + SUBSTRING(full_url, term + 1, 4000)
        WHEN sepChar = N'?' AND SUBSTRING(full_url, term, 1) = N'#' THEN LEFT(full_url, sep - 1) + SUBSTRING(full_url, term, 4000)
        ELSE LEFT(full_url, sep - 1) + SUBSTRING(full_url, term, 4000) END)
FROM #fix f
CROSS APPLY (SELECT sepChar = SUBSTRING(full_url, sep, 1),
                    endAmp  = CHARINDEX(N'&', full_url, sep + 8),
                    endHash = CHARINDEX(N'#', full_url, sep + 8)) a
CROSS APPLY (SELECT term = CASE WHEN a.endAmp = 0 AND a.endHash = 0 THEN 0
                               WHEN a.endAmp = 0 THEN a.endHash
                               WHEN a.endHash = 0 THEN a.endAmp
                               WHEN a.endAmp < a.endHash THEN a.endAmp
                               ELSE a.endHash END) b;

-- 3. Stuck rows = oversize rows STILL > 850 after removing xsdata (or with no xsdata at all).
--    Reduce them to their path (everything before the first '?' or '#'); paths are short, so they fit.
IF OBJECT_ID('tempdb..#stuck') IS NOT NULL DROP TABLE #stuck;
SELECT o.id, o.full_url,
       pathkey = CAST(CASE WHEN cut = 0 THEN o.full_url ELSE LEFT(o.full_url, cut - 1) END AS nvarchar(850))
INTO #stuck
FROM #over o
LEFT JOIN #fix f ON f.id = o.id
CROSS APPLY (SELECT qpos = CHARINDEX(N'?', o.full_url), hpos = CHARINDEX(N'#', o.full_url)) q
CROSS APPLY (SELECT cut = CASE WHEN qpos = 0 AND hpos = 0 THEN 0
                               WHEN qpos = 0 THEN hpos
                               WHEN hpos = 0 THEN qpos
                               WHEN qpos < hpos THEN qpos ELSE hpos END) c
WHERE f.id IS NULL OR f.cleaned_len > 850;

-- 4. Pick a canonical url row per path: an existing clean row that already holds that exact path,
--    otherwise the lowest-id stuck row.
IF OBJECT_ID('tempdb..#path') IS NOT NULL DROP TABLE #path;
SELECT DISTINCT pathkey INTO #path FROM #stuck;
ALTER TABLE #path ADD existing_id int NULL, canonical_id int NULL;
UPDATE p SET existing_id = (SELECT MIN(u.id) FROM dbo.urls u WHERE u.full_url = p.pathkey) FROM #path p;
UPDATE p SET canonical_id = COALESCE(existing_id, (SELECT MIN(s.id) FROM #stuck s WHERE s.pathkey = p.pathkey)) FROM #path p;

-- 5. Map every non-canonical stuck row -> its canonical row.
IF OBJECT_ID('tempdb..#map') IS NOT NULL DROP TABLE #map;
SELECT s.id AS dup_id, p.canonical_id
INTO #map
FROM #stuck s JOIN #path p ON p.pathkey = s.pathkey
WHERE s.id <> p.canonical_id;

-- 6. Repoint EVERY foreign key that references dbo.urls(id) from the duplicate rows to the canonical
--    row (schema-driven, so no referencing table is missed). Temp tables are visible to the batch.
DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'UPDATE p SET p.' + QUOTENAME(pc.name) + N' = m.canonical_id FROM '
    + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N' p '
    + N'JOIN #map m ON p.' + QUOTENAME(pc.name) + N' = m.dup_id;' + CHAR(13)
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables t ON t.object_id = fk.parent_object_id
JOIN sys.columns pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fkc.parent_column_id
WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.urls');
IF @sql <> N'' EXEC sys.sp_executesql @sql;

-- 7. Delete the now-unreferenced duplicate rows.
DELETE u FROM dbo.urls u JOIN #map m ON m.dup_id = u.id;
DECLARE @deleted int = @@ROWCOUNT;

-- 8. Set the canonical rows that are themselves stuck rows (no pre-existing clean row) to the path.
UPDATE u SET u.full_url = p.pathkey
FROM dbo.urls u JOIN #path p ON p.canonical_id = u.id
WHERE p.existing_id IS NULL;
DECLARE @reduced int = @@ROWCOUNT;

RAISERROR('Stuck rows reduced to path: %d ; duplicate rows merged away: %d', 0, 1, @reduced, @deleted) WITH NOWAIT;
SELECT remaining_oversize = COUNT(*) FROM dbo.urls WHERE LEN(full_url) > 850;
SELECT duplicate_full_urls = ISNULL((SELECT COUNT(*) FROM (SELECT full_url FROM dbo.urls GROUP BY full_url HAVING COUNT(*) > 1) d), 0);

DROP TABLE #over; DROP TABLE #fix; DROP TABLE #stuck; DROP TABLE #path; DROP TABLE #map;

-- VERIFY remaining_oversize = 0 and duplicate_full_urls = 0, then change ROLLBACK to COMMIT and re-run.
ROLLBACK TRANSACTION;
-- COMMIT TRANSACTION;
```

> **Edge case:** if a child table has a `UNIQUE (url_id, …)` constraint and both a duplicate and the
> canonical row already hold a matching child row, repointing would violate that unique key. Because
> the script runs under `SET XACT_ABORT ON` inside the transaction, such a clash rolls the whole
> thing back cleanly (nothing is lost) and reports the error — resolve those few child rows by hand
> (delete the redundant duplicate child rows) and re‑run. This is rare for SharePoint page URLs.

Re‑run the count query from step 1 to confirm `oversize_urls` is `0`, then re‑run the upgrade. The
migration is idempotent, so it simply continues once the data fits.
