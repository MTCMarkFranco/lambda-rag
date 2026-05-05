@description('Search service name (must be globally unique, lowercase, 2-60 chars).')
param name string
param location string
param tags object
@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param sku string = 'basic'

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: sku
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    publicNetworkAccess: 'enabled'
    semanticSearch: 'standard'
    // RBAC only — authoring CLI uses DefaultAzureCredential.
    disableLocalAuth: true
    authOptions: null
  }
}

output name string = search.name
output endpoint string = 'https://${search.name}.search.windows.net'
output principalId string = search.identity.principalId
output id string = search.id
