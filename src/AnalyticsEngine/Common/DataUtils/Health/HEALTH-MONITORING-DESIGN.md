# Default system-health monitoring & alerting — design record

Tracks issue [#144](https://github.com/pnp/Microsoft365-Analytics-Insights/issues/144).

This is the **engineering design record** for opt-out monitoring provisioned by the installer.
The implementation below is the current contract; the subsequent proposal sections retain the
broader issue design and are **not a claim that every proposed signal is implemented**.

## Delivered monitoring contract

New wizard configurations enable monitoring and require one notification email or an explicit
opt-out. Old JSON without `HealthMonitoring` stays opted out. The existing saved schema is unchanged.
The installer writes `EnableDefaultHealthChecks` to the web app in both enabled and disabled states;
the importers read it on startup. This runtime setting is not a new persisted installer property.

When enabled, each expected importer runs two independent checks immediately and every five minutes,
off the import loop. Each has a 30-second budget; cancellation is requested at timeout, late work
cannot emit success after disposal, and a non-cooperative check is not launched again until it returns.
The other check and the one-minute process heartbeat continue while an import/check is stuck.

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
- Both checks emit privacy-safe `HealthCheck` results with `JobName`, `Monitor=DefaultHealth` and
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
24 hours, across restarts observed within the two-day query window), latest unhealthy SQL/credential
check, known certificate expiry <14 days, and either expected check absent for 30 minutes.
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
At two jobs, the baseline is about 2,880 periodic heartbeat events and 1,152 check events per day,
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
The broader catalog below is a design proposal, not an enabled default or an approved cost ceiling.

## What's implemented now

The Appendix E **uniform telemetry schema**, emitted through the existing `AnalyticsLogger` →
Application Insights pipeline (no new sink):

