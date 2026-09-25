#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $SubscriptionId,

    [Parameter(Mandatory)]
    [string] $Tenant,

    [Parameter(Mandatory)]
    [string] $Location,

    [Parameter(Mandatory)]
    [string] $ResourceGroupName,

    [Parameter(Mandatory)]
    [string] $WebAppName,

    [Parameter(Mandatory)]
    [string] $NamePrefix,

    [Parameter(Mandatory)]
    [string] $VnetAddressPrefix,

    [Parameter(Mandatory)]
    [string] $AppIntegrationSubnetPrefix,

    [Parameter(Mandatory)]
    [string] $PrivateEndpointSubnetPrefix,

    [string] $EnvironmentName = 'poc',

    [string] $EntraAppDisplayName = 'Microsoft 365 Analytics Telemetry Dashboard',

    [string] $TelemetrySecretEnvironmentVariableName = 'TELEMETRY_SERVICE_SECRET',

    # Client ID of the dashboard app registration. Optional: when omitted, the one already
    # configured on an existing site is used, and a registration is only created for a new site.
    [string] $AzureAdClientId,

    # Create a replacement app registration when the one this deployment is configured for no
    # longer exists. Never done implicitly, because a replacement has a new client ID.
    [switch] $AllowNewEntraApplication,

    # Delete an existing Linux App Service and its plan so they can be re-created on Windows with
    # the same name. An App Service cannot change operating system in place.
    [switch] $ReplaceLinuxWebApp,

    # Leave WEBSITE_LOAD_FIRST_PARTY_AUTH unset, for a subscription App Service has not enabled for
    # first-party authentication. App Service Authentication then validates tokens with MISE v1,
    # which does not satisfy the MISE compliance KPI.
    [switch] $SkipFirstPartyAuth,

    [switch] $SkipApplicationPublish,

    [switch] $WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ScopeName = 'Telemetry.Read'
$script:RoleName = 'Telemetry.Dashboard.Read'

function Invoke-AzCli {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments,

        [switch] $AsJson
    )

    $allArguments = @($Arguments) + @('--only-show-errors')
    $errorFile = Join-Path ([IO.Path]::GetTempPath()) "telemetry-az-$([guid]::NewGuid().ToString('N')).log"
    try {
        $output = & az @allArguments 2> $errorFile
        if ($LASTEXITCODE -ne 0) {
            $errorText = if (Test-Path -LiteralPath $errorFile) {
                Get-Content -LiteralPath $errorFile -Raw
            }
            else {
                ''
            }
            throw "Azure CLI failed: az $($Arguments -join ' ')`n$errorText"
        }

        $text = $output -join [Environment]::NewLine
        if ($AsJson) {
            if ([string]::IsNullOrWhiteSpace($text)) {
                return $null
            }
            return $text | ConvertFrom-Json
        }

        return $text.Trim()
    }
    finally {
        if (Test-Path -LiteralPath $errorFile) {
            Remove-Item -LiteralPath $errorFile -Force
        }
    }
}

function Invoke-AzRestJson {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('get', 'post', 'patch', 'put', 'delete')]
        [string] $Method,

        [Parameter(Mandatory)]
        [string] $Uri,

        [object] $Body
    )

    $arguments = @('rest', '--method', $Method, '--url', $Uri, '--output', 'json')
    $bodyFile = $null
    try {
        if ($null -ne $Body) {
            $bodyFile = Join-Path ([IO.Path]::GetTempPath()) "telemetry-rest-$([guid]::NewGuid().ToString('N')).json"
            $Body | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $bodyFile -Encoding utf8NoBOM
            $arguments += @('--headers', 'Content-Type=application/json', '--body', "@$bodyFile")
        }

        return Invoke-AzCli -Arguments $arguments -AsJson
    }
    finally {
        if ($bodyFile -and (Test-Path -LiteralPath $bodyFile)) {
            Remove-Item -LiteralPath $bodyFile -Force
        }
    }
}

function Assert-AzureContext {
    $account = Invoke-AzCli -Arguments @(
        'account', 'show',
        '--subscription', $SubscriptionId,
        '--output', 'json'
    ) -AsJson

    $tokenTenant = Invoke-AzCli -Arguments @(
        'account', 'get-access-token',
        '--tenant', $Tenant,
        '--scope', 'https://management.azure.com/.default',
        '--query', 'tenant',
        '--output', 'tsv'
    )
    if ([string]::IsNullOrWhiteSpace($tokenTenant)) {
        throw "Azure CLI could not acquire a management token for tenant '$Tenant'."
    }

    if ($Tenant -match '^[0-9a-fA-F-]{36}$' -and $account.tenantId -ne $Tenant) {
        throw "Subscription '$SubscriptionId' belongs to a different tenant."
    }

    Invoke-AzCli -Arguments @('account', 'set', '--subscription', $SubscriptionId) | Out-Null
}

function Register-ResourceProviders {
    $providers = @(
        'Microsoft.DocumentDB',
        'Microsoft.Insights',
        'Microsoft.KeyVault',
        'Microsoft.Network',
        'Microsoft.OperationalInsights',
        'Microsoft.Web'
    )

    foreach ($provider in $providers) {
        $state = Invoke-AzCli -Arguments @(
            'provider', 'show',
            '--namespace', $provider,
            '--query', 'registrationState',
            '--output', 'tsv'
        )
        if ($state -ne 'Registered') {
            Write-Host "Registering resource provider $provider..."
            Invoke-AzCli -Arguments @('provider', 'register', '--namespace', $provider, '--wait') | Out-Null
        }
    }
}

