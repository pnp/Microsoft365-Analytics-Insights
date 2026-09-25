# Lessons learned

Hard-won notes from operating this service. Each entry exists because something
cost real time or produced a confidently wrong conclusion.

Kept separate from [`README.md`](README.md), which describes how the service
works today. This file is about *how we got things wrong*.

> This is a public repository. Nothing here records tenant IDs, subscription
> IDs, application IDs, hostnames or resource names — see the repository
> [copilot instructions](../../.github/copilot-instructions.md).

---

## Verifying a deployment

### `wwwroot` is a decoy when running from package

The app service sets `WEBSITE_RUN_FROM_PACKAGE=1`, so `/home/site/wwwroot`
(`D:\home\site\wwwroot` on Windows) contains only the `hostingstart.html`
placeholder. Listing it and concluding "nothing is deployed" is wrong.

The running code is a zip under `/home/data/SitePackages/`, and
`packagename.txt` in that folder names the active one. To confirm what is
actually live:

```powershell
$tok = az account get-access-token --resource https://management.core.windows.net/ --query accessToken -o tsv
$H = @{ Authorization = "Bearer $tok" }
$scm = "https://<app>.scm.azurewebsites.net"

$pkg = (Invoke-RestMethod "$scm/api/vfs/data/SitePackages/packagename.txt" -Headers $H).Trim()
Invoke-WebRequest "$scm/api/vfs/data/SitePackages/$pkg" -Headers $H -OutFile package.zip
```

Then read `Web.Server.deps.json` inside the zip for the exact resolved package
versions. That is the only statement about what is deployed that does not rely
on trusting the deployment API, the workflow log, or the commit history.

### Kudu basic auth is disabled; use an Entra token

`az webapp deployment list-publishing-credentials` returns credentials that
produce `401` against `*.scm.azurewebsites.net`, because SCM basic
authentication is turned off. Use a bearer token for
`https://management.core.windows.net/` instead, as above.

### "Is the right version deployed?" is a question worth asking early

Ours was — the deployed package was byte-identical to `dev` for this service.
But confirming it took one command against the deployment history and one
`git diff <deployed-sha>..origin/dev -- src/TelemetryService`, and it removed a
whole branch of speculation. Do it before theorising about behaviour.

Note the deployed SHA may be a `main` merge commit, so
`git merge-base --is-ancestor <sha> origin/dev` can legitimately report "no"
while the code is identical. Compare the paths you care about, not ancestry.

---

## Telemetry

### Query the Log Analytics workspace, not the App Insights component

This component is workspace-based (`ingestionMode: LogAnalytics`). Querying it
through `az monitor app-insights query` returned **only about an hour** of data,
which read exactly like "the app was redeployed and lost its history".

Querying the backing workspace directly returned the full 30 days:

```powershell
$ws = az monitor log-analytics workspace show -g <rg> -n <workspace> --query customerId -o tsv
az monitor log-analytics query -w $ws --analytics-query "AppRequests | where TimeGenerated > ago(30d) | summarize count() by Name"
```

Table names differ: `requests` / `dependencies` in the component view become
`AppRequests` / `AppDependencies` in the workspace. If a query returns
suspiciously little history, re-run it against the workspace before drawing any
conclusion from the gap.

Two smaller traps in the same area: `first` is a reserved word in KQL and fails
to parse as a column alias, and some `az monitor app-insights query` failures
surface only as `BadArgumentError: The request had some invalid properties`
with no indication of which property.

### Outbound dependency telemetry is the ground truth for identity behaviour

`AppDependencies` records the full outbound URL in `Data` (the `Name` column
drops the query string). That is how the anonymous key discovery in #409 was
found and proved, rather than inferred from source:

```kusto
AppDependencies
| where TimeGenerated > ago(30d)
| where Target has "login.microsoftonline.com"
| project TimeGenerated, Data, ResultCode
```

When reasoning about what an identity library does at runtime, prefer this over
reading the library's source. It answers "what did this deployment actually
send", which is the question that matters.

### …but dependency telemetry redacts query strings

`Data` keeps the *shape* of a URL, not its query values. A request carrying
`?appid=<guid>` is recorded as `…/openid-configuration?*`, and other callers
show per-parameter redaction such as `?api-version=2019-08-01&resource=REDACTED`.

