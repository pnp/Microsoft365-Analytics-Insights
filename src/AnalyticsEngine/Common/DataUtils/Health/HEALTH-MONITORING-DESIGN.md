# Default system-health monitoring & alerting — design record

Tracks issue [#144](https://github.com/pnp/Microsoft365-Analytics-Insights/issues/144).

This is the **engineering design record** for opt-out monitoring provisioned by the installer.
The first part describes what is implemented: it is the current contract. The
[design history](#design-history-the-144-proposal) after it keeps the original issue proposal for
context, and notes where the delivered design took a different route; it is **not a claim that every
proposed signal is implemented**. The operator procedure for alerts is the wiki's
[Health Alerts](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Health-Alerts) page.

## Delivered monitoring contract

New wizard configurations enable monitoring and require one notification email or an explicit
opt-out. Old JSON without `HealthMonitoring` stays opted out. The existing saved schema is unchanged.
The installer writes `EnableDefaultHealthChecks` to the web app in both enabled and disabled states;
the importers read it on startup. This runtime setting is not a new persisted installer property.

When enabled, each expected importer runs its checks immediately and every five minutes, off the
import loop: SQL and credential in both importers, plus the two Copilot cost-connection checks in the
activity importer. Each has a 30-second budget; cancellation is requested at timeout, late work
cannot emit success after disposal, and a non-cooperative check is not launched again until it returns.
The other checks and the one-minute process heartbeat continue while an import/check is stuck.

- **SQL:** connects only to the configured database, reads read/write metadata and its
  `__MigrationHistory`, and compares that context's complete history with this build's local
  migration metadata. No EF initializer, migration application, table scan, `master` connection or
  write probe is run. This detects missing/newer/wrong-context history, not arbitrary manual schema
  damage, storage exhaustion in advance, or the runtime user's effective write permissions.
- **Credential:** requests a fresh Graph `.default` token using a new credential instance, bypassing
  the importer's token cache. Token issuance proves authentication, **not API permission grants**.
  Certificate authentication uses the same cached certificate as the running importer and reports
  its `NotAfter` (<14 whole days degraded, <3 unhealthy). A successful secret-token acquisition is
  **degraded/expiry unknown**, never an invented expiry date or a claim that the secret cannot expire.
  No extra app-read permission, secret inspection or invasive permission probe is introduced.
- **Copilot cost connections (activity importer only):** `CopilotCostApi` and
  `CopilotStudioConsumption` report the stored state of the Copilot credit-history connection and
  the Copilot Studio billing connection, read from Azure Table storage. A collection that is switched
  off reports healthy (`costApi.disabled`); a connection that needs renewal or was refused reports
  unhealthy.
- The checks emit privacy-safe `HealthCheck` results with `JobName`, `Monitor=DefaultHealth` and
  stable `ReasonKey`. Exceptions, connection strings, tokens and tenant identifiers are not copied
  into those events. The existing bilingual Health page recognises these reasons, prefers fresh
  token evidence over its certificate-date-only check, and does not show stale success as current.

One action group (`<web-app>-health-notifications`) and **at most seven** severity-2 rules are managed:

| Rule | Default / boundary |
|---|---|
| Activity importer health | Only when a non-web import is selected |
| Web-traffic importer health | Only when web traffic is selected |
| Logged SQL / Teams permission failures | Only when at least one importer is expected |
| SQL CPU | Average >90% for 1 hour; CPU is available across DTU/vCore models, but is not a DTU IO alert |
| SQL storage | Average >90% for 15 minutes |
| Shared App Service plan CPU | Average >90% for 1 hour |
| Shared App Service plan memory | Average >90% for 1 hour |

Each importer rule evaluates every five minutes and combines: missing heartbeat (configurable
15–1440 minutes), no completed cycle for 24 hours (including a first cycle still unfinished after
24 hours, across restarts observed within the two-day query window), latest unhealthy check,
known certificate expiry <14 days, and any expected check absent for 30 minutes. The activity
importer's rule expects its two cost checks only when the installer run that wrote the rule also
deployed the release content: an older importer never sends them, so otherwise only SQL and
credential are expected.
Latest-per-component results allow recovery; an old failure alone does not pin the rule on.
A completed loop is **not** proof of successful sub-imports or non-zero rows. The passive rule
detects >10 logged SQL exceptions in one hour (both SqlClient namespaces), >2 logged full/read-only
SQL exceptions in ten minutes, and the existing explicit Teams-user permission-failure trace.
No matching logged failures is not proof of permission/data-flow health.

Only exact deterministic names bearing this deployment's ownership hash are reconciled. An unowned
name collision blocks the monitoring update before any write. Upgrades **update owned definitions,
recipients and thresholds**; portal edits to these installer-owned rules are not preserved. Use
separately named custom rules for operator-managed thresholds. Opt-out disables all owned log and
metric rules, removes their actions, retains the action group, and stops active probes after the
updated runtime settings are loaded; the basic process heartbeat/operational telemetry remain.
Unknown/unowned resources are never adopted, disabled or deleted. Provisioning is best-effort:
failure is logged by the installer and must be remediated, but does not abort the product install.

### Cost, scale and planned shutdowns

Monitoring is not free: log/metric alert evaluation and telemetry ingestion/retention can be billed.
At two jobs, the baseline is about 2,880 periodic heartbeat events and 1,728 check events per day
(four checks in the activity importer and two in the web-traffic importer, every five minutes),
plus cycle-completion heartbeats. Up to three log rules and four metric rules evaluate every five
minutes. The per-job log query reads at most two days of telemetry to distinguish a long first cycle
from a fresh startup; this is a bounded query horizon, **not a currency spending cap**. Review Azure's
current pricing and retention for the deployment. An already opted-in liveness-only installation
gains the additional rules on upgrade; budget for that expanded scope.

SQL checks read only migration-history metadata (O(number of migrations)), not the 200,000-user or
multi-million-row fact tables. Token checks are fixed per process, not per user. No new SQL table,
configuration table or migration is required.

Expected shutdowns, upgrades and deliberately paused jobs need an Azure alert processing rule for
the maintenance interval or an explicit monitoring opt-out. Suppression is not a claim of health.
Newly created rules can alert before the first deployment/check telemetry arrives. Restoring a host
does not prove email delivery: inspect rule evaluation and use an explicitly authorised action-group
test against a disposable target.

### Remaining issue acceptance / live gates

Offline tests cover the real ARM SDK resource-list/deployment serialization branches, ownership,
opt-out/idempotency, KQL syntax/type binding against an App Insights schema, token/expiry/timeout
branches, isolated LocalDB history drift/read-only checks and wizard round trips/layout.
They do **not** prove Azure accepts/deploys every rule, actual KQL results in the service, firing,
recovery notifications or email delivery. Those require a disposable target and explicit permission.

The complete #144 acceptance remains open: proactive **client-secret expiry**, active Graph/Activity
permission/anonymisation checks, SQL DTU/data/log-IO/deadlocks, Service Bus metrics/dead letters,
Key Vault/Storage reachability, web availability, and workload-gated **non-zero data freshness**
are not all delivered here. No Redis monitor is appropriate: Redis has been retired.
The broader catalog in the design history below is a proposal, not an enabled default or an approved
cost ceiling.

## What's implemented now

The Appendix E **uniform telemetry schema**, emitted through the existing `AnalyticsLogger` →
Application Insights pipeline (no new sink):

- **`HealthCheck`** custom event — one per check per cycle:
  - `Component` — one of `DataUtils.Health.HealthComponent` (`Sql`, `ActivityApi`, `Graph`, `KeyVault`,
    `ServiceBus`, `Credential`, `Dns`, `BlobCheckpoint`, `CopilotAuditBackfill`, `MessageTracing`,
    `CopilotCostApi`, `CopilotStudioConsumption`).
  - `Status` — one of `DataUtils.Health.HealthStatus` (`Healthy` | `Degraded` | `Unhealthy`).
  - `Detail` — optional free-text reason. **Must not contain secrets or customer data.**
  - `DaysToExpiry` — integer dimension in invariant text (`toint` in KQL), `Credential` only.
- **`ImporterHeartbeat`** custom event — every minute independently, and when a cycle completes:
  - `JobName` (e.g. `Office365ActivityImporter` | `AppInsightsImporter`),
    `ProcessStartedUtc`, and (only after a completed cycle) `LastCycleUtc`, `LastCycleDurationSeconds`.

Why this first: a uniform signal lets runtime alerting collapse to **1–2 generic rules**
(`any HealthCheck Status == Unhealthy`, `Credential DaysToExpiry < N`, `no ImporterHeartbeat in N min`)
instead of ~12 bespoke log queries, and it is the shared building block reused by both the installer's
`SolutionInstallVerifier` checks and the runtime heartbeat.

```csharp
var log = new AnalyticsLogger(appInsightsConnectionString, "Office365ActivityImporter");
log.TrackHealthCheck(HealthComponent.Sql, HealthStatus.Healthy, "schema v123 == expected");
log.TrackHealthCheck(HealthComponent.Credential, HealthStatus.Degraded, "secret expiring soon", daysToExpiry: 12);
log.TrackImporterHeartbeat("Office365ActivityImporter", DateTime.UtcNow, cycleDurationSeconds);
```

### Also delivered: in-app Health dashboard + query client + alert-setup guide

Beyond the telemetry primitive, this work now also ships the **Phase-2 surfacing** described below:

- **`Common/DataUtils/AppInsights/AppInsightsQueryClient.cs`** — a small, self-contained KQL client for the
  App Insights REST query API, authenticated with the app's existing Entra credential (no new key/config). It
  is intentionally separate from the importer's `AppInsightsAPIClient` (which is coupled to the importer's
  response parsers) to avoid destabilising the importer; consolidating the two onto one client remains a
  future cleanup.
- **Web Health section (in the portal)** — a `Service health` destination in the React portal SPA
  (`Scripts/portal/src/pages/HealthPage.tsx` + `components/health/*` + `api/healthApi.ts` +
  `types/health.ts`, routed from the route table in `navigation.tsx`), backed by the `api/Health` controller (`HealthAPIController` →
  `Web/Models/Health/HealthService.cs` + `HealthModels.cs`). The page is split into **lazily-loaded
  sub-section tabs**, each backed by its own independently-cached endpoint
  (`api/Health/{summary,data,liveness,exceptions,components,config}`, best-effort, 60 s-cached, single-flight
  per section): the SPA only fetches the sub-section the user opens, and a slow/failing source degrades that
  one section instead of the whole page. The **Overview** tab leads with the single **overall traffic-light**
  (rolled up by the pure, unit-tested `DataUtils.Health.HealthRollup`) via the cheap `summary` endpoint, which
  deliberately **skips the heavy SQL row-count/freshness scans** (it only probes DB reachability with
  `SELECT 1`). The heavy Data section runs on its own tab with a short per-query `CommandTimeout`, its two big
  tables scanned in parallel and each folded into a single 24h/7d+freshness pass, so on a very large tenant
  (whose `audit_events`/`hits` timestamp columns are un-indexed) it degrades to a "scan didn't complete" note
  rather than hanging the request. Sub-sections:
  - **Import liveness** — `FinishedImportCycle` per job, `FinishedSectionImport` per section,
    `ImporterHeartbeat` (when it lands), **plus a web-tracker `pageViews`-in-App-Insights probe** that tells
    a "tracker not deployed" apart from an "AppInsightsImporter not running".
  - **Exceptions overview** — 24 h total, a **zero-filled 24-bar** per-hour sparkline, top types, and a
    dedicated **SQL capacity / read-only** sub-count (count only — no message text is surfaced).
  - **Component health** — the two proactive checks the web app can run itself: the runtime
    **credential expiry** (certificate `NotAfter` → days-to-expiry; a client secret's expiry isn't
    visible at runtime) and the **Service Bus** Teams-calls queue depth / dead-letter count. It also
    shows the latest `HealthCheck` events from Application Insights, which include the importers' SQL,
    credential and Copilot cost-connection checks. Activity API, Graph, Key Vault and DNS have no
    runtime check yet.
  - **Data overview** — **scale-safe**: approximate row counts per workload (from
    `sys.dm_db_partition_stats`, so a 200k-user tenant is never hit with `COUNT(*)` on fact tables),
    last-24 h / last-7 d volume on the indexed audit + hits tables, newest audit/hit freshness, and DB size.
  - **Configuration** — enabled imports, resource endpoints, the Teams call-records **webhook subscription
    state + expiry** (reusing the homepage `SystemStatus` logic), and the **schema/migration version**
    (`DbMigrator.GetPendingMigrations()` → "up to date with this build" vs "N pending — DB behind build").

  All App-Insights-backed queries authenticate with the app's existing Entra credential **honouring
  certificate auth** (`UseClientCertificate` → `ClientCertificateCredential`), not a hard-coded client
  secret, so the AI cards work on certificate-based installs too.
- **Alert-setup guide** — the repeatable "add an alert" procedure plus the health-telemetry rules are
  published to the wiki's
  [Health Alerts](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Health-Alerts) page.

## Relationship to existing telemetry (no duplication)

The solution **already** emits two custom events the wiki's
[Confirm Import Cycles & Sections Are Finishing (custom events)](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Monitoring#confirm-import-cycles--sections-are-finishing-custom-events)
section alerts on, via `AnalyticsLogger.TrackEvent` / `JobTimer.TrackFinishedEventAndStopTimer`:

- **`FinishedImportCycle`** — one per full importer loop (`Office365ActivityImporter` and
  `AppInsightsImporter`), with a `context` dimension = elapsed-time string. `AppInsightsImporter` sends it
  only for a cycle that got to the end (outcome `Succeeded` or `Degraded`), never for one that stopped at a
  failed day; every one of its cycles, whatever the outcome, also sends an `AppInsightsImportCycle` event
  with `Outcome`, `Reasons` and `FailedDays`, and each day it reads sends an `AppInsightsDay` event.
- **`FinishedSectionImport`** — one per section (audit, user metadata, user apps, usage activity, Teams,
  sent emails, App Insights hits/custom events).

These **are** the wiki's cycle/section-completion signals, so there is deliberate overlap and the plan
is to **reuse them, not duplicate them**:

- **Data-flow / freshness** monitoring (issue #144 default alert #9, "Activity import stalled") was
  planned to codify the wiki query (`FinishedImportCycle` count == 0 over the window) as a shipped
  default rule. The delivered importer rule reads `LastCycleUtc` from `ImporterHeartbeat` instead (no
  completed cycle for 24 hours), so it also catches a first cycle that never finishes.
- **`ImporterHeartbeat`** is **not** a rename of `FinishedImportCycle`. `FinishedImportCycle` fires only
  when a cycle *completes*, so a job stuck **mid-cycle** emits nothing and looks identical to a dead job.
  The Appendix E heartbeat is raised on an **independent timer** so a stuck import can't suppress the
  liveness beat. It also adds structured `JobName` / `LastCycleUtc` / `LastCycleDurationSeconds`
  dimensions (vs the free-text `context` string) so the same rule works across both jobs. Each importer
  hosts its own heartbeat, on a one-minute timer in the same process; `FinishedImportCycle` remains a
  separate loop-completion signal, not a dependency or non-zero-data success guarantee.

## Design history: the #144 proposal

The rest of this record is the proposal the work started from, kept for its reasoning. Where the
delivered design differs, the sections above are the contract. Where a part of the proposal was built
differently, or not at all, its section below says so.

### What should be monitored

- **A. Liveness** — continuous web-jobs (`Office365ActivityImporter`, `AppInsightsImporter`) alive &
  completing cycles (interim: existing `FinishedImportCycle` event; target: independent-timer
  `ImporterHeartbeat` that survives a stuck mid-cycle import — see "Relationship to existing telemetry"
  above); web app reachable (tracker-config / Teams-auth API / Teams call-record webhook).
- **B. Data flow / freshness** — activity cycle completing, audit events non-zero, user/Teams metadata
  & usage-report imports completing, `AppInsightsImporter` writing hits, web-tracker `pageViews`
  arriving; backlog/lag (a full cycle must finish < 24h).
- **C. Credentials & permissions** — runtime credential valid *now* (token acquisition);
  proactive secret/cert **expiry warning** (`DaysToExpiry`); Graph/Activity API permission loss (403 /
  consent revoked); tenant usage-report anonymisation turned on.
- **D. Data store & dependency health** — SQL reachable **+ schema/migration version == expected**, SQL
  capacity (full/read-only, DTU/vCore %, storage %), Service Bus reachable + dead-letter depth, Key Vault
  reachable, Storage reachable (blobs, and the Table service that holds the audit blob checkpoint and the
  runtime state table `AnalyticsState`).
- **E. Capacity / performance KPIs** — App Service Plan CPU/memory, SQL DTU/vCore + storage growth,
  Service Bus throttling.
- **F. Safety net** — spike in App Insights `exceptions` rate as a **general health probe**. Every
  web-job logs unhandled/handled errors through `AnalyticsLogger.TrackException` into the same App
  Insights instance, so a rising `exceptions` count is a cheap catch-all for failures that no specific
  check anticipates (a new/unknown error, a dependency the checks don't cover, a code regression). Ship
  a default rule on total `exceptions` volume in addition to the specific `SqlException` /
  "database is read-only" rules below.

### Where to surface — options

1. **Installer provisions Azure Monitor / App Insights alert rules + a default action group.** Native,
   no extra compute, "recommended set for free"; but resource sprawl, weak for *proactive* checks
   (cert expiry before failure, schema correctness) that aren't in the logs today.
2. **Runtime heartbeat health-check (recommended core).** A lightweight timer task runs the
   verifier-style checks at runtime and emits the structured `HealthCheck` / `ImporterHeartbeat`
   signals above. Captures what passive log-mining can't (proactive expiry, schema drift, active
   dependency validation); the heartbeat itself *is* the liveness signal; alerting collapses to 1–2
   rules. New component to host; must keep running even when the main import is stuck.
3. **App Insights availability test against a web-app `/health` endpoint.** External perspective, easy
   alerting; only as good as what the endpoint inspects.

**Recommendation — hybrid:** Option 2 as the core (this telemetry schema), delivered via Option 1 (the
installer provisions a default action group + a small heartbeat-driven rule set), optionally Option 3 +
a `SystemStatus` health page; whole thing **opt-out**, thresholds documented + overridable.

### Central health dashboard (web-app tab)

**Delivered differently.** The proposal was a server-rendered MVC tab that reused the importer's
`AppInsightsAPIClient`. What shipped is the portal's **Service health** page (`/admin/health`), backed
by `api/Health` and a separate `Common/DataUtils/AppInsights/AppInsightsQueryClient.cs`: see *Also
delivered* above. It kept the proposal's cards (component health, import liveness, exceptions overview
and data overview), added a configuration section, and caches each section for 60 seconds so opening
the page doesn't hammer the query API.

### Reusing `SolutionInstallVerifier` at runtime

**Not done this way.** The delivered SQL and credential checks were written for the runtime in
`Common` (`Common/Entities/Health/DefaultRuntimeHealth.cs` and
`Common/DataUtils/Health/RuntimeCredentialHealth.cs`), and `SolutionInstallVerifier` remains the
installer's own. Activity API, Graph, Key Vault and DNS still have no runtime check, and the Service
Bus queue is read by the web app's Service health page rather than reported as a `HealthCheck`. The
proposal was:

`App.ControlPanel.Engine/SolutionInstallVerifier.cs` already implements most checks (SQL, runtime-credential
token acquisition, Graph/Activity permission probes, Key Vault data-plane, DNS) but only at install time.
Refactor the check logic into a reusable health component in `Common` so the installer ("Test
Configuration") and the runtime heartbeat call the same code, each emitting the matching `HealthCheck`:

| Verifier method | Runtime check | Signal |
|-|-|-|
| `VerifySQL(...)` | SQL connect + schema/migration version == expected | `HealthCheck{Component=Sql}` |
| `VerifyActivityAPIImport` | Activity API token + reachability | `HealthCheck{Component=ActivityApi}` |
| `VerifyTeamsAndUserActivityImport` | Graph token + Teams/user permission probe | `HealthCheck{Component=Graph}` |
| `VerifyKeyVaultDataPlaneAccess` | Key Vault read | `HealthCheck{Component=KeyVault}` |
| `VerifyResourceDnsResolution` | DNS/endpoint reachability | `HealthCheck{Component=Dns}` |
| (new) | Credential days-to-expiry from KV secret/cert attributes | `HealthCheck{Component=Credential, DaysToExpiry=n}` |
| (new) | Service Bus reachability + dead-letter depth | `HealthCheck{Component=ServiceBus}` |

### Proposed default "ships-by-default" alert set

Kept small; heartbeat-driven where possible. All route to a single default action group (recipient
configured at install). The last column says what was delivered; the delivered rules are the ones in
[Delivered monitoring contract](#delivered-monitoring-contract).

| # | Monitored item | Signal source | Type | Default threshold | Delivered as |
|-|-|-|-|-|-|
| 1 | Any component unhealthy | `HealthCheck Status == Unhealthy` | log/heartbeat | any in 15m | Part of each importer rule: the latest check per component |
| 2 | Web-jobs not heartbeating | `ImporterHeartbeat` absent (interim: `FinishedImportCycle` absent) | log/heartbeat | none in 30m | Part of each importer rule: missing heartbeat, 15–1440 minutes |
| 3 | Runtime credential expiring | `HealthCheck{Component=Credential}.DaysToExpiry` | metric/log | < 14 warn, < 3 critical | Certificates only: a client secret's expiry is unknown at runtime |
| 4 | SQL unreachable / schema mismatch | `HealthCheck{Component=Sql}` | log/heartbeat | any Unhealthy in 15m | Part of each importer rule |
| 5 | SQL full / read-only | `exceptions ... "database is read-only"` | log | > 2 in 10m | The logged-failures rule |
| 6 | SQL capacity | DTU/vCore % + storage % | metric | sustained high | SQL CPU and storage rules; no DTU or IO rule |
| 7 | App Service Plan capacity | CPU/memory % | metric | > 90% for 1h | Yes |
| 8 | Service Bus backlog (Teams calls) | dead-letter / throttled requests | metric | > 0 | Not delivered |
| 9 | Activity import stalled | existing `FinishedImportCycle` / `FinishedSectionImport` custom events (wiki) | log | count == 0 in window | Part of each importer rule: no completed cycle for 24 hours, from `ImporterHeartbeat` |
| 10 | Exception spike (general probe) | total App Insights `exceptions` volume | log | sustained spike vs baseline | Not delivered; the logged-failures rule counts SQL exceptions only |

### How the delivered implementation differs from the proposal

- **Heartbeat host:** each importer raises `ImporterHeartbeat` on its own one-minute timer, in the
  same process; `RuntimeHealthMonitor`, started by `DefaultRuntimeHealth`, runs the checks every five
  minutes. There is no separate WebJob or Function.
- **Shared checks:** written for the runtime, as described under *Reusing `SolutionInstallVerifier`
  at runtime* above.
- **Provisioning:** `HealthMonitoringInstallTask` (in `Common/CloudInstallEngine/Azure/InstallTasks`)
  creates the action group, log-search rules and metric alerts. It is non-critical: a failure is logged
  and doesn't stop the install.
- **Configuration:** the saved installer setting `HealthMonitoring` (`Enabled`, `NotificationEmail`,
  `MissingHeartbeatMinutes`) and the runtime app setting `EnableDefaultHealthChecks`.
- **Surfacing:** the portal's Service health page; see *Central health dashboard* above.
- **Upgrades:** an upgrade updates the rules the installer owns, the opposite of the proposal's
  create-if-absent. Operators keep their own thresholds in separately named rules.

### Phasing

- **Phase 1 (MVP):** heartbeat liveness, SQL/schema, runtime-credential validity and certificate
  expiry, the action group, and rules #1–7 and #9 are delivered, within the limits the table above
  notes. The exception-spike probe (#10) isn't.
- **Phase 2:** the central health dashboard is delivered. Still open: the remaining dependency checks
  (Service Bus dead letters as an alert, Key Vault), data-freshness alerts and a web-app
  availability test.
- **Phase 3:** Azure workbook/portal dashboard, richer overridable thresholds, extra notification
  channels (Teams/webhook/ITSM), tenant report-anonymisation re-check, cost/quota anomaly alerts.

### Alert-setup guide

The admin guide drafted here is published, and maintained, as the wiki's
[Health Alerts](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Health-Alerts) page.

### Questions the delivered design settled

- **Action-group recipient:** one notification email address. Teams, webhook, Logic App and ITSM
  channels remain Phase 3.
- **Heartbeat host:** inside each importer, on its own timer.
- **Credential-expiry source:** for a certificate, the `NotAfter` of the certificate the importer
  runs with. A client secret's expiry is still unknown at runtime, and proactive client-secret
  expiry remains open.
- **Alert-rule lifecycle on upgrade:** an upgrade updates the definitions, recipients and thresholds
  of the rules the installer owns; portal edits to them are not preserved.
- **Cost ceiling:** no currency cap. The contract documents the event volume, rule count and the
  two-day query horizon instead; see *Cost, scale and planned shutdowns*.

### Out of scope (for now)

Cost/quota anomaly alerting, Azure portal workbooks/dashboards (the portal's Service health page is the
Phase-2 dashboard; a portal workbook stays Phase 3), SIEM integration — possible follow-ups.
