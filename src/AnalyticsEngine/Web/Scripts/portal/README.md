# Microsoft 365 Advanced Analytics - Web Portal (`portal`)

A React single-page application that is the whole web experience for the Microsoft 365 Advanced
Analytics Engine. The ASP.NET `Web` project serves it at the site root (`/`, via
`HomeController.Index`) and its built assets live under `/Scripts/portal/build/`.

> This app replaces the old single-purpose `teams-permission-grant` sample and the old
> server-rendered home page. It is built with **Vite + React 19 + TypeScript** and uses
> **Fluent UI React v9** (`@fluentui/react-components`) for an Office 365 look & feel.

## Areas and pages

The portal is split into two areas so the two audiences it serves don't have to wade through
each other's tooling. The area switcher sits in the header; each area has its own left nav.

**Insights** — what the data says, for a business/adoption reader.

| Route (hash) | Page | What it does |
| --- | --- | --- |
| `#/insights/overview` | **Overview** | The landing page. Headline figures for the workloads this deployment actually imports, a system-health snapshot (overall status, per-section state, data freshness and 24h volume) and a short tour of the rest of the portal. |
| `#/insights/reports` | **Reports** | In-app version of the Power BI reports: a sub-area per enabled workload, charting usage over a configurable window. Includes **Office apps** — which Office apps people use, on which platforms, by department and by email domain, and how far Copilot has reached each app. |
| `#/insights/copilot-adoption` | **Copilot Adoption** | Which licensed users aren't getting value from their licence, and which unlicensed heavy users have the strongest case for one. |
| `#/insights/teams` | **Teams Explorer** | How Microsoft Teams is actually being used: adoption and reach, engagement segments, meeting load and patterns, team/channel health and governance, conversation insight, and champions. Replaces the archived `reports\Misc\Archive\Teams.pbit`. |
| `#/insights/web-activity` | **Web activity** | What people do on the SharePoint intranet: visits and visitors, page views, where visitors arrive and give up (entry/exit pages, bounce, page-to-page journeys), geography, search terms and the terms that lead nowhere, and browser/device/load-time technology. Replaces the Power BI web-traffic report. |
| `#/insights/licence-activity` | **Licence activity** | Whether assigned licences are being used. One sortable table compares every licence - people assigned, a 0-100 adoption score (the share of measured holder-weeks that were active, over the services with a known band) and per-service activity - against the all-licences baseline, with empty and unassigned licences hidden by default. Choosing a licence, or **All licences**, shows each service's activity bands next to the baseline and its rank among the licences, the most and least active people (See PII only, and available for all licences as well as one), and a department and country breakdown. Explanatory notes can be hidden and stay hidden. |
| `#/insights/activity-analysis` | **Activity analysis** | Microsoft 365 activity week by week from the profiling runbooks' weekly roll-up (`profiling.ActivitiesWeeklyColumns`): a metric slicer over the 58 Teams, Outlook, OneDrive, SharePoint, Copilot and Viva Engage metrics, active people by company and department, metrics by week, and a department results matrix (Sum and Unique per metric) with a pinned total. Filters narrow the people by directory attributes, licences held and min/max ranges on each person's totals - all See PII only, because any condition can be differenced against the unfiltered report to single one person out; durations are entered and shown in hours. A department expands to its people, and a Top people list ranks the champions - both See PII only. Replaces the Power BI "Activity Analysis" and "Filter Settings" views. |

**Administration** — running the service, for an IT operator.

