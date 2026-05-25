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

When launched, an interactive menu is shown:

```
DATA GENERATION
  1. Generate fake Copilot activity

STRESS TESTS
  2. ActivityAPI import stress test
  3. Copilot event import stress test
  4. Power Platform event import stress test
  5. User activity data stress test (profiling SQL inputs)

  0. Exit
```

## Folder layout

```
Tests.FakeDataGen/
├── Program.cs                # menu + dispatcher
├── App.config                # EF + Azure binding redirects
├── Copilot/                  # realistic Copilot data generators
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
departments, license catalogue, etc. are populated everywhere.

## Data generation

### Copilot activity

`CopilotActivityGenerator` inserts:

- License types (Copilot, E5, E3, Business Premium, Exchange Online) if missing.
- 10 test users with random departments, mail attributes, and license
  assignments (configurable Copilot percentage).
- Copilot chat events (configurable count) tagged with a mix of standard and
  custom agents, plus the matching `audit_events` + meeting / file metadata
  rows when applicable.

The generator confirms before writing if the target database already has data.

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
| 2 | `ActivityAPIStressTest` | Drives the ActivityAPI ingestion pipeline with fake loaders to detect leaks and benchmark the batch save path. |
| 3 | `CopilotStressTest` | Exercises `CopilotAuditEventManager` at scale and validates the accessed-resources SQL path under load. |
| 4 | `PowerPlatformStressTest` | Exercises `PowerPlatformAuditEventManager` across the five Power Platform workloads (Power Apps, Power Automate, Power BI, Copilot Studio, Dataverse). |
| 5 | `UserActivityStressTest` | Bulk-loads the user + license + per-workload activity tables so the profiling SQL in `App.ControlPanel.Engine/SqlExtentions/Profiling-03-CreateSchema.sql` can be exercised against realistic volumes. After the seed, optionally invokes `[profiling].[usp_CompileWeekly]` to roll the daily rows into the weekly profiling tables straight away (the same proc that `WebJob.Office365ActivityImporter/AutomationPS/ProfilingJobs/Weekly.ps1` runs on schedule). |

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
