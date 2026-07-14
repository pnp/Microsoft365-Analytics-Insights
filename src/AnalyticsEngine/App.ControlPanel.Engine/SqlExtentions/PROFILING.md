# The SQL `profiling` subsystem — contributor guide

> Audience: engineers working on the AnalyticsEngine solution.
> Scope: what the `profiling.*` SQL schema is, how it is installed and run, and what
> every object does. This is a *how-it-works* primer, not an admin/operator guide.

Everything described here lives in **one file**:
`App.ControlPanel.Engine/SqlExtentions/Profiling-03-CreateSchema.sql` (plus the two
Ola-Hallengren maintenance scripts `Profiling-01-CommandExecute.sql` and
`Profiling-02-IndexOptimize.sql`, which create `dbo.CommandExecute` / `dbo.IndexOptimize`).

---

## 1. What it is (in one paragraph)

The product imports **raw per-user, per-day Microsoft 365 activity/usage** from the Graph
usage reports and Copilot audit into `dbo.*_user_activity_log` tables (written by EF, see
§4). The `profiling` schema is a **weekly roll-up layer** on top of that raw data: a set of
stored procedures aggregate each ISO week (Mon–Sun) into three wide "weekly" tables that
downstream **business-intelligence dashboards** (Power BI, external to this repo) read. It is
a classic ELT batch: raw daily rows in → weekly aggregates out.

It is deliberately **decoupled** from the importer: it is plain T-SQL, installed as a
schema extension, and driven on a schedule by **Azure Automation runbooks**, not by the WebJob.

---

## 2. How it is installed

- The installer runs **every `*.sql` in `SqlExtentions/`** (they are embedded resources) as
  part of the database upgrade, **after** the EF Code-First migrations, in **alphabetical
  order** — so `Profiling-01` → `02` → `03`. See `SqlExtentions/readme.md` and
  `App.ControlPanel.Engine/DatabaseUpgrader.cs` (`CheckDbUpgraded`, custom-SQL step).
- The scripts are **idempotent**: each object is guarded with `IF OBJECT_ID(...) IS NULL`
  (create-if-absent for tables) or `DROP ... ; CREATE ...` (procs/views/functions), and
  columns are added with `IF NOT EXISTS (... sys.columns ...)` `ALTER TABLE ADD`. You can
  re-run the whole file safely on an existing database.
- `usp_...` procedures cannot contain `GO`; the installer splits a file on `GO` and executes
  each batch separately (a hack — see the readme). Keep `GO` only as batch separators.

### Legacy cleanup block (lines ~119–181)
The top of `Profiling-03` **drops** objects from earlier versions of this subsystem that no
longer exist: `usp_CompileDaily`, `usp_Version`, `udf_GetLimitDate`, `tvf_ActivitiesBetweenDates`,
`tvf_DuplicatedUsers`, `tvf_Version`, `usp_CompileWeek/Columns/Rows`, and the old
`profiling.Activities` / `profiling.ActivitiesDaily` tables. If you see these names referenced
anywhere, they are **historical** — the current pipeline does not use them.

---

## 3. How it is run (orchestration)

Three PowerShell **Azure Automation runbooks** live in
`WebJob.Office365ActivityImporter/AutomationPS/ProfilingJobs/`. The installer
(`ProfilingScriptsLocateTask` → `RunbookUploadTask`) uploads their bodies straight into the
customer's Automation account as runbook drafts (it deliberately avoids a storage account so
it works with public network access disabled).

| Runbook | What it does | Key call |
|---|---|---|
| **`Weekly.ps1`** | The actual aggregation. Opens SQL, `EXEC [profiling].[usp_CompileWeekly] @WeeksToKeep`, 3-hour command timeout. Non-zero return ⇒ `Write-Error`. | `usp_CompileWeekly` |
| **`Aggregation_Status.ps1`** | Monitoring only. Prints `profiling.*` + `*_activity_log` table row counts and the MIN/MAX dates of the three weekly tables. | 4 `SELECT`s |
| **`Database_Maintenance.ps1`** | Index/stats maintenance via Ola Hallengren `dbo.IndexOptimize`, scoped to `%.profiling.%` (weekly) or `%.dbo.%_activity_log.%` (activitylog). | `dbo.IndexOptimize` |

`@WeeksToKeep` and the SQL connection come from Automation variables/credentials.

---

## 4. Data flow

