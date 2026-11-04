# Contributing to Microsoft 365 Advanced Analytics

First off, thank you for taking the time to contribute! This project is part of
the **Microsoft 365 & Power Platform Community (PnP)** and we welcome
contributions of all kinds — code, documentation, bug reports, and ideas — from
everyone.

By participating in this project you agree to abide by our
[Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting security issues

Please **do not** open public issues for security vulnerabilities. Follow the
process in our [Security Policy](SECURITY.md) instead.

## Ways to contribute

* **Report a bug** — open an issue using the *Bug report* template.
* **Request a feature** — open an issue using the *Feature request* template.
* **Improve the documentation** — the docs live in the
  [project wiki](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki).
* **Fix or build something** — pick up an existing issue (or open one to discuss
  first) and send a pull request.

For anything more than a trivial change, please open or comment on an issue
first so we can agree on the approach before you invest time in a pull request.

## Development environment

This is a Windows-based .NET Framework solution. To build it you will need:

* **Windows** — the installer is a WinForms app and several projects target
  .NET Framework 4.8.
* **Visual Studio 2022** (17.8 or later) with the *.NET desktop development*,
  *ASP.NET and web development*, and *Azure development* workloads — or a
  matching **MSBuild** install.
* **Node.js 26.13.0 (Lithium LTS)** — required to build the admin web app
  (`src/AnalyticsEngine/Web/Scripts/portal`) and the SharePoint AI Tracker
  (`src/SPO/AITracker/TypeScript`). The major is pinned repo-wide in
  [`.nvmrc`](.nvmrc); see [Node.js version](#nodejs-version) below.
* A SQL Server **LocalDB** instance, `(localdb)\MSSQLLocalDB`, for running the
  unit tests.
* **Azurite**, the Azure Storage emulator, listening for the Table service on
  `127.0.0.1:10002`. The sample debug configs in `deploy/Debug Config Files` set
  `Storage` to `UseDevelopmentStorage=true`, and the importer and web app keep
  their runtime state (import checkpoints, delta tokens, Teams authorisation
  tokens) in that account's `AnalyticsState` table. Visual Studio ships it
  (`Common7\IDE\Extensions\Microsoft\Azure Storage Emulator\azurite.exe`), or
  run `npm install -g azurite`. Without Azurite running, every state read fails,
  so the Graph user import - and the Graph imports after it - are deferred on
  every cycle, and the `StateStoreAzuriteTests` report Inconclusive. To run
  without it, leave `Storage` empty instead: state is then kept in memory and
  resets whenever the process restarts.

A full end-to-end run also needs Azure resources (Azure SQL, App Service, a storage
account, and others). See the
[Architecture & costs](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Architecture%20and%20Costs)
and
[Prerequisites](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Prerequisites)
wiki pages for details.

### Before your first build: create your local config files

Each executable project generates its `App.config` (or `Web.config`) at build
time: MSBuild's `TransformXml` task applies `App.$(Configuration).config` to
`App.Template.config` (`Web.$(Configuration).config` to `Web.Template.config` in
the `Web` project). The Release transforms are committed. The Debug transforms
are **not**, because they hold your own credentials, and git ignores them. A
fresh clone therefore has no `App.Debug.config` / `Web.Debug.config`, and a Debug
build fails at the transform step until you copy the templates in
[`deploy/Debug Config Files`](deploy/Debug%20Config%20Files/README.md):

| Copy | To |
|---|---|
| `deploy/Debug Config Files/Unit tests/App.Debug.config` | `src/AnalyticsEngine/Tests.UnitTests/` |
| `deploy/Debug Config Files/Webjobs/App.Debug.config` | `src/AnalyticsEngine/WebJob.Office365ActivityImporter/` **and** `src/AnalyticsEngine/WebJob.AppInsightsImporter/` |
| `deploy/Debug Config Files/Web/Web.Debug.config` | `src/AnalyticsEngine/Web/` |
| `deploy/Debug Config Files/ControlPanel/App.Debug.config` | `src/AnalyticsEngine/App.ControlPanel/` |

From the repository root:

```powershell
$t = 'deploy\Debug Config Files'; $p = 'src\AnalyticsEngine'
Copy-Item "$t\Unit tests\App.Debug.config" "$p\Tests.UnitTests\"
Copy-Item "$t\Webjobs\App.Debug.config" "$p\WebJob.Office365ActivityImporter\"
Copy-Item "$t\Webjobs\App.Debug.config" "$p\WebJob.AppInsightsImporter\"
Copy-Item "$t\Web\Web.Debug.config" "$p\Web\"
Copy-Item "$t\ControlPanel\App.Debug.config" "$p\App.ControlPanel\"
```

The unit-test, WebJob and web templates point SQL at `(localdb)\MSSQLLocalDB`
(database `UnitTestingSPOInsights`) and `Storage` at Azurite. Their runtime
credentials (`ClientID`, `ClientSecret`, `TenantGUID`) are empty:

* **With no development tenant**, set `ClientID` and `TenantGUID` in
  `Tests.UnitTests/App.Debug.config` to `00000000-0000-0000-0000-000000000000` and
  leave `ClientSecret` empty. That's the identity CI uses for pull requests that
  can't read its secrets. Don't leave `TenantGUID` empty: the configuration
  parses it as a GUID, so every test that reads the configuration would fail.
* **To run against a development tenant**, fill in its runtime app
  registration. Never use a production tenant's credentials.

Never commit these files. Git ignores them, so keep it that way.

### Building

Open `src/AnalyticsEngine/O365 Advanced Analytics Engine.sln` in Visual Studio
and build, or from a Developer PowerShell for Visual Studio:

```powershell
cd src\AnalyticsEngine
msbuild ".\O365 Advanced Analytics Engine.sln" /t:Restore
msbuild ".\O365 Advanced Analytics Engine.sln" /t:Build /p:Configuration=Debug
```

Building the `Web` project also restores and builds the portal: it runs
`npm ci` and `npm run build` in `Web/Scripts/portal`, so the Node.js version
below must be on your `PATH`.

> Note: building the solution with `dotnet build` is not supported — use Visual
> Studio or MSBuild.

### Node.js version

Every part of the repository that runs `node` or `npm` builds on the **same
Node.js major**, and that major lives in exactly one place: the
[`.nvmrc`](.nvmrc) at the repository root (currently **26.13.0**, Lithium LTS).

If you use `nvm`, `nvm-windows` or `fnm`, run this once from the repository
root and you are on the right version:

```powershell
nvm install 26.13.0
nvm use 26.13.0
```

Otherwise install Node.js 26.13.0 from [nodejs.org](https://nodejs.org/).

The pin is enforced, not merely documented, so a mismatch fails fast with a
clear message instead of producing a subtly different bundle:

* **CI** — every workflow job that runs `npm` uses
  `actions/setup-node` with `node-version-file: .nvmrc`, so no workflow
  hardcodes a version and none inherits whatever the runner image ships.
* **MSBuild** — `Web.csproj` builds the portal SPA by shelling out to `npm`,
  so it depends on the `CheckNodeVersion` target in
  [`build/NodeVersion.targets`](build/NodeVersion.targets), which reads the same
  `.nvmrc` and errors if the Node on `PATH` is a different major.
* **npm** — each Node project declares an `engines.node` range matching the
  pin, and sets `engine-strict=true` in its `.npmrc` so `npm ci` / `npm install`
  hard-fail with `EBADENGINE` rather than printing a warning and carrying on.

To move to a new Node major, change `.nvmrc` **and** the `engines.node` range in
each `package.json`; nothing else hardcodes a version.

#### Exception: the SPFx extension

`src/SPO/ModernPagesAITrackerExtension` is a SharePoint Framework project, and
SPFx supports only the Node major that Microsoft ships support for — currently
**Node 22**, not 26. It therefore keeps its own `.nvmrc` and its own
`engines.node` range, and is deliberately excluded from the repo-wide pin. It is
not built by CI. Run `nvm use` from *that* directory before building it, and
raise the pin only when the SPFx version in its `package.json` supports a newer
Node.

### Running the tests

CI (`.github/workflows/tests.yml`) runs the whole suite for each part a pull
request changes, so locally run the tests for the area you changed rather than
everything.

**.NET tests.** The tests live in `src/AnalyticsEngine/Tests.UnitTests`. Build
the project, then run a filtered subset with `vstest.console.exe` from a
Developer PowerShell for Visual Studio (or use Test Explorer):

```powershell
msbuild src\AnalyticsEngine\Tests.UnitTests\Tests.UnitTests.csproj /restore /p:Configuration=Debug
vstest.console.exe src\AnalyticsEngine\Tests.UnitTests\bin\Debug\Tests.UnitTests.dll /TestCaseFilter:"FullyQualifiedName~UserImportCheckpoint|FullyQualifiedName~GlobalFilter"
```

* **LocalDB.** Most SQL tests use the shared `UnitTestingSPOInsights` database on
  `(localdb)\MSSQLLocalDB`. A Debug build creates it and applies every migration
  the first time a test opens it. Some tests create, and then drop, a database
  of their own. Don't run two test runs against the shared database at the same
  time: they interfere with each other. If its schema gets out of step with the
  code, drop `UnitTestingSPOInsights` and the next run rebuilds it.
* **Inconclusive means "not run here", not "passed"** (`vstest` reports it as
  skipped). Tests go Inconclusive when something they need isn't configured:

  | Missing | Inconclusive tests (examples) |
  |---|---|
  | Azurite listening on `127.0.0.1:10002` | `StateStoreAzuriteTests`, `DlpHistoricalRepairTests` |
  | Azure AI Language (`CognitiveEndpoint` / `CognitiveKey`) | the sentiment tests in `TeamsTests`, `GraphImportTests` and `AppInsightsImportTests` |
  | An opt-in environment variable, such as `UPN_LOOKUP_BENCHMARK=1` or `LICENCE_ACTIVITY_PERF=1` | benchmarks, performance, stress and synthetic-scale tests; CI never sets them |
  | PowerShell 7 (`pwsh.exe`) | `CopilotAdoptionWorkbookComparisonScriptTests` |

  CI sets `REQUIRE_AZURITE=true`, so a missing Azurite fails the Azurite tests
  there instead.
* **Two Debug-only test groups fail without real resources.** They're compiled
  only in Debug builds, so CI never runs them: `GraphImportTests.MessageImportTests`
  calls Microsoft Graph with the runtime credentials, and three of the
  `VNetIntegrationTests` read an installer configuration file
  (`InstallTests\TestConfigs\InstallerTestConfig.json`) that you provide. Without
  a development tenant, leave them out:
  `/TestCaseFilter:"FullyQualifiedName!~VNetIntegrationTests&FullyQualifiedName!~MessageImportTests"`.

**Portal tests.** In `src/AnalyticsEngine/Web/Scripts/portal`, as the
`test_portal` CI job does:

```powershell
npm ci
npm run lint              # type-checks the app; fails on a missing Spanish string
npm run typecheck:tests   # type-checks the tests
npm test                  # every vitest test, the translation checks included
npx vitest run src/i18n   # just the translation checks
```

**SharePoint tracker tests.** In `src/SPO/AITracker/TypeScript`, run `npm ci`
and `npm test` (jest). The SPFx extension in `src/SPO/ModernPagesAITrackerExtension`
needs Node 22: `npm ci`, then `npm run build`, which runs its jest tests and
builds the package. Then run `./.github/scripts/Test-SpfxPackage.ps1` from the
repository root, which CI also runs. See the
[extension README](src/SPO/ModernPagesAITrackerExtension/README.md).

### Running the portal or an importer locally

`npm run dev` in `Web/Scripts/portal` serves the portal on its own, at
`http://localhost:5173`, but without the ASP.NET site behind it every API call
fails. To run the real site or an importer from Visual Studio, start `Web` or
`WebJob.Office365ActivityImporter`, with a development tenant's runtime app
registration in its Debug config. The site runs at `https://localhost:44307/`
(`WebAppURL` in `Web.Debug.config`), so that registration needs it as a reply
URL, and your account needs the portal's app roles to see the Administration
area: see the wiki's
[Runtime App Reply URLs](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Runtime%20App%20Reply%20URLs)
and
[Portal Permissions](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Portal%20Permissions).

## Coding conventions

* Match the style of the surrounding code.
* For C# work under `src/AnalyticsEngine/`, follow the conventions documented in
  [`src/AnalyticsEngine/.github/copilot-instructions.md`](src/AnalyticsEngine/.github/copilot-instructions.md)
  (for example database/column choices, NuGet and binding-redirect handling, and
  EF migration rules).
* Any text that can hold customer data (URLs, file names, user / display names,
  free text) must support the full Unicode range — use `nvarchar`, never
  `varchar`, in SQL and EF.
* Keep performance in mind: the solution is expected to run against large
  tenants (~200,000 users), so avoid patterns that scan or allocate per row.

## Rules your pull request is checked against

CI or review enforces each of these. The linked instruction files have the
detail; read the one for the area you're changing before you start.

* **No real customer or tenant data, anywhere**: code, tests, commit messages,
  pull requests, issues, screenshots and documentation. Use `Contoso`,
  `00000000-0000-0000-0000-000000000000` and made-up numbers. This repository is
  public, and a pushed commit stays reachable even after a force-push.
  ([Repository instructions](.github/copilot-instructions.md#sensitive--customer-data-handling-read-first))
* **Portal text ships in English and Spanish.** Every string a user can read goes
  in `src/i18n/catalog/en/<area>.ts` **and** `src/i18n/catalog/es/<area>.ts`, and
  is rendered with `t('<key>')`. In `src/AnalyticsEngine/Web/Scripts/portal`,
  `npm run lint` fails on a missing Spanish key and `npx vitest run src/i18n`
  fails on text that never reached the catalog. CI runs both, and either failure
  blocks the merge. When you reword English, re-read its Spanish.
  ([Portal README: Languages](src/AnalyticsEngine/Web/Scripts/portal/README.md#languages))
* **Every SharePoint tracker change bumps a version.** A change to `AITracker.js`
  bumps `AI_TRACKER_VER` in `src/SPO/AITracker/TypeScript/src/AiTrackerConstants.ts`.
  A change to the SPFx extension bumps `version` in its
  `config/package-solution.json` **and** `AITRACKER_MODERN_VERSION` in
  `AiTrackerModernApplicationCustomizer.ts` to the same value, and commits the
  rebuilt package, because CI doesn't build it.
  ([SharePoint instructions](src/SPO/.github/copilot-instructions.md#bump-the-version-with-every-change))
* **A new external API needs proof that it exists.** Before coding against a new
  Microsoft Graph function, entity or property, find its exact name in Graph's
  `$metadata` (`https://graph.microsoft.com/v1.0/$metadata` or
  `https://graph.microsoft.com/beta/$metadata`) or on its Microsoft Learn
  reference page, and cite it in the pull request description. Cite the official
  reference for any other external API the same way. Tests with made-up fixtures
  can't prove an endpoint exists.
  ([Repository instructions](.github/copilot-instructions.md#external-apis-prove-they-exist-before-coding-against-them))
* **A database migration ships with its manual upgrade script.** Add
  `<migrationid>.manual.sql` next to the migration, and work through the
  migration checklist in the [pull request template](.github/pull_request_template.md).
  A schema change made for performance also needs a measured before/after
  benchmark.
  ([AnalyticsEngine instructions](src/AnalyticsEngine/.github/copilot-instructions.md#writing-robust-schema-change-migrations-raw-sql-alter-column-create-index-backfills))
* **Link issues with `Addresses #123`, not `Fixes #123`.** `main` is the default
  branch, so a closing keyword in a pull request to `dev` does nothing; the
  release pull request to `main` closes the issue when the fix reaches a release.

## Pull request process

1. **Fork** the repository and create a feature branch.
2. Base your work on, and open your pull request against, the **`dev`** branch
   (not `main`) unless a maintainer asks otherwise.
3. Make sure the solution builds and the tests for the area you changed pass
   (see [Running the tests](#running-the-tests)).
4. Write a clear PR description and **link the issue(s)** it addresses
   (for example `Addresses #123`; see the rules above).
5. Be responsive to review feedback — maintainers may request changes before
   merging.

## Licensing and contributor agreement

This project is licensed under the [MIT License](LICENSE). By submitting a
contribution, you agree that your contribution is licensed under the same terms
and that you have the right to submit it.

Should this project join the [.NET Foundation](https://dotnetfoundation.org/),
contributors may be asked to sign the .NET Foundation
[Contributor License Agreement (CLA)](https://cla.dotnetfoundation.org/); the CLA
bot would then guide you through this automatically on your first pull request.

Thank you for helping make Microsoft 365 Advanced Analytics better!
