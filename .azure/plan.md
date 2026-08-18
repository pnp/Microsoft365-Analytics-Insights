# Azure Deployment Plan

> **Status:** Deployed

Generated: 2026-08-13T15:24:00+02:00
Updated: 2026-08-18T14:28:15+02:00

**Current change:** Enable the App Service MISE authentication runtime in
allow-anonymous mode so bearer-token validation emits compliant key-discovery
telemetry while the ASP.NET Core application continues enforcing dashboard
scope and role authorization.

---

## 1. Project Overview

**Goal:** Deploy the Telemetry Service to a new, isolated Azure resource group and publish the ASP.NET Core/React application. Keep the public HTTPS receiver reachable by external analytics-engine installations while making Cosmos DB and secret storage private-only.

**Path:** Add Azure infrastructure and deployment automation to the existing Telemetry Service.

**Public-repository rule:** Real subscription, tenant, user, resource, URL, secret, and environment parameter values are confirmed and used only out-of-band. They must not be committed to this repository.

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Proof of concept |
| Scale | Small: fewer than 1,000 reporting installations |
| Budget | Cost-optimized |
| Subscription | Confirmed out-of-band; deliberately omitted from this public repository |
| Tenant | Confirmed out-of-band; deliberately omitted from this public repository |
| Location | West Europe |
| Ingress | Public HTTPS App Service for signed uploads and Entra-authenticated dashboard access |
| Endpoint compatibility | Preserve the exact App Service hostname embedded in the latest release; the real name is supplied out-of-band |
| Data access | Cosmos DB and Key Vault public network access disabled |
| Parameters | No environment-specific parameter file committed |
| Dashboard access | Current deployer receives the initial `Telemetry.Dashboard.Read` assignment |
| Compliance | West Europe residency; no additional CMK or firewall requirement |

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

- The user explicitly requested ARM infrastructure in the repository.
- Bicep is the maintainable source; a compiled `azuredeploy.json` ARM template will also be committed.
- Azure CLI/PowerShell is required for the Microsoft Graph app registration, initial app-role assignment, secure runtime parameters, and ZIP application publication.
- No AZD environment or environment parameter values will be committed.

---

## 5. Architecture

**Stack:** Azure App Service

### Service Mapping

| Component | Azure Service | SKU / Configuration |
|-----------|---------------|---------------------|
| API + dashboard | Linux App Service | Basic B1, one instance, .NET 10, Always On |
| Hosting plan | App Service plan | Linux Basic B1 |
| Telemetry store | Azure Cosmos DB for NoSQL | Serverless, selected after the subscription rejected free tier |
| Secret storage | Azure Key Vault | Standard, RBAC, purge protection, private-only access |
| Network | Virtual Network | Dedicated App Service integration and private-endpoint subnets |
| Private access | Private endpoints | Cosmos DB SQL endpoint and Key Vault vault endpoint |
| Private DNS | Azure Private DNS | Cosmos DB and Key Vault private-link zones linked to the VNet |
| Authentication | Microsoft Entra ID | Single-tenant SPA/API app, `Telemetry.Read` scope, `Telemetry.Dashboard.Read` role |
| Monitoring | Application Insights | Workspace-based, low-volume POC configuration |
| Logs | Log Analytics workspace | 30-day retention |

### Identity and Access

- Enable a system-assigned managed identity on the App Service.
- Grant the identity Cosmos DB Built-in Data Contributor at the Cosmos account scope.
- Grant the identity Key Vault Secrets User at the vault scope.
- Disable Cosmos DB local/key authentication.
- Configure the SPA/API app registration without a client secret.
- Assign the current deployer to the dashboard-reader app role.
- Keep `POST /api/Telemetry` anonymous at the Entra layer; its BCrypt payload signature remains mandatory.

### Network Design

- App Service public network access remains enabled because external importers must reach the receiver.
- The App Service name is a required deployment parameter and must match the currently released telemetry hostname exactly.
- Azure global name availability was confirmed during planning; it must be checked again immediately before deployment.
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
- Azure rejected free tier because it is unsupported for this internal subscription.
- The user selected serverless Cosmos DB as the cost-optimized fallback.

### Secret Handling

