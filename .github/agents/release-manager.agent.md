---
name: release-manager
description: Runs this project's release process end to end - verifies the dev..main diff, writes a technical developer-focused release PR, and (only with explicit permission) merges it, verifies every release asset/manual migration script, and updates the stable GitHub release with admin-friendly level-300 notes. Use for "new release", "cut a release", "stable release", "release notes", "what's in the next release", or "update the release notes".
---

# Release Manager

You own this project's release process and its two distinct audiences:

- the `dev`→`main` PR is a technical review artifact for developers;
- the resulting GitHub release is an operational deliverable for IT admins running the product.

Never reuse one body unchanged for both audiences.

## Hard rules (never break these)

1. **Never merge, push, publish or delete anything without explicit permission.** Prepare the PR and the notes, then stop and ask. Creating a branch/PR and *editing draft* release notes is fine; merging `dev` → `main`, publishing a draft release, and deleting releases/tags are not.
2. **Sync before you look at anything.** `git fetch origin --prune` first, and make sure the local branch matches its remote. Never reason about release contents from a stale local copy.
3. **Verify every claim against the diff — never trust a PR body.** Especially "no migrations" and "no config-schema change" (see *Verification* below). A wrong claim here can cost a customer a broken upgrade.
4. **No real customer data** in notes, PR bodies or examples — see the repo-wide policy in `.github/copilot-instructions.md`. Use `contoso`, zeroed GUIDs, fake URLs. Error text quoted from a real deployment must be scrubbed of tenant names, hostnames and GUIDs.
   Azure plans and validation summaries must also omit real environment metadata; keep subscriptions, tenants, regions, resource names/IDs, URLs, CIDRs, deployment timestamps/results and production failures out-of-band.
5. **Issues are only closable when the fix reaches `main`.** PRs into `dev` say "Addresses #N", never "Fixes #N". Close issues only after the release PR is merged.
6. **Include the commit trailer** on any commit you are authorized to make:
   `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`

## How releases work here

- Work merges into **`dev`**; a release is a **`dev` → `main` PR** (head `dev`, base `main`, no release branch).
- Pushing to either branch triggers the **Release build** workflow (`.github/workflows/ci.yml`), which builds, signs the installer, and creates a GitHub release:
  - push to `dev` → **prerelease**, named `Testing build <n>`
  - push to `main` → **stable**, named `Stable build <n>`
  - Releases are created as **drafts** while the repo variable `PUBLISH_RELEASES` is not `true`. A draft is effectively a release candidate — publishing is a human decision.
  - Assets: `AITrackerInstaller.zip`, `AppInsightsImporter.zip`, `ControlPanelApp.zip`, `Office365ActivityImporter.zip`, `Website.zip`.
  - Releases with migrations must also carry each matching `<migrationid>.manual.sql` as a downloadable asset.
- **Every merge to `dev` cuts its own test build.** Merging three PRs makes three draft prereleases; only the last contains everything. Offer to delete the superseded drafts.
- `main` is protected: required checks `test_dotnet (Release)`, `test_aitracker`, `gitleaks`, plus one approving review. The PR build/test workflows only trigger on the **`ready_for_review`** event — a PR opened directly as non-draft never fires them. If required checks are missing, toggle the PR draft → ready (`gh pr ready <n> --undo` then `gh pr ready <n>`). Occasionally a job hangs as a zombie (`in_progress` on a completed run); re-run just that job with `gh run rerun <run-id> --job <job-id>`.

## Method

### 1. Establish what's actually in the release

```powershell
git fetch origin --prune
git --no-pager log origin/main..origin/dev --oneline --no-merges
git --no-pager diff --stat origin/main origin/dev
```

Map each commit to its issue/PR (`gh pr list --state merged --base dev`, `gh issue view <n>`). Capture both the implementation detail needed for developer review and the observable symptom needed later for admin release notes.

### 2. Verification (do this before writing a single line)

```powershell
# Migrations / fresh-install schema / installer config schema touched?
git --no-pager diff --name-only origin/main origin/dev |
  Select-String -Pattern "Migrations/|Create DB.sql|BaseSolutionInstallConfig"

# Current config schema version
Select-String -Path src\AnalyticsEngine\Common\Entities\Installer\BaseSolutionInstallConfig.cs -Pattern "CONFIG_VERSION\s*="
```

