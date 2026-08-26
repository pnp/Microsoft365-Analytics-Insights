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
| `#/insights/overview` | **Overview** | Tracking-data overview: how much of each workload is in the database. The default page. |
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
analytics. If `SiteTokenAPI` can't return a token, the SPA falls back to client-side MSAL.

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
Home and User Lookup pages will report an API error and the Teams page falls back to
client-side MSAL sign-in - that is expected outside the ASP.NET host.

## Production build

```bash
npm run build    # type-checks then emits ./build (served by the ASP.NET site)
```

The Vite config sets `base = /Scripts/portal/build/` and `build.outDir = build`. The
ASP.NET `Web.csproj` runs `npm install` + `npm run build` automatically and the
`HomeController.Index` action serves the generated `build/index.html` at the site root.
