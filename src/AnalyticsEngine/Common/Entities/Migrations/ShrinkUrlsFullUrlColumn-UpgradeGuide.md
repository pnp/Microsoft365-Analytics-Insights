# Upgrade guide: `ShrinkUrlsFullUrlColumn` migration

**Migration id:** `202606011710001_ShrinkUrlsFullUrlColumn`
**Schema change:** **Yes** – narrows `dbo.urls.full_url` from `(n)varchar(max)` to `varchar(1700)` and adds a non‑clustered index `IX_urls_full_url`.

## Why

`urls.full_url` is the join / de‑duplication key for the staging‑table merges
(`Migrate Hits Import into Hits.sql`, `Insert Activity from Staging Table.sql`), which all do
`JOIN urls ON urls.full_url = imports.url/object_id`. As an `(n)varchar(max)` LOB column it
**cannot be an index key**, so every one of those joins is forced into a full scan of the
`urls` table (the largest dimension table) with LOB string comparisons. 1700 bytes is the
SQL Server single‑column non‑clustered index‑key limit, so `varchar(1700)` is the widest
indexable URL column.

## What the migration does (in order)

1. **Skips** if `dbo.urls` / `full_url` are absent, or if it has already been applied
   (column already `varchar(1700)` **and** `IX_urls_full_url` exists). Idempotent and safe to
   re‑run.
2. **Pre‑flight data checks** (run *before* any schema change, so a failure leaves the DB
   untouched):
   - any `full_url` **longer than 1700 characters** (would be truncated), and
   - any `full_url` containing characters that **cannot be represented as `varchar`** in the
     column's code page (would be corrupted to `?`).

   If either check finds rows it **lists the offending `id` + `full_url`** (up to 50) and the
   exact diagnostic query, then **aborts**. See *"If the migration aborts"* below.
3. `ALTER COLUMN full_url varchar(1700) NOT NULL` – the slow step (rewrites every row).
4. `CREATE NONCLUSTERED INDEX IX_urls_full_url ON urls(full_url)` – `ONLINE = ON` on
   Enterprise / Azure SQL DB / Azure SQL MI, offline on other editions.

Progress (row counts, edition, online/offline, per‑step timings) is streamed live via
`RAISERROR ... WITH NOWAIT`; watch it in the SSMS / Azure Data Studio *Messages* tab or
SQL Profiler.

## How long will it take? (≈10,000,000 URLs)

These are **order‑of‑magnitude estimates** to size a maintenance window, not guaranteed
numbers. Actual time is dominated by your storage throughput / service tier and average URL
length. **Always validate on a restored copy of the customer database first** – the migration
logs the real per‑step durations, so a dry‑run on a copy gives you exact figures.

Assuming ~10M rows, average URL ~150 characters (~1.5 GB of URL data):

| Step | Work | Rough estimate @ 10M rows |
|------|------|---------------------------|
| Pre‑flight length + lossy scans | Two full scans reading the LOB data | ~1–5 min |
| `ALTER COLUMN` → `varchar(1700)` | **Size‑of‑data row rewrite, fully logged, holds a Sch‑M lock (offline)** | **~5–20 min** (dominant cost) |
| `CREATE INDEX` (≈1–2 GB B‑tree) | Sort + build; `ONLINE` on Enterprise/Azure | ~2–10 min |
| **Total** | | **~10–35 min** |

Scaling is roughly linear with row count / data size: a 50–100M‑row `urls` table can take
**well over an hour**, so plan a maintenance window accordingly.

### Resource requirements / pre‑deploy steps

- **Pause the importers** (App Insights + Activity WebJobs) during the upgrade. The
  `ALTER COLUMN` takes a schema‑modification lock on `urls`, which will block – and be blocked
  by – the staging merges.
- **Transaction log space:** `ALTER COLUMN` is fully logged; ensure free log space on the
  order of the table size (several GB for 10M rows), or more on a busy server. On Azure SQL
  make sure the tier has headroom.
- **Backups:** take a backup / snapshot before upgrading (standard practice for schema
  changes).
- `Configuration.CommandTimeout` is already `0` (infinite), so the migration will not time out
  even on a multi‑hour rewrite.
- The migration runs **outside** the EF migration transaction (`suppressTransaction: true`), so
  each step commits independently and a pre‑flight failure rolls back nothing (no change was
  made yet).

## If the migration aborts

If a customer DB has URLs longer than 1700 characters, or URLs with non‑code‑page characters,
the migration **aborts before changing anything** and prints the offending rows. The operator
must fix the data and re‑run the upgrade. To find them:

```sql
-- Too long:
SELECT id, LEN(full_url) AS length, full_url
FROM dbo.urls
WHERE LEN(full_url) > 1700
ORDER BY length DESC;

-- Not representable as varchar (lossy conversion):
SELECT id, full_url
FROM dbo.urls
WHERE full_url <> CONVERT(nvarchar(max), CONVERT(varchar(1700), full_url));
```

Resolve each offending URL (and the `hits` / `event_meta_sharepoint` rows that reference it via
`url_id`) – e.g. delete the obsolete records or correct the stored URL – then re‑run the
upgrade. The migration is idempotent, so re‑running after a fix simply continues.

## ⚠️ Follow‑up required to actually realise the performance gain

This migration makes `full_url` **indexable**, but on its own it does **not** speed up the
staging merges yet. The staging columns that are compared against `full_url` are generated as
`nvarchar(max)` (see `InsertBatchTypeFieldCache.cs`), e.g. `##import_staging_hit_imports.url`
and `##import_staging_event_lookups.object_id`. Comparing `varchar(1700)` against
`nvarchar(max)` makes SQL Server implicitly convert `urls.full_url` to `nvarchar`, which
**defeats the new index** (the predicate is no longer SARGable on the `urls` side).

To get the benefit, a follow‑up change must make the comparison types match – e.g. define the
relevant staging columns (and ideally the `Url.FullUrl` EF mapping) as `varchar`, so the join
seeks `IX_urls_full_url`. That is intentionally **not** part of this migration; it is a
separate, independently testable change.
