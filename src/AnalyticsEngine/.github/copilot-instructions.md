# Copilot Instructions

## Sensitive & customer data handling (read first)
- **Never** put real customer/tenant/environment data — including anything obtained by analysing a database, external system, or "example"/sample data (DB names, tenant/org/agent GUIDs, agent/user names, URLs, paths, row counts, raw payloads) — into source, tests, commit messages, PRs, issues, screenshots, or documentation. Use synthetic substitutes (`Contoso`, zeroed GUIDs) and **always double-check the diff before committing/pushing**. See the repo-wide policy in [`.github/copilot-instructions.md`](../../../.github/copilot-instructions.md).

## Git workflow
- Never commit. Never push. Make file changes only.
- Wait for the user to explicitly say "commit", "commit and push", or similar before running any `git commit` / `git push`. "Commit and push" given for one change does not extend to subsequent changes — ask again each time.
- This applies to all branches, including `dev`, and to the sibling wiki repo at `V:\Repos\Microsoft365-Analytics-Insights.wiki`.

## Project Guidelines
- User prefers to keep the existing InsertBatch row-by-row implementation rather than replacing it with SqlBulkCopy.

## Character set support (Unicode / Greek)
- **Every data structure that can hold customer text MUST support the full Unicode range, including non-Latin scripts such as Greek.** SharePoint/OneDrive URLs, file names, titles, user/display names, search terms etc. routinely contain characters like `Καλημέρα κόσμε` (e.g. `https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.pdf`).
- In SQL Server / EF, this means **`nvarchar`, never `varchar`** for any column that stores text originating from a customer tenant (URLs, names, paths, free text). `varchar` is single-code-page and silently corrupts characters outside that code page to `?`. This applies to entity columns, staging/temp table columns, `SqlTypeOverride` values, `Create DB.sql`, and migration `ALTER COLUMN` statements.
- Indexing trade-off: the SQL Server non-clustered index-key limit is 1700 bytes. `nvarchar` is 2 bytes/char, so the widest indexable Unicode string column is `nvarchar(850)`. Prefer `nvarchar(850)` (not `varchar(1700)`) when a text column must be both indexed and Unicode-safe. See migration `ShrinkUrlsFullUrlColumn` / `UrlFullUrlNvarchar` and issue #122 for the canonical example (`dbo.urls.full_url`).
- When generating C#/JSON/serialization/test data, use real non-ASCII samples (e.g. the Greek URL above) so round-trip and truncation bugs surface in tests rather than in a customer tenant.

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
   - Validate the script before shipping: it must apply from the prior state, stamp `__MigrationHistory` (Model matching the predecessor), and be a **no-op on re-run**.
8. **Adding-a-migration checklist:**
   - The migration id/timestamp (`yyyyMMddHHmmssf`, 15 digits) must sort **after** the current latest migration.
   - Register all three files in `Common/Entities/Entities.csproj`: `.cs` as `<Compile>`, `.Designer.cs` as `<Compile>` with `<DependentUpon>`, `.resx` as `<EmbeddedResource>` with `<DependentUpon>`.
   - Add the manual SQL upgrade script (rule 7) and attach it to the release.
   - Update any test that hard-codes the latest migration id (e.g. `UrlFullUrlMigrationPipelineTests.LatestId`).
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
- Window menu → "Solution Tests Configuration" → "Autodetect from installer configuration" pulls SQL/FTP details from the deployed resources, then pops an **"Autodetect Complete"** MessageBox that must be dismissed *before* "Save" will register. SQL/FTP passwords are masked (`PasswordChar`), so the window is safe to screenshot.

### Capturing
- Capture the window region via `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` (tighter than `GetWindowRect`) + `Graphics.CopyFromScreen`. Raise the window first with `SetWindowPos(HWND_TOPMOST)` — more reliable than `SetForegroundWindow` alone.

### Safety
- **"Install/Upgrade" starts a real, irreversible Azure deployment immediately** — no confirmation prompt, and it re-saves the loaded config file first. **"Test Configuration" is read-only.** When a real (non-sanitized) config is loaded, do NOT screenshot the Credentials / Azure Config tabs (real secrets) — only the tests-config window (passwords masked), the test results, and the deploy-progress log are safe to capture.