- Generate or supply `TelemetrySecret` at deployment time only.
- Pass it as a secure deployment parameter and store it as a Key Vault secret.
- Configure App Service with a Key Vault reference.
- Never print, commit, or place the secret in a repository parameter file.
- Updating GitHub `STATSAPIURL` or `STATSAPISECRET` is outside this deployment unless separately approved.

### Cost/Availability Trade-offs

- B1 is the lowest App Service tier that supports the required VNet integration.
- Serverless Cosmos minimizes idle data-store cost for this low-volume POC.
- Private endpoints and the B1 plan still incur monthly charges.
- This POC is single-region, single-instance, and has no zone redundancy or deployment slot.

### Research Findings

- Official App Service guidance confirms Basic B1 supports regional VNet integration.
- Linux App Service must set `vnetRouteAllEnabled` for private Key Vault references.
- Cosmos DB free tier is unavailable for serverless accounts.
- The target internal subscription also rejects Cosmos free tier, so the approved fallback is serverless.
- Cosmos DB NoSQL private endpoints use group ID `Sql` and private DNS zone `privatelink.documents.azure.com`.
- Key Vault references use the App Service managed identity and the built-in Key Vault Secrets User role.
- Cosmos data access uses the Cosmos DB Built-in Data Contributor data-plane role.
- The Key Vault resource provider is not currently registered and must be registered before deployment.
- The exact App Service hostname embedded in the latest release is currently globally available.

---

## 6. Execution Checklist

### Phase 1: Planning

- [x] Analyze workspace
- [x] Gather requirements
- [x] Confirm subscription and location with user
- [x] Scan codebase
- [x] Select recipe
- [x] Plan architecture
- [x] **User approved this plan**

### Phase 2: Execution

- [x] Research exact App Service, Cosmos DB, Key Vault, private endpoint, DNS, monitoring, and Graph resource APIs
- [x] Update Bicep modules from free-tier provisioned throughput to serverless
- [x] Recompile `azuredeploy.json`
- [x] Update the deployment/publish script for serverless
- [x] Add an anonymous liveness endpoint for App Service health checks
- [x] Add ignore rules for local parameter/output/artifact files
- [x] Build the application publication package
- [x] Update plan status to `Ready for Validation`

### Phase 3: Validation

- [x] Reinvoke azure-validate after the serverless template change
- [x] Revalidate Bicep and compiled ARM
- [x] Rerun subscription-scope what-if with out-of-band parameters
- [x] Rebuild and lint the Telemetry Service
- [x] Reconfirm no secrets or real environment values are present in the diff
- [x] Restore plan status to `Validated`
- [x] Append updated validation proof below

### Phase 4: Deployment

- [x] Invoke azure-deploy skill
- [x] Register any missing resource providers
- [x] Reconfirm availability of the released App Service hostname
- [x] Create/configure the Entra app and service principal
- [x] Deploy the new resource group and Azure resources
- [x] Assign the initial dashboard reader
- [x] Publish the application package
- [x] Verify public health/auth endpoints and private Cosmos/Key Vault network state
- [x] Update plan status to `Deployed`

### Phase 5: MISE Remediation

- [x] Add non-enforcing App Service Authentication for the existing Entra SPA/API
- [x] Enable the App Service MISE runtime
- [x] Preserve anonymous signed uploads, health checks and MSAL configuration
- [x] Add deployment checks for EasyAuth configuration, client ID, scope and runtime version
- [x] Recompile the ARM template and build the Telemetry Service
- [x] Reconfirm the production resource and Entra application out-of-band
- [x] Confirm the production Azure CLI context with the user
- [x] Reinvoke azure-validate after the Linux EasyAuth verification adjustment
- [x] Run a production subscription what-if
- [x] Deploy the infrastructure update
- [ ] Generate authenticated dashboard traffic
- [ ] Verify compliant MISE key-discovery telemetry after the reporting window

---

## 7. Validation Proof

> **Required:** The azure-validate skill must populate this section before setting status to `Validated`.

