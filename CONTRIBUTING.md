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
* **Node.js** (LTS) — required to build the admin web app
  (`src/AnalyticsEngine/Web/Scripts/admin-app`) and the SharePoint AI Tracker
  (`src/SPO/AITracker`).
* A SQL Server **LocalDB** instance for running the unit tests.

A full end-to-end run also needs Azure resources (Azure SQL, App Service, Redis,
and others). See the
[Architecture & costs](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Architecture%20and%20Costs)
and
[Prerequisites](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Prerequisites)
wiki pages for details.

### Building

Open `src/AnalyticsEngine/O365 Advanced Analytics Engine.sln` in Visual Studio
and build, or from a Developer command prompt:

```powershell
cd src\AnalyticsEngine
msbuild ".\O365 Advanced Analytics Engine.sln" /t:Restore
msbuild ".\O365 Advanced Analytics Engine.sln" /t:Build /p:Configuration=Debug
```

> Note: building the solution with `dotnet build` is not supported — use Visual
> Studio or MSBuild.

### Running the tests

Unit tests live in `src/AnalyticsEngine/Tests.UnitTests` and run against a local
`(localdb)\MSSQLLocalDB` database (created and migrated automatically in Debug
builds). Run them from Visual Studio Test Explorer or with
`vstest.console.exe`. The CI test workflow
(`.github/workflows/tests.yml`) runs on every pull request.

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

## Pull request process

1. **Fork** the repository and create a feature branch.
2. Base your work on, and open your pull request against, the **`dev`** branch
   (not `main`) unless a maintainer asks otherwise.
3. Make sure the solution builds and the unit tests pass.
4. Write a clear PR description and **link the issue(s)** it addresses
   (for example `Fixes #123`).
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
