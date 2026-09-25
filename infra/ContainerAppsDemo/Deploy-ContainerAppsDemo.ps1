#requires -Version 7.0
<#
.SYNOPSIS
    Deploys, or updates, the Azure Container Apps demo of the Microsoft 365 Analytics Insights portal.

.DESCRIPTION
    A lifelike demo, not an installation. Into a new or an existing Container Apps environment it
    deploys:

      * the web portal - the .NET 10 build, signed in with Microsoft Entra ID through its own app
        registration exactly as a real deployment is - reading one Azure SQL database, and
      * a scheduled Container Apps job that empties that database every night and rebuilds it from
        Tests.FakeDataGen's synthetic Contoso tenant, so the portal always shows recent activity.

    There are no importers, and no Redis, Service Bus, Storage, Application Insights, Key Vault or
    AI Language: the portal needs none of them when its data is generated rather than imported.

    Everything is described by one environment file (copy demo.environment.example.json to
    demo.environment.json, which is gitignored). Re-running the script updates what exists and
    creates what does not; it never deletes anything.

    The image is built in the registry by ACR Tasks from the committed and working-tree source, so
    nothing but PowerShell, git and the Azure CLI is needed locally.

.PARAMETER EnvironmentFile
    The environment file. Defaults to demo.environment.json next to this script.

.PARAMETER SkipImageBuild
    Redeploy the image the portal is already running instead of building a new one.