Searching `Data` for a literal parameter value therefore finds nothing even when
the parameter is definitely on the wire. **Test for the presence of `?`, not for
the parameter.** The most reliable form is a before/after count, which is immune
to how any individual URL was redacted:

```kusto
AppDependencies
| where TimeGenerated > ago(30d)
| where Target has "login.microsoftonline.com"
| summarize total = count(), tagged = countif(Data contains "?") by bin(TimeGenerated, 1d)
```

This cost real time. A verified-working deployment was briefly declared broken
because the literal `appid=` could not be found in telemetry.

### One token validation produces two different key fetches

Microsoft.Identity.Web runs an independent `ConfigurationManager` through
`AadIssuerValidator` to resolve the issuer, separate from the one
`JwtBearerOptions` owns. Both fetch `openid-configuration` and then the JWKS, so
a single validation emits two near-simultaneous pairs of calls.

Only the `JwtBearerOptions` one is ours to configure. The issuer validator's
pair will never carry our parameter, and its presence is not a defect. Any check
that assumes "all discovery calls should look the same" will misfire.

Related: validation short-circuits. A token rejected on audience or issuer may
never require signing keys, so a probe can produce the issuer validator's fetch
and nothing else. When forcing key discovery deliberately, the token has to get
far enough to need a signature check.

### The EasyAuth sidecar is invisible from inside the app

