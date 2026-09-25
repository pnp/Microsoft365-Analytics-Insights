// Container Apps demo of the Microsoft 365 Analytics Insights portal - phase 2: the two apps.
//
//   web      the portal, signed in with Microsoft Entra ID exactly as a real deployment is (the app's
//            own OpenID Connect flow and app registration), reading the one demo database.
//   datagen  a scheduled Container Apps job that empties that database and rebuilds it from
//            Tests.FakeDataGen's synthetic Contoso tenant, so the portal always shows recent data.
//
// Both run the same image (see Dockerfile) as the same user-assigned managed identity, which is the
// SQL server's Microsoft Entra admin. Phase 1 (infra.bicep) creates everything referenced here.

targetScope = 'resourceGroup'

@description('Region of the Container Apps environment. Container apps and jobs must be in their environment\'s region.')
param location string

@description('Tags applied to every resource.')
param tags object = {}

@description('Resource ID of the Container Apps environment.')
param environmentId string

@description('The environment\'s default domain. The portal\'s address is <webAppName>.<this>.')
param environmentDefaultDomain string

@description('Workload profile to run on: Consumption in a workload-profiles environment, empty in a legacy consumption-only one.')
param workloadProfileName string = 'Consumption'

@description('Resource ID of the user-assigned managed identity both apps run as.')
param identityId string

@description('Client ID of that identity: the SQL login, and the identity SqlClient asks for a token.')
param identityClientId string

@description('Login server of the registry holding the image.')
param registryServer string

@description('Full image reference, e.g. <registry>.azurecr.io/m365analytics-demo:<tag>.')
param image string

@description('Container app name for the portal. Lower case, digits and hyphens, at most 32 characters.')
@maxLength(32)
param webAppName string

@description('Container Apps job name for the nightly data generator.')
@maxLength(32)
param jobName string

@description('Application (client) ID of the portal\'s Entra app registration.')
param entraClientId string

@description('Client secret of that registration. Stored as a Container Apps secret, never as a plain setting.')
@secure()
param entraClientSecret string

@description('The tenant\'s primary domain, e.g. contoso.onmicrosoft.com.')
param tenantDomain string

@description('Azure SQL server FQDN.')
param sqlServerFqdn string

@description('Demo database name (ContosoDemo_*).')
param sqlDatabaseName string

@description('Which workloads the portal treats as imported. Every flag defaults to false in the product, so without this the portal reports the synthetic data as "not measured". Must equal DemoPortalReadiness.RequiredImportJobSettings - DemoContainerAppsTests fails when the two drift.')
param importJobSettings string = 'GraphUsersMetadata=True;GraphUsageReports=True;GraphCopilotUsageReports=True;Copilot=True;CopilotInteractionHistory=True;ActivityLog=True;WebTraffic=True;Calls=True;GraphTeams=True;SentEmails=True;ImportPowerPlatform=True;ImportDlp=True;CopilotStudioCredits=True;AzureCostManagement=True'

@description('Portal replicas kept warm. 0 scales to zero when idle (cheapest; the first visit then waits for a cold start and signs in again). 1 keeps it always on.')
@minValue(0)
@maxValue(1)
param webMinReplicas int = 0

@description('Portal container CPU (cores).')
param webCpu string = '1.0'

@description('Portal container memory.')
param webMemory string = '2Gi'

@description('When the data job runs, as a cron expression. Container Apps evaluates it in UTC.')
param dataGenCron string = '0 0 * * *'

@description('Extra arguments for "Tests.FakeDataGen demo", e.g. ["--users", "500", "--days", "120"].')
param dataGenArgs array = []

@description('Data job container CPU (cores).')
param dataGenCpu string = '1.0'

@description('Data job container memory.')
param dataGenMemory string = '2Gi'

@description('Optional custom domain for the portal, e.g. analyticsdemo.contoso.com. Its CNAME must point at <webAppName>.<environmentDefaultDomain>.')
param customDomainName string = ''

