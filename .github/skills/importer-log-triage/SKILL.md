---
name: importer-log-triage
description: Triage a log export (Application Insights traces, exceptions and customEvents, or Kudu/App Service log text) from the Microsoft 365 Analytics Insights importers and web portal. Use when the user hands over logs, a KQL export or "the importer is slow / stuck / failing" and wants to know what is failing or running slowly, per area (user import, Teams, sent email, usage reports, audit and Copilot enrichment, calls, App Insights importer, agent costs, migrations, portal errors).
---

# Importer log triage

Goal: from a log export of **any** environment, say what is failing, what is slow and what is merely quiet, with the line that proves it.

This contract was read from the code after the S1-S10 logging fixes. If a line you expect is missing, check the build label first: the build may predate it. Grep the repository for the quoted prefix before concluding a line "should" exist. The prefix is the contract; wording after it can drift.

## Privacy

- An export contains personal data (UPNs, display names, Team and site names, user ids). **Treat it as private**: keep it out of the repository, PRs, issues, wiki pages and commit messages.
- In anything public, use synthetic values (`Contoso`, zeroed GUIDs, rounded counts). Quote the *shape* of a line, not the tenant's values.
- Never ask for secrets. The importers do not log tokens, `clientState` or skip tokens; if one appears in an export, say so and treat it as a defect.
- A value SQL Server quotes in an error (a duplicate key, a truncated or unconvertible value) is logged as `<redacted>`, in every trace and exception the importers and the web app send (#853): `The duplicate key value is (<redacted>)`, `Truncated value: <redacted>.`. The error number (`SqlErrorNumber`), the table and the column are kept. Pages are reported by URL id, never by address; look the id up in `dbo.urls`.

## 1. Where logs live

| Source | What it holds | Use it for |
|---|---|---|
| **Application Insights** `traces`, `exceptions`, `customEvents` | Everything, every host, every build. | The only complete source. Always ask for this. |
| Kudu `data/jobs/continuous/<job>/job_log.txt` | Console output; **keeps only the first ~100 lines of each process start**. | Startup crashes that never reached App Insights (SQL login failure, missing settings). Useless after startup. |
| App Service application logs (if enabled) | Console output. stdout lines are INFO, stderr lines are ERR. | Fallback when App Insights is not configured. |

Console lines look like `2026-01-02T03:04:05.678Z [WRN] [op 1a2b3c4d] message`. Level tokens: `DBG INF WRN ERR CRT`, and `EVT` for custom events. Error and Critical go to stderr.

### What to ask the user for

One KQL export for a stated UTC window (the failure plus about an hour before it), from **all three tables**:

```kusto
let startUtc = datetime(2000-01-01T00:00:00Z); // replace
let endUtc   = datetime(2000-01-01T06:00:00Z); // replace
union
  (traces       | extend kind = "trace"),
  (exceptions   | extend kind = "exception"),
  (customEvents | extend kind = "event")
| where timestamp between (startUtc .. endUtc)
| project timestamp, kind, severityLevel, name, message, type, operation_Id, operation_Name,
          cloud_RoleInstance, customDimensions, customMeasurements
| order by timestamp asc
```

`name` exists only on events, `message` on traces and exceptions; the union leaves the other blank. Also ask: which environment and build, whether the nightly Always-On policy applies, and for a portal error the `errorId` the admin saw.

### Severity (`severityLevel`)

0 Verbose, 1 Information, 2 Warning, 3 Error, 4 Critical. Contract: Warning = degraded but continuing; Error = a unit of work failed; Critical = a feature is down; Information is never a failure.

## 2. Orient first

Every telemetry item carries `customDimensions`: **`RunId`** (new per process start), **`ProcessStartedUtc`**, **`JobName`** (`Office365ActivityImporter` or `AppInsightsImporter`) and **`BuildLabel`**. Items sent before configuration was read carry only `RunId`.

1. **Builds and restarts.** Group by `JobName, BuildLabel, RunId`. Many short runs = crash loop; a gap between runs = host stopped.
2. **Startup.** Both importers check SQL with a read-only, 30-second probe (`hits` for web traffic, `audit_events` for activity). `SQL startup probe failed: table=..; ...` reports an outage. Continuous jobs then log `STARTUP-SQL-CHECK Failed: job=..; attempt=..` (Critical) and back off until `Recovered` or `Abandoned`; an explicit App Insights one-shot instead exits nonzero after its first failure. A Kudu stop interrupts an active probe or retry wait and is not a SQL outage. `FATAL <job>: unhandled exception in <source>; the process is stopping.` is Critical, with the exception chain; `Shutdown requested by host ...` indicates a graceful stop (Kudu stop, restart or deploy); `Unobserved task exception in <job>` identifies an unobserved background failure.
3. **Heartbeat.** Event `ImporterHeartbeat` about every minute with `JobName, LastCycleUtc, LastCycleDurationSeconds, ProcessStartedUtc, BuildLabel, CurrentStage, StageStartedUtc, BackgroundStage, BackgroundStageStartedUtc`. It proves the process is alive, **not** that it made progress: no `LastCycleUtc` means no cycle has finished in this process yet.
   - A heartbeat gap means the process was not running or telemetry did not flush.
   - **Dev environments often have Always On disabled nightly by policy**, so an overnight gap there is expected. Ask before reporting it.
4. **Where it hung.** The last heartbeat's `CurrentStage` + `StageStartedUtc` is what the process was doing; compare to the last timestamp in the export. `BackgroundStage` is the usage-report import running off the main loop.
5. **Stage outcomes.** Trace `ImportStage start: Stage=<s>; Slot=<Cycle|Background>.`, then event `ImportStage` with dimensions `Stage, Slot, Outcome (Succeeded|Skipped|Failed), Reason, ExceptionType, SqlErrorNumber, HttpStatus` and measurement `DurationMs`. A failed stage also writes Error `ImportStage finish: Stage=<s>; Outcome=Failed; DurationMs=<n>; ...; Reason=<r>.` A `start` with no matching event = still running, or the process died inside it. Stage names include `CallQueueWebhook`, `GraphMainPass`, `GraphDeferredPass`, `ActivityImport`, `RepairDenormalisedCopilotColumns`, `RepairSplitCopilotAgents`, `AgentCosts`, `CopilotCosts`, `LeadershipCohort`, `ConcealedIdentityCleanup`, `Stats`, `AppInsightsImport`, plus the Graph section names.
   - `Outcome=Skipped`, `Reason=HostShutdown` during a user SKU read or `AppInsightsImport` means the host interrupted the cycle. Expect no finished-cycle event; it is not a Graph or App Insights outage. A checkpoint already committed stays committed (see §3.7).
6. **Cycle durations.** Event `FinishedSectionImport` (dimension `OperationName`, measurement `ElapsedSeconds`) and trace `<OperationName>: N hours, N mins, and N seconds.`; heartbeat `LastCycleDurationSeconds`.
7. **Exceptions** carry `ExceptionType`, `RootExceptionType`, `SqlErrorNumber`, `HttpStatus` in `customDimensions`. An exception object is sent once; the outcome trace holds the context.

### Ready-to-run: orientation

```kusto
// Runs, builds and how long each lived
union traces, customEvents, exceptions
| where timestamp between (startUtc .. endUtc)
| extend Job=tostring(customDimensions.JobName), Build=tostring(customDimensions.BuildLabel), Run=tostring(customDimensions.RunId)
| summarize firstSeen=min(timestamp), lastSeen=max(timestamp), items=count(), errors=countif(severityLevel >= 3) by Job, Build, Run
| order by firstSeen asc
```

```kusto
// Heartbeat gaps over 5 minutes
customEvents
| where timestamp between (startUtc .. endUtc) and name == "ImporterHeartbeat"
| extend Job=tostring(customDimensions.JobName)
| order by Job asc, timestamp asc
| extend prevTs=prev(timestamp), prevJob=prev(Job)
| where Job == prevJob and timestamp - prevTs > 5m
| project Job, gapStart=prevTs, gapEnd=timestamp, gap=timestamp - prevTs
```

```kusto
// What each run was doing at its last heartbeat
customEvents
| where timestamp between (startUtc .. endUtc) and name == "ImporterHeartbeat"
| extend Job=tostring(customDimensions.JobName), Run=tostring(customDimensions.RunId),
         Stage=tostring(customDimensions.CurrentStage), StageSince=todatetime(customDimensions.StageStartedUtc),
         Background=tostring(customDimensions.BackgroundStage), LastCycle=todatetime(customDimensions.LastCycleUtc)
| summarize arg_max(timestamp, Stage, StageSince, Background, LastCycle) by Job, Run
| extend stageAge=timestamp - StageSince
| order by timestamp desc
```

```kusto
// Stage outcomes and durations
customEvents
| where timestamp between (startUtc .. endUtc) and name == "ImportStage"
| extend Stage=tostring(customDimensions.Stage), Outcome=tostring(customDimensions.Outcome), DurationMs=todouble(customMeasurements.DurationMs)
| summarize runs=count(), failed=countif(Outcome == "Failed"), skipped=countif(Outcome == "Skipped"),
            p50=percentile(DurationMs, 50), p95=percentile(DurationMs, 95), worst=max(DurationMs) by Stage
| order by p95 desc
```

### Ready-to-run: what's slow

```kusto
// Stages over five minutes, with the failure classification
customEvents
| where timestamp between (startUtc .. endUtc) and name == "ImportStage"
| extend DurationMin=round(todouble(customMeasurements.DurationMs) / 60000.0, 1)
| where DurationMin > 5
| project timestamp, Stage=tostring(customDimensions.Stage), Outcome=tostring(customDimensions.Outcome), DurationMin,
          Reason=tostring(customDimensions.Reason), Exc=tostring(customDimensions.ExceptionType)
| order by DurationMin desc
```

```kusto
// Section durations
customEvents
| where timestamp between (startUtc .. endUtc) and name == "FinishedSectionImport"
| summarize runs=count(), avgSec=avg(todouble(customMeasurements.ElapsedSeconds)), maxSec=max(todouble(customMeasurements.ElapsedSeconds))
    by Section=tostring(customDimensions.OperationName)
| order by maxSec desc
```

Area timing lines (`SentEmailChunk` `*Ms`, audit `save-phase timing` / `metadata breakdown`, `Usage report phase` per-report ms, `TeamsCrawlSummary elapsedMs`, App Insights `FetchSeconds`/`SaveSeconds`) are in section 3.

## 3. Per area

Each area lists key lines (`{}` are values), what healthy looks like, a decision table and a query. Lines are `traces` unless marked event.

### 3.1 User import and identity reconciliation

Prefix **`User import - `** (activity job, stage `GraphMainPass`).

- Read: `User import - read {n} users (all) from Graph API` (full) or `... updated users from Graph API, using last delta.`; `loaded {n} users from database`; `{n} of the {m} users read are in UserGroupsFilter; only they are written.`
- Outcome: `User import - complete. Inserted {n} new users, updated metadata for {n} existing users (from {n} Graph users)`; `User import - metadata only (licence refresh still due)` means the scheduled app saved metadata without refreshing licences.
- Checkpoint (Warning): `NOT moving the user checkpoint forward: the Graph /users/delta read did not reach its end (no @odata.deltaLink)`; `NOT committing the Graph delta token: at least one import phase did not complete`; `the /users/delta read stopped on page {n} ({failure}) after {n} user(s)`; `Microsoft Graph rejected the stored /users/delta token`; `the stored /users/delta checkpoint was cleared while this import was running`.
- Checkpoint store (the `Storage` table): Warning `User delta token store {read|write|delete} attempt {n}/{max} failed ({type}); retrying.`; Warning `User delta token store read failed after {n} attempt(s); using the checkpoint this import cycle already read or committed.`; or the import is deferred: `User delta token store is unavailable and this import cycle has not read or committed a checkpoint. Deferring user metadata/licence import ...`. The fallback is per cycle (a new provider each cycle), so a store that can't be read when a cycle starts defers that cycle's user import; older builds' text said "for this WebJob process", which was never true.
- Identity: `identity reconciliation: {n} renamed in place (...), {n} placeholder row(s) adopted (...), {n} obsolete sign-in name(s) released atomically (...), {n} quarantined, {n} retried from earlier cycles, {n} resolved, {n} dropped (...), in {ms} ms.` (Warning when anything is quarantined or the store was unavailable); `identity quarantine: {n} user(s) quarantined, oldest since {u}, {n} retried, {n} resolved` (Warning while any remain); Errors `identity quarantine: could not save ...`, `... stored set could not be read ...`, `... more than the {max} the state store keeps`.
- A released obsolete name requires three direct answers: a fresh enabled-current-owner read, a previous-object-ID 404 with `Request_ResourceNotFound` or `ResourceNotFound`, and a successful bounded directory query corroborating exclusive current ownership and previous-object absence. A bare 404, an unreadable error or a redirect leaves the name quarantined. A prior deletion event or complete-directory omission never bypasses the previous-object read: the object may have been restored and directory lists can lag. The previous row keeps its PK, Entra ID and history under a reserved `retired:entra:...:row:...@history.invalid` name and is disabled; the current account has a separate row. Only conflicting pairs are verified (at most 50 per cycle, rotating); no full tenant crawl is required. Incomplete reads and `UserGroupsFilter` do not release names. `reused UPN verification unavailable (...)`, `reused UPN verification declined: {reason}`, or `reused UPN verification budget` explains why identities remain quarantined, not that they were merged.
- `metadata batch identity changed or a row disappeared: expected ... updates, matched ...` means a competing writer invalidated a bulk metadata batch. The entire batch is rolled back, the user import throws before licence/organisation attribution, and its delta checkpoint is withheld so the next run re-reads those users.
- Writer lock (Warning): `another writer of users and licences (a one-person refresh from the portal, or this import ...)`: the import skipped this cycle because the lock was held.
- Licences: Warning `Graph reported ZERO tenant SKUs`; Error `Graph reports {n} consumed seat(s) for SKU '{x}' but returned NO users holding it. Refusing to delete ...`; Warning `held back {n} licence removal(s)`. These protect existing data.
- Disable: `marked {n} users row(s) disabled ...`; Warning `NOT marking {n} account(s) disabled although this complete read of the directory did not list them`.

Healthy: `read` -> `loaded` -> `complete` once per cycle, no `NOT moving/committing` warnings, quarantine absent or zero, `GraphMainPass` Succeeded.

| Last evidence | What it proves |
|---|---|
| `read ... users`, then nothing; heartbeat `CurrentStage=GraphMainPass` for a long time | Stuck or slow in the SQL phase after the read. The next expected line (`Insert phase`, `Starting metadata update`, `licence refresh`) names the phase |
| `/users/delta read stopped on page N` + `NOT moving the user checkpoint` | Graph paging failed mid-read. What was read is saved; next cycle retries from the stored checkpoint. The first line carries the HTTP/Graph code |
| `Graph rejected the stored /users/delta token` | Normal after a long gap or tenant reset; the next run is a full read (big, slow). A defect only if every cycle |
| `identity reconciliation ... N quarantined` with the same `oldest since` repeating | Ambiguous identities are parked and the import continues; the checkpoint will not move until each quarantined profile is stored safely. A growing count points at the directory, not the importer |
| `another writer of users and licences` every cycle | The lock is permanently held (stuck refresh or old process); users are not updating at all |
| `NOT committing the Graph delta token` | A phase failed earlier in the same run; find the Error just above |
| `ZERO tenant SKUs` or `NO users holding it` | Graph or permission glitch handled safely; licence data is stale, not deleted |
| `tenant subscribedSkus request returned HTTP 403` and `licence refresh unavailable: the scheduled app cannot read licence details per user` | The scheduled app did not refresh assignments or licence history. A direct HTTP 403 is sufficient for this fallback even when the error body is missing or malformed; it does not establish why access was refused. Purchased-seat capacity is unknown, not zero. Metadata can still advance its delta checkpoint, but the combined import is not stamped complete. When the inventory can be read again, the next run reconciles all in-scope users, even with an empty metadata delta. [Graph does not support application permissions for per-user licence details](https://learn.microsoft.com/en-us/graph/api/user-list-licensedetails?view=graph-rest-1.0#permissions). |

```kusto
traces
| where timestamp between (startUtc .. endUtc) and message startswith "User import - "
| where severityLevel >= 2 or message has_any ("complete.", "identity reconciliation", "identity quarantine")
| project timestamp, severityLevel, message, Run=tostring(customDimensions.RunId)
| order by timestamp asc
```

### 3.2 Teams crawl

Stage `GraphMainPass`. Prefixes **`TeamsCrawlSummary:`**, **`Teams import:`**, `TeamsChannel*`.

- One summary per crawl: `TeamsCrawlSummary: groups={n}; withTeam={n}; crawled={n}; saved={n}; failed={n}; channelsRead={n}; channelsSkipped={n}; channelsFailed={n} (in {n} Team(s)); channelsWithheld={n}; tokensCommitted={n}; tokensKept={n}; tokensReset={n}; elapsedMs={n}; topFailure={x}.` Warning when `failed>0` or `channelsFailed>0`, else Information.
- `Teams import: all {n} Team(s) failed to load or save this cycle - most likely an outage ...` (Error; back-off from one hour up). `Teams import: {n} of {m} Team(s) couldn't be loaded or saved ... (most common: {x})` (Warning).
- Channels (Warning unless noted): `TeamsChannelReadFailed: teamId=..; channelId=..; status=..; code=..; failure=..; consecutive=..; kind=..`; `TeamsChannelReadWithheld`; `TeamsChannelReadIncomplete`; `TeamsChannelGraphFailure`; `TeamsChannelBudgetExhausted: ... channelsRead=..; channelsDeferred=..; limit=..`; Information `TeamsChannelBudgetProgressSaved`, `TeamsChannelReadSkipped`.
- Deep-analytics tokens: Error `Entra ID no longer accepts the refresh token read for Team '{x}' (...)`; Warning `Deleted the refresh token stored for Team '{x}'. Authorise the Team again ...`; Warning `No Storage connection string found in config. No deep Teams analytics will be possible.`
- Per-Team: Error `Couldn't load team from Group ...`, `Couldn't save Group ... to SQL`, `Got SQL exception saving Team: ... Will try again next cycle.`
- A Graph read that came back without its collection fails that Team for the cycle (it keeps what was saved before): `Couldn't load team from Group ...: Graph answered page {n} with no 'value' collection, so the rows on that page are unknown.` for the owner list, the member list, the channel list or a channel's tabs (older builds read these as "none"). Owners are read from `/groups/{id}/owners` page by page (older builds used `$expand=owners`, which Graph cuts short); `... a group's owners went past {n} pages` or `... back to a page it had already returned` is a Graph paging loop.

Healthy: one `TeamsCrawlSummary` per cycle at Information, `failed=0`, `topFailure=none`.

| Last evidence | What it proves |
|---|---|
| Summary at Information, large `elapsedMs` | Not a failure; size the cost from channel counts |
| `channelsWithheld>0` or `TeamsChannelBudgetExhausted` | Channel reading is budgeted per cycle; deferred channels resume after SQL persistence. Persistent only on a big tenant |
| `Teams import: all N Team(s) failed` | Outage-shaped (SQL, Graph, throttling). `topFailure` and the per-Team Errors above it name it |
| `Entra ID no longer accepts the refresh token` | That Team's deep analytics need re-authorisation in the portal; nothing else is affected |
| `ImportStage start: Stage=GraphMainPass`, no summary, no finish | Crawl still running, or the process died in it |

```kusto
traces
| where timestamp between (startUtc .. endUtc) and message startswith "TeamsCrawlSummary:"
| extend failed=toint(extract(@"failed=(\d+);", 1, message)),
         elapsedMs=tolong(replace_string(extract(@"elapsedMs=([\d,]+);", 1, message), ",", "")),
         topFailure=extract(@"topFailure=([^.]+)\.", 1, message)
| project timestamp, severityLevel, failed, elapsedMs, topFailure
| order by timestamp asc
```

### 3.3 Sent email

Prefixes **`SentEmailChunk:`**, `SentEmailQuarantine:`, `Sent emails:`; runs inside `GraphMainPass`.

- `Starting sent emails import...`; Warning `Skipping sent emails import: the configured identity does not have Mail.Read permission.`; `Sent emails: {n} of {m} users with a mail address are in UserGroupsFilter; the other mailboxes are not read.`; `Found {n} users with email addresses to scan for sent items. Processing in chunks of {n} ...`; `Sent emails: skipping {n} of {m} users already known to have no mailbox.`; `Sent emails: re-checking every mailbox (last full sweep ...)`.
- **One line per chunk**: `SentEmailChunk: chunk={i}/{n}; mailboxes=; loaded=; committed=; failed=; quarantined=; messages=; inserted=; recipients=; skippedNoSentDate=; aborted=; loadMs=; addressMs=; existingMs=; scoreMs=; persistMs=; checkpointMs=; totalMs=.` Warning when `failed>0`, `aborted=True` or `skippedNoSentDate>0`.
- Per mailbox, by user id (no UPNs): Error `Sent emails: persistence or checkpoint failed for user ID {id} ...`, `Error loading sent emails for user ID {id} (...)`; Warning `SentEmailMailboxQuarantined: userId=; consecutiveFailures=; failure=; sqlError=`; Warning `SentEmailQuarantine: {n} mailbox(es) are not read this run`; Warning `SentEmailCheckpointBatchFailed: mailboxes=; failure=. Committing them one at a time.`; Warning `SentEmailStateDegraded: no Storage connection string ...` (checkpoints in memory).
- End: `Finished sent emails import in {hh:mm:ss}. ...`; Warning `Sent emails: a chunk completed with failures ...`.

Healthy: Information `SentEmailChunk` lines, `failed=0`, `chunk=i/n` advancing, then `Finished sent emails import`.

| Last evidence | What it proves |
|---|---|
| `SentEmailChunk` stops at `i/n`, no `Finished` | Hung or killed in chunk i+1. The previous chunk's `*Ms` fields show which phase was slowing |
| `loadMs` dominates | Graph mailbox reads (throttling, 429s in `exceptions`) |
| `persistMs` or `checkpointMs` dominates | SQL cost (contention, locks); the failure lines carry the SQL error number |
| `failed>0` for the same user ids, then `SentEmailMailboxQuarantined` | That mailbox cannot be saved and sits out its quarantine; others continue |
| `Skipping sent emails import: ... Mail.Read` | Permission missing; nothing imported by design |
| `skipping N of M users already known to have no mailbox` | Normal skip list, not an error |

```kusto
traces
| where timestamp between (startUtc .. endUtc) and message startswith "SentEmailChunk:"
| extend chunk=extract(@"chunk=([0-9/]+);", 1, message), failed=toint(extract(@"failed=(\d+);", 1, message)),
         loadMs=tolong(extract(@"loadMs=(\d+);", 1, message)), persistMs=tolong(extract(@"persistMs=(\d+);", 1, message)),
         totalMs=tolong(extract(@"totalMs=(\d+)", 1, message))
| project timestamp, severityLevel, chunk, failed, loadMs, persistMs, totalMs
| order by timestamp asc
```

### 3.4 Usage reports and the concealed-identity cleanup

Usage reports run in the **background** (`GraphDeferredPass`, heartbeat `BackgroundStage`), so they do not block the audit import.

- Gate: `Skipping activity reports as have processed recently (less than {n} hours ago)`; `Reading all activity reports from {n} days back (the UsageReportsDaysBack setting ...)`; `ForceUsageReportsImport=true; bypassing ...`. A recent run that found all ten daily tables empty and fully accounted for its rows (including rows excluded by `UserGroupsFilter`) still waits a day. If that run retained rows but all ten tables are now empty, the importer rechecks the configured window immediately. No Teams rows alone does not trigger this.
- `All daily usage tables are empty and ...; rechecking the configured report window.` distinguishes a loss of stored rows from an unverified run or a legacy timestamp with no matching evidence. A genuinely empty installation upgraded from the old timestamp is rechecked once, not on every poll. `ImportSchedule` stores the run's observation at `UserActivityPhaseFacts:v1` alongside the matching `UserActivityLastImported` timestamp; it is not a certificate that Graph published a report. Deleting the timestamp still forces the next run. A current refusal for concealed identities keeps its existing daily backoff.
- Per report: `Importing {thing} reports...`; `{thing}: skipping {n} finalized day(s) with matching SQL outcomes`; `{thing}: downloading the full {n}-date window from Graph ...`; `Imported {n} {thing} reports.`
- `Graph returned HTTP 404 for continuation page ...`: the importer kept the rows it received, but did not mark the report or daily phase complete. The affected report retries its configured window next cycle. A 404 on **page one** means the date is unavailable, not successfully empty: the daily throttle can still apply, but that date is checked again.
- Each daily report stores the mature dates it finished (including successful empty dates) in `ImportSchedule`, row `UserActivityReportWindow:v1:{loader}`, beside its existing completion timestamp. Only a matching run and SQL row presence can skip a date. The first run with an older timestamp but no matching window rechecks the configured look-back once. After a missed run, dates last requested before age three are fetched again even if their nonempty D+2 response already verified dashboard coverage.
- **Phase summary (one line)**: `Usage report phase: {n} of {m} daily report(s) imported, {n} failed, {n} refused for concealed identities; {n} row(s) read, {n} written, in {ms} ms. Per report: {name} {r} read/{w} written, {d} date(s) downloaded, {s} finalized date(s) skipped, {ms} ms; ... Failed: ... Refused: ... SharePoint sites weekly usage: {status}.` Warning if any failed or refused. The totals count completed reports only; earlier rows retained by a failed report are not included.
- Failures: Error `Activity reports did NOT fully import - {n} of {m} report(s) failed: ...`; Error `{report} failed with HTTP {code} ...`; Warning `{loader}: every identity Graph returned for {date} is concealed (hashed) ...` and `skipping {n} of {m} row(s) whose identity ...`. Concealment is a Microsoft 365 admin-centre privacy setting; the importer cannot retry past it.
- Per-workload coverage: each daily Teams, Outlook, SharePoint, OneDrive and Viva Engage **user activity** import records its outcome in `AnalyticsState`, PartitionKey `UsageReportCoverage`, RowKey `v1:{source}:yyyyMMdd`. Viva Engage (`yammer`) is used by the weekly active-users chart only; the group and device reports do not certify it. Copilot Adoption still uses the other four sources.
- Outcomes: `partial` (refresh in progress, interrupted, or requested before the day settled), `complete` (tenant-wide, every row saved, requested at least 2 days after the day), `empty` (no rows, requested at least 3 days after the day; an earlier empty answer may mean Graph has not published the report), `incomplete` (settled, but concealed or blank identities were skipped) and `scopeUnverified` (UserGroupsFilter). Only `complete`/`empty` verify an Adoption source. A new request marks its day `partial` before the first page. Unlike historical days with no ledger entry, a partial day cannot be verified by the weekly chart from surviving rows alone. A later complete tenant-wide download can reverify a purged day while Graph still retains it. Dates skipped as finalized are not invalidated.
- Information `{loader}: re-downloading {n} date(s) with no matching final coverage outcome.` is expected for days with a missing or unfinished ledger outcome, or where an `empty` outcome contradicts stored rows; it is not a failure. `incomplete` and `scopeUnverified` days with retained rows can settle like other stored days (a `scopeUnverified` day is re-downloaded once after the filter is removed). Warning `{loader}: could not read usage-report coverage outcomes ({ExType}); ...` means Storage could not be read during the skip check. The importer does not trust a rowless day as empty without its ledger outcome. If the `partial` marker cannot be written, the importer does not start that day's SQL save.
- SharePoint sites weekly report (the `SharePoint sites weekly usage:` part of the phase summary): progress is `{report}: loading page {n}...` (Information every 50th page, Debug otherwise; older builds logged every page's link, `Loading next page from '...'`, with its `$skiptoken`). The report fails for the cycle, before anything is saved, on `Graph returned page {n} of the report in a form that could not be read`, `page {n} of the report came back from Graph without its "value" collection`, `Graph linked page {n} of the report back to a page it had already returned` or `Graph's paging of the report went past 10,000 pages` (older builds looped for ever on the last two, holding the background pass).
- Copilot per-user report: `Copilot per-user report {request}: parsed {n} row(s), wrote {n} to SQL.`; Warning `... is not available on this tenant: ...`; Error `... came back with concealed user identities for all {n} row(s)`. Warning `Copilot per-user report: Graph returned {n} extra row(s) for a user, date and period it had already reported. Kept the last row for each` (and the `Copilot user-count report '{type}': ... for an app and date` equivalent) is informational: the report was saved. Before this was handled, such a duplicate failed the whole report on every cycle.
- Copilot per-user weekly reading (the 7-day report Licence activity measures Copilot from, taken once a week): Information `Copilot per-user weekly reading: none due. ...` on most days (no Graph call); `Copilot per-user weekly reading: Microsoft's newest per-user report is dated Sunday {date}; collecting its D7 report for Licence activity.` then the per-user report line above for `(period='D7',...)`. Warning `Copilot per-user report {request} is dated {date}, not Sunday {date}, so it doesn't cover a Monday-to-Sunday week and was not stored.` means that week's reading was missed: Licence activity leaves Copilot Unknown (or falls back to an exactly matching 28-day report) for any period containing that week.
- Optional `UsageReportSaveStage` events exist only when save instrumentation is enabled.
- **Concealed cleanup** (stage `ConcealedIdentityCleanup`), one outcome line: `Concealed-identity user cleanup: deleted {n} placeholder user(s) ... in {n} batch(es) taking {s} s.` or `nothing deleted in {n} batch(es).`, ending `None left to check; the next cycle confirms it.` or `None left to delete; checked again in {h} hours.` Error `Stopped by an error at step '{step}' ({type}, SQL error {n})` (the whole batch rolled back, retried later); Warning on lost lock or pre-flight timeout; Error `... is turned OFF: the {Setting} app setting is '{v}', which is neither true nor false` (fail-closed kill switch); Information `waiting for the usage-report import running in the background to finish` and `skipped this cycle: another writer of users and licences`.

Healthy: one `Usage report phase` Information line per refresh window (hours apart), `0 failed, 0 refused`; cleanup `None left to delete`.

| Last evidence | What it proves |
|---|---|
| `Usage report phase ... N failed` | Failures are isolated per report; `Failed:` lists them. The HTTP code in the Error above separates permission (403) from throttling or outage |
| `refused for concealed identities` | Graph returned hashed names so rows were not stored; the tenant setting must change |
| Heartbeat `BackgroundStage=GraphDeferredPass` for hours, no phase summary | Long first-time or backfill window; `Importing ... for date` progress and, once printed, per-report ms show the cost |
| `cleanup ... Stopped by an error at step` | Batch rolled back, retries after the stated minutes; the SQL error number separates deadlock from timeout |
| `cleanup ... waiting for the usage-report import` | Intentional ordering; repeats until the background pass ends |
| `cleanup is turned OFF` | Setting invalid or false; cleanup deliberately does nothing |

```kusto
traces
| where timestamp between (startUtc .. endUtc)
| where message startswith "Usage report phase:" or message startswith "Concealed-identity user cleanup" or message startswith "Activity reports did NOT"
| project timestamp, severityLevel, message
| order by timestamp asc
```

### 3.5 Audit import, Copilot enrichment and the enrichment queue

Stage `ActivityImport` (plus the enrichment retry pass). Prefix **`Audit events import:`**.

- `Audit events import: Got {n} audit events from {m} summary reports. {i} imported, {p} processed already, {u} URLs out of scope ...`.
- Outcome, exactly one per cycle: Information `all save batches committed successfully this cycle.` **or** Error `COMPLETED WITH FAILURES - {n} save batch(es) (~{m} event(s)) could not be committed this cycle after retries. Their content blobs were NOT checkpointed and will be retried next cycle`.
- Warnings: `DOWNLOAD ERRORS DETECTED - {n} metadata download failures, {m} report download failures` (retried next cycle); `{n} audit record(s) were SKIPPED this cycle because they could not be deserialised, and will not be retried`; `blob checkpoint read failed (...); processing all content blobs`. Error `batch save FAILED after retries for {n} event(s)`.
- Progress: `processed {n}% activity report data...`; `blob checkpoint skipped {n} of {m} content blobs already committed in a previous cycle`.
- A save batch whose merge failed carries `Couldn't merge batch insert using given SQL: {SQL Server's message, values as <redacted>}` (any importer that stages through `InsertBatch`); `SqlErrorNumber` says which error. Warning `Could not find SP event for audit-log content ID {id} in cached SP events when assigning web for its site|item URL`: by content id, not by the file's or site's address (#853).
- **Timing**: `save-phase timing (aggregate ...): dedup+scope {s}s, staging+merge {s}s, metadata {s}s.`; `metadata breakdown - EF-load {s}s, Copilot resource resolution {s}s, Copilot SQL commit {s}s, Power Platform {s}s, EF save-changes {s}s.`; `Copilot commit timing: SP-docs ..., Teams ..., chat-only ...` (Information only when the commit took 5 s or more).
- Inline enrichment, per batch (Warning): `Copilot enrichment: {n} interaction(s) in this batch were saved without their file or meeting details because Microsoft Graph failed transiently ... queued for durable re-enrichment`; or `... saved chat-only ... no durable retry queue is configured (no Storage connection string)`. `Copilot enrichment in-lock lookups: {n} lookup(s), {n} cache hit(s), {n} resolved, {n} unresolvable, {n} transient ({n} skipped Graph after a recent transient failure, {n} hit the deadline); {n} Graph call(s), no SDK retries; elapsed {ms} ms.` (Warning when any transient, Information when slow). The counts include the user lookup a Teams meeting interaction needs before its meeting lookup.
- Warning, once per run: `Copilot meeting enrichment could not read a user from Microsoft Graph (Graph returned HTTP 404) ...` - expected for a deleted account; that user's meeting interactions are saved without meeting details and the user isn't looked up again that run.
- Warning, per batch: `Copilot metadata: {n} message id(s) were longer than copilot_event_messages.message_id ... {n} AI model name(s) were longer than copilot_ai_models.name ... The interactions were saved.` Lengths only. Such a value used to fail the whole Copilot merge for the batch on every retry.
- **Queue summary**: `Copilot enrichment queue: due {n}, claimed {n}, completed {n}, terminal {n}, deferred {n}, abandoned {n}, leased-skipped {n}; oldest due {m} min ago; max attempts {n}; (run budget of {n} min reached with work still due | no more work due); elapsed {ms} ms.` Warning when deferred, abandoned or budget reached. Follow-ups: Warning `{n} item(s) completed without file or meeting details because retrying can never succeed` (`terminal`); Error `{n} item(s) abandoned after {k} failed attempts or {d} days, and parked as 'abandoned-<event id>' rows in the CopilotEnrichment state partition`.
- Graph lookups (Warning, capped): `Transient Graph error resolving ... (status {s}, code {c})`; `Copilot meeting enrichment is unavailable in this tenant`; HTTP 403 and 404 variants. Warning `Graph answered a document library enumeration without its drives collection; contexts depending on this fallback will not be cached as unresolvable`: a transient failure, retried like a 503 (older builds took it as "no other libraries" and saved those interactions without their file).
- Repairs run every cycle as stages `RepairDenormalisedCopilotColumns` and `RepairSplitCopilotAgents`; `Recovered origin for {n} Copilot agent(s) ...`.
- Backfill: `Copilot audit backfill {id}: ...` lines and event `CopilotAuditBackfill` (`Outcome, State, ErrorCode, PermissionStatus`; metrics `RecordsSeen, RecordsImported, RecordsAlreadyPresent, MappingFailure_*`). Warning `Skipping Copilot audit backfill: ... AuditLogsQuery.Read.All` means the opt-in permission is not granted (expected).
- Section back-off: Warning `ImportBackoff: section={name}; consecutiveFailures={n}; nextAttemptAfterUtc=..`.

Healthy: each cycle ends with `all save batches committed`; the queue line says `no more work due`, `abandoned 0`.

| Last evidence | What it proves |
|---|---|
| `Got {n} audit events`, then no outcome line | Process died or is still in the save phase; heartbeat `CurrentStage=ActivityImport` and its age decide which |
| `COMPLETED WITH FAILURES` | Some blobs were not checkpointed and will retry; data is late, not lost. The preceding `batch save FAILED` / exception has the SQL error number |
| `DOWNLOAD ERRORS DETECTED` alone | Source (Management Activity API) flakiness; retried next cycle |
| `SKIPPED ... could not be deserialised, and will not be retried` | Those records are permanently lost; the first capped `Failed to deserialize` warnings show why |
| `Copilot SQL commit` dominates `metadata breakdown` | The Copilot merge is the bottleneck (see the slow-merge guidance in the engine instructions) |
| Queue `oldest due` growing, `budget reached` | Backlog exceeds the per-run budget; it drains over cycles unless `abandoned` rises |
| Queue `abandoned > 0` (Error) | Items parked after repeated failures; interactions stay counted chat-only; read "By last failure" |
| `saved chat-only ... no durable retry queue` | No Storage connection string; transient Graph failures are never retried |

```kusto
traces
| where timestamp between (startUtc .. endUtc)
| where message startswith "Audit events import:" or message startswith "Copilot enrichment" or message startswith "Copilot commit timing"
| where severityLevel >= 2 or message has_any ("all save batches", "Got ", "timing", "breakdown", "queue:")
| project timestamp, severityLevel, message
| order by timestamp asc
```

### 3.6 Calls queue and webhook subscription

Stage `CallQueueWebhook`. The subscription timer runs **independently of the import cycle**. Tags **`[Calls Webhook]`**, **`CallImport outcome:`**, `Teams calls import`.

- Subscription: `Created subscription id '{id}' for webhook at '{url}' in {ms} ms`; `Renewed subscription id ... New expiry: {u}.`; `Replacing call-records subscription id ...: {reason}`; Warning `Subscription lapsed: subscription id ..., {what}`; Warning `The client secret has changed since subscription id ... was created ...` (the subscription is replaced); Warning `Graph refused a second call-records subscription (409 Conflict)`, then Error `Removed conflicting subscription ... but the replacement could not be created` if the second create fails; `Webhook warm-up answered in {ms} ms.` / Warning `Warming the webhook up failed ...`.
- Timer: `Subscription timer started: checks every {n} minutes, independently of the import cycle`; Error `Subscription timer tick failed unexpectedly (...); the timer carries on.`; `Recovered: the subscription check succeeded on attempt {n} in {ms} ms, after failing since ...`; Critical for a missing client secret (subscription cannot be kept).
- Queue: `ServiceBus client: Now listening for service-bus messages.` / Warning `Not listening for service-bus messages?`; Error `ServiceBus processor encountered an exception` with Namespace, Entity Path and Error Source lines; `New message received from ServiceBus with ID ...`; `Succesfully processed & completed ServiceBus message ID ...`. A listener that fails to start fails the `CallQueueWebhook` stage with Critical `Got exception on ProcessCallQueueAndWebhook: ...`, and is started again on the next cycle (older builds started it fire-and-forget, so the failure was never logged, and once a start had failed every later start failed too).
- **Per-call outcome**: Information `CallImport outcome: call='{id}' outcome=Saved sessions={n} [skippedSessions={n}] pages={n} ...`; Warning `CallImport outcome: call='{id}' outcome=Retry:{kind} failure={code} elapsedMs={n}.`; Error `CallImport outcome: call='{id}' outcome=DeadLettered:{reason}`. Pause: Warning `Teams calls import paused for {n} minute(s): more than one call has failed in a way ...`, then `Teams calls import resumed: a call was saved after the pause.`
- Malformed sessions: `skippedSessions={n}` and Warning (at most every 10 minutes) `Call record '{id}' was saved without {n} of its {m} session(s): nobody on them could be attributed ...` - a session without a caller or callee, or two people neither of whom organised the call; the rest of the call is saved. A call with nothing usable is dead-lettered at once, not retried, as `DeadLettered:NoUsableSessions` (every session malformed) or `DeadLettered:NoCallType`: fetching the same record again cannot help, so a default dead-letter replay skips both. Older builds failed the whole call on one such session and dead-lettered it as `CallImportRetriesExhausted` after about 4 hours of retries.
- Missing session list: a call record that comes back without `sessions` (or with `"sessions": null`) is a failed read, not a call with nobody on it - Graph can still be completing the record. Warning `CallImport outcome: call='{id}' outcome=Retry:Transient failure=SessionPagingFailed ...` with `Last error: CallRecordSessionPagingException: Call record '{id}' came back without its session list (sessions page 1) ...`, retried with the usual back-off; if it never comes back whole, `DeadLettered:CallImportRetriesExhausted`, which a default replay replays. `"sessions": []` is Graph saying the call had no sessions: it is saved without them.
- Dead-letter replay tool: `Teams calls dead-letter replay ...`. A default `--replayCallDeadLetters` run skips the reasons the importer gives a notification that can never be imported as it stands - `InvalidCallNotification`, `MissingCallId`, `CallImportNotRetryable`, `NoUsableSessions`, `NoCallType` - which `--include-invalid` replays and `--purge-invalid` deletes. Every other reason is replayed, unless the notification was first queued over 30 days ago or has been replayed 3 times already.
- Operator single-call mode (`--callId`): `Loaded call record '{id}' from Graph: {n} session(s) on {p} page(s). The payload is not logged.`, then `Nobody on this call is in UserGroupsFilter, so it is not saved.` or `Saving call to SQL...`. It no longer logs the call record's JSON (#853).
- Webhook (the web app's own traces): `CallRecordWebhookController: {n} validation request(s) received (from Graph, or the importer's warm-up) since {u}. Logged at most every 15 minutes.` and `CallRecordWebhookController invoked with invalid body {n} time(s) since {u}. ...` - sampled per web app instance, so one line can stand for many requests.
- Startup: Critical `Teams calls import is enabled but Service Bus is not configured. Skipping Call Queue import & webhook validation.`

Healthy: `Created`/`Renewed` on schedule with a future expiry, one `listening` line per process, `Succesfully processed` per message, no `DeadLettered`.

| Last evidence | What it proves |
|---|---|
| Last `Renewed`/`Created` has an expiry in the past, no later subscription lines | The timer is not running (process down, or ticks failing: see `tick failed`); no call notifications arrive |
| `Subscription lapsed`, then `Created` | Self-healed; a problem only if it repeats quickly |
| `Graph refused a second call-records subscription (409 Conflict)` | Graph's [documented duplicate response](https://learn.microsoft.com/en-us/graph/api/subscription-post-subscriptions?view=graph-rest-1.0#duplicate-subscription-behavior) named a subscription on this webhook's resource and URL. Only that ID is removed before retrying creation. If the retry fails, delivery is at risk. |
| `The response does not establish a permission problem or a duplicate owned subscription` on replacement | The direct request failed, but a bare status, an empty or unreadable error, or an unrelated conflict does not justify deletion or a consent change. The importer leaves the old subscription in place and attempts renewal if due. |
| `a redirect is not an answer from the requested Graph endpoint` | The subscription operation returned a redirect rather than a result. It was not followed; no further subscription change is started on that evidence. Check the route to Graph. |
| `Graph refused the replacement after retries (HTTP ...)` | The SDK exhausted its retries. The importer does not issue another create or delete. It attempts to renew this webhook's existing subscription if due; look for `Renewed subscription id` to confirm that worked. The timer still retries the failed replacement. |
| `The fallback renewal ... also failed (Graph SDK request deadline)` | The PATCH for the old subscription did not finish in time. Renewal is unconfirmed; the timer reports the original replacement failure and retries it. Stopping the importer cancels the check instead. |
| `Webhook warm-up failed` or a Graph validation timeout | Web app cold or unreachable from Graph; it must answer within seconds |
| `DeadLettered:{reason}` | The importer gave up on the notification; the reason names why. A default replay replays it unless the reason is one of the never-importable ones above (`--include-invalid` for those) |
| `paused for N minute(s)` | Systemic failure (SQL down, for example); resumes after one success. The `Retry:{kind}` lines before it say why |
| `Not listening for service-bus messages?` or processor errors | Queue connectivity or RBAC; messages accumulate in Service Bus |

```kusto
traces
| where timestamp between (startUtc .. endUtc)
| where message has "[Calls Webhook]" or message startswith "CallImport outcome:" or message startswith "Teams calls import" or message has "ServiceBus"
| project timestamp, severityLevel, message
| order by timestamp asc
```

#### Reprocess and clear requests from the portal

Administration > Service configuration's **Dead-lettered Teams calls** card records a request - reprocess, or clear - in the runtime state table; the activity job's poller checks for one every minute, claims it, runs the same replay as `--replayCallDeadLetters` (clear is its `--purge-all`), and records the outcome. Importer lines are under operation `Office365CallsImporter`, like the queue's; the web app's under `CallDeadLetters`. They carry the action, counts, codes and the request's random id - never a message, call id, user or URL.

- Poller: once per process `Teams calls dead-letter requests from the portal: checked every 1 minute(s) in partition 'CallDeadLetters' of the runtime state table; each is worked on for at most 15 minutes.`; without a Storage connection string, `... not checked. There is no Storage connection string ...` (the card then says the feature is unavailable). Warning, at most hourly, `Teams calls dead-letter requests from the portal: couldn't check for one in the runtime state table ({type}, HTTP {status}, {code}). Tried again every 1 minute(s).`
- **The one line saying a run was requested from the portal**: `Teams calls dead-letter request from the portal: reprocess (send the dead-lettered calls back to the queue) (request {id}, recorded {u}), claimed by this importer instance and worked on for at most 15 minutes. The replay's summary follows.` (or `clear (DELETE every dead-lettered call, whatever its reason or age)`). Then, per pass over the dead-letter queue, the replay tool's own `Teams calls dead-letter replay starting: ...` line and summary. Reprocess repeats passes while one stops at its limit having replayed something; clear is `DELETING every message it looks at`.
- Claim whose reply the state table lost (the write landed, and the SDK's retry then failed on the ETag it had changed): Information `Teams calls dead-letter request from the portal {id}: the runtime state table didn't confirm the claim, but the row holds this instance's claim, so it stands.`, then the claimed line as usual. When the row couldn't be read back either: Warning `... {id}: couldn't tell whether this instance's claim landed, as the runtime state table couldn't be read ({type}, HTTP {status}, {code}). It looks again at the next check, and carries the request out then if the claim is its own.`, and at a later check Information `... {id}: the claim this instance couldn't confirm at the last check did land, so it carries the request out now.` before the claimed line.
- Finish: Information (succeeded) or Warning `Teams calls dead-letter request from the portal {id} ({action}) finished: {succeeded|partial|failed} ({failureCode}) after {n} pass(es); replayed {n}, deleted {n}, left in the dead-letter queue {n}. Recorded for the portal.` Failure codes: `queueUnavailable` (Error `... couldn't open the Teams calls queue ({type}), so nothing was done.`), `readFailed`, `sendFailures`, `sendFailed`, `deleteFailed`, `lockedElsewhere`, `locksLapsing`, `timeBudget` (15 minutes used, more to do), `scanLimit`, `unexpectedError`.
- Warning `... was interrupted: the importer is stopping. It stays claimed ...` (shutdown mid-run; nothing recorded); Warning `... The request was replaced while it ran, so this result is in this log only.`; Warning `... Couldn't record it for the portal (...)`.
- Web app: `Teams calls dead-letter {reprocess|clear} requested from the portal (request {id}); the importer checks for requests every 1 minute(s).`; Warnings `Teams calls dead-letter requests: couldn't read the latest request ({type}).`, `... couldn't record a {action} request ({type}).`, `... couldn't read the calls queue's counts ({type}).`

**Reading a request's state.** The card shows it; so does the row itself: table `AnalyticsState`, PartitionKey `CallDeadLetters`, RowKey `LatestRequest`, column `Value` - JSON with `id`, `action`, `requestedUtc`, `claimedUtc`, `claimToken` (a random value the claiming importer instance writes, to recognise its own claim; absent on a request claimed by an older importer), `finishedUtc`, `outcome`, `failureCode` and `result` (`passes`, `scanned`, `replayed`, `deleted`, `sendFailed`, `deleteFailed`, `skipped` by code, `reasons` by dead-letter reason, `stopCode`). Only the latest request is kept. Deleting the row forgets it and lets a new request be made at once.

| Last evidence | What it proves |
|---|---|
| `requested from the portal` (web app), then no `claimed by this importer instance` within a few minutes | No importer is polling: the WebJob is stopped, or it has no Storage connection string or a different one. After 15 minutes the card says "Not picked up" and lets it be asked again; the importer never runs a request that went stale unclaimed |
| `claimed by this importer instance`, then neither `finished:` nor `interrupted` | The process died mid-run (Kudu restart, scale-in). The card shows "Running" until 2 hours after the claim, then "Not finished"; ask again then, or delete the row |
| `couldn't tell whether this instance's claim landed`, then no `did land` line | The state table failed while the claim was written and read back. Had the claim landed, the next check able to read the row would have carried it out. So either it didn't land - the request is claimed as usual, with a `claimed by this importer instance` line - or the process restarted first, when the card shows "Running" until 2 hours after the claim, then "Not finished" |
| `finished: partial (timeBudget)` | A big backlog: each request works for 15 minutes. Ask again to carry on |
| `finished: partial (scanLimit)` after a reprocess | The head of the dead-letter queue is mostly what can never be imported; clear it (or `--purge-invalid` / `--purge-unimportable`) so a reprocess reaches the calls behind |
| `finished: failed (queueUnavailable)` | The importer couldn't open Service Bus: see the Health page's Service Bus row and the queue's RBAC |

```kusto
traces
| where timestamp between (startUtc .. endUtc)
| where message startswith "Teams calls dead-letter request" or message startswith "Teams calls dead-letter replay" or message has "requested from the portal"
| project timestamp, severityLevel, cloud_RoleName, message
| order by timestamp asc
```

### 3.7 App Insights importer (per-day outcomes)

Job `AppInsightsImporter`, stage `AppInsightsImport`. Reads App Insights by an ingestion-time cursor, one bounded window at a time.

- **Event `AppInsightsImportCycle`**: dimensions `Outcome (Succeeded|Degraded|Failed), Mode, Reasons, FailedDays, CursorUtc` plus exception dimensions; measurements `Days, Windows, PageViews, Events, ElapsedSeconds, PendingSearchHours, ReconciledSearches`. The last two are pending ingestion hours (omitted when unknown) and new search facts recovered, not replays. Trace twin `AppInsightsImportCycle: Outcome=..; Mode=..; Days=..; Windows=..; PageViews=..; Events=..; ElapsedMs=..; Reasons=..; FailedDays=..` (Information, Warning for Degraded, Error for Failed).
- **Event `AppInsightsDay`**, one per day: `Day, Outcome (Saved|NoData|UnmatchedSearches|Failed|RetriesStopped), Mode, FailedStage (Download|Save), FailedSections`; measurements `Windows, PageViews, Events, UnmatchedSearches, FetchSeconds, SaveSeconds, ConsecutiveFailures`. For searches, `Events` counts newly inserted facts, not replayed, missing or invalid rows or new lookup terms. Other event kinds retain their accepted-input count for a successful window. `UnmatchedSearches` counts inputs awaiting an eligible visit at the end of their window, not distinct missing visits. Trace twin `AppInsightsDay: Day=..; Outcome=..; ...`.
- **Event `AppInsightsDayGivenUp`**: sent once when forced retries of a day stop (`Attempts`).
- Cursor: `AppInsightsCursor: Outcome=Advanced|ReadFailed|WriteFailed; Cursor=..; Previous=..`; Warning `AppInsightsCursor: Outcome=RewoundAfterRestore; Cursor=..; RewoundTo=..; NewestStoredHit=..; NewestStoredHitAtCursor=..|none` when the hits table holds older data than when the cursor was written; Warning when the cursor says data was imported but the hits table is empty; `AppInsightsImport plan: Mode=..; Range=..; Cursor=..; NewestStoredHit=..`; Error `AppInsightsRowCounts: Outcome=Failed ...`.
- Failures: Error `Failed {what} data for day {date} (attempt {n} of {max}).` and `... again ({n} failed attempts in a row). Stopped forced retries of this day ...`; `Day {date} failed in an earlier cycle; starting from it so it is retried before any later day.`
- Progress: `Importing day {i}/{n}: {date} in {p} page-view and {e} event window(s)...`; `Left out data by people outside UserGroupsFilter for {date}: ...`; `Import loop finished: {n} days processed ...`.
- Host: Warning `Web-traffic import is disabled in app configuration. Stopping here until the app is restarted.`; Critical `No Application Insights connection string found - can't continue.` and `Check settings & restart app. The Application Insights import is not running.`
- Page metadata (the `Page updates` section): Information `Updated {n} URLs from {m} page-update events`; Error `Failed saving page-update metadata for URL id {id}: {exception types, SQL error number and message without values}. Skipping this URL; it is not marked as refreshed.` for each page whose metadata could not be saved, then Error `The metadata of {n} page(s) could not be saved ...`. Those pages do not fail the day: each is read again the next time the tracker sends its metadata. A transient SQL failure (timeout, dropped connection) is not skipped: it fails the section (`FailedSections=Page updates`) and the day is retried. Look a URL up by id in `dbo.urls`.
- Unreadable tracker data (#853): Warning `Page update for URL id {id}: {what}.`, where `{what}` is `CommentsString|LikesString|TaxonomyPropsString is {n} characters that aren't a valid JSON list, so it is ignored`, `props aren't valid JSON, so they are ignored`, `props contain a value that isn't a simple JSON value, so it is ignored`, or `props contain legacy comments|likes data; check the AITracker is updated`. The rest of the page's update is still saved. These used to be console-only `ERROR:Unexpected ... value for URL ...` / `DEBUG ERROR: Page update for '{url}' ...` lines.
- Debug switch `-logApiResponses` (operator use only): writes each query response to `%TEMP%\AppInsightsImporter\REST\{query type}\` as `{WrittenUtc, Operation, Request{Method, Uri}, Response{StatusCode, ReasonPhrase}, Body}`, never the request headers or token. The body is raw telemetry: delete the folder when the investigation is over, within 7 days at most.

Healthy: `AppInsightsImportCycle` Succeeded each interval, days `Saved` or `NoData`, cursor `Advanced`.

If Kudu stops the job while it is resolving `UserGroupsFilter`, the `AppInsightsImport` stage has not started. Expect no outcome for that cycle and no cursor advance. This is not a directory failure or a decision to import everyone. If the stage has already started and is waiting for an Entra SQL token, that connection has not yet issued a command; earlier connections may already have committed.

With a site filter, a search needs its **own** admitted page request, the same user and case-sensitive tracker session, and a page that still matches the current rules. Exact-site rules also need the hit's stored site collection. An unrelated allowed page in the same session does not qualify. `Skipped ... search event(s) not eligible under the site filter` is an exclusion, not a request to retry. If the request id is usable but no hit or explicit exclusion is known, its ingestion hour can remain pending. Without any configured site rules, historical searches still match by user and session.

| Last evidence | What it proves |
|---|---|
| Cycle `Failed`, same `FailedDays` repeating, `ConsecutiveFailures` climbing | The cycle stops at the failed day and retries it before any later day, so nothing after it imports. `FailedStage` separates download (App Insights query, permission, throttle) from save (SQL) |
| `AppInsightsDayGivenUp` | After the attempt limit the cycle moves past that day; it stays a gap until fixed |
| Cycle `Degraded` with `Reasons` | Imported with a caveat; not a hard failure |
| `SearchSessionsPending` | A search reached App Insights before its matching visit was in SQL. Other imports continue. The runtime state checkpoint contains only the UTC ingestion hour and next retry time, never the search or identity. Without Storage, it lasts only for this process. After normal imports, at most eight due hours per cycle reread **only searches**, first after 30 minutes and once more when the hour is three days old if needed. Completed hours are removed. Retries never extend App Insights retention or read beyond the importer's 90-day ingestion lookback. |
| `AppInsightsSearchRetry: ... Final=True` / `SearchRetriesStopped` | `Final=True` means this was the last attempt, not necessarily a loss. `SearchRetriesStopped` means a visit was still missing, a final query failed/returned no source data, or the source hour was too old. Check for the page view and site/user filters. A search alone doesn't identify its site's eligibility; no visit is invented. After an eligible visit exists, `ReadHitsDaysBeforeToday` can reread the **event timestamp day** while the source retains it. A manual backfill leaves the cursor and retry checkpoint alone; `UnmatchedBackfillSearches` means it couldn't match every search. |
| `SearchRetrySourceEmpty`, `SearchRetryFailed`, `SearchNoIngestionTime` | A pending hour returned no searches, a retry threw, or an unmatched event had no source ingestion hour to retry. Check the `AppInsightsSearchRetry` trace for the hour/exception. The first two are retried at the final pass; the last needs a timestamp-day backfill. |
| `SearchRetryReadFailed` / `SearchRetryWriteFailed` / `AppInsightsCursor: Outcome=WriteFailed` | The normal cursor didn't advance. Check the runtime state store; the next cycle can replay events without creating duplicate searches. |
| `AppInsightsImport` stage skipped with `HostShutdown` | No normal cycle result. Earlier SQL commits can remain: a stop during a later page's metadata or comment enrichment does not reverse a saved search. An interrupted search merge rolls back unless it already committed. A stop while rereading searches does **not** spend the pending hour's retry. The importer saves the remaining hours **before** moving the ingestion cursor. If only the hours were saved, the next run uses them and replays the old cursor, skipping facts already in SQL. If the cursor write also committed, all preceding work was already saved or marked for retry. |
| `CursorReadFailed` / `ReplayStartReadFailed` / `ReplayStartWriteFailed` | The cycle failed before writing to SQL. Check runtime state storage. An unreadable cursor or restore watermark is **not** a first run: importing whole days on that assumption could let a newer SQL hit hide an earlier search. The next cycle retries. For a confirmed bootstrap or database restore, the importer pins the starting times against the previous cursor (`IngestionCursorReplayStart`) before SQL can change them. Advancing the cursor makes that record obsolete. Without Storage, checkpoints do not survive a process restart: use a timestamp-day backfill for older work if needed. |
| `AppInsightsCursor: Outcome=RewoundAfterRestore` | The database lost its newest hits (point-in-time restore, or pointed at an older copy). That cycle re-reads ingestion from a day before `NewestStoredHit` (at most 90 days back); rows already stored are skipped. Expected once after a restore; repeating every cycle means something keeps deleting hits |
| Warning `AppInsightsCursor: could not re-read the newest stored hit after saving (...)` | The cycle's data and cursor are saved; only the restore watermark beside the cursor is older (the value read when the cycle started). Harmless once; repeated, the SQL read of the newest hit is failing and the next restore check compares against a stale value |
| `NoData` every day while the site has traffic | Out-of-scope filter (UserGroupsFilter or URL filters), wrong App Insights resource, or an empty read; the `plan` line's `Range` shows what was asked |
| No cycle event, heartbeat alive, `CurrentStage=AppInsightsImport` for hours | One very long window; compare earlier days' `FetchSeconds` |

```kusto
customEvents
| where timestamp between (startUtc .. endUtc) and name == "AppInsightsDay"
| project timestamp, Day=tostring(customDimensions.Day), Outcome=tostring(customDimensions.Outcome),
          Stage=tostring(customDimensions.FailedStage), Fetch=todouble(customMeasurements.FetchSeconds),
          Save=todouble(customMeasurements.SaveSeconds), PageViews=todouble(customMeasurements.PageViews),
          Events=todouble(customMeasurements.Events), Fails=toint(customMeasurements.ConsecutiveFailures)
| order by timestamp asc
```

```kusto
customEvents
| where timestamp between (startUtc .. endUtc) and name in ("AppInsightsImportCycle", "AppInsightsDayGivenUp")
| project timestamp, name, Outcome=tostring(customDimensions.Outcome), Reasons=tostring(customDimensions.Reasons),
          FailedDays=tostring(customDimensions.FailedDays), ElapsedSeconds=todouble(customMeasurements.ElapsedSeconds)
| order by timestamp asc
```

### 3.8 Agent costs (Copilot Studio credits and Azure cost)

Stages `AgentCosts` and `CopilotCosts`.

- Gate: `Skipping Copilot Studio credit import.`; `Skipping Azure Cost Management import.`; `Skipping {description} - last ran {u}, and the interval is {n}h.`; `Skipping {description} - its last attempt did not succeed, and it is backing off ...`.
- Copilot Studio: `Copilot Studio per-user credits: read {n} row(s) across ...`; `Copilot Studio credit entitlement: consumed {x} of ...`; Information `Copilot Studio consumption was not imported: no delegated administrator connection ...` (not configured, expected); Error `Copilot Studio credit import failed: ...` or `could not start`; Error `Copilot Studio credit entitlement read failed`; Warning `The per-user Copilot Studio credit read did not succeed. See the agent_cost_import_log entry for the reason.`; `resumed an unfinished read. {n} day(s) ...`.
- Azure cost: `Azure costs for scope '{scope}': read {n} row(s) for ...`; `... the query matched no rows.`; Error `Azure cost import failed for scope '{scope}': ...`; Warning `Azure Cost Management rejected the configured query ...`; Warning `Azure Cost Management import has failed on every attempt since {u}, so it ...`; Warning `Azure cost import is enabled but no scope is configured, so it did nothing.`
- Delegated connection: Error `Copilot Studio credit import failed: agentCosts.import.reconnectNeeded` means Microsoft no longer accepts the connected administrator's sign-in (HTTP 401, or a token refresh it refused), and reconnecting is the fix. Error `Copilot Studio credit import failed: agentCosts.import.routeRefused` means the sign-in was accepted but the licensing route answered HTTP 403: the account lost the Power Platform Administrator role, or Microsoft restricted the per-user consumption routes it has marked as deprecated. Reconnecting alone will not clear it; the exception's inner message has the detail, and the state clears after the next run that reads usage. Builds before #914 logged both as `reconnectNeeded`.
- Recording: Warning `{description} did not fully succeed, so it has NOT been recorded as up to date.`; Warning `{description} was refused, and retrying cannot fix that, so it will wait for the ...`; `Copilot cost preview collection: {Outcome}` - logged at its level (Warning for a failure code) only when the outcome changes, a persisting warning once an hour, and at Debug otherwise (older builds logged every 5-second poll of the cycle pause, about 16,000 lines a day).
- A Power Platform licensing read that answered HTTP 200 with a blank or non-JSON body is a failed read, e.g. Error `Copilot Studio credit entitlement read failed: The Power Platform licensing API answered the {what} request with HTTP 200 and an empty body ...`; older builds took it as an empty page (no consumption, or "no entitlement for this tenant"). A 204 is still an empty page.
- Per-run detail also lands in the `agent_cost_import_log` table and the portal Health page.

Healthy: `read {n} row(s)` lines at the configured interval, no `NOT been recorded`.

| Last evidence | What it proves |
|---|---|
| `... was refused, and retrying cannot fix that` | Permission or configuration (401/403 class); an admin must act, waiting will not help |
| `failed on every attempt since {u}` | Persistent failure for days; read the first Error after `{u}` |
| `matched no rows` | Query returned nothing (scope, filters, period); not a failure by itself |
| `Azure cost import failed ...` with `HTTP 200 ... and an empty body` or `a query result without its 'columns'/'rows'` | Cost Management answered without its rows. Nothing was stored or removed for that scope and a closed month is not marked final; retried next cycle |
| `no delegated administrator connection` | Feature not authorised; expected until an admin connects |
| `skipping ... backing off` repeating | Failure back-off in effect; find the original failure line |

```kusto
traces
| where timestamp between (startUtc .. endUtc)
| where message has_any ("Copilot Studio", "Azure cost", "Azure Cost Management", "Azure costs for scope", "Copilot cost preview")
| project timestamp, severityLevel, message
| order by timestamp asc
```

### 3.9 Migrations and installer upgrade output

Not App Insights: this is `AnalyticsInstaller.exe --initdb` output, which the installer relays line by line (`[child] ...`) and also writes to the Windows application log.

- `Build '{label}' - begin database upgrade.`; `Authenticating to Azure SQL with Microsoft Entra ID (no SQL login in the connection string).`
- Per migration: `[{utc}] MigrationStarting: migration={id} stage=apply.`, then `MigrationOutcome: migration={id} stage=apply outcome=Succeeded elapsedMs={n}.` or `outcome=Failed elapsedMs={n} exceptionType={t} sqlError={n}.` SQL progress: `MigrationSqlProgress: severity={n} sqlError={n}; {text}`. The installer relays `severity=Error` and `outcome=Failed` lines at Error.
- Final: `Database initialised successfully. Everything worked.` Failures: `Initialise database failed with EF. Exception: ...`, `Run custom SQL scripts failed`, `Org URL population check failed`.
- Installer wrapper: `Database initialisation failed: downloaded control-panel app exited with code {n}. Last output: ...`; `Database initialisation completed (exit 0)`. `=== Phase: <name> ===` headers delimit installer phases.
- Blocked process launch: Warning `Downloaded installer launch failed: operation=... exceptionType=... nativeError=...`; Error `Database initialisation did not run because Windows could not launch the downloaded installer.` The remaining tasks continue, and `UpgradeDeployment: stage=complete outcome=Deferred` means the schema still needs upgrading. Deployment cleanup now removes `WEBJOBS_STOPPED` even after failure or cancellation, and `=== Phase: Final WebJobs check ===` checks again before the final install summary. Configuration registration and opening the browser are separate best-effort operations and report their own warnings.
- Downloaded release older than the installer (#814), checked before anything is paused or stopped (#832): Error `UpgradeDeployment: stage=preflight outcome=Refused exceptionType=UnexpectedInstallException; nothing was paused or stopped by this upgrade.`, and the run stops with `FATAL: Unexpected error of type 'UnexpectedInstallException': [UnexpectedInstallException] The control-panel app downloaded for this release is older than this installer, and can only receive the database credentials on its command line, ... so the schema upgrade was not run and nothing was changed in the database.` The same preflight line, followed by `This database uses Microsoft Entra ID authentication, but the control-panel app downloaded for this release has no support for it, ...`, is the refusal of a release without Entra ID SQL support. A release the installer can't classify is not refused; the run logs `NOTE: could not confirm that the downloaded control-panel app reads the database credentials from a file. If the database upgrade fails as soon as it starts, that release is older than this installer: deploy it with the installer from the same release instead.` and goes ahead. The final check still removes a pause setting left by an earlier installer.

| Last evidence | What it proves |
|---|---|
| `MigrationStarting` with no `MigrationOutcome` | Still running (large index builds take a long time) or the process was killed. Migrations are idempotent and resumable, so a re-run converges |
| `outcome=Failed ... sqlError=N` | That migration did not apply and later ones did not run; N names the cause (1205 deadlock, -2 timeout, 207/208 schema) |
| `Database initialised successfully` | Schema current; per-migration `elapsedMs` gives the maintenance-window estimate |
| `outcome=Deferred` after a downloaded-installer launch failure | Schema upgrade did not run, even if content deployment completed. Apply the matching release's manual SQL scripts in migration-id order or upgrade from an approved machine. Cleanup removes the WebJobs pause regardless; importers may fail until schema and binaries match. This is not a successful migration |
| `WebJobsCleanup: outcome=Verified` | A fresh, complete ARM settings list confirmed `WEBJOBS_STOPPED` and the legacy pause marker are absent. An explicitly empty list is valid; a response missing the list is not. This does not prove a WebJob is healthy or the App Service is running |
| `WebJobsCleanup: outcome=Retry attempt=N` | One read-remove-reread of the App Service settings failed, including an incomplete list or verification response; up to three attempts a second apart. Only the final `Verified` or `Failed` line is the outcome |
| `WebJobsCleanup: outcome=Failed` | The final check could not confirm removal. An ARM rejection identifies the failed settings read or update and its HTTP status. For `ScopeLocked`, check inherited resource locks; for `RequestDisallowedByPolicy`, check the policy assignment before retrying. Other service codes and response bodies are not logged. Follow the manual removal instructions in the install summary; do not assume the WebJobs resumed |
| `UpgradeDeployment: stage=resume outcome=Failed` | Removing the upgrade's WebJobs pause right after deployment failed. Not the outcome: the final WebJobs check retries it, so read its `WebJobsCleanup` line. Any earlier `stage=... outcome=Failed` deployment error is still the one to act on |
| `UpgradeDeployment: stage=complete outcome=CleanupFailed` | The deployment stages ran, but the resume above failed. The schema/content result is as the earlier lines say; whether the WebJobs run again is decided by the later `WebJobsCleanup` line |
| WebJobs running after `outcome=Failed`, `Cancelled` or `Deferred`, or after an administrator had paused them | Expected since #911: the installer always removes `WEBJOBS_STOPPED` at the end, whatever the upgrade outcome, and does not restore an earlier manual pause. If the schema did not upgrade, importers may fail until schema and binaries match; pause them again by hand if needed |
| `stage=preflight outcome=Refused` followed by "older than this installer" | The chosen release is older than the installer, so its control-panel app can only take the database credentials on its command line. The run stopped before pausing the WebJobs or stopping the site, and before any database change. Deploy a release at least as new as the installer, or deploy the older release with the installer from that release |
| Portal or importer errors naming a missing column right after a deploy | The upgrade was not applied for that build; the portal Health page shows the pending migration |

### 3.10 Web portal errors (`errorId`)

A remote 500 returns `{ code: "unexpected", errorId: "<guid>" }`. The same value is `customDimensions.ErrorId` on the exception and is in the trace text.

- Error `Unhandled web request error in {context}. [RunId {id}.] ErrorId {guid}. {base exception message}`. The `operation_Id` is the `ErrorId`, unless a Copilot Adoption run owns it (then it is that `RunId`). For Copilot Adoption pages also use events `CopilotAdoptionLifecycle` and `CopilotAdoptionAnalysis` and the decision table in `src/AnalyticsEngine/.github/copilot-instructions.md`.
- A report section, chart or Copilot Adoption warning whose query failed shows the reader a reason and `error reference <guid>`, never the SQL message (`ReportQueryFailure`). The reference is `customDimensions.ErrorId` on the `CopilotAdoptionLifecycle` `QueryFailed` event, or `errorReference=<guid>` in a Warning `WebActivity query failed: key=... reason=... sqlError=... exceptionChain=...`, `Reports query failed: ...` or `TeamsExplorer query failed: ...`. These carry the classification only; the message is deliberately not logged, because SQL Server can quote a data value in it.

| Last evidence | What it proves |
|---|---|
| Exception with `ErrorId` equal to the one quoted | That exact request; read `type`, `SqlErrorNumber`, `HttpStatus` and the stack |
| `QueryFailed` event or `query failed` Warning with the quoted reference | That one degraded section; read `FailureKind`/`reason` and the SQL error number (-2 timeout, 207/208 schema not upgraded, 18456 login) |
| Many distinct ErrorIds with the same `type` and `SqlErrorNumber` | One systemic cause (SQL timeout, schema mismatch), not many bugs |
| No exception for a quoted ErrorId | Telemetry not flushed (host stopping), wrong resource, or the time window is too narrow |

```kusto
let errorId = "00000000-0000-0000-0000-000000000000"; // replace
union exceptions, traces, customEvents
| where timestamp between (startUtc .. endUtc)
| where tostring(customDimensions.ErrorId) == errorId or operation_Id == errorId or message has errorId
| project timestamp, itemType, severityLevel, type, name, message, ErrorId=tostring(customDimensions.ErrorId),
          Sql=tostring(customDimensions.SqlErrorNumber), Http=tostring(customDimensions.HttpStatus),
          FailureKind=tostring(customDimensions.FailureKind), Query=tostring(customDimensions.Query), operation_Id
| order by timestamp asc
```

```kusto
exceptions
| where timestamp between (startUtc .. endUtc)
| summarize n=count(), sampleErrorId=any(tostring(customDimensions.ErrorId))
    by type, Sql=tostring(customDimensions.SqlErrorNumber), Http=tostring(customDimensions.HttpStatus), cloud_RoleName
| order by n desc
```

## 4. Triage routine

1. Orient (section 2): builds, runs, heartbeat gaps, current stage.
2. List `severityLevel >= 3` rows and `exceptions` grouped by prefix: that is the failing list. Then `severityLevel == 2` by prefix for degraded work.
3. For each failing area, read its decision table and name the *last evidence*.
4. For slowness, run the stage and section duration queries, then the area's timing lines.
5. State what the evidence does **not** show: no telemetry before the window, no heartbeat means no data, per-record warnings are capped.
6. Separate expected gaps (Always-On policy overnight in dev, opt-in permissions not granted, features not configured) from real faults.

A Warning is degraded-but-continuing, so do not report it as an outage. An Error is one failed unit of work. Only Critical means a feature is down. The aggregate line (chunk, crawl, phase, queue, cycle) is the unit of truth, not individual Information lines.
