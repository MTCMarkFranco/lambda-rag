@description('Storage account name (lowercase, 3-24 chars, globally unique).')
@minLength(3)
@maxLength(24)
param name string
param location string
param tags object

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blob 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  parent: storage
  name: 'default'
}

resource sourceContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  parent: blob
  name: 'policies'
  properties: {
    publicAccess: 'None'
  }
}

output name string = storage.name
output id string = storage.id
output sourceContainerName string = sourceContainer.name
