# Github actions

`ci`, `pr` and `tests` all build the **AnalyticsEngine** solution (`src/AnalyticsEngine`), so they
are scoped to that path plus `reports/**` and their own workflow file. Changes elsewhere under
`src/` — for example `src/TelemetryService` — do not trigger them; that has its own workflow below.

## ci

* Build and release when pushes to `main` or `dev`.

## pr

* Build when PRs a ready for review.
* Does not sign the executable.

## tests

* Run tests on pushes to `main`, `dev` and PRs ready for review.
* Only runs if code under `src/AnalyticsEngine` has changed (can by bypassed manually).

## telemetry-service

* Builds, tests and deploys the maintainer-side telemetry dashboard (`src/TelemetryService`).
* Separate from the workflows above because it is a .NET 10 Linux web app deployed straight to
  App Service, not a .NET Framework build published as a GitHub release.
* Only runs when `src/TelemetryService/**` (or the shared `UsageReporting` project) changes.
* Deploys on pushes to `main` and `dev`; never from a pull request or a fork.
* Requires the repository secrets/variables listed in
  [`src/TelemetryService/README.md`](../../src/TelemetryService/README.md#continuous-delivery).
