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

### 3. What's left after the clean‑up

`remaining_oversize` counts the rows the script could **not** auto‑fix:

- **Still > 850 after removing `xsdata`** — a few URLs are long because of *other* parameters (e.g.
  a huge `FilterValues1=…` list), not `xsdata`. Removing `xsdata` isn't enough; shorten or delete
  these by hand.
- **Cleaned value already exists** — the de‑duplicated URL is already tracked by another `urls`
  row, so the oversized duplicate was left untouched to avoid creating a duplicate key. Re‑point its
  `hits` / `event_meta_sharepoint` / Copilot rows (via `url_id`) at the existing row and delete the
  duplicate, or simply delete the oversized duplicate if its facts are redundant.

Re‑run the count query from step 1 to confirm `oversize_urls` is `0`, then re‑run the upgrade. The
migration is idempotent, so it simply continues once the data fits.