function Assert-WebAppNameAvailable {
    $existingApps = @(Invoke-AzCli -Arguments @(
        'webapp', 'list',
        '--subscription', $SubscriptionId,
        '--query', "[?name=='$WebAppName'].{name:name,resourceGroup:resourceGroup}",
        '--output', 'json'
    ) -AsJson)

    if ($existingApps.Count -gt 0) {
        $matchingApp = $existingApps | Where-Object { $_.resourceGroup -eq $ResourceGroupName }
        if (-not $matchingApp) {
            throw "App Service name '$WebAppName' is already used in a different accessible resource group."
        }
        return
    }

    $availability = Invoke-AzRestJson -Method post `
        -Uri "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Web/checknameavailability?api-version=2024-04-01" `
        -Body @{
            name = $WebAppName
            type = 'Microsoft.Web/sites'
            isFqdn = $false
        }

    if (-not $availability.nameAvailable) {
        throw "App Service name '$WebAppName' is unavailable: $($availability.message)"
    }
}

function Get-ExistingWebApp {
    $existing = @(Invoke-AzCli -Arguments @(
        'webapp', 'list',
        '--subscription', $SubscriptionId,
        '--query', "[?name=='$WebAppName'].{id:id,resourceGroup:resourceGroup}",
        '--output', 'json'
    ) -AsJson) | Where-Object { $_.resourceGroup -eq $ResourceGroupName } | Select-Object -First 1

    if (-not $existing) {
        return $null
    }

    return Invoke-AzCli -Arguments @('webapp', 'show', '--ids', $existing.id, '--output', 'json') -AsJson
}

function Test-IsLinuxWebApp {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $WebApp
    )

    return ($WebApp.reserved -eq $true) -or ("$($WebApp.kind)" -match 'linux')
}

function Get-ConfiguredEntraClientId {
    $configured = Invoke-AzCli -Arguments @(
        'webapp', 'config', 'appsettings', 'list',
        '--resource-group', $ResourceGroupName,
        '--name', $WebAppName,
        '--query', "[?name=='AzureAd__ClientId'].value | [0]",
        '--output', 'tsv'
    )

    if ([string]::IsNullOrWhiteSpace($configured)) {
        return $null
    }
    return $configured.Trim()
}

function Find-EntraApplicationsByDisplayName {
    return @(Invoke-AzCli -Arguments @(
        'ad', 'app', 'list',
        '--display-name', $EntraAppDisplayName,
        '--output', 'json'
    ) -AsJson | Where-Object { $_.displayName -eq $EntraAppDisplayName })
}

function Find-EntraApplicationByClientId {
    param(
        [Parameter(Mandatory)]
        [string] $ClientId
    )

    return @(Invoke-AzCli -Arguments @(
        'ad', 'app', 'list',
        '--app-id', $ClientId,
        '--output', 'json'
    ) -AsJson) | Select-Object -First 1
}

function Find-DeletedEntraApplication {
    param(
        [Parameter(Mandatory)]
        [string] $ClientId
    )

    # Reading deleted directory objects needs a permission not every deployer has. Without it the
    # resulting error is only less specific, so a failure here is not fatal.
    try {
        $response = Invoke-AzRestJson -Method get `
            -Uri "https://graph.microsoft.com/v1.0/directory/deletedItems/microsoft.graph.application?`$filter=appId eq '$ClientId'"
        return @($response.value) | Select-Object -First 1
    }
    catch {
        return $null
    }
}

function New-EntraApplication {
    Write-Host 'Creating the single-tenant Entra SPA/API registration...'
    return Invoke-AzCli -Arguments @(
        'ad', 'app', 'create',
        '--display-name', $EntraAppDisplayName,
        '--sign-in-audience', 'AzureADMyOrg',
        '--output', 'json'
    ) -AsJson
}

function Resolve-EntraApplication {
    param(
        [string] $ExpectedClientId
    )

    if (-not $ExpectedClientId) {
        # A new deployment: adopt the registration with the configured name, or create it.
        $applications = @(Find-EntraApplicationsByDisplayName)
        if ($applications.Count -gt 1) {
            throw "More than one Entra application has display name '$EntraAppDisplayName'."
        }
        if ($applications.Count -eq 1) {
            return $applications[0]
        }
        return New-EntraApplication
    }

    $application = Find-EntraApplicationByClientId -ClientId $ExpectedClientId
    if ($application) {
        return $application
    }

    # The registration this deployment is configured for has gone - typically deleted by a
    # compliance process. Creating another would quietly change the client ID that sign-in, consent,
    # role assignments and compliance tracking are all tied to, and restart any compliance process
    # against a registration nobody knows exists. Stop and make that a deliberate decision instead.
    $deleted = Find-DeletedEntraApplication -ClientId $ExpectedClientId
    $deletedDetail = if ($deleted) {
        " It was deleted on $($deleted.deletedDateTime). A deleted registration can be restored, with its client ID, for 30 days from Microsoft Entra ID > App registrations > Deleted applications."
    }
    else {
        ''
    }
    $message = "The Entra app registration this deployment is configured for (client ID $ExpectedClientId) no longer exists.$deletedDetail " +
        'This script will not replace it on its own, because a replacement has a new client ID. ' +
        'Restore it, pass -AzureAdClientId to use a different existing registration, or re-run with -AllowNewEntraApplication to create a replacement deliberately.'
    if (-not $AllowNewEntraApplication) {
        throw $message
    }

    $sameName = @(Find-EntraApplicationsByDisplayName)
    if ($sameName.Count -gt 0) {
        throw "-AllowNewEntraApplication was specified, but an Entra application named '$EntraAppDisplayName' already exists (client ID $($sameName[0].appId)). Pass -AzureAdClientId $($sameName[0].appId) to use it rather than creating another."
    }

    Write-Warning $message
    Write-Warning 'Creating a replacement registration because -AllowNewEntraApplication was specified. Dashboard users must be assigned the dashboard role again.'
    return New-EntraApplication
}

