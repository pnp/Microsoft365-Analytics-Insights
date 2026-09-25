# Container Apps demo

A lifelike, low-cost demo of the Microsoft 365 Analytics Insights **web portal** on Azure Container
Apps, for showing what the product measures without a real tenant's data or a full installation.

It is a demo, not an installation. There are no importers: the portal's one database is emptied and
rebuilt every night from the synthetic Contoso tenant that `Tests.FakeDataGen` generates, so the
portal always shows recent, realistic activity. Sign-in is real - the portal uses its own Microsoft
Entra ID app registration exactly as a production deployment does.

```text
 Browser ──https──► Container app  <prefix>-portal        (web portal, scales to zero)
                          │  Entra ID sign-in (OpenID Connect, app registration)
                          │  user-assigned managed identity
                          ▼  (private endpoint in the environment's virtual network)
                    Azure SQL  ContosoDemo_Portal         (free offer, serverless, auto-pauses)
                          ▲
                          │  00:00 UTC daily: empty + rebuild
                    Container Apps job  <prefix>-datagen  (Tests.FakeDataGen demo --recreate)
```

Both apps run one image, built from this repository's `.NET 10` (`net10`) branch, as one user-assigned
managed identity that is the SQL server's Microsoft Entra admin. There is no password anywhere except
the portal's own client secret, which is held as a Container Apps secret.

By default the SQL server has **no public endpoint**: the demo creates a virtual network, integrates
its Container Apps environment with it and reaches SQL through a private endpoint. That is what
subscriptions governed by an Azure Policy that switches public network access off on every new SQL
server require: there, a public-endpoint deployment fails with *"Unable to create or modify firewall
rules when public network interface for the server is disabled"*. Where no such policy applies,
`sql.networkAccess: public` is cheaper and works in any existing environment.

## What it costs

| Resource | Tier | Cost |
|---|---|---|
| Azure SQL database | [Free offer](https://learn.microsoft.com/azure/azure-sql/database/free-offer) (serverless, 2 vCores, 32 GB, pauses after 15 idle minutes) | Nothing within 100,000 vCore-seconds a month |
| Container app (portal) | Consumption, scale to zero | Normally within the monthly Container Apps free grant |
| Container Apps job (data) | Consumption, one ten-minute run a day | Normally within the free grant |
| Container registry | Basic | About USD 5 a month |
| Private networking (`sql.networkAccess: private`, the default) | The environment's managed load balancer and two public IPs, the SQL private endpoint, a private DNS zone | About USD 33 a month - the price of a SQL server without a public endpoint |
| Log Analytics (new environment only) | Pay-as-you-go, capped at 1 GB a day | Pennies |

So roughly USD 38 a month with private SQL access and USD 5 without. Only the registry and the private
networking are fixed costs; everything else is used on demand.

**The SQL allowance.** At the default size (500 users, 120 days: about 1.25 million rows) the nightly
rebuild takes about ten minutes, and the database then idles for its 15-minute pause delay: together
about 40,000 of the month's 100,000 free vCore-seconds. The rest covers roughly fifteen to twenty hours
of the portal being used. Past that, `sql.freeLimitExhaustionBehavior` decides:

- `BillOverUsage` (the default) keeps the demo up and bills the excess at the normal serverless rate -
  cents for a busy month. The one way to run up a real bill is to keep the portal busy around the
  clock: the Health page refreshes itself every minute, so do not leave it open.
- `AutoPause` caps the SQL cost at zero, but stops the database until the next month once the
  allowance is gone - the demo is then down - and Azure fixes the pause delay at 60 minutes, which
  roughly doubles what the nightly rebuild uses. Switching back to `BillOverUsage` and re-running the
  script brings a paused database back at once.

`web.minReplicas: 1` keeps the portal warm - no cold start, no second sign-in after an idle spell - for
roughly USD 20 a month at the default container size.

## Deploying

Prerequisites:

- PowerShell 7 and git. Nothing is built locally: the image is built in the registry by ACR Tasks.
- The Azure CLI, signed in to the target tenant: `az login --tenant <tenant-id>`. The script names
  its subscription on every call and never changes the CLI's default subscription.
- **Owner** (or Contributor plus User Access Administrator) on the subscription - it assigns AcrPull
  to the managed identity.
- Permission to create an app registration and **grant admin consent** in Microsoft Entra ID (for
  example Global Administrator, or Cloud Application Administrator plus Privileged Role
  Administrator). The portal asks for `ChannelMessage.Read.All`, which only an administrator can
  consent to. Without the consent nobody can sign in, and the script warns rather than fails.

```powershell
cd infra/ContainerAppsDemo
Copy-Item demo.environment.example.json demo.environment.json   # gitignored, like every *.environment.json
# edit demo.environment.json: subscriptionId, tenantId, location, resourceGroupName, namePrefix
./Deploy-ContainerAppsDemo.ps1
```

The first run takes about 25 minutes: 5 to 10 for the environment, its network and the database,
about 5 for the image build, and about 10 for the first data generation, which the script waits for so
the portal has data when it finishes. It then prints the portal's address. Re-running it updates
everything in place (a few minutes, plus the build) and never deletes anything.

| Switch | Effect |
|---|---|
| `-EnvironmentFile <path>` | Use another environment file (default `demo.environment.json`). |
| `-SkipImageBuild` | Redeploy the image already running, e.g. to change only settings. |
| `-Image <registry>/m365analytics-demo:<tag>` | Deploy an image already in the demo's registry instead of building one. |
| `-RegenerateData` | Run the data job now instead of waiting for tonight. |
| `-RotateClientSecret` | Issue a new client secret even though the current one is still valid. |
| `-NoWait` | Start the data job but do not wait for it. |

What the script does, in order:

1. Checks the sign-in and registers the resource providers it needs.
2. Creates or updates the resource group.
3. Finds the Container Apps environment, or decides to create one (`containerAppsEnvironment`). For
   an existing environment it did not create, with private SQL access, it finds the environment's
   virtual network, a subnet there for the SQL private endpoint, and the private DNS zone that
   network already resolves `privatelink.database.windows.net` from.
4. Checks that Azure SQL accepts new servers in `sql.location` for this subscription, then deploys
   `infra.bicep`: managed identity, registry, Microsoft Entra-only SQL server and database, the SQL
   private endpoint and its DNS, and the environment with its Log Analytics workspace and virtual
   network when it is new.
5. Builds the image with ACR Tasks from the repository's source (tracked and untracked-but-not-ignored
   files only, so nothing gitignored - local settings, the environment file - is ever uploaded).
