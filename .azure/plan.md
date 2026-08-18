# Azure Deployment Plan

> **Status:** Template — no environment-specific values recorded here.

**Current change:** Enable the App Service MISE authentication runtime in
allow-anonymous mode so bearer-token validation emits compliant key-discovery
telemetry while the ASP.NET Core application continues enforcing dashboard
scope and role authorization.

---

## Public-repository rule for this file

This repository is public. This plan is a **synthetic template**. It must never
record real subscription, tenant, resource, application or user names or IDs,
regions, hostnames, URLs, CIDRs, deployment timestamps, resource counts,
capacity or policy failures, or production validation and deployment results.

Real deployment context, parameter values and run evidence are held
**out-of-band** and are deliberately omitted here. Use placeholders such as
`<subscription-id>`, `<tenant-id>`, `<region>`, `<app-service-name>` and
`<hostname>`, and record outcomes as generic shapes only.

---

## 1. Project Overview

**Goal:** Deploy the Telemetry Service to an isolated Azure resource group and
publish the ASP.NET Core/React application. Keep the public HTTPS receiver
reachable by external analytics-engine installations while making Cosmos DB and
secret storage private-only.

**Path:** Add Azure infrastructure and deployment automation to the existing
Telemetry Service.

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Proof of concept |
| Scale | Small deployment |
| Budget | Cost-optimized |
| Subscription | `<subscription-id>` — supplied out-of-band |
| Tenant | `<tenant-id>` — supplied out-of-band |
| Location | `<region>` — supplied out-of-band |
| Ingress | Public HTTPS App Service for signed uploads and Entra-authenticated dashboard access |
| Endpoint compatibility | Preserve the exact App Service hostname embedded in the released importer; the name is supplied out-of-band |
| Data access | Cosmos DB and Key Vault public network access disabled |
| Parameters | No environment-specific parameter file committed |
| Dashboard access | The deploying identity receives the initial `Telemetry.Dashboard.Read` assignment |
| Compliance | Single-region residency; no additional CMK or firewall requirement |

---

## 3. Components Detected

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| Telemetry API | API | ASP.NET Core 10 | `src/TelemetryService/Web.Server/` |
| Telemetry dashboard | Frontend | React 19, Vite, MSAL Browser | `src/TelemetryService/web.client/` |
| Telemetry contracts/store adaptor | Library | .NET Standard 2.0, Cosmos DB SDK | `src/AnalyticsEngine/Common/UsageReporting/` |
| Existing deployment | None | No TelemetryService deployment workflow or IaC | N/A |

The service already:

- validates signed anonymous uploads at `POST /api/Telemetry`;
- requires Entra scope and role authorization for dashboard reads;
- uses `DefaultAzureCredential` for Cosmos DB;
- creates/verifies its Cosmos database and containers on startup.

---

## 4. Recipe Selection

**Selected:** Direct Bicep/ARM deployment with an idempotent PowerShell orchestrator.

**Rationale:**

- ARM infrastructure is required in the repository.
- Bicep is the maintainable source; a compiled `azuredeploy.json` ARM template is also committed.
- Azure CLI/PowerShell is required for the Microsoft Graph app registration, initial app-role assignment, secure runtime parameters, and ZIP application publication.
- No AZD environment or environment parameter values are committed.

---

## 5. Architecture

**Stack:** Azure App Service

### Service Mapping

| Component | Azure Service | SKU / Configuration |
|-----------|---------------|---------------------|
| API + dashboard | Linux App Service | Basic B1, one instance, .NET 10, Always On |
| Hosting plan | App Service plan | Linux Basic B1 |
| Telemetry store | Azure Cosmos DB for NoSQL | Serverless |
| Secret storage | Azure Key Vault | Standard, RBAC, purge protection, private-only access |
| Network | Virtual Network | Dedicated App Service integration and private-endpoint subnets |
| Private access | Private endpoints | Cosmos DB SQL endpoint and Key Vault vault endpoint |
| Private DNS | Azure Private DNS | Cosmos DB and Key Vault private-link zones linked to the VNet |
| Authentication | Microsoft Entra ID | Single-tenant SPA/API app, `Telemetry.Read` scope, `Telemetry.Dashboard.Read` role |
| Monitoring | Application Insights | Workspace-based, low-volume configuration |
| Logs | Log Analytics workspace | 30-day retention |

