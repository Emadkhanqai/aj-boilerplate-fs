// ---------------------------------------------------------------------------
// Entry point. Subscription-scoped, so it creates its own resource group and
// nothing needs to pre-exist.
//
//   az deployment sub create \
//     --name dev-001 --location <location> \
//     --template-file main.bicep \
//     --parameters environment=dev location=<location> resourcePrefix=<prefix>
//
// infra/gcp/ provisions the same logical shape. See ADR-0002.
// ---------------------------------------------------------------------------

targetScope = 'subscription'

@description('Deployment environment. Drives sizing, redundancy, and deletion protection.')
@allowed([
  'dev'
  'staging'
  'prod'
])
param environment string

@description('Azure region for every resource.')
param location string

@minLength(2)
@maxLength(12)
@description('Short prefix for every resource name. Keep it short — several Azure resource types cap names at 24 characters.')
param resourcePrefix string = 'app'

@description('The repository allowed to authenticate as CI, as "owner/repo". Only this repository can obtain a token.')
param githubRepository string = ''

@description('Container image for the API, including its tag or digest. Leave empty on first deploy to start from a placeholder image.')
param apiImage string = ''

@description('Object ID of the Microsoft Entra principal that administers the SQL server. Required — the server has no SQL login.')
param sqlAdminObjectId string

@description('Display name of that principal.')
param sqlAdminLogin string

@description('Tags applied to every resource that supports them.')
param tags object = {}

var resourceName = '${resourcePrefix}-${environment}'

var allTags = union(
  {
    environment: environment
    managedBy: 'bicep'
  },
  tags
)

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${resourceName}-rg'
  location: location
  tags: allTags
}

module resources 'modules/resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    location: location
    environment: environment
    resourceName: resourceName
    resourcePrefix: resourcePrefix
    githubRepository: githubRepository
    apiImage: apiImage
    sqlAdminObjectId: sqlAdminObjectId
    sqlAdminLogin: sqlAdminLogin
    tags: allTags
  }
}

@description('Public URL of the API.')
output apiUrl string = resources.outputs.apiUrl

@description('Login server of the container registry to push the API image to.')
output containerRegistryLoginServer string = resources.outputs.containerRegistryLoginServer

@description('Value for the AZURE_CLIENT_ID repository secret.')
output ciClientId string = resources.outputs.ciIdentityClientId

@description('Fully qualified name of the SQL server.')
output sqlServerFqdn string = resources.outputs.sqlServerFqdn

@description('Name of the created resource group.')
output resourceGroupName string = rg.name
