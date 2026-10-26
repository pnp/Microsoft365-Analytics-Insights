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
2. **Startup.** `STARTUP-SQL-CHECK Recovered|Failed|Abandoned: job=..; attempt=..` (Critical on Failed); `FATAL <job>: unhandled exception in <source>; the process is stopping.` (Critical, with the exception chain); `Shutdown requested by host ...` (graceful stop: Kudu stop, restart or deploy); `Unobserved task exception in <job>`.
3. **Heartbeat.** Event `ImporterHeartbeat` about every minute with `JobName, LastCycleUtc, LastCycleDurationSeconds, ProcessStartedUtc, BuildLabel, CurrentStage, StageStartedUtc, BackgroundStage, BackgroundStageStartedUtc`. It proves the process is alive, **not** that it made progress: no `LastCycleUtc` means no cycle has finished in this process yet.
   - A heartbeat gap means the process was not running or telemetry did not flush.
   - **Dev environments often have Always On disabled nightly by policy**, so an overnight gap there is expected. Ask before reporting it.
4. **Where it hung.** The last heartbeat's `CurrentStage` + `StageStartedUtc` is what the process was doing; compare to the last timestamp in the export. `BackgroundStage` is the usage-report import running off the main loop.
5. **Stage outcomes.** Trace `ImportStage start: Stage=<s>; Slot=<Cycle|Background>.`, then event `ImportStage` with dimensions `Stage, Slot, Outcome (Succeeded|Skipped|Failed), Reason, ExceptionType, SqlErrorNumber, HttpStatus` and measurement `DurationMs`. A failed stage also writes Error `ImportStage finish: Stage=<s>; Outcome=Failed; DurationMs=<n>; ...; Reason=<r>.` A `start` with no matching event = still running, or the process died inside it. Stage names include `CallQueueWebhook`, `GraphMainPass`, `GraphDeferredPass`, `ActivityImport`, `RepairDenormalisedCopilotColumns`, `RepairSplitCopilotAgents`, `AgentCosts`, `CopilotCosts`, `LeadershipCohort`, `ConcealedIdentityCleanup`, `Stats`, `AppInsightsImport`, plus the Graph section names.
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
- Outcome: `User import - complete. Inserted {n} new users, updated metadata for {n} existing users (from {n} Graph users)`.
- Checkpoint (Warning): `NOT moving the user checkpoint forward: the Graph /users/delta read did not reach its end (no @odata.deltaLink)`; `NOT committing the Graph delta token: at least one import phase did not complete`; `the /users/delta read stopped on page {n} ({failure}) after {n} user(s)`; `Microsoft Graph rejected the stored /users/delta token`; `the stored /users/delta checkpoint was cleared while this import was running`.
- Identity: `identity reconciliation: {n} renamed in place (...), {n} placeholder row(s) adopted (...), {n} quarantined, {n} retried from earlier cycles, {n} resolved, {n} dropped (...), in {ms} ms.` (Warning when anything is quarantined or the store was unavailable); `identity quarantine: {n} user(s) quarantined, oldest since {u}, {n} retried, {n} resolved` (Warning while any remain); Errors `identity quarantine: could not save ...`, `... stored set could not be read ...`, `... more than the {max} the state store keeps`.
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

