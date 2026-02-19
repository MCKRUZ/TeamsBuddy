@description('Location for all resources')
param location string = resourceGroup().location

@description('Environment name (dev, staging, prod)')
param environmentName string = 'dev'

@description('Application name')
param appName string = 'teams-assistant'

@description('Azure AD Tenant ID')
param azureAdTenantId string

@description('Azure AD Client ID')
param azureAdClientId string

@secure()
@description('Azure AD Client Secret')
param azureAdClientSecret string

// Variables
var uniqueSuffix = uniqueString(resourceGroup().id)
var apiAppName = '${appName}-api-${environmentName}-${uniqueSuffix}'
var webAppName = '${appName}-web-${environmentName}-${uniqueSuffix}'
var appServicePlanName = '${appName}-plan-${environmentName}'
var signalRName = '${appName}-signalr-${environmentName}-${uniqueSuffix}'
var openAIName = '${appName}-openai-${environmentName}-${uniqueSuffix}'
var appInsightsName = '${appName}-insights-${environmentName}'
var keyVaultName = '${appName}-kv-${environmentName}-${take(uniqueSuffix, 6)}'
var logAnalyticsName = '${appName}-logs-${environmentName}'
var aiSearchName = '${appName}-search-${environmentName}-${take(uniqueSuffix, 8)}'

// Log Analytics Workspace (required for Application Insights)
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2021-06-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// App Service Plan
resource appServicePlan 'Microsoft.Web/serverfarms@2022-09-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: 'B1' // Basic tier - upgrade to S1 or P1V2 for production
    tier: 'Basic'
    capacity: 1
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

// Key Vault
resource keyVault 'Microsoft.KeyVault/vaults@2023-02-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
  }
}

// API App Service
resource apiApp 'Microsoft.Web/sites@2022-09-01' = {
  name: apiAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'AzureAd__TenantId'
          value: azureAdTenantId
        }
        {
          name: 'AzureAd__ClientId'
          value: azureAdClientId
        }
        {
          name: 'AzureAd__ClientSecret'
          value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=AzureAdClientSecret)'
        }
        {
          name: 'Azure__SignalR__ConnectionString'
          value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=SignalRConnectionString)'
        }
        {
          name: 'AzureOpenAI__Endpoint'
          value: openAI.properties.endpoint
        }
        {
          name: 'AzureOpenAI__ApiKey'
          value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=OpenAIApiKey)'
        }
        {
          name: 'AzureOpenAI__DeploymentName'
          value: 'gpt-4'
        }
        {
          name: 'GraphApi__BaseUrl'
          value: 'https://graph.microsoft.com/v1.0'
        }
        {
          name: 'TranscriptProcessing__PollingIntervalSeconds'
          value: '5'
        }
        {
          name: 'TranscriptProcessing__QuestionGenerationThresholdSeconds'
          value: '30'
        }
        {
          name: 'AzureAISearch__Endpoint'
          value: 'https://${aiSearch.name}.search.windows.net'
        }
        {
          name: 'AzureAISearch__ApiKey'
          value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=AISearchApiKey)'
        }
        {
          name: 'AzureAISearch__IndexName'
          value: 'org-knowledge'
        }
        {
          name: 'AzureAISearch__SemanticConfigName'
          value: 'org-knowledge-semantic'
        }
      ]
    }
  }
}

// Web App Service (Blazor)
resource webApp 'Microsoft.Web/sites@2022-09-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'ApiBaseUrl'
          value: 'https://${apiApp.properties.defaultHostName}'
        }
        {
          name: 'Azure__SignalR__Endpoint'
          value: 'https://${signalR.properties.hostName}'
        }
      ]
    }
  }
}

// Azure SignalR Service
resource signalR 'Microsoft.SignalRService/signalR@2023-02-01' = {
  name: signalRName
  location: location
  sku: {
    name: 'Free_F1' // Free tier - upgrade to Standard_S1 for production
    tier: 'Free'
    capacity: 1
  }
  kind: 'SignalR'
  properties: {
    features: [
      {
        flag: 'ServiceMode'
        value: 'Default'
      }
    ]
    cors: {
      allowedOrigins: [
        'https://${apiApp.properties.defaultHostName}'
        'https://${webApp.properties.defaultHostName}'
      ]
    }
  }
}

// Azure AI Search (org-wide knowledge base)
resource aiSearch 'Microsoft.Search/searchServices@2023-11-01' = {
  name: aiSearchName
  location: location
  sku: {
    name: 'basic' // Supports semantic search; upgrade to 'standard' for production
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    publicNetworkAccess: 'enabled'
    semanticSearch: 'free' // Enable semantic search (free tier available in basic+)
  }
}

// Azure OpenAI
resource openAI 'Microsoft.CognitiveServices/accounts@2023-05-01' = {
  name: openAIName
  location: location
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAIName
    publicNetworkAccess: 'Enabled'
  }
}

// GPT-4 Deployment
resource gpt4Deployment 'Microsoft.CognitiveServices/accounts/deployments@2023-05-01' = {
  parent: openAI
  name: 'gpt-4'
  sku: {
    name: 'Standard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4'
      version: '0613'
    }
  }
}

// Store secrets in Key Vault
resource secretAzureAdClientSecret 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'AzureAdClientSecret'
  properties: {
    value: azureAdClientSecret
  }
}

resource secretSignalRConnectionString 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'SignalRConnectionString'
  properties: {
    value: signalR.listKeys().primaryConnectionString
  }
}

resource secretOpenAIApiKey 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'OpenAIApiKey'
  properties: {
    value: openAI.listKeys().key1
  }
}

resource secretAISearchApiKey 'Microsoft.KeyVault/vaults/secrets@2023-02-01' = {
  parent: keyVault
  name: 'AISearchApiKey'
  properties: {
    value: aiSearch.listAdminKeys().primaryKey
  }
}

// RBAC: Grant API App access to Key Vault secrets
resource apiKeyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, apiApp.id, 'Key Vault Secrets User')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// RBAC: Grant Web App access to Key Vault secrets
resource webKeyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, webApp.id, 'Key Vault Secrets User')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output apiAppUrl string = 'https://${apiApp.properties.defaultHostName}'
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output keyVaultName string = keyVault.name
output openAIEndpoint string = openAI.properties.endpoint
output signalREndpoint string = 'https://${signalR.properties.hostName}'
output resourceGroupName string = resourceGroup().name
output aiSearchEndpoint string = 'https://${aiSearch.name}.search.windows.net'