function Get-OrCreateEntraApplication {
    param(
        [Parameter(Mandatory)]
        [string] $RedirectUri,

        [string] $ExpectedClientId
    )

    $application = Resolve-EntraApplication -ExpectedClientId $ExpectedClientId

    $applicationObject = Invoke-AzRestJson -Method get `
        -Uri "https://graph.microsoft.com/v1.0/applications/$($application.id)"

    $scope = @($applicationObject.api.oauth2PermissionScopes) |
        Where-Object { $_.value -eq $script:ScopeName } |
        Select-Object -First 1
    $scopeId = if ($scope) { $scope.id } else { [guid]::NewGuid().ToString() }

    $role = @($applicationObject.appRoles) |
        Where-Object { $_.value -eq $script:RoleName } |
        Select-Object -First 1
    $roleId = if ($role) { $role.id } else { [guid]::NewGuid().ToString() }

    $otherScopes = @($applicationObject.api.oauth2PermissionScopes) |
        Where-Object { $_.value -ne $script:ScopeName }
    $otherRoles = @($applicationObject.appRoles) |
        Where-Object { $_.value -ne $script:RoleName }
    $otherRequiredAccess = @($applicationObject.requiredResourceAccess) |
        Where-Object { $_.resourceAppId -ne $application.appId }

    $scopeDefinition = @{
        id = $scopeId
        value = $script:ScopeName
        type = 'User'
        isEnabled = $true
        adminConsentDisplayName = 'Read telemetry dashboard data'
        adminConsentDescription = 'Allows assigned users to read the Microsoft 365 Analytics telemetry dashboard.'
        userConsentDisplayName = 'Read telemetry dashboard data'
        userConsentDescription = 'Allows you to read the Microsoft 365 Analytics telemetry dashboard.'
    }
    $roleDefinition = @{
        id = $roleId
        value = $script:RoleName
        displayName = 'Telemetry dashboard reader'
        description = 'Can view aggregate telemetry dashboard data.'
        allowedMemberTypes = @('User')
        isEnabled = $true
    }

    Invoke-AzRestJson -Method patch `
        -Uri "https://graph.microsoft.com/v1.0/applications/$($application.id)" `
        -Body @{
            identifierUris = @("api://$($application.appId)")
            spa = @{
                redirectUris = @($RedirectUri)
            }
            api = @{
                requestedAccessTokenVersion = 2
                oauth2PermissionScopes = @($otherScopes) + @($scopeDefinition)
            }
            appRoles = @($otherRoles) + @($roleDefinition)
            requiredResourceAccess = @($otherRequiredAccess) + @(
                @{
                    resourceAppId = $application.appId
                    resourceAccess = @(
                        @{
                            id = $scopeId
                            type = 'Scope'
                        }
                    )
                }
            )
        } | Out-Null

    $servicePrincipals = @(Invoke-AzCli -Arguments @(
        'ad', 'sp', 'list',
        '--filter', "appId eq '$($application.appId)'",
        '--output', 'json'
    ) -AsJson)

    if ($servicePrincipals.Count -eq 0) {
        $servicePrincipal = Invoke-AzCli -Arguments @(
            'ad', 'sp', 'create',
            '--id', $application.appId,
            '--output', 'json'
        ) -AsJson
    }
    else {
        $servicePrincipal = $servicePrincipals[0]
    }

    Invoke-AzRestJson -Method patch `
        -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($servicePrincipal.id)" `
        -Body @{
            appRoleAssignmentRequired = $true
        } | Out-Null

    return [pscustomobject]@{
        AppId = $application.appId
        ApplicationObjectId = $application.id
        ServicePrincipalId = $servicePrincipal.id
        ScopeId = $scopeId
        RoleId = $roleId
    }
}