| Route (hash) | Page | What it does |
| --- | --- | --- |
| `#/admin/health` | **Service health** | System health: overview, import liveness, exceptions, component health, data overview and configuration, each lazily loaded from its own cached endpoint. |
| `#/admin/install-log` | **Install log** | History of configurations applied to the solution (the `sys_configs` table): when, by whom, install messages, and the config JSON per entry. The most recent is the current configuration. |
| `#/admin/profiling` | **Profiling** | Current state of the profiling data: earliest/latest dates for each compiled profiling table and the source activity tables that feed it (each with the **SQL** behind it), plus a paged view of the profiling runbooks' trace log (`profiling.TraceLogs`). Lets admins quickly check the runbooks have run, data is fresh, and spot errors. |
| `#/admin/teams-permissions` | **Teams permissions** | Authorise / de-authorise Teams for deep analytics (stores a delegated refresh token per Team in the `TeamsAuth` partition of the `AnalyticsState` Azure Table in the solution's storage account). Ported from the original app. |
| `#/admin/agent-cost-connection` | **Copilot Studio billing connection** | Connect/reconnect, disconnect and inspect the deployment-wide delegated billing administrator used for Copilot Studio consumption. Administration only; no tokens or named-user analytics are returned to the SPA. |
| `#/admin/user-lookup` | **User data lookup** | Enter a user's UPN to see all of their data held in SQL: profile, per-category record counts (broken down by workload, including Copilot and Power Platform; each row has a **SQL** button to view & copy the query behind its count), drill-down to recent rows, and which **import workloads** are enabled (so a legitimate 0 count is explained). |
| `#/admin/user-import` | **User import** | Whether the Graph user import has a stored checkpoint (its `/users/delta` token, kept in the `UserImport` partition of the `AnalyticsState` Azure Table), where it is kept, when the import last completed and how often it runs - and a confirmed **Clear checkpoint** action so the next run reads every user again, optionally on the next import cycle. The in-product version of deleting the stored token by hand (issue #664). The token itself never reaches the browser. |
| `#/admin/leadership-cohort` | **Leadership group** | Saves an explicit Entra ID group for the Copilot Adoption comparison and requests membership reads by the activity importer. The response acknowledges durable settings/request storage, not Graph completion. **Check status** retrieves the worker outcome without requesting another read. |
| `#/admin/copilot-adoption-settings` | **Copilot Adoption settings** | Configures the tenant-wide frequency, depth and breadth weights and adoption-level thresholds. Settings and change history are stored in the `CopilotAdoptionSettings` partition of `AnalyticsState`. |
| `#/admin/global-filter` | **Report filter** | The administrator's global report filter: conditions every Insights report applies for everyone, on top of their own filters, optionally compared with the viewer's own attributes. Previews the draft against the administrator's own account before saving. Needs See PII as well as Administration. See [The administrator's global filter](#the-administrators-global-filter). |
| `#/admin/configuration` | **Service configuration** | What this deployment is pointed at: SQL, the storage account (which holds the runtime state table), Cognitive Services and Service Bus, plus the Teams calls import state and the Graph call webhook (with a live validation POST to test it). |

Routing uses `HashRouter`, so the whole SPA is served by a single MVC action and no IIS /
MVC route changes are needed to add pages.

The Reports **Copilot** tab also exposes prompt categories when the existing
`CopilotInteractionHistory` import is enabled without the Copilot audit import. In that mode it
requests only the category report, not the audit charts or the Copilot agents area. This availability
flag does not enable history import or categorisation; the report reads classifications already
collected under the administrator's existing opt-ins.

`src/navigation.tsx` is the single source of truth for both the router and the left nav, so
the two cannot drift — adding a page means adding one entry to `ROUTES`.

> The pre-split routes (`#/home`, `#/reports`, `#/teams`, `#/health`, ...) are **not**
> redirected. Anything unrecognised falls back to the Insights overview.

## Leadership membership requests

The leadership admin API does not run Graph or SQL in an HTTP request. Settings are durably saved in
the `LeadershipCohort` partition of `AnalyticsState`; a new settings revision is due in the activity
importer's next available cycle. Explicit read requests use a separate `RefreshRequest` row for
the currently configured group, so they cannot overwrite a concurrently saved group. Each worker snapshot
acknowledges the request ID it started with; a newer request remains pending. Requests survive
web-app/importer restarts, and a failed outcome is still an acknowledged attempt (normally retried
after one hour). No new SQL migration or installer configuration is needed.

The report comparison reads settings, the snapshot header and the durable request together whenever
its 60-second header cache expires (or immediately after a local save/request invalidates it).
An unacknowledged request yields `pendingRefresh` without loading member pages or returning the
previous figures. Only a snapshot acknowledging that request clears pending: success exposes the
new membership, failure exposes its unavailable reason. A newer request made during a worker write
stays pending. Unreachable or malformed request state fails closed as `stateUnavailable`, cached for
15 seconds. Other web instances can retain their previous cached comparison for up to 60 seconds;
this bounded cache policy is unchanged.

A save/read request returns the configured group with `refresh: null` (pending). Keep the
Office 365 activity importer running and use **Check status** after its next cycle; Graph retries
may delay that cycle. A membership failure is shown as a refresh outcome, not a settings-save
error. Clearing the group immediately disables the comparison. Status/storage operations share
a cancellable 30-second request deadline, below App Service's HTTP deadline. If storage cannot
acknowledge a write, check status before retrying: as with any network write, an interrupted
response does not prove that the write failed.

## Copilot Adoption score settings

Copilot Adoption score-settings reads and saves have a cancellable **10-second storage deadline**,
including lazy opening of `AnalyticsState`. Every adoption report request reads the current settings
before consulting the analysis cache. A storage deadline or outage returns the existing
settings-unavailable **503**, never a report scored with last-known or default rules. Administration
reads, saves and resets use the same deadline; a cancelled client request remains a cancellation.
After an interrupted save, read the current settings before retrying because a network timeout
cannot prove whether a conditional write committed. No additional app setting or installer
configuration is required.

## Copilot Studio billing administrator connection

Copilot Studio credit **consumption** is read with a delegated administrator, because Microsoft's Power
Platform licensing API refuses the runtime application's app-only identity on every consumption route.
Only the tenant **capacity** read (and Azure Cost Management) stays app-only. With no connection the
importer still imports capacity, makes no consumption calls and records a non-error
"connection required" state (`agentCosts.import.connectionRequired`) instead of a 403 every cycle.
A failed connected identity never silently falls back or reports success.

**Prepaid credits and Azure charges are separate measures.** The report explicitly shows prepaid
Copilot Credits consumed from the tenant capacity snapshot, including Microsoft's consumption period
and timestamp, alongside Azure monetary spend for the selected date window. The snapshot is not
recalculated for that window. Prepaid consumption is pooled, not attributed to individual purchased
packs. Per-agent "billed credits" means chargeable usage, not necessarily an additional Azure charge.
Pay-as-you-go credit consumption is shown only if the entitlement API supplies it; a missing value is
not zero and is never inferred from `payGo.entitled` or by subtracting per-agent totals from capacity.
Credits and money are never added together. Both import toggles can be enabled independently.

**Health checks the billing connection only when Copilot Studio credit tracking is enabled.**
Component health shows whether a Power Platform billing administrator connection is saved.
A missing connection, a required reconnect, or an unreadable connection store degrades the overall
health status and points to Administration > Agent costs. This is a saved-connection check, not a
live Power Platform token validation. Azure-cost-only tracking does not require this connection
and does not add the check.

**How per-agent figures are built.** Microsoft restricted the tenant-wide per-agent route
(`/MCSMessages/resources`) to its own clients
([Power CAT maintainer clarification](https://github.com/microsoft/Power-CAT-Copilot-Studio-Kit/issues/855)),
so the importer no longer calls it. Per day it reads `/MCSMessages/users` once, then
`/MCSMessages/users/{userId}/resources` for every person with consumption, and sums those rows per
agent/environment/feature. The per-agent user count is the number of distinct contributing people.
Per-agent figures are tenant-wide (people outside the user-group filter are included, nothing identifying
is stored); per-person rows still honour the filter.

**Caveat:** per-agent figures cover only consumption Microsoft attributes to a person. Anything it does not
attribute to a user is not visible through any permitted API; the capacity tile's tenant "consumed" total
remains the authoritative total. Microsoft's last-refreshed stamp is not available, and
`copilot_studio_credit_daily.last_refreshed_utc` is no longer populated.

**API volume.** Calls per cycle are about *window days (default 7) × daily active Copilot Studio users*,
plus a few paging calls (10,000 daily active users is roughly 70,000 calls), run with bounded
concurrency (6) and the existing throttle/retry handling. The cadence is daily.

Route access, verified live: capacity, `/users`, `/users/{id}/resources` and
`/resources/{id}/users` succeed delegated; only capacity succeeds app-only; `/MCSMessages/resources` and
the environment route are refused for both. The connection is verified by probing `/users`.

Setup for a build containing this page:

1. On the **runtime** app registration, open **Authentication** and add a **Web** redirect URI
   `https://<portal-host>/signin-agent-costs`. The server builds it from `WebAppURL.TrimEnd('/')`
   plus `/signin-agent-costs`. Register each hosted slot/local instance separately; for the synthetic
   example `WebAppURL=https://localhost:44400/`, its callback is `https://localhost:44400/signin-agent-costs`.
   Use your actual local port, not the example's. Keep the existing portal and Teams callbacks.
   Do not select SPA/mobile, append a hash route, or add a trailing slash after the callback path.
   The callback uses a separate passive Katana OIDC v2 middleware; normal sign-in requests no billing scopes.
2. Under **API permissions → Add a permission → APIs my organization uses**, search for
   **Power Platform API**, application ID `8578e004-a5c6-46e7-913e-12f58912df43`,
   not a similarly named legacy resource. Select **Delegated permissions**, add
   **CopilotStudio.Licenses.Read** and **EnvironmentManagement.Environments.Read**, then
   **Grant admin consent** and confirm the granted status. These are advertised read-only permissions;
   if absent in your tenant, consult Microsoft rather than inventing a scope or substituting write access.
   The connection uses the documented
   `https://api.powerplatform.com/.default` scope plus `openid profile offline_access`.
   `.default` selects the app's configured delegated permissions; it is not a permission to add.
   The entitlement REST reference documents `.default`; it does **not** document a `Licensing.Read`
   scope, so this feature does not invent one or claim a generic allocation permission guarantees entitlement access.
   See Microsoft's [authentication guide](https://learn.microsoft.com/power-platform/admin/programmability-authentication-v2),
   [permission reference](https://learn.microsoft.com/power-platform/admin/programmability-permission-reference), and
   [resources endpoint reference](https://learn.microsoft.com/rest/api/power-platform/licensing/entitlement-insight/get-tenant-resources-across-environments).
3. Configure the existing `Storage` connection string, Table service reachability and the runtime identity's
   **Storage Table Data Contributor** access. Both portal and importer must use the same Storage and runtime
   credential. Missing/unreachable Storage is explicit; there is no browser-only or in-memory token fallback.
4. Sign in to the portal with **Portal.Administration**, using an account with **Power Platform Administrator**
   or equivalent billing read access. Under Administration → Copilot Studio billing connection, connect the
   **same account**. The server verifies MSAL can renew the credential and read consumption before publishing the connection.
5. Keep the Copilot Studio credit import toggle enabled. A successful connection or disconnect changes the
   credit cadence generation, so the next cycle does not wait behind the previous 24-hour stamp.

Setup failures:

| Error | Fix |
| --- | --- |
| `AADSTS650057` / Invalid resource | Configure the **Power Platform API** delegated permissions and grant consent on the runtime registration. Power Platform Reader RBAC alone does not configure an Entra requested resource. |
| `AADSTS50011` / Redirect URI mismatch | Add the exact callback shown in the error to **Authentication → Web**, including the instance's scheme, host, local port and path. A hosted callback does not cover localhost. If a hosted instance sends localhost, correct its `WebAppURL` instead. |
| Administrator consent required | Grant consent using an authorized Entra administrator, then start a fresh connection attempt. |
| Account mismatch / billing access refused | Connect the same account signed into the portal; it needs Administration and actual Power Platform billing read access. |
| Consumption probe denied despite correct consent/account | The per-user read (`/users`) returned 401/403, so the connection is not published. Check the delegated permissions' consent, that the account has Power Platform billing read access and Conditional Access; do not add unrelated roles. |

After fixing registration, retry Connect, refresh status and verify the next credit import succeeds.
Registration changes alone do not require a deployment or SQL migration; successful sign-in alone
does not prove a successful connection/import.

Security and operational contract:

- Same-origin XHR POST issues the short-lived, protected connect intent; MVC refuses direct/tampered or
  account-mismatched intents and separately enforces Administration. Katana validates the OIDC state,
  ID-token issuer/audience and nonce; the callback also binds the tenant/object ID and Administration role
  to the initiator and ignores any supplied return URL.
- MSAL owns code redemption, refresh-token rotation and serialization. Its complete user cache is stored
  encrypted and authenticated in `AnalyticsState`, partition **AgentCostDelegatedAuth**. The runtime secret
  derives AES/HMAC keys; certificate mode wraps random AES/HMAC keys with the runtime certificate.
  Neither credentials nor tenant response payloads enter the connection status, browser, or connection logs.
- State addresses: **Connection** is the active generation pointer; **Cache_&lt;generation&gt;** and
  **Error_&lt;generation&gt;** belong to that generation. Refresh writes cannot replace the pointer, so an old
  refresh cannot undo disconnect/reconnect. Cache/error rows expire 90 days after their last write; old
  replaced generations age out. Keep this partition restricted like credentials, including backups.
- ImportSchedule retains the legacy **CopilotStudioCreditsLastImported** key when no connection has ever
  existed. A changed connection uses **CopilotStudioCreditsLastImported_&lt;generation&gt;**.
  **AzureCostLastImported** is unchanged. To force an existing connection's retry, clear its matching stamp.
- Revocation, refresh expiry, consent/Conditional Access challenges or an aggregate consumption 401/403 set
  **Reconnect needed**. A transient token or Storage outage does not erase the connection. Reconnect with
  an account whose access and policies permit unattended refresh; this flow does not bypass Conditional Access.
  Credential replacement can make the saved cache unreadable and require reconnection.
- Import diagnostics `agentCosts.import.reconnectNeeded` and `agentCosts.import.tokenUnavailable` are stable
  keys, translated on the cost page. The first requires administrator reconnection; the second retries
  on the next due cycle without deleting the connection.
  `agentCosts.import.userAccessDenied` reports a refused best-effort per-user route without disabling
  an independently working aggregate connection.
- Disconnect stops new imports using the connection, not an import already in progress; it does not revoke Entra
  consent or erase imported cost data. Manage the consent separately in Entra if that is also required.

There is no SQL migration, installer configuration schema change or new app setting. Registration and
live delegated authorization must be performed by the deployment administrator after upgrade; tests use
synthetic identity-provider/API responses and do not prove access for a particular deployment.

## Permissions

The portal reads `GET /api/PortalAccess` once at startup through `PortalAccessProvider`.
Components use `usePortalAccess()` to check the two app-role permissions:

| Permission | Wire name | Entra app role |
| --- | --- | --- |
| Administration | `administration` | `Portal.Administration` |
| See PII | `seePii` | `Portal.SeePII` |

`src/navigation.tsx` has a `requires` field for areas and routes. New administration pages must
live in the `admin` area so they inherit `administration`; a route that exposes individual people
adds `requires: 'seePii'`. New per-person UI inside an aggregate page must check `seePii`, avoid
calling the per-person endpoint without it, and render the shared `PiiHiddenNote` instead.

The server enforces both permissions on its own (see *Portal permissions* in
`src/AnalyticsEngine/.github/copilot-instructions.md`); the portal's job is to not offer what the
server would refuse. So:

- **It fails closed.** Until `/api/PortalAccess` answers, the shell shows only a spinner; if it cannot
  be read, the portal behaves as if neither permission is held and says so.
- **A refusal is an error, not an empty result.** `apiFetch` turns the server's
  `403 { code: 'portalPermissionRequired' }` into a `PortalPermissionError` carrying a translated
  message, so a call the page should not have made fails loudly rather than rendering "no data".
- **Tests default to all granted.** `renderWithProvider` wraps the tree in a `PortalAccessProvider`
  holding both permissions, so existing tests see the whole portal; a test of a restricted view
  passes `{ access: { administration: false, seePii: false } }`. Fields left out of `access` are
  treated as not held.

## Authentication

The user signs in via the server's Azure AD (OIDC) redirect, which gates the `[Authorize]`'d
host action. Signing in asks for `openid email profile` only — nothing that needs consent beyond
signing in — so an optional feature's permissions can never lock anyone out of the portal
(issue #670: it used to redeem every sign-in's code for the Teams scopes, and a tenant that hadn't
granted them got a server error instead of the portal).

The only page that needs the admin's own Microsoft Graph token is **Teams permissions** (Teams deep
analytics), and it asks for the delegated `Team.ReadBasic.All` and `ChannelMessage.Read.All`
permissions **on demand**. When the site has no Graph token for the session, the page offers
*Connect to Microsoft Teams*: a full-page navigation to `/Account/ConnectTeams`, which re-runs the
OIDC challenge marked as a Teams connection. The callback redeems the authorisation code for the
Teams scopes and captures the OAuth **refresh token** into the encrypted, httpOnly auth cookie.
If Entra ID refuses — no admin consent, or the prompt was declined — the admin comes back to the
page still signed in, with an outcome key
(`?teamsConnect=consent_required|access_denied|failed&teamsConnectError=AADSTS…`) that
`src/auth/teamsConnect.ts` turns into translated guidance. The server half is
`App_Start/DelegatedGraphConsent.cs`, and `src/i18n/lint/serverAuthoredText.test.ts` keeps the two
in step.

The SPA then gets a fresh Graph **access token** from `api/SiteTokenAPI` (which mints one from the
cookie's refresh token). Nothing about the signed-in admin's token is stored server-side.
`TeamsAuthAPIController.Put` copies that refresh token into the `TeamsAuth` partition of the
`AnalyticsState` Azure Table only for each Team the admin explicitly authorises, so the importer
can read that Team's channels. When there is no refresh token, or the one in the cookie no longer
works (expired, revoked, or blocked by a sign-in-frequency policy), `SiteTokenAPI` answers `401`
and the page offers the Teams connection again. It never falls back to a carried access token:
that short-lived token would normally already be expired, so Graph would reject it and the page
could only say "No Teams found".

There is **no client-side sign-in**. A client-side MSAL fallback used to exist for when
`SiteTokenAPI` returned no token, but it was pinned to a hard-coded app registration that no
longer resolves (`AADSTS5000224`), so it could not sign anyone in — it only replaced a clear
failure with an opaque popup error, while adding `@azure/msal-browser` to the initial bundle for
every page. The Teams permissions page offers the Teams connection instead when no token is available.

### Expired sessions

Every call to the site's own API goes through `apiFetch` (`src/api/http.ts`) rather than raw
`fetch`. That exists because of how an expired session used to surface: the OIDC middleware runs
in Active mode, so it turns the 401 from an `[Authorize]`'d controller into a **302 to
login.microsoftonline.com**. A top-level navigation follows that happily, but `fetch` follows it
cross-origin, the login page carries no CORS headers, and the call rejects with an opaque
`TypeError: Failed to fetch`. Leave the portal open long enough — or let the App Service recycle,
so the new instance can't decrypt the old auth cookie — and every page started failing with a
network-looking error that was really just "please sign in again".

`Startup.ConfigureAuth` now suppresses that redirect for API requests (matched by the `/api` path
or the `X-Requested-With: XMLHttpRequest` header `apiFetch` always sends) and returns a plain
`401` carrying `X-Auth-Session-Expired: true`. `apiFetch` watches for that header and
re-authenticates with a full-page navigation, which is the only thing that can complete the OIDC
round-trip — and normally completes silently, because the user's Entra session outlives the
site's. The current hash route is stashed first and restored by `restoreRouteAfterReauth()` in
`main.tsx`, so the user lands back where they were, and a `sessionStorage` flag makes it one-shot
so a session that can't be re-established fails loudly instead of looping.

The header matters: a bare 401 is **not** enough to conclude the session is gone. `SiteTokenAPI`
returns 401 to mean "you are signed in, but I have no Graph refresh token for you" — the server
only sets the header when there is genuinely no authenticated user, so that case still shows the
Teams page's *Connect to Microsoft Teams* prompt instead of bouncing through a pointless sign-in.

Graph access tokens are fetched at the point of use (`src/auth/siteToken.ts`), never cached on a
page, because they only last about an hour and the pages that need them are ones an admin leaves
open. They are used for the SPA's **direct** calls to `graph.microsoft.com` (the Teams page's
profile and joined-teams lookups). The Teams authorisation save does **not** send one:
`TeamsAuthAPIController.Put` authorises each Team with the refresh token it already holds in the
auth cookie, so a token in the request body would be ignored.

## Backend APIs used

| Window var | Endpoint | Purpose |
| --- | --- | --- |
| `o365AnalyticsTokenAPI` | `api/SiteTokenAPI` | Fresh Graph access token for the signed-in admin (minted from the cookie refresh token). |
| `o365AnalyticsAuthAPI` | `api/TeamsAuthAPI` | Get / set Teams deep-analytics authorisation. |
| _(none - origin-relative)_ | `api/AgentCostConnection` | Administration-only delegated billing status, same-origin `/begin` and `/disconnect` POSTs. |
| `o365AnalyticsUserLookupAPI` | `api/UserDataLookup` | User data lookup (summary + per-category detail). |
| `o365AnalyticsSystemStatusAPI` | `api/SystemStatus` | System status / configuration for the Home page, plus the record counts for the imports this deployment runs. |
| `o365AnalyticsInstallLogAPI` | `api/InstallLog` | Install log (config history from `sys_configs`) for the Install Log page. |
| `o365AnalyticsProfilingStatusAPI` | `api/ProfilingStatus` | Profiling data freshness + paged `profiling.TraceLogs` for the Profiling page. |
| `o365AnalyticsReportsAPI` | `api/Reports` | Lite in-app reports: enabled areas (`/areas`) + weekly usage charts per area (`/copilot`, `/usage`, `/office-apps`, `/spo-audit`, `/web-traffic`, `/calls`, `/emails`). |
| `o365AnalyticsCopilotAdoptionAPI` | `api/CopilotAdoption` | Copilot licence adoption: availability, executive summary, licensed-user and licence-opportunity lists, and their CSV exports. Every endpoint takes the page-wide `userFilter` - see [The user filter](#the-user-filter). |
| _(none - origin-relative)_ | `api/UserFilter` | The user filter's picker: which attributes can be filtered on (`/dimensions`) and each one's values, largest first (`/values?dimension=&search=&take=`). For a reader the global filter applies to, both are counted within it. |
| _(none - origin-relative)_ | `api/GlobalFilter` | The administrator's global filter: `GET /effective` (how it applies to the signed-in reader, for the bar on every Insights page); and, for administrators with See PII, `GET` the definition, `POST` a new one (`{ filter, revision }`) and `POST /preview` a draft. The two POSTs are state-changing calls, so they go through `apiFetch` (see below). |
| _(none - origin-relative)_ | `api/TeamsExplorer` | Teams Explorer: source availability, and one endpoint per tab (`/overview`, `/adoption`, `/meetings`, `/collaboration`, `/conversations`, `/people`) plus `/export/{section}` CSVs. |
| _(none - origin-relative)_ | `api/WebActivity` | SharePoint web activity: source availability, and one endpoint per tab (`/overview`, `/visits`, `/pages`, `/journeys`, `/geography`, `/search`, `/technology`) plus `/export/{section}` CSVs. |
| _(none - origin-relative)_ | `api/ActivityAnalysis` | Activity analysis: `/availability` (whether the profiling tables exist and hold weeks, the period bounds and the metric catalogue), `/report` (aggregates for a period and the selected `metrics`, open to every reader with small groups folded and 1-4 people suppressed for a reader without See PII; any `userFilter`, `licences` or `ranges` condition - and the `rangeMaxima` bounds, each one person's total - need See PII) and `/people` (a department's people or the top people; See PII only). The metric labels, categories, reasons and error codes are stable keys the portal translates. |
| _(none - origin-relative)_ | `api/UserImportCheckpoint` | User import checkpoint: `GET` its state; `POST /clear` (body `{ "runOnNextCycle": bool }`) deletes it. The only state-changing call the portal makes to its own API, so the server requires the `X-Requested-With` header `apiFetch` sends (see below). |

### Calls that change something

The site authenticates with a cookie, and a browser sends that cookie with a request whichever page started it,
so an action that changes state is open to cross-site request forgery unless the server checks where the request
came from. Such actions carry `RequireSameOriginXhrAttribute` (in the `Web` project): the request must have
`X-Requested-With: XMLHttpRequest` - which `apiFetch` always sends, an HTML form cannot set, and another origin
can only add after a CORS preflight that this site never grants with credentials - and, when the browser sends
`Sec-Fetch-Site`, it must be `same-origin`. A refused request gets a bare `403`. Call such an action through
`apiFetch`, never raw `fetch`, or it will be refused.

`window.o365AnalyticsBuildLabel` is not an endpoint: it is the running build's label
(`Common.Entities.BuildConstants.BuildLabel`, stamped as `Build <number>` by ci.yml), substituted
into `index.html` by `HomeController.InjectBuildLabel` when it serves the page. The SPA prints it in
the footer of a printed report, which has to exist *before* `window.print()` runs - so it cannot be
fetched; and `api/SystemStatus`, which carries the same label elsewhere, `COUNT(*)`s whole tables
and is far too expensive to call on every page just to name a version.

The footer reads `Microsoft 365 Advanced Analytics (build 1836) · https://github.com/...`, and the
parenthesis is never empty. `npm run dev` serves `index.html` straight from disk, so the placeholder
survives; the SPA reads that, and `DEV_BUILD`, as an unstamped build and prints
`(development build)`. It prints neither a fake version nor - as it once did - nothing at all, which
made a report run off a developer's machine indistinguishable on paper from one off a release.

## Printing

Every page prints without the app shell. The report keeps the whole sheet, each numbered section of
a report starts a new page, and a footer naming the product, build and repository repeats at the
foot of every page.

This is a contract between components and the `@media print` block in `src/index.css`, expressed as
`data-print` attributes (class names are Griffel-generated and cannot be targeted from a
stylesheet):

| Attribute | Meaning |
| --- | --- |
| `data-print="hide"` | Chrome the reader cannot use on paper - nav, tabs, filter controls, buttons. |
| `data-print="content"` | A layout wrapper, flattened so it imposes no width limit, gutter or viewport-height floor. |
| `data-print="flow"` | A flex/grid stack of sections, returned to block flow so page breaks take effect. |
| `data-print="page-break"` | Starts a new sheet, and keeps its own content with it. |
| `data-print="keep-with-next"` | Never left stranded at the foot of a page with its content overleaf. |
| `data-print="only"` | Rendered on paper only - standing in for a control that is not: the line stating a list's filters, the figure typed into an assumption box. |
| `data-print="shell"` | The layout table that carries the running footer. Block flow on screen. |
| `data-print="footer"` | The `<tfoot>` repeated at the foot of every printed page. |

Nothing that can only be clicked is printed. A filter bar is hidden and replaced by a line naming what
each drop-down is set to, plus any ticked box and applied search; row expanders, sort arrows other
than the active one, info buttons and SQL buttons are hidden; and an accordion section prints only if
it is open (a closed one is left out altogether, since Fluent does not render its panel).

### Paged lists print in full

A stylesheet cannot print rows that are not on the page, so the server-paged lists (licensed users,
licence candidates, Cowork people) register with `src/components/shared/printPreparation.ts` while
they are showing. The **Print** button - and Ctrl+P/Cmd+P while that button is on the page - goes
through `requestPrint`, which loads every row of each such list in pages of 500 (the API's `MaxTake`),
commits them with `flushSync`, calls `window.print()`, and then puts each list back to its page.

- **Lists longer than `PRINT_ROW_LIMIT` (1,000 rows) print their first 1,000 rows, and say so.** Only
  that many are ever loaded - in the list's own sort order, so the people the sort puts first - and the
  printout opens the list with a warning naming how many of how many rows it holds and how to get the
  rest (narrow the filters, or use the list's CSV export for every row); the Print button also raises
  an on-screen warning toast. These lists grow with the tenant, and laying out tens of thousands of rows
  to print would stall the tab, but refusing outright left nothing to print at all.
- **"Expand all" is a mode, not a list of ids**, so it also opens the rows only the printout loads.
- **A list in a hidden section or tab does not take part**: it is not on the printout, so it must not
  hold the print up loading rows nobody will see.
- **The browser's own File > Print cannot be delayed** (`beforeprint` is synchronous), so it prints the
  page on screen, and each list says on paper that it holds only the rows that were on screen.

Wide lists are still clipped at the right-hand edge of the sheet: the Cowork people table lays out
at about 1,190px against roughly 700px of printable A4 width, and landscape does not recover it. On
the lists with expandable rows, **Expand all** also prints each clipped column's figure in that row's
detail. The licensed-user table is the exception: it is sized to fit the page, with the signal, apps,
Cowork use and reclaim detail in each row's expander, and its interactions and last-used columns step
aside on a narrow page (a container query on the table's wrapper) rather than scroll.

The footer is a real `<tfoot>` inside a layout `<table>` wrapping the report, and that is load-bearing.
A running footer must repeat on every page *and* have room reserved for it; `position: fixed` gives
only the first, so it overprints the last line of a full page, and Chromium mis-resolves the negative
`bottom` meant to lift it into the page margin - it lands the footer across the *top* of each sheet.
A table section is the only construct that does both, the same mechanism that repeats a long report
table's header row. On screen `src/index.css` flattens the table back to block flow and hides the
footer, so it costs nothing there.

`src/printStyles.test.ts` asserts the stylesheet half - Vitest runs with `css: false`, so a
component test can prove an attribute is present but never that it does anything. It also fails on
any `data-print` value the stylesheet has never heard of.

## The user filter

One filter control narrows a whole report to the people it matches: their standard **Entra ID
attributes** (user name, email domain, department, job title, company, office location, country or
region, state or province, postal code, usage location, user type, account status, manager, management chain) and
every enabled **custom organisation type** an administrator has defined on the *User organisations*
page. It is shown as pills, the way Azure Monitor shows metric filters: `Department = Sales, Marketing`,
`Cost centre ≠ CC-100`, `User name contains “smith”`.

- **Operators:** *is* (=), *is not* (≠), and - on text attributes - *contains* / *does not contain*.
  Every attribute except the user name and the management chain also offers **(not set)**. *Is not*
  includes people with no value, so *is* and *is not* always divide the directory between them.
- **User name** is the free-text search: it opens on *contains*, a term typed and never added with
  Enter still counts when Apply is pressed, and Enter on an empty box applies - so a name search is
  "smith", Enter, Enter. *Does not contain* leaves accounts out by pattern (`svc-`, `#EXT#`), and *is*
  picks named people from a searchable list.
- **Email domain** is the page's domain filter: the domain breakdown's **Filter** buttons add or
  replace an email-domain condition, so it combines with everything else by AND / OR like any other.
  The lists on the tabs have no department or domain drop-downs of their own - two department filters
  combining by AND could contradict each other and leave an empty list nobody can explain.
- **AND / OR:** the first condition needs neither; from the second on, a connector sits between the
  pills. AND binds tighter than OR, exactly as in SQL, and the bar draws each OR-group in its own box
  and reads the whole filter back in words, so nobody has to know the rule to read it. An existing
  condition is edited under the pills, with its pill highlighted, so switching a connector while it is
  open keeps the draft.
- **Management chain** matches everyone who reports to a manager at any level (the manager
  excluded). Custom organisations carry the organisation icon of the *User organisations* page;
  Entra attributes carry a person icon.
- **Where it lives:** `src/components/userFilter` - `UserFilterBar` (the pills),
  `UserFilterClauseEditor` (property / operator / values), `UserFilterPrintSummary` (the paper
  version), `describeUserFilter` (the words) and `userFilterModel` (the rules and the wire format).
  None of it knows which report it narrows.
- **Wire format:** `serializeUserFilter` produces the compact JSON `Common.Entities.UserFilters.UserFilterCodec`
  reads - `[{"d":"department","v":["Sales"]},{"j":"or","d":"org:12","op":"isNot","v":["CC-1"],"n":true}]`
  - passed as the `userFilter` query parameter. GET, because a report's CSV and Excel exports are
  plain links. The portal refuses a filter over 6,000 encoded characters; `Web.Template.config` lifts
  the host's 2,048-character query-string default for `api/CopilotAdoption` and `api/ActivityAnalysis`.
- **On the server** the filter is evaluated in memory against a shared directory snapshot
  (`IUserDirectorySource`, refreshed every few minutes and invalidated when an organisation type
  changes), so changing a filter never re-runs a report's SQL. The response echoes the filter it
  applied (`userFilter` on the adoption summary) and the page describes *that*, not what it asked for.
- **On paper** the bar is hidden like every other control, and a **Who this report covers** block
  states the filter in plain English - one sentence for one condition, a list for several, numbered
  groups for OR - with how many people in the directory match.
- **In the address:** the Copilot Adoption page keeps its filter in `?filter=`, so a filtered view is
  a link that can be bookmarked or sent to a colleague.

To adopt it in another report: render `UserFilterBar`, send `serializeUserFilter(filter)` with every
request and export, and on the server parse it with `UserFilterCodec.Parse`, compile it with
`UserFilterCompiler.Compile(expression, await directory.GetAsync())`, and keep the rows whose user id
`Matches`.

## The administrator's global filter

A portal administrator can set conditions that **every report in Insights** applies for everyone who
opens it, on top of any filter the reader adds - on the **Report filter** page
(`#/admin/global-filter`). Readers see the conditions as locked pills above their own filter, on every
Insights page, and cannot change or remove them. Editing the filter needs **See PII** as well as
Administration, as the reader's own filter does: its value picker lists people, and a filter that
selects one person turns every report into that person's record (#680). It applies to every reader,
with or without See PII.

- **Same conditions as the user filter**, with one more kind of value: **the viewer's own**. A
  condition can compare an attribute with the value the person viewing holds - "Department is the
  viewer's own value" shows each manager their own department, with one filter for everyone. The
  comparisons offered are deliberately few (`GlobalFilterViewerAttributes.AllowedFor`, mirrored by
  `viewerAttributesFor`): most attributes compare with the viewer's value of the same attribute; the
  user name with the viewer ("only my own figures"); the manager and management chain with the viewer
  ("my team", "my organisation") or the viewer's manager ("my peers"). Text searches cannot use it.
- **Enforced by the server, never by the page.** `ReportScopeResolver` resolves the filter for the
  signed-in person (found by Entra object id, then sign-in name) on every report request, whatever the
  page sends. In-memory reports (Copilot Adoption, Licence activity, the DLP and agent-cost people lists)
  keep only rows whose user it `Includes`. SQL reports carry a comment marker on each statement -
  `/*scope: AND x.user_id IN {scopeUsers}*/` - which `ReportScopeSql.Apply` removes when nothing is
  filtered (the statement is byte for byte what it was) and uncomments when something is, against a
  temporary table filled from one JSON parameter. Filling it uses `OPENJSON`, so it needs database
  compatibility level 130 or later - which the Copilot and Power Platform imports already require
  (`OPENJSON`, `STRING_SPLIT`). A statement that is tenant-wide by design says so
  with `/*scope:none*/`; a statement with no marker is **refused** under a filter rather than run
  unfiltered.
- **Fails closed.** A filter that cannot be read, cannot be parsed, or cannot be evaluated because the
  directory is unavailable refuses the report with a `503` and a code (`globalFilterUnavailable`,
  `globalFilterInvalid`, `filterDirectoryUnavailable`), which `apiFetch` turns into a translated
  `ReportScopeError`. A condition needing a value the viewer does not have - or a viewer the directory
  does not hold - matches **nobody**, whatever its operator, and the bar says why. Activity that cannot
  be linked to a person in the directory is not counted while a filter applies.
- **Readers without See PII** are never shown who a condition names. In the echo (`api/GlobalFilter/effective`
  and every report's) and in the Excel descriptions, the sign-in names on a user name, manager or
  management chain condition become a count (`hiddenValues`), and a viewer value that is a sign-in name -
  their own, their manager's - is withheld (`viewerValueHidden`) and read as "you" / "your manager". A
  filter that leaves such a reader **1 to 4 people** (`ReportScopeResolver.MinimumPeopleWithoutSeePii`,
  the same floor as `CopilotAdoptionOptions.MinSeatsPerSegment`) turns every figure into a few
  individuals' records, so their reports are refused with the portal's own See PII `403`, and the bar
  says why (`tooFewPeople`). None at all is not refused: an empty report shows nobody's activity. The
  reader alone - "only my own figures" - is refused too: the user import moves a directory row to
  whoever holds its sign-in name now, so a reused address carries its former holder's activity with
  it. The floor bounds **the people the filter admits from the directory**, not the
  people a given report has rows for nor the groups a report breaks them into: a department, country or
  adoption breakdown can still show a group smaller than five, exactly as it can with no filter.
- **What stays tenant-wide**, and says so beside the figures: the Overview page's data counts, Teams
  team-level figures (collaboration and conversations), agent and Azure cost figures, and the Copilot
  Adoption sections already marked as tenant-wide.
- **Administrators** see the filter applied too, so they see what everyone sees. Only those with **both
  Administration and See PII** can switch it off for their own view from the bar, which sets the session
  cookie `GlobalFilterBypass=1`; the server requires both permissions, and exports follow it because it
  is a cookie. `GlobalFilterProvider` remounts the Insights pages (`viewKey`) whenever the switch or the
  filter changes, so no figures from before the change sit under a bar describing after it. The cookie is
  the browser's, not the tab's, so a tab coming back into view reads the filter again when the cookie no
  longer agrees with what it last read - switched off or on in another tab. The browser clears a stored
  bypass only after a fresh server answer says the reader cannot bypass, including after a sign-in or
  permission change: an older tab's cached denial must not undo an authorized sign-in's explicit switch.
  A failed read leaves the shared cookie alone without granting a cached-denied reader bypass;
  a manually set or stale cookie never bypasses the server's permission checks.
- **Not a security boundary without roles.** With `EnforcePortalRoles=false` everyone who can sign in is
  granted both permissions and can switch the filter off; the editor warns about this.
- **Where it is kept:** one row in `dbo.portal_global_filters` (migration
  `202610011330001_PortalGlobalFilter`, with its manual upgrade script). Each web process caches it for
  a minute, and a save is refused (`409`) if someone else saved since the editor opened it. Every save
  is recorded in Application Insights as a `GlobalFilterChanged` event with who made it.
- **Where it lives:** `src/components/globalFilter` - `GlobalFilterBar` (the locked pills),
  `GlobalFilterEditor` (the editor, reusing `UserFilterClauseEditor` with its `viewer` options),
  `GlobalFilterProvider` (the reader's view of it), `describeGlobalFilter` and `globalFilterModel` (the
  `vu` wire format of `GlobalFilterCodec`); `src/pages/GlobalFilterPage.tsx`.

A new Insights page must render `<GlobalFilterBar />` - `insightsCoverage.test.ts` fails otherwise - and
its endpoints must resolve the scope with `ReportScopeResolver.ResolveAsync` and narrow every statement:
a marker on each SQL statement, or `scope.Includes(userId)` on rows held in memory.

## Languages

The portal ships in **English (en-GB)** and **Spanish (es-ES)**.

It opens in the visitor's own language with nothing to configure: an earlier explicit choice wins,
then the browser's `navigator.languages`, then English. Matching is on the primary subtag, so
`es-MX` and `es-419` get Spanish rather than falling back to English because the region is not
Spain. The picker is a globe in the brand bar — in the header rather than on a settings page,
because somebody who has landed on a portal in a language they cannot read also cannot navigate to
a settings page to fix it. The choice is kept in `localStorage`, and a browser with site data
blocked degrades to detection instead of throwing.

### How it works

| File | Role |
| --- | --- |
| `src/i18n/index.ts` | The public API. Import from here, not from the files behind it. |
| `src/i18n/catalog/en/<area>.ts` | English text for one area, as a flat `as const` object of dotted keys. |
| `src/i18n/catalog/es/<area>.ts` | The Spanish counterpart, typed `Record<keyof typeof en, string>`. |
| `src/i18n/catalog/index.ts` | Merges the modules, derives `TranslationKey`, and fetches non-English catalogs. |
| `src/i18n/I18nProvider.tsx` | Context, `useT()`/`useTNode()`, and `<html lang>`. |
| `src/i18n/locale.ts` | Locale-aware `formatNumber` / `formatDateParts` / `compareStrings`. |
| `src/i18n/lint/` | The untranslated-text check and its allow-list. |

```tsx
import { useT } from '../../i18n';

export default function Panel() {
  const t = useT();
  return <Text>{t('health.title')}</Text>;
}
```

`t()` takes a `TranslationKey`, so a typo or a renamed key is a build error rather than a key name
appearing on screen. `{placeholder}` markers are substituted from the second argument; `useTNode()`
does the same but accepts elements, so a sentence containing a link stays one translatable
sentence. `plural(count, oneKey, otherKey)` picks between two real keys — English and Spanish share
the same one/other split, so a runtime plural engine would buy nothing and would hide both forms
from the compiler.

Outside a component — in a thrown error, a chart callback, an exporter — use `translateActive()`
from `src/i18n/runtime`, which resolves in whatever language is currently in force. That is how the
API layer's error messages are translated: `src/api/*.ts` throws `Error`s whose `message` the pages
render directly, and an English failure message on a Spanish page is what a reader sees at the
moment something has already gone wrong. Import it from `../i18n/runtime`, not from `../i18n` —
the package index re-exports `LanguageSwitcher`, which would pull Fluent UI into the API chunk.

Text that came out of the customer's tenant — user and display names, departments, site and team
names, file names, URLs, agent and SKU names — is **never** translated. Only the product's own
wording is.

Numbers and dates must go through `formatNumber` / `formatDateParts` rather than a bare
`toLocaleString()`. This is correctness, not polish: `1,234` is one thousand two hundred and
thirty-four in English and **one point two three four** in Spanish.

### Only the language you read is downloaded

English is bundled — it is the source language and the runtime fallback. Every other language is a
separate chunk (one per language, not one per module: `Promise.all` over many chunks rejects if any
single request fails, which would drop a whole language over one proxy hiccup) that `main.tsx`
fetches for the language it detects, before the first render, so a Spanish reader never sees an
English flash and an English reader never downloads Spanish. A fetch that does fail falls the
*whole* language back to English — text, locale and `<html lang>` together — and picking the
language again retries it.

Measured on the production build (gzipped, eagerly-loaded chunks):

| | eager |
| --- | --- |
| before this feature | 178 kB |
| English reader | 275 kB |
| Spanish reader | 275 kB + 86 kB in one chunk |

**The point of the split is the third language, not the second.** Bundling every language would
make each new one a permanent download for every user, which turns "should we support Norwegian?"
into a question about page weight. This way it costs existing readers nothing.

The English catalog is the +94 kB: text that used to live inside each page's lazily-loaded chunk
now sits in the eager bundle. Moving it back — one catalog module per route, loaded with the page —
is possible but would mean each page registering its own catalog at runtime, and a page that forgot
would render raw keys. That trade (a guaranteed-correct 94 kB against a silently-breakable saving)
was not worth taking on an authenticated internal admin portal whose assets are content-hashed and
cached.

### Two checks stop a half-translated portal shipping

1. **`npm run lint` (`tsc --noEmit`)** — each Spanish module is typed against its English
   counterpart, so an English key with no Spanish translation is a *compile error*:
   `Property '"health.title"' is missing in type ... src/i18n/catalog/es/health.ts`.
   The production build runs the same type-check, so an untranslated string cannot be built.

2. **`npx vitest run src/i18n`** — catches what the type system cannot see:
   - `hardcodedStrings.test.ts` parses every page and component with the TypeScript AST and fails
     on any string a user could read that is not a catalog key, listing file, line and text. It
     covers JSX text, user-facing props (`label`, `title`, `aria-label`, `content`, `blurb`,
     `sublabel`, …), rendered expressions, the `label`/`title`/`what`/`how` properties of the
     constant tables this portal keeps most of its text in, toasts, **English passed as a *value*
     to `t()`** (which would land inside a translated sentence), and — as a catch-all — **any
     phrase of three words or more wherever it is written**, which is what catches a sentence
     assembled inside a helper and returned as a string.
   - `catalog.test.ts` runs against **every language in `LANGUAGES`**, and fails on a key claimed
     by two modules, a key not namespaced to its module, a language whose modules do not line up
     with English module-for-module, a `{placeholder}` present in one language and not the other,
     an empty translation, a sentence chopped into fragments that no translator can reassemble, an
     HTML entity that would be shown to the reader verbatim, and English pasted into a translated
     catalog to satisfy the compiler — which is the realistic way a half-translated page ships
     while every other check is green.
   - `placeholders.test.ts` fails when a call site does not supply a `{placeholder}` the string
     needs (it would render as literal `{count}`) or supplies one the string does not have (it is
     silently dropped, so a figure vanishes from the sentence). It resolves `t(plural(…))` to both
     of its keys, because that is the shape most likely to carry a placeholder.
   - `localeFormatting.test.ts` fails on any `toLocaleString()` / `toLocaleDateString(undefined, …)`
     / `new Intl.*(undefined, …)` outside `locale.ts`. That class is invisible to every other
     check, because such a call contains no string at all — and three of them survived the initial
     conversion in a module twelve components import from.

`src/i18n/lint/allowList.ts` is the only escape hatch, and is for text that reads *identically* in
both languages: Microsoft product names Microsoft itself does not translate (Copilot, Teams,
SharePoint, Power BI…), technical identifiers (SQL, CSV, GUID, UPN, DLP), units and symbols. An
ordinary English word added there defeats the whole mechanism, so additions are expected to be
challenged in review — and the `release-manager` agent reads every change to it in a release diff.

Both checks run in CI on every pull request. `tests.yml` builds the solution (which runs
`npm run build`, and therefore `tsc`) and then runs `npm run test` in this directory, inside the
`test_dotnet (Release)` job — a required status check on `dev` and `main`. A branch that leaves a
string untranslated cannot be merged.

### Adding a language

1. Add it to `LANGUAGES` in `src/i18n/languages.ts` with a region-qualified locale and its name in
   its own language, and extend the `Language` union.
2. Copy `src/i18n/catalog/es/` to `src/i18n/catalog/<id>/`, including its `index.ts`, and translate.
3. Add one line to `LOADERS` in `src/i18n/catalog/index.ts`: `import('./<id>')`. Write the path
   literally — Vite has to see it to split the chunk.
4. Register it in `MODULES` in `src/test/catalogModules.ts`, so the per-module checks cover it.
   `catalog.test.ts` runs its whole suite against every language in `LANGUAGES` and fails if one is
   missing from that list, so this cannot be forgotten silently.

`npm run lint` then lists every key still missing, and will not go green until the new language is
complete. A language with more than two plural categories (Polish, Arabic, Russian) needs a real
plural selector in `plural.ts` first — that is a deliberate deferral, not an oversight.

### Adding a string

Put it in the catalog module for its area, translate it in every language, and render it with
`t()`. Existing component tests assert **English** wording and `renderWithProvider` pins the
language to English, so a test failing after a translation change means the English text changed —
restore the English rather than editing the test.

## Local development

```bash
npm install
npm run dev      # Vite dev server (http://localhost:5173)
```

When running the Vite dev server in isolation the backend APIs are not available, so the
Home and User Lookup pages will report an API error and the Teams page reports that no Graph
token could be obtained - that is expected outside the ASP.NET host.

## Production build

```bash
npm run build    # type-checks then emits ./build (served by the ASP.NET site)
```

The Vite config sets `base = /Scripts/portal/build/` and `build.outDir = build`. The
ASP.NET `Web.csproj` runs `npm install` + `npm run build` automatically and the
`HomeController.Index` action serves the generated `build/index.html` at the site root.