.PARAMETER Image
    Deploy this image (a full reference in the demo's registry) instead of building one.

.PARAMETER RegenerateData
    Run the data job now, after deploying, rather than waiting for its schedule. It also runs whenever
    the job has never completed a run - on the first deployment, for instance - so the portal has data
    straight away.

.PARAMETER RotateClientSecret
    Issue a new client secret for the portal's app registration, even though the current one is valid.

.PARAMETER NoWait
    Start the data job but do not wait for it to finish.

.EXAMPLE
    ./Deploy-ContainerAppsDemo.ps1

.EXAMPLE
    ./Deploy-ContainerAppsDemo.ps1 -EnvironmentFile ./contoso.environment.json -RegenerateData
#>
[CmdletBinding()]
param(
    [string] $EnvironmentFile = (Join-Path $PSScriptRoot 'demo.environment.json'),

    [switch] $SkipImageBuild,

    [string] $Image,

    [switch] $RegenerateData,

    [switch] $RotateClientSecret,

    [switch] $NoWait
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:ImageRepository = 'm365analytics-demo'
$script:WorkloadTag = 'M365AnalyticsContainerAppsDemo'
# Azure public cloud, as the rest of the product is: <server>.database.windows.net.
$script:SqlHostnameSuffix = '.database.windows.net'
$script:ClientSecretName = 'entra-client-secret'
$script:ClientSecretDisplayName = 'Container Apps demo portal'
$script:ContainerAppsApiVersion = '2024-03-01'
$script:GraphAppId = '00000003-0000-0000-c000-000000000000'
# Delegated Microsoft Graph permissions the portal's sign-in asks for (Web/Program.cs): the OpenID
# Connect basics plus the two Teams scopes it requests for Teams deep analytics.
$script:GraphScopes = @('openid', 'email', 'profile', 'offline_access', 'User.Read', 'Team.ReadBasic.All', 'ChannelMessage.Read.All')

#region Output and Azure CLI helpers

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Detail([string] $Message) {
    Write-Host "    $Message"
}

# Every call names its subscription explicitly. The Azure CLI's default subscription is shared by
# everything else on this machine, so the script never changes it with `az account set`.
function Invoke-Az {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [switch] $AsJson
    )

    $errorFile = New-TemporaryFile
    try {
        $output = & az @Arguments --only-show-errors 2> $errorFile.FullName
        if ($LASTEXITCODE -ne 0) {
            $errorText = (Get-Content -LiteralPath $errorFile.FullName -Raw)
            throw "Azure CLI failed: az $($Arguments[0..([Math]::Min(2, $Arguments.Count - 1))] -join ' ') ...`n$errorText"
        }
        $text = ($output -join [Environment]::NewLine).Trim()
        if ($AsJson) {
            if ([string]::IsNullOrWhiteSpace($text)) { return $null }
            return $text | ConvertFrom-Json -Depth 50
        }
        return $text
    }
    finally {
        Remove-Item -LiteralPath $errorFile.FullName -Force -ErrorAction SilentlyContinue
    }
}

$script:Tokens = @{}

# Tokens are fetched for the demo's subscription for the same reason: `az rest` and the `az ad`
# commands act in whichever tenant is the CLI's default. `--subscription` also picks the account that
# is signed in to that subscription; `--tenant` would ask the CLI's current account instead, which can
# belong to another tenant and then blocks on an interactive sign-in.
function Get-Token([ValidateSet('arm', 'graph')] [string] $Audience) {
    $cached = $script:Tokens[$Audience]
    if ($cached -and $cached.ExpiresOn -gt (Get-Date).AddMinutes(5)) { return $cached.Token }

    $resourceType = if ($Audience -eq 'arm') { 'arm' } else { 'ms-graph' }
    $token = Invoke-Az -Arguments @('account', 'get-access-token', '--subscription', $script:Config.SubscriptionId, '--resource-type', $resourceType, '--output', 'json') -AsJson
    if ($token.tenant -ne $script:Config.TenantId) {
        throw "The Azure CLI returned a token for tenant $($token.tenant), not $($script:Config.TenantId)."
    }
    $script:Tokens[$Audience] = @{
        Token     = $token.accessToken
        ExpiresOn = [DateTimeOffset]::FromUnixTimeSeconds([long]$token.expires_on).LocalDateTime
    }
    return $token.accessToken
}

function Invoke-Api {
    param(
        [Parameter(Mandatory)] [ValidateSet('arm', 'graph')] [string] $Audience,
        [Parameter(Mandatory)] [ValidateSet('GET', 'POST', 'PATCH', 'PUT', 'DELETE')] [string] $Method,
        [Parameter(Mandatory)] [string] $Uri,
        [object] $Body,
        [hashtable] $Headers = @{},
        [switch] $AllowNotFound
    )

    $baseUri = if ($Audience -eq 'arm') { 'https://management.azure.com' } else { 'https://graph.microsoft.com/v1.0' }
    $request = @{
        Method  = $Method
        Uri     = if ($Uri -match '^https://') { $Uri } else { "$baseUri$Uri" }
        Headers = @{ Authorization = "Bearer $(Get-Token $Audience)" } + $Headers
    }
    if ($null -ne $Body) {
        $request.Body = ($Body | ConvertTo-Json -Depth 30 -Compress)
        $request.ContentType = 'application/json'
    }
    elseif ($Method -eq 'POST') {
        # ARM action endpoints (listSecrets, start) answer a body-less POST with 415.
        $request.Body = '{}'
        $request.ContentType = 'application/json'
    }

    try {
        return Invoke-RestMethod @request
    }
    catch {
        $status = $null
        if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        if ($AllowNotFound -and $status -eq 404) { return $null }
        $detail = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        throw "$Method $($request.Uri) failed ($status): $detail"
    }
}

#endregion

#region Configuration

function Get-Setting {
    param([object] $Object, [string] $Path, [object] $Default = $null)

    $current = $Object
    foreach ($name in $Path.Split('.')) {
        if ($null -eq $current -or -not ($current.PSObject.Properties.Name -contains $name)) { return $Default }
        $current = $current.$name
    }
    if ($null -eq $current -or ($current -is [string] -and [string]::IsNullOrWhiteSpace($current))) { return $Default }
    return $current
}

function Read-DemoEnvironment([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        $example = Join-Path $PSScriptRoot 'demo.environment.example.json'
        throw "Environment file '$Path' was not found. Copy '$example' to '$Path' and fill it in; it is gitignored."
    }

    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 20
    $guid = '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'

    $prefix = Get-Setting $json 'namePrefix'
    if ($prefix -notmatch '^[a-z][a-z0-9-]{2,15}$') {
        throw "namePrefix must be 3-16 characters: lower-case letters, digits and hyphens, starting with a letter."
    }
    $config = [ordered]@{
        SubscriptionId = Get-Setting $json 'subscriptionId'
        TenantId       = Get-Setting $json 'tenantId'
        Location       = Get-Setting $json 'location'
        ResourceGroup  = Get-Setting $json 'resourceGroupName'
        NamePrefix     = $prefix
        Tags           = Get-Setting $json 'tags' ([pscustomobject]@{})
    }
    if ($config.SubscriptionId -notmatch $guid) { throw 'subscriptionId must be a subscription GUID.' }
    if ($config.TenantId -notmatch $guid) { throw 'tenantId must be the Microsoft Entra tenant GUID.' }
    if (-not $config.Location) { throw 'location is required, e.g. westeurope.' }
    if (-not $config.ResourceGroup) { throw 'resourceGroupName is required.' }

    $config.EnvironmentName = Get-Setting $json 'containerAppsEnvironment.name' "$prefix-env"
    $config.EnvironmentResourceGroup = Get-Setting $json 'containerAppsEnvironment.resourceGroupName' $config.ResourceGroup

    $config.WebAppName = Get-Setting $json 'web.appName' "$prefix-portal"
    $config.JobName = Get-Setting $json 'demoData.jobName' "$prefix-datagen"
    foreach ($name in @($config.WebAppName, $config.JobName)) {
        if ($name -notmatch '^[a-z][a-z0-9-]{0,30}[a-z0-9]$' -or $name -match '--') {
            throw "'$name' is not a valid Container Apps name: 2-32 lower-case letters, digits and single hyphens, starting with a letter."
        }
    }
    $config.WebMinReplicas = [int](Get-Setting $json 'web.minReplicas' 0)
    if ($config.WebMinReplicas -notin 0, 1) { throw 'web.minReplicas must be 0 (scale to zero) or 1 (always on).' }
    $config.CustomDomain = ([string](Get-Setting $json 'web.customDomain' '')).Trim().TrimEnd('.').ToLowerInvariant()
    if ($config.CustomDomain -and $config.CustomDomain -notmatch '^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.){2,}[a-z]{2,63}$') {
        throw "web.customDomain must be a host name within a domain you own, such as analyticsdemo.contoso.com."
    }
    $config.DnsZoneId = Get-Setting $json 'web.dnsZoneId'
    if ($config.DnsZoneId -and $config.DnsZoneId -notmatch '^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\.Network/dnszones/[^/]+$') {
        throw 'web.dnsZoneId must be the full resource ID of an Azure DNS zone.'
    }

    # Per instance, because the registration is found by this name: two demos sharing one would each
    # rewrite the other's redirect URIs and delete the other's client secret.
    $config.EntraAppDisplayName = Get-Setting $json 'entraApp.displayName' "Microsoft 365 Analytics Insights demo ($prefix)"
    $config.TenantDomain = Get-Setting $json 'entraApp.tenantDomain'

    $config.SqlServerName = Get-Setting $json 'sql.serverName'
    $config.SqlLocation = Get-Setting $json 'sql.location' $config.Location
    $config.SqlDatabaseName = Get-Setting $json 'sql.databaseName' 'ContosoDemo_Portal'
    if ($config.SqlDatabaseName -cnotmatch '^ContosoDemo_[A-Za-z0-9_]{1,70}$') {
        throw 'sql.databaseName must start with ContosoDemo_ and contain only ASCII letters, digits and underscores: the data generator refuses to reset any other database.'
    }
    $config.SqlUseFreeOffer = [bool](Get-Setting $json 'sql.useFreeOffer' $true)
    $config.SqlFreeLimitExhaustionBehavior = Get-Setting $json 'sql.freeLimitExhaustionBehavior' 'BillOverUsage'
    if ($config.SqlFreeLimitExhaustionBehavior -notin 'AutoPause', 'BillOverUsage') {
        throw 'sql.freeLimitExhaustionBehavior must be AutoPause or BillOverUsage.'
    }
    $config.SqlAutoPauseDelayMinutes = [int](Get-Setting $json 'sql.autoPauseDelayMinutes' 15)
    $config.SqlNetworkAccess = Get-Setting $json 'sql.networkAccess' 'private'
    if ($config.SqlNetworkAccess -notin 'private', 'public') {
        throw 'sql.networkAccess must be private (a private endpoint in the environment''s virtual network) or public.'
    }
    $config.SqlPrivateEndpointSubnetId = Get-Setting $json 'sql.privateEndpointSubnetId'
    $config.SqlPrivateDnsZoneId = Get-Setting $json 'sql.privateDnsZoneId'
    foreach ($pair in @(@('sql.privateEndpointSubnetId', $config.SqlPrivateEndpointSubnetId, '/providers/Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$'),
            @('sql.privateDnsZoneId', $config.SqlPrivateDnsZoneId, '/providers/Microsoft.Network/privateDnsZones/[^/]+$'))) {
        if ($pair[1] -and $pair[1] -notmatch "^/subscriptions/[^/]+/resourceGroups/[^/]+$($pair[2])") {
            throw "$($pair[0]) must be a full resource ID."
        }
    }

    $config.Schedule = Get-Setting $json 'demoData.schedule' '0 0 * * *'
    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($pair in @(@('users', '--users'), @('days', '--days'), @('seed', '--seed'), @('copilotPercent', '--copilot-percent'), @('areas', '--areas'))) {
        $value = Get-Setting $json "demoData.$($pair[0])"
        if ($null -ne $value) { $arguments.Add($pair[1]); $arguments.Add([string]$value) }
    }
    foreach ($extra in @(Get-Setting $json 'demoData.extraArguments' @())) { $arguments.Add([string]$extra) }
    $config.DataGenArguments = $arguments.ToArray()

    return [pscustomobject]$config
}

#endregion

#region Azure resources

function Assert-AzureSignIn {
    try {
        $account = Invoke-Az -Arguments @('account', 'show', '--subscription', $script:Config.SubscriptionId, '--output', 'json') -AsJson
    }
    catch {
        throw "The Azure CLI cannot see subscription $($script:Config.SubscriptionId). Sign in first: az login --tenant $($script:Config.TenantId)"
    }
    if ($account.tenantId -ne $script:Config.TenantId) {
        throw "Subscription $($script:Config.SubscriptionId) belongs to tenant $($account.tenantId), not $($script:Config.TenantId)."
    }
    Write-Detail "Subscription: $($account.name)"
    Write-Detail "Signed in as: $($account.user.name)"
    # Proves a Graph token can be had for the tenant before anything is created.
    Get-Token 'graph' | Out-Null
}

function Register-Providers {
    foreach ($namespace in 'Microsoft.App', 'Microsoft.ContainerRegistry', 'Microsoft.Sql', 'Microsoft.ManagedIdentity', 'Microsoft.OperationalInsights', 'Microsoft.Network') {
        $state = Invoke-Az -Arguments @('provider', 'show', '--namespace', $namespace, '--subscription', $script:Config.SubscriptionId, '--query', 'registrationState', '--output', 'tsv')
        if ($state -ne 'Registered') {
            Write-Detail "Registering resource provider $namespace..."
            Invoke-Az -Arguments @('provider', 'register', '--namespace', $namespace, '--subscription', $script:Config.SubscriptionId, '--wait') | Out-Null
        }
    }
}

function Get-ContainerAppsEnvironment {
    $path = "/subscriptions/$($script:Config.SubscriptionId)/resourceGroups/$($script:Config.EnvironmentResourceGroup)/providers/Microsoft.App/managedEnvironments/$($script:Config.EnvironmentName)?api-version=$script:ContainerAppsApiVersion"
    return Invoke-Api -Audience arm -Method GET -Uri $path -AllowNotFound
}

# True for the environment this demo instance created: that one stays under infra.bicep's control on
# every run. Any other environment - another demo instance's included - is only deployed into.
function Test-DemoOwnsEnvironment([object] $Environment) {
    if (-not $Environment) { return $false }
    if ($script:Config.EnvironmentResourceGroup -ne $script:Config.ResourceGroup) { return $false }
    if ((Get-Setting $Environment 'tags.Workload') -ne $script:WorkloadTag) { return $false }
    $instance = Get-Setting $Environment 'tags.DemoInstance'
    if ($instance) { return $instance -eq $script:Config.NamePrefix }
    # Created before the DemoInstance tag existed: the default name identifies it.
    return $Environment.name -eq "$($script:Config.NamePrefix)-env"
}

function Get-AllPages([string] $Uri) {
    $items = [System.Collections.Generic.List[object]]::new()
    $next = $Uri
    while ($next) {
        $page = Invoke-Api -Audience arm -Method GET -Uri $next
        foreach ($item in @(Get-Setting $page 'value' @())) { if ($item) { $items.Add($item) } }
        $next = Get-Setting $page 'nextLink'
    }
    return $items.ToArray()
}

# The privatelink.database.windows.net zone, anywhere in the subscription, that already answers for
# a virtual network. A network can be linked to only one zone of a name, so an existing one - a hub
# zone, or another demo's - has to be used rather than a second one created.
function Find-LinkedSqlPrivateDnsZone([string] $VnetId) {
    $zoneName = "privatelink$($script:SqlHostnameSuffix)"
    foreach ($zone in @(Get-AllPages "/subscriptions/$($script:Config.SubscriptionId)/providers/Microsoft.Network/privateDnsZones?api-version=2020-06-01")) {
        if ($zone.name -ne $zoneName) { continue }
        foreach ($link in @(Get-AllPages "$($zone.id)/virtualNetworkLinks?api-version=2020-06-01")) {
            if ((Get-Setting $link 'properties.virtualNetwork.id') -eq $VnetId) { return $zone.id }
        }
    }
    return $null
}

# Private SQL access through an environment the demo did not create: the private endpoint goes into a
# subnet of that environment's virtual network, and its name is published in the zone that network
# already resolves privatelink names from - or in a new zone linked to it.
function Resolve-ExistingEnvironmentNetwork([object] $Environment) {
    $environmentSubnetId = Get-Setting $Environment 'properties.vnetConfiguration.infrastructureSubnetId'
    if (-not $environmentSubnetId) {
        throw "sql.networkAccess 'private' reaches SQL through a private endpoint, which the existing environment '$($Environment.name)' cannot use: it is not integrated with a virtual network, and an environment cannot be added to one after it is created. Remove containerAppsEnvironment.name to let the demo create an environment with its own network, or set sql.networkAccess to 'public' where policy allows SQL public network access."
    }
    $vnetId = $environmentSubnetId -replace '/subnets/[^/]+$', ''
    $vnet = Invoke-Api -Audience arm -Method GET -Uri "$vnetId`?api-version=2023-11-01"

    $subnetId = $script:Config.SqlPrivateEndpointSubnetId
    if ($subnetId) {
        if (-not (Invoke-Api -Audience arm -Method GET -Uri "$subnetId`?api-version=2023-11-01" -AllowNotFound)) {
            throw "sql.privateEndpointSubnetId '$subnetId' was not found."
        }
        $endpointVnet = if (($subnetId -replace '/subnets/[^/]+$', '') -eq $vnetId) { $vnet } else {
            Invoke-Api -Audience arm -Method GET -Uri "$($subnetId -replace '/subnets/[^/]+$', '')?api-version=2023-11-01"
        }
    }
    else {
        # Any subnet that can hold a private endpoint: not delegated to a service, and not one of the
        # names Azure reserves for gateways, firewalls and Bastion.
        $reserved = 'GatewaySubnet', 'AzureFirewallSubnet', 'AzureFirewallManagementSubnet', 'AzureBastionSubnet', 'RouteServerSubnet'
        $candidates = @(@(Get-Setting $vnet 'properties.subnets' @()) | Where-Object {
                $_ -and $_.id -ne $environmentSubnetId -and $_.name -notin $reserved -and
                @(Get-Setting $_ 'properties.delegations' @()).Count -eq 0
            })
        if ($candidates.Count -ne 1) {
            $listed = if ($candidates.Count) { ' Candidates: ' + (($candidates | ForEach-Object { $_.name }) -join ', ') + '.' } else { '' }
            throw "The existing environment '$($Environment.name)' is in virtual network '$($vnet.name)'. The SQL private endpoint needs a subnet there that is not delegated to a service, and $(if ($candidates.Count) { 'there is more than one' } else { 'there is none' }).$listed Set sql.privateEndpointSubnetId to the subnet's resource ID$(if (-not $candidates.Count) { ', after adding one (a /28 is plenty)' })."
        }
        $subnetId = $candidates[0].id
        $endpointVnet = $vnet
        Write-Detail "SQL private endpoint subnet: $($vnet.name)/$($candidates[0].name)."
    }

    $zoneId = $script:Config.SqlPrivateDnsZoneId
    if (-not $zoneId) { $zoneId = Find-LinkedSqlPrivateDnsZone $vnetId }
    if ($zoneId) {
        Write-Detail "SQL private DNS: the zone already linked to '$($vnet.name)' ($($zoneId -replace '^.*/resourceGroups/([^/]+)/.*$', '$1'))."
    }
    else {
        Write-Detail "SQL private DNS: a new zone linked to '$($vnet.name)', falling back to public DNS for every name it does not hold."
    }

    return @{
        privateEndpointSubnetId = $subnetId
        privateEndpointLocation = $endpointVnet.location
        privateDnsZoneId        = [string]$zoneId
        environmentVnetId       = $vnetId
    }
}

#endregion

#region Custom domain

# The Azure DNS zone that holds the custom domain: web.dnsZoneId, or the most specific zone in the
# subscription whose name the domain ends with. $null when the domain's DNS is hosted elsewhere.
function Find-CustomDomainZone([string] $Domain) {
    if ($script:Config.DnsZoneId) {
        $zone = Invoke-Api -Audience arm -Method GET -Uri "$($script:Config.DnsZoneId)?api-version=2018-05-01" -AllowNotFound
        if (-not $zone) { throw "web.dnsZoneId '$($script:Config.DnsZoneId)' was not found." }
        if (-not $Domain.EndsWith(".$($zone.name)")) { throw "web.customDomain '$Domain' is not in the zone '$($zone.name)'." }
        return $zone
    }
    return @(Get-AllPages "/subscriptions/$($script:Config.SubscriptionId)/providers/Microsoft.Network/dnszones?api-version=2018-05-01") |
        Where-Object { $Domain.EndsWith(".$($_.name)", [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object { $_.name.Length } -Descending | Select-Object -First 1
}

# Creates or updates one record set, refusing to take over a name that is already used for something
# else: a subdomain somebody else points somewhere is not the demo's to repoint.
function Set-DemoDnsRecord([object] $Zone, [string] $Type, [string] $Name, [hashtable] $Properties, [scriptblock] $IsSameTarget) {
    $uri = "$($Zone.id)/$Type/$Name`?api-version=2018-05-01"
    $existing = Invoke-Api -Audience arm -Method GET -Uri $uri -AllowNotFound
    if ($existing) {
        $ours = (Get-Setting $existing 'properties.metadata.DemoInstance') -eq $script:Config.NamePrefix
        if (-not $ours -and -not (& $IsSameTarget $existing)) {
            throw "$Name.$($Zone.name) already has a $Type record that points somewhere else. Remove it, or choose another web.customDomain."
        }
    }
    $Properties.metadata = @{ Workload = $script:WorkloadTag; DemoInstance = $script:Config.NamePrefix }
    Invoke-Api -Audience arm -Method PUT -Uri $uri -Body @{ properties = $Properties } | Out-Null
}

# The two records Container Apps needs for a subdomain: a CNAME to the app, which also lets the free
# managed certificate be validated, and the asuid TXT record that proves the domain is yours.
function Set-CustomDomainDns([string] $Domain, [string] $AppFqdn, [string] $VerificationId) {
    if (-not $VerificationId) { throw "The environment reports no custom domain verification ID, so it cannot take a custom domain." }
    $zone = Find-CustomDomainZone $Domain
    if (-not $zone) {
        Write-Detail "$Domain is not in an Azure DNS zone in this subscription. Create these records with your DNS provider, then run the script again:"
        Write-Detail "    CNAME  $Domain  ->  $AppFqdn"
        Write-Detail "    TXT    asuid.$Domain  =  $VerificationId"
        if (-not (Test-PublicDns $Domain $AppFqdn $VerificationId)) {
            throw "The DNS records for $Domain are not in place yet."
        }
        return
    }

    $name = $Domain.Substring(0, $Domain.Length - $zone.name.Length - 1)
    # A short TTL, so a later change of environment or app name takes effect quickly.
    Set-DemoDnsRecord -Zone $zone -Type 'CNAME' -Name $name -Properties @{ TTL = 300; CNAMERecord = @{ cname = $AppFqdn } } -IsSameTarget {
        param($record) ([string](Get-Setting $record 'properties.CNAMERecord.cname')).TrimEnd('.') -eq $AppFqdn
    }.GetNewClosure()
    Set-DemoDnsRecord -Zone $zone -Type 'TXT' -Name "asuid.$name" -Properties @{ TTL = 300; TXTRecords = @(@{ value = @($VerificationId) }) } -IsSameTarget {
        param($record) [bool](@(Get-Setting $record 'properties.TXTRecords' @()) | ForEach-Object { $_.value } | Where-Object { $_ -eq $VerificationId })
    }.GetNewClosure()
    Write-Detail "DNS: $name CNAME $AppFqdn, and its asuid TXT record, in zone $($zone.name) ($($zone.id -replace '^.*/resourceGroups/([^/]+)/.*$', '$1'))."

    # Container Apps checks both records through public DNS when the host name is added, so wait until
    # the internet sees them. Azure DNS answers straight away; this normally takes seconds.
    for ($attempt = 1; -not (Test-PublicDns $Domain $AppFqdn $VerificationId); $attempt++) {
        if ($attempt -ge 20) { throw "Public DNS still does not show the records for $Domain after 5 minutes. Check that the zone is delegated to Azure DNS." }
        Start-Sleep -Seconds 15
    }
}

function Test-PublicDns([string] $Domain, [string] $AppFqdn, [string] $VerificationId) {
    $cname = @(Resolve-PublicDns $Domain 'CNAME') | ForEach-Object { $_.TrimEnd('.') }
    $txt = @(Resolve-PublicDns "asuid.$Domain" 'TXT') | ForEach-Object { $_.Trim('"') }
    return ($cname -contains $AppFqdn) -and ($txt -contains $VerificationId)
}

# DNS over HTTPS against public resolvers: what the internet - and so Container Apps - sees, from any
# platform the script runs on. More than one, because networks that inspect TLS often block one of them.
function Resolve-PublicDns([string] $Name, [ValidateSet('CNAME', 'TXT')] [string] $Type) {
    $code = @{ CNAME = 5; TXT = 16 }[$Type]
    foreach ($resolver in 'https://dns.google/resolve', 'https://cloudflare-dns.com/dns-query') {
        try {
            $answer = Invoke-RestMethod -Uri "$($resolver)?name=$([uri]::EscapeDataString($Name))&type=$Type" `
                -Headers @{ Accept = 'application/dns-json' } -TimeoutSec 15
        }
        catch {
            continue
        }
        return @(@(Get-Setting $answer 'Answer' @()) | Where-Object { $_ -and $_.type -eq $code } | ForEach-Object { [string]$_.data })
    }
    return @()
}

function Get-ManagedCertificateName([string] $Domain) {
    $name = 'mc-' + ($Domain -replace '[^a-z0-9]+', '-')
    return $name.Substring(0, [Math]::Min(60, $name.Length)).TrimEnd('-')
}

# The environment's issued certificate for the domain, if there is one - under any name, so a
# certificate created by hand or by the portal is reused rather than duplicated.
function Get-ManagedCertificateId([object] $Environment, [string] $Domain) {
    $certificates = @(Get-AllPages "$($Environment.id)/managedCertificates?api-version=$script:ContainerAppsApiVersion")
    $issued = $certificates | Where-Object {
        (Get-Setting $_ 'properties.subjectName') -eq $Domain -and (Get-Setting $_ 'properties.provisioningState') -eq 'Succeeded'
    } | Select-Object -First 1
    if ($issued) { return $issued.id }
    return ''
}

# Asks Container Apps for its free managed certificate, validated through the CNAME record, and waits
# for it to be issued. The host name must already be on the app (unbound) for validation to succeed.
function New-ManagedCertificate([object] $Environment, [string] $Domain) {
    $uri = "$($Environment.id)/managedCertificates/$(Get-ManagedCertificateName $Domain)?api-version=$script:ContainerAppsApiVersion"
    $existing = Invoke-Api -Audience arm -Method GET -Uri $uri -AllowNotFound
    if (-not $existing -or (Get-Setting $existing 'properties.provisioningState') -in 'Failed', 'Canceled') {
        if ($existing) {
            Write-Detail "Replacing a certificate request that failed: $(Get-Setting $existing 'properties.error' 'no reason given')."
            Invoke-Api -Audience arm -Method DELETE -Uri $uri | Out-Null
            Start-Sleep -Seconds 10
        }
        Invoke-Api -Audience arm -Method PUT -Uri $uri -Body @{
            location   = $Environment.location
            tags       = $script:Tags
            properties = @{ subjectName = $Domain; domainControlValidation = 'CNAME' }
        } | Out-Null
    }

    Write-Detail "Waiting for the managed certificate for $Domain (usually a few minutes)..."
    $started = Get-Date
    while ($true) {
        $certificate = Invoke-Api -Audience arm -Method GET -Uri $uri
        $state = Get-Setting $certificate 'properties.provisioningState'
        if ($state -eq 'Succeeded') {
            Write-Detail "Certificate issued in $([int]((Get-Date) - $started).TotalMinutes) min."
            return $certificate.id
        }
        if ($state -in 'Failed', 'Canceled') {
            throw "The managed certificate for $Domain was not issued: $(Get-Setting $certificate 'properties.error' $state). The portal is still reachable at its default address."
        }
        if (((Get-Date) - $started).TotalMinutes -gt 30) {
            throw "The managed certificate for $Domain is still '$state' after 30 minutes. Run the script again later; it picks the certificate up once issued."
        }
        Start-Sleep -Seconds 20
    }
}

#endregion

#region Azure resources (continued)

# Many subscriptions - internal and trial ones especially - cannot create SQL servers in every region,
# and not every region has the free offer. The deployment would only find out after it has created
# everything else ("Location '...' is not accepting creation of new Windows Azure SQL Database
# servers at this time", "Provisioning of free limit database is not supported for provided service
# level objective or region"). The capabilities API says so up front. Skipped once the database exists.
function Assert-SqlRegionSupportsTheDemo {
    $servers = Invoke-Api -Audience arm -Method GET -Uri "/subscriptions/$($script:Config.SubscriptionId)/resourceGroups/$($script:Config.ResourceGroup)/providers/Microsoft.Sql/servers?api-version=2023-08-01-preview" -AllowNotFound
    foreach ($server in @($servers.value)) {
        if (-not $server) { continue }
        $existing = Invoke-Api -Audience arm -Method GET -Uri "$($server.id)/databases/$($script:Config.SqlDatabaseName)?api-version=2023-08-01-preview" -AllowNotFound
        if ($existing) { return }
    }

    $location = $script:Config.SqlLocation
    $capabilities = Invoke-Api -Audience arm -Method GET -Uri "/subscriptions/$($script:Config.SubscriptionId)/providers/Microsoft.Sql/locations/$location/capabilities?api-version=2023-08-01-preview&include=supportedEditions"
    if ($capabilities.status -notin 'Available', 'Default') {
        throw "This subscription cannot create an Azure SQL server in '$location': $($capabilities.reason)`nSet sql.location in the environment file to another region (the apps can stay where they are), or location to move everything."
    }
    if ($script:Config.SqlUseFreeOffer) {
        $serverless = foreach ($version in @($capabilities.supportedServerVersions)) {
            foreach ($edition in @($version.supportedEditions) | Where-Object name -eq 'GeneralPurpose') {
                @($edition.supportedServiceLevelObjectives) | Where-Object name -eq 'GP_S_Gen5_2'
            }
        }
        $serverless = @($serverless) | Select-Object -First 1
        if (-not $serverless -or -not ($serverless.PSObject.Properties.Name -contains 'supportedFreeLimitExhaustionBehaviors')) {
            throw "The Azure SQL Database free offer is not available in '$location'. Set sql.location to a region that has it (for example francecentral, westeurope or swedencentral), or sql.useFreeOffer to false for Basic."
        }
    }
    Write-Detail "Azure SQL accepts new servers$(if ($script:Config.SqlUseFreeOffer) { ' and the free offer' }) in $location."
}

function Invoke-Deployment {
    param([string] $Name, [string] $TemplateFile, [hashtable] $Parameters)

    $parameterFile = Join-Path ([IO.Path]::GetTempPath()) "m365ai-demo-$([guid]::NewGuid().ToString('N')).json"
    try {
        $document = @{
            '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
            contentVersion = '1.0.0.0'
            parameters     = @{}
        }
        foreach ($key in $Parameters.Keys) { $document.parameters[$key] = @{ value = $Parameters[$key] } }
        # The file can hold the client secret, so it lives only for the length of the deployment.
        [IO.File]::WriteAllText($parameterFile, ($document | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))

        $result = Invoke-Az -Arguments @(
            'deployment', 'group', 'create',
            '--subscription', $script:Config.SubscriptionId,
            '--resource-group', $script:Config.ResourceGroup,
            '--name', $Name,
            '--template-file', $TemplateFile,
            '--parameters', "@$parameterFile",
            '--output', 'json'
        ) -AsJson
        return $result.properties.outputs
    }
    finally {
        Remove-Item -LiteralPath $parameterFile -Force -ErrorAction SilentlyContinue
    }
}

function Get-ContainerAppSecret([string] $AppName, [string] $SecretName) {
    $base = "/subscriptions/$($script:Config.SubscriptionId)/resourceGroups/$($script:Config.ResourceGroup)/providers/Microsoft.App/containerApps/$AppName"
    $app = Invoke-Api -Audience arm -Method GET -Uri "$base`?api-version=$script:ContainerAppsApiVersion" -AllowNotFound
    if (-not $app) { return $null }
    $secrets = Invoke-Api -Audience arm -Method POST -Uri "$base/listSecrets?api-version=$script:ContainerAppsApiVersion"
    $match = @($secrets.value) | Where-Object { $_ -and $_.name -eq $SecretName } | Select-Object -First 1
    if ($match) { return $match.value }
    return $null
}

function Get-DeployedImage([string] $AppName) {
    $path = "/subscriptions/$($script:Config.SubscriptionId)/resourceGroups/$($script:Config.ResourceGroup)/providers/Microsoft.App/containerApps/$AppName`?api-version=$script:ContainerAppsApiVersion"
    $app = Invoke-Api -Audience arm -Method GET -Uri $path -AllowNotFound
    if (-not $app) { return $null }
    $container = @($app.properties.template.containers) | Select-Object -First 1
    if ($container) { return $container.image }
    return $null
}

#endregion

#region Image build

function New-BuildContext {
    $repositoryRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'This script must run from a git clone of the repository.' }

    $context = Join-Path ([IO.Path]::GetTempPath()) "m365ai-demo-context-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $context | Out-Null

    # Tracked plus untracked-but-not-ignored files: exactly the source git would commit, including
    # uncommitted edits. bin/obj/node_modules and every gitignored file - local settings, user
    # secrets, the environment file itself - are never uploaded. NUL-separated and unquoted, so a
    # file name with spaces or non-ASCII characters arrives intact.
    $previousEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $listing = (& git -C $repositoryRoot -c core.quotePath=false ls-files -z --cached --others --exclude-standard -- .nvmrc build src/AnalyticsEngine ':(glob)src/*.sql') -join ''
        if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
    }
    finally {
        [Console]::OutputEncoding = $previousEncoding
    }
    $files = @($listing.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    foreach ($file in $files) {
        $source = Join-Path $repositoryRoot $file
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }   # deleted in the working tree
        $target = Join-Path $context $file
        $directory = Split-Path $target -Parent
        if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
        Copy-Item -LiteralPath $source -Destination $target
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Dockerfile') -Destination (Join-Path $context 'Dockerfile')

    $commit = (& git -C $repositoryRoot rev-parse --short=8 HEAD).Trim()
    $dirty = [bool](& git -C $repositoryRoot status --porcelain -- src/AnalyticsEngine build .nvmrc)
    # The Node major the portal must be built with lives in one place, /.nvmrc (see build/NodeVersion.targets).
    $nodeMajor = [regex]::Match((Get-Content -LiteralPath (Join-Path $repositoryRoot '.nvmrc') -Raw), '[0-9]+').Value
    if (-not $nodeMajor) { throw 'Could not read the Node.js major version from .nvmrc.' }
    return [pscustomobject]@{
        Path      = $context
        Files     = @($files).Count
        Tag       = "$(Get-Date -Format 'yyyyMMdd-HHmmss')-$commit$(if ($dirty) { '-dirty' })"
        NodeMajor = $nodeMajor
    }
}

function Build-Image([string] $RegistryName, [string] $RegistryServer) {
    $context = New-BuildContext
    $registryPath = "/subscriptions/$($script:Config.SubscriptionId)/resourceGroups/$($script:Config.ResourceGroup)/providers/Microsoft.ContainerRegistry/registries/$RegistryName"
    $queuedAt = [DateTimeOffset]::UtcNow
    $errorFile = New-TemporaryFile
    try {
        $image = "$RegistryServer/$($script:ImageRepository):$($context.Tag)"
        Write-Detail "Building $image from $($context.Files) source files with ACR Tasks (about 5 minutes)..."
        # Queued and polled rather than streamed. The build log carries non-ASCII characters (npm prints
        # check marks), and the Windows CLI - an isolated Python that ignores PYTHONIOENCODING - encodes
        # redirected output in the ANSI code page and crashes on the first one, abandoning the stream
        # while the build carries on in the registry. The log is fetched directly if the build fails.
        # Called directly rather than through Invoke-Az: --no-wait reports the run it queued only as a
        # warning, which --only-show-errors would suppress.
        & az acr build --subscription $script:Config.SubscriptionId --registry $RegistryName `
            --image "$($script:ImageRepository):$($context.Tag)" --image "$($script:ImageRepository):latest" `
            --build-arg "NODE_VERSION=$($context.NodeMajor)" `
            --platform linux/amd64 --file Dockerfile --no-wait --output none $context.Path 2> $errorFile.FullName
        $messages = Get-Content -LiteralPath $errorFile.FullName -Raw
        if ($LASTEXITCODE -ne 0) { throw "az acr build could not queue the image build:`n$messages" }
    }
    finally {
        Remove-Item -LiteralPath $context.Path -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $errorFile.FullName -Force -ErrorAction SilentlyContinue
    }

    $runId = [regex]::Match([string]$messages, 'Queued a build with ID:\s*(\S+)').Groups[1].Value
    if (-not $runId) {
        # Fall back on the registry's newest run queued since this call started.
        $runs = Invoke-Api -Audience arm -Method GET -Uri "$registryPath/runs?api-version=2019-06-01-preview&`$top=10"
        $runId = @($runs.value) | Where-Object { $_ -and [DateTimeOffset]$_.properties.createTime -ge $queuedAt.AddMinutes(-2) } |
            Sort-Object { [DateTimeOffset]$_.properties.createTime } -Descending |
            Select-Object -First 1 | ForEach-Object { $_.properties.runId }
    }
    if (-not $runId) { throw 'az acr build did not report the run it queued.' }
    $started = Get-Date
    $reported = -1
    $runPath = "$registryPath/runs/$runId"
    while ($true) {
        Start-Sleep -Seconds 20
        $state = Invoke-Api -Audience arm -Method GET -Uri "$runPath`?api-version=2019-06-01-preview"
        $status = $state.properties.status
        if ($status -notin 'Queued', 'Started', 'Running') { break }
        $minutes = [int][Math]::Floor(((Get-Date) - $started).TotalMinutes)
        if ($minutes -ne $reported) { Write-Detail "  build $runId $($status.ToLowerInvariant()) for $minutes min..."; $reported = $minutes }
    }
    if ($status -ne 'Succeeded') {
        $log = Invoke-Api -Audience arm -Method POST -Uri "$runPath/listLogSasUrl?api-version=2019-06-01-preview"
        $text = (Invoke-WebRequest -Uri $log.logLink -UseBasicParsing).Content
        if ($text -is [byte[]]) { $text = [Text.Encoding]::UTF8.GetString($text) }
        Write-Host (($text -split "`n") | Select-Object -Last 60 | Out-String)
        throw "The image build $runId finished as '$status'. The end of its log is above."
    }
    Write-Detail "Built in $([int]((Get-Date) - $started).TotalMinutes) min (run $runId)."
    return $image
}

#endregion

#region Microsoft Entra app registration

function Get-TenantDefaultDomain {
    $organization = Invoke-Api -Audience graph -Method GET -Uri '/organization?$select=verifiedDomains'
    $default = foreach ($org in @($organization.value)) { @($org.verifiedDomains) | Where-Object { $_.isDefault } }
    $default = @($default) | Select-Object -First 1
    if (-not $default) { throw 'Could not read the tenant''s default domain; set entraApp.tenantDomain in the environment file.' }
    return $default.name
}

function Set-PortalAppRegistration([string[]] $PortalUrls) {
    $graph = Invoke-Api -Audience graph -Method GET -Uri "/servicePrincipals(appId='$script:GraphAppId')?`$select=id,oauth2PermissionScopes"
    $access = foreach ($scope in $script:GraphScopes) {
        $definition = $graph.oauth2PermissionScopes | Where-Object value -eq $scope | Select-Object -First 1
        if (-not $definition) { throw "Microsoft Graph has no delegated permission '$scope'." }
        @{ id = $definition.id; type = 'Scope' }
    }

    # Every address the portal answers on - its custom domain first, when it has one, then the default
    # one - because the OpenID Connect handler builds its callback from whichever the browser used.
    $web = @{
        # The ASP.NET Core OpenID Connect handler's callback paths. The portal asks for
        # "code id_token", so ID tokens must be issued from the authorize endpoint.
        redirectUris          = @($PortalUrls | ForEach-Object { "$_/signin-oidc"; "$_/signout-callback-oidc" })
        logoutUrl             = "$($PortalUrls[0])/signout-oidc"
        implicitGrantSettings = @{ enableIdTokenIssuance = $true; enableAccessTokenIssuance = $false }
    }
    $requiredResourceAccess = @(@{ resourceAppId = $script:GraphAppId; resourceAccess = @($access) })

    $filter = [uri]::EscapeDataString("displayName eq '$($script:Config.EntraAppDisplayName.Replace("'", "''"))'")
    $existing = @((Invoke-Api -Audience graph -Method GET -Uri "/applications?`$filter=$filter&`$select=id,appId,displayName").value)
    if ($existing.Count -gt 1) {
        throw "More than one app registration is named '$($script:Config.EntraAppDisplayName)'. Rename or delete the extras, or set entraApp.displayName."
    }
    $rename = $false
    if ($existing.Count -eq 0) {
        # Adopt the registration already serving this portal's address - one created under an earlier
        # default name, or renamed in the tenant since - rather than create a second for the same portal.
        # Filtering on redirect URIs is an advanced query, hence ConsistencyLevel and $count.
        foreach ($portalUrl in $PortalUrls) {
            $callback = [uri]::EscapeDataString("web/redirectUris/any(p:p eq '$portalUrl/signin-oidc')")
            $byAddress = @((Invoke-Api -Audience graph -Method GET -Uri "/applications?`$filter=$callback&`$count=true&`$select=id,appId,displayName" -Headers @{ ConsistencyLevel = 'eventual' }).value)
            if ($byAddress.Count -gt 1) {
                throw "More than one app registration already redirects to $portalUrl ($(($byAddress | ForEach-Object { $_.displayName }) -join ', ')). Delete the extras, or set entraApp.displayName to the one to keep."
            }
            if ($byAddress.Count -eq 1) {
                $existing = $byAddress
                $rename = $true
                break
            }
        }
    }

    if ($existing.Count -eq 0) {
        Write-Detail "Creating app registration '$($script:Config.EntraAppDisplayName)'..."
        $application = Invoke-Api -Audience graph -Method POST -Uri '/applications' -Body @{
            displayName            = $script:Config.EntraAppDisplayName
            signInAudience         = 'AzureADMyOrg'
            web                    = $web
            requiredResourceAccess = $requiredResourceAccess
        }
    }
    else {
        $application = $existing[0]
        $update = @{
            web                    = $web
            requiredResourceAccess = $requiredResourceAccess
        }
        if ($rename) {
            Write-Detail "Adopting app registration '$($application.displayName)' ($($application.appId)), which already serves this portal, and renaming it '$($script:Config.EntraAppDisplayName)'..."
            $update.displayName = $script:Config.EntraAppDisplayName
        }
        else {
            Write-Detail "Updating app registration '$($application.displayName)' ($($application.appId))..."
        }
        Invoke-Api -Audience graph -Method PATCH -Uri "/applications/$($application.id)" -Body $update | Out-Null
    }

    $spFilter = [uri]::EscapeDataString("appId eq '$($application.appId)'")
    $servicePrincipal = @((Invoke-Api -Audience graph -Method GET -Uri "/servicePrincipals?`$filter=$spFilter&`$select=id").value) | Select-Object -First 1
    if (-not $servicePrincipal) {
        # A new application can take a few seconds to become visible to the service principal endpoint.
        for ($attempt = 1; -not $servicePrincipal; $attempt++) {
            try { $servicePrincipal = Invoke-Api -Audience graph -Method POST -Uri '/servicePrincipals' -Body @{ appId = $application.appId } }
            catch { if ($attempt -ge 6) { throw }; Start-Sleep -Seconds 10 }
        }
    }

    Grant-AdminConsent -ClientServicePrincipalId $servicePrincipal.id -GraphServicePrincipalId $graph.id

    # Read directly rather than by member enumeration: in strict mode, enumerating a property whose
    # value is an empty array - a new registration's passwordCredentials - reports it as missing.
    $current = Invoke-Api -Audience graph -Method GET -Uri "/applications/$($application.id)?`$select=passwordCredentials"
    return [pscustomobject]@{
        ObjectId            = $application.id
        AppId               = $application.appId
        PasswordCredentials = @($current.passwordCredentials)
    }
}

# ChannelMessage.Read.All needs an administrator's consent, so without this grant nobody could sign in.
function Grant-AdminConsent([string] $ClientServicePrincipalId, [string] $GraphServicePrincipalId) {
    $scope = $script:GraphScopes -join ' '
    try {
        $filter = [uri]::EscapeDataString("clientId eq '$ClientServicePrincipalId' and resourceId eq '$GraphServicePrincipalId' and consentType eq 'AllPrincipals'")
        $grant = @((Invoke-Api -Audience graph -Method GET -Uri "/oauth2PermissionGrants?`$filter=$filter").value) | Select-Object -First 1
        if ($grant) {
            if ($grant.scope.Trim() -ne $scope) {
                Invoke-Api -Audience graph -Method PATCH -Uri "/oauth2PermissionGrants/$($grant.id)" -Body @{ scope = $scope } | Out-Null
            }
        }
        else {
            Invoke-Api -Audience graph -Method POST -Uri '/oauth2PermissionGrants' -Body @{
                clientId    = $ClientServicePrincipalId
                consentType = 'AllPrincipals'
                resourceId  = $GraphServicePrincipalId
                scope       = $scope
            } | Out-Null
        }
        Write-Detail "Admin consent granted for: $scope"
    }
    catch {
        Write-Warning "Admin consent could not be granted ($($_.Exception.Message)). An administrator must consent to the portal's Microsoft Graph permissions before anyone can sign in."
    }
}

# Reuses the secret the portal already holds while it is valid for another month; otherwise issues a
# new one. A secret is matched to its credential by the three-character hint Graph keeps for it.
function Get-PortalClientSecret([pscustomobject] $Application, [string] $CurrentSecret) {
    if ($CurrentSecret -and -not $RotateClientSecret) {
        $credential = $Application.PasswordCredentials | Where-Object {
            $_.displayName -eq $script:ClientSecretDisplayName -and $_.hint -ceq $CurrentSecret.Substring(0, 3)
        } | Sort-Object endDateTime -Descending | Select-Object -First 1
        if ($credential -and ([datetime]$credential.endDateTime) -gt (Get-Date).AddDays(30)) {
            Write-Detail "Keeping the current client secret (expires $(([datetime]$credential.endDateTime).ToString('yyyy-MM-dd')))."
            return [pscustomobject]@{ Value = $CurrentSecret; KeyId = $credential.keyId; IsNew = $false }
        }
    }

    Write-Detail 'Issuing a new client secret (valid for one year)...'
    $created = Invoke-Api -Audience graph -Method POST -Uri "/applications/$($Application.ObjectId)/addPassword" -Body @{
        passwordCredential = @{
            displayName = $script:ClientSecretDisplayName
            endDateTime = (Get-Date).ToUniversalTime().AddYears(1).ToString('o')
        }
    }
    return [pscustomobject]@{ Value = $created.secretText; KeyId = $created.keyId; IsNew = $true }
}

function Remove-SupersededClientSecrets([pscustomobject] $Application, [string] $KeepKeyId) {
    foreach ($credential in $Application.PasswordCredentials | Where-Object { $_.displayName -eq $script:ClientSecretDisplayName -and $_.keyId -ne $KeepKeyId }) {
        Invoke-Api -Audience graph -Method POST -Uri "/applications/$($Application.ObjectId)/removePassword" -Body @{ keyId = $credential.keyId } | Out-Null
        Write-Detail "Removed superseded client secret $($credential.hint)... (created $(([datetime]$credential.startDateTime).ToString('yyyy-MM-dd')))."
    }
}

#endregion

#region Data job

# Whether the job has ever rebuilt the database. A first deployment - or one that stopped before its
# first run completed - has not, and the portal would otherwise sit empty until midnight.
function Test-DataJobHasSucceeded([string] $JobName) {
    $base = "/subscriptions/$($script:Config.SubscriptionId)/resourceGroups/$($script:Config.ResourceGroup)/providers/Microsoft.App/jobs/$JobName"
    $executions = @(Get-AllPages "$base/executions?api-version=$script:ContainerAppsApiVersion")
    return [bool]($executions | Where-Object { (Get-Setting $_ 'properties.status') -eq 'Succeeded' } | Select-Object -First 1)
}

function Start-DataJob([string] $JobName) {
    $base = "/subscriptions/$($script:Config.SubscriptionId)/resourceGroups/$($script:Config.ResourceGroup)/providers/Microsoft.App/jobs/$JobName"
    $execution = Invoke-Api -Audience arm -Method POST -Uri "$base/start?api-version=$script:ContainerAppsApiVersion"
    Write-Detail "Started data job execution $($execution.name)."
    if ($NoWait) { return }

    Write-Detail 'Waiting for it to finish (about ten minutes at the default demo size; the portal shows errors until it does)...'
    $started = Get-Date
    $status = $null
    $reported = -1
    while ($true) {
        Start-Sleep -Seconds 30
        $state = Invoke-Api -Audience arm -Method GET -Uri "$base/executions/$($execution.name)?api-version=$script:ContainerAppsApiVersion"
        $status = $state.properties.status
        if ($status -in 'Succeeded', 'Failed', 'Stopped', 'Degraded') { break }
        $minutes = [int][Math]::Floor(((Get-Date) - $started).TotalMinutes)
        if ($minutes -ne $reported) { Write-Detail "  $status after $minutes min..."; $reported = $minutes }
        if (((Get-Date) - $started).TotalHours -gt 3) { break }
    }
    if ($status -ne 'Succeeded') {
        Write-Warning "The data job finished as '$status'. See its log with:`n    az containerapp job logs show --subscription $($script:Config.SubscriptionId) -g $($script:Config.ResourceGroup) -n $JobName --execution $($execution.name) --container datagen"
    }
    else {
        Write-Detail "Data job succeeded in $([int]((Get-Date) - $started).TotalMinutes) min."
    }
}

function Test-Portal([string] $PortalUrl) {
    # "/" is [Authorize]d, so a healthy portal answers an anonymous request by sending the browser to
    # Microsoft Entra ID with this portal's own https callback - which proves the app started, read
    # its configuration and sees the forwarded https scheme. HttpClient rather than Invoke-WebRequest,
    # which reports an unfollowed redirect as an error.
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    try {
        for ($attempt = 1; $attempt -le 20; $attempt++) {
            try {
                $response = $client.GetAsync("$PortalUrl/").GetAwaiter().GetResult()
                $location = if ($response.Headers.Location) { $response.Headers.Location.AbsoluteUri } else { '' }
                if ([int]$response.StatusCode -eq 302 -and $location -match '^https://login\.microsoftonline\.com/') {
                    if ($location -notmatch [regex]::Escape([uri]::EscapeDataString("$PortalUrl/signin-oidc"))) {
                        Write-Warning "The portal redirects to sign-in, but not with its https callback: $location"
                    }
                    else {
                        Write-Detail 'The portal is up and redirects to Microsoft Entra ID sign-in.'
                    }
                    return
                }
                Write-Detail "  The portal answered $([int]$response.StatusCode); retrying..."
            }
            catch {
                Write-Detail "  The portal is not answering yet ($($_.Exception.GetBaseException().Message)); retrying..."
            }
            Start-Sleep -Seconds 15
        }
    }
    finally {
        $client.Dispose()
    }
    Write-Warning "The portal did not redirect to sign-in. Check its log: az containerapp logs show --subscription $($script:Config.SubscriptionId) -g $($script:Config.ResourceGroup) -n $($script:Config.WebAppName) --type console"
}

#endregion

$script:Config = Read-DemoEnvironment $EnvironmentFile
$config = $script:Config

Write-Step "Checking the Azure sign-in for tenant $($config.TenantId)"
Assert-AzureSignIn
Register-Providers

Write-Step "Resource group $($config.ResourceGroup)"
$tags = @{}
foreach ($property in $config.Tags.PSObject.Properties) { $tags[$property.Name] = [string]$property.Value }
$tags['Workload'] = $script:WorkloadTag
# Tells this instance's resources apart from another demo instance's, e.g. in a shared environment.
$tags['DemoInstance'] = $config.NamePrefix
Invoke-Az -Arguments (@('group', 'create', '--subscription', $config.SubscriptionId, '--name', $config.ResourceGroup, '--location', $config.Location, '--output', 'none', '--tags') + @($tags.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })) | Out-Null

Write-Step 'Container Apps environment'
$environment = Get-ContainerAppsEnvironment
$ownEnvironment = Test-DemoOwnsEnvironment $environment
$createEnvironment = (-not $environment) -or $ownEnvironment
# Only an existing environment the demo did not create needs its network described to infra.bicep.
$network = @{ privateEndpointSubnetId = ''; privateEndpointLocation = $config.Location; privateDnsZoneId = ''; environmentVnetId = '' }
if (-not $environment) {
    if ($config.EnvironmentResourceGroup -ne $config.ResourceGroup) {
        throw "Container Apps environment '$($config.EnvironmentName)' was not found in resource group '$($config.EnvironmentResourceGroup)'. Only an environment in the demo's own resource group is created automatically."
    }
    Write-Detail "Creating a new environment '$($config.EnvironmentName)' in $($config.Location)$(if ($config.SqlNetworkAccess -eq 'private') { ', with its own virtual network' })."
}
elseif ($ownEnvironment) {
    # An environment's network is fixed when it is created, so a change of sql.networkAccess cannot be applied to it.
    $hasNetwork = [bool](Get-Setting $environment 'properties.vnetConfiguration.infrastructureSubnetId')
    if ($hasNetwork -ne ($config.SqlNetworkAccess -eq 'private')) {
        throw "The demo's environment '$($environment.name)' was created $(if ($hasNetwork) { 'with' } else { 'without' }) its own virtual network, for sql.networkAccess '$(if ($hasNetwork) { 'private' } else { 'public' })', and an environment's network cannot be changed once it exists. Set sql.networkAccess back, or set containerAppsEnvironment.name to create another environment."
    }
    Write-Detail "Updating the demo's environment '$($environment.name)' ($($environment.location))."
}
else {
    Write-Detail "Using existing environment '$($environment.name)' ($($environment.location), $($environment.properties.defaultDomain)). It is deployed into, never modified."
    if (Get-Setting $environment 'properties.vnetConfiguration.internal') {
        Write-Warning "'$($environment.name)' is an internal environment: the portal will only be reachable from inside its virtual network, so sign-in only works from there."
    }
    if ($config.SqlNetworkAccess -eq 'private') {
        $network = Resolve-ExistingEnvironmentNetwork $environment
    }
}

Write-Step 'Shared resources: identity, registry, Azure SQL'
Assert-SqlRegionSupportsTheDemo
$infraParameters = @{
    location                       = $config.Location
    sqlLocation                    = $config.SqlLocation
    sqlNetworkAccess               = $config.SqlNetworkAccess
    privateEndpointSubnetId        = $network.privateEndpointSubnetId
    privateEndpointLocation        = $network.privateEndpointLocation
    privateDnsZoneId               = $network.privateDnsZoneId
    environmentVnetId              = $network.environmentVnetId
    tags                           = $tags
    identityName                   = "$($config.NamePrefix)-id"
    registryName                   = ''
    sqlServerName                  = [string]$config.SqlServerName
    sqlDatabaseName                = $config.SqlDatabaseName
    sqlUseFreeOffer                = $config.SqlUseFreeOffer
    sqlFreeLimitExhaustionBehavior = $config.SqlFreeLimitExhaustionBehavior
    sqlAutoPauseDelayMinutes       = $config.SqlAutoPauseDelayMinutes
    createEnvironment              = $createEnvironment
    environmentName                = $config.EnvironmentName
    logAnalyticsName               = "$($config.NamePrefix)-logs"
    namePrefix                     = $config.NamePrefix
}
$infra = Invoke-Deployment -Name 'm365ai-demo-infra' -TemplateFile (Join-Path $PSScriptRoot 'infra.bicep') -Parameters $infraParameters
Write-Detail "Registry: $($infra.registryLoginServer.value)"
Write-Detail "SQL:      $($infra.sqlServerFqdn.value) / $($infra.sqlDatabaseName.value)$(if ($config.SqlUseFreeOffer) { ' (free offer, serverless)' } else { ' (Basic)' })"

if ($createEnvironment) {
    $environment = Get-ContainerAppsEnvironment
}
$environmentLocation = $environment.location
$environmentDomain = $environment.properties.defaultDomain
$workloadProfile = if (@($environment.properties.workloadProfiles).Count -gt 0) { 'Consumption' } else { '' }
$appFqdn = "$($config.WebAppName).$environmentDomain"
$defaultUrl = "https://$appFqdn"
$portalUrl = if ($config.CustomDomain) { "https://$($config.CustomDomain)" } else { $defaultUrl }

Write-Step 'Image'
if ($Image) {
    if (-not $Image.StartsWith("$($infra.registryLoginServer.value)/", [StringComparison]::OrdinalIgnoreCase)) {
        throw "-Image must be in the demo's registry ($($infra.registryLoginServer.value)): the apps pull with the managed identity's AcrPull role there."
    }
    $image = $Image
    Write-Detail "Deploying $image"
}
elseif ($SkipImageBuild) {
    $image = Get-DeployedImage $config.WebAppName
    if (-not $image) { throw '-SkipImageBuild needs a portal that is already deployed; there is no image to reuse yet.' }
    Write-Detail "Reusing $image"
}
else {
    $image = Build-Image -RegistryName $infra.registryName.value -RegistryServer $infra.registryLoginServer.value
}

Write-Step 'Microsoft Entra app registration'
$tenantDomain = if ($config.TenantDomain) { $config.TenantDomain } else { Get-TenantDefaultDomain }
$application = Set-PortalAppRegistration -PortalUrls @($portalUrl, $defaultUrl | Select-Object -Unique)
$secret = Get-PortalClientSecret -Application $application -CurrentSecret (Get-ContainerAppSecret $config.WebAppName $script:ClientSecretName)

$certificateId = ''
if ($config.CustomDomain) {
    Write-Step "Custom domain $($config.CustomDomain)"
    Set-CustomDomainDns -Domain $config.CustomDomain -AppFqdn $appFqdn `
        -VerificationId (Get-Setting $environment 'properties.customDomainConfiguration.customDomainVerificationId')
    $certificateId = Get-ManagedCertificateId -Environment $environment -Domain $config.CustomDomain
    if ($certificateId) { Write-Detail "Managed certificate: issued." }
}

Write-Step 'Portal and data job'
$appsParameters = @{
    location                 = $environmentLocation
    tags                     = $tags
    environmentId            = $environment.id
    environmentDefaultDomain = $environmentDomain
    workloadProfileName      = $workloadProfile
    identityId               = $infra.identityId.value
    identityClientId         = $infra.identityClientId.value
    registryServer           = $infra.registryLoginServer.value
    image                    = $image
    webAppName               = $config.WebAppName
    jobName                  = $config.JobName
    entraClientId            = $application.AppId
    entraClientSecret        = $secret.Value
    tenantDomain             = $tenantDomain
    sqlServerFqdn            = $infra.sqlServerFqdn.value
    sqlDatabaseName          = $infra.sqlDatabaseName.value
    webMinReplicas           = $config.WebMinReplicas
    dataGenCron              = $config.Schedule
    dataGenArgs              = $config.DataGenArguments
    customDomainName         = $config.CustomDomain
    customDomainCertificateId = $certificateId
}
$apps = Invoke-Deployment -Name 'm365ai-demo-apps' -TemplateFile (Join-Path $PSScriptRoot 'apps.bicep') -Parameters $appsParameters
if ($config.CustomDomain -and -not $certificateId) {
    # A managed certificate can only be issued once the host name is on the app, so the first deployment
    # adds it unbound; this one binds it with the certificate.
    $appsParameters.customDomainCertificateId = New-ManagedCertificate -Environment $environment -Domain $config.CustomDomain
    $apps = Invoke-Deployment -Name 'm365ai-demo-apps' -TemplateFile (Join-Path $PSScriptRoot 'apps.bicep') -Parameters $appsParameters
}
Write-Detail "Portal:   $($apps.webAppUrl.value)$(if ($config.CustomDomain) { " (also $defaultUrl)" })"
Write-Detail "Data job: $($apps.jobName.value), schedule '$($config.Schedule)' (UTC)"
if ($secret.IsNew) {
    Remove-SupersededClientSecrets -Application $application -KeepKeyId $secret.KeyId
}

if ($RegenerateData -or -not (Test-DataJobHasSucceeded $config.JobName)) {
    Write-Step 'Generating the demo data now'
    Start-DataJob $config.JobName
}

Write-Step 'Checking the portal'
Test-Portal $apps.webAppUrl.value

Write-Host ''
Write-Host "Done. Open $($apps.webAppUrl.value) and sign in with an account from $tenantDomain." -ForegroundColor Green
Write-Host "The database is rebuilt from synthetic data on the schedule '$($config.Schedule)' (UTC)."