function Grant-CurrentUserDashboardAccess {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $EntraApplication
    )

    $user = Invoke-AzCli -Arguments @('ad', 'signed-in-user', 'show', '--output', 'json') -AsJson
    $assignmentResponse = Invoke-AzRestJson -Method get `
        -Uri "https://graph.microsoft.com/v1.0/users/$($user.id)/appRoleAssignments"
    $assignments = if ($assignmentResponse.PSObject.Properties.Name -contains 'value') {
        @($assignmentResponse.value)
    }
    else {
        @($assignmentResponse)
    }

    $existingAssignment = $assignments | Where-Object {
        $_.PSObject.Properties.Name -contains 'resourceId' -and
        $_.PSObject.Properties.Name -contains 'appRoleId' -and
        $_.resourceId -eq $EntraApplication.ServicePrincipalId -and
        $_.appRoleId -eq $EntraApplication.RoleId
    }

    if (-not $existingAssignment) {
        Invoke-AzRestJson -Method post `
            -Uri "https://graph.microsoft.com/v1.0/users/$($user.id)/appRoleAssignments" `
            -Body @{
                principalId = $user.id
                resourceId = $EntraApplication.ServicePrincipalId
                appRoleId = $EntraApplication.RoleId
            } | Out-Null
    }

    try {
        Invoke-AzCli -Arguments @(
            'ad', 'app', 'permission', 'admin-consent',
            '--id', $EntraApplication.AppId
        ) | Out-Null
    }
    catch {
        Write-Warning 'Admin consent could not be granted automatically. The assigned user might be prompted for delegated consent on first sign-in.'
    }
}

function Assert-FirstPartyAuthAccepted {
    # App Service only accepts WEBSITE_LOAD_FIRST_PARTY_AUTH on a subscription it has enabled for
    # first-party authentication. Try it on the site that is about to be replaced anyway, so an
    # unsupported subscription is found while nothing has been deleted, rather than halfway through
    # building the replacement.
    try {
        Invoke-AzCli -Arguments @(
            'webapp', 'config', 'appsettings', 'set',
            '--resource-group', $ResourceGroupName,
            '--name', $WebAppName,
            '--settings', 'WEBSITE_LOAD_FIRST_PARTY_AUTH=true',
            '--output', 'none'
        ) | Out-Null
    }
    catch {
        throw ('App Service did not accept WEBSITE_LOAD_FIRST_PARTY_AUTH on this subscription, so nothing has been deleted. ' +
            'The setting is only accepted once App Service has enabled the subscription for first-party authentication. ' +
            "Arrange that first, or re-run with -SkipFirstPartyAuth (App Service Authentication then stays on MISE v1).`n$($_.Exception.Message)")
    }
}

function Get-WebAppRoleAssignments {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $WebApp,

        [string] $ManagedIdentityPrincipalId
    )

    # Assignments scoped to the site itself - typically the CI deployment identity's Website
    # Contributor - are deleted with it, and nothing in the template re-creates them.
    return @(Invoke-AzCli -Arguments @(
        'role', 'assignment', 'list',
        '--scope', $WebApp.id,
        '--output', 'json'
    ) -AsJson | Where-Object {
        $_.scope -eq $WebApp.id -and $_.principalId -ne $ManagedIdentityPrincipalId
    } | ForEach-Object {
        [pscustomobject]@{
            PrincipalId = $_.principalId
            PrincipalType = $_.principalType
            RoleDefinitionId = $_.roleDefinitionId
            RoleDefinitionName = $_.roleDefinitionName
        }
    })
}

function Restore-WebAppRoleAssignments {
    param(
        [Parameter(Mandatory)]
        [string] $WebAppResourceId,

        [object[]] $Assignments = @()
    )

    foreach ($assignment in $Assignments) {
        $existing = @(Invoke-AzCli -Arguments @(
            'role', 'assignment', 'list',
            '--scope', $WebAppResourceId,
            '--assignee-object-id', $assignment.PrincipalId,
            '--output', 'json'
        ) -AsJson) | Where-Object {
            $_.scope -eq $WebAppResourceId -and $_.roleDefinitionId -eq $assignment.RoleDefinitionId
        }
        if ($existing) {
            continue
        }

        Write-Host "Restoring site-scoped role '$($assignment.RoleDefinitionName)' for $($assignment.PrincipalType) $($assignment.PrincipalId)..."
        Invoke-AzCli -Arguments @(
            'role', 'assignment', 'create',
            '--assignee-object-id', $assignment.PrincipalId,
            '--assignee-principal-type', $assignment.PrincipalType,
            '--role', $assignment.RoleDefinitionId,
            '--scope', $WebAppResourceId
        ) | Out-Null
    }
}

function Remove-PrincipalRoleAssignments {
    param(
        [Parameter(Mandatory)]
        [string] $PrincipalId
    )

    # A deleted site's system-assigned identity leaves its role assignments behind. The template's
    # assignments take their names from the site's resource ID, which the replacement shares, and ARM
    # refuses to repoint an existing assignment at a different principal
    # (RoleAssignmentUpdateNotPermitted) - so the orphans would block the deployment.
    $assignments = @(Invoke-AzCli -Arguments @(
        'role', 'assignment', 'list',
        '--all',
        '--assignee-object-id', $PrincipalId,
        '--output', 'json'
    ) -AsJson)
    foreach ($assignment in $assignments) {
        Invoke-AzCli -Arguments @('role', 'assignment', 'delete', '--ids', $assignment.id) | Out-Null
    }

    $cosmosAccountNames = @(Invoke-AzCli -Arguments @(
        'cosmosdb', 'list',
        '--resource-group', $ResourceGroupName,
        '--query', '[].name',
        '--output', 'json'
    ) -AsJson)
    foreach ($accountName in $cosmosAccountNames) {
        $sqlAssignments = @(Invoke-AzCli -Arguments @(
            'cosmosdb', 'sql', 'role', 'assignment', 'list',
            '--resource-group', $ResourceGroupName,
            '--account-name', $accountName,
            '--output', 'json'
        ) -AsJson) | Where-Object { $_.principalId -eq $PrincipalId }
        foreach ($sqlAssignment in $sqlAssignments) {
            Invoke-AzCli -Arguments @(
                'cosmosdb', 'sql', 'role', 'assignment', 'delete',
                '--resource-group', $ResourceGroupName,
                '--account-name', $accountName,
                '--role-assignment-id', $sqlAssignment.name,
                '--yes'
            ) | Out-Null
        }
    }
}