```mermaid
flowchart LR
  subgraph Graph/Audit import (C#, EF)
    A[Graph usage reports<br/>Copilot audit] -->|AbstractDailyActivityLoader<br/>DbSet.Add / SaveChangesAsync| B
  end
  subgraph dbo (raw, per user per day)
    B[teams/onedrive/outlook/sharepoint/yammer_user_activity_log<br/>teams_user_device_usage_log, platform_user_activity_log,<br/>yammer_device_activity_log, copilot_chats + audit_events]
  end
  subgraph profiling (weekly roll-up, this subsystem)
    B --> C[usp_CompileWeekly<br/>(Azure Automation, weekly)]
    C --> D[usp_CompileActivityWeek]
    C --> E[usp_CompileUsageWeek]
    D -->|usp_Upsert* → #ActivitiesStaging| F[ActivitiesWeekly &#40;long&#41;<br/>ActivitiesWeeklyColumns &#40;wide&#41;]
    E -->|usp_Upsert*Devices/M365Apps → #UsageStaging| G[UsageWeekly &#40;wide, BIT flags&#41;]
  end
  F --> H[External Power BI dashboards]
  G --> H
  F -. freshness only .-> I[ProfilingStatusAPIController<br/>Aggregation_Status.ps1]
  G -. freshness only .-> I
```

**Important:** the raw `dbo.*_user_activity_log` tables are written by **EF**
(`WebJob.Office365ActivityImporter.Engine/Graph/UsageReports/*` →
`AbstractDailyActivityLoader`), **not** by the `usp_Upsert*` procs. The `usp_Upsert*` procs
read those tables; they are internal helpers of the compile pipeline.

**Consumers:** nothing in *this repo* reads the aggregated values — only freshness
(MIN/MAX date, row counts) is read by `ProfilingStatusAPIController` (the SPA "Profiling" tab)
and `Aggregation_Status.ps1`. The metric data itself is consumed by **external BI**.

---

## 5. Object reference

### Output tables (the product of the subsystem)

