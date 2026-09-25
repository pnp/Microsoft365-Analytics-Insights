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

## npm registry troubleshooting

- Use the public npm registry (`https://registry.npmjs.org/`) when it is available. If it is unavailable or a package cannot be fetched, test the Microsoft employee proxy before changing dependency versions or lockfiles: `npm config get registry`, `npm ping --registry=https://packagefeedproxy.microsoft.io/npm/`, and `npm view <package>@<version> version --registry=https://packagefeedproxy.microsoft.io/npm/`.
- On managed Microsoft devices, the approved proxy is `https://packagefeedproxy.microsoft.io/npm/`. Do not add credentials or machine-specific `.npmrc` settings to the repository. If the proxy test succeeds, use it consistently to regenerate the lockfile and verify the exact failing package/version before selecting a fallback.

## Web portal UI — every UI change ships with its translations

The web portal (`src/AnalyticsEngine/Web/Scripts/portal`) is **multilingual**: it ships in English (en-GB) and Spanish (es-ES), picks a language from the browser, and lets the user change it from the header. Both languages are first-class, so a panel added in English only is not "translated later" — it is a defect visible to every Spanish-speaking admin the moment it ships.

**The rule: no string a user can read may be typed into a component. It goes in the catalog, in every language, in the same change.**

- Text lives in `src/i18n/catalog/en/<area>.ts`, with its translation in `src/i18n/catalog/es/<area>.ts`, and is rendered with `t('<key>')` from `useT()`.
- `catalog/es/*.ts` is **typed against** `catalog/en/*.ts`, so adding an English key without a Spanish one is a **compile error** (`npm run lint`). This is not advisory; the build fails.
- `npx vitest run src/i18n` additionally fails on: a user-facing string that never reached the catalog (with a file/line worklist), English pasted into the Spanish catalog to silence the compiler, a `{placeholder}` that differs between languages or is not supplied at the call site, a key that does not match its module, and a number or date formatted with `toLocaleString()` instead of the locale-aware helpers.
- Data that came out of SQL or the Graph — user names, department names, site titles, file names, URLs, agent names, SKU names — is **never** translated. Only the product's own wording is.
- Numbers and dates must go through `formatNumber` / `formatDateParts` from `src/i18n`, not bare `toLocaleString()`. `1,234` means one thousand two hundred and thirty-four in English and **one point two three four** in Spanish, so this is correctness, not polish.
- Outside a component — a thrown error, a chart callback, an exporter — use `translateActive()` from `src/i18n/runtime`. The API layer's error messages go through it, because an English error on a Spanish page is what a reader sees when something has already gone wrong.
- `src/i18n/lint/allowList.ts` is the only way to exempt a string, and it is for text that reads **identically** in both languages (Microsoft product names, file formats, units). Adding an ordinary English word to it is how a half-translated portal gets shipped with every check green — expect an addition there to be challenged in review.
- **Rewording an existing English string silently invalidates its Spanish.** The key still exists in both languages, so both checks stay green while the Spanish is now a translation of the old sentence. This is one translation defect the tooling cannot see: when you change an English value, re-read its Spanish in the same edit.
- **Display text authored by the .NET API is the other blind spot.** A string the server writes and the SPA renders verbatim — a chart title, a KPI tile name, an availability reason — is invisible to every check, because from the SPA's side there is no string at all. The rule: **the API reports facts, the UI writes the sentences.** Where the server must send text, it sends a *stable key* beside it and the SPA maps that key to a catalog entry, falling back to the server's English only for a key this build does not recognise. `src/i18n/lint/serverAuthoredText.test.ts` reads the C# and fails when the two drift apart. If you add a figure, chart or reason to an API controller, add its catalog entry in the same change.

Both checks already run on every pull request: `tests.yml` builds the solution (which runs `npm run build`, hence `tsc`) and then runs `npm run test` in the portal directory, inside the required `test_dotnet (Release)` check. So an untranslated string blocks the merge, not just the release.

Full detail, including how to add a language: [`src/AnalyticsEngine/Web/Scripts/portal/README.md`](../src/AnalyticsEngine/Web/Scripts/portal/README.md) and the module doc comment in `src/i18n/index.ts`.

**The release process enforces this.** The `release-manager` agent refuses to cut a release whose diff touches the portal until both checks are green, and reads every `allowList.ts` change in the diff — see *Releases* below.

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

