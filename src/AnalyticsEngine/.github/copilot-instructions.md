# Copilot Instructions

## Sensitive & customer data handling (read first)
- **Never** put real customer/tenant/environment data — including anything obtained by analysing a database, external system, or "example"/sample data (DB names, tenant/org/agent GUIDs, agent/user names, URLs, paths, row counts, raw payloads) — into source, tests, commit messages, PRs, issues, screenshots, or documentation. Use synthetic substitutes (`Contoso`, zeroed GUIDs) and **always double-check the diff before committing/pushing**. See the repo-wide policy in [`.github/copilot-instructions.md`](../../../.github/copilot-instructions.md).

## Git workflow
- Never commit. Never push. Make file changes only.
- Wait for the user to explicitly say "commit", "commit and push", or similar before running any `git commit` / `git push`. "Commit and push" given for one change does not extend to subsequent changes — ask again each time.
- This applies to all branches, including `dev`, and to the sibling wiki repo at `V:\Repos\Microsoft365-Analytics-Insights.wiki`.

## Project Guidelines
- User prefers to keep the existing InsertBatch row-by-row implementation rather than replacing it with SqlBulkCopy.

## Installer config schema — bump `CONFIG_VERSION` on every change
Whenever you change the **installer's saved config schema** — any add / remove / rename of a persisted property on `BaseSolutionInstallConfig`, `SolutionInstallConfig`, `TargetSolutionConfig` or `ImportTaskSettings` (a new import toggle, a new Azure-resource field, etc.) — you **must** bump `CONFIG_VERSION` in [`Common/Entities/Installer/BaseSolutionInstallConfig.cs`](../Common/Entities/Installer/BaseSolutionInstallConfig.cs). Use `Major.Minor.Patch`: **minor** for additive / back-compatible changes, **major** for breaking ones. Add a one-line entry to the `// History:` comment next to the constant describing what changed. This value becomes the `ConfigSchemaVersion` stamped into every saved `*.json` config, so keeping it in step with the schema is how config compatibility is reasoned about across upgrades. Do this in the **same** change that alters the schema — don't leave it to a follow-up.