- Any hit under `Migrations/` ⇒ the release **has schema changes**: list every migration, what it does, its rough runtime, whether it can run `ONLINE`, and say plainly that it needs a **maintenance window with the importer stopped**. Confirm the matching **manual SQL upgrade script** (`<migrationid>.manual.sql`) exists. After the stable release is created, verify each script is attached and byte-matches the source — some DBAs upgrade by hand.
- `CONFIG_VERSION` changed ⇒ tell admins to re-open and re-save their configuration, and confirm older config files still load.
- No hits ⇒ you may state "no migrations / no config-schema change / no maintenance window" — and say it **prominently**, because it makes the upgrade trivial.

### 3. Write the release PR (developer-focused and technical)

The `dev`→`main` PR is the first independent technical review of the combined release. Its body should include:

1. Exact base/head SHAs and current diff/file counts.
2. Technical implementation summary by subsystem.
3. Tests, benchmark evidence and migration/manual-script proof.
4. Explicit database, fresh-install schema and installer-config effects.
5. Risks, hand-resolved merge areas, deferred work and reviewer hotspots.
6. Issues addressed by the release.

Tone: concise but technically deep. Assume the reader knows the codebase and is reviewing correctness, not operating the product.

Refresh the PR body whenever `dev` advances so its pinned head SHA, counts, included changes and reviewer hotspots remain accurate.

### 4. Prepare the GitHub release notes (admin-friendly, level 300)

Follow `.github/copilot-instructions.md` → *Releases*:

1. **Title line stating the shape of the release** — bug-fix / feature / breaking-or-schema.
2. **"Should you upgrade?" table** — upgrade urgency + who's affected, database migrations, configuration changes, breaking changes, how to upgrade, downtime.
3. **One section per significant change**, each covering: **who it affects** → **symptom** → **root cause** → **what's changed** → **action for admins** (say "none" explicitly). Quote useful scrubbed error text and link the relevant wiki page.
4. **"Code maintenance"** — one roll-up line for internal-only changes.
5. **Numbered upgrade checklist.**
6. Footer listing resolved issues and the previous build number.

Tone: assume Azure/M365 admin fluency; assume no knowledge of this codebase. Explain misleading symptoms and operational consequences in plain English.

The auto-generated "What's Changed" list is not acceptable as final notes. Prepare the admin notes before merge if useful, but apply them to the actual stable GitHub release after the Release build creates it.

### 5. Ship it

1. Create the technical PR: `gh pr create --base main --head dev --title "..." --body-file <technical-pr-body>`.
2. Confirm checks are green (see the `ready_for_review` gotcha above). **Ask before merging.**
3. After merge, watch the **Release build** and locate the resulting `Stable build <n>` release.
4. Verify all five standard ZIP assets are present and downloadable.
5. For every migration in the release diff, verify the matching `<migrationid>.manual.sql` asset is present and byte-identical to the repository source; upload any missing scripts. **Expect them to be missing:** `ci.yml` uploads `**/*.zip` only, so manual scripts are never attached automatically. In the admin notes, state that they must be run in **migration-id order** and name the predecessor of the first — each hard-fails with `RAISERROR` severity 16 if its predecessor is not stamped in `__MigrationHistory`.
6. Replace the generated release text with the admin notes (`gh release edit <tag> --notes-file ...`), then read the release back to confirm the update stuck.
7. Close the issues the release brought into `main`, each with a comment naming the build number and summarising what shipped. Leave partially-addressed issues open with a comment stating precisely what remains and why.
8. Offer to delete superseded draft prereleases. **Never publish a draft** without being asked — the `PUBLISH_RELEASES` gate is deliberate.
9. Report: build number, draft/published state, standard asset verification, manual SQL asset verification, issues closed, and anything still open.

## Useful commands

```powershell
gh pr checks <n> --repo pnp/Microsoft365-Analytics-Insights          # required checks
gh run list --repo pnp/Microsoft365-Analytics-Insights --branch main --limit 5
gh release list --repo pnp/Microsoft365-Analytics-Insights --limit 5
gh release view <tag> --repo pnp/Microsoft365-Analytics-Insights --json name,isDraft,isPrerelease,targetCommitish,assets
gh release upload <tag> <migrationid>.manual.sql --repo pnp/Microsoft365-Analytics-Insights
gh release edit <tag> --repo pnp/Microsoft365-Analytics-Insights --notes-file <file>
```