### Identity and Access

- Enable a system-assigned managed identity on the App Service.
- Grant the identity Cosmos DB Built-in Data Contributor at the Cosmos account scope.
- Grant the identity Key Vault Secrets User at the vault scope.
- Disable Cosmos DB local/key authentication.
- Configure the SPA/API app registration without a client secret.
- Assign the deploying identity to the dashboard-reader app role.
- Keep `POST /api/Telemetry` anonymous at the Entra layer; its BCrypt payload signature remains mandatory.

### Network Design

- App Service public network access remains enabled because external importers must reach the receiver.
- The App Service name is a required deployment parameter and must match the released telemetry hostname exactly.
- Global name availability must be checked immediately before deployment.
- HTTPS-only, TLS 1.2+, FTPS disabled, HTTP/2 enabled.
- App Service regional VNet integration routes outbound Cosmos and Key Vault traffic through the VNet.
- Cosmos DB and Key Vault public network access are disabled.
- Private endpoint network policies are disabled only on the dedicated private-endpoint subnet.
- CIDRs and concrete resource names are deployment parameters supplied out-of-band.

### Data Design

- Cosmos API: NoSQL.
- Consistency: Session.
- Capacity mode: serverless, with no provisioned RU/s.
- Containers: current and history.
- Partition key: `/AnonClientId`.
- Serverless is the cost-optimized choice for this workload; free tier is unavailable for serverless accounts and may also be unavailable on a given subscription.

### Secret Handling

- Generate or supply `TelemetrySecret` at deployment time only.
- Pass it as a secure deployment parameter and store it as a Key Vault secret.
- Configure App Service with a Key Vault reference.
- Never print, commit, or place the secret in a repository parameter file.
- Updating importer-side `STATSAPIURL` or `STATSAPISECRET` is outside this deployment unless separately approved.

### Cost/Availability Trade-offs

- B1 is the lowest App Service tier that supports the required VNet integration.
- Serverless Cosmos minimizes idle data-store cost for a low-volume service.
- Private endpoints and the B1 plan still incur monthly charges.
- This design is single-region, single-instance, and has no zone redundancy or deployment slot.

### Research Findings

- Official App Service guidance confirms Basic B1 supports regional VNet integration.
- Linux App Service must set `vnetRouteAllEnabled` for private Key Vault references.
- Cosmos DB free tier is unavailable for serverless accounts.
- Cosmos DB NoSQL private endpoints use group ID `Sql` and private DNS zone `privatelink.documents.azure.com`.
- Key Vault references use the App Service managed identity and the built-in Key Vault Secrets User role.
- Cosmos data access uses the Cosmos DB Built-in Data Contributor data-plane role.
- Required resource providers must be registered before deployment.

---

## 6. Execution Checklist

### Phase 1: Planning

- [ ] Analyze workspace
- [ ] Gather requirements
- [ ] Confirm subscription and location out-of-band
- [ ] Scan codebase
- [ ] Select recipe
- [ ] Plan architecture
- [ ] Plan approved

### Phase 2: Execution

- [ ] Research exact App Service, Cosmos DB, Key Vault, private endpoint, DNS, monitoring, and Graph resource APIs
- [ ] Author/maintain the Bicep modules
- [ ] Recompile `azuredeploy.json`
- [ ] Maintain the deployment/publish script
- [ ] Provide an anonymous liveness endpoint for App Service health checks
- [ ] Keep ignore rules for local parameter/output/artifact files
- [ ] Build the application publication package

