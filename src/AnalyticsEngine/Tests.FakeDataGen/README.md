# Tests.FakeDataGen

Console host that produces real-looking test data for the Microsoft 365 Analytics
Insights database, and also stress-tests the import + SQL commit code paths.

The two capabilities share user/license/lookup seeding so generated data and
stress runs land in the same shape and can be re-run side-by-side.

## Usage

For a rounded demo rather than an importer stress test:

```
Tests.FakeDataGen.exe demo --database ContosoDemo_Example
Tests.FakeDataGen.exe demo --help
```

This non-interactive command creates a **new LocalDB-only** target, applies the
existing schema, and generates current overlapping licence assignments, daily
workload coverage (including explicit zero rows), Copilot adoption and official
D28 snapshots, metadata-only prompt/response pairs, SharePoint/web facts, detailed
Teams activity, sent email, Power Platform activity, and complete-week Power BI
profiles. Actual Power BI report views are separate from these profiling tables.
It never reads a configured production connection.
Exact completed reruns are read-only no-ops; other existing targets are refused.
`--preview` runs the same generator without SQL. Fix `--as-of` and `--seed` for
reproducibility. `--help` lists all flags and the deliberately unsupported datasets.
The operator guide lives in the wiki: [Synthetic demo data](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Synthetic-demo-data).

