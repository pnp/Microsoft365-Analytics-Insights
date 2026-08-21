# Copilot Instructions

This repository contains multiple workloads (the C# AnalyticsEngine solution, SharePoint trackers, deployment assets, reports, etc.). Workload-specific guidance lives next to each workload.

## Sensitive & customer data handling (read first)

This is a **public** repository (part of a multi-fork network). Anything pushed is effectively permanent and world-readable, and a force-push does **not** remove it — orphaned commits stay reachable by SHA until GitHub Support purges them. So the bar is: never let real data in, and always double-check before it leaves your machine.

- **Never** put real customer, tenant, or environment data anywhere in this repo or its public surface — source, tests, commit messages, PR titles/descriptions, issues, screenshots, and **all public documentation (README, wiki, samples, reports)**.
- This explicitly includes **anything obtained by analysing a database, external/production system, or "example"/sample data**: real database names, tenant/organization/agent GUIDs, agent or user display names, SharePoint/OneDrive URLs and paths, file names, row counts, and raw payloads. If you queried a real DB or inspected a real payload to understand a bug, **do not** paste those values into code, tests, commit messages, or docs — reproduce the *shape*, not the data.
- **Always use synthetic substitutes**: `Contoso`, zeroed GUIDs (`00000000-0000-0000-0000-000000000000`), obviously-fake names/URLs, and rounded/made-up counts.
- **Always double-check before you commit, push, or publish.** Re-scan the full diff *and* any new/edited documentation for real names, GUIDs, DB names, URLs, counts, and payloads. When in doubt, ask before committing.
- **Azure deployment plans must be synthetic.** Files such as `.azure/plan.md` must never record real subscription/tenant/resource names or IDs, regions, hostnames/URLs, CIDRs, app IDs, user identities, deployment timestamps, resource counts, capacity/policy failures, or production validation/deployment results. Use placeholders and generic result shapes; keep real deployment context and evidence out-of-band.
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
- **PR bodies are for developers and reviewers.** Keep every PR description technical and implementation-focused: exact scope, architecture/code changes, tests, benchmarks, schema/config effects, risks, deferred work and reviewer hotspots.
- A `dev`→`main` release PR is also developer-focused. Its body should make the release diff reviewable and prove that migration/configuration claims are correct; it is deliberately not the customer-facing release note.
- **Issue-closing keywords: the default branch is `main`.** GitHub only auto-closes on merge to the default branch, so `Closes #N` in a PR targeting `dev` is **inert**. Prefer `Addresses #N` in `dev` PRs and let the `dev`→`main` release PR carry the closing keyword, so an issue closes when the fix actually reaches customers rather than when it reaches `dev`. Closing an issue earlier by hand is fine, but say in the comment that the fix is in `dev` and name the release PR it is riding.
- **Attribute a state change from the event data, not from prose.** Before asserting *why* an issue or PR is in some state, check `gh api repos/{owner}/{repo}/issues/{n}/timeline` — the `closed` event carries the actor and a `commit_id` (`null` means a manual close, not a keyword). Inferring the cause from a "Closes #N" line in a PR body has already produced a confident, wrong claim of a process violation in this repo. The same discipline applies to the release rule below: verify against the diff and the API, never the PR text.

## Releases
GitHub release notes are for operators and customers. They are a separate deliverable from the technical `dev`→`main` PR body.

**Every stable GitHub release must have admin-friendly level-300 release notes.** Write for an IT admin / M365 or Azure operator who runs the product — technically deep, but about *operating* it, not about the source code. That means:
- **Lead with the shape of the release** — is it a bug-fix release, a feature release, or a breaking/schema release? Say so in the first line.
- **Open with a "Should you upgrade?" summary table**: upgrade urgency and who's affected, database migrations (or "none"), configuration/config-schema changes (or "none"), breaking changes, how to upgrade, and expected downtime.
- **Per significant change, cover: who it affects, the observable symptom, the root cause, what changed, and the admin action required** (explicitly say "none" when there is none). Include real error text/log lines an admin would search for, and link the relevant wiki page.
- **Explain misleading errors.** If a symptom looks like something else (e.g. a network block that surfaces as a 401), say so — that's usually the most valuable part for the reader.
- **Close with a numbered upgrade checklist.**
- Level 300 means: assume Azure/M365 admin fluency (SKUs, private endpoints, DNS zones, Entra permissions, App Service), don't assume knowledge of this codebase, and never require reading the diff to understand the impact.

Also:
- **A push or merge to `main` is not complete when the branch update finishes.** Watch the Release build, locate the resulting stable GitHub release, and update that actual release's notes to the admin-level format above. Preparing notes before merge is useful, but the generated release must still be edited after it exists.
- **Verify the release downloads before declaring success.** The stable release must contain `AITrackerInstaller.zip`, `AppInsightsImporter.zip`, `ControlPanelApp.zip`, `Office365ActivityImporter.zip`, and `Website.zip`.
- **Verify manual database-upgrade assets.** For every migration in the release diff, confirm its matching `<migrationid>.manual.sql` is attached to the stable GitHub release and byte-matches the repository source. Upload missing scripts before reporting the release complete. If there are no migrations, state that no manual SQL assets are required.
  - **CI does not attach these — expect them to be missing.** `ci.yml` uploads `**/*.zip` only, so every `.manual.sql` must be uploaded to the release by hand. This is the single easiest release step to forget, and its absence is invisible until a DBA needs it.
  - **State the run order.** The manual scripts form a strict prerequisite chain: each hard-fails with `RAISERROR` severity 16 if its predecessor is not stamped in `__MigrationHistory`. Tell operators to run them in migration-id order and name the predecessor of the first.
- **Always explain changes in plain English** — say what changed and why it matters to someone running the product, not just the technical/internal detail. Prefer more explanation over less; err on the side of over-explaining a user-facing change.
- **Don't list pure code changes individually.** Internal-only changes with no user-visible effect (e.g. "Standardise ILogger variable names to `_logger` / `logger`", trimming redundant `PackageReference`s, cleaning binding redirects, test-data tweaks) must **not** each get their own bullet. Roll them all up under a single general **"Code maintenance"** line.
- Reserve individual, plain-English bullets for changes an operator or end-user would actually notice: new features, bug fixes, installer/UI changes, performance/reliability improvements, and any schema/database or upgrade-step changes.
- **Verify the claims against the diff before publishing** — especially "no migrations" / "no config-schema change". Check `Migrations/`, `Create DB.sql` and `CONFIG_VERSION` in the `main..dev` diff rather than trusting the PR text.
- **Gate stable on proven schema changes.** Every **performance-motivated** SQL schema change in a `dev`→`main` release must have a measured before/after benchmark proving a positive impact (logical reads **and** elapsed time at synthetic scale, plan operator, and more than one selectivity — e.g. a narrow and a wide query window, or a small and a large batch). If such a migration in the diff has no measurement, it is **not** approved for stable — either get it measured or hold it back.
  - **Purely additive schema is out of scope**, and saying so is part of the review: new empty tables, `NULL`able column adds, and the FK indexes that come with them have no "before" query to measure. Classify each migration explicitly in the release PR (additive / removal / performance-motivated) rather than leaving it implicit — an unclassified migration reads as an unmeasured one.
  - See *Prove every schema change improves performance BEFORE it is approved for stable* in [`src/AnalyticsEngine/.github/copilot-instructions.md`](../src/AnalyticsEngine/.github/copilot-instructions.md), which is the authoritative detail for this rule.
- **Give admins an upgrade-time estimate for every migration**, as a function of table size (e.g. 1M / 10M / 100M rows), and say whether the build is online or offline on their SQL edition. Index builds on the large fact tables (`audit_events`, `hits`) are the ones that decide the maintenance window.
- The `release-manager` agent (`.github/agents/release-manager.agent.md`) automates this end to end.

## Documentation
- The wiki repo for Microsoft365-Analytics-Insights is normally cloned as a sibling directory named `Microsoft365-Analytics-Insights.wiki` (e.g., `V:\Repos\Microsoft365-Analytics-Insights.wiki`).
- When a docs update is requested, make the changes in the wiki repo.
- If the wiki repo does not exist at the expected location, ask the user to clone it first before proceeding.
