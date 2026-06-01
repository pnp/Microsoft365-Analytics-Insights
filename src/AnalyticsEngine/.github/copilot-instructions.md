# Copilot Instructions

## Git workflow
- Never commit. Never push. Make file changes only.
- Wait for the user to explicitly say "commit", "commit and push", or similar before running any `git commit` / `git push`. "Commit and push" given for one change does not extend to subsequent changes — ask again each time.
- This applies to all branches, including `dev`, and to the sibling wiki repo at `V:\Repos\Microsoft365-Analytics-Insights.wiki`.

## Project Guidelines
- User prefers to keep the existing InsertBatch row-by-row implementation rather than replacing it with SqlBulkCopy.

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
- In production the schema is brought up to date explicitly by `DatabaseUpgrader.CheckDbUpgraded` (called from the WebJob bootstraps and the installer). Never assume `new AnalyticsEntitiesContext()` will run migrations at runtime.
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