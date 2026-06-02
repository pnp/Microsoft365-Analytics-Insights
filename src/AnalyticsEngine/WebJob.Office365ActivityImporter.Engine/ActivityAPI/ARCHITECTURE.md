# Activity API – Import Pipeline Overview

This document describes how the Office 365 audit-log activity import works. It covers
the end-to-end flow, key classes, filtering logic, the staging→merge SQL pattern, and
operational guidance for extending or debugging the pipeline.

---

## High-Level Flow

```
Office 365 Management Activity API
         │
         ▼
┌─────────────────────────────────────┐
│ ActivitySubscriptionManager         │ ← ensures content-type subscriptions are active
│ ContentMetaDataLoader               │ ← fetches time-chunked summary URLs
│ ActivityReportWebLoader             │ ← downloads full JSON blobs from each URL
└─────────────────────────────────────┘
         │
         ▼  (per JSON array item)
┌─────────────────────────────────────┐
│ AuditLogContentDispatcher.Dispatch  │ ← routes workload → deserialiser + per-workload filter
└─────────────────────────────────────┘
         │
         ▼  AbstractAuditLogContent subclass
┌─────────────────────────────────────┐
│ ActivityReportSqlPersistenceManager │
│  1. _filterConfig.InScope(...)      │ ← SharePoint org-URL whitelist
│  2. _userGroupsCache.IsInFilter(...) │ ← optional user/group filter
│  3. Stage to temp table             │ ← AuditLogTempEntity → ${STAGING_TABLE_ACTIVITY}
│  4. Merge SQL                       │ ← Insert_Activity_from_Staging_Table
│  5. ProcessExtendedProperties(...)  │ ← per-workload metadata (SP metadata, Power Platform, Copilot)
└─────────────────────────────────────┘
```

---

## Key Classes

| Class | Responsibility |
|-------|---------------|
| `ActivityImporter<T>` | Abstract orchestrator: subscribe, fetch summaries, chunk, load full reports, save |
| `ActivityReportWebLoader` | Downloads JSON arrays from Management API content URIs |
| `AuditLogContentDispatcher` | Routes raw `JToken` + workload string to the correct `AbstractAuditLogContent` subclass; applies per-workload operation filters |
| `ActivityReportSqlPersistenceManager` | Applies global filters (org-URLs, user groups), stages into temp table, runs merge SQL, then invokes `ProcessExtendedProperties` per event |
| `PowerPlatformAuditEventManager` | Stages Power Apps / Automate / BI / Copilot Studio rows via `InsertBatch<T>` into workload-specific staging tables; commits via merge SQL scripts |
| `CopilotAuditEventManager` | Same pattern for M365 Copilot events (separate staging tables) |
| `SaveSession` | EF6-backed metadata persistence (SP sites/webs/pages/files, hit lookups) |

---

## Workload Dispatch (`AuditLogContentDispatcher`)

Located at `Loaders/AuditLogContentDispatcher.cs`. Single static `Dispatch()` method.

Each supported workload (`Constants.cs → ActivityImportConstants.WORKLOAD_*`) maps to:
- A deserialization target (e.g. `SharePointAuditLogContent`, `PowerBIAuditLogContent`).
- An optional **operation filter** that drops events before they reach the staging table.

### Supported Workloads

| Workload string | Content class | Filter |
|----------------|---------------|--------|
| `SharePoint` / `OneDrive` | `SharePointAuditLogContent` | Org-URL whitelist (at persistence layer) |
| `Exchange` | `ExchangeAuditLogContent` | None |
| `AzureActiveDirectory` | `AzureADAuditLogContent` | None |
| `MicrosoftStream` | `StreamAuditLogContent` | None |
| `Copilot` | `CopilotAuditLogContent` | None (custom parser from JSON string) |
| `PowerPlatform` (RecordType 256) | → `PowerPlatformAdminActivityRecordContent.ToWorkloadSpecificContent()` | Power Automate: FlowRunStarted only |
| `PowerApps` (legacy) | `PowerAppsAuditLogContent` | None |
| `MicrosoftFlow` (legacy) | `PowerAutomateAuditLogContent` | FlowRunStarted only |
| `PowerBI` | `PowerBIAuditLogContent` | ViewReport only |
| `MicrosoftCopilotStudio` | `CopilotStudioAuditLogContent` | None |

---

## Unified PowerPlatform Schema (RecordType 256)

Microsoft now delivers most Power Platform audit events under `Workload="PowerPlatform"` with
`RecordType=256`. Data arrives as OpenTelemetry-style key/value pairs inside a `PropertyCollection`
array (and duplicated as a `JsonPropertiesCollection` JSON string), NOT as top-level fields.