function Assert-AppServicePlanReplaceable {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $WebApp
    )

    $otherSites = @((Invoke-AzRestJson -Method get `
        -Uri "https://management.azure.com$($WebApp.appServicePlanId)/sites?api-version=2024-04-01").value |
        Where-Object { $_.name -ne $WebAppName } |
        ForEach-Object { $_.name })
    if ($otherSites.Count -gt 0) {
        throw "The App Service plan also hosts $($otherSites -join ', '), so it cannot be re-created on Windows. Nothing has been changed."
    }
}

function Remove-LinuxWebApp {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $WebApp,

        [string] $ManagedIdentityPrincipalId
    )

    $planId = $WebApp.appServicePlanId

    Write-Warning "Deleting the Linux App Service '$WebAppName' and its plan so they can be re-created on Windows with the same name. The service is unavailable until this deployment completes."

    if ($ManagedIdentityPrincipalId) {
        Remove-PrincipalRoleAssignments -PrincipalId $ManagedIdentityPrincipalId
    }

    Invoke-AzCli -Arguments @(
        'webapp', 'delete',
        '--resource-group', $ResourceGroupName,
        '--name', $WebAppName,
        '--keep-empty-plan'
    ) | Out-Null
    Invoke-AzCli -Arguments @('appservice', 'plan', 'delete', '--ids', $planId, '--yes') | Out-Null

    # The replacement plan integrates with the same subnet, which App Service only releases a little
    # after the old plan is deleted.
    $subnetId = if ($WebApp.PSObject.Properties.Name -contains 'virtualNetworkSubnetId') { $WebApp.virtualNetworkSubnetId } else { $null }
    if ($subnetId) {
        for ($attempt = 1; $attempt -le 30; $attempt++) {
            $subnet = Invoke-AzRestJson -Method get -Uri "https://management.azure.com$($subnetId)?api-version=2024-05-01"
            $links = @()
            if ($subnet.properties.PSObject.Properties.Name -contains 'serviceAssociationLinks' -and $subnet.properties.serviceAssociationLinks) {
                $links = @($subnet.properties.serviceAssociationLinks)
            }
            if ($links.Count -eq 0) {
                return
            }
            Start-Sleep -Seconds 20
        }
        Write-Warning 'The App Service integration subnet still reports a service association link; the deployment may fail until App Service releases it. Re-run this script if it does.'
    }
}

function New-DeploymentParameterFile {
    param(
        [Parameter(Mandatory)]
        [string] $TelemetrySecret,

        [Parameter(Mandatory)]
        [string] $ClientId
    )

    $parameterFile = Join-Path ([IO.Path]::GetTempPath()) "telemetry-parameters-$([guid]::NewGuid().ToString('N')).json"
    @{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = @{
            resourceGroupName = @{ value = $ResourceGroupName }
            location = @{ value = $Location }
            webAppName = @{ value = $WebAppName }
            namePrefix = @{ value = $NamePrefix }
            vnetAddressPrefix = @{ value = $VnetAddressPrefix }
            appIntegrationSubnetPrefix = @{ value = $AppIntegrationSubnetPrefix }
            privateEndpointSubnetPrefix = @{ value = $PrivateEndpointSubnetPrefix }
            azureAdClientId = @{ value = $ClientId }
            loadFirstPartyAuth = @{ value = (-not $SkipFirstPartyAuth) }
            telemetrySecret = @{ value = $TelemetrySecret }
            tags = @{
                value = @{
                    Environment = $EnvironmentName
                }
            }
        }
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $parameterFile -Encoding utf8NoBOM

    return $parameterFile
}

function Publish-TelemetryApplication {
    param(
        [Parameter(Mandatory)]
        [string] $WebAppResourceId
    )

    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $artifactRoot = Join-Path $repositoryRoot 'artifacts\TelemetryService'
    $publishDirectory = Join-Path $artifactRoot 'publish'
    $zipPath = Join-Path $artifactRoot 'TelemetryService.zip'

    if (Test-Path -LiteralPath $artifactRoot) {
        Remove-Item -LiteralPath $artifactRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    dotnet publish (Join-Path $repositoryRoot 'src\TelemetryService\Web.Server\Web.Server.csproj') `
        --configuration Release `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -Force

    Invoke-AzCli -Arguments @(
        'webapp', 'deploy',
        '--resource-group', $ResourceGroupName,
        '--name', $WebAppName,
        '--src-path', $zipPath,
        '--type', 'zip',
        '--clean', 'true',
        '--restart', 'true',
        '--async', 'false'
    ) | Out-Null

    Invoke-AzRestJson -Method post `
        -Uri "https://management.azure.com$WebAppResourceId/config/configreferences/appsettings/refresh?api-version=2022-03-01" | Out-Null
}

function Test-TelemetryDeployment {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $Outputs,

        [Parameter(Mandatory)]
        [string] $ClientId
    )

    $healthUrl = "$($Outputs.webAppUrl.value)/health"
    $healthSucceeded = $false
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        try {
            $health = Invoke-WebRequest -Uri $healthUrl -TimeoutSec 20 -SkipHttpErrorCheck
            if ($health.StatusCode -eq 200) {
                $healthSucceeded = $true
                break
            }
        }
        catch {
            # App Service can take several minutes to restart after ZIP deployment and role propagation.
        }
        Start-Sleep -Seconds 10
    }
    if (-not $healthSucceeded) {
        throw "Health endpoint did not become ready: $healthUrl"
    }

    $webAppResourceId = Invoke-AzCli -Arguments @(
        'webapp', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $WebAppName,
        '--query', 'id',
        '--output', 'tsv'
    )

    $authSettings = Invoke-AzRestJson -Method get `
        -Uri "https://management.azure.com$webAppResourceId/config/authsettingsV2/list?api-version=2024-04-01"
    if (-not $authSettings -or -not $authSettings.properties) {
        throw 'App Service Authentication settings were not returned.'
    }

    $authProperties = $authSettings.properties
    if ($authProperties.platform.enabled -ne $true) {
        throw 'App Service Authentication is not enabled.'
    }
    if ($authProperties.platform.runtimeVersion -ne '~1') {
        throw "App Service Authentication runtime is '$($authProperties.platform.runtimeVersion)'; expected '~1'."
    }
    if ($authProperties.globalValidation.requireAuthentication -ne $false) {
        throw 'EasyAuth must allow anonymous requests so signed telemetry uploads can reach the application.'
    }
    if ($authProperties.globalValidation.unauthenticatedClientAction -ne 'AllowAnonymous') {
        throw "EasyAuth unauthenticated action is '$($authProperties.globalValidation.unauthenticatedClientAction)'; expected 'AllowAnonymous'."
    }

    $azureAdProvider = $authProperties.identityProviders.azureActiveDirectory
    if ($azureAdProvider.enabled -ne $true) {
        throw 'The EasyAuth Microsoft Entra provider is not enabled.'
    }
    if ($azureAdProvider.registration.clientId -ne $ClientId) {
        throw 'The EasyAuth client ID does not match the telemetry dashboard app registration.'
    }
    if ($azureAdProvider.registration.openIdIssuer -ne $Outputs.easyAuthIssuer.value) {
        throw "EasyAuth issuer is '$($azureAdProvider.registration.openIdIssuer)'; expected '$($Outputs.easyAuthIssuer.value)'."
    }

    $allowedAudiences = @($azureAdProvider.validation.allowedAudiences)
    foreach ($expectedAudience in @($ClientId, "api://$ClientId")) {
        if ($allowedAudiences -notcontains $expectedAudience) {
            throw "EasyAuth allowed audiences do not include '$expectedAudience'."
        }
    }
    if ($authProperties.login.tokenStore.enabled -ne $false) {
        throw 'The EasyAuth token store should remain disabled because the SPA manages its own tokens.'
    }

    $miseSetting = Invoke-AzCli -Arguments @(
        'webapp', 'config', 'appsettings', 'list',
        '--resource-group', $ResourceGroupName,
        '--name', $WebAppName,
        '--query', "[?name=='WEBSITE_AAD_ENABLE_MISE'].value | [0]",
        '--output', 'tsv'
    )
    if ($miseSetting -ne 'true') {
        throw "WEBSITE_AAD_ENABLE_MISE is '$miseSetting'; expected 'true'."
    }

    # WEBSITE_AAD_ENABLE_MISE on its own gives MISE v1, which does not satisfy the compliance KPI.
    if (-not $SkipFirstPartyAuth) {
        $firstPartyAuthSetting = Invoke-AzCli -Arguments @(
            'webapp', 'config', 'appsettings', 'list',
            '--resource-group', $ResourceGroupName,
            '--name', $WebAppName,
            '--query', "[?name=='WEBSITE_LOAD_FIRST_PARTY_AUTH'].value | [0]",
            '--output', 'tsv'
        )
        if ($firstPartyAuthSetting -ne 'true') {
            throw "WEBSITE_LOAD_FIRST_PARTY_AUTH is '$firstPartyAuthSetting'; expected 'true'."
        }
    }

    $siteIsLinux = Invoke-AzCli -Arguments @(
        'webapp', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $WebAppName,
        '--query', 'reserved',
        '--output', 'tsv'
    )
    if ($siteIsLinux -eq 'true') {
        throw 'The App Service is running on Linux, where App Service Authentication validated tokens with MISE v1. Expected Windows.'
    }

    $easyAuthReady = $false
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        $easyAuthVersionResponse = $null
        try {
            $easyAuthVersionResponse = Invoke-WebRequest `
                -Uri "$($Outputs.webAppUrl.value)/.auth/version" `
                -TimeoutSec 20 `
                -SkipHttpErrorCheck
        }
        catch {
            # App Service Authentication can recycle after authsettings or app settings change.
        }

        if ($null -eq $easyAuthVersionResponse) {
            Start-Sleep -Seconds 10
            continue
        }

        if ($easyAuthVersionResponse.StatusCode -eq 401) {
            # EasyAuth can require an authenticated session for this endpoint (it did on Linux).
            # A 401 proves the platform route is active instead of falling through to the SPA.
            $easyAuthReady = $true
            break
        }

        if ($easyAuthVersionResponse.StatusCode -eq 200) {
            $versionMatch = [regex]::Match($easyAuthVersionResponse.Content, '\d+(?:\.\d+){2,3}')
            if ($versionMatch.Success) {
                $easyAuthVersion = [version] $versionMatch.Value
                # 1.13.0 is the first App Service Authentication release that can load MISE v2.
                if ($easyAuthVersion -lt [version] '1.13.0') {
                    throw "EasyAuth runtime version is $easyAuthVersion; 1.13.0 or later is required for MISE v2."
                }
                $easyAuthReady = $true
                break
            }
        }

        Start-Sleep -Seconds 10
    }
    if (-not $easyAuthReady) {
        throw 'EasyAuth did not begin intercepting platform authentication routes.'
    }

    $authConfig = Invoke-WebRequest -Uri $Outputs.authConfigUrl.value -TimeoutSec 20 -SkipHttpErrorCheck
    if ($authConfig.StatusCode -ne 200) {
        throw "Authentication configuration endpoint returned HTTP $($authConfig.StatusCode)."
    }
    $authClientConfig = $authConfig.Content | ConvertFrom-Json -ErrorAction Stop
    if ($authClientConfig.clientId -ne $ClientId) {
        throw 'The deployed dashboard authentication configuration has an unexpected client ID.'
    }
    if ($authClientConfig.scope -ne "api://$ClientId/$script:ScopeName") {
        throw "The deployed dashboard scope is '$($authClientConfig.scope)'; expected 'api://$ClientId/$script:ScopeName'."
    }

    $protectedEndpoint = Invoke-WebRequest `
        -Uri "$($Outputs.webAppUrl.value)/api/Telemetry/stats" `
        -TimeoutSec 20 `
        -SkipHttpErrorCheck
    if ($protectedEndpoint.StatusCode -ne 401) {
        throw "Protected dashboard endpoint returned HTTP $($protectedEndpoint.StatusCode) without a token; expected 401."
    }

    $cosmosPublicAccess = Invoke-AzCli -Arguments @(
        'cosmosdb', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $Outputs.cosmosAccountName.value,
        '--query', 'publicNetworkAccess',
        '--output', 'tsv'
    )
    if ($cosmosPublicAccess -ne 'Disabled') {
        throw 'Cosmos DB public network access is not disabled.'
    }

    $keyVaultPublicAccess = Invoke-AzCli -Arguments @(
        'keyvault', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $Outputs.keyVaultName.value,
        '--query', 'properties.publicNetworkAccess',
        '--output', 'tsv'
    )
    if ($keyVaultPublicAccess -ne 'Disabled') {
        throw 'Key Vault public network access is not disabled.'
    }

    $configReferences = Invoke-AzRestJson -Method get `
        -Uri "https://management.azure.com$webAppResourceId/config/configreferences/appsettings?api-version=2026-07-15"
    $telemetrySecretReference = @($configReferences.value) |
        Where-Object { $_.name -eq 'TelemetrySecret' -or $_.id -match '/TelemetrySecret$' } |
        Select-Object -First 1
    $referenceStatus = if ($telemetrySecretReference.properties.status -is [string]) {
        $telemetrySecretReference.properties.status
    }
    else {
        $telemetrySecretReference.properties.status.name
    }
    if ($referenceStatus -ne 'Resolved') {
        throw "TelemetrySecret Key Vault reference status is '$referenceStatus'; expected 'Resolved'."
    }

    $privateEndpoints = @(Invoke-AzCli -Arguments @(
        'network', 'private-endpoint', 'list',
        '--resource-group', $ResourceGroupName,
        '--output', 'json'
    ) -AsJson)
    $privateEndpointCount = $privateEndpoints.Count
    if ($privateEndpointCount -lt 2) {
        throw "Expected at least two private endpoints; found $privateEndpointCount."
    }
}

