# Tests.FakeDataGen

Console host that produces real-looking test data for the Microsoft 365 Analytics
Insights database, and also stress-tests the import + SQL commit code paths.

The two capabilities share user/license/lookup seeding so generated data and
stress runs land in the same shape and can be re-run side-by-side.

## Usage

```
Tests.FakeDataGen.exe "<SQL Connection String>"
```

The connection string is optional. Options that need SQL will refuse to run
without one; stress tests that work in-memory still run.

The first time a menu option that needs the database runs in a session, the
host invokes `App.ControlPanel.Engine.DatabaseUpgrader.CheckDbUpgraded` against
the supplied connection string. This applies the Entity Framework migrations
and the custom SQL scripts under
`App.ControlPanel.Engine/SqlExtentions/` (including the profiling schema and
stored procedures) so generators and stress tests never run against a stale
schema. The upgrade is performed once per process; the in-memory
`ActivityAPIStressTest` skips it because it does not touch SQL.

When launched, an interactive menu is shown:

```
DATA GENERATION
  1. Generate fake Copilot activity
  2. Generate fake O365 audit activity
  3. Generate combined profiling data (O365 + Copilot)

STRESS TESTS
  4. ActivityAPI import stress test
  5. ActivityAPI import stress test (DB-backed, COLD+WARM)
  6. Copilot event import stress test
  7. Power Platform event import stress test
  8. Sent email importer stress test
  9. User activity data stress test (profiling SQL inputs)

  0. Exit
```

## Folder layout

```
Tests.FakeDataGen/
├── Program.cs                # menu + dispatcher
├── App.config                # EF + Azure binding redirects
├── Copilot/                  # realistic Copilot data generators
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

`Seeding` is intentionally shared: every data generator and stress test that
needs prerequisite metadata calls `UserMetadataSeeder` so the same lookup tables,
departments, license catalogue, etc. are populated everywhere. Users are made as
realistic as a live tenant: `SeedDataCatalogue` assigns each user a coherent geo
locale (country / state / city / office / usage location / postal code all agree,
across 21 countries incl. non-Latin values), a job title that fits its department,
a company, a realistic account-enabled state, a UPN on one of several tenant
domains, and a manager in their own company.

## Data generation

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

### Combined profiling data

The combined option prompts once for the event count, shared user count, and date
window, then generates both Copilot and O365 data with the same UTC window
endpoint. Copilot runs first so a new database gets one user population with the
requested Copilot-license distribution; the O365 generator reuses those users
while adding SharePoint, OneDrive, Outlook, and Teams profiling sources.

## Stress tests

Each stress test prompts for load parameters (event counts, batch sizes, GC
behaviour, verbosity) and reports:

- Items processed / throughput (items per second)
- Initial / peak / final memory
- Memory growth warnings (default > 50% growth from initial to final)
- Any errors or exceptions caught during the run

### Available stress tests

| # | Test | Purpose |
| - | ---- | ------- |
| 4 | `ActivityAPIStressTest` | Drives the ActivityAPI ingestion pipeline with fake loaders to detect leaks and benchmark the batch save path. |
| 5 | `ActivityApiDbStressTest` | Drives the real SQL persistence path through repeatable cold and warm scenarios. |
| 6 | `CopilotStressTest` | Exercises `CopilotAuditEventManager` at scale and validates the accessed-resources SQL path under load. |
| 7 | `PowerPlatformStressTest` | Exercises `PowerPlatformAuditEventManager` across the four Power Platform workloads (Power Apps, Power Automate, Power BI, Copilot Studio). |
| 8 | `SentEmailImporterStressTest` | Exercises sent-email persistence and sentiment-scoring boundaries with synthetic messages. |
| 9 | `UserActivityStressTest` | Bulk-loads the user + license + per-workload activity tables so the profiling SQL in `App.ControlPanel.Engine/SqlExtentions/Profiling-03-CreateSchema.sql` can be exercised against realistic volumes. After the seed, optionally invokes `[profiling].[usp_CompileWeekly]` to roll the daily rows into the weekly profiling tables straight away (the same proc that `WebJob.Office365ActivityImporter/AutomationPS/ProfilingJobs/Weekly.ps1` runs on schedule). |

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
