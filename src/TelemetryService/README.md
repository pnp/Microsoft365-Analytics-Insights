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
- Exposes a small read API + React dashboard that surface aggregate stats
  across all reporting installations.

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
| `GET`  | `/api/Telemetry/stats` | Aggregated headline figures (client count, total rows / size, last update, per-table totals). | Anonymous (intentional — public dashboard data). |
| `GET`  | `/api/Telemetry/clients` | Per-client summary rows for the dashboard table. | Anonymous (intentional — public dashboard data). |

### Auth model

- **Uploads** (`POST /api/Telemetry`) are gated by a shared-secret BCrypt
  signature: the importer hashes `<TelemetrySecret> + StatsModel.Generated.Ticks`
  and the receiver re-verifies it. Both sides must hold the same secret
  (`TelemetrySecret` here, `StatsApiSecret` on the importer).
- **Dashboard reads** are anonymous by design. The payloads are anonymised
  aggregates (no tenant IDs, no PII) and the dashboard is intended to be a
  public-style overview. To avoid hammering Cosmos under load the read path
  is cached in-process (see `DashboardCacheSeconds` below).

## Configuration

| Key | Required | Description |
| --- | --- | --- |
| `TelemetrySecret` | yes | Shared secret used to verify the BCrypt signature on incoming payloads. Must match the importer-side `StatsApiSecret`. |
| `CosmosDb:AccountEndpoint` | yes | Cosmos account URL, e.g. `https://myaccount.documents.azure.com:443/`. The account uses AAD (key auth disabled) — `DefaultAzureCredential` is used. |
| `CosmosDb:DatabaseName` | yes | Cosmos database to use (created on startup if missing). |
| `CosmosDb:ContainerNameCurrent` | yes | Container for the latest record per client. |
| `CosmosDb:ContainerNameHistory` | yes | Container for the append-only history. |
| `AZURE_TENANT_ID` | optional | Set when your default Azure tenant differs from the Cosmos account's home tenant. |
| `MaxDashboardItems` | optional | Cap on the number of client records the dashboard endpoints will pull in one request. Defaults to `5000`. |
| `DashboardCacheSeconds` | optional | How long the dashboard endpoints cache the Cosmos read for. Defaults to `60`. Set to `0` to disable caching (useful for local debugging). |

Local development: use `dotnet user-secrets` so secrets stay out of source
control. Example:

```pwsh
dotnet user-secrets set "TelemetrySecret" "<your-secret>" --project Web.Server
dotnet user-secrets set "CosmosDb:AccountEndpoint" "https://myaccount.documents.azure.com:443/" --project Web.Server
dotnet user-secrets set "CosmosDb:DatabaseName" "Telemetry" --project Web.Server
dotnet user-secrets set "CosmosDb:ContainerNameCurrent" "Current" --project Web.Server
dotnet user-secrets set "CosmosDb:ContainerNameHistory" "History" --project Web.Server
```

> The service calls `CreateDatabaseIfNotExistsAsync` + `CreateContainerIfNotExistsAsync`
> on startup, so the configured database / containers are created automatically
> the first time the app runs against a fresh Cosmos account.

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
