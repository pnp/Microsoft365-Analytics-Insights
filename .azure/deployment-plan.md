# Application Insights Telemetry Plan

> **Status:** Validated

Generated: 2026-09-02

## 1. Project overview

Enhance the existing ASP.NET application's Copilot adoption telemetry so an
analysis that stalls can be distinguished from SQL execution, result
materialisation, scoring, cache publication, and process-lifetime failure.

**Path:** Modify an existing production application.

No Azure resources, infrastructure, configuration schema, or deployment
settings will be created or changed.

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Production |
| Scale | Large |
| Priority | Reliability and diagnostic precision |
| Subscription | Not applicable: no resource operation |
| Location | Not applicable: no resource operation |
| Compliance | Telemetry must exclude customer and environment data |

## 3. Components detected

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| Analytics web API | API | ASP.NET Web API on .NET Framework | `src/AnalyticsEngine/Web` |
| Adoption engine | Application service | EF6 and Azure SQL | `src/AnalyticsEngine/Common/Entities/CopilotAdoption` |
| Telemetry abstraction | Logging | Application Insights SDK | `src/AnalyticsEngine/Common/DataUtils` |
| Tests | Unit/performance | MSTest and repository stress harness | `src/AnalyticsEngine/Tests.*` |

## 4. Recipe selection

**Selected:** Existing repository build and deployment process.

No AZD, CLI, Bicep, or Terraform recipe is required because this change adds
no Azure resource and performs no deployment.

## 5. Architecture

The existing App Service, Application Insights, and Azure SQL architecture is
unchanged. The code change adds structured lifecycle events through the
existing telemetry abstraction.

Independent reviews by Claude Opus, Gemini, and Grok identified the following
required design constraints:

- Telemetry submission must be queued and non-blocking so the Application
  Insights SDK cannot delay analysis completion or cache publication.
- Cache publication must precede terminal telemetry.
- Every event needs a monotonic sequence number and every concurrent operation
  needs its own generated operation number.
- A random AppDomain-lifetime identifier and host shutdown event must
  distinguish process lifetime from analysis lifetime without exposing Azure
  resource identifiers.
- `QueryCompleted` means EF returned from `ToListAsync`; it deliberately covers
  connection acquisition, SQL execution, network transfer, and EF
  materialisation. It must not be labelled as server-only SQL time.
- Heartbeats must run independently of the initiating ASP.NET request context
  and report schedule drift as well as the active operation set.
- Production code needs injectable analysis-runner, cache, wait-budget, and
  telemetry seams so fast deterministic tests can exercise the 202, in-flight
  deduplication, completion, failure, and cache-publication paths.
- The shared `TelemetryClient` context must never be mutated per run; operation
  correlation is applied to each telemetry item.

## 6. Provisioning limit checklist

| Resource type | Number to deploy | Total after deployment | Limit/quota | Notes |
|---------------|------------------|------------------------|-------------|-------|
| None | 0 | Unchanged | Not applicable | Code-only observability change |

**Status:** No provisioning or quota validation required.

## Data handling

- Emit only compile-time phase names, generated run identifiers, durations,
  booleans, bounded configuration values, and process resource measurements.
- Do not emit customer names, user identifiers, URLs, database or tenant
  identifiers, raw SQL, query parameters, row counts, payloads, or search text.

## 7. Execution checklist

- [x] Analyze workspace and requirements.
- [x] Confirm that no Azure resource or deployment changes are required.
- [x] Select the existing repository delivery process.
- [x] Define the telemetry data boundary.
- [x] User approved this plan, conditional on independent model critique before implementation.
- [x] Rebase the worktree branch onto current `origin/dev`.
- [x] Design and implement correlated lifecycle telemetry.
- [x] Add focused tests.
- [x] Obtain independent multi-model critiques.
- [x] Address every verified critique in the implementation.
- [x] Build and test the affected workloads.
- [x] Scan the full diff for customer and environment data.
- [ ] Open a pull request against `dev`.

## 8. Validation

- Unit tests for telemetry fields and lifecycle transitions.
- Copilot adoption tests and the affected project build.
- Diff scan for customer or environment identifiers.

### Validation proof

| Check | Command | Result |
|-------|---------|--------|
| Web and test build | `MSBuild Tests.UnitTests.csproj /t:Build /p:Configuration=Release /p:LangVersion=12` | Passed |
| Adoption behavior | `vstest.console Tests.UnitTests.dll /TestCaseFilter:"FullyQualifiedName~CopilotAdoption"` | 135 passed |
| Performance harness build | `MSBuild Tests.FakeDataGen.csproj /t:Build /p:Configuration=Release` | Passed |
| Patch integrity | `git diff --check` | Passed |
| Independent review | Claude Opus, Gemini, and Grok design and implementation critiques | All verified findings addressed; final review clean |
| Static role verification | Not applicable: no infrastructure or RBAC change | Passed |

**Validated by:** azure-validate workflow

**Validation date:** 2026-09-02

## 9. Files

| File area | Purpose | Status |
|-----------|---------|--------|
| `.azure/deployment-plan.md` | Code-only preparation record | Complete |
| Existing C# telemetry and adoption files | Lifecycle instrumentation | Pending |
| Existing MSTest files | Behavioral coverage | Pending |

## 10. Deployment

Not in scope. The pull request will contain application code and tests only.