On Linux, App Service Authentication runs as a **separate container** — the
docker log shows `StartingAuthContainer`. It is not part of the application
process, so it does not appear in the application's Application Insights at all.
(On Windows it runs inside the site's IIS pipeline, but still emits nothing to
the application's own telemetry.)

Practical consequence: you cannot confirm what EasyAuth does, which identity
stack it uses, or what it sends to Entra, from application telemetry. Absence of
evidence in App Insights says nothing about the sidecar. We spent time looking
for its key-discovery calls in a place they could never appear. App Service's
own **EasyAuth detector** is where to look instead — see
[Read the platform's own diagnostics first](#read-the-platforms-own-diagnostics-first).

---

## Identity

### Nothing adds `appid` to key discovery for you

Neither `Microsoft.Identity.Web` nor `Microsoft.IdentityModel` appends an
`appid` parameter to the OpenID Connect metadata or JWKS request — verified by
searching both repositories' `src/` trees. Left alone, an app fetches Entra's
signing keys anonymously and Entra cannot tell which application is asking.

Entra echoes the parameter from the metadata request into the `jwks_uri` it
returns, so setting it once on `MetadataAddress` covers both requests. It also
narrows the response to the keys that apply to that app.

### `MetadataAddress` must be set in `Configure`, never `PostConfigure`

This is the subtle one. `JwtBearerPostConfigureOptions` builds the
`ConfigurationManager` — the object that actually calls Entra — *from*
`MetadataAddress`, during `PostConfigure`.

Set the address in a `PostConfigure` that runs afterwards and you get a failure
that is invisible to every ordinary check: `JwtBearerOptions.MetadataAddress`
reads back correctly, configuration dumps look right, and tokens still validate
— while the real network request continues to use the old address. The only
symptom is in telemetry that nobody looks at.

`EntraKeyDiscoveryTests.ConfiguredJwtBearerOptions_FetchKeysWithTheClientId`
asserts against the `ConfigurationManager`'s own address, not just the options
string, precisely so this cannot regress silently.

### Expanding a convenience overload can drop behaviour

`AddMicrosoftIdentityWebApi(IConfigurationSection)` binds the section to
**both** `JwtBearerOptions` and `MicrosoftIdentityOptions`. Expanding it into
the two-delegate overload to customise something means both binds must be
reproduced by hand; forgetting the first is a silent behaviour change.

Read the overload you are replacing before replacing it.

---

## Compliance work

### Confirm a remediation is viable before deploying it

Enabling App Service Authentication as MISE remediation was designed and shipped
without first establishing that platform-supplied authentication satisfies the
compliance KPI. As configured, it could not: it validated tokens with MISE v1
(see below), and two review cycles were spent finding out that it did not work
— and several more before anyone found out why.

### Read the platform's own diagnostics first

App Service's EasyAuth detector (*Diagnose and solve problems* →
*Authentication Configuration and Investigation Detector (EasyAuth)*, or
`GET {site}/detectors/EasyAuth?api-version=2022-03-01&startTime=…&endTime=…`)
reports the App Service Authentication version actually running and, for every
failed token validation, which MISE generation handled it. On this service it
had been saying `JWT validation failed: MISEv1 enabled` and
`Component: AuthenticationTicketProvider:1.37.0.0` since the day MISE was
switched on. Five weeks of hypotheses, emails and configuration changes went
by before anyone opened it.

It only records *failures*, so to get a reading on demand, send one
well-formed but unsigned token (the README has a snippet) and read the detector
a few minutes later. `/.auth/version` is no substitute: on Linux it answers
`401` unless the request is already authenticated.

### `WEBSITE_AAD_ENABLE_MISE` is only half the switch

`WEBSITE_AAD_ENABLE_MISE=true` makes App Service Authentication validate with
MISE — but on its own that is **MISE v1**, and the compliance KPI requires v2.
MISE v2 is in App Service Authentication's first-party modules, which load only
when `WEBSITE_LOAD_FIRST_PARTY_AUTH=true` is set too, and App Service accepts
that setting only on a subscription it has enabled for first-party
authentication. Even then it depends on the host: those modules reached Windows
first and Linux region by region, and had not reached this service's region on
Linux. That is why the service moved to Windows.

The public Linux build cannot do it. The public
`mcr.microsoft.com/appsvc/middleware` image for the exact App Service
Authentication version this service ran lists only MISE `1.37.0` packages in
its `Middleware.Host.deps.json`; MISE v2 ships in the separate first-party
modules. The earlier conclusion that "EasyAuth bundles MISE v1, so there is no
path" was half right: it described the configuration we had, not the
platform's limits.

### A deleted app registration gets replaced without anyone deciding to

Non-compliant registrations are scheduled for deletion, and this one was
deleted. Three days later a replacement with the same display name — and a new
client ID — was in place and the site had been pointed at it. Nothing made
anyone notice the ID had changed, and `deploy.ps1` would have done the same on
its own: it looked the registration up by display name and created one when
nothing matched. A new client ID restarts the compliance process against a
registration nobody is tracking and drops every user's role assignment and
consent, and the next reminder looks exactly like the last one apart from the
App ID.

`deploy.ps1` now resolves the registration from the client ID the existing site
is configured for, and **stops** if it has gone, pointing at Entra's *Deleted
applications* (restorable for 30 days). Creating a replacement needs
`-AllowNewEntraApplication`. When a compliance notice arrives, compare its App
ID with the site's `AzureAd__ClientId` before assuming it is the one you have
already been working on.

The check that should have come first: what signal does the KPI actually read,
and does the proposed change produce that specific signal? "Tokens now validate
through a MISE-enabled component" was assumed to imply the KPI would clear. It
did not.

### Prove it with the target signal, not a proxy

Every check made after that deployment — EasyAuth intercepting requests,
`allowedApplications` correct, tokens validating end to end, protected endpoints
returning `200` — was true, and none of them measured the thing being asked for.
A remediation is verified when the signal it targets changes, not when its
plumbing looks healthy.

### Check package availability before designing around a dependency

The MISE packages (`Microsoft.Identity.ServiceEssentials*`,
`Microsoft.IdentityModel.S2S`) are published to an internal feed only — all
return 404 from nuget.org. This repository is public and CI restores from
nuget.org on a hosted public runner, so referencing them is structurally
impossible, not merely inconvenient.

A single query against the nuget.org flat-container API settles this in seconds
and should precede any design that assumes a package is reachable:

```powershell
Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/<lowercase.package.id>/index.json"
```

### Low-traffic services emit very little identity telemetry

Key discovery happens on cold start and cache expiry, not per request. A service
with a handful of sign-ins produces correspondingly few key-discovery events,
which makes any telemetry-derived compliance signal slow and noisy for it. Worth
stating explicitly when discussing results, rather than assuming a clean signal
exists to be read.

### Record what a change is *not*

The `appid` fix in #409 is a genuine defect fix and a diagnosability
improvement. It is **not** compliance remediation — the KPI has a separate
supported-version requirement, and MISE identifies the calling application with
a request header rather than that query parameter.

The first version of that commit implied otherwise. Overstating a partial fix is
worse than shipping nothing, because it stops the search for the real one. Where
a change only moves part of the way, say so in the code and the documentation.
