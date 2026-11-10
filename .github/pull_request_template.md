<!--
**Please remember to always target `dev` in your pull requests.**

**Please keep pull requests light. They will be easier to merge.**
-->

## Title

<!--
Provide a succinct and descriptive title for the pull request, e.g., "Improve caching mechanism for API calls"
-->

## Type of Change

- [ ] New feature
- [ ] Bug fix
- [ ] Documentation update
- [ ] Refactoring
- [ ] Hotfix
- [ ] Security patch
- [ ] UI/UX improvement

## Description

<!--
New feature, Bug fixing, or Improvement?
Please include a summary of the change and which issue is fixed. Also include relevant motivation and context.
-->

## Related issue(s)

<!--
Link the issue(s) this PR addresses.

Note on keywords: the default branch is `main`, so a closing keyword in a PR that
targets `dev` is INERT - it will not close anything on merge. Prefer "Addresses #X"
in a `dev` PR, and let the `dev` -> `main` release PR do the closing, where the
keyword actually fires and the fix has genuinely reached customers.
-->

- Addresses #X

## Check list

- [ ] Related issue / work item is attached
- [ ] Changes are tested
- [ ] Tests are written (if applicable)
- [ ] Documentation is updated (if applicable)
- [ ] No real customer/tenant data (names, GUIDs, URLs, DB names, row counts, payloads) in the code, tests, commit messages or this description

### If this PR changes the database schema (a new `Migrations/` entry)

<!-- Delete this section if there is no migration. -->

- [ ] The migration id sorts after the current latest, and `.cs` / `.Designer.cs` / `.resx` are all registered in `Entities.csproj`
- [ ] A `<migrationid>.manual.sql` is included, with the `Up` SQL verbatim and a guarded `__MigrationHistory` stamp, and it has been run to confirm it applies, stamps, and is a no-op on re-run
- [ ] Any test that hard-codes the latest migration id is updated (`UrlFullUrlMigrationPipelineTests.LatestId`)
- [ ] `Create DB.sql` updated if the table is defined there
- [ ] **If the change is performance-motivated:** before/after benchmark at synthetic scale in the description - logical reads **and** elapsed time, plan operator, and more than one selectivity/window. A migration that cannot point at its measurement is not approved for stable.
- [ ] Index build time and storage overhead measured, so release notes can give an upgrade-window estimate

### If this PR changes the installer config schema

- [ ] `CONFIG_VERSION` bumped in `BaseSolutionInstallConfig.cs` with a `// History:` line

### If this PR adds or rewords text in the web portal (`src/AnalyticsEngine/Web/Scripts/portal`)

<!-- Delete this section if no portal text changed. -->

- [ ] Every new or reworded string is in both `src/i18n/catalog/en/<area>.ts` and `src/i18n/catalog/es/<area>.ts`, and the Spanish of every reworded English string has been re-read
- [ ] `npm run lint` and `npx vitest run src/i18n` pass in the portal directory
- [ ] Nothing was added to `src/i18n/lint/allowList.ts` except text that reads identically in both languages

### If this PR changes the SharePoint tracker (`src/SPO`)

<!-- Delete this section if nothing under src/SPO changed. -->

- [ ] A change to `AITracker.js` bumps `AI_TRACKER_VER`
- [ ] A change to the SPFx extension bumps `version` in `package-solution.json` and `AITRACKER_MODERN_VERSION` together, the rebuilt `.sppkg` is committed, and `./.github/scripts/Test-SpfxPackage.ps1` passes

### If this PR calls an external API it did not call before

<!-- Delete this section if there is no new external call. -->

- [ ] The description cites evidence that it exists: for a Microsoft Graph function, entity or property, its exact name in `https://graph.microsoft.com/v1.0/$metadata` or `https://graph.microsoft.com/beta/$metadata`, or its Microsoft Learn API reference page; for any other API, its official reference page. Tests with synthetic fixtures don't prove an endpoint exists.