- **`HealthCheck`** custom event — one per check per cycle:
  - `Component` — one of `DataUtils.Health.HealthComponent` (`Sql`, `ActivityApi`, `Graph`, `KeyVault`,
    `ServiceBus`, `Credential`, `Dns`, `BlobCheckpoint`).
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
  `types/health.ts`, routed in `App.tsx`), backed by the `api/Health` controller (`HealthAPIController` →
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
  - **Component health** — populated **today** for the two proactive checks the web app can run itself: the
    runtime **credential expiry** (certificate `NotAfter` → days-to-expiry; a client secret's expiry isn't
    visible at runtime) and the **Service Bus** Teams-calls queue depth / dead-letter count. SQL, Activity
    API, Graph, Key Vault and DNS fill in as the runtime `HealthCheck` emitter lands.
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

- **Data-flow / freshness** monitoring (issue #144 default alert #9, "Activity import stalled") builds
  directly on the existing `FinishedImportCycle` / `FinishedSectionImport` events — no new emit needed;
  the design just codifies the wiki query (`FinishedImportCycle` count == 0 over the window) as a
  shipped default rule.
- **`ImporterHeartbeat`** is **not** a rename of `FinishedImportCycle`. `FinishedImportCycle` fires only
  when a cycle *completes*, so a job stuck **mid-cycle** emits nothing and looks identical to a dead job.
  The Appendix E heartbeat is intended to be raised on an **independent timer** (Appendix D open
  question: same web-job on its own timer vs a separate WebJob/Function) so a stuck import can't
  suppress the liveness beat. It also adds structured `JobName` / `LastCycleUtc` /
  `LastCycleDurationSeconds` dimensions (vs the free-text `context` string) so the same rule works
  across both jobs. The independent host is now implemented; `FinishedImportCycle` remains a separate
  loop-completion signal, not a dependency or non-zero-data success guarantee.

## What should be monitored

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

## Where to surface — options

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

## Central health dashboard (web-app tab)

The most common operational question is simply *"is it working?"*, and today there is no single place
to answer it — an admin has to open Application Insights and run ad-hoc queries. So, alongside the
Azure-Monitor alerting above, ship a **central health view inside the web app** as a new nav tab so the
answer is one click away. This complements (does not replace) the alert rules: alerts push when
something breaks; the dashboard is the pull/at-a-glance "green board".

**Surface** — add a `Health` tab to the nav in `Views/Shared/_Layout.cshtml` (next to the brand link),
backed by a new `HomeController.Health` action + `Views/Home/Health.cshtml` view and a
`Web/Models/HealthDashboard` view-model (mirrors the existing `SystemStatus.LoadFrom` pattern). Keep it
`[Authorize]` like the rest of the app.

**What it shows** (all read-only, best-effort — a data-source hiccup degrades a card, never errors the
page):

| Card | Content | Source |
|-|-|-|
| Component health | Latest `HealthCheck` per `Component` with `Status` (green/amber/red) + `Detail`; credential `DaysToExpiry` | App Insights `customEvents` (`HealthCheck`), newest per `Component` |
| Import liveness | Per job: last `ImporterHeartbeat` / `FinishedImportCycle` time + "N min ago" freshness badge; last `FinishedSectionImport` per section | App Insights `customEvents` (interim: `FinishedImportCycle`/`FinishedSectionImport`) |
| Exceptions overview | Count/hour sparkline over last 24h, top exception `type` / `problemId` with counts, total in window | App Insights `exceptions` table |
| Data overview | Hits / Activity / Teams counts (already on Home) + newest audit/hit timestamps ("data as fresh as …") | SQL (`AnalyticsEntitiesContext`, as `SystemStatus` does today) |

**Data access** — the exception/event cards need to *query* App Insights (not just receive telemetry).
The solution already has a KQL query client, `WebJob.AppInsightsImporter.Engine.AppInsightsAPIClient`,
that authenticates to `https://api.applicationinsights.io` with the app's **existing Entra credential**
(`ClientId`/`ClientSecret`/`TenantGUID` already in web config) and parses the `ApplicationId` from the
App Insights connection string — **no new API key or config is required**. Two clean ways to reuse it
from the Web project (which does not currently reference `WebJob.AppInsightsImporter.Engine`):

- **(preferred)** extract `AppInsightsAPIClient` + its connection-string parsing into `Common`
  (`Common/DataUtils` or a small `Common.AppInsightsQuery`) so both the importer and the web app depend
  on one copy — this also removes the duplicated `ParseInstrumentationKey` helper noted in
  `ImportConfigController`; or
- add a `ProjectReference` from `Web` to `WebJob.AppInsightsImporter.Engine`.

Example queries the dashboard runs (KQL, same store the alerts use):

```kusto
// Exceptions per hour (last 24h)
exceptions | where timestamp > ago(24h) | summarize count() by bin(timestamp, 1h) | order by timestamp asc
// Top exception types (last 24h)
exceptions | where timestamp > ago(24h) | summarize count() by type, problemId | top 10 by count_ desc
// Latest health per component
customEvents | where name == "HealthCheck" | summarize arg_max(timestamp, *) by tostring(customDimensions.Component)
// Last confirmed import cycle per job
customEvents | where name == "FinishedImportCycle" | summarize arg_max(timestamp, *) by operation_Name
```

The health values are cheap to cache (e.g. 60s `MemoryCache`) so opening the tab doesn't hammer the
query API. When the runtime heartbeat (Option 2) is not yet deployed, the Component-health card is
simply empty/greyed and the liveness card falls back to `FinishedImportCycle` — the dashboard degrades
gracefully as later phases fill it in.

## Reusing `SolutionInstallVerifier` at runtime

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

## Proposed default "ships-by-default" alert set

Kept small; heartbeat-driven where possible. All route to a single default action group (recipient
configured at install).

| # | Monitored item | Signal source | Type | Default threshold |
|-|-|-|-|-|
| 1 | Any component unhealthy | `HealthCheck Status == Unhealthy` | log/heartbeat | any in 15m |
| 2 | Web-jobs not heartbeating | `ImporterHeartbeat` absent (interim: `FinishedImportCycle` absent) | log/heartbeat | none in 30m |
| 3 | Runtime credential expiring | `HealthCheck{Component=Credential}.DaysToExpiry` | metric/log | < 14 warn, < 3 critical |
| 4 | SQL unreachable / schema mismatch | `HealthCheck{Component=Sql}` | log/heartbeat | any Unhealthy in 15m |
| 5 | SQL full / read-only | `exceptions ... "database is read-only"` | log | > 2 in 10m |
| 6 | SQL capacity | DTU/vCore % + storage % | metric | sustained high |
| 7 | App Service Plan capacity | CPU/memory % | metric | > 90% for 1h |
| 8 | Service Bus backlog (Teams calls) | dead-letter / throttled requests | metric | > 0 |
| 9 | Activity import stalled | existing `FinishedImportCycle` / `FinishedSectionImport` custom events (wiki) | log | count == 0 in window |
| 10 | Exception spike (general probe) | total App Insights `exceptions` volume | log | sustained spike vs baseline |

## Implementation pointers (remaining phases)

- **Heartbeat host:** a `TimerTrigger` task — candidate `WebJob.Office365ActivityImporter` on an
  *independent* timer, or a separate small WebJob/Function so a stuck import can't suppress it.
- **Shared checks:** refactor `SolutionInstallVerifier` checks into a reusable `Common` health component
  emitting the `HealthCheck` events defined here.
- **Provisioning tasks:** new install tasks under `Common/CloudInstallEngine/Azure/InstallTasks`
  following `AppInsightsInstallTask` / `LogAnalyticsInstallTask`, using `Azure.ResourceManager.Monitor`
  for `ActionGroup`, `MetricAlert`, `ScheduledQueryRule`; wire into
  `App.ControlPanel.Engine/InstallerTasks/AzurePaaSInstallJob`. Make them **non-critical**
  (`BaseInstallTask.IsCritical = false`) so a monitoring-provisioning hiccup never fails the install.
- **Config:** add `AlertEmail` + `EnableDefaultMonitoring` (opt-out) to `SolutionInstallConfig` and the
  installer UI; the action group uses `AlertEmail`. (DB/config/permissions change → "migration needed".)
- **Surfacing:** the central health dashboard tab (see "Central health dashboard (web-app tab)" above) —
  new `HomeController.Health` action + `Views/Home/Health.cshtml` + `Web/Models/HealthDashboard`, nav
  entry in `_Layout.cshtml`, reusing the extracted `AppInsightsAPIClient` for the exception/event cards
  and `AnalyticsEntitiesContext` (as `SystemStatus` does) for the SQL cards; optionally add a `/health`
  endpoint + availability test.
- **Idempotency:** provision create-if-absent so admin edits to thresholds/recipients aren't clobbered
  on upgrade.

## Phasing

- **Phase 1 (MVP):** heartbeat liveness + SQL/schema + runtime-credential valid + credential expiry
  warning; ship the action group (email) + heartbeat-based rules (#1–4, #6–7) **plus the two cheap
  catch-alls that reuse events already emitted today** — activity import stalled (#9, existing
  `FinishedImportCycle`) and the exception-spike general probe (#10); opt-out flag; doc update.
  Delivers the originally-requested items. *(This PR lands the Phase-1 telemetry primitive.)*
- **Phase 2:** the central health dashboard tab in the web app (exceptions overview + last-confirmed
  import cycles + component-health + data-freshness cards) **— delivered, see "Also delivered" above** — plus
  the remaining dependency checks (Service Bus dead-letter, Key Vault), data-freshness alerts,
  web-app availability test, `SystemStatus` health page.
- **Phase 3:** Azure workbook/portal dashboard, richer overridable thresholds, extra notification
  channels (Teams/webhook/ITSM), tenant report-anonymisation re-check, cost/quota anomaly alerts.

## Alert-setup guide (to publish to the wiki)

The wiki's [Monitoring](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Monitoring) page
documents individual queries but not a repeatable "add another alert" procedure. The content below is
the source for a **step-by-step admin guide to be published to the wiki** (the wiki lives in the sibling
`Microsoft365-Analytics-Insights.wiki` repo; it is drafted here so it is version-controlled and reviewed
with the code). Once the installer provisions the default action group, extra alerts are a few clicks.

**One-time:** confirm the default action group exists (installer creates `<web-app>-health-notifications`, or
create one under *Monitor → Alerts → Action groups* with an email/Teams-webhook receiver). Every alert
below routes to it, so recipients are configured in one place.

**Add a log-search (KQL) alert** — for the custom-event / exception rules:

1. Application Insights resource → **Logs**, paste the query and confirm it returns rows.
2. **New alert rule** (top of the Logs blade) → the query is pre-filled as the condition.
3. Measurement: aggregate **Table rows** / **Count**; set the threshold and **Aggregation granularity**
   + **Frequency of evaluation** to match the window (e.g. 30 min for liveness).
4. Actions → select the default action group. Details → name + severity. **Create**.

**Add a metric alert** — for capacity rules (SQL DTU, App Service CPU, Service Bus dead-letter):

1. The resource (SQL DB / App Service Plan / Service Bus) → **Alerts → New alert rule**.
2. Condition → pick the platform metric (e.g. *DTU percentage*, *CPU Percentage*,
   *Dead-lettered messages*), set operator/threshold/window.
3. Actions → default action group. Name + severity → **Create**.

**Ready-to-paste queries for the default rules** (numbers match the ship-list table above):

```kusto
// #1 Any component unhealthy (last 15m)
customEvents | where name == "HealthCheck" and timestamp > ago(15m)
| where tostring(customDimensions.Status) == "Unhealthy"
// #2 Web-jobs not heartbeating (alert when result is 0; interim uses FinishedImportCycle)
customEvents | where name in ("ImporterHeartbeat","FinishedImportCycle") and timestamp > ago(30m)
| summarize beats = count()
// #3 Credential expiring (warn < 14 days)
customEvents | where name == "HealthCheck" and tostring(customDimensions.Component) == "Credential"
| extend days = toint(customDimensions.DaysToExpiry) | where days < 14
// #9 Activity import stalled (alert when result is 0 over the window)
customEvents | where name == "FinishedImportCycle" and timestamp > ago(24h) | summarize cycles = count()
// #10 Exception spike (general health probe)
exceptions | where timestamp > ago(1h) | summarize errors = count()
```

For "alert when a query returns **no** rows" rules (#2, #9), use *Number of results* **= 0** with the
window as the evaluation period. Document the chosen thresholds next to each rule so admins can override
them.

## Open questions

- Action-group recipient: email only, or also Teams webhook / Logic App / ITSM?
- Heartbeat host: inside `Office365ActivityImporter` on its own timer, or a separate WebJob/Function?
- Credential-expiry source: Key Vault secret/cert `exp` attributes (no extra Graph perms) vs the Entra
  app registration (needs app-read permission)?
- Alert-rule lifecycle on upgrade: re-provision/diff each install, or create-if-absent and never touch?
- Acceptable cost ceiling for log-search alerts, or prefer metric/heartbeat alerts only?

## Out of scope (for now)

Cost/quota anomaly alerting, Azure portal workbooks/dashboards (the in-app Health tab above is the
Phase-2 dashboard; a portal workbook stays Phase 3), SIEM integration — possible follow-ups.