### Mapping Flow

```
PowerPlatformAdminActivityRecordContent (raw record)
    │
    ├── .PropertyCollection  (List<NameValuePair>)
    │       ├── powerplatform.analytics.resource.type  → route to subclass
    │       ├── powerplatform.analytics.resource.power_app.id / .display_name
    │       ├── powerplatform.analytics.resource.cloud_flow.id / .display_name
    │       ├── powerplatform.analytics.resource.environment.name / .id
    │       ├── user_agent.original
    │       └── ... (see Constants.cs → PowerPlatformProps)
    │
    └── .ToWorkloadSpecificContent(logger)
            │
            ├── resource.type == "PowerApp"  → PowerAppsAuditLogContent
            ├── resource.type == "CloudFlow" → PowerAutomateAuditLogContent
            └── otherwise → logs "unsupported resource type" and returns null
```

### What's NOT in the Unified Schema
- **AppType** (Canvas/Model-Driven/etc.) – not present in `LaunchPowerApp` events.
- **RecurrenceType** – not present in `FlowRunStarted` events.
- **ConnectionReferences** – only on publish/save events (which we don't persist).

These were removed in migration `202605201510051_RemovePowerAppTypesAndFlowRecurrenceTypes`.

---

## Filtering Layers

### 1. Workload Operation Filters (Dispatcher level)
Applied **before** any staging. Defined in:
- `PowerPlatformAuditLogFilter.ShouldPersistPowerAutomateOperation()` → FlowRunStarted only
- `ActivityImportConstants.PowerBIOps.IsSupported()` → ViewReport only
- `PowerPlatformOps.IsPowerAppShareOp()` → recognises share operation names

### 2. SharePoint Org-URL Whitelist (`AuditFilterConfig.cs`)
Applied at **persistence** stage (`ActivityReportSqlPersistenceManager.SaveToSqlAllTheThings`).
- Non-SharePoint content → always in scope (filter doesn't apply).
- SharePoint content without ObjectId → out of scope.
- SharePoint content with ObjectId → `OrgUrlConfigs.UrlInScope(SiteUrl, ObjectId)`.

### 3. User Groups Filter
Optional. Checks if the event's `UserId` belongs to configured Azure AD groups.

---

## Staging → Merge SQL Pattern

Most workloads follow this pattern:

1. **Stage**: `InsertBatch<T>` writes rows to a temp table (e.g. `##import_staging_power_app`
   in Release, `debug_import_staging_power_app` in Debug).
2. **Merge**: An embedded `.sql` resource script runs a series of numbered sections:
   - Upsert lookup tables (environments, flows, apps, workspaces, connectors, etc.)
   - Insert event metadata rows linking `audit_events.id` → workload-specific tables
   - Handle idempotency via `NOT EXISTS` / `LEFT JOIN ... WHERE target.id IS NULL`

### SQL Scripts (in `PowerPlatform/SQL/`)
| Script | Purpose |
|--------|---------|
| `insert_power_app_events_from_staging_table.sql` | Power Apps: environments → apps → client types → event_meta |
| `insert_power_app_share_events_from_staging_table.sql` | Power App share permissions |
| `insert_power_automate_events_from_staging_table.sql` | Power Automate: environments → flows → event_meta |
| `insert_power_automate_share_events_from_staging_table.sql` | Flow share permissions |
| `insert_power_bi_events_from_staging_table.sql` | Power BI: workspaces → reports → event_meta |
| `insert_copilot_studio_events_from_staging_table.sql` | Copilot Studio: bots → event_meta |

### SQL Conventions (Important)
- Dedupe lookup-table upserts by the **unique key only** (use `GROUP BY + MAX/MIN` for other
  columns). `SELECT DISTINCT key, name` breaks when name varies for the same key.
- Use the `InsertBatch` row-by-row implementation (project preference – do NOT replace with
  `SqlBulkCopy`).
- Staging table names use `##` prefix (session-scoped temp tables) in Release, `debug_` prefix
  in Debug for easier inspection.

---

## ProcessExtendedProperties (Per-Event Metadata)

After the base audit event is merged into `audit_events`, each `AbstractAuditLogContent`
subclass implements `ProcessExtendedProperties(SaveSession, CommonAuditEvent, ILogger)`:

- **SharePoint**: resolves SP site/web/page/file lookups, hit counts, etc.
- **Power Platform subclasses**: delegates to `PowerPlatformAuditEventManager` which stages
  into its own workload-specific temp tables and runs the corresponding merge SQL.
- **Copilot**: delegates to `CopilotAuditEventManager` similarly.

---

## Debugging & Trace

### Trace Dump
Set `AuditTraceConfig.TraceDirectory` (via `--traceAuditDir` CLI param) to dump every raw audit
JSON blob to disk as `audit_trace_{timestamp}_{guid}.json`. Useful for capturing real event
payloads to verify property names.

### Common Issues
| Symptom | Likely Cause |
|---------|-------------|
| "0 imported" but events exist | `SharePointOrgUrlsFilterConfig.InScope` returning false – check `org_urls` table has matching prefixes. Non-SP events must NOT be filtered by this. |
| "skipping record with unsupported resource type ''" | Unified PowerPlatform event with an unknown `powerplatform.analytics.resource.type` value. Add handling to `ToWorkloadSpecificContent`. |
| "skipping Power Automate event with non-run operation 'X'" | Expected – only `FlowRunStarted` is persisted. If a new run-like operation appears, add it to `PowerPlatformOps.FlowRunOps`. |
| Power BI events not saving | Only `ViewReport` is persisted. Other operations (Login, PublishReport, etc.) are intentionally dropped. |
| Duplicate key violations in merge SQL | Typically a staging batch that includes the same natural key with different attribute values. Fix: `GROUP BY natural_key` + `MAX(other_cols)` in the upsert SELECT. |

---

## EF6 Migrations

Located in `Common/Entities/Migrations/`. Key points:

- `Configuration.cs`: `AutomaticMigrationsEnabled = true` (EF6 auto-migrates the schema
  forward when a new `AnalyticsEntitiesContext` is created with `autoUpdate: true`).
- Each explicit migration has `.cs` (Up/Down), `.Designer.cs` (metadata), and `.resx`
  (compressed EDMX model snapshot).
- The `.resx` Target value is **gzip + base64 of a UTF-8 BOM + EDMX XML**. When manually
  creating a migration, the EDMX must have `encoding="utf-8"` (not utf-16) and include the
  BOM prefix (`EF BB BF`) before gzipping.
- The `__MigrationHistory` table now has a `CreatedOn` column (EF 6.5.x requirement). If
  running tests on an older LocalDB, you may need:  
  `ALTER TABLE __MigrationHistory ADD CreatedOn DATETIME NOT NULL DEFAULT GETUTCDATE()`

### Recent Migrations (newest first)
| ID | Name | Notes |
|----|------|-------|
| `202605201510051` | RemovePowerAppTypesAndFlowRecurrenceTypes | Drops dead lookup tables |
| `202605141410030` | PowerPlatformAuditLogging | Adds all Power Platform tables |
| `202512011404129` | CopilotExtendedDataAgentType | Adds `is_custom_agent` to copilot_agents |

---

## Extending the Pipeline

### Adding a New Workload
1. Define `WORKLOAD_*` constant in `Constants.cs`.
2. Create `*AuditLogContent` class in `Entities/Serialisation/` extending `AbstractAuditLogContent`.
3. Add a branch in `AuditLogContentDispatcher.Dispatch()`.
4. Create staging entity class in the appropriate `StagingClasses.cs`.
5. Create merge SQL in the relevant `SQL/` directory.
6. Wire up in `ProcessExtendedProperties` (either in the content class or via a manager).
7. Add EF entities + DbSets + a new migration if new tables are needed.
8. Add unit tests mirroring `PowerPlatformAuditEventManagerTests` style.

### Adding a New Operation to an Existing Workload
1. Add it to the relevant filter whitelist (e.g. `PowerPlatformOps.FlowRunOps`).
2. Verify the operation carries the required fields (capture a real JSON sample first).
3. Update the staging class if new columns are needed.
4. Update the merge SQL.

---

## Build & Test

```bash
# Build (VS 2022/18 MSBuild)
"C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" ^
  Tests.UnitTests\Tests.UnitTests.csproj /t:Restore /v:minimal
"C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe" ^
  Tests.UnitTests\Tests.UnitTests.csproj /t:Build /v:minimal /p:Configuration=Debug

# Run tests (vstest.console)
"C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE\Extensions\TestPlatform\vstest.console.exe" ^
  Tests.UnitTests\bin\Debug\Tests.UnitTests.dll ^
  /TestCaseFilter:"FullyQualifiedName~PowerPlatformAuditEventManagerTests|FullyQualifiedName~OrgURLsFilter"
```

Tests require a LocalDB instance with database `UnitTestingAnalytics` (connection string in
`Tests.UnitTests/App.config`). The test DB auto-migrates on first use (DEBUG build sets
`autoUpdate: true`).
