# Local configuration when debugging in Visual Studio

> **This is the `net10` branch.** The solution has no `App.config` / `Web.config` files at all. If you
> are looking for the XML templates this folder used to hold, they are on `main` - the .NET Framework
> build still uses them.

Configuration now comes from, in increasing order of precedence:

1. **`appsettings.json`** next to each executable - checked in, and deliberately holds only
   non-secret defaults (which workloads to import, chunk size, and so on).
2. **`appsettings.{Environment}.json`** - optional.
3. **User secrets** - Development only. This is where your own credentials go.
4. **Environment variables** - how Azure App Service supplies configuration in a real deployment,
   and the highest precedence so a deployment always wins.

## Putting your credentials in user secrets

There is **one** secrets store for the whole solution, `pnp-m365-analytics-insights`. It is declared
as `<UserSecretsId>` on `Common/Entities/Entities.csproj` **and repeated on every runnable project**
(the web app, both web jobs, the installer, and both test projects), so it does not matter which one
you use — Visual Studio's *Manage User Secrets* and `dotnet user-secrets` both open the same file:

```pwsh
cd src/AnalyticsEngine/Common/Entities   # ...or Web, or either WebJob - they all share one store

dotnet user-secrets set "ClientID"     "<your app registration's client id>"
dotnet user-secrets set "ClientSecret" "<your app registration's secret>"
dotnet user-secrets set "TenantGUID"   "<your tenant id>"
dotnet user-secrets set "TenantDomain" "<yourtenant>.onmicrosoft.com"

# Connection strings go under the ConnectionStrings section:
dotnet user-secrets set "ConnectionStrings:SPOInsightsEntities" "<your SQL connection string>"
dotnet user-secrets set "ConnectionStrings:Storage"             "<your storage connection string>"
```

> **Do not let a project acquire its own secrets id.** `AnalyticsConfig` reads the shared store *by
> id*, so a project pointing at a different one has its secrets silently ignored — and it looks like
> it works, because `WebApplication.CreateBuilder` loads the web project's own store into
> `builder.Configuration`, which nothing in this solution reads. The symptom is a blank `ClientID` or
> `TenantDomain` at startup, far from the cause. If you ever see Visual Studio add a fresh GUID
> `<UserSecretsId>` to a `.csproj`, change it back to `pnp-m365-analytics-insights`.
> `UserSecretsConsistencyTests` fails the build if one slips through.

The store lives in your user profile (`%APPDATA%\Microsoft\UserSecrets\pnp-m365-analytics-insights\`),
outside the repository, so there is no longer a gitignored file in the working tree that a careless
`git add -f` could publish.

**User secrets are only read when the environment is Development**, so set `DOTNET_ENVIRONMENT`
(web jobs, installer, tests) or `ASPNETCORE_ENVIRONMENT` (the web app) to `Development` when you
debug. Visual Studio's launch profiles do this for you.

## Environment variables instead

Anything above can be supplied as an environment variable, which is useful in CI and for one-off
runs. App settings use their plain name; connection strings use the standard `__` section separator:

```pwsh
$env:ClientID = "..."
$env:ConnectionStrings__SPOInsightsEntities = "..."
```

## How this maps to a real Azure deployment

The installer writes the solution's connection strings to App Service as *connection strings* (typed
`SQLAzure` for `SPOInsightsEntities`, `Custom` for the rest), and App Service injects those into the
process prefixed `SQLAZURECONNSTR_` / `CUSTOMCONNSTR_`. `AddEnvironmentVariables()` folds those
prefixes back under `ConnectionStrings:`, so deployed code reads exactly the same key you set
locally. `Tests.UnitTests/ConfigurationSourceTests.cs` proves this rather than assuming it.
