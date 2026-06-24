# Microsoft 365 Advanced Analytics - Admin SPA (`admin-app`)

A React single-page application that provides admin tooling for the Microsoft 365 Advanced
Analytics Engine. It is the whole admin web experience: the ASP.NET `Web` project serves it
at the site root (`/`, via `HomeController.Index`) and its built assets live under
`/Scripts/admin-app/build/`.

> This app replaces the old single-purpose `teams-permission-grant` sample and the old
> server-rendered home page. It is built with **Vite + React 19 + TypeScript** (the previous
> Create React App tooling was deprecated).

## Pages

| Route (hash) | Page | What it does |
| --- | --- | --- |
| `#/home` | **Home** | System status: data counts and configuration (SQL, Redis, Cognitive, Service Bus, calls webhook). The default page. |
| `#/teams` | **Teams Permissions** | Authorise / de-authorise Teams for deep analytics (stores a delegated refresh token in Redis). Ported from the original app. |
| `#/user-lookup` | **User Data Lookup** | Enter a user's UPN to see all of their data held in SQL: profile, per-category record counts, and drill-down to recent rows. |

Routing uses `HashRouter`, so the whole SPA is served by a single MVC action and no IIS /
MVC route changes are needed to add pages.

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

The Vite config sets `base = /Scripts/admin-app/build/` and `build.outDir = build`. The
ASP.NET `Web.csproj` runs `npm install` + `npm run build` automatically and the
`HomeController.Index` action serves the generated `build/index.html` at the site root.