## Character set support (Unicode / Greek)
- **Every data structure that can hold customer text MUST support the full Unicode range, including non-Latin scripts such as Greek.** SharePoint/OneDrive URLs, file names, titles, **display** names, department / job title / office / company / country, search terms etc. routinely contain characters like `Καλημέρα κόσμε` (e.g. `https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.pdf`).
- **Exception — `userPrincipalName` is ASCII by Entra policy, and is NOT an example of the rule above.** Entra restricts a UPN to `A-Z a-z 0-9 ' . - _ ! # ^ ~`, with accented characters explicitly disallowed; non-Latin names live in `displayName` ([docs](https://learn.microsoft.com/en-us/entra/identity/hybrid/connect/plan-connect-userprincipalname)). This is why `dbo.users.user_name` is `varchar(250)` and why that is **not** a bug — see #402 (closed as not-a-bug) and #414. Do not file "non-Latin UPNs are corrupted" issues, and do not widen `user_name` on that basis. Fields that merely *look* like UPNs but are **not schema-guaranteed to be Entra UPNs** — the Management Activity API's `UserId` (whose common schema also carries `app@sharepoint`, SIDs and GUIDs), Power Platform's `PrincipalName`, and the SharePoint comment/like author email the AI Tracker copies verbatim from `c.author.email` — are unvalidated, so rules that handle them must not *assume* ASCII, and a defensive non-ASCII sample in an **in-memory** rule test is fine. **But say where the value ends up.** All three are eventually inserted into `dbo.users.user_name` (`insert_activity_from_staging_table.sql`, `insert_power_app_share_events_from_staging_table.sql`, `PageUpdateManager`), which is `varchar(250)` — so a genuinely non-ASCII value is corrupted *at rest*, and such a test proves the rule is neutral, **not** that the value round-trips to the database. Never write a test that asserts a non-ASCII identifier survives that storage boundary. **Check how the specific workload consumes the value before classifying it either way**, because this has already been got wrong three times: `ParticipantEndpointDTO.UserEmailAddress` in the Teams call-records importer looks like free text but is `[JsonIgnore]` and is filled by `CallRecordDTO.GetEmailAddress` from Graph's `user.userPrincipalName`; the **Copilot** audit records' `UserId`, though it arrives on the same generic Management Activity envelope, is passed to `GraphFileMetadataLoader.GetSpoFileInfo` as `eventUpn` and on to `GetUserDriveAsync` / `GetUserIdFromUpn`; and conversely the page-comment author email is *not* an Entra UPN even though `PageUpdateManager` assigns it to a `User.UserPrincipalName` — the destination property does not validate the source.
- In SQL Server / EF, this means **`nvarchar`, never `varchar`** for any column that stores text originating from a customer tenant (URLs, names, paths, free text). `varchar` is single-code-page and silently corrupts characters outside that code page to `?`. This applies to entity columns, staging/temp table columns, `SqlTypeOverride` values, `Create DB.sql`, and migration `ALTER COLUMN` statements.
- Indexing trade-off: the SQL Server non-clustered index-key limit is 1700 bytes. `nvarchar` is 2 bytes/char, so the widest indexable Unicode string column is `nvarchar(850)`. Prefer `nvarchar(850)` (not `varchar(1700)`) when a text column must be both indexed and Unicode-safe. See migration `ShrinkUrlsFullUrlColumn` / `UrlFullUrlNvarchar` and issue #122 for the canonical example (`dbo.urls.full_url`).
- When generating C#/JSON/serialization/test data, use real non-ASCII samples (e.g. the Greek URL above) so round-trip and truncation bugs surface in tests rather than in a customer tenant. Put the sample on a field that is genuinely Unicode (a display name, department, file name or URL) — **not** on a UPN, per the exception above.
- **A Unicode test must cross the encoding boundary it claims to guard.** If a test creates its own scratch table, that table's column types must match production (`Create DB.sql` / the migrations / the EF annotations, e.g. `AbstractEFEntityWithName.Name` is `[MaxLength(100)]`); declaring `nvarchar` in the fixture while production is `varchar` makes the assertion self-fulfilling and proves nothing. `CopilotAdoptionSqlIntegrationTests.CreateUserTables` is the worked example — it used to declare `user_name nvarchar(400)` against a production `varchar(250)`.

## Performance baseline for new / epic features
For any new feature or epic work in this solution, **assume a tenant of ~200,000 users**. Flag performance concerns proactively in reviews and design — don't wait to be asked. In particular, any new code that touches importers, batch processing, EF queries, SQL merges or Graph paging must be evaluated against this scale.

Concrete anti-patterns that get expensive at 200k users:
1. **`.ToLower()` on an indexed EF column** (e.g. `db.users.Where(u => u.UserPrincipalName.ToLower() == upn)` or `.Where(u => list.Contains(u.UserPrincipalName.ToLower()))`). EF6 translates `.ToLower()` to a SQL `LOWER()` call, which makes the predicate non-SARGable and forces a table scan even when an index exists. SQL Server's default code-first collation (`Latin1_General_CI_AS`) is already case-insensitive, so just compare against the column directly: `.Where(u => list.Contains(u.UserPrincipalName))`. Same applies to `==` comparisons.
2. **`List<T>.Skip(i).Take(n).ToList()` inside a chunking loop**. `Skip()` on a list walks past `i` elements every call, so chunking N items in slices of K costs O(N²/K) iterations (~700M for 187k/25). Use `list.GetRange(i, Math.Min(K, list.Count - i))`.
3. **`.ToLower()` keys into a `StringComparer.OrdinalIgnoreCase` dictionary or HashSet**. The comparer already does case-insensitive matching; the `.ToLower()` allocates a new string per lookup. At 200k users x N lookups this is millions of unnecessary string allocations.
4. **Per-row EF queries inside a loop** (e.g. `foreach (var url in urls) { db.urls.Where(u => u.Url == url).SingleOrDefaultAsync(); }`). Batch with `Where(u => batch.Contains(u.Url)).ToListAsync()` in IN-clause-friendly chunks (~1000 elements is safe for SQL Server's 2100 parameter limit).
5. **Rebuilding a 200k-entry dictionary inside a per-SKU / per-batch loop**. Hoist the dictionary build to the outer scope and pass it in.
6. **Unbounded per-user Graph pulls**. A single noisy mailbox / dataset can dominate import time; add a per-entity cap with a "will resume next cycle" log.

When writing or reviewing such code, call this out in the PR description / review comment with a concrete cost estimate at 200k-user scale.

### Diagnosing a slow DB query / merge (measure before indexing)
Confirm the root cause from the plan / DMVs **before** adding an index or blaming fragmentation — and validate the fix at scale before shipping it to a customer:
- **Triage with the live request:** `sys.dm_exec_requests` showing **high `logical_reads` + ~0 `physical_reads` + `wait_type` NULL = a bad in-memory nested-loop plan** (repeated rescans) — a *plan/index* problem, **not** fragmentation (`sys.dm_db_index_physical_stats.avg_fragmentation_in_percent`) and **not** data skew (rows-per-key). Rule those two out with their own queries before doing an offline rebuild in a maintenance window.
- **Index shape for a full-tuple existence check** (e.g. the Copilot merge's `NOT EXISTS … INTERSECT` accessed-resource de-dup): it needs the **whole tuple as a composite KEY** to seek. A covering `INCLUDE` index only removes key lookups — it does **not** give the tuple seek (here it was measurably *worse*). But don't over-generalise that into "`INCLUDE` is bad": it is the right shape when the extra column is only *returned* rather than *matched* — see *When `INCLUDE` is right and when a composite KEY is right*. **Validate the chosen index with a synthetic-scale reproduction** (populate a replica table, `SET STATISTICS IO, TIME ON`, compare before/after) before shipping an offline index build to prod.
- **Use the built-in import diagnostics instead of guessing:** the per-step SQL profiler `dbo.copilot_merge_step_timings` (create the table to enable, drop to disable — zero overhead when absent; see `WebJob.Office365ActivityImporter.Engine/ActivityAPI/Copilot/SQL/copilot_merge_step_timings.sql`) and the importer's per-cycle `metadata breakdown` / `Copilot commit timing` save-timing traces (`ActivityImporter.cs`, `CopilotAuditEventManager.cs`) pinpoint the exact slow stage.

### Diagnosing a Copilot Adoption page that keeps returning 202

PR #412 added a privacy-safe `CopilotAdoptionLifecycle` event stream specifically for the case
where the database appears to finish but the browser keeps polling. Do not turn on broad debug or
EF SQL logging: request filters and SQL parameters can contain tenant data, and the lifecycle events
already expose the useful boundaries without it.

Collect these artifacts against the **same UTC window**:

1. The approximate UTC start/end time, selected reporting period, visible outcome, and a HAR captured
   without manually reloading the page.
2. The lifecycle export below from the deployment's Application Insights resource.
3. Azure SQL CPU, Data IO, Log IO and DTU percentages for that window. Use Query Store or
   `sys.dm_exec_requests` to establish server execution separately, but never paste query text,
   database names, identifiers, real row counts or raw plans into this public repository.

```kusto
let startUtc = datetime(2000-01-01T00:00:00Z); // replace out-of-band
let endUtc   = datetime(2000-01-01T00:30:00Z); // replace out-of-band
customEvents
| where timestamp between (startUtc .. endUtc)
| where name == "CopilotAdoptionLifecycle"
| extend RunId=tostring(customDimensions.RunId),
         InstanceId=tostring(customDimensions.InstanceId),
         Sequence=tolong(customMeasurements.Sequence),
         Stage=tostring(customDimensions.Stage),
         Step=tostring(customDimensions.Step),
         Query=tostring(customDimensions.Query),
         Outcome=tostring(customDimensions.Outcome),
         ExceptionType=tostring(customDimensions.ExceptionType),
         SyncContext=tostring(customDimensions.SynchronizationContext),
         ActiveOperations=tostring(customDimensions.ActiveOperations),
         ElapsedMs=tolong(customMeasurements.ElapsedMs),
         DurationMs=tolong(customMeasurements.DurationMs),
         WorkingSetMB=round(todouble(customMeasurements.ProcessWorkingSetBytes) / 1048576.0, 1),
         ManagedHeapMB=round(todouble(customMeasurements.ManagedHeapBytes) / 1048576.0, 1),
         Gen2Collections=toint(customMeasurements.Gen2Collections),
         AvailableWorkers=toint(customMeasurements.ThreadPoolAvailableWorkers),
         HeartbeatDriftMs=tolong(customMeasurements.HeartbeatDriftMs),
         DroppedEvents=toint(customMeasurements.DroppedEvents)
| project timestamp, RunId, InstanceId, Sequence, Stage, Step, Query,
          Outcome, ExceptionType, SyncContext, ActiveOperations,
          ElapsedMs, DurationMs, WorkingSetMB, ManagedHeapMB,
          Gen2Collections, AvailableWorkers, HeartbeatDriftMs, DroppedEvents
| order by RunId asc, Sequence asc
```

Interpret boundaries literally:

| Last evidence | What it proves |
|---|---|
| No `Started` event and an immediate page | Usually a 10-minute web-process result-cache hit; confirm Application Insights was otherwise receiving events |
| `QueryStarted` without `QueryCompleted` | EF's full `ToListAsync` boundary did not return: connection acquisition, SQL execution, TDS transfer and materialisation are still combined here; Query Store decides whether SQL itself finished |
| `QueryCompleted` without `StepCompleted` | SQL/EF returned; the C# projection for that step did not finish |
| All analysis steps complete without `ScoringCompleted` | Final in-memory summary scoring stalled or failed |
| `ServiceReturned` without `CachePublished` | The service returned, but cache publication did not complete |
| `CachePublished` without `CompletionTelemetryReturned` | The result was available to callers, but legacy completion telemetry did not return |
| Repeating `Heartbeat` | Read `ActiveOperations`, memory/GC, available threads, drift and `DroppedEvents`; heartbeat starts after 30 seconds, so a healthy fast run has none |
| `HostStopping` during a run | A graceful AppDomain shutdown interrupted it |
| A run stops without a terminal event and the next run has a new `InstanceId` | Treat as an abrupt AppDomain/process loss |

`ElapsedMs` is time since the run began. `DurationMs` is the named query/step/cache/telemetry
operation only. A completed analysis is cached in the web process for 10 minutes per normalised
period and licence-override shape; the in-flight task is also shared. Azure SQL's buffer/plan caches
are separate and can make a fresh analysis much faster even after the web application is redeployed.

## NuGet Package Management
- When NuGet packages are added or updated, always update binding redirects in both App.Template.config and App.config for all affected projects (including test projects). App.config is generated dynamically from App.Template.config at build time, so App.Template.config is the source of truth.
- When updating Azure SDK packages in this solution, be aware that CloudInstallEngine targets .NET Standard 2.0 while App.ControlPanel.Engine and test projects target .NET Framework 4.8. Upgrading packages in CloudInstallEngine can cause CS1705 assembly version mismatch errors in consuming .NET Framework projects that need matching direct PackageReference additions and binding redirect updates.

## Azure Cache for Redis
- For AAD/Entra ID token-based authentication with Azure Cache for Redis (classic), ensure the following:
  - Set `redisConfiguration.aad-enabled` to "true" via ARM API version 2023-05-01-preview or later.
  - Create a Redis Access Policy Assignment (e.g., "Data Owner") for the service principal.
- Note that the "Redis Cache Contributor" RBAC role is control-plane only and does NOT grant data-plane access.
- For Redis RBAC fallback in this codebase, use `ClientSecretCredential` with the runtime account from config (tenantId, clientId, clientSecret) — NOT `DefaultAzureCredential` or managed identity.
- The Azure.ResourceManager.Redis SDK version 1.1.0 uses an API version too old to support `aad-enabled` configuration. To set `aad-enabled` on Redis, use the ARM REST API directly with api-version 2023-08-01 or later instead of the SDK.

## EF6 migrations and `AnalyticsEntitiesContext`

### Default behaviour: the context does NOT apply migrations in Release builds
- `new AnalyticsEntitiesContext()` (the parameterless constructor) only auto-applies migrations when compiled in **DEBUG** (it sets `MigrateDatabaseToLatestVersion<...>`). In **Release** the initializer is `CreateDatabaseIfNotExists<>` — no migrations are run.
- In production the schema is brought up to date explicitly by `DatabaseUpgrader.CheckDbUpgraded` (called from the installer — `AnalyticsInstaller.exe --initdb` — and from `Tests.FakeDataGen`; the web-jobs do **not** call it). Never assume `new AnalyticsEntitiesContext()` will run migrations at runtime.
- The `// THIS IS BAD` comment on the DEBUG branch is intentional; the production path uses the explicit upgrader so test/dev failures aren't masked by silent auto-migrations.

### `AutomaticMigrationsEnabled = true` + an outdated snapshot ⇒ `AutomaticDataLossException`
- `Migrations/Configuration.cs` has `AutomaticMigrationsEnabled = true`. When the latest explicit migration's model snapshot (the gzipped EDMX inside the `.resx` Target) doesn't match the current C# entity model, EF auto-generates an on-the-fly migration. If that auto-migration would DROP a table or column, it throws `System.Data.Entity.Migrations.Infrastructure.AutomaticDataLossException`.
- This bites every time you remove an entity / `DbSet` / property / `[Table]` but forget to update the snapshot in the last migration's `.resx`.

### Rules of thumb when editing entities or migrations
1. **Always pair entity removals with a migration**. Removing a `DbSet<T>` / `[Table("...")]` / property without updating the EF model snapshot leaves every test / runtime startup throwing `AutomaticDataLossException`.
2. **The Target snapshot in `.resx` is the source of truth EF compares against at runtime**. It is gzipped + base64-encoded EDMX (CSDL + SSDL + Mapping). Editing the XML by hand only works if it ends up byte-identical to what EF would serialize — namespace prefixes, attribute order and whitespace all count. Hand-editing usually triggers an auto-migration anyway.
3. **The canonical way to refresh a `.resx` snapshot** is `Add-Migration -Force <Name>` in the Visual Studio Package Manager Console (with `Entities` as the project, `WebJob.Office365ActivityImporter` as the startup project). That re-scaffolds the `.cs`, `.Designer.cs` and `.resx` from the live entity model.
4. **For a one-off schema cleanup** (e.g. dropping orphan tables that were created by an older dev-only migration that we no longer want to ship), add a defensive follow-up migration that uses `IF OBJECT_ID(...) IS NOT NULL DROP TABLE [...]` so it is safe on fresh installs *and* on databases that already have the orphan tables.
5. **Tests fail at `AnalyticsEntitiesContext..ctor` with `AutomaticDataLossException`?** Don't enable `AutomaticMigrationDataLossAllowed = true` to make them pass — that masks the real problem and silently drops customer data. Instead, fix the snapshot so the current model matches the latest migration's Target, or add an explicit cleanup migration.
6. **Reproducing snapshot mismatches locally**: drop `(localdb)\MSSQLLocalDB::UnitTestingAnalytics` (the default test DB per `Tests.UnitTests/App.Debug.config`) to force a fresh migration replay; that's the fastest way to surface a snapshot/model mismatch before pushing.

### Prove every schema change improves performance BEFORE it is approved for stable

**Rule: no SQL schema change ships to a stable release without a measured before/after benchmark showing a positive impact.** This applies to every migration whose purpose is performance — new indexes, changed index shapes (key columns, `INCLUDE` lists), column narrowing, table/proc rewrites. "It should be faster" is not sufficient; a plausible-looking index can make things *worse*.

This is not hypothetical. Measured in this repo at synthetic scale on the Copilot accessed-resource de-dup (`NOT EXISTS … INTERSECT`):

| Index shape | Logical reads | Time |
|---|---|---|
| `chat_id`-only (before) | 8,374 | 375 ms |
| **+ covering `INCLUDE`** | **25,228** ⬆️ 3x worse | 137 ms |
| **+ full composite KEY** | **6,414** | **7 ms** ✅ |

The covering `INCLUDE` **tripled logical reads** — it would have shipped as an "optimisation" that made the hot path worse. `INCLUDE` columns only remove key lookups; they do **not** provide a seekable access path.

### When `INCLUDE` is right and when a composite KEY is right

The rule above is often mis-generalised into "`INCLUDE` is bad". It isn't — it just answers a different question. The distinction is **what the extra column is used for**:

- The extra column takes part in **matching** (it's in the `JOIN` / `NOT EXISTS` / `WHERE` equality that decides *which* rows qualify) → it must be a **KEY** column, or there is no seekable access path for it.
- The extra column is only **returned** (the predicate is fully satisfied by the key columns, and you just want to avoid a lookup) → **`INCLUDE`** is exactly right and cheaper than widening the key.

Both shapes are in the codebase, each measured:

| Migration | Query | Right shape | Why |
|---|---|---|---|
| `WidenCopilotAccessedResourceDedupIndex` (#287) | full-tuple `NOT EXISTS … INTERSECT` — every column is part of the match | composite **KEY** | `INCLUDE` made the extra columns residual predicates, so the optimiser abandoned the seek and hash-joined a full scan |
| `IndexCopilotInteractionsDedupWindow` (#294) | seek on `(session_id, created_utc)`, only `graph_interaction_id` returned | key + **`INCLUDE`** | the predicate is fully served by the key; `INCLUDE` just removes the lookup |

### Logical reads alone can choose the slower index

Measured on the #287 de-dup with a 20,000-row commit batch:

| Index shape | Logical reads | Elapsed | Plan |
|---|---|---|---|
| 6-key + 2 `INCLUDE` | **10,904** ⬅️ fewest reads | **521 ms** ⬅️ 5.5x slower | Index Scan + Hash Match |
| full 8-column composite KEY | 63,883 | **94 ms** ✅ | Index Seek |

The `INCLUDE` shape wins on reads by 6x and loses on wall-clock by 5.5x: one sequential scan of a compact index touches far fewer pages than 20,000 individual seeks, but the hash build costs far more time. **Had this been judged on logical reads alone — as the guidance above used to imply — the slower index would have shipped.** Read counts and elapsed time answer different questions; a shape is only proven when both are acceptable, at more than one batch size / selectivity.

Note also that the right answer flipped with **batch size**, not with the date window: at 500 rows every shape seeks and the numbers are identical. Vary whichever input actually changes the plan — for a merge that's the batch size, for a report query it's the date range.

**What "proven" means — required evidence:**
1. A **synthetic-scale reproduction**: replica tables populated to a realistic size for a ~200k-user tenant (millions of rows on the fact tables — `audit_events`, `hits`, the `*_user_activity_log` tables). Never benchmark against a customer database, and never paste real data or real row counts into the repo (see the sensitive-data policy).
2. **The real query**, taken from the code that motivated the change (e.g. the report queries in `Web/Controllers/ReportsAPIController.cs` or the merge SQL), not a simplified stand-in.
3. **Before/after `logical reads` AND elapsed time**, via `SET STATISTICS IO, TIME ON`, medians over several runs, discarding the first (cold) run and defeating plan caching (`OPTION (RECOMPILE)` / `DBCC FREEPROCCACHE`). **Measure both and judge on both.** Logical reads are the more stable signal — they don't move with machine load, and a reads regression is the early warning that wall-clock will regress under real concurrency — but reads *alone* will sometimes pick the wrong index outright. See *Logical reads alone can choose the slower index* below.
4. **The plan operator before and after** (seek vs scan) — proving *why* it got faster, not just that it did on the day.
5. **More than one window/selectivity.** Test both a narrow range (e.g. 30 days) and a wide one (e.g. 365 days). Regressions frequently appear only at one end, which is exactly how a fixed join hint or index shape can help one case and hurt the other.
6. **Index build time and storage overhead**, so the release notes can give admins an upgrade-window estimate as a function of table size, and so the added disk cost is a conscious decision.

**Record the numbers where they will be found again:** put the before/after table in the PR description **and** summarise it in the migration's XML doc comment (`IndexCopilotAccessedResourceLookups` / `CoverCopilotAccessedResourceDedup` are the reference examples — the latter documents "on a 500k-row chat the dedup dropped from 375 ms to 7 ms"). A migration that cannot point at its measurement is not ready for stable.

**A negative result is a good result.** If the measurement shows the change is neutral or harmful, do not ship it — change the index shape (composite KEY vs `INCLUDE`, different key order) and re-measure, or drop it. Shipping an unproven index costs every customer an offline index build on their largest table for no benefit, and index builds are not free to undo.

### Writing robust schema-change migrations (raw SQL: `ALTER COLUMN`, `CREATE INDEX`, backfills)

Canonical examples: `ShrinkUrlsFullUrlColumn` / `UrlFullUrlNvarchar` (`urls.full_url`, issue #122) and `IndexCopilotAccessedResourceLookups` (the Copilot accessed-resource lookup tables). Follow these when a migration changes the physical schema of a table that may be **huge** on a customer tenant.

1. **A raw-SQL migration that does NOT change the EF model can reuse the previous migration's snapshot.** If the migration only runs `Sql(...)` (narrow a column, add an index, backfill) and leaves the entity classes unchanged, copy the *previous* migration's `.resx` **verbatim** into the new migration's `.resx` — the `Target` base64 must be byte-identical. EF then sees `model == latest snapshot`, so it neither auto-migrates nor throws `AutomaticDataLossException`, and you don't need `Add-Migration` at all. (Sanity check: `((xml)newer.resx).Target == ((xml)prev.resx).Target`.) This is how `ShrinkUrlsFullUrlColumn` (SQL narrows the column while the model stays `nvarchar(max)`) works. Only regenerate the snapshot (via `Add-Migration`) when you actually change the entity model.
2. **Never index an `nvarchar(max)` / `varchar(max)` column.** LOB columns can't be a B-tree key, so any `JOIN` / `NOT EXISTS` / de-dup on them is a full scan that gets catastrophic at scale. Narrow to `nvarchar(850)` (Unicode-safe, = the 1700-byte index-key limit) + a non-clustered index. Trim the source values to the same width wherever they're written (e.g. `LEFT(value, 850)` in the merge SQL, or `StringUtils.EnsureUrlWithinLength`) so nothing over-width ever hits the narrowed column.
3. **Make every migration idempotent, guarded and resumable.** Guard each step (`IF OBJECT_ID(...) IS [NOT] NULL`, check the current column type via `sys.columns`/`sys.types`, check `sys.indexes` for the index) and **no-op if already applied**. Use `Sql(sql, suppressTransaction: true)` so each step commits independently — schema locks release promptly and a partial apply *converges on re-run* instead of rolling back hours of work. `Migrations/Configuration.cs` already sets `CommandTimeout = 0` so a multi-hour `ALTER`/index build won't time out. Emit live progress with `RAISERROR(@msg, 0, 1) WITH NOWAIT`.
4. **Pre-flight the data before destructive DDL.** Before narrowing a column, handle over-width rows *first* (so a failure leaves the DB unchanged under `suppressTransaction`): either **abort** with a message listing offenders (`ShrinkUrlsFullUrlColumn`) or **trim** them (`IndexCopilotAccessedResourceLookups`). Prefer trimming when a truncated value is still a valid key and blocking the customer's upgrade is the worse outcome.
5. **`ONLINE` operations: gate by edition AND wrap in a catchable offline fallback.**
   - `WITH (ONLINE = ON)` (non-blocking) is only supported on Enterprise (`SERVERPROPERTY('EngineEdition')` = 3), Azure SQL DB (5) and Azure SQL MI (8). Express / Standard / **LocalDB (dev) = 4** do NOT support it — gate the attempt on the edition.
   - Even on a capable edition a specific `ONLINE` op can be rejected (notably shrinking an `nvarchar(max)` LOB column). So **attempt `ONLINE`, and fall back to a normal offline op on any failure**; the offline retry re-surfaces a genuine (non-`ONLINE`) error rather than masking it.
   - **Gotcha:** the *"Online index operations can only be performed in Enterprise edition…"* error **aborts the batch and is NOT catchable by `TRY/CATCH`** for a plain statement — but **IS catchable when the statement runs via `EXEC sp_executesql`**. So issue `ONLINE` attempts through `sp_executesql` inside `TRY/CATCH`.
6. **`ALTER COLUMN` is an offline, table-rewriting, schema-locked operation unless `ONLINE` succeeds** — on a large table it blocks all access for its duration. Migrations run via the installer / `DatabaseUpgrader.CheckDbUpgraded` (not the web-jobs), so tell operators to run large upgrades in a **maintenance window with the importer stopped**. Index builds are non-blocking only when `ONLINE` succeeds.
7. **Ship a standalone manual SQL upgrade script with every release that has a schema migration.** Some customers/DBAs upgrade the database **by hand** (in a controlled maintenance window) instead of running the installer, so every schema migration must also be published as a runnable script and **attached to the GitHub release**. Put it next to the migration as `<migrationid>.manual.sql` (e.g. `202607101200001_IndexCopilotAccessedResourceLookups.manual.sql`) containing:
   - the migration's `Up` SQL **verbatim** (which is why the `Up_Sql` is kept as a `public const` — idempotent, guarded, edition-aware online/offline), then
   - a **guarded `__MigrationHistory` stamp** so EF (`DatabaseUpgrader` / `MigrateDatabaseToLatestVersion`) and the web-app Health page treat it as applied. When the migration reuses the previous snapshot (see rule 1), the stamp just **copies the predecessor's row** — `INSERT ... SELECT '<new id>', ContextKey, Model, ProductVersion FROM __MigrationHistory WHERE MigrationId = '<prev id>'` — because the `Model` blob is byte-identical; no need to embed it. Guard with `IF NOT EXISTS (... WHERE MigrationId = '<new id>')` and verify the predecessor row exists.
   - Validate the script before shipping: it must apply from the prior state, stamp `__MigrationHistory` (Model matching the predecessor), and be a **no-op on re-run**. If it backfills, also validate it **with a concurrent writer inserting rows into the target table** — that is the case customers actually hit (they do not all stop the importer), and it is the case that broke a customer upgrade; see the pre-stamp guard rule below.
   - **The `__MigrationHistory` stamp must be reached on every "already applied / nothing to do" path.** If the script's work is already done (index already exists, data already clean), fall through — or `GOTO` a stamp label — rather than `RETURN`ing before the stamp; otherwise a by-hand run against an already-up-to-date DB leaves the migration "pending". Only a genuine "prerequisite missing" guard may skip the stamp (structure the "already done" case as `IF/ELSE` that falls through, not an early `RETURN`).
   - **A pre-stamp guard may check SCHEMA, never DATA STATE.** Verifying the columns/indexes the migration creates actually exist is correct — that catches a batch that failed mid-run, which matters because sqlcmd/SSMS continue to the next batch after a severity-16 error. Refusing to stamp because *rows* are in an unexpected state is not, and has already broken a customer upgrade. **Concurrency will always beat a data-state guard**, so a "did the data end up perfect?" check is a test the script cannot reliably pass.
     - **The incident (do not repeat it).** `DenormaliseCopilotChatUserAndTime`'s manual script refused to stamp unless *zero* `copilot_chats` rows still had a NULL `time_stamp`. On a customer upgrade with the importer left running it aborted with `NOT stamped - the schema work did not complete: backfill incomplete (N repairable row(s) still NULL)` — where N was **a few hundred rows out of several million, far below 0.01%**, with the columns added, every pre-existing row backfilled and the index built ONLINE. The schema work had *completely succeeded*. Worse, because the manual scripts form a prerequisite chain, the unstamped migration also blocked the *next* script in the release, stranding the whole upgrade.
     - **Why those rows are unavoidable, not a bug:** `dbo.copilot_chats` is clustered on `event_id`, a GUID taken from the audit event and therefore effectively **random**. A batched backfill that walks the clustered key with an ascending watermark will have roughly a **50% chance of missing any row inserted while it runs**, because half of them land *below* the watermark that has already passed. Any migration backfilling a randomly-clustered table has this property.
     - **Note the asymmetry with the installer path.** EF writes the `__MigrationHistory` row itself after `Up()` returns, so the installer never had this failure — it left the same handful of rows NULL and the importer's self-healing repair fixed them, exactly as designed. A hand-written stamp guard is therefore *stricter than EF itself*, which should be the warning sign: **if the guard would reject a state the installer path treats as success, the guard is wrong.**
     - **Do this instead:** (a) hard-fail only on missing columns/indexes; (b) if the migration backfills, add a bounded **mop-up pass** after the index exists — `WHERE <col> IS NULL` becomes a cheap seek once the new index sorts NULLs first, so it catches the stragglers the watermark walk missed; (c) report any remainder as a **warning that still stamps**, naming the self-healing repair that will clear it. Never let a handful of concurrently-inserted rows block a release's migration chain.
8. **Adding-a-migration checklist:**
   - **A performance-motivated migration must carry its before/after benchmark** (see *Prove every schema change improves performance*) in the PR description and the migration's doc comment. No measurement = not approved for stable.
   - The migration id/timestamp (`yyyyMMddHHmmssf`, 15 digits) must sort **after** the current latest migration.
   - Register all three files in `Common/Entities/Entities.csproj`: `.cs` as `<Compile>`, `.Designer.cs` as `<Compile>` with `<DependentUpon>`, `.resx` as `<EmbeddedResource>` with `<DependentUpon>`.
   - Add the manual SQL upgrade script (rule 7) and attach it to the release.
   - **If the migration backfills data, check the manual script's stamp guard checks schema only, and that it has a mop-up pass.** A guard that requires the data to be perfect will fail on any tenant that upgrades without stopping the importer (rule 7).
   - Update any test that hard-codes the latest migration id (`UrlFullUrlMigrationPipelineTests.LatestId`). This is the easiest item on the list to miss — the class name mentions neither "migration id" nor the area you're working in, so a filtered local test run usually won't cover it. `test_dotnet (Release)` catches it, but only after you've pushed: `Assert.AreEqual failed. Expected:<...> Actual:<...>. DB should now be at the latest migration.`
   - Some tables are created **only by migrations**, not by `Common/Entities/Resources/Create DB.sql` — check which, and update `Create DB.sql` too if the table is defined there.
9. **C# verbatim-string gotcha:** SQL held in a C# `@"..."` string must **double every `"`** (or avoid them) — a single stray `"` (e.g. inside a SQL comment) silently terminates the string and produces confusing compile errors far from the real spot.

## Installer (App.ControlPanel) UI automation & screenshots

Hard-won lessons for driving / screenshotting the built installer (`AnalyticsInstaller.exe` — the `App.ControlPanel` WinForms app) with PowerShell + UI Automation. The app's UIA tree is **sparse** and many standard controls surface as pattern-less `Pane`s with no Invoke/Toggle/Value patterns, so prefer Win32 window messages over UIA patterns.

### Before launching
- **Clear Mark-of-the-Web first.** A freshly built/copied output folder carries MOTW on the exe + DLLs, so Defender SmartScreen ("Windows protected your PC") blocks the launch and *hangs* `Start-Process` on the modal warning. Run `Get-ChildItem <outputDir> -Recurse -File | Unblock-File` before launching.
- **The workstation must be UNLOCKED.** When the session is locked, `CopyFromScreen` (screenshots) and synthetic input (`mouse_event`/`keybd_event`/`SetForegroundWindow`) silently fail — only window messages (`SendMessage`/`BM_CLICK`/`WM_SETTEXT`) get through. `LogonUI.exe` can linger after unlock, so confirm with `CopyFromScreen`/`GetForegroundWindow`, not the LogonUI process.

### Navigating the UI
- **Welcome → wizard:** tick "I accept … at my own risk" then click "Install Solution" (`btnStartInstall`) to reveal the config tabs. This is *navigation, not an install*. The tab control's HWND already exists (hidden) on the Welcome screen, so detect wizard state with `IsWindowVisible`, not mere presence.
- **Menus need the keyboard, not synthetic mouse clicks.** `Alt`+mnemonic opens them: File = `Alt+F` then `O` ("&Open Configuration File"). "Window" / "Solution Tests Configuration" have no mnemonic — use `Alt`, arrow keys, `Enter`. Requires the window to be foreground (hence unlocked).
- **UIA `Invoke` on a menu item that opens a modal dialog BLOCKS / times out** until that dialog closes. Drive menus by keyboard, or run the Invoke in a background job while the main script handles the dialog.
- **Tabs are a native `SysTabControl32`.** Read the selected index with `TCM_GETCURSEL`; to switch reliably, click the first header to focus the strip then `Ctrl+Tab` (auto-scrolls and fires `SelectedIndexChanged`). There are 8 tabs by default; loading a config (or enabling Web/Audit import on Targets) reveals the 9th, **SharePoint**.

### Dialogs, fields, buttons
- **Owned windows are UIA *Descendants*, not RootElement children.** The open-config dialog (custom title **"Load Configuration Details"**, not "Open"), the **"Enter Password"** form, and every MessageBox live under the main window — find them with `TreeScope::Descendants` filtered by process id.
- **File-name field:** put the path on the clipboard and `Ctrl+V` into the focused field, then `Enter` (its inner edit isn't reliably reachable via UIA `SetValue`).
- **Password fields:** UIA `ValuePattern.SetValue` throws on them; use `WM_SETTEXT` to the textbox HWND, then submit via the AcceptButton (`Enter`) or `BM_CLICK`.
- **Buttons / checkboxes:** `BM_CLICK` via `SendMessage` to the control HWND (located by window text + class "BUTTON") is the most reliable click and ignores z-order/focus.
- **Modal-stack deadlock:** `SendMessage`/`BM_CLICK` to a control whose UI thread is in a *nested* modal loop HANGS. Only message the innermost **enabled** window. Unwind a modal stack with the keyboard (`Enter` = OK, `Esc` = Cancel) against the window whose `IsEnabled = true`.
- Sending one key sequence then a short retry loop can open the same modal **twice** — send once and poll patiently instead.

### Solution Tests Configuration
- Window menu → "Solution Tests Configuration" → "Autodetect from installer configuration" pulls SQL details from the deployed resources, then pops an **"Autodetect Complete"** MessageBox that must be dismissed *before* "Save" will register. The SQL password is masked (`PasswordChar`), so the window is safe to screenshot.

### Capturing
- Capture the window region via `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` (tighter than `GetWindowRect`) + `Graphics.CopyFromScreen`. Raise the window first with `SetWindowPos(HWND_TOPMOST)` — more reliable than `SetForegroundWindow` alone.

### Safety
- **"Install/Upgrade" starts a real, irreversible Azure deployment immediately** — no confirmation prompt, and it re-saves the loaded config file first. **"Test Configuration" is read-only.** When a real (non-sanitized) config is loaded, do NOT screenshot the Credentials / Azure Config tabs (real secrets) — only the tests-config window (passwords masked), the test results, and the deploy-progress log are safe to capture.