- Gate: `Skipping activity reports as have processed recently (less than {n} hours ago)`; `Reading all activity reports from {n} days back (the UsageReportsDaysBack setting ...)`; `ForceUsageReportsImport=true; bypassing ...`.
- Per report: `Importing {thing} reports...`; `{thing}: skipping {n} already-stored finalized day(s)`; `{thing}: downloading the full {n}-date window from Graph ...`; `Imported {n} {thing} reports.`
- **Phase summary (one line)**: `Usage report phase: {n} of {m} daily report(s) imported, {n} failed, {n} refused for concealed identities; {n} row(s) read, {n} written, in {ms} ms. Per report: {name} {r} read/{w} written, {d} date(s) downloaded, {s} finalized date(s) skipped, {ms} ms; ... Failed: ... Refused: ... SharePoint sites weekly usage: {status}.` Warning if any failed or refused.
- Failures: Error `Activity reports did NOT fully import - {n} of {m} report(s) failed: ...`; Error `{report} failed with HTTP {code} ...`; Warning `{loader}: every identity Graph returned for {date} is concealed (hashed) ...` and `skipping {n} of {m} row(s) whose identity ...`. Concealment is a Microsoft 365 admin-centre privacy setting; the importer cannot retry past it.
- Copilot per-user report: `Copilot per-user report {request}: parsed {n} row(s), wrote {n} to SQL.`; Warning `... is not available on this tenant: ...`; Error `... came back with concealed user identities for all {n} row(s)`.
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
- **Timing**: `save-phase timing (aggregate ...): dedup+scope {s}s, staging+merge {s}s, metadata {s}s.`; `metadata breakdown - EF-load {s}s, Copilot resource resolution {s}s, Copilot SQL commit {s}s, Power Platform {s}s, EF save-changes {s}s.`; `Copilot commit timing: SP-docs ..., Teams ..., chat-only ...` (Information only when the commit took 5 s or more).
- Inline enrichment, per batch (Warning): `Copilot enrichment: {n} interaction(s) in this batch were saved without their file or meeting details because Microsoft Graph failed transiently ... queued for durable re-enrichment`; or `... saved chat-only ... no durable retry queue is configured (no Storage connection string)`. `Copilot enrichment in-lock lookups: {n} lookup(s), {n} cache hit(s), {n} resolved, {n} unresolvable, {n} transient ({n} skipped Graph after a recent transient failure, {n} hit the deadline); {n} Graph call(s), no SDK retries; elapsed {ms} ms.` (Warning when any transient, Information when slow).
- **Queue summary**: `Copilot enrichment queue: due {n}, claimed {n}, completed {n}, terminal {n}, deferred {n}, abandoned {n}, leased-skipped {n}; oldest due {m} min ago; max attempts {n}; (run budget of {n} min reached with work still due | no more work due); elapsed {ms} ms.` Warning when deferred, abandoned or budget reached. Follow-ups: Warning `{n} item(s) completed without file or meeting details because retrying can never succeed` (`terminal`); Error `{n} item(s) abandoned after {k} failed attempts or {d} days, and parked as 'abandoned-<event id>' rows in the CopilotEnrichment state partition`.
- Graph lookups (Warning, capped): `Transient Graph error resolving ... (status {s}, code {c})`; `Copilot meeting enrichment is unavailable in this tenant`; HTTP 403 and 404 variants.
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

- Subscription: `Created subscription id '{id}' for webhook at '{url}' in {ms} ms`; `Renewed subscription id ... New expiry: {u}.`; `Replacing call-records subscription id ...: {reason}`; Warning `Subscription lapsed: subscription id ..., {what}`; Warning `The client secret has changed since subscription id ... was created ...` (the subscription is replaced); Warning `Graph refused a second call-records subscription: ...`, then Error `Deleted subscription(s) ... after a 409 Conflict, but the replacement could not be created`; `Webhook warm-up answered in {ms} ms.` / Warning `Warming the webhook up failed ...`.
- Timer: `Subscription timer started: checks every {n} minutes, independently of the import cycle`; Error `Subscription timer tick failed unexpectedly (...); the timer carries on.`; `Recovered: the subscription check succeeded on attempt {n} in {ms} ms, after failing since ...`; Critical for a missing client secret (subscription cannot be kept).
- Queue: `ServiceBus client: Now listening for service-bus messages.` / Warning `Not listening for service-bus messages?`; Error `ServiceBus processor encountered an exception` with Namespace, Entity Path and Error Source lines; `New message received from ServiceBus with ID ...`; `Succesfully processed & completed ServiceBus message ID ...`.
- **Per-call outcome**: Warning `CallImport outcome: call='{id}' outcome=Retry:{kind} failure={code} elapsedMs={n}.`; Error `CallImport outcome: call='{id}' outcome=DeadLettered:{reason}`. Pause: Warning `Teams calls import paused for {n} minute(s): more than one call has failed in a way ...`, then `Teams calls import resumed: a call was saved after the pause.`
- Dead-letter replay tool: `Teams calls dead-letter replay ...`.
- Startup: Critical `Teams calls import is enabled but Service Bus is not configured. Skipping Call Queue import & webhook validation.`

