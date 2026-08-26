# Telemetry Service

ASP.NET Core 10 + React/Vite web app that acts as the **receiver endpoint**
for anonymous usage telemetry sent by Microsoft 365 Analytics Insights
installations (the AnalyticsEngine importer in
`src/AnalyticsEngine/WebJob.Office365ActivityImporter`).

> This is a **separate solution** from `AnalyticsEngine`. It only depends on
> the shared `Common/UsageReporting` netstandard2.0 project (data contracts
> + Cosmos save adaptor) so the wire format stays in sync.

## What it does

- Receives `TelemetryPayload` POSTs from instances of
  [`WebApiStatsUploader`](../AnalyticsEngine/WebJob.Office365ActivityImporter.Engine/StatsUploader/WebApiStatsUploader.cs).
- Validates the BCrypt-signed payload against a shared `TelemetrySecret`.
- Upserts the latest report into a Cosmos DB "current" container and appends
  a per-update row to a "history" container (see
  [`CosmosTelemetrySaveAdaptor`](../AnalyticsEngine/Common/UsageReporting/CosmosTelemetrySaveAdaptor.cs)).
- Exposes an Entra-protected read API + React dashboard that surfaces
  aggregate stats across all reporting installations.

## Projects

| Project | What it is |
| --- | --- |
| `Web.Server/` | ASP.NET Core 10 host. Exposes `/api/Telemetry`. Serves the built SPA in production and proxies to Vite in development. |
| `web.client/` | React 19 + Vite SPA. Renders a minimal dashboard from the read API. |
| `../AnalyticsEngine/Common/UsageReporting/` | Shared (netstandard2.0) data contracts + Cosmos adaptor. Also referenced by the importer. |

## Endpoints

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| `POST` | `/api/Telemetry` | Receive a `TelemetryPayload` from an importer instance. | BCrypt signature against `TelemetrySecret` (see [`AnonUsageStatsModel.IsValidSecretForThisObject`](../AnalyticsEngine/Common/UsageReporting/AnonUsageStatsModel.cs)). |
| `GET`  | `/api/auth/config` | Public MSAL configuration (authority, client ID and API scope; no secrets). | Anonymous. |
| `GET`  | `/api/Telemetry/stats` | Aggregated headline figures (client count, total rows / size, last update, per-table totals, per-schema totals, build adoption, import-feature adoption, reporting freshness and deployment-size distribution). | Entra token with `Telemetry.Read` scope and `Telemetry.Dashboard.Read` role. |
| `GET`  | `/api/Telemetry/clients` | Per-client summary rows for the dashboard table. | Entra token with `Telemetry.Read` scope and `Telemetry.Dashboard.Read` role. |

### Auth model

- **Uploads** (`POST /api/Telemetry`) are gated by a shared-secret BCrypt
  signature: the importer hashes `<TelemetrySecret> + StatsModel.Generated.Ticks`
  and the receiver re-verifies it. Both sides must hold the same secret
  (`TelemetrySecret` here, `StatsApiSecret` on the importer).
- **Dashboard reads** require a delegated Entra access token. The API checks
  both the `Telemetry.Read` scope and the `Telemetry.Dashboard.Read` app role,
  so only explicitly assigned users or groups can view the reports. The SPA
  uses MSAL and stores tokens in session storage.

  Both checks live in a **single authorization policy**
  (`DashboardAuthorization.PolicyName`), not in separate `[Authorize(Roles = …)]`
  and `[RequiredScope(…)]` attributes. That attribute pair reads as if it
  enforces both, but only the role is applied: `RequiredScopeAttribute` is
  endpoint *metadata*, and the handler that evaluates it is reached only through
  the default authorization policy — which `[Authorize(Roles = …)]` replaces
  rather than extends. The integration tests assert the scope is genuinely
  enforced.
- **App Service Authentication** runs in allow-anonymous mode so it validates
  bearer tokens and emits MISE key-discovery telemetry without becoming the
  authorization boundary. The ASP.NET Core API still performs the scope and
  role checks, while anonymous health, configuration and signed-upload
  requests continue to reach the application.
