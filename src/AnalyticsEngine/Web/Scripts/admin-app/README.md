# Microsoft 365 Advanced Analytics - Admin SPA (`admin-app`)

A React single-page application that provides admin tooling for the Microsoft 365 Advanced
Analytics Engine. It is served by the ASP.NET `Web` project from
`/Scripts/admin-app/build/` and surfaced through the **Admin** page (`HomeController.AdminApp`).

> This app replaces the old single-purpose `teams-permission-grant` sample. It is built with
> **Vite + React 19 + TypeScript** (the previous Create React App tooling was deprecated).

## Pages

| Route (hash) | Page | What it does |
| --- | --- | --- |
| `#/teams` | **Teams Permissions** | Authorise / de-authorise Teams for deep analytics (stores a delegated refresh token in Redis). Ported from the original app. |
| `#/user-lookup` | **User Data Lookup** | Enter a user's UPN to see all of their data held in SQL: profile, per-category record counts, and drill-down to recent rows. |

Routing uses `HashRouter`, so the whole SPA is served by a single MVC action and no IIS /
MVC route changes are needed to add pages.

## Backend APIs used

| Window var | Endpoint | Purpose |
| --- | --- | --- |
| `o365AnalyticsTokenAPI` | `api/SiteTokenAPI` | Server-side delegated OAuth token for the signed-in admin. |
| `o365AnalyticsAuthAPI` | `api/TeamsAuthAPI` | Get / set Teams deep-analytics authorisation. |
| `o365AnalyticsUserLookupAPI` | `api/UserDataLookup` | User data lookup (summary + per-category detail). |

## Local development

```bash
npm install
npm run dev      # Vite dev server (http://localhost:5173)
```

When running the Vite dev server in isolation the backend APIs are not available, so the
Teams page falls back to client-side MSAL sign-in and the User Lookup page will report an
API error - that is expected outside the ASP.NET host.

## Production build

```bash
npm run build    # type-checks then emits ./build (served by the ASP.NET site)
```

The Vite config sets `base = /Scripts/admin-app/build/` and `build.outDir = build`. The
ASP.NET `Web.csproj` runs `npm install` + `npm run build` automatically and the
`HomeController.AdminApp` action serves the generated `build/index.html`.