Assert-AzureContext
Register-ResourceProviders
Assert-WebAppNameAvailable

$existingWebApp = Get-ExistingWebApp
$replaceLinuxSite = $false
if ($existingWebApp -and (Test-IsLinuxWebApp -WebApp $existingWebApp)) {
    $linuxMessage = "App Service '$WebAppName' runs on Linux, and this template deploys it on Windows: App Service Authentication on Linux validated tokens with MISE v1, which does not satisfy the MISE compliance KPI. " +
        'An App Service cannot change operating system, so the site and its plan must be deleted and re-created with the same name, and the service is offline until that deployment completes.'
    if ($WhatIf) {
        Write-Warning "$linuxMessage A real run needs -ReplaceLinuxWebApp; this preview cannot show that replacement."
    }
    elseif (-not $ReplaceLinuxWebApp) {
        throw "$linuxMessage Re-run with -ReplaceLinuxWebApp to do that."
    }
    else {
        $replaceLinuxSite = $true
    }
}

# Which app registration the deployment is tied to. An existing site already names one; it must not
# be swapped for a different one just because the original can no longer be found by name.
$expectedClientId = $AzureAdClientId
if ($existingWebApp) {
    $configuredClientId = Get-ConfiguredEntraClientId
    if (-not $expectedClientId) {
        $expectedClientId = $configuredClientId
    }
    elseif ($configuredClientId -and $configuredClientId -ne $expectedClientId) {
        Write-Warning "The site is configured for app registration $configuredClientId; -AzureAdClientId switches it to $expectedClientId."
    }
}

