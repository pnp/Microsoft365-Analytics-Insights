# Github actions

## ci

* Build and release when pushes to `main` or `dev`.

## pr

* Build when PRs a ready for review.
* Does not sign the executable.

## tests

* Run tests on pushes to `main`, `dev` and PRs ready for review.
* Only runs if code under `src` has changed (can by bypassed manually).