@description('The environment\'s managed certificate for customDomainName, once issued. Empty: the host name is added unbound, which is what lets the certificate be issued.')
param customDomainCertificateId string = ''

// The address users sign in at: the custom domain when there is one. The default address keeps working.
var webFqdn = empty(customDomainName) ? '${webAppName}.${environmentDefaultDomain}' : customDomainName

// The installer's shape (MARS, a long connect timeout), with SqlClient's own managed identity
// authentication in place of a password. The timeout and retries give an auto-paused serverless
// database the minute or so it takes to resume.
var sqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${identityClientId};Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True;Connect Timeout=120;ConnectRetryCount=6;ConnectRetryInterval=10'

var registries = [
  {
    server: registryServer
    identity: identityId
  }
]

resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: webAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: empty(workloadProfileName) ? null : workloadProfileName
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        customDomains: empty(customDomainName) ? [] : [
          empty(customDomainCertificateId) ? {
            name: customDomainName
            bindingType: 'Disabled'
          } : {
            name: customDomainName
            bindingType: 'SniEnabled'
            certificateId: customDomainCertificateId
          }
        ]
      }
      registries: registries
      secrets: [
        {
          name: 'entra-client-secret'
          value: entraClientSecret
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: image
          resources: {
            cpu: json(webCpu)
            memory: webMemory
          }
          env: [
            // The same settings the installer writes to a real App Service - minus Redis, Service Bus,
            // Storage, App Insights, Key Vault and AI Language, none of which this portal needs.
            { name: 'ClientID', value: entraClientId }
            { name: 'ClientSecret', secretRef: 'entra-client-secret' }
            { name: 'TenantGUID', value: tenant().tenantId }
            { name: 'TenantDomain', value: tenantDomain }
            { name: 'WebAppURL', value: 'https://${webFqdn}' }
            { name: 'ImportJobSettings', value: importJobSettings }
            { name: 'ConnectionStrings__SPOInsightsEntities', value: sqlConnectionString }
            // AppConnectionStrings refuses to start without a Storage connection string, although the
            // portal never uses one (only the importer's blob checkpoint does). The storage emulator's
            // well-known placeholder satisfies it without pointing at anything real.
            { name: 'ConnectionStrings__Storage', value: 'UseDevelopmentStorage=true' }
            // Container Apps terminates TLS and forwards plain HTTP. Without this the OpenID Connect
            // handler builds an http:// redirect URI, which Entra ID rejects.
            { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
            { name: 'AZURE_CLIENT_ID', value: identityClientId }
          ]
        }
      ]
      scale: {
        minReplicas: webMinReplicas
        // One replica: sign-in cookies are protected with keys held in the container, so a second
        // replica could not read a cookie the first one issued.
        maxReplicas: 1
      }
    }
  }
}

resource dataJob 'Microsoft.App/jobs@2024-03-01' = {
  name: jobName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: empty(workloadProfileName) ? null : workloadProfileName
    configuration: {
      triggerType: 'Schedule'
      scheduleTriggerConfig: {
        cronExpression: dataGenCron
        parallelism: 1
        replicaCompletionCount: 1
      }
      replicaTimeout: 10800
      // One retry: --recreate rebuilds from scratch, so a second attempt after a transient failure is
      // always safe.
      replicaRetryLimit: 1
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'datagen'
          image: image
          command: [
            'dotnet'
            '/app/datagen/Tests.FakeDataGen.dll'
          ]
          args: concat([
            'demo'
            '--connection-string'
            sqlConnectionString
            '--recreate'
          ], dataGenArgs)
          resources: {
            cpu: json(dataGenCpu)
            memory: dataGenMemory
          }
          env: [
            { name: 'AZURE_CLIENT_ID', value: identityClientId }
          ]
        }
      ]
    }
  }
}

output webAppUrl string = empty(customDomainName) ? 'https://${web.properties.configuration.ingress.fqdn}' : 'https://${customDomainName}'
output defaultUrl string = 'https://${web.properties.configuration.ingress.fqdn}'
output webAppName string = web.name
output jobName string = dataJob.name
