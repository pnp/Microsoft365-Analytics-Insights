# Copilot Adoption scripts

## Compare-CopilotAdoptionWorkbooks.ps1

Compares two Copilot Adoption workbooks exported from the portal (the **Excel report** button on the
Copilot Adoption page) and lists what changed between them. The product keeps no history, so this is
how a baseline is compared with a later export.

It refuses to compare two files that cannot be compared fairly. Before it lists anything, it checks
that both files have:

| Check | Read from |
|---|---|
| The same product build | *Report* sheet, "Product build" |
| The same settings | *Settings* sheet. `fromUtc`, `toUtc` and `toExclusiveUtc` place the period in time and are not compared |
| The same reporting period length | *Report* sheet, "Period covered" |
| The same population | *Report* sheet: the title, the banner lines above the table and "Population", which show an email-domain narrowing and any people filter, including an administrator's |

If any of these differs, the script stops with exit code 1, names each failed check and shows both
values. `-Force` compares anyway, and reports the failed checks prominently.

It then lists every key on the *Snapshot facts* sheet that changed: the value before, the value after,
the change, and a note when a key exists in only one file (added or removed by a later build) or is
blank in one file. A blank is unknown, never zero. It never reads *Run diagnostics*, whose keys vary
from run to run.

```powershell
.\Compare-CopilotAdoptionWorkbooks.ps1 -Before .\copilot-adoption-28d-2026-01-05.xlsx -After .\copilot-adoption-28d-2026-04-05.xlsx

# A CSV for a board pack, UTF-8 with a byte-order mark so Excel opens it correctly
.\Compare-CopilotAdoptionWorkbooks.ps1 .\baseline.xlsx .\latest.xlsx -Format Csv -OutFile .\copilot-adoption-change.csv
```

`Get-Help .\Compare-CopilotAdoptionWorkbooks.ps1 -Full` describes every parameter and the exit codes.

**Requirements:** Windows PowerShell 5.1 or PowerShell 7. No modules and no Excel: the script reads
the `.xlsx` package directly. A script downloaded from the internet is blocked by the `RemoteSigned`
execution policy until you unblock it (`Unblock-File .\Compare-CopilotAdoptionWorkbooks.ps1`), or run
it with `powershell.exe -ExecutionPolicy Bypass -File .\Compare-CopilotAdoptionWorkbooks.ps1 ...`.

The procedure for taking a baseline, and what each key on *Snapshot facts* means, are in the wiki:
[Copilot Adoption Tool](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Copilot-Adoption-Tool).

Tested by `CopilotAdoptionWorkbookComparisonScriptTests` in `src/AnalyticsEngine/Tests.UnitTests`, which
builds real workbooks with the product's own writer and runs this script against them.
