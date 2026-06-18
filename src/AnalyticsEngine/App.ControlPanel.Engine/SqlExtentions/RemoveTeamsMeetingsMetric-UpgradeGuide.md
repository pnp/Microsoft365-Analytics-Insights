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

## What is *not* removed

Only the deprecated `meetingCount`‑derived metric is removed. The other Teams meeting metrics that
have live Graph sources remain unchanged:

- `Teams Meetings Attended` / `Teams Meetings Organized`
- `Teams Adhoc Meetings Attended` / `Teams Adhoc Meetings Organized`
- `Teams Scheduled One-time Meetings Attended` / `Teams Scheduled One-time Meetings Organized` (and recurring variants)

The source column `dbo.teams_user_activity_log.meetings_count` and the importer that populates it are
intentionally left untouched — only the now‑dead profiling plumbing
(`usp_UpsertTeams`, `usp_CompileWeekActivityColumns`, `usp_CompileWeekActivityRows`,
`#ActivitiesStaging` and the `ut_teams_user_activity_log` table type) is updated so the
INSERT/SELECT lists stay aligned.

## Applying the change

The block is part of `Profiling-03-CreateSchema.sql`, which the installer runs on every database
upgrade (UI or command‑line). No manual steps are required: the `IF EXISTS` guards make re‑running
the script safe whether or not the column / rows are still present.
