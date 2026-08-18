targetScope = 'subscription'

@description('Name of the new resource group.')
@minLength(1)
@maxLength(90)
param resourceGroupName string

@description('Azure region for all regional resources.')
param location string

@description('Existing release-compatible App Service name. Supply this out-of-band.')
@minLength(2)
@maxLength(60)
param webAppName string

@description('Short product prefix used for generated Azure resource names.')
@minLength(3)
@maxLength(18)
param namePrefix string

@description('Address prefix for the dedicated virtual network.')
param vnetAddressPrefix string

@description('Address prefix for the delegated App Service integration subnet.')
param appIntegrationSubnetPrefix string

@description('Address prefix for the private endpoint subnet.')
param privateEndpointSubnetPrefix string

@description('Application (client) ID of the single-tenant Entra SPA/API registration.')
param azureAdClientId string

@description('Signed-upload shared secret. This value is stored in Key Vault.')
@secure()
param telemetrySecret string

@description('Optional additional resource tags.')
param tags object = {}

var defaultTags = {
  Workload: 'Microsoft365AnalyticsTelemetry'
  ManagedBy: 'Bicep'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: union(defaultTags, tags)
}

module telemetryResources './resources.bicep' = {
  name: 'telemetry-resources'
  scope: resourceGroup
  params: {
    location: location
    webAppName: webAppName
    namePrefix: namePrefix
    vnetAddressPrefix: vnetAddressPrefix
    appIntegrationSubnetPrefix: appIntegrationSubnetPrefix
    privateEndpointSubnetPrefix: privateEndpointSubnetPrefix
    azureAdTenantId: tenant().tenantId
    azureAdClientId: azureAdClientId
    telemetrySecret: telemetrySecret
    tags: union(defaultTags, tags)
  }
}

output resourceGroupName string = resourceGroup.name
output webAppName string = telemetryResources.outputs.webAppName
output webAppUrl string = telemetryResources.outputs.webAppUrl
output statsApiUrl string = telemetryResources.outputs.statsApiUrl
output authConfigUrl string = telemetryResources.outputs.authConfigUrl
output easyAuthIssuer string = telemetryResources.outputs.easyAuthIssuer
output cosmosAccountName string = telemetryResources.outputs.cosmosAccountName
output keyVaultName string = telemetryResources.outputs.keyVaultName
output appServicePrincipalId string = telemetryResources.outputs.appServicePrincipalId