Healthy: `Created`/`Renewed` on schedule with a future expiry, one `listening` line per process, `Succesfully processed` per message, no `DeadLettered`.

| Last evidence | What it proves |
|---|---|
| Last `Renewed`/`Created` has an expiry in the past, no later subscription lines | The timer is not running (process down, or ticks failing: see `tick failed`); no call notifications arrive |
| `Subscription lapsed`, then `Created` | Self-healed; a problem only if it repeats quickly |
| `Graph refused a second call-records subscription ... 409` | Another subscription exists for the resource; the replacement path runs. An Error on the next line means calls are down |
| `Webhook warm-up failed` or a Graph validation timeout | Web app cold or unreachable from Graph; it must answer within seconds |
| `DeadLettered:{reason}` | The notification can never be imported as it stands; the reason names why, and the replay tool exists |
| `paused for N minute(s)` | Systemic failure (SQL down, for example); resumes after one success. The `Retry:{kind}` lines before it say why |
| `Not listening for service-bus messages?` or processor errors | Queue connectivity or RBAC; messages accumulate in Service Bus |

```kusto
traces
| where timestamp between (startUtc .. endUtc)
| where message has "[Calls Webhook]" or message startswith "CallImport outcome:" or message startswith "Teams calls import" or message has "ServiceBus"
| project timestamp, severityLevel, message
| order by timestamp asc
```

### 3.7 App Insights importer (per-day outcomes)

Job `AppInsightsImporter`, stage `AppInsightsImport`. Reads App Insights by an ingestion-time cursor, one bounded window at a time.

- **Event `AppInsightsImportCycle`**: dimensions `Outcome (Succeeded|Degraded|Failed), Mode, Reasons, FailedDays, CursorUtc` plus exception dimensions; measurements `Days, Windows, PageViews, Events, ElapsedSeconds`. Trace twin `AppInsightsImportCycle: Outcome=..; Mode=..; Days=..; Windows=..; PageViews=..; Events=..; ElapsedMs=..; Reasons=..; FailedDays=..` (Information, Warning for Degraded, Error for Failed).
- **Event `AppInsightsDay`**, one per day: `Day, Outcome (Saved|NoData|Failed|RetriesStopped), Mode, FailedStage (Download|Save), FailedSections`; measurements `Windows, PageViews, Events, FetchSeconds, SaveSeconds, ConsecutiveFailures`. Trace twin `AppInsightsDay: Day=..; Outcome=..; ...`.
- **Event `AppInsightsDayGivenUp`**: sent once when forced retries of a day stop (`Attempts`).
- Cursor: `AppInsightsCursor: Outcome=Advanced|ReadFailed|WriteFailed; Cursor=..; Previous=..`; Warning when the cursor says data was imported but the hits table is empty; `AppInsightsImport plan: Mode=..; Range=..; Cursor=..; NewestStoredHit=..`; Error `AppInsightsRowCounts: Outcome=Failed ...`.
- Failures: Error `Failed {what} data for day {date} (attempt {n} of {max}).` and `... again ({n} failed attempts in a row). Stopped forced retries of this day ...`; `Day {date} failed in an earlier cycle; starting from it so it is retried before any later day.`
- Progress: `Importing day {i}/{n}: {date} in {p} page-view and {e} event window(s)...`; `Left out data by people outside UserGroupsFilter for {date}: ...`; `Import loop finished: {n} days processed ...`.
- Host: Warning `Web-traffic import is disabled in app configuration. Stopping here until the app is restarted.`; Critical `No Application Insights connection string found - can't continue.` and `Check settings & restart app. The Application Insights import is not running.`

