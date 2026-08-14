# Upgrade guide: remove the deprecated `Teams Meetings` profiling metric (issue #13)

**Script involved:** `Profiling-03-CreateSchema.sql`

**Schema change:** **Yes** – the `Teams Meetings` column is dropped from
`profiling.ActivitiesWeeklyColumns` (with its `df_teams_meetings` default constraint), the
`meetings_count` column is dropped from the `ut_teams_user_activity_log` table type, and any
already‑aggregated `profiling.ActivitiesWeekly` rows where `Metric = 'Teams Meetings'` are purged.
The removal blocks are idempotent and mirror the existing Yammer column‑removal pattern.

---

## ⚠️ Why this exists — Microsoft deprecated the source property

The `Teams Meetings` report metric was sourced from the Microsoft Graph
`getTeamsUserActivityUserDetail` report's **`meetingCount`** property. Microsoft **deprecated and
stopped populating `meetingCount`** in the Teams user activity user detail report (and its
downloadable equivalent) from **February 2023**. Since then the property has no longer returned a
meaningful value, so the metric surfaced in the profiling reports as misleading / always‑zero data.

Microsoft references:

- [`reportRoot: getTeamsUserActivityUserDetail`](https://learn.microsoft.com/en-us/graph/api/reportroot-getteamsuseractivityuserdetail) — the report API the value was read from.
- [Microsoft Teams activity reports — API changes](https://learn.microsoft.com/en-us/graph/teams-activity-reports) — change notice that `meetingCount` is removed from the report.

Because the upstream value is gone, keeping the metric only produces zero / inaccurate rows in
customer reports, so it is removed.

## Where to get the meeting count instead

We are **already importing the replacement** from the **same** Graph report. When Microsoft
deprecated the single aggregate `meetingCount`, they replaced it with granular per‑user meeting
counts. Those granular properties have been imported since March 2022 (migration
`202203091433062_UsageReportsEnhancements`) into `dbo.teams_user_activity_log` and are already
surfaced in the profiling report — so no new import is required.

Mapping from the deprecated property to what we already collect from
`getTeamsUserActivityUserDetail`:

| Deprecated | Replacement (already imported) | DB column (`teams_user_activity_log`) | Profiling metric |
|---|---|---|---|
| `meetingCount` (single combined total) | `meetingsAttendedCount` — meetings the user **attended** | `meetings_attended_count` | `Teams Meetings Attended` |
| | `meetingsOrganizedCount` — meetings the user **organized** | `meetings_organized_count` | `Teams Meetings Organized` |
| | `adHocMeetingsOrganizedCount` / `adHocMeetingsAttendedCount` | `adhoc_meetings_organized_count` / `adhoc_meetings_attended_count` | `Teams Adhoc Meetings Organized` / `Teams Adhoc Meetings Attended` |
| | `scheduledOneTimeMeetingsOrganizedCount` / `...AttendedCount` | `scheduled_onetime_meetings_organized_count` / `scheduled_onetime_meetings_attended_count` | `Teams Scheduled Onetime Meetings Organized` / `...Attended` |
| | `scheduledRecurringMeetingsOrganizedCount` / `...AttendedCount` | `scheduled_recurring_meetings_organized_count` / `scheduled_recurring_meetings_attended_count` | `Teams Scheduled Recurring Meetings Organized` / `...Attended` |

### Which one replaces the old number?

**Neither is a like‑for‑like replacement, and that matters.** `meetingCount` was a single combined
figure; Microsoft deliberately split it into *organized* vs *attended* because the two answer
different questions. Pick by intent:

| You want… | Use | Watch out for |
|---|---|---|
| Meetings the user **took part in** (closest to the old `meetingCount`) | **`Teams Meetings Attended`** | Counts the same meeting once per attendee, so it **double counts when summed across users** — fine per user, misleading as an org‑wide total |
| Meetings the user **ran / hosted** | **`Teams Meetings Organized`** | Excludes meetings the user merely joined, so it is **lower** than the old number |
| A distinct **org‑wide meeting count** | **`Teams Meetings Organized`** | Counts each meeting once (at its organizer), which is what makes it safe to sum |

⚠️ **Do not simply add Attended + Organized** to recreate the old total — a meeting a user both
organized and attended can be counted twice, so the sum is not meaningful.

ℹ️ You don't need to add up the ad‑hoc / scheduled one‑time / scheduled recurring breakdowns:
Graph already reports `meetingsOrganizedCount` and `meetingsAttendedCount` as the totals across
those three categories. The breakdowns are there if you want to split by meeting type.

## Where these numbers are surfaced today

| Surface | Status |
|---|---|
| `reports/Usage Analytics/Analytics_Report.pbit` and `Analytics_DataModel.pbit` | ✅ **Already expose `Teams Meetings Organized` and `Teams Meetings Attended`** — this is where to look after the upgrade |
| In‑app web **Reports** page | ➖ Surfaces no Teams meeting counts at all (it reports on audit activity, not the Graph usage reports) — nothing to change |
| `reports/Misc/TeamsActivityInsights.pbit` | ⚠️ Binds the **deprecated** `meetings_count` column **only**, so it shows zeros with no working alternative. Not covered by this change — needs a follow‑up to repoint it at `meetings_organized_count` / `meetings_attended_count` |
| `reports/Misc/Archive/Teams.pbit` | ⚠️ Same problem, but archived — no action |

## Report templates that MUST be updated with this change

The `.pbit` templates bind to `profiling.ActivitiesWeeklyColumns` directly, so dropping the column
breaks them unless they are updated in the same change:

| Template | Part | Effect if not updated |
|---|---|---|
| `Analytics_DataModel.pbit` | `DataModelSchema` | 🔴 **Refresh fails** |
| `Analytics_DataModel_base.pbit` | `DataModelSchema` | 🔴 **Refresh fails** |
| `Analytics_DataModel_base.pbit` | `Report/Layout` | 🟠 Visuals silently blank |
| `Analytics_Report.pbit` | `Report/Layout` | 🟠 Visuals silently blank |

The model table **`User Activities in Columns`** reads the altered table:

```
Source = Sql.Database(SQLServer, SQLDatabase, [CreateNavigationProperties = false]),
ActivitiesWeekly = Source{[Schema="profiling",Item="ActivitiesWeeklyColumns"]}[Data]
```

and carries `{ "name": "Teams Meetings", "sourceColumn": "Teams Meetings" }` plus Power Query that
references it explicitly — `{"Teams Meetings", each List.Sum([Teams Meetings]), type nullable Int64.Type}`.
Once the column is dropped that reference no longer resolves and the refresh errors.
`User Activities Totals` carries the same column.

The `Report/Layout` references are visual filters of the form `Metric = 'Teams Meetings'` against the
row‑based `profiling.ActivitiesWeekly`. Because this change also deletes those rows, the visuals
won't error — they render empty, which is harder to spot.

**Required edits (Power BI Desktop):**

1. `Analytics_DataModel.pbit` / `Analytics_DataModel_base.pbit` — remove the `Teams Meetings` column
   from `User Activities in Columns` and `User Activities Totals`, and drop it from the Power Query
   grouping step.
2. `Analytics_Report.pbit` / `Analytics_DataModel_base.pbit` — remove or repoint the visuals filtered
   on `Metric = 'Teams Meetings'`, per the table above.

Repointing is an improvement rather than a loss: the deprecated column has been all‑zeros since
February 2023, so those visuals currently show nothing useful.

## What is *not* removed

Only the deprecated `meetingCount`‑derived metric is removed. The other Teams meeting metrics that
have live Graph sources remain unchanged:

- `Teams Meetings Attended` / `Teams Meetings Organized`
- `Teams Adhoc Meetings Attended` / `Teams Adhoc Meetings Organized`
- `Teams Scheduled Onetime Meetings Attended` / `Teams Scheduled Onetime Meetings Organized` (and recurring variants)

The source column `dbo.teams_user_activity_log.meetings_count` and the importer that populates it are
intentionally left untouched — only the now‑dead profiling plumbing
(`usp_UpsertTeams`, `usp_CompileWeekActivityColumns`, `usp_CompileWeekActivityRows`,
`#ActivitiesStaging` and the `ut_teams_user_activity_log` table type) is updated so the
INSERT/SELECT lists stay aligned.

## Applying the change

The block is part of `Profiling-03-CreateSchema.sql`, which the installer runs on every database
upgrade (UI or command‑line). The `IF EXISTS` guards make re‑running the script safe whether or not
the column / rows are still present, so **no manual database steps are required**.

⚠️ **But the database is not the whole story.** Anyone using the Power BI templates must also take
the updated `.pbit` files (see *Report templates that MUST be updated with this change* above).
Upgrading the database without updating the templates leaves `Analytics_DataModel.pbit` /
`Analytics_DataModel_base.pbit` **failing to refresh**, because they bind to the dropped column.

Customers who have built their **own** reports on `profiling.ActivitiesWeeklyColumns` need to check
them for a `Teams Meetings` column reference and repoint it — see *Which one replaces the old
number?* for how to choose the replacement.
