// Container Apps demo of the Microsoft 365 Analytics Insights portal - phase 1: the shared resources.
//
// Everything the two apps need to exist before an image can be built and deployed: the identity
// both apps run as, the registry the image is built into, the Azure SQL database, and - unless an
// existing one is reused - a Container Apps environment. Phase 2 (apps.bicep) adds the web app and
// the nightly data job. Deploy-ContainerAppsDemo.ps1 runs both; see README.md.
//
// There is deliberately nothing else. A real deployment also has Redis, Service Bus, Storage, App
// Insights, Key Vault, AI Language and the importer web jobs; the demo portal needs none of them,
// because its database is rebuilt every night from synthetic data rather than imported.

targetScope = 'resourceGroup'

@description('Region for the resources this template creates.')
param location string = resourceGroup().location

@description('Tags applied to every resource.')
param tags object = {}

@description('Short lower-case prefix used to derive the names left empty below.')
@minLength(3)
@maxLength(16)
param namePrefix string

@description('User-assigned managed identity that the web app and the data job both run as.')
param identityName string = '${namePrefix}-id'

@description('Container registry (Basic) the demo image is built into. Globally unique, alphanumeric. Empty: derived from namePrefix.')
param registryName string = ''

@description('Azure SQL logical server. Globally unique. Empty: derived from namePrefix.')
param sqlServerName string = ''

@description('Region for the SQL server and database. Some subscriptions cannot create SQL servers in every region; the deployment script checks before it starts.')
param sqlLocation string = location

@description('Demo database. Must be named ContosoDemo_*: the data generator refuses any other name.')
param sqlDatabaseName string

@description('true: the Azure SQL Database free offer (serverless General Purpose, auto-pauses when idle, no compute charge within the monthly allowance). false: Basic, 5 DTU (about USD 5 a month, never pauses).')
param sqlUseFreeOffer bool = true

@description('Free offer only: what happens when the monthly free allowance runs out. BillOverUsage keeps the demo up at the normal serverless rate and allows the 15-minute pause delay, so it normally stays inside the allowance; AutoPause caps the cost at zero but stops the database until next month, and Azure then fixes the pause delay at 60 minutes.')
@allowed([
  'AutoPause'
  'BillOverUsage'
])
param sqlFreeLimitExhaustionBehavior string = 'BillOverUsage'

@description('Free offer with BillOverUsage only: idle minutes before the database pauses (15 is the minimum). With AutoPause the free offer only accepts its default of 60 minutes.')
@minValue(15)
param sqlAutoPauseDelayMinutes int = 15

@description('private: SQL has no public endpoint and is reached through a private endpoint in the environment\'s virtual network - required wherever policy turns public network access off on SQL servers. public: the public endpoint, open to Azure services only.')
@allowed([
  'private'
  'public'
])
param sqlNetworkAccess string = 'private'

@description('Create a new Container Apps environment. When false, the script supplies an existing one to phase 2.')
param createEnvironment bool = true

@description('New environment only: its name.')
param environmentName string = ''

@description('New environment only: the Log Analytics workspace its container logs go to.')
param logAnalyticsName string = ''

@description('Private access, new environment: address space of the virtual network created for it.')
param vnetAddressPrefix string = '10.70.0.0/16'

@description('Private access, new environment: the environment\'s subnet (a workload profiles environment needs at least /27).')
param environmentSubnetPrefix string = '10.70.0.0/24'

@description('Private access, new environment: the subnet holding the SQL private endpoint.')
param privateEndpointSubnetPrefix string = '10.70.1.0/27'

@description('Private access, existing environment: a subnet of its virtual network for the SQL private endpoint. The script picks it.')
param privateEndpointSubnetId string = ''

@description('Private access, existing environment: the region of that subnet\'s virtual network, where the private endpoint has to be.')
param privateEndpointLocation string = location

@description('Private access, existing environment: the privatelink.database.windows.net zone its virtual network already resolves from. Empty: a new zone is created and linked to it.')
param privateDnsZoneId string = ''

@description('Private access, existing environment: its virtual network, which a new zone is linked to.')
param environmentVnetId string = ''

var privateSql = sqlNetworkAccess == 'private'
// A private endpoint is only reachable from a virtual network. A new environment gets its own; an
// existing one must already be integrated with one (the script checks), and the endpoint joins it.
var createNetwork = privateSql && createEnvironment
var useExistingZone = privateSql && !createNetwork && !empty(privateDnsZoneId)
var createZone = privateSql && !useExistingZone
var vnetName = '${namePrefix}-vnet'
var endpointSubnetId = createNetwork ? resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, 'private-endpoints') : privateEndpointSubnetId
var linkedVnetId = createNetwork ? resourceId('Microsoft.Network/virtualNetworks', vnetName) : environmentVnetId

