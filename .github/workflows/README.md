# Github actions

`ci` and `pr` use the same reusable `build-packages.yml` workflow. Their triggers include the
AnalyticsEngine, SharePoint tracker, reports, Node pin and shared build/CI implementation.
`tests` always starts for a non-draft pull request so required checks cannot disappear. Its
workload filters live in [`.github/ci-paths.json`](../ci-paths.json), with regression tests for
workflow-only, toolchain-only and workload-specific changes. Telemetry-service source has its
own workflow; changing only that source does not build AnalyticsEngine.

## Node.js version

No workflow hardcodes a Node version. Every job that runs `npm` — directly, or indirectly via
MSBuild building the portal SPA — uses `actions/setup-node` with `node-version-file: .nvmrc`, so
the repo-root [`.nvmrc`](../../.nvmrc) is the single place the Node major is set for CI and for
developer machines alike. See the *Node.js version* section of
[`CONTRIBUTING.md`](../../CONTRIBUTING.md).

Note that .NET build jobs also need Node:
`Web.csproj` shells out to `npm ci` and `npm run build` for the portal, and its
`CheckNodeVersion` guard fails the build if the Node major does not match the pin.

## ci

* Runs on relevant pushes to `main`, or on demand on a selected branch.
* Calls the complete test workflow with `force_run=true`, testing the exact release SHA.
* Builds/signs with `build-packages.yml`; publication waits for both tests and packages.
* Validates all five ZIPs, including portal assets and singleton WebJob settings.
* Compares against the last published stable tag and exports matching migration manual SQL
  and changed `repair_*.sql` directly from git blobs, preserving LF and Unicode bytes.
* Attaches those scripts and a `release-manifest.json` recording the commit, baseline and SHA-256
  of every ZIP/script. Missing manual SQL for a new migration or an unresolved stable baseline
  fails the release; historical migrations without a manual script do not block documentation edits.
* Always creates a draft first, downloads every attached asset and verifies the checksums.
  Only then does `PUBLISH_RELEASES=true` publish it. Otherwise it remains a verified draft.
* Generated notes remain a starting point: the release manager still writes admin-friendly notes,
  states migration run order, retires superseded releases and forward-ports stable to `net10`.
* Release runs are serialized per branch and are never cancelled in progress.

## pr

* Builds non-draft PRs and subsequent commits, or runs on demand.
* Calls the same packaging workflow as releases, without signing.
* Cancels obsolete builds for the same PR.

## Release packages

`ci` and `pr` both zip each .NET package with
[`.github/scripts/Build-ReleasePackage.ps1`](../scripts/Build-ReleasePackage.ps1) rather than a bare
`Compress-Archive`, so a pull request build is packaged exactly like the release it would become. The
script:

* **prunes** what a package never uses: satellite resource assemblies for languages the product
  doesn't ship (it keeps `es`), third-party `.pdb` files (our own stay, for stack-trace line numbers),
  and `Microsoft.Data.SqlClient`'s ARM64 native library from the three packages that run on App
  Service, which has no ARM64 Windows workers;
* **fails the job** if a package carries a dependency it must never ship - the Cosmos DB SDK anywhere,
  installer-only assemblies (`Azure.ResourceManager.*`, `BouncyCastle`, the installer engine) outside
  `ControlPanelApp.zip`, or the Graph SDK and importer engines in `Website.zip` - or is missing its
  entry point;
* writes each package's size to the job summary.

The dependency rules exist because every package references `Common/DataUtils` and
`Common/Entities`: a package reference added there ships in all four zips. If the check fails, move
the reference to the project that actually needs it rather than relaxing the rule.

## SPFx package

CI never builds the SharePoint Framework extension (`src/SPO/ModernPagesAITrackerExtension`). The
release zips the package committed at `src/SPO/AITracker/spoinsights-modern-ui-aitracker.sppkg` into
`AITrackerInstaller.zip`, and the installer deploys it as it is. So a change to the extension that wasn't
rebuilt and committed would ship the old package.
[`.github/scripts/Test-SpfxPackage.ps1`](../scripts/Test-SpfxPackage.ps1) fails the job unless these all
carry the same version:

* `package-solution.json`;
* `AITRACKER_MODERN_VERSION`;
* the committed package's `AppManifest.xml`;
* the bundle inside that package.

It runs in two places:

* in `tests`' required `test_aitracker` check, when the extension, the package or the script changes;
* in the shared packaging workflow, before either a PR or release tracker ZIP is built.

If it fails, rebuild the extension and commit the package, as
[`src/SPO/.github/copilot-instructions.md`](../../src/SPO/.github/copilot-instructions.md) describes.

## tests

* Runs on every non-draft PR, on demand, and as a reusable workflow called by releases.
  There is no duplicate push-to-main test run.
* Builds .NET test binaries once, then runs four exhaustive, disjoint VSTest filters on
  separate Windows runners: demo, migrations/upgrades, reports and everything else.
  New tests automatically join a shard; no category or test is dropped.
* Each runner has its own LocalDB instance and Azurite. Separate catalogs on one shared SQL
  instance would not isolate the global staging tables.
* Downloadable test binaries contain synthetic configuration only. Credentials are substituted
  into the executing runner's config after download, and that config is never re-uploaded.
* Runs portal production/test type checks and the complete Vitest suite independently on Linux.
  The i18n suite is included. `npm run typecheck:tests` uses `tsconfig.tests.json`, because
  production TypeScript excludes test files and Vitest alone does not type-check them.
* Uploads per-shard TRX and portal JUnit results, with slow .NET tests in each job summary.
* Preserves required checks `test_dotnet (Release)` and `test_aitracker`. The former is a final
  gate that fails if any applicable build, shard, portal or CI-helper check fails or is cancelled.
* Cancels superseded PR runs; jobs have explicit timeouts. CI helpers and actionlint run without
  tenant credentials. NuGet caches are keyed on dependency manifests, not build outputs.

All workflow actions are pinned to immutable commits. Dependabot proposes action updates
against `dev` weekly. To validate workflow changes locally:

```powershell
node --test .github\scripts\*.test.mjs
actionlint -shellcheck=''
```

## telemetry-service

* Builds, tests and deploys the maintainer-side telemetry dashboard (`src/TelemetryService`).
* Separate from the workflows above because it is a .NET 10 Linux web app deployed straight to
  App Service, not a .NET Framework build published as a GitHub release.
* Only runs when `src/TelemetryService/**` (or the shared `UsageReporting` project) changes.
* Deploys relevant pushes to `main` to production; never from a pull request or a fork.
* Manual runs default to `deployment=none` (build/test only). `development` is permitted only
  from `dev`, `production` only from `main`. Development requires a distinct configured App
  Service and never falls back to production.
* Uses `telemetry-service` for production and `telemetry-service-dev` for development, with
  destination-specific deployment concurrency. Configure environment reviewers/branch protection
  in GitHub settings as appropriate; workflow files do not create those policies or Azure resources.
* Requires the repository secrets/variables listed in
  [`src/TelemetryService/README.md`](../../src/TelemetryService/README.md#continuous-delivery).

## infra-drift

* Weekly (and on demand) check that the deployed telemetry-service resource group still matches
  `infra/TelemetryService/resources.bicep`, via read-only `az deployment group what-if`.
* Deploys nothing and can change nothing — it only reports.
* On a pull request only the filter's own unit tests run; the Azure job is skipped, so a PR never
  needs Azure access.
* Skips with a notice when the environment variables are not configured, so forks stay green.
* See [`infra/TelemetryService/drift/README.md`](../../infra/TelemetryService/drift/README.md).