- **EasyAuth `allowedApplications` must be set explicitly.** If
  `defaultAuthorizationPolicy` is omitted, the platform stores
  `allowed_client_applications` as an *empty array*, which EasyAuth reads as
  "permit nothing": it authenticates the caller and then returns a bare `403`
  before the request reaches the application. The symptom is deeply misleading —
  anonymous requests still succeed, and a token with an invalid signature also
  succeeds (it fails validation, so it is treated as anonymous), so a **valid**
  token is the only thing that gets rejected. Verify with
  `az webapp auth show` that `aadClaimsAuthorization` lists the SPA client ID
  rather than `"allowed_client_applications":[]`.
- **Restart the App Service after changing `authsettingsV2`.** The EasyAuth /
  MISE runtime caches the auth configuration, so a corrected
  `allowedApplications` is not honoured until the site restarts. Until then the
  config APIs report the new value while requests are still rejected using the
  old one, which makes the fix look like it did not work.

### Application Insights

Telemetry is emitted **in-process** by
[`Azure.Monitor.OpenTelemetry.AspNetCore`](https://www.nuget.org/packages/Azure.Monitor.OpenTelemetry.AspNetCore),
wired up in `Program.cs` via `AddOpenTelemetry().UseAzureMonitor()`. It reads
`APPLICATIONINSIGHTS_CONNECTION_STRING` and no-ops when that is unset, so local
development sends nothing.

Do **not** rely on the `ApplicationInsightsAgent_EXTENSION_VERSION` codeless
attach setting. It applies only to **Windows** App Service, and this service
runs on **Linux** (`DOTNETCORE|10.0`), so setting it there is silently
ineffective for .NET while making the site look instrumented: the connection
string is present and the ingestion endpoint is reachable, yet nothing is ever
sent. If the Application Insights resource is empty, check that this package is
actually referenced before investigating networking.

## Dashboard

The SPA uses [Fluent UI v9](https://react.fluentui.dev/) and mirrors the look and
feel of the in-product portal
(`src/AnalyticsEngine/Web/Scripts/portal`): brand header, `TabList`
navigation and the same content width. Content is split across four tabs, each
lazy-loaded as its own chunk:

| Tab | Contents |
| --- | --- |
| Overview | Headline figures, reporting freshness, deployment-size distribution (median/average/largest), Azure AI usage, storage by schema and the top tables. |
| Tables | Every table aggregated across clients, filterable and sortable, including average rows per client. |
| Clients | One row per installation, with relative "last report" times, stale highlighting and the enabled imports per client. |
| Adoption | Build-version adoption and per-import feature adoption across the install base. |

All figures are derived server-side in `DashboardService.Aggregate` from the
telemetry each client already sends — no client-side change is needed to
populate them. Two derived values are worth knowing about:

- **Build adoption** groups clients that never sent a `BuildVersionLabel` under
  `(unknown)` rather than dropping them, so the counts always total the client
  count.
- **Import feature adoption** parses each client's
  `ConfiguredImportsEnabledDescription` (a `Name=True;Name=False` string) and
  reports percentages **of the clients reporting that toggle**, not of all
  clients — otherwise a newly added import would look widely disabled simply
  because older builds do not mention it.

## Tests

| Project | Runner | Command |
| --- | --- | --- |
| `Tests.Unit` | MSTest (net10.0) | `dotnet test src/TelemetryService/Tests.Unit/Tests.Unit.csproj` |
| `web.client` | Vitest + Testing Library | `npm test` (from `src/TelemetryService/web.client`) |

The server tests cover the dashboard aggregation (freshness bucketing, build and feature
adoption, schema/table keying, medians), the settings-string parser, the upload endpoint's
signature checks, the save/merge behaviour and configuration binding. `Aggregate` takes an
injectable `nowUtc` so the freshness buckets are deterministic rather than dependent on when
the suite runs, and the internal helpers are exposed to the test project via `InternalsVisibleTo`.

The client tests cover the formatting helpers and the sortable table component. Fluent UI is
inlined in `vitest.config.ts` (`server.deps.inline`) because `@fluentui/react-icons` ships
extensionless ESM chunk imports that Vite's node resolver cannot follow.

### Integration tests

`IntegrationTests.cs` hosts the real application with `WebApplicationFactory` (see
`TelemetryWebAppFactory.cs`), so routing, the authorisation pipeline, model binding and the DI
graph are exercised as they run in Azure. The Cosmos adaptors are replaced with the in-memory
`FakeTelemetryStore` and JWT bearer validation with a header-driven test scheme, so the tests need
no Azure resources and no real tokens; they run in the ordinary `dotnet test` pass.

They assert that `/health` and `/api/auth/config` stay anonymous, that the dashboard endpoints
return `401` unauthenticated and `403` for a token missing either the role or the scope, and that
the signed upload endpoint accepts a correct signature and rejects a wrong one.

Configuration is supplied through **environment variables** rather than
`ConfigureAppConfiguration`. `Program.cs` reads `builder.Configuration` while registering services,
which happens inside `Main` — before `WebApplicationFactory` applies its configuration callbacks at
`Build()`. Environment variables are read by `WebApplication.CreateBuilder` itself, so they are the
only injection point early enough.

## Continuous delivery

[`.github/workflows/telemetry-service.yml`](../../.github/workflows/telemetry-service.yml)
builds, lints and tests both halves on every pull request, and deploys on pushes to `main` and
`dev`. `dotnet publish` also builds the Vite client into `wwwroot`, so a single artifact is the
whole deployable site. After deploying, the workflow polls `/health` so a build that deploys but
fails to start is reported as a failed deployment rather than a successful one.

Azure sign-in uses **OIDC federated credentials**, so no client secret is stored. Configure the
app registration with a federated credential for this repository and the `telemetry-service`
environment, and grant it Website Contributor (or Contributor) on the target App Service.

| Kind | Name | Purpose |
| --- | --- | --- |
| Secret | `AZURE_CLIENT_ID` | App registration used for the federated deployment credential. |
| Secret | `AZURE_TENANT_ID` | Tenant of that app registration. |
| Secret | `AZURE_SUBSCRIPTION_ID` | Subscription containing the App Service. |
| Variable | `TELEMETRY_APP_NAME` | App Service name to deploy to. |
| Variable | `TELEMETRY_RESOURCE_GROUP` | Resource group containing that App Service. |
| Variable | `TELEMETRY_INFRA_PARAMETERS` | Non-secret Bicep parameters as a JSON object, used by the [drift check](../../infra/TelemetryService/drift/README.md#configuration). Only needed for `infra-drift.yml`. |

The deploy job is guarded so it can only ever run from this repository on a push — never from a
pull request and never from a fork.

Configuration drift between these deployed resources and `infra/TelemetryService/resources.bicep` is
detected weekly by [`.github/workflows/infra-drift.yml`](../../.github/workflows/infra-drift.yml),
which runs a read-only `az deployment group what-if` and deploys nothing. See
[`infra/TelemetryService/drift/README.md`](../../infra/TelemetryService/drift/README.md).

## Configuration

| Key | Required | Description |
| --- | --- | --- |
| `TelemetrySecret` | yes | Shared secret used to verify the BCrypt signature on incoming payloads. Must match the importer-side `StatsApiSecret`. |
| `AzureAd:Instance` | yes | Entra authority base URL. Use `https://login.microsoftonline.com/`. |
| `AzureAd:TenantId` | yes | Tenant containing the dashboard app registration. |
| `AzureAd:ClientId` | yes | Client ID of the single-tenant SPA/API app registration. |
| `AzureAd:Scopes` | yes | Delegated scope name exposed by the app registration. Must be `Telemetry.Read`. |
| `WEBSITE_AAD_ENABLE_MISE` | Azure deployment | Enables the App Service MISE validation runtime. The Bicep deployment sets this to `true`. |
| `CosmosDb:AccountEndpoint` | yes | Cosmos account URL, e.g. `https://myaccount.documents.azure.com:443/`. The account uses AAD (key auth disabled) — `DefaultAzureCredential` is used. |
| `CosmosDb:DatabaseName` | yes | Cosmos database to use (created on startup if missing). |
| `CosmosDb:ContainerNameCurrent` | yes | Container for the latest record per client. |
| `CosmosDb:ContainerNameHistory` | yes | Container for the append-only history. |
| `AZURE_TENANT_ID` | optional | Set when your default Azure tenant differs from the Cosmos account's home tenant. |
| `MaxDashboardItems` | optional | Cap on the number of client records the dashboard endpoints will pull in one request. Defaults to `5000`. |
| `DashboardCacheSeconds` | optional | How long the dashboard endpoints cache the Cosmos read for. Defaults to `60`. Set to `0` to disable caching (useful for local debugging). |

### Entra app registration

Use one single-tenant app registration for the SPA and API:

1. Add SPA redirect URIs for the deployed service root and
   `https://localhost:7167` for local development.
2. Under **Expose an API**, keep the default Application ID URI
   `api://<client-id>` and add delegated scope `Telemetry.Read`.
3. Add an app role with value `Telemetry.Dashboard.Read`, allowed for
   users/groups.
4. In the Enterprise Application, assign only the users or groups that may
   view the dashboard to that role.
5. Grant tenant admin consent for the delegated scope.

The upload endpoint does not use Entra authentication because deployed
importers authenticate each payload with the shared signature instead.

Local development: use `dotnet user-secrets` so secrets stay out of source
control. Example:

```pwsh
dotnet user-secrets set "TelemetrySecret" "<your-secret>" --project Web.Server
dotnet user-secrets set "AzureAd:TenantId" "00000000-0000-0000-0000-000000000000" --project Web.Server
dotnet user-secrets set "AzureAd:ClientId" "00000000-0000-0000-0000-000000000000" --project Web.Server
dotnet user-secrets set "CosmosDb:AccountEndpoint" "https://myaccount.documents.azure.com:443/" --project Web.Server
dotnet user-secrets set "CosmosDb:DatabaseName" "Telemetry" --project Web.Server
dotnet user-secrets set "CosmosDb:ContainerNameCurrent" "Current" --project Web.Server
dotnet user-secrets set "CosmosDb:ContainerNameHistory" "History" --project Web.Server
```

> The service calls `CreateDatabaseIfNotExistsAsync` + `CreateContainerIfNotExistsAsync`
> on startup, so the configured database / containers are created automatically
> the first time the app runs against a fresh Cosmos account.

## Deploying to Azure

The deployment assets are in [`infra/TelemetryService/`](../../infra/TelemetryService):

| File | Purpose |
| --- | --- |
| `main.bicep` | Subscription-scope entry point; creates the resource group. |
| `resources.bicep` | App Service, serverless Cosmos DB, Key Vault, VNet, private endpoints/DNS, RBAC and monitoring. |
| `azuredeploy.json` | Compiled ARM template generated from the Bicep files. |
| `deploy.ps1` | Recommended deployment and redeployment entry point. It also configures Entra, builds/publishes the app and verifies the result. |

The script intentionally does **not** use or generate a committed environment
parameter file. Supply environment-specific values at runtime and keep them in
your approved secure backup system.

### Prerequisites

- PowerShell 7, Azure CLI, .NET 10 SDK and Node.js/npm.
- Azure CLI signed into the target tenant and subscription.
- Permission to create resources and role assignments in the subscription.
- Permission to create an Entra app registration and assign its app role.
- A globally unique App Service name.
- The upload signing secret. It must match `StatsApiSecret` in every importer
  that sends telemetry to this service.

On a managed Microsoft device, configure npm to use the approved package feed:

```pwsh
npm config set registry "https://packagefeedproxy.microsoft.io/npm/" --location=user
npm config set replace-registry-host npmjs --location=user
```

### Parameters to retain securely

Back up these values outside the public repository so the deployment can be
reproduced:

- subscription ID and tenant/domain;
- Azure region and resource-group name;
- App Service name and resource-name prefix;
- VNet and subnet CIDR prefixes;
- Entra app display name, if changed from the default;
- `TelemetrySecret` / importer `StatsApiSecret`.

Azure resource configuration is reproducible from Bicep. The signing secret is
stored in Key Vault after deployment, but the deployer still needs an approved
copy when rebuilding an environment from scratch.

### Preview the deployment

From the repository root, set the secret for the current PowerShell process:

```pwsh
$env:TELEMETRY_SERVICE_SECRET = "<existing-importer-StatsApiSecret>"
```

Run an ARM what-if before provisioning:

```pwsh
.\infra\TelemetryService\deploy.ps1 `
  -SubscriptionId "<subscription-id>" `
  -Tenant "<tenant-domain-or-id>" `
  -Location "<azure-region>" `
  -ResourceGroupName "<resource-group-name>" `
  -WebAppName "<globally-unique-app-service-name>" `
  -NamePrefix "<short-resource-prefix>" `
  -VnetAddressPrefix "<vnet-cidr>" `
  -AppIntegrationSubnetPrefix "<app-service-subnet-cidr>" `
  -PrivateEndpointSubnetPrefix "<private-endpoint-subnet-cidr>" `
  -WhatIf
```

The preview does not create the Entra application; it uses a synthetic client
ID only for ARM validation.

### Deploy or redeploy

Run the same command without `-WhatIf`:

```pwsh
.\infra\TelemetryService\deploy.ps1 `
  -SubscriptionId "<subscription-id>" `
  -Tenant "<tenant-domain-or-id>" `
  -Location "<azure-region>" `
  -ResourceGroupName "<resource-group-name>" `
  -WebAppName "<globally-unique-app-service-name>" `
  -NamePrefix "<short-resource-prefix>" `
  -VnetAddressPrefix "<vnet-cidr>" `
  -AppIntegrationSubnetPrefix "<app-service-subnet-cidr>" `
  -PrivateEndpointSubnetPrefix "<private-endpoint-subnet-cidr>"

Remove-Item Env:TELEMETRY_SERVICE_SECRET
```

The deployment is idempotent. Reusing the same values updates the existing
resources and republishes the current application. Use
`-SkipApplicationPublish` to update only infrastructure and Entra configuration.

The script:

1. checks the Azure context and App Service hostname;
2. registers required Azure resource providers;
3. creates or updates the single-tenant Entra SPA/API and assigns the current
   user the `Telemetry.Dashboard.Read` role;
4. deploys the ARM template, including non-enforcing App Service
   Authentication and the MISE runtime setting, using a temporary parameters
   file that is deleted afterward;
5. stores the signing secret in private Key Vault;
6. builds and ZIP-deploys the application using Entra authentication;
7. verifies health, application authorization, EasyAuth configuration and
   runtime version, Cosmos/Key Vault network isolation, private endpoints and
   the Key Vault reference.

Tenant admin consent might need to be granted manually after deployment. The
assigned dashboard user can otherwise be prompted for delegated
`Telemetry.Read` consent on first sign-in.

> Deploying `azuredeploy.json` directly provisions only Azure resources. Use
> `deploy.ps1` for the complete Entra configuration, secure secret handling,
> application publication and verification workflow.

### MISE compliance verification

The public repository intentionally keeps `Microsoft.Identity.Web` for its
portable, public NuGet restore path. App Service Authentication supplies the
MISE runtime and validates the same bearer tokens before the application
performs its existing scope and role authorization.

After deploying:

1. Sign in to the dashboard and load both protected API endpoints so token
   acquisition and key discovery occur together.
2. Confirm the deployment script sees EasyAuth intercept `/.auth/version`.
   Authenticated responses must report a runtime newer than `1.7.0`; Linux
   App Service can return HTTP 401 to anonymous version requests, so the script
   also verifies the ARM runtime selector is `~1`.
3. Allow 3–5 days for the compliance pipeline to report the new key-discovery
   telemetry.

Do not change EasyAuth to require authentication globally without preserving
the anonymous signed-upload, health and public authentication-configuration
paths. Deployed importers do not send Entra tokens to the upload endpoint.

## Importer side — pointing an installation at this service

The importer reads two values from its `App.config` / Azure app settings
(see [`Common/Entities/Config/AppConfig.cs`](../AnalyticsEngine/Common/Entities/Config/AppConfig.cs)):

| Importer key | Set to |
| --- | --- |
| `StatsApiUrl` | Full POST URL of this service, e.g. `https://telemetry.example.com/api/Telemetry`. |
| `StatsApiSecret` | The same value as this service's `TelemetrySecret`. |

When both are set, the importer uploads an anonymised
[`AnonUsageStatsModel`](../AnalyticsEngine/Common/UsageReporting/AnonUsageStatsModel.cs)
at the end of each successful import cycle. If either is empty, telemetry
uploading is silently skipped.

## Running locally

Visual Studio: open `TelemetryService.slnx`, set `Web.Server` as the startup
project, hit F5. The Vite dev server is launched automatically by
`Microsoft.AspNetCore.SpaProxy`.

From the CLI:

```pwsh
# Inside src/TelemetryService/Web.Server
dotnet run
```

Without a reachable Cosmos account the app will fail at startup (the
adaptor's `Init()` is awaited before the host starts). That's intentional —
the dashboard is only meaningful when backed by real data — so configure
user-secrets pointing at a dev Cosmos account before trying to run.
