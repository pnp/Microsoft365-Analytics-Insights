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

    [string] $AzureAdClientId,

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

function Get-OrCreateEntraApplication {
    param(
        [Parameter(Mandatory)]
        [string] $RedirectUri
    )

    $applications = @(Invoke-AzCli -Arguments @(
        'ad', 'app', 'list',
        '--display-name', $EntraAppDisplayName,
        '--output', 'json'
    ) -AsJson | Where-Object { $_.displayName -eq $EntraAppDisplayName })

    if ($applications.Count -gt 1) {
        throw "More than one Entra application has display name '$EntraAppDisplayName'."
    }

    if ($applications.Count -eq 0) {
        Write-Host 'Creating the single-tenant Entra SPA/API registration...'
        $application = Invoke-AzCli -Arguments @(
            'ad', 'app', 'create',
            '--display-name', $EntraAppDisplayName,
            '--sign-in-audience', 'AzureADMyOrg',
            '--output', 'json'
        ) -AsJson
    }
    else {
        $application = $applications[0]
    }

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
            # Linux EasyAuth can require an authenticated session for this endpoint.
            # A 401 proves the platform route is active instead of falling through to the SPA.
            $easyAuthReady = $true
            break
        }

        if ($easyAuthVersionResponse.StatusCode -eq 200) {
            $versionMatch = [regex]::Match($easyAuthVersionResponse.Content, '\d+(?:\.\d+){2,3}')
            if ($versionMatch.Success) {
                $easyAuthVersion = [version] $versionMatch.Value
                if ($easyAuthVersion -le [version] '1.7.0') {
                    throw "EasyAuth runtime version is $easyAuthVersion; a version newer than 1.7.0 is required."
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

$redirectUri = "https://$WebAppName.azurewebsites.net"

if ($WhatIf) {
    $clientId = if ($AzureAdClientId) { $AzureAdClientId } else { '00000000-0000-0000-0000-000000000000' }
}
else {
    $entraApplication = Get-OrCreateEntraApplication -RedirectUri $redirectUri
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

    $deployment = Invoke-AzCli -Arguments @(
        'deployment', 'sub', 'create',
        '--name', $deploymentName,
        '--location', $Location,
        '--template-file', $templateFile,
        '--parameters', "@$parameterFile",
        '--output', 'json'
    ) -AsJson

    $outputs = $deployment.properties.outputs
    $webAppResourceId = Invoke-AzCli -Arguments @(
        'webapp', 'show',
        '--resource-group', $ResourceGroupName,
        '--name', $WebAppName,
        '--query', 'id',
        '--output', 'tsv'
    )

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
