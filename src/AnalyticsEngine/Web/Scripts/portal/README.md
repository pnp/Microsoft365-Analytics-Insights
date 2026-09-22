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
| `o365AnalyticsSystemStatusAPI` | `api/SystemStatus` | System status / configuration for the Home page, plus the record counts for the imports this deployment runs. |
| `o365AnalyticsInstallLogAPI` | `api/InstallLog` | Install log (config history from `sys_configs`) for the Install Log page. |
| `o365AnalyticsProfilingStatusAPI` | `api/ProfilingStatus` | Profiling data freshness + paged `profiling.TraceLogs` for the Profiling page. |
| `o365AnalyticsReportsAPI` | `api/Reports` | Lite in-app reports: enabled areas (`/areas`) + weekly usage charts per area (`/copilot`, `/usage`, `/office-apps`, `/spo-audit`, `/web-traffic`, `/calls`, `/emails`). |
| `o365AnalyticsCopilotAdoptionAPI` | `api/CopilotAdoption` | Copilot licence adoption: availability, executive summary, licensed-user and licence-opportunity lists, and their CSV exports. |
| _(none - origin-relative)_ | `api/TeamsExplorer` | Teams Explorer: source availability, and one endpoint per tab (`/overview`, `/adoption`, `/meetings`, `/collaboration`, `/conversations`, `/people`) plus `/export/{section}` CSVs. |
| _(none - origin-relative)_ | `api/WebActivity` | SharePoint web activity: source availability, and one endpoint per tab (`/overview`, `/visits`, `/pages`, `/journeys`, `/geography`, `/search`, `/technology`) plus `/export/{section}` CSVs. |

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
| `data-print="only"` | Rendered on paper only. |
| `data-print="shell"` | The layout table that carries the running footer. Block flow on screen. |
| `data-print="footer"` | The `<tfoot>` repeated at the foot of every printed page. |

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

Text that came out of the customer's tenant — user and display names, departments, site and team
names, file names, URLs, agent and SKU names — is **never** translated. Only the product's own
wording is.

Numbers and dates must go through `formatNumber` / `formatDateParts` rather than a bare
`toLocaleString()`. This is correctness, not polish: `1,234` is one thousand two hundred and
thirty-four in English and **one point two three four** in Spanish.

### Only the language you read is downloaded

English is bundled — it is the source language and the runtime fallback. Every other language is a
separate chunk that `main.tsx` fetches for the language it detects, before the first render, so a
Spanish reader never sees an English flash and an English reader never downloads Spanish.

Measured on the production build (gzipped, eagerly-loaded chunks):

| | eager |
| --- | --- |
| before this feature | 178 kB |
| English reader | 272 kB |
| Spanish reader | 272 kB + 65 kB fetched |

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
     covers JSX text, user-facing props (`label`, `title`, `aria-label`, `content`, …), rendered
     expressions, the `label`/`title`/`what`/`how` properties of the constant tables this portal
     keeps most of its text in, and toasts.
   - `catalog.test.ts` fails on a key claimed by two modules, a key not namespaced to its module, a
     `{placeholder}` present in one language and not the other, an empty translation, a sentence
     chopped into fragments that no translator can reassemble, an HTML entity that would be shown
     to the reader verbatim, and English pasted into the Spanish catalog to satisfy the compiler —
     which is the realistic way a half-Spanish page ships while every other check is green.

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
2. Copy `src/i18n/catalog/es/` to `src/i18n/catalog/<id>/` and translate.
3. Add a loader for it in `LOADERS` in `src/i18n/catalog/index.ts`, listing each module's
   `import()` literally — Vite has to see them to split them into chunks.
4. Add it to `ES_MODULES`' sibling list in `src/test/catalogModules.ts` so the per-module checks
   cover it.

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