### Phase 3: Validation

- [ ] Validate Bicep and compiled ARM
- [ ] Run subscription-scope what-if with out-of-band parameters
- [ ] Build and lint the Telemetry Service
- [ ] Confirm no secrets or real environment values are present in the diff

### Phase 4: Deployment

- [ ] Register any missing resource providers
- [ ] Confirm availability of the released App Service hostname
- [ ] Create/configure the Entra app and service principal
- [ ] Deploy the resource group and Azure resources
- [ ] Assign the initial dashboard reader
- [ ] Publish the application package
- [ ] Verify public health/auth endpoints and private Cosmos/Key Vault network state

### Phase 5: MISE Remediation

- [ ] Add non-enforcing App Service Authentication for the Entra SPA/API
- [ ] Enable the App Service MISE runtime
- [ ] Preserve anonymous signed uploads, health checks and MSAL configuration
- [ ] Add deployment checks for EasyAuth configuration, client ID, scope and runtime version
- [ ] Recompile the ARM template and build the Telemetry Service
- [ ] Run validation and what-if with out-of-band parameters
- [ ] Deploy the infrastructure update
- [ ] Generate authenticated dashboard traffic
- [ ] Verify compliant MISE key-discovery telemetry after the reporting window

---

## 7. Validation Checks

Record only the **shape** of each check here. Actual commands with real
parameters, their output, timings and results are kept out-of-band.

| Check | Sanitized command shape |
|-------|-------------------------|
| Bicep compilation | `az bicep build --file infra/TelemetryService/main.bicep` |
| Compiled ARM parity | Rebuild ARM to a temporary file and compare SHA-256 with `azuredeploy.json` |
| Subscription deployment validation | `az deployment sub validate ... --parameters @<temporary-parameters>` |
| Subscription what-if | `deploy.ps1 ... -WhatIf` with out-of-band parameters |
| Application build | `dotnet build src/TelemetryService/TelemetryService.slnx --configuration Release` |
| Frontend lint and dependency audit | `npm run lint` and `npm audit --omit=dev` |
| NuGet dependency audit | `dotnet list ... package --vulnerable --include-transitive` |
| Deployment script syntax | PowerShell parser over `infra/TelemetryService/deploy.ps1` |
| Repository safety scan | Diff scan for real environment identifiers and secret assignments |
| EasyAuth behavior | ARM `authsettings` query plus anonymous health, platform-route, protected-API and public-config probes |

Expected post-deployment behavior (design intent, not a recorded run):

- the public health and MSAL-configuration endpoints return HTTP 200;
- the dashboard API returns HTTP 401 without an access token;
- an anonymous invalid telemetry upload reaches the application and returns HTTP 400;
- Cosmos DB and Key Vault public network access are disabled and private endpoints are approved;
- Linux App Service protects the `/.auth/` platform route, so that route returning HTTP 401 is expected and is not a failure.

Note: automatic tenant admin consent may be unavailable, in which case an
assigned dashboard user can receive a delegated-consent prompt on first sign-in.

---

## 8. Files to Generate

| File | Purpose |
|------|---------|
| `.azure/plan.md` | Deployment plan template (synthetic; no environment values) |
| `infra/TelemetryService/main.bicep` | Subscription-scope entry point and resource-group creation |
| `infra/TelemetryService/resources.bicep` | Resource-group infrastructure |
| `infra/TelemetryService/azuredeploy.json` | Compiled ARM template |
| `infra/TelemetryService/deploy.ps1` | Entra, ARM, RBAC, build, publish, and verification orchestrator |
| `src/TelemetryService/Web.Server/Program.cs` | Anonymous health endpoint |
| `.gitignore` | Exclude environment parameters, deployment outputs, and publish artifacts |

No environment-specific parameter file is generated inside the repository.

---

## 9. Next Steps

1. Exercise the authenticated dashboard with an assigned user.
2. Confirm the MISE compliance KPI after its reporting delay.
