// =============================================================================
// lambda-rag — AI Search authoring pipeline (Phase A, issue #72)
//
// Provisions the cloud surface for the AUTHORING side of lambda-rag:
//
//   - Azure AI Search service (hosts the rules index + skillset + indexer)
//   - Storage account + blob container for source policy documents
//   - RBAC: Search service identity reads blobs, calls the Foundry endpoint
//
// HARD BOUNDARY: This stack is AUTHORING-TIME ONLY. Runtime evaluation never
// touches Azure AI Search. Phase C guardrails enforce that boundary.
//
// The index schema, skillset, indexer, and datasource are defined as JSON
// alongside this template (see ./rest/*.json) and deployed via the search
// service REST API by ./scripts/deploy-search-assets.ps1 — Bicep does not
// have first-class resource types for those objects today.
// =============================================================================

@description('Short workload prefix used to name resources, e.g. "lambdarag".')
@minLength(3)
@maxLength(11)
param workload string = 'lambdarag'

@description('Environment suffix — dev | test | prod.')
@allowed([
  'dev'
  'test'
  'prod'
])
param environment string = 'dev'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('AI Search SKU. basic is cheapest with semantic ranker support.')
@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param searchSku string = 'basic'

@description('Resource ID of the existing Azure OpenAI / Foundry account that hosts the chat + embedding deployments. The search service identity will be granted Cognitive Services OpenAI User on it.')
param openAiAccountResourceId string

@description('Object IDs of the human authors (Entra ID) who should be able to push to the index from their machine.')
param authorObjectIds array = []

var tags = {
  workload: workload
  environment: environment
  component: 'authoring'
  issue: '72'
}

var nameSuffix = '${workload}-${environment}'

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    name: replace('${workload}auth${environment}', '-', '')
    location: location
    tags: tags
  }
}

module search 'modules/search-service.bicep' = {
  name: 'search'
  params: {
    name: 'srch-${nameSuffix}'
    location: location
    tags: tags
    sku: searchSku
  }
}

module rbac 'modules/rbac.bicep' = {
  name: 'rbac'
  params: {
    storageAccountName: storage.outputs.name
    searchServiceName: search.outputs.name
    searchPrincipalId: search.outputs.principalId
    openAiAccountResourceId: openAiAccountResourceId
    authorObjectIds: authorObjectIds
  }
}

output searchServiceName string = search.outputs.name
output searchEndpoint string = search.outputs.endpoint
output storageAccountName string = storage.outputs.name
output sourceContainerName string = storage.outputs.sourceContainerName
output searchPrincipalId string = search.outputs.principalId