6. Creates or updates the app registration: web redirect URIs, ID-token issuance for the portal's
   `code id_token` sign-in, the Microsoft Graph delegated permissions it requests, and admin consent.
   The registration is found by `entraApp.displayName`, or else by already redirecting to this
   portal, in which case it is renamed. The client secret is kept while it has more than 30 days
   left; otherwise a new one is issued and the old one removed.
7. With `web.customDomain`, creates its DNS records (or asks you to) and waits for public DNS.
8. Deploys `apps.bicep`: the portal container app and the scheduled job. For a new custom domain it
   then requests the managed certificate, waits for it and deploys again to bind it.
9. Runs the job and waits for it if it has never completed a run (the first deployment, say), or
   with `-RegenerateData`.
10. Checks that the portal answers an anonymous request by redirecting to Microsoft Entra ID with its
    own `https` callback - at the custom domain, when there is one.

### Deploying into an existing environment

Set `containerAppsEnvironment.name` (and `.resourceGroupName`, if it is elsewhere) to an environment
in the same subscription. The portal and the job are added to it; the environment itself is not
modified - apart from holding the managed certificate of a [custom domain](#a-custom-domain) - and
several demo instances - each with its own `namePrefix`, resource group and app registration - can
share one.

- With `sql.networkAccess: public` any environment will do.
- With `sql.networkAccess: private` (the default) the environment must be **integrated with a virtual
  network**, because that is the only way its apps can reach a private endpoint - and an environment
  cannot be added to one after it is created. The endpoint goes into a subnet of that network that is
  not delegated to a service: the script uses the only such subnet, or `sql.privateEndpointSubnetId`
  when there is more than one (or none yet - a `/28` is plenty). The endpoint's name is published in
  the `privatelink.database.windows.net` zone that network already resolves from, anywhere in the
  subscription; `sql.privateDnsZoneId` names one in another subscription, such as a hub's. When there
  is none, the demo creates a zone and links it to the network with *fallback to internet*, so a name
  it does not hold still resolves publicly and nothing else in that network is affected.

### A custom domain

Set `web.customDomain` to a host name in a domain you own - `analyticsdemo.contoso.com`, say - and the
portal answers there too, over HTTPS, with a free certificate that Container Apps issues and renews
itself. The default `*.azurecontainerapps.io` address keeps working, and both are registered as
redirect URIs on the app registration, so sign-in works from either.

Container Apps needs two DNS records for it:

| Record | Name | Value |
|---|---|---|
| CNAME | `analyticsdemo.contoso.com` | the portal's default address, `<appName>.<environment default domain>` |
| TXT | `asuid.analyticsdemo.contoso.com` | the environment's custom domain verification ID |

When the domain's zone is in **Azure DNS in the same subscription**, the script creates both itself: it
finds the most specific zone the name belongs to (or uses `web.dnsZoneId`), writes the records with a
five-minute TTL, and tags them as the demo's. It refuses to take over a name that already points
somewhere else. When the zone is hosted anywhere else, the script prints the two records and stops;
create them with your DNS provider and run it again.

It then waits for public DNS to show the records, adds the host name to the portal, requests the
managed certificate - validated through the CNAME, usually issued within a few minutes - and binds it.
On later runs the issued certificate is simply reused. Only subdomains are supported, not a zone apex,
because an apex cannot hold a CNAME. A certificate for the domain is added to the environment, which
for an existing environment is the one change the demo makes to it.

Removing `web.customDomain` unbinds the name on the next run; the DNS records and the certificate are
left for you to delete, like everything else the script creates.

### Environment file

See [`demo.environment.example.json`](demo.environment.example.json); every field except the first
five has a default.

| Field | Default | Notes |
|---|---|---|
| `subscriptionId`, `tenantId` | - | Required. |
| `location` | - | Region for new resources. An existing environment keeps its own region; the apps follow it. |
| `resourceGroupName` | - | Created if missing. |
| `namePrefix` | - | 3-16 lower-case letters, digits and hyphens. Derives every other name. |
| `containerAppsEnvironment.name` / `.resourceGroupName` | `<prefix>-env` / the demo group | A missing environment is created, but only in the demo's own group; an existing one is deployed into. See [Deploying into an existing environment](#deploying-into-an-existing-environment). |
| `web.appName` | `<prefix>-portal` | The default address is `https://<appName>.<environment default domain>`. |
| `web.minReplicas` | `0` | `1` keeps it always on. |
| `web.customDomain` | none | A friendlier address, such as `analyticsdemo.contoso.com`. See [A custom domain](#a-custom-domain). |
| `web.dnsZoneId` | found automatically | The Azure DNS zone to put the custom domain's records in, when the script should not pick it. |
| `entraApp.displayName` | `Microsoft 365 Analytics Insights demo (<prefix>)` | Found by name, so it must be unique to this demo instance: two instances sharing a registration would overwrite each other's redirect URIs and client secret. |
| `sql.databaseName` | `ContosoDemo_Portal` | Must start with `ContosoDemo_`: the generator resets no other database. |
| `sql.location` | `location` | Many subscriptions cannot create SQL servers in every region (`RegionDoesNotAllowProvisioning`), and not every region has the free offer. The script checks the region's SQL capabilities before it deploys anything and says so; pick a nearby region here and the apps stay where they are. |
| `sql.networkAccess` | `private` | `private`: no public SQL endpoint, only a private endpoint in the environment's virtual network. `public`: the public endpoint, open to Azure services. |
| `sql.privateEndpointSubnetId`, `sql.privateDnsZoneId` | found automatically | Private access with an existing environment only; see above. |
| `sql.useFreeOffer` | `true` | `false` uses Basic (5 DTU, about USD 5 a month, never pauses). A subscription can hold a limited number of free-offer databases. |
| `demoData.schedule` | `0 0 * * *` | Cron, evaluated in **UTC** by Container Apps. |
| `demoData.users`, `.days`, `.seed`, `.copilotPercent`, `.areas` | generator defaults | Passed to `demo` as `--users` etc. `extraArguments` are passed verbatim. |

## The nightly data job

The job runs:

```text
dotnet /app/datagen/Tests.FakeDataGen.dll demo --connection-string "<the portal's database>" --recreate [demoData arguments]
```

`--recreate` rebuilds the database from scratch. Against a `--connection-string` target it **empties
the database in place** - every table, view, procedure, function, type and schema, the migration
history included - and then rebuilds it through the product's own migrations, exactly as for a
brand-new database. It does not drop the Azure resource, because that would lose the free-offer SKU,
its auto-pause setting and the managed identity's access.

Its safety rules are the generator's own: the database must be named `ContosoDemo_*`, and it must
either carry the generator's synthetic-demo marker or be completely empty. Anything else is refused
unchanged. See [`Tests.FakeDataGen`](../../src/AnalyticsEngine/Tests.FakeDataGen/README.md).

The data's end date is the run's UTC date, so the portal's default reporting periods always cover the
last few days. With a fixed `seed` every night produces the same population and shape, shifted forward
by a day.

While the job runs - about ten minutes at the default size - the portal shows errors. The schedule puts
that at midnight UTC.

## Differences from a real deployment

- **No importers.** Nothing reads Microsoft 365. Pages that report on import runs or on components the
  demo does not deploy - Redis, Service Bus, App Insights, AI Language - show them as not configured.
  Teams deep analytics needs Redis and stays unavailable.
- **Cold starts.** With `minReplicas: 0` the portal stops after a few idle minutes; the next visit waits
  for it to start and signs in again, because the keys protecting sign-in cookies live in the
  container. A paused database adds about a minute to the first request after it pauses.
- **Linux.** Container Apps runs Linux containers, so the image is built with
  `-p:AnalyticsTargetOS=linux`, which retargets the `net10.0-windows` projects to `net10.0` (see
  `src/AnalyticsEngine/Directory.Build.props`). A Linux build ignores the Windows-only NLS globalization
  setting the importers depend on, which is one reason the importers are not part of this demo.

## Troubleshooting

```powershell
# Portal log
az containerapp logs show --subscription <id> -g <resource group> -n <prefix>-portal --type console
# Data job runs, and one run's log
az containerapp job execution list --subscription <id> -g <resource group> -n <prefix>-datagen -o table
az containerapp job logs show --subscription <id> -g <resource group> -n <prefix>-datagen --execution <name> --container datagen
```

(`az containerapp` needs the CLI extension: `az extension add -n containerapp`. The deployment script
itself does not.)

| Symptom | Cause |
|---|---|
| `This subscription cannot create an Azure SQL server in '<region>'` before anything is deployed | The subscription is restricted in that region (the portal reports it as `RegionDoesNotAllowProvisioning`: *"Location '...' is not accepting creation of new Windows Azure SQL Database servers at this time"*). Set `sql.location` to a nearby region. |
| `... cannot use: it is not integrated with a virtual network` | Private SQL access through an existing environment that has no virtual network. Let the demo create its own environment, or use `sql.networkAccess: public` where policy allows it. |
| `The SQL private endpoint needs a subnet there ...` | The existing environment's network has no subnet that can hold a private endpoint, or several. Add one (a `/28` is plenty), or set `sql.privateEndpointSubnetId`. |
| `... already has a CNAME record that points somewhere else` | The custom domain's name is in use. Remove that record, or choose another `web.customDomain`. |
| `The managed certificate for ... was not issued` | Usually a CAA record on the domain that does not allow DigiCert, which issues Container Apps' managed certificates, or a CNAME that no longer points at the portal. The portal stays reachable at its default address; fix the cause and run the script again. |
| `AADSTS65001` / "Need admin approval" at sign-in | Admin consent was not granted. Re-run the script as an administrator, or grant consent to the app registration in the Entra admin center. |
| `AADSTS50011` redirect URI mismatch | The portal's address changed (new environment or app name). Re-run the script; it rewrites the redirect URIs. |
| Job fails with `Refusing to --recreate ContosoDemo_...` | The database holds objects but no synthetic-demo marker. Only point the demo at a database it created. |
| Job fails with `Login failed for user '<token-identified principal>'` | The managed identity is not the SQL server's Entra admin - the server was created by something else. |
| Portal shows data errors for about ten minutes after 00:00 UTC | The nightly rebuild. |

## Removing it

```powershell
az group delete --subscription <id> --name <resource group>
```

then delete the app registration (`entraApp.displayName`) in the Microsoft Entra admin center, and a
custom domain's two DNS records (tagged with the demo's `DemoInstance`) from their zone. The
portal, the job and the SQL private endpoint always live in the demo's resource group, so they go with
it - including from an existing environment they were deployed into, which is in its own group and is
left alone, as is the DNS zone it already used. If other demo instances were deployed into *this*
demo's environment, remove them first: deleting this group deletes the environment they run in.
