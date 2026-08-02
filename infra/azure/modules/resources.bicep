// ---------------------------------------------------------------------------
// Everything inside the resource group.
//
//   Container Apps        the API
//   Azure SQL             the database — Entra-only authentication, no SQL login
//   Cache for Redis       distributed cache
//   Key Vault             the connection strings the API reads at startup
//   Container Registry    the image the app runs
//   Managed identities    one for the app, one for CI (federated to GitHub OIDC)
// ---------------------------------------------------------------------------

@description('Azure region for every resource.')
param location string

@allowed([
  'dev'
  'staging'
  'prod'
])
param environment string

@description('Composed "<prefix>-<environment>" name used for every resource.')
param resourceName string

@description('Short prefix, reused for resources whose names cannot contain hyphens.')
param resourcePrefix string

@description('Repository allowed to authenticate as CI, as "owner/repo". Empty disables the federation.')
param githubRepository string

@description('Container image for the API. Empty falls back to a placeholder so the first deploy succeeds.')
param apiImage string

@description('Object ID of the Entra principal that administers the SQL server.')
param sqlAdminObjectId string

@description('Display name of that principal.')
param sqlAdminLogin string

param tags object

var isProd = environment == 'prod'

// Several Azure resource types reject hyphens or cap the name at 24 characters.
var compactName = toLower(replace('${resourcePrefix}${environment}${uniqueString(resourceGroup().id)}', '-', ''))
var registryName = take(compactName, 24)
var keyVaultName = take('kv${compactName}', 24)

// ---------------------------------------------------------------------------
// Identities
// ---------------------------------------------------------------------------

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${resourceName}-api-id'
  location: location
  tags: tags
}

resource ciIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${resourceName}-ci-id'
  location: location
  tags: tags
}

// Federated credentials let GitHub Actions exchange its OIDC token for an Azure
// token. There is no client secret to create, store, rotate, or leak — and none
// should ever be added. One credential per subject; GitHub sends a different
// subject for a branch push and for an environment deployment.
resource ciFederationBranch 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = if (!empty(githubRepository)) {
  parent: ciIdentity
  name: 'github-main'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:ref:refs/heads/main'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

resource ciFederationEnvironment 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = if (!empty(githubRepository)) {
  parent: ciIdentity
  name: 'github-environment-${environment}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:environment:${environment}'
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

// Contributor on this resource group only — never on the subscription. CI manages
// what it deploys and nothing else. Narrow it further if your pipeline allows.
resource ciContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(githubRepository)) {
  name: guid(resourceGroup().id, ciIdentity.id, 'contributor')
  properties: {
    // Contributor
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
    principalId: ciIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// Container Registry
// ---------------------------------------------------------------------------

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  // registryName is always at least 18 characters — resourcePrefix (2+) plus environment
  // (3+) plus a 13-character uniqueString — so the minimum-length rule cannot fire.
  #disable-next-line BCP334
  name: registryName
  location: location
  tags: tags
  sku: {
    name: isProd ? 'Premium' : 'Basic'
  }
  properties: {
    // Managed-identity pull only. Admin credentials are a shared password by another name.
    adminUserEnabled: false
  }
}

// AcrPull for the API's identity, so Container Apps can pull without a registry password.
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, apiIdentity.id, 'acrpull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// Key Vault
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    // RBAC rather than access policies: one authorisation model, auditable like everything else.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: isProd ? 90 : 7
    enablePurgeProtection: isProd ? true : null
    publicNetworkAccess: 'Enabled'
  }
}

resource apiReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, apiIdentity.id, 'kv-secrets-user')
  scope: keyVault
  properties: {
    // Key Vault Secrets User — read secret values, nothing else.
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// Azure SQL
//
// Entra-only authentication: the server has no SQL administrator login and no
// password, so there is no database credential to store anywhere.
//
// One manual step after the first deploy — connect as the Entra admin and run:
//   CREATE USER [<api-identity-name>] FROM EXTERNAL PROVIDER;
//   ALTER ROLE db_datareader ADD MEMBER [<api-identity-name>];
//   ALTER ROLE db_datawriter ADD MEMBER [<api-identity-name>];
// See README.md.
// ---------------------------------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${resourceName}-sql'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'app'
  location: location
  tags: tags
  sku: isProd ? {
    name: 'S2'
    tier: 'Standard'
  } : {
    name: 'Basic'
    tier: 'Basic'
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    zoneRedundant: false
    requestedBackupStorageRedundancy: isProd ? 'Geo' : 'Local'
  }
}

// Allows other Azure services, including Container Apps, to reach the server.
// For anything beyond dev, replace this with a private endpoint.
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Passwordless: the API authenticates with its managed identity.
resource sqlConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ConnectionStrings--Default'
  properties: {
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Managed Identity;User Id=${apiIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;'
  }
}

// ---------------------------------------------------------------------------
// Azure Cache for Redis
//
// Redis is protocol-identical across providers, so nothing in the application
// changes between here and Memorystore — only this file does.
// ---------------------------------------------------------------------------

resource redis 'Microsoft.Cache/redis@2024-03-01' = {
  name: '${resourceName}-redis'
  location: location
  tags: tags
  properties: {
    sku: isProd ? {
      name: 'Standard'
      family: 'C'
      capacity: 1
    } : {
      name: 'Basic'
      family: 'C'
      capacity: 0
    }
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    redisVersion: '6'
  }
}

resource redisConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ConnectionStrings--Redis'
  properties: {
    value: '${redis.properties.hostName}:${redis.properties.sslPort},password=${redis.listKeys().primaryKey},ssl=True,abortConnect=False'
  }
}

// ---------------------------------------------------------------------------
// Container Apps
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${resourceName}-logs'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: isProd ? 90 : 30
  }
}

resource containerAppEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${resourceName}-env'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${resourceName}-api'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${apiIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: apiIdentity.id
        }
      ]
      // Values are pulled from Key Vault with the app's managed identity — nothing
      // sensitive is written into this template or into the revision definition.
      secrets: [
        {
          name: 'db-connection'
          keyVaultUrl: sqlConnectionSecret.properties.secretUri
          identity: apiIdentity.id
        }
        {
          name: 'redis-connection'
          keyVaultUrl: redisConnectionSecret.properties.secretUri
          identity: apiIdentity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: empty(apiImage) ? 'mcr.microsoft.com/k8se/quickstart:latest' : apiImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            // The provider switch the application reads at startup. See ADR-0002.
            {
              name: 'CLOUD_PROVIDER'
              value: 'azure'
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: isProd ? 'Production' : 'Staging'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: apiIdentity.properties.clientId
            }
            {
              name: 'KeyVault__Uri'
              value: keyVault.properties.vaultUri
            }
            {
              name: 'ConnectionStrings__Default'
              secretRef: 'db-connection'
            }
            {
              name: 'ConnectionStrings__Redis'
              secretRef: 'redis-connection'
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health/ready'
                port: 8080
              }
              initialDelaySeconds: 10
              periodSeconds: 5
              failureThreshold: 12
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
              }
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: isProd ? 1 : 0
        maxReplicas: 4
      }
    }
  }
  dependsOn: [
    acrPull
    apiReadsSecrets
  ]
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output containerRegistryLoginServer string = registry.properties.loginServer
output keyVaultUri string = keyVault.properties.vaultUri
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output apiIdentityName string = apiIdentity.name
output apiIdentityClientId string = apiIdentity.properties.clientId
output ciIdentityPrincipalId string = ciIdentity.properties.principalId
output ciIdentityClientId string = ciIdentity.properties.clientId
