# Default system-health monitoring & alerting — design record

Tracks issue [#144](https://github.com/pnp/Microsoft365-Analytics-Insights/issues/144).

This is the **engineering design record** for shipping sensible, opt-out, default health monitoring
from the installer so a fresh install is observable by default. Today the solution has **no default
health monitoring**: the wiki documents how to build ~12 Azure Monitor / Application Insights alert
rules and an action group by hand, but most installs never do it, so when a web-job dies, the runtime
app secret expires, or the DB fills up, **data silently stops flowing and nobody is told**.

The full feature is phased (see below) and touches the (.NET Framework 4.8) installer, web-jobs, web app
and Azure provisioning SDK. The **first, additive slice lives here**: the uniform health-telemetry
signal (`HealthTelemetry.cs` + `AnalyticsLogger.TrackHealthCheck` / `TrackImporterHeartbeat`) that all
later alerting collapses onto.

## What's implemented now

The Appendix E **uniform telemetry schema**, emitted through the existing `AnalyticsLogger` →
Application Insights pipeline (no new sink):

- **`HealthCheck`** custom event — one per check per cycle:
  - `Component` — one of `DataUtils.Health.HealthComponent` (`Sql`, `ActivityApi`, `Graph`, `KeyVault`,
    `Redis`, `ServiceBus`, `Credential`, `Dns`).
  - `Status` — one of `DataUtils.Health.HealthStatus` (`Healthy` | `Degraded` | `Unhealthy`).
  - `Detail` — optional free-text reason. **Must not contain secrets or customer data.**
  - `DaysToExpiry` — numeric, `Credential` only.
- **`ImporterHeartbeat`** custom event — one per import job per cycle:
  - `JobName` (e.g. `Office365ActivityImporter` | `AppInsightsImporter`),
    `LastCycleUtc`, `LastCycleDurationSeconds`.

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

## What should be monitored

- **A. Liveness** — continuous web-jobs (`Office365ActivityImporter`, `AppInsightsImporter`) alive &
  completing cycles (emit `ImporterHeartbeat`); web app reachable (tracker-config / Teams-auth API /
  Teams call-record webhook).
- **B. Data flow / freshness** — activity cycle completing, audit events non-zero, user/Teams metadata
  & usage-report imports completing, `AppInsightsImporter` writing hits, web-tracker `pageViews`
  arriving; backlog/lag (a full cycle must finish < 24h).
- **C. Credentials & permissions** — runtime credential valid *now* (token acquisition);
  proactive secret/cert **expiry warning** (`DaysToExpiry`); Graph/Activity API permission loss (403 /
  consent revoked); tenant usage-report anonymisation turned on.
- **D. Data store & dependency health** — SQL reachable **+ schema/migration version == expected**, SQL
  capacity (full/read-only, DTU/vCore %, storage %), Redis reachable, Service Bus reachable +
  dead-letter depth, Key Vault reachable, Storage reachable.
- **E. Capacity / performance KPIs** — App Service Plan CPU/memory, SQL DTU/vCore + storage growth,
  Redis server load, Service Bus throttling.
- **F. Safety net** — spike in App Insights `exceptions` rate.

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
| (new) | Redis ping | `HealthCheck{Component=Redis}` |
| (new) | Service Bus reachability + dead-letter depth | `HealthCheck{Component=ServiceBus}` |

## Proposed default "ships-by-default" alert set

Kept small; heartbeat-driven where possible. All route to a single default action group (recipient
configured at install).

| # | Monitored item | Signal source | Type | Default threshold |
|-|-|-|-|-|
| 1 | Any component unhealthy | `HealthCheck Status == Unhealthy` | log/heartbeat | any in 15m |
| 2 | Web-jobs not heartbeating | `ImporterHeartbeat` absent | log/heartbeat | none in 30m |
| 3 | Runtime credential expiring | `HealthCheck{Component=Credential}.DaysToExpiry` | metric/log | < 14 warn, < 3 critical |
| 4 | SQL unreachable / schema mismatch | `HealthCheck{Component=Sql}` | log/heartbeat | any Unhealthy in 15m |
| 5 | SQL full / read-only | `exceptions ... "database is read-only"` | log | > 2 in 10m |
| 6 | SQL capacity | DTU/vCore % + storage % | metric | sustained high |
| 7 | App Service Plan capacity | CPU/memory % | metric | > 90% for 1h |
| 8 | Service Bus backlog (Teams calls) | dead-letter / throttled requests | metric | > 0 |
| 9 | Activity import stalled | data-flow trace query | log | per wiki |

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
- **Surfacing:** extend `Web/Models/SystemStatus` + `HomeController` to show health; optionally add a
  `/health` endpoint + availability test.
- **Idempotency:** provision create-if-absent so admin edits to thresholds/recipients aren't clobbered
  on upgrade.

## Phasing

- **Phase 1 (MVP):** heartbeat liveness + SQL/schema + runtime-credential valid + credential expiry
  warning; ship the action group (email) + heartbeat-based rules (#1–4, #6–7); opt-out flag; doc update.
  Delivers the originally-requested items. *(This PR lands the Phase-1 telemetry primitive.)*
- **Phase 2:** dependency checks (Redis, Service Bus dead-letter, Key Vault), data-freshness alerts,
  web-app availability test, `SystemStatus` health page.
- **Phase 3:** dashboard/workbook, richer overridable thresholds, extra notification channels
  (Teams/webhook/ITSM), tenant report-anonymisation re-check, cost/quota anomaly alerts.

## Open questions

- Action-group recipient: email only, or also Teams webhook / Logic App / ITSM?
- Heartbeat host: inside `Office365ActivityImporter` on its own timer, or a separate WebJob/Function?
- Credential-expiry source: Key Vault secret/cert `exp` attributes (no extra Graph perms) vs the Entra
  app registration (needs app-read permission)?
- Alert-rule lifecycle on upgrade: re-provision/diff each install, or create-if-absent and never touch?
- Acceptable cost ceiling for log-search alerts, or prefer metric/heartbeat alerts only?

## Out of scope (for now)

Cost/quota anomaly alerting, full dashboards/workbooks, SIEM integration — possible follow-ups.