Healthy: `AppInsightsImportCycle` Succeeded each interval, days `Saved` or `NoData`, cursor `Advanced`.

| Last evidence | What it proves |
|---|---|
| Cycle `Failed`, same `FailedDays` repeating, `ConsecutiveFailures` climbing | The cycle stops at the failed day and retries it before any later day, so nothing after it imports. `FailedStage` separates download (App Insights query, permission, throttle) from save (SQL) |
| `AppInsightsDayGivenUp` | After the attempt limit the cycle moves past that day; it stays a gap until fixed |
| Cycle `Degraded` with `Reasons` | Imported with a caveat; not a hard failure |
| `AppInsightsCursor: Outcome=WriteFailed` | Data saved but the cursor was not; the next cycle re-reads. State store unreachable |
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
- Recording: Warning `{description} did not fully succeed, so it has NOT been recorded as up to date.`; Warning `{description} was refused, and retrying cannot fix that, so it will wait for the ...`; `Copilot cost preview collection: {Outcome}`.
- Per-run detail also lands in the `agent_cost_import_log` table and the portal Health page.

Healthy: `read {n} row(s)` lines at the configured interval, no `NOT been recorded`.

| Last evidence | What it proves |
|---|---|
| `... was refused, and retrying cannot fix that` | Permission or configuration (401/403 class); an admin must act, waiting will not help |
| `failed on every attempt since {u}` | Persistent failure for days; read the first Error after `{u}` |
| `matched no rows` | Query returned nothing (scope, filters, period); not a failure by itself |
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

| Last evidence | What it proves |
|---|---|
| `MigrationStarting` with no `MigrationOutcome` | Still running (large index builds take a long time) or the process was killed. Migrations are idempotent and resumable, so a re-run converges |
| `outcome=Failed ... sqlError=N` | That migration did not apply and later ones did not run; N names the cause (1205 deadlock, -2 timeout, 207/208 schema) |
| `Database initialised successfully` | Schema current; per-migration `elapsedMs` gives the maintenance-window estimate |
| Portal or importer errors naming a missing column right after a deploy | The upgrade was not applied for that build; the portal Health page shows the pending migration |

### 3.10 Web portal errors (`errorId`)

A remote 500 returns `{ code: "unexpected", errorId: "<guid>" }`. The same value is `customDimensions.ErrorId` on the exception and is in the trace text.

- Error `Unhandled web request error in {context}. [RunId {id}.] ErrorId {guid}. {base exception message}`. The `operation_Id` is the `ErrorId`, unless a Copilot Adoption run owns it (then it is that `RunId`). For Copilot Adoption pages also use events `CopilotAdoptionLifecycle` and `CopilotAdoptionAnalysis` and the decision table in `src/AnalyticsEngine/.github/copilot-instructions.md`.

| Last evidence | What it proves |
|---|---|
| Exception with `ErrorId` equal to the one quoted | That exact request; read `type`, `SqlErrorNumber`, `HttpStatus` and the stack |
| Many distinct ErrorIds with the same `type` and `SqlErrorNumber` | One systemic cause (SQL timeout, schema mismatch), not many bugs |
| No exception for a quoted ErrorId | Telemetry not flushed (host stopping), wrong resource, or the time window is too narrow |

```kusto
let errorId = "00000000-0000-0000-0000-000000000000"; // replace
union exceptions, traces
| where timestamp between (startUtc .. endUtc)
| where tostring(customDimensions.ErrorId) == errorId or operation_Id == errorId or message has errorId
| project timestamp, itemType, severityLevel, type, message, ErrorId=tostring(customDimensions.ErrorId),
          Sql=tostring(customDimensions.SqlErrorNumber), Http=tostring(customDimensions.HttpStatus), operation_Id
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