$redirectUri = "https://$WebAppName.azurewebsites.net"

if ($WhatIf) {
    $clientId = if ($expectedClientId) { $expectedClientId } else { '00000000-0000-0000-0000-000000000000' }
    if ($expectedClientId) {
        try {
            if (-not (Find-EntraApplicationByClientId -ClientId $expectedClientId)) {
                Write-Warning "App registration $expectedClientId no longer exists. A real run stops until it is restored, or until -AllowNewEntraApplication is passed."
            }
        }
        catch {
            Write-Warning "Could not check that app registration $expectedClientId still exists: $($_.Exception.Message)"
        }
    }
}
else {
    $entraApplication = Get-OrCreateEntraApplication -RedirectUri $redirectUri -ExpectedClientId $expectedClientId
    Grant-CurrentUserDashboardAccess -EntraApplication $entraApplication
    $clientId = $entraApplication.AppId
}

$telemetrySecret = [Environment]::GetEnvironmentVariable($TelemetrySecretEnvironmentVariableName)
if ([string]::IsNullOrWhiteSpace($telemetrySecret)) {
    if ($WhatIf) {
        $telemetrySecret = 'synthetic-validation-secret'
    }
    else {
        throw "Environment variable '$TelemetrySecretEnvironmentVariableName' must contain the telemetry signing secret."
    }
}