The same demo is the **first option on the interactive menu**, so it can be run
without knowing any of the flags - see
[Full synthetic demo](#full-synthetic-demo-contoso) below.

Each major area also has a menu entry offering a new database or additive
generation into an existing **test/demo** database. Supply the existing connection
when starting the menu:

```
Tests.FakeDataGen.exe "<SQL Connection String>"
```

The connection string is optional. Options that need SQL will refuse to run
without one; stress tests that work in-memory still run. The synthetic demo
option never uses it - it always creates its own new LocalDB database. The
individual-area entries default to a new target and require explicit confirmation
before appending to an existing one.

The first time a **legacy generator or database-backed stress test** runs, the
host invokes `App.ControlPanel.Engine.DatabaseUpgrader.CheckDbUpgraded` against
the supplied connection string. This applies the Entity Framework migrations
and the custom SQL scripts under
`App.ControlPanel.Engine/SqlExtentions/` (including the profiling schema and
stored procedures) so generators and stress tests never run against a stale
schema. The upgrade is performed once per process; the in-memory
`ActivityAPIStressTest` skips it because it does not touch SQL.

The new existing-database path does **not** run that upgrader: it checks the
schema before inserting, preserves existing users, remaps generated IDs and adds
a separate synthetic population on each run. No deletes, resets or automatic
cleanup are performed; committed batches remain after an interrupted append.

The menu has three sections: **Data Generation** (full demo followed by each
activity area, existing or new DB), **Legacy Generators**, and **Stress Tests**.
Area keys and display labels come from `DemoAreas.Catalogue`, which also drives
the command-line selector:

```text
Tests.FakeDataGen.exe demo --database ContosoDemo_Teams --areas teams
Tests.FakeDataGen.exe demo --database ContosoDemo_PowerPlatform --areas powerapps,powerautomate,powerbi,copilot-studio
Tests.FakeDataGen.exe append "<test-database-connection-string>" --areas powerbi --confirm-existing
```

`--areas all` is the default. Other keys are `directory`, `copilot`,
`copilot-history`, `outlook`, `sent-email`, `sharepoint`, `web`, `onedrive`,
`engage`, `office` and `dlp`. DLP includes prerequisite Copilot audit interactions
and preserves the blocked-versus-audit-only policy scenarios. Shared users/licences and dependent dimensions accompany
individual areas. Appends skip tenant-wide Copilot count snapshots and global
profile compilation rather than publishing partial-population totals as tenant
data. Use a full new demo for a self-contained profiled database.

## Folder layout

```
Tests.FakeDataGen/
├── Program.cs                # menu + dispatcher
├── App.config                # EF + Azure binding redirects
├── Copilot/                  # realistic Copilot data generators
│   └── SQL/                  # refusal-only compatibility stub for the retired shaper
├── Demo/                     # safe single-command + menu generator, calendar, plan and bounded SQL sink
├── Generation/               # shared synthetic activity helpers
├── Office365/                # O365 audit activity generator
├── Seeding/                  # shared user / license / lookup seed data
│   ├── SeedDataCatalogue.cs
│   └── UserMetadataSeeder.cs
└── StressTests/
    ├── BaseStressTest.cs
    ├── StressTestResult.cs
    ├── MemoryMonitor.cs
    ├── ActivityAPIStressTest.cs
    ├── CopilotStressTest.cs
    ├── PowerPlatformStressTest.cs
    ├── UserActivityStressTest.cs
    └── FakeLoaders/          # fakes only used by stress tests
```

`Seeding` is intentionally shared: legacy generators and stress tests call
`UserMetadataSeeder`; the new `demo` command uses the same `SeedDataCatalogue`
through its new-target bounded sink or additive, identity-remapping existing-target sink. Users are made as
realistic as a live tenant: `SeedDataCatalogue` assigns each user a coherent geo
locale (country / state / city / office / usage location / postal code all agree,
across 21 countries incl. non-Latin values), a job title that fits its department,
a company, a realistic account-enabled state, a UPN on one of several tenant
domains, and a manager in their own company.

## Data generation

### Full synthetic demo (Contoso)

Menu option 1 is the interactive front end for the `demo` command. It asks for
the values the flags carry - preview or a new database name, then optionally
population size, SKU count, history length, end date, seed, Copilot licence
percentage, activity mix, weekly profile compilation, SQL batch size and a JSON
summary path. Every question that shapes the data offers the command line's own
default, so pressing Enter through the prompts produces exactly the documented
`demo` data set. The only menu-invented default is the timestamped target name,
because the command line has no default target.

Before it starts, it prints a summary and the **equivalent command line**, which
records exactly what was chosen:

```
Equivalent command line: Tests.FakeDataGen.exe demo --database ContosoDemo_20260901_134530
  --as-of 2026-09-01 --users 1000 --skus 50 --days 180 --seed 42 --copilot-percent 60
  --mix 30,35,20,8,7 --batch-size 250
```

`--as-of` is always emitted explicitly, so the line still describes the same
window on a later day. To generate another copy from it, give `--database` (and
`--output`, if one was chosen) new names: a completed target is a read-only
no-op and an existing summary file is never overwritten. The default target name
is timestamped because demo targets are never reset.

The menu option only chooses flag values: `DemoCommand` still parses and
validates them, so the LocalDB-only rule, the `ContosoDemo_` name restriction,
the refusal of unmarked or changed targets and the read-only no-op on an
identical rerun apply exactly as they do on the command line. It ignores the
connection string the tool was started with.

#### Viewing it in the portal

A finished run prints the two settings a web application needs:

```
  connectionStrings   SPOInsightsEntities = Server=(localdb)\MSSQLLocalDB;Database=ContosoDemo_X;Integrated Security=True
  appSettings         ImportJobSettings = GraphUsersMetadata=True;GraphUsageReports=True;GraphCopilotUsageReports=True;Copilot=True;CopilotInteractionHistory=True;ActivityLog=True;WebTraffic=True;Calls=True;GraphTeams=True;SentEmails=True;ImportPowerPlatform=True;ImportDlp=True
```

**The portal decides which workloads it can measure from `ImportJobSettings`, not
from the rows in the database**, and every one of those flags is opt-in with a
default of `false`. Without `GraphCopilotUsageReports=True` the Licence
assignments report renders Copilot as *"Not measured"* for every user even though
the database is full of Copilot activity: the Copilot audit and interaction
sources are positive evidence only and never produce activity bands. No
generated data can change that, so the generator prints the setting instead.

It then runs the Licence assignments report's **own coverage query** against the
finished database and prints what the portal will be able to measure:

```
Default reporting period 2026-08-03 to 2026-08-30, licence "Contoso Demo Workplace" (40 users):
  teams      available        40 measured, 0 unknown
  ...
  copilot    available        24 measured, 16 unknown
```

Copilot unknowns are expected and correct: the official per-user report covers
Copilot-licensed users only. *Every* user unknown is the symptom to look for.

Two further things are worth knowing when a workload reads as unmeasured:

- The official Copilot report is a **rolling 28-day** snapshot, and nothing in
  this product imports a `report_period_days = 7` row, so Copilot licence bands
  are only produced for a **28-day** reporting period. Other periods report
  `missingCoverage`, which the portal shows as "Not measured". This applies to
  real tenants too, not just the demo.
- The demo warms its rolling Copilot counters up over the 28 days *before* the
  window starts, so the official report rows cover the whole generated window.
  Before that warm-up existed they began 27 days in, which left short-history
  demos (`--days 31` to `--days 35`) with no Copilot coverage at all while the
  M365 workloads still measured fine.

### Copilot activity

`CopilotActivityGenerator` inserts:

- License types (Copilot, E5, E3, Business Premium, Exchange Online) if missing.
- Test users with coherent, realistic metadata (country / state / city / office /
  usage location / postal code, department + fitting job title, company, account
  state) spread across several email domains and a manager hierarchy, plus license
  assignments (configurable Copilot percentage).
- Copilot chat events (configurable count) tagged with a mix of standard and
  custom agents, plus the matching `audit_events` + meeting / file metadata
  rows when applicable.

The generator confirms before writing if the target database already has data.

### O365 audit activity

`Office365ActivityGenerator` inserts a realistic weighted mix of:

- SharePoint and OneDrive file activity with reusable sites, webs, URLs, file
  metadata, and Unicode paths.
- Exchange mailbox activity with synthetic client, IP, and logon properties.
- Microsoft Entra ID sign-in and directory activity with authentication and
  result properties.
- Matching `audit_events` rows, operations, users, licenses, and workload-specific
  metadata rows.
- Daily SharePoint, OneDrive, Outlook, and Teams usage-report source rows so
  `[profiling].[usp_CompileWeekly]` produces non-zero weekly metrics from the
  generated activity. Entra ID does not have an `ActivitiesWeekly` metric.

Activity is spread across a configurable date window, weighted toward weekdays
and business hours, and saved in bounded batches so large runs do not retain the
entire generated data set in the EF change tracker.

`usp_CompileActivityWeek` deliberately skips weeks already present in
`profiling.ActivitiesWeekly` and `profiling.ActivitiesWeeklyColumns`. Generate
the source data before compiling, or clear/rebuild previously compiled fake-data
weeks before re-running the weekly procedure.

### Copilot prompt history (AI interaction history)

`CopilotInteractionHistoryGenerator` fills the per-turn interaction-history tables that the
Graph `getAllEnterpriseInteractions` import writes, so **interaction reports can be built and
measured without a real tenant**. It writes sessions, interactions, the five lookups, key-phrase
links, per-user watermarks and an import-log row.

It generates report *shape*, not uniform noise:

- **Real turns.** Every `userPrompt` is followed by an `aiResponse` sharing its `request_id`, which is
  what makes turn counts and prompt-to-response ratios meaningful. `response_latency_ms` is set on the
  response only - never the prompt - matching what the importer stores. Latency has a deliberate long
  tail so percentile reports have something to show.
- **Prompt-only enrichment.** Sentiment, language and key phrases land on `userPrompt` rows only,
  because the importer never scores Copilot's own output. A report that averaged sentiment across all
  rows would look correct against uniform data and be wrong in production.
- **Shared threads.** A configurable share of conversations exist under the same `session_ref` for two
  users - a Teams meeting Copilot session in more than one participant's history. That is why the
  sessions table is unique on (user, ref) rather than ref alone.
- **Skew.** App class, device and locale are weighted and turns-per-conversation varies, so "top N"
  reports have something to rank.
- **Unicode.** Locales and key phrases include Greek, so a truncation or collation bug surfaces here
  rather than in a customer tenant.

**No prompt or response text is generated or stored.** The real import keeps only counts, so this keeps
only counts; the sole free text is topical key phrases of the kind Azure AI Language returns.

Prompts for users, conversations per user, turns per conversation, window, enrichment percentage and
shared-thread percentage. Roughly `users x conversations x turns x 2` interaction rows.

### Combined profiling data

The combined option prompts once for the event count, shared user count, and date
window, then generates both Copilot and O365 data with the same UTC window
endpoint. Copilot runs first so a new database gets one user population with the
requested Copilot-license distribution; the O365 generator reuses those users
while adding SharePoint, OneDrive, Outlook, and Teams profiling sources.

### Shaping an existing demo database

`Copilot/SQL/ShapeCopilotAdoptionDemo.sql` is now a **refusal-only compatibility
stub**. Run the `demo` command against a new name instead. The new command reuses
`CopilotAdoptionPersonas` and the real adoption scorer instead of maintaining a
second scoring implementation in SQL. It adds the script's useful shaping to the
wider demo dataset without carrying forward its destructive rewrite path.
The legacy menu's random-volume generators remain available for their original
stress-testing purposes; they do not have the new command's target safeguards.

## Stress tests

Each stress test prompts for load parameters (event counts, batch sizes, GC
behaviour, verbosity) and reports:

- Items processed / throughput (items per second)
- Initial / peak / final memory
- Memory growth warnings (default > 50% growth from initial to final)
- Any errors or exceptions caught during the run

### Available stress tests

| Test | Purpose |
| ---- | ------- |
| `ActivityAPIStressTest` | Drives the ActivityAPI ingestion pipeline with fake loaders to detect leaks and benchmark the batch save path. |
| `ActivityApiDbStressTest` | Drives the real SQL persistence path through repeatable cold and warm scenarios. |
| `CopilotStressTest` | Exercises `CopilotAuditEventManager` at scale and validates the accessed-resources SQL path under load. |
| `CopilotAdoptionPerfTest` | Read-only before/after timing of the Copilot Adoption page's analysis against an existing database. |
| `PowerPlatformStressTest` | Exercises `PowerPlatformAuditEventManager` across Power Apps, Power Automate, Power BI and Copilot Studio. |
| `SentEmailImporterStressTest` | Exercises sent-email persistence and sentiment-scoring boundaries with synthetic messages. |
| `UserActivityStressTest` | Loads user/licence/daily workload rows and optionally compiles weekly profiling tables. |

### Adding a new stress test

1. Create a new class in `StressTests/` that inherits from `BaseStressTest`.
2. Implement `Execute()` to return a populated `StressTestResult`.
3. Register the test in the `MenuItems` list in `Program.cs`.

```csharp
public class MyNewStressTest : BaseStressTest
{
    protected override StressTestResult Execute()
    {
        var result = new StressTestResult { Success = true };
        // ... drive the system under test ...
        return result;
    }
}
```

## Best practices

- Start with low iteration / event counts to establish baselines, then scale up.
- Run each test multiple times to check for variance.
- For long runs, watch Task Manager / Resource Monitor in parallel to confirm
  memory and CPU behaviour matches what the test reports.
- Use the forced-GC option to confirm that growth is genuine retention, not
  delayed collection.

## Dependencies

- `WebJob.Office365ActivityImporter.Engine` - the system under test.
- `Tests.UnitTests` - reused fake entities and fake loader base classes.
- `Common.Entities` - entity models.
- `Common.DataUtils` - logging + batch helpers.
