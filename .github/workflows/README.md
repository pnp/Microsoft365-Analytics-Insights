# Github actions

All three workflows below build the **AnalyticsEngine** solution (`src/AnalyticsEngine`), so they
are scoped to that path plus `reports/**` and their own workflow file. Changes elsewhere under
`src/` — for example `src/TelemetryService` — do not trigger them.

## ci

* Build and release when pushes to `main` or `dev`.

## pr

* Build when PRs a ready for review.
* Does not sign the executable.

## tests

* Run tests on pushes to `main`, `dev` and PRs ready for review.
* Only runs if code under `src/AnalyticsEngine` has changed (can by bypassed manually).
