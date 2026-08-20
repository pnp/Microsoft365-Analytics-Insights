# Azure configuration drift detection

Detects when the deployed telemetry-service resource group no longer matches
[`../resources.bicep`](../resources.bicep).

## Why

Deployed resources drift, and until now nothing noticed. Two examples found on the same service in
one afternoon:

| Drift | Symptom | How it presented |
| --- | --- | --- |
| `siteConfig.alwaysOn` was `false`, template said `true` | The app cold-started after idle | Slow first request, intermittently |
| EasyAuth `defaultAuthorizationPolicy.allowedApplications` was an empty array | Every authenticated caller rejected | A bare `403` — but **only** for valid tokens. Anonymous requests succeeded, and a token with a bad signature also succeeded (it fails validation, so it is treated as anonymous). Diagnosing it from the outside was close to impossible. |

In both cases the template in this repository was correct and the deployment was not, so reading the
repository told you nothing about the actual state.

## How it works

[`../../../.github/workflows/infra-drift.yml`](../../../.github/workflows/infra-drift.yml) runs
weekly and on demand:

1. `az deployment group what-if` diffs the committed template against the deployed resource group.
   **This is read-only.** ARM computes the diff server-side; nothing is deployed.
2. `filter-what-if.mjs` removes the differences that are expected (see below) and turns the rest into
   a job summary.
3. The job fails if anything is left.

There is deliberately **no** remediation step. A drift check that can "fix" things is a deployment,
and would happily undo a deliberate out-of-band change at 07:00 on a Monday.

## Configuration

No environment identifiers are committed — this repository is public. Everything comes from
repository secrets and variables, reusing what `telemetry-service.yml` already needs:

| Kind | Name | Purpose |
| --- | --- | --- |
| Secret | `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` | OIDC federated sign-in. Already configured for deployment. |
| Variable | `TELEMETRY_RESOURCE_GROUP` | Resource group to compare against. Already configured for deployment. |
| Variable | `TELEMETRY_INFRA_PARAMETERS` | **New.** The non-secret template parameters, as a JSON object. |

`TELEMETRY_INFRA_PARAMETERS` holds the values the resource group was deployed with:

```json
{
  "location": "<region>",
  "webAppName": "<app service name>",
  "namePrefix": "<prefix>",
  "vnetAddressPrefix": "10.0.0.0/16",
  "appIntegrationSubnetPrefix": "10.0.1.0/24",
  "privateEndpointSubnetPrefix": "10.0.2.0/24",
  "azureAdClientId": "<app registration client id>"
}
```

The workflow adds `azureAdTenantId` from `AZURE_TENANT_ID`, and a **placeholder** for the
`@secure()` `telemetrySecret` parameter — its real value lives only in Key Vault, and `what-if`
cannot read the deployed secret to compare against in any case. The resulting difference on the
secret resource is suppressed by the ignore rules.

The conversion into ARM's `{"name": {"value": …}}` form is done by
[`build-parameters.mjs`](./build-parameters.mjs), which is unit tested — including a check that
every non-secure `param` declared in `resources.bicep` is actually supplied, so adding a parameter
to the template fails a pull request rather than silently breaking the weekly run.

The credential needs only **Reader** on the resource group plus
`Microsoft.Resources/deployments/whatIf/action`. It does not need Contributor; if you are reusing
the deployment credential it already has more than enough.

If either variable is unset the job logs a notice and skips, so a fork never sees a red check.

## The ignore rules

[`drift-ignore.json`](./drift-ignore.json). Without them the check is red on day one and muted by day
two — the Key Vault secret value alone guarantees a permanent difference.

Each rule needs a `reason`. `propertyPath` is a JavaScript regular expression matched against the
flattened delta path, and `propertyChangeType` narrows a rule to one kind of difference. A rule with
no `propertyPath` suppresses the whole resource; that is a blind spot, and a test asserts none are
present.

The precision matters. Platform-injected `WEBSITE_*` app settings are suppressed **only** when they
are being *deleted* (the platform added something the template does not declare). A `WEBSITE_*`
setting the template does own being *modified* is still reported — otherwise a drifted
`WEBSITE_AAD_ENABLE_MISE` would slip through, which is the same class of failure as the EasyAuth bug
above. There is a regression test for exactly this.

### Adding a rule

Do it reluctantly: every rule is somewhere the check has been told not to look. Both defects that
motivated this feature were single properties on `Microsoft.Web` resources.

1. Add the entry with a `reason` that explains *why the difference is expected*, not what it is.
2. Add a test to `filter-what-if.test.mjs` proving the rule suppresses what it should, and — if the
   rule is at all broad — that it does **not** suppress a neighbouring real difference.
3. `node --test`.

## Running the filter locally

The filter is pure and needs no Azure access, so it can be run against saved `what-if` output:

```bash
az deployment group what-if -g <rg> --template-file ../resources.bicep \
  --parameters @params.json --result-format FullResourcePayloads --no-pretty-print -o json > what-if.json

node filter-what-if.mjs what-if.json drift-ignore.json
node --test          # the filter's own tests
```

`what-if.json` contains subscription and resource-group identifiers, and — because the check runs with
`--result-format FullResourcePayloads` — full resource payloads including app settings. Do not commit
it or paste it into an issue.

The job summary is safe to publish: `shortenResourceId` trims every reported id to its
provider-relative part, and **string values are redacted**, leaving only the property path, the change
type, and the value's type/size. Booleans and numbers are published because they cannot carry a
secret and they are usually the finding itself (`alwaysOn: false -> true`). Without that redaction a
drifted app-settings resource would print the Application Insights connection string — instrumentation
key included — into a world-readable summary on a public repository. There are regression tests for
both behaviours.
