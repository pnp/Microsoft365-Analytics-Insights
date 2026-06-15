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
anything** and prints the offending rows. Find them with:

```sql
SELECT id, LEN(full_url) AS length, full_url
FROM dbo.urls
WHERE LEN(full_url) > 850
ORDER BY length DESC;
```

Resolve each offending URL (and the `hits` / `event_meta_sharepoint` rows that reference it via
`url_id`) — e.g. delete the obsolete records or shorten the stored URL — then re‑run the upgrade.
The migration is idempotent, so re‑running after a fix simply continues.

> There is no longer a "not representable as varchar" abort: the target is `nvarchar`, which holds
> every Unicode character, so Greek (and any other script) is preserved rather than rejected.
