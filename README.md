![Design Header](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/media/design.jpg)

# Microsoft 365 Advanced Analytics

**Own your Microsoft 365 usage data — and get far more insight than the admin center gives you.**

Microsoft 365 Advanced Analytics is an open-source engine that continuously collects *enhanced* usage data from across Microsoft 365 — SharePoint, Teams, Outlook, OneDrive, Viva Engage (Yammer), Copilot, Power Platform and more — into a **single SQL database you own**. Power BI reports and dashboards then build on that dataset, so you can analyse adoption, engagement and ROI in ways the built-in reports simply can't.

> Microsoft 365 already ships usage reports. This solution exists for everything those reports *don't* do: long-term history, raw queryable data, cross-workload correlation, and page/call/message-level detail — all joined to your own org structure.

## Why it's more than the built-in reports

| | Microsoft 365 built-in reports | Microsoft 365 Advanced Analytics |
|-|-|-|
| **History** | Rolling 7–180 days | Keep as long as you want — *you* control retention |
| **Data access** | Locked in the admin center, limited export | One SQL database in **your** tenant — query it any way |
| **Scope** | Siloed per workload, pre-aggregated | Unified, joinable schema across every workload |
| **Granularity** | High-level counts | Page hits/clicks/sessions, per-call quality, per-email + sentiment, Copilot interactions, files & resources |
| **People** | Names often de-identified | Identifiable users enriched with department, manager, job title, office and license/SKU |
| **Reporting** | Microsoft's fixed report set | Your own Power BI & dashboards (templates included) |
| **Enrichment** | — | Optional AI sentiment, language & keyword detection |

## What you can do with it

- **Measure real adoption** by department, region, role or license — not just tenant-wide totals.
- **See how SharePoint is actually used** — page hits, sessions, link clicks, searches, render performance, geography and device, right down to the page.
- **Track Copilot adoption & ROI** — who's using it, in which apps and agents, against which files and resources.
- **Right-size licensing** with per-user, per-app usage history.
- **Watch Teams call quality** and meeting habits at the session level.
- **Trend over years**, not weeks, with history you keep.

## What data is collected

Enable only the areas you need — permissions scale accordingly. Fully enabled, the engine collects:

- **SharePoint Online** — web traffic (page hits, clicks, sessions, performance, geo/device), file activity, searches, page metadata, comments & likes (with sentiment).
- **SharePoint & OneDrive usage** — per-user daily activity; per-site weekly activity & storage.
- **Teams & Calls** — teams, channels, membership, add-ins; per-user activity; call & meeting detail with quality/feedback.
- **Outlook** — usage activity; optional per-recipient sent-email records with sentiment.
- **Viva Engage (Yammer), Stream, M365 Apps & platforms** — adoption and activity signals.
- **Copilot** — interactions, files, meetings, accessed resources (with sensitivity labels), models used.
- **Power Platform** — Power Apps / Automate / BI and Copilot Studio adoption events.
- **Users** — profile attributes (department, manager, title, office) and assigned SKUs.

> Full data-collection map: **[What data is collected](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Data%20Collection)**.

## Getting started

All documentation lives in the **[wiki](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki)**.

1. **[Prerequisites](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Prerequisites)** — service principals, permissions (Graph/Microsoft 365 API + Azure RBAC), tenant/subscription setup.
2. **[Install with the installer](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Deployment%20Guidance)** — the standard deployment.
3. **[Verify the deployment](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Verify)** — confirm the import jobs are running.

See also [Release notes](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Release%20Notes) and [Known issues](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Known%20Issues).

## Solutions built on the engine

- **[Activity & Usage Analytics](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Analytics)** — Power BI reporting over the dataset.
- **[Copilot Analytics](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Copilot)** — Copilot usage & adoption reporting.
- **SharePoint Insights** — additional solution built on the engine.

Some require extra configuration after install — see [Additional solutions](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Deployment%20Guidance#additional-solutions).

## Architecture & running costs

The engine runs on a handful of Azure components (App Service web-jobs + admin site, Azure SQL, Redis, optional Service Bus / Cognitive Services / Automation). A medium environment (~20,000 users, 1 year of data) is roughly **€170/month**, dominated by the App Service plan and SQL database — *indicative only; verify current pricing for your region and scale*.

Full breakdown: **[Architecture & costs](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Architecture%20and%20Costs)**.

![Architecture diagram](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/media/architecture.jpg)