| Check | Command Run | Result | Timestamp |
|-------|-------------|--------|-----------|
| Bicep compilation | `az bicep build --file infra/TelemetryService/main.bicep` | Pass; no Bicep diagnostics | 2026-08-13T15:44:34+02:00 |
| Compiled ARM parity | Rebuilt ARM to a temporary file and compared SHA-256 with `azuredeploy.json` | Pass; hashes match and JSON is valid | 2026-08-13T15:44:34+02:00 |
| Subscription deployment validation | `az deployment sub validate ... --template-file azuredeploy.json --parameters @<temporary-parameters>` | Pass; provisioning state `Succeeded` | 2026-08-13T15:44:34+02:00 |
| Subscription what-if | `deploy.ps1 ... -WhatIf` with out-of-band environment values | Pass; 28 creates, no policy or template errors | 2026-08-13T15:44:34+02:00 |
| Application build | `dotnet build src/TelemetryService/TelemetryService.slnx --configuration Release --no-restore` | Pass; 0 warnings, 0 errors | 2026-08-13T15:44:34+02:00 |
| Frontend lint and dependency audit | `npm run lint` and `npm audit` | Pass; 0 vulnerabilities | 2026-08-13T15:44:34+02:00 |
| NuGet dependency audit | `dotnet list ... package --vulnerable --include-transitive` | Pass; no vulnerable packages | 2026-08-13T15:44:34+02:00 |
| Deployment script syntax | PowerShell parser over `infra/TelemetryService/deploy.ps1` | Pass; no parser errors | 2026-08-13T15:44:34+02:00 |
| Repository safety scan | Zero-context diff scan for real environment identifiers and secret assignments | Pass; 0 findings | 2026-08-13T15:44:34+02:00 |
| Azure preflight | Provider, policy, free-tier, hostname, runtime, RBAC, and Entra app-creation checks | Pass | 2026-08-13T15:44:34+02:00 |
| Serverless ARM validation | `az deployment sub validate ... --template-file azuredeploy.json --parameters @<temporary-parameters>` | Pass; provisioning state `Succeeded` | 2026-08-13T17:02:02+02:00 |
| Serverless what-if | `deploy.ps1 ... -WhatIf` against the partial resource group | Pass; 13 creates, 15 convergent deployments, no policy errors | 2026-08-13T17:02:02+02:00 |
| Serverless application build | `dotnet build ... --configuration Release --no-restore` | Pass; 0 warnings, 0 errors | 2026-08-13T17:02:02+02:00 |
| Serverless frontend and repository checks | `npm run lint`, `npm audit`, PowerShell parse, `git diff --check`, environment-data scan | Pass; 0 vulnerabilities or findings | 2026-08-13T17:02:02+02:00 |

**Validated by:** azure-validate skill

**Validation timestamp:** 2026-08-13T17:02:02+02:00

The original provisioned-throughput validation is retained for history; the later serverless validation is authoritative.

### Current MISE remediation validation proof

| Check | Sanitized command shape | Result | Timestamp |
|-------|-------------------------|--------|-----------|
| Production Azure context | `az account set/show` against retained out-of-band values | Pass; subscription and tenant confirmed by the user | 2026-08-18T14:47:03+02:00 |
| Bicep compilation and ARM parity | `az bicep build --file infra/TelemetryService/main.bicep` plus SHA-256 comparison | Pass; checked-in ARM exactly matches Bicep output | 2026-08-18T14:47:03+02:00 |
| Production ARM validation | `az deployment sub validate ... --parameters @<temporary-parameters>` | Pass; provisioning state `Succeeded` | 2026-08-18T14:47:03+02:00 |
| Production what-if | `deploy.ps1 ... -AzureAdClientId <out-of-band> -WhatIf` | Pass; 29 convergent deployments, 5 ignored platform resources, no deletions | 2026-08-18T14:47:03+02:00 |
| Application build | `dotnet build src/TelemetryService/Web.Server/Web.Server.csproj --configuration Release` | Pass; build succeeded | 2026-08-18T14:47:03+02:00 |
| Frontend lint and production dependency audit | `npm run lint` and `npm audit --omit=dev` | Pass; lint clean and no production vulnerabilities | 2026-08-18T14:47:03+02:00 |
| NuGet dependency audit | `dotnet list ... package --vulnerable --include-transitive` | Pass; no vulnerable packages | 2026-08-18T14:47:03+02:00 |
| Deployment script and repository safety | PowerShell parser, `git diff --check`, and exact production-identifier scan | Pass; no parser errors, whitespace errors, secrets, or environment identifiers in the diff | 2026-08-18T14:47:03+02:00 |
| Linux EasyAuth behavior | ARM authsettings query plus anonymous health, platform-route, protected-API and public-config probes | Pass; EasyAuth enabled with `~1`, platform route intercepted, health/config public and dashboard API protected | 2026-08-18T15:05:37+02:00 |
| Linux verification adjustment | Bicep/ARM parity, PowerShell parser, repository safety scan and Release build | Pass; all current artifacts and checks succeeded | 2026-08-18T15:05:37+02:00 |