| Table | Shape | Grain / PK | Notes |
|---|---|---|---|
| `profiling.ActivitiesWeekly` | **long / EAV** | `(user_id, MetricDate, Metric)` | One row per user × week × metric, `Sum INT`. `Metric VARCHAR(250)`. Indexed on `MetricDate`. |
| `profiling.ActivitiesWeeklyColumns` | **wide** | `(user_id, date)` | ~70 `BIGINT` metric columns (OneDrive/Email/SPO/Teams/Yammer/**Copilot** + per-Copilot-app). Same data as `ActivitiesWeekly`, pivoted. Grown over time via `ALTER TABLE ADD` blocks. |
| `profiling.UsageWeekly` | **wide, boolean** | `(user_id, date)` | ~50 `BIT` "did user use platform X this week" flags (Teams device, Office app×OS, Yammer device) + `Yammer Platform Count TINYINT`. |

`ActivitiesWeekly` and `ActivitiesWeeklyColumns` are two representations of the **same** data
(long vs wide); `UsageWeekly` is a separate boolean "platforms used" fact.

### Views
- `profiling.users` — the users "worth reporting on": enabled, has an Azure AD id, joined to
  their license count. Wraps `dbo.users` + `dbo.user_license_type_lookups`.
- `profiling.user_PostalCodes` — `DISTINCT postalcode` from `profiling.users`.

### Support objects
- `profiling.TraceLogs` (`Id, Datetime, Message`) + `profiling.usp_Trace(@Message, @p1..@p5)` —
  the pipeline's own log. `usp_Trace` uses `FORMATMESSAGE` and inserts one row. This is the
  **only** place compile progress/errors surface (see §6, error handling).
- `profiling.udf_GetMonday(@date)` — returns the Monday of `@date`'s week (week bucketing).
- `ut_*` **table types** (9) — schemas for the local table variables used inside `usp_Upsert*`
  (e.g. `ut_teams_user_activity_log`, `ut_copilot_activities`). Only used internally.

### Stored procedures

**Orchestrator**
- `usp_CompileWeekly(@WeeksToKeep, @All = 0)` — the entry point (called by `Weekly.ps1`):
  1. `@ThisWeeksMonday` = Monday of `GETDATE() - 4 days` (the 4 days allow for M365 report
     latency; the current partial week is never compiled).
  2. `@RetentionDate` = `@ThisWeeksMonday - @WeeksToKeep` weeks.
  3. Resume point `@LastDateInTables` = **MIN** of `MAX(date)` across the three weekly tables
     (so a lagging table pulls the whole run back to re-fill it). `NULL` (or `@All=1`) ⇒ start
     at `@RetentionDate`.
  4. `WHILE @ThisWeeksMonday > @Monday`: `usp_CompileActivityWeek @Monday` then
     `usp_CompileUsageWeek @Monday`, stepping forward 7 days.
  5. **Retention**: `DELETE` rows older than `@RetentionDate` from all three tables.

**Per-week compilers**
- `usp_CompileActivityWeek(@Monday)` — builds a temp `#ActivitiesStaging`, calls the six
  activity upserts (`usp_UpsertTeams/OneDrive/SharePoint/Outlook/Yammer/Copilot`, each
  `@Monday..@Sunday`), then `usp_CompileWeekActivityColumns` and/or `usp_CompileWeekActivityRows`.
  Guarded by `@ColumnsDone`/`@RowsDone` row-existence checks so each target is filled at most once.
- `usp_CompileUsageWeek(@Monday)` — builds `#UsageStaging`, calls the three device/app upserts
  (`usp_UpsertTeamsDevices/M365Apps/YammerDevices`), inserts into `UsageWeekly`. Guarded by a
  `@ColumnsDone` check.
- `usp_CompileWeekActivityColumns(@Monday)` — `INSERT ... SELECT` `#ActivitiesStaging` →
  `ActivitiesWeeklyColumns` (wide → wide).
- `usp_CompileWeekActivityRows(@Monday)` — `UNPIVOT`s `#ActivitiesStaging`'s ~58 metric columns
  into `ActivitiesWeekly` (wide → long), `GROUP BY user_id, Metric`.

**Upserts (staging populators)** — pattern: aggregate the source `dbo.*_activity_log` for
`@StartDate..@EndDate` (`SUM ... GROUP BY user_id`) into a local `ut_*` table variable, then
`UPDATE` matching rows in the staging temp table and `INSERT` the rest.
- Activity: `usp_UpsertTeams`, `usp_UpsertOneDrive`, `usp_UpsertSharePoint`, `usp_UpsertOutlook`,
  `usp_UpsertYammer`, `usp_UpsertCopilot`.
- Usage: `usp_UpsertTeamsDevices`, `usp_UpsertM365Apps`, `usp_UpsertYammerDevices`.

`usp_UpsertCopilot` is the most complex: it `PIVOT`s `dbo.copilot_chats` (joined to
`dbo.audit_events` for the timestamp) by `app_host` into per-app counts, folds
`bizchat + M365App → copilot_m365app` and `appchat + Office → copilot_office`, and separately
counts chats/files/meetings via `dbo.copilot_event_files` / `dbo.copilot_event_meetings`.

---

## 6. Behaviours a contributor must know

- **Week = Monday.** All bucketing is ISO-week via `udf_GetMonday`. Dates stored are the Monday.
- **4-day latency.** `usp_CompileWeekly` never compiles the current week; it stops at the Monday
  of `today-4`.
- **Insert-once, resume-forward.** A week is compiled only if its target table has **no** rows
  for that Monday. `usp_CompileWeekly` resumes from the least-advanced table. There is **no
  update path** — once a week is written it is never recompiled (see critique).
- **Retention** is enforced every run by the `DELETE`s at the end of `usp_CompileWeekly`
  (`@WeeksToKeep` weeks kept).
- **Error handling = trace-and-continue.** Every compile proc wraps its body in
  `BEGIN TRY ... BEGIN CATCH` that writes `ERROR_MESSAGE()` to `profiling.TraceLogs` and
  **does not re-raise**. So a failed week is logged but the run still returns success. The
  Profiling tab / `TraceLogs` are the only place failures show up.
- **Idempotent installer, idempotent compile** (via the row-existence guards). Re-running is safe.

---

## 7. How to run / test / observe

- **Run** (prod): the `Weekly` Automation runbook (or `EXEC profiling.usp_CompileWeekly @WeeksToKeep = 53`).
- **Test**: `Tests.UnitTests/ProfilingStoredProcedureTests.cs` seeds raw rows then calls
  `profiling.usp_CompileActivityWeek @monday` and asserts on the weekly tables (needs LocalDB).
- **Observe**:
  - SPA **Profiling tab** → `GET api/ProfilingStatus` (per-table MIN/MAX date, source freshness)
    and `GET api/ProfilingStatus/tracelogs` (paged `TraceLogs`, newest first).
  - `Aggregation_Status.ps1` for table sizes + date coverage.

---

## 8. Gotchas when changing it

- **Adding a metric column touches ~6 places consistently**: the `ALTER TABLE ADD` on
  `ActivitiesWeeklyColumns`, the `#ActivitiesStaging` temp definition, the owning `usp_Upsert*`
  (`ut_*` type + `INSERT`/`UPDATE` lists), the `usp_CompileWeekActivityColumns` insert+select
  lists, and the `usp_CompileWeekActivityRows` `UNPIVOT` list. Miss one and you get a silent
  gap or a swallowed error. There is no single source of truth for the column set.
- **New Copilot `app_host` values** must be added to the `PIVOT` list in `usp_UpsertCopilot`
  *and* the corresponding column plumbing above — unmapped hosts are silently dropped.
- Keep everything `nvarchar`-safe and don't reintroduce `GO` inside a procedure body.
- The `usp_Upsert*` procs reference the caller's temp table (`#ActivitiesStaging` /
  `#UsageStaging`) by name — they only work when executed inside the matching `usp_Compile*Week`.
