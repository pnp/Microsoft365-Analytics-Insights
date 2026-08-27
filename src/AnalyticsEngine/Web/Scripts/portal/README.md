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
| `#/insights/overview` | **Overview** | Tracking-data overview: how much of each workload is in the database, plus how fresh it is. The default page. |
| `#/insights/reports` | **Reports** | In-app version of the Power BI reports: a sub-area per enabled workload, charting usage over a configurable window. |
| `#/insights/copilot-adoption` | **Copilot Adoption** | Which licensed users aren't getting value from their licence, and which unlicensed heavy users have the strongest case for one. |

**Administration** — running the service, for an IT operator.

| Route (hash) | Page | What it does |
| --- | --- | --- |
| `#/admin/health` | **Service health** | System health: overview, import liveness, exceptions, component health, data overview and configuration, each lazily loaded from its own cached endpoint. |
| `#/admin/install-log` | **Install log** | History of configurations applied to the solution (the `sys_configs` table): when, by whom, install messages, and the config JSON per entry. The most recent is the current configuration. |
| `#/admin/profiling` | **Profiling** | Current state of the profiling data: earliest/latest dates for each compiled profiling table and the source activity tables that feed it (each with the **SQL** behind it), plus a paged view of the profiling runbooks' trace log (`profiling.TraceLogs`). Lets admins quickly check the runbooks have run, data is fresh, and spot errors. |
| `#/admin/teams-permissions` | **Teams permissions** | Authorise / de-authorise Teams for deep analytics (stores a delegated refresh token in Redis). Ported from the original app. |
| `#/admin/user-lookup` | **User data lookup** | Enter a user's UPN to see all of their data held in SQL: profile, per-category record counts (broken down by workload, including Copilot and Power Platform; each row has a **SQL** button to view & copy the query behind its count), drill-down to recent rows, and which **import workloads** are enabled (so a legitimate 0 count is explained). |
| `#/admin/configuration` | **Service configuration** | What this deployment is pointed at: SQL, Redis, Cognitive Services and Service Bus, plus the Teams calls import state and the Graph call webhook (with a live validation POST to test it). |

Routing uses `HashRouter`, so the whole SPA is served by a single MVC action and no IIS /
MVC route changes are needed to add pages.

`src/navigation.tsx` is the single source of truth for both the router and the left nav, so
the two cannot drift — adding a page means adding one entry to `ROUTES`.

> The pre-split routes (`#/home`, `#/reports`, `#/teams`, `#/health`, ...) are **not**
> redirected. Anything unrecognised falls back to the Insights overview.

## Authentication

The user signs in via the server's Azure AD (OIDC) redirect, which gates the `[Authorize]`'d
host action. During that redirect the server captures the OAuth **refresh token** into the
encrypted, httpOnly auth cookie. The SPA then gets a fresh Graph **access token** from
`api/SiteTokenAPI` (which mints one from the cookie's refresh token). This works **without
Redis** — Redis is only needed to persist Teams refresh tokens for the importer's deep
analytics.

There is **no client-side sign-in**. A client-side MSAL fallback used to exist for when
`SiteTokenAPI` returned no token, but it was pinned to a hard-coded app registration that no
longer resolves (`AADSTS5000224`), so it could not sign anyone in — it only replaced a clear
failure with an opaque popup error, while adding `@azure/msal-browser` to the initial bundle for
every page. The Teams permissions page now explains what to do instead when no token is available.

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
Teams page's specific message instead of bouncing through a pointless sign-in.

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
| `o365AnalyticsUserLookupAPI` | `api/UserDataLookup` | User data lookup (summary + per-category detail). |
| `o365AnalyticsSystemStatusAPI` | `api/SystemStatus` | System status / configuration for the Home page. |
| `o365AnalyticsInstallLogAPI` | `api/InstallLog` | Install log (config history from `sys_configs`) for the Install Log page. |
| `o365AnalyticsProfilingStatusAPI` | `api/ProfilingStatus` | Profiling data freshness + paged `profiling.TraceLogs` for the Profiling page. |
| `o365AnalyticsReportsAPI` | `api/Reports` | Lite in-app reports: enabled areas (`/areas`) + weekly usage charts per area (`/copilot`, `/usage`, `/spo-audit`, `/web-traffic`, `/calls`, `/emails`). |
| `o365AnalyticsCopilotAdoptionAPI` | `api/CopilotAdoption` | Copilot licence adoption: availability, executive summary, licensed-user and licence-opportunity lists, and their CSV exports. |

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