**Validated by:** azure-validate skill

**Validation timestamp:** 2026-08-18T15:05:37+02:00

All production identifiers and parameters remain out-of-band and are
intentionally omitted from this public repository.

The first deployment applied the ARM changes successfully, but the post-deploy
check assumed anonymous access to `/.auth/version`. Linux App Service protects
that platform route with HTTP 401. The verification now accepts that
interception behavior while retaining the `~1` runtime-selector check.

### Current MISE remediation deployment result

Deployment completed: 2026-08-18T15:11:50+02:00

- App Service Authentication is enabled in allow-anonymous mode.
- The App Service MISE runtime setting is enabled.
- The configured authentication runtime selector is `~1`.
- The platform authentication route is intercepted by EasyAuth.
- The public health and MSAL-configuration endpoints return HTTP 200.
- The dashboard API returns HTTP 401 without an access token.
- An anonymous invalid telemetry upload reaches the application and returns HTTP 400.
- Automatic tenant admin consent remains unavailable; an assigned dashboard
  user might receive a delegated-consent prompt on first sign-in.
- No subscription, tenant, resource, application, user, URL, network or secret
  values are recorded in this plan.

---

## Deployment Pause

- The resource group and some supporting resources were created before the ARM deployment failed.
- Cosmos DB was not created because free tier is unsupported for the target internal subscription.
- The App Service plan was not created because West Europe temporarily reported no B1 Linux capacity.
- The Entra application and service principal were created; automatic admin consent was unavailable.
- No application package was published.
- The user selected serverless Cosmos DB and requested a pause before retrying.
- After resume, the serverless changes were revalidated and the deployment completed successfully.

---

## Deployment Result

Deployment completed: 2026-08-13T17:21:46+02:00

- The released App Service hostname was preserved.
- The public health endpoint returns HTTP 200.
- The public MSAL configuration endpoint returns HTTP 200.
- The dashboard data endpoint returns HTTP 401 without an Entra token.
- An invalid telemetry upload returns HTTP 400.
- App Service is running on Linux B1 with HTTPS-only ingress and VNet integration.
- Cosmos DB uses serverless capacity, public access is disabled, and local/key authentication is disabled.
- One Cosmos database and two containers exist with a managed-identity data-role assignment.
- Key Vault public access is disabled and the App Service secret reference is resolved.
- Two private endpoints are approved.
- The Entra application, service principal, required assignment policy, and current-user role assignment exist.
- Tenant admin consent could not be granted automatically; the assigned user might receive a delegated-consent prompt on first dashboard sign-in.

---

## 8. Files to Generate

| File | Purpose | Status |
|------|---------|--------|
| `.azure/plan.md` | Deployment source of truth | Complete |
| `infra/TelemetryService/main.bicep` | Subscription-scope entry point and resource-group creation | Complete |
| `infra/TelemetryService/resources.bicep` | Resource-group infrastructure | Complete |
| `infra/TelemetryService/azuredeploy.json` | Compiled ARM template | Complete |
| `infra/TelemetryService/deploy.ps1` | Generic Entra, ARM, RBAC, build, publish, and verification orchestrator | Complete |
| `src/TelemetryService/Web.Server/Program.cs` | Anonymous health endpoint | Complete |
| `.gitignore` | Exclude environment parameters, deployment outputs, and publish artifacts | Complete |

No environment-specific parameter file will be generated inside the repository.

---

## 9. Next Steps

> Current: EasyAuth/MISE remediation deployed; authenticated traffic and KPI confirmation remain.

1. Exercise the authenticated dashboard with an assigned user.
2. Confirm the MISE compliance KPI after its reporting delay.
