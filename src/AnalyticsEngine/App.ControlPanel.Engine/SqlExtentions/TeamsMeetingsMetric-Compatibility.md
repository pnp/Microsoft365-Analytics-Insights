# `Teams Meetings` metric compatibility (issue #13)

Microsoft stopped populating the `meetingCount` property in the Graph
`getTeamsUserActivityUserDetail` report from February 2023:

- [`reportRoot: getTeamsUserActivityUserDetail`](https://learn.microsoft.com/en-us/graph/api/reportroot-getteamsuseractivityuserdetail)
- [Microsoft Teams activity reports - API changes](https://learn.microsoft.com/en-us/graph/teams-activity-reports)

Removing the corresponding `meetings_count` and `Teams Meetings` columns would break existing
customer Power BI models. The columns and report metric are therefore retained with a supported
meaning:

| Existing contract | Value from this change onward |
|---|---|
| `dbo.teams_user_activity_log.meetings_count` | Graph `meetingsAttendedCount` |
| `profiling.ActivitiesWeeklyColumns.[Teams Meetings]` | Weekly sum of `meetings_count` |
| `profiling.ActivitiesWeekly` row where `Metric = 'Teams Meetings'` | Weekly sum of `meetings_count` |

`meetingsAttendedCount` is the closest supported replacement for the old per-user total: it is the
number of meetings the user attended. It should not be interpreted as an organization-wide distinct
meeting count because the same meeting is counted once for every attendee.

The more specific, existing metrics remain available:

- `Teams Meetings Organized`
- `Teams Meetings Attended`
- Ad-hoc and scheduled one-time/recurring organized and attended counts

**Breaking schema change:** None. Existing Power BI models do not need to remove, rename, or remap
any columns. The installer includes a guarded repair that restores `Teams Meetings` only if an
earlier draft of the script removed it. Historical rows are not rewritten; newly imported data uses
the new meaning.
