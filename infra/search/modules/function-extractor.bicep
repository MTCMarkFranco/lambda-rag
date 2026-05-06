// =============================================================================
// lambda-rag — extract-rule Azure Function (issue #79)
//
// Hosts LambdaRag.Authoring.ExtractFunction on Flex Consumption. The function
// is invoked by the AI Search WebApiSkill once per chunk during authoring-time
// indexing. It calls Foundry chat with DefaultAzureCredential (its own
// system-assigned MI), validates against the ExtractedRule schema, and returns
// the rule object to the indexer.
//
// HARD BOUNDARY: authoring-time only. Runtime never calls this function.
// =============================================================================

@description('Function App name (must be globally unique).')
@minLength(2)
@maxLength(60)
param functionAppName string

@description('Region.')
param location string = resourceGroup().location

@description('Tags applied to all resources in this module.')
param tags object = {}

@description('Existing storage account name used to host the Flex deployment package container.')
param deploymentStorageAccountName string

@description('Blob container name used for the Flex deployment package. Created if missing.')
param deploymentContainerName string = 'function-extractor-deploy'

@description('Foundry / Azure OpenAI endpoint.')
param azureOpenAiEndpoint string

@description('Chat deployment name (e.g. gpt-4o-mini).')
param azureOpenAiChatDeployment string = 'gpt-4o-mini'

@description('Application Insights connection string. Optional — set empty to disable.')
param appInsightsConnectionString string = ''

@description('Maximum instance count for Flex Consumption.')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 100

@description('Always-ready instance count (0 = scale to zero).')
@minValue(0)
@maxValue(1000)
param alwaysReadyInstanceCount int = 0

@description('Memory size per instance (MB). Flex supports 512, 2048, 4096.')
@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048

var roleIds = {
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

resource deploymentStorage 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: deploymentStorageAccountName
}

// Container that holds the zipped deployment package for Flex Consumption.
resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  name: '${deploymentStorageAccountName}/default/${deploymentContainerName}'
  properties: {
    publicAccess: 'None'
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${functionAppName}-plan'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${deploymentStorage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
        alwaysReady: [
          {
            name: 'http'
            instanceCount: alwaysReadyInstanceCount
          }
        ]
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '9.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
    }
  }
}

resource appSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: functionApp
  name: 'appsettings'
  properties: union(
    {
      AzureWebJobsStorage__accountName: deploymentStorageAccountName
      AzureWebJobsStorage__credential: 'managedidentity'
      AZURE_OPENAI_ENDPOINT: azureOpenAiEndpoint
      AZURE_OPENAI_CHAT_DEPLOYMENT: azureOpenAiChatDeployment
    },
    empty(appInsightsConnectionString) ? {} : {
      APPLICATIONINSIGHTS_CONNECTION_STRING: appInsightsConnectionString
    }
  )
}

// Function MI needs to read its own deployment package from the storage container.
resource fnDeploymentAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: deploymentStorage
  name: guid(deploymentStorage.id, functionApp.id, roleIds.storageBlobDataOwner)
  properties: {
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleIds.storageBlobDataOwner)
  }
}

output functionAppName string = functionApp.name
output defaultHostName string = functionApp.properties.defaultHostName
output principalId string = functionApp.identity.principalId
output extractRuleEndpoint string = 'https://${functionApp.properties.defaultHostName}/api/extract-rule'
