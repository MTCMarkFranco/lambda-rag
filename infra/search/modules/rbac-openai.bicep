// Cross-RG role assignment on the OpenAI / Foundry account.
@description('Name of the Cognitive Services / OpenAI account in the target RG.')
param accountName string
@description('Principal to grant Cognitive Services OpenAI User to.')
param principalId string
@description('Full role definition ID (subscription scope).')
param roleDefinitionId string

resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: accountName
}

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account
  name: guid(account.id, principalId, roleDefinitionId)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: roleDefinitionId
  }
}
