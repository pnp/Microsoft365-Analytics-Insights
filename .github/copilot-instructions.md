# Copilot Instructions

This repository contains multiple workloads (the C# AnalyticsEngine solution, SharePoint trackers, deployment assets, reports, etc.). Workload-specific guidance lives next to each workload.

## Sensitive & customer data handling (read first)

This is a **public** repository (part of a multi-fork network). Anything pushed is effectively permanent and world-readable, and a force-push does **not** remove it — orphaned commits stay reachable by SHA until GitHub Support purges them. So the bar is: never let real data in, and always double-check before it leaves your machine.

- **Never** put real customer, tenant, or environment data anywhere in this repo or its public surface — source, tests, commit messages, PR titles/descriptions, issues, screenshots, and **all public documentation (README, wiki, samples, reports)**.
- This explicitly includes **anything obtained by analysing a database, external/production system, or "example"/sample data**: real database names, tenant/organization/agent GUIDs, agent or user display names, SharePoint/OneDrive URLs and paths, file names, row counts, and raw payloads. If you queried a real DB or inspected a real payload to understand a bug, **do not** paste those values into code, tests, commit messages, or docs — reproduce the *shape*, not the data.
- **Always use synthetic substitutes**: `Contoso`, zeroed GUIDs (`00000000-0000-0000-0000-000000000000`), obviously-fake names/URLs, and rounded/made-up counts.
- **Always double-check before you commit, push, or publish.** Re-scan the full diff *and* any new/edited documentation for real names, GUIDs, DB names, URLs, counts, and payloads. When in doubt, ask before committing.
- If real data does reach a public location, treat it as **compromised**: flag it immediately so history can be rewritten and a GitHub Support purge requested (a force-push alone is not enough).

## C# / AnalyticsEngine

For all work inside `src/AnalyticsEngine/` (the C# solution, web-jobs, installer, Common libraries and tests), follow the conventions in:

- [`src/AnalyticsEngine/.github/copilot-instructions.md`](../src/AnalyticsEngine/.github/copilot-instructions.md)

That file is the source of truth for:

- Project guidelines (e.g. `InsertBatch` row-by-row implementation preference)
- NuGet package management (App.Template.config vs App.config, .NET Standard 2.0 vs .NET Framework 4.8 mismatches)
- Azure Cache for Redis auth conventions
- Documentation / wiki repo location (`Microsoft365-Analytics-Insights.wiki` sibling directory)

Always read it before making changes under `src/AnalyticsEngine/`.

## Pull requests
- Always open PRs against the `dev` branch unless the user explicitly says to target `main` (or another branch).
- This applies to both human-driven and Copilot-driven PRs, including coding-agent tasks that auto-create branches.
- If a PR has already been opened against the wrong base, retarget it with `gh pr edit <num> --base dev` rather than closing and reopening.

## Releases
Release descriptions (the dev→main release PR body and the GitHub release notes) are read by operators and customers, not just developers.
- **Always explain changes in plain English** — say what changed and why it matters to someone running the product, not just the technical/internal detail. Prefer more explanation over less; err on the side of over-explaining a user-facing change.
- **Don't list pure code changes individually.** Internal-only changes with no user-visible effect (e.g. "Standardise ILogger variable names to `_logger` / `logger`", trimming redundant `PackageReference`s, cleaning binding redirects, test-data tweaks) must **not** each get their own bullet. Roll them all up under a single general **"Code maintenance"** line.
- Reserve individual, plain-English bullets for changes an operator or end-user would actually notice: new features, bug fixes, installer/UI changes, performance/reliability improvements, and any schema/database or upgrade-step changes.

## Documentation
- The wiki repo for Microsoft365-Analytics-Insights is normally cloned as a sibling directory named `Microsoft365-Analytics-Insights.wiki` (e.g., `V:\Repos\Microsoft365-Analytics-Insights.wiki`).
- When a docs update is requested, make the changes in the wiki repo.
- If the wiki repo does not exist at the expected location, ask the user to clone it first before proceeding.