// Stable per resource group, so a re-run finds the same registry and server.
var suffix = uniqueString(resourceGroup().id)
var resolvedRegistryName = empty(registryName) ? take('${replace(namePrefix, '-', '')}${suffix}', 50) : registryName
var resolvedSqlServerName = empty(sqlServerName) ? '${namePrefix}-sql-${suffix}' : sqlServerName
var sqlPrivateDnsZoneName = 'privatelink${environment().suffixes.sqlServerHostname}'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: resolvedRegistryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

// AcrPull, so the apps pull the image as their managed identity and no registry password exists.
resource registryPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, 'AcrPull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Microsoft Entra-only, with the apps' managed identity as the Entra admin: there is no SQL login or
// password anywhere, and the data job - which empties and rebuilds the database - has the rights it
// needs without a separate database-user step. An application admin is identified by its client ID.
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: resolvedSqlServerName
  location: sqlLocation
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: privateSql ? 'Disabled' : 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: identity.name
      sid: identity.properties.clientId
      principalType: 'Application'
      tenantId: tenant().tenantId
    }
  }
}

// Public access only. Container Apps on the consumption plan have no fixed outbound address, so the
// server admits Azure services; every connection still needs a Microsoft Entra token for the
// managed identity above.
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (!privateSql) {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: sqlLocation
  tags: tags
  sku: sqlUseFreeOffer ? {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  } : {
    name: 'Basic'
    tier: 'Basic'
    capacity: 5
  }
  properties: union({
    requestedBackupStorageRedundancy: 'Local'
  }, sqlUseFreeOffer ? union({
    useFreeLimit: true
    freeLimitExhaustionBehavior: sqlFreeLimitExhaustionBehavior
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
  }, sqlFreeLimitExhaustionBehavior == 'AutoPause' ? {} : {
    // "Only default value for auto pause delay is allowed for Free Limit database with auto pause
    // exhaustion behavior" - so the delay is only set when the allowance can be exceeded.
    autoPauseDelay: sqlAutoPauseDelayMinutes
  }) : {
    maxSizeBytes: 2147483648
  })
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = if (createNetwork) {
  name: vnetName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: 'container-apps'
        properties: {
          addressPrefix: environmentSubnetPrefix
          delegations: [
            {
              name: 'Microsoft.App.environments'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: 'private-endpoints'
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

resource sqlPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = if (createZone) {
  name: sqlPrivateDnsZoneName
  location: 'global'
  tags: tags
}

// The environment resolves the server's usual name through this zone, so the connection string
// stays <server>.database.windows.net and simply arrives at the private endpoint. NxDomainRedirect:
// a name the zone does not hold - any other server's privatelink name - falls back to public DNS, so
// linking the zone to an existing environment's network cannot break anything else running there.
resource sqlPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = if (createZone) {
  parent: sqlPrivateDnsZone
  name: vnetName
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    resolutionPolicy: 'NxDomainRedirect'
    virtualNetwork: {
      id: linkedVnetId
    }
  }
  dependsOn: [
    vnet
  ]
}

resource sqlPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-11-01' = if (privateSql) {
  name: '${namePrefix}-sql-pe'
  // A private endpoint lives in its network's region, which for an existing environment need not be the demo's.
  location: createNetwork ? location : privateEndpointLocation
  tags: tags
  properties: {
    subnet: {
      id: endpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'sql'
        properties: {
          privateLinkServiceId: sqlServer.id
          groupIds: [
            'sqlServer'
          ]
        }
      }
    ]
  }
  dependsOn: [
    vnet
  ]
}

resource sqlPrivateDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = if (privateSql) {
  parent: sqlPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'sql'
        properties: {
          privateDnsZoneId: useExistingZone ? privateDnsZoneId : sqlPrivateDnsZone.id
        }
      }
    ]
  }
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (createEnvironment) {
  name: empty(logAnalyticsName) ? 'unused-logs' : logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    // A demo writes very little; the cap is a guard against a crash loop turning into a bill.
    workspaceCapping: {
      dailyQuotaGb: 1
    }
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = if (createEnvironment) {
  name: empty(environmentName) ? 'unused-environment' : environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        // Same condition as the workspace, so it always exists when this is evaluated.
        customerId: logs!.properties.customerId
        sharedKey: logs!.listKeys().primarySharedKey
      }
    }
    // External: the portal keeps a public address. The virtual network carries only its outbound
    // traffic, which is how it reaches the SQL private endpoint.
    vnetConfiguration: createNetwork ? {
      infrastructureSubnetId: resourceId('Microsoft.Network/virtualNetworks/subnets', vnetName, 'container-apps')
      internal: false
    } : null
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
  }
  dependsOn: [
    vnet
    sqlPrivateDnsLink
  ]
}

output identityId string = identity.id
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = database.name
output environmentId string = createEnvironment ? containerAppsEnvironment.id : ''
