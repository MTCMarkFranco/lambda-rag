// All RBAC for the authoring stack. Hosted at the resource-group scope of
// the search + storage. Cross-RG assignments (OpenAI) are delegated to
// rbac-openai.bicep.

@description('Storage account that holds source policies.')
param storageAccountName string

@description('Search service (so we can scope author-side assignments correctly).')
param searchServiceName string

@description('System-assigned principal of the AI Search service.')
param searchPrincipalId string

@description('Resource ID of the existing OpenAI / Foundry account.')
param openAiAccountResourceId string

@description('Object IDs of human authors who should manage the index from their machine.')
param authorObjectIds array = []

var roleIds = {
  storageBlobDataReader: '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
  cognitiveServicesOpenAIUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  searchIndexDataContributor: '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
  searchServiceContributor: '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
}

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: storageAccountName
}

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' existing = {
  name: searchServiceName
}

// Search service identity reads source blobs.
resource searchReadsBlobs 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, searchPrincipalId, roleIds.storageBlobDataReader)
  properties: {
    principalId: searchPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataReader)
  }
}

// Search service identity calls Foundry deployments (chat + embedding).
var openAiParts = split(openAiAccountResourceId, '/')
var openAiSubscriptionId = openAiParts[2]
var openAiResourceGroupName = openAiParts[4]
var openAiAccountName = openAiParts[8]

module openAiAccess 'rbac-openai.bicep' = {
  name: 'rbac-openai'
  scope: resourceGroup(openAiSubscriptionId, openAiResourceGroupName)
  params: {
    accountName: openAiAccountName
    principalId: searchPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.cognitiveServicesOpenAIUser)
  }
}

// Authors get Index Data Contributor + Service Contributor on the search service.
resource authorIndexAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (oid, i) in authorObjectIds: {
  scope: search
  name: guid(search.id, oid, roleIds.searchIndexDataContributor)
  properties: {
    principalId: oid
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.searchIndexDataContributor)
  }
}]

resource authorServiceAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (oid, i) in authorObjectIds: {
  scope: search
  name: guid(search.id, oid, roleIds.searchServiceContributor)
  properties: {
    principalId: oid
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.searchServiceContributor)
  }
}]
