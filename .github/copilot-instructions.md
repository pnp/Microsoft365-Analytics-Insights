# Copilot Instructions

This repository contains multiple workloads (the C# AnalyticsEngine solution, SharePoint trackers, deployment assets, reports, etc.). Workload-specific guidance lives next to each workload.

## C# / AnalyticsEngine

For all work inside `src/AnalyticsEngine/` (the C# solution, web-jobs, installer, Common libraries and tests), follow the conventions in:

- [`src/AnalyticsEngine/.github/copilot-instructions.md`](../src/AnalyticsEngine/.github/copilot-instructions.md)

That file is the source of truth for:

- Project guidelines (e.g. `InsertBatch` row-by-row implementation preference)
- NuGet package management (App.Template.config vs App.config, .NET Standard 2.0 vs .NET Framework 4.8 mismatches)
- Azure Cache for Redis auth conventions
- Documentation / wiki repo location (`Microsoft365-Analytics-Insights.wiki` sibling directory)

Always read it before making changes under `src/AnalyticsEngine/`.

## Documentation
- The wiki repo for Microsoft365-Analytics-Insights is normally cloned as a sibling directory named `Microsoft365-Analytics-Insights.wiki` (e.g., `V:\Repos\Microsoft365-Analytics-Insights.wiki`).
- When a docs update is requested, make the changes in the wiki repo.
- If the wiki repo does not exist at the expected location, ask the user to clone it first before proceeding.