## Reviews: multi-model critique is opt-in
- **Do not run a multi-model critique loop by default** — no parallel review agents, no "review until a round comes back clean" — unless the user explicitly asks for one. It is slow and costly, and it is the wrong tool for day-to-day work.
- **New features favour a quick turnaround.** Build it, prove it with the targeted tests (and a visual check for UI work), push, and summarise concisely, so the user can iterate on it in a fast human + AI feedback loop.
- **Multi-model review is for hardening releases** and similar high-stakes changes — typically via the `release-critic` agent — and even then only when the user asks for it.

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
- **Publishing a release retires the testing builds before it.** As soon as a new stable **or** testing release is published, delete every earlier `Testing build <n>` release — published prereleases and drafts alike — together with its tag, so only the newest testing build is ever kept. Never delete a stable release, or a testing build numbered above the one just published. First make sure the surviving release's notes do not link to a build you are about to delete. This is standing permission: do it without asking. The procedure is *Retire superseded testing builds* in the `release-manager` agent.
- **Gate stable on a fully translated portal.** If the release diff touches `src/AnalyticsEngine/Web/Scripts/portal`, run `npm run lint` and `npx vitest run src/i18n` in that directory and require both to pass **before** writing the release PR. Either failing means new UI shipped without its Spanish text, which is a customer-visible defect on a page a Spanish-speaking admin cannot avoid — it is a blocker, not a release note. Read every change to `src/i18n/lint/allowList.ts` in the diff and challenge any ordinary English word added to it; that file is the only way to make the check ignore a string. If the diff does not touch the portal, say so explicitly rather than leaving it unstated.
- **Always explain changes in plain English** — say what changed and why it matters to someone running the product, not just the technical/internal detail. Prefer more explanation over less; err on the side of over-explaining a user-facing change.
- **Don't list pure code changes individually.** Internal-only changes with no user-visible effect (e.g. "Standardise ILogger variable names to `_logger` / `logger`", trimming redundant `PackageReference`s, cleaning binding redirects, test-data tweaks) must **not** each get their own bullet. Roll them all up under a single general **"Code maintenance"** line.
- Reserve individual, plain-English bullets for changes an operator or end-user would actually notice: new features, bug fixes, installer/UI changes, performance/reliability improvements, and any schema/database or upgrade-step changes.
- **Verify the claims against the diff before publishing** — especially "no migrations" / "no config-schema change". Check `Migrations/`, `Create DB.sql` and `CONFIG_VERSION` in the `main..dev` diff rather than trusting the PR text.
- **Gate stable on proven schema changes.** Every **performance-motivated** SQL schema change in a `dev`→`main` release must have a measured before/after benchmark proving a positive impact (logical reads **and** elapsed time at synthetic scale, plan operator, and more than one selectivity — e.g. a narrow and a wide query window, or a small and a large batch). If such a migration in the diff has no measurement, it is **not** approved for stable — either get it measured or hold it back.
  - **Purely additive schema is out of scope**, and saying so is part of the review: new empty tables, `NULL`able column adds, and the FK indexes that come with them have no "before" query to measure. Classify each migration explicitly in the release PR (additive / removal / performance-motivated) rather than leaving it implicit — an unclassified migration reads as an unmeasured one.
  - See *Prove every schema change improves performance BEFORE it is approved for stable* in [`src/AnalyticsEngine/.github/copilot-instructions.md`](../src/AnalyticsEngine/.github/copilot-instructions.md), which is the authoritative detail for this rule.
- **Give admins an upgrade-time estimate for every migration**, as a function of table size (e.g. 1M / 10M / 100M rows), and say whether the build is online or offline on their SQL edition. Index builds on the large fact tables (`audit_events`, `hits`) are the ones that decide the maintenance window.
- **Forward-port every stable release into the `net10` branch.** `net10` is the long-lived .NET 10 / ASP.NET Core PoC and a mirror of the stable release, not a feature branch — it rots when `main` moves, not when someone commits to it. Merge `main` → `net10` (never the reverse) as part of finishing the release, using the existing commit-message convention `Merge stable build <n> (origin/main) into net10`, and confirm the `net10 build` workflow (`.github/workflows/net10.yml`, which has a `drift_check` job) is green afterwards.
  - **The merge compiles but can be semantically wrong.** `net10` has no XML configuration: settings come from `appsettings.json` via `AnalyticsConfig.AppSettings`. Any `ConfigurationManager.AppSettings` call site merged in from `main` **compiles cleanly and silently reads nothing**, so grep the merged diff for `ConfigurationManager` and port those call sites. Conversely, a `main` change that only touches `App.config` / `Web.config` / `*.Template.config` / binding redirects / `packages.config` is a legitimate no-op on `net10` — record it as deliberately dropped rather than reconstructing it.
  - Never add `net10` to `ci.yml` or `tests.yml`: that renames the required status checks and blocks PRs on `dev`/`main` (issue #270).
- The `release-manager` agent (`.github/agents/release-manager.agent.md`) automates this end to end.
- The `release-critic` agent (`.github/agents/release-critic.agent.md`) hardens a release *before* it ships: it runs an iterative multi-model critique loop — review, fix blockers, re-review — until a round comes back clean, verifying each finding against the code and testing each fix in both directions. Run it **only when the user asks for it**, typically when preparing a release that carries schema changes — it is not part of day-to-day feature work (see *Reviews* above). It is deliberately a loop: in the session it was derived from, four consecutive rounds each found a defect introduced by the *previous* round's fix, and the two most damaging would have broken upgrades for exactly the customers the change was meant to protect.

## Documentation
- The wiki repo for Microsoft365-Analytics-Insights is normally cloned as a sibling directory named `Microsoft365-Analytics-Insights.wiki` (e.g., `V:\Repos\Microsoft365-Analytics-Insights.wiki`).
- When a docs update is requested, make the changes in the wiki repo.
- If the wiki repo does not exist at the expected location, ask the user to clone it first before proceeding.