$templateFile = Join-Path $PSScriptRoot 'azuredeploy.json'
$parameterFile = New-DeploymentParameterFile `
    -TelemetrySecret $telemetrySecret `
    -ClientId $clientId

try {
    $deploymentName = "telemetry-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
    if ($WhatIf) {
        Invoke-AzCli -Arguments @(
            'deployment', 'sub', 'what-if',
            '--name', $deploymentName,
            '--location', $Location,
            '--template-file', $templateFile,
            '--parameters', "@$parameterFile",
            '--result-format', 'ResourceIdOnly',
            '--output', 'json'
        ) | Write-Output
        return
    }

    $preservedRoleAssignments = @()
    if ($replaceLinuxSite) {
        $managedIdentityPrincipalId = if (
            $existingWebApp.PSObject.Properties.Name -contains 'identity' -and
            $existingWebApp.identity -and
            $existingWebApp.identity.PSObject.Properties.Name -contains 'principalId') {
            $existingWebApp.identity.principalId
        }
        else {
            $null
        }

        # Read-only checks first, so a failure leaves the Linux site exactly as it was.
        Assert-AppServicePlanReplaceable -WebApp $existingWebApp
        $preservedRoleAssignments = @(Get-WebAppRoleAssignments -WebApp $existingWebApp -ManagedIdentityPrincipalId $managedIdentityPrincipalId)

        if (-not $SkipFirstPartyAuth) {
            Assert-FirstPartyAuthAccepted
        }

        foreach ($assignment in $preservedRoleAssignments) {
            # Printed first so they can be restored by hand if the deployment fails part-way.
            Write-Host "Will restore site-scoped role '$($assignment.RoleDefinitionName)' for $($assignment.PrincipalType) $($assignment.PrincipalId) after re-creating the site."
        }

        Remove-LinuxWebApp -WebApp $existingWebApp -ManagedIdentityPrincipalId $managedIdentityPrincipalId
    }

    try {
        $deployment = Invoke-AzCli -Arguments @(
            'deployment', 'sub', 'create',
            '--name', $deploymentName,
            '--location', $Location,
            '--template-file', $templateFile,
            '--parameters', "@$parameterFile",
            '--output', 'json'
        ) -AsJson
    }
    catch {
        if (-not $SkipFirstPartyAuth -and "$($_.Exception.Message)" -match '(?i)first[ -]?party') {
            throw ('The deployment failed because App Service did not accept WEBSITE_LOAD_FIRST_PARTY_AUTH on this subscription. ' +
                'The setting is only accepted once App Service has enabled the subscription for first-party authentication. ' +
                "Arrange that and re-run, or re-run with -SkipFirstPartyAuth (App Service Authentication then stays on MISE v1).`n$($_.Exception.Message)")
        }
        throw
    }

    $outputs = $deployment.properties.outputs
    $webAppResourceId = Invoke-AzCli -Arguments @(
        'webapp', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $WebAppName,
        '--query', 'id',
        '--output', 'tsv'
    )

    if ($preservedRoleAssignments.Count -gt 0) {
        Restore-WebAppRoleAssignments -WebAppResourceId $webAppResourceId -Assignments $preservedRoleAssignments
    }

    if (-not $SkipApplicationPublish) {
        Publish-TelemetryApplication -WebAppResourceId $webAppResourceId
    }

    Test-TelemetryDeployment -Outputs $outputs -ClientId $clientId

    Write-Host "Telemetry Service deployed successfully: $($outputs.statsApiUrl.value)"
}
finally {
    if (Test-Path -LiteralPath $parameterFile) {
        Remove-Item -LiteralPath $parameterFile -Force
    }
    Remove-Variable telemetrySecret -ErrorAction SilentlyContinue
}
