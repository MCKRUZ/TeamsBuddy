#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Deploys Teams Meeting Assistant to Azure
.DESCRIPTION
    Complete deployment script that:
    1. Creates Azure AD app registration
    2. Configures Graph API permissions
    3. Deploys Azure infrastructure
    4. Stores secrets in Key Vault
    5. Builds and publishes .NET applications
.PARAMETER ResourceGroupName
    Name of the Azure resource group
.PARAMETER Location
    Azure region (e.g., eastus, westus2)
.PARAMETER Environment
    Environment name (dev, staging, prod)
.PARAMETER TenantId
    Azure AD Tenant ID (optional, will use current tenant)
#>

param(
    [Parameter(Mandatory=$true)]
    [string]$ResourceGroupName,
    
    [Parameter(Mandatory=$false)]
    [string]$Location = "eastus",
    
    [Parameter(Mandatory=$false)]
    [string]$Environment = "dev",
    
    [Parameter(Mandatory=$false)]
    [string]$TenantId
)

$ErrorActionPreference = "Stop"

# Color output functions
function Write-Step { param($Message) Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Success { param($Message) Write-Host "✓ $Message" -ForegroundColor Green }
function Write-Error { param($Message) Write-Host "✗ $Message" -ForegroundColor Red }
function Write-Warning { param($Message) Write-Host "⚠ $Message" -ForegroundColor Yellow }

Write-Host @"
╔════════════════════════════════════════════════════════════╗
║     Teams Meeting Assistant - Azure Deployment Script      ║
╚════════════════════════════════════════════════════════════╝
"@ -ForegroundColor Cyan

# Step 1: Verify prerequisites
Write-Step "Verifying prerequisites..."

# Check Azure CLI
try {
    $azVersion = az version --output json | ConvertFrom-Json
    Write-Success "Azure CLI version: $($azVersion.'azure-cli')"
} catch {
    Write-Error "Azure CLI not found. Install from: https://aka.ms/installazurecli"
    exit 1
}

# Check .NET SDK
try {
    $dotnetVersion = dotnet --version
    Write-Success ".NET SDK version: $dotnetVersion"
    
    if ([version]$dotnetVersion -lt [version]"8.0.0") {
        Write-Error ".NET 8.0 or higher required"
        exit 1
    }
} catch {
    Write-Error ".NET SDK not found. Install from: https://dotnet.microsoft.com/download"
    exit 1
}

# Step 2: Login to Azure
Write-Step "Logging in to Azure..."

$currentAccount = az account show 2>$null
if (-not $currentAccount) {
    Write-Warning "Not logged in to Azure"
    az login
}

$account = az account show | ConvertFrom-Json
Write-Success "Logged in as: $($account.user.name)"
Write-Success "Subscription: $($account.name) ($($account.id))"

if (-not $TenantId) {
    $TenantId = $account.tenantId
}

Write-Success "Tenant ID: $TenantId"

# Step 3: Create Resource Group
Write-Step "Creating resource group..."

$rgExists = az group exists --name $ResourceGroupName
if ($rgExists -eq "false") {
    az group create --name $ResourceGroupName --location $Location
    Write-Success "Resource group '$ResourceGroupName' created"
} else {
    Write-Success "Resource group '$ResourceGroupName' already exists"
}

# Step 4: Create Azure AD App Registration
Write-Step "Creating Azure AD app registration..."

$appName = "Teams-Meeting-Assistant-$Environment"
$existingApp = az ad app list --display-name $appName --query "[0]" | ConvertFrom-Json

if ($existingApp) {
    Write-Warning "App registration '$appName' already exists"
    $appId = $existingApp.appId
    $appObjectId = $existingApp.id
} else {
    # Create app registration
    $app = az ad app create --display-name $appName --query "{appId:appId, id:id}" | ConvertFrom-Json
    $appId = $app.appId
    $appObjectId = $app.id
    Write-Success "App registration created: $appId"
}

# Step 5: Create Service Principal
Write-Step "Creating service principal..."

$spExists = az ad sp list --filter "appId eq '$appId'" --query "[0].appId" --output tsv
if (-not $spExists) {
    az ad sp create --id $appId
    Write-Success "Service principal created"
    Start-Sleep -Seconds 10  # Wait for propagation
} else {
    Write-Success "Service principal already exists"
}

# Step 6: Configure Graph API Permissions
Write-Step "Configuring Microsoft Graph API permissions..."

# Microsoft Graph App ID (constant)
$graphAppId = "00000003-0000-0000-c000-000000000000"

# Required permissions
$permissions = @(
    @{
        ResourceAppId = $graphAppId
        ResourceAccess = @(
            @{
                Id = "df021288-bdef-4463-88db-98f22de89214"  # User.Read.All (Application)
                Type = "Role"
            },
            @{
                Id = "b633e1c5-b582-4048-a93e-9f11b44c7e96"  # OnlineMeetings.Read.All (Application)
                Type = "Role"
            },
            @{
                Id = "a4a80d8d-d69e-4376-8b78-7f4b28c03e8c"  # OnlineMeetings.ReadWrite.All (Application)
                Type = "Role"
            },
            @{
                Id = "c1684f21-1984-47fa-9d61-2dc8c296bb70"  # Calls.AccessMedia.All (Application)
                Type = "Role"
            }
        )
    }
)

$requiredResourceAccess = $permissions | ConvertTo-Json -Depth 10 -Compress

# Update app permissions
az ad app update --id $appObjectId --required-resource-accesses "[$requiredResourceAccess]"
Write-Success "Graph API permissions configured"

Write-Warning "IMPORTANT: Admin consent required for Graph API permissions"
Write-Host "Visit: https://portal.azure.com/#blade/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/CallAnAPI/appId/$appId/isMSAApp/" -ForegroundColor Yellow

# Step 7: Create Client Secret
Write-Step "Creating client secret..."

$secretName = "deployment-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$secret = az ad app credential reset --id $appObjectId --display-name $secretName --query "password" --output tsv

Write-Success "Client secret created (expires in 1 year)"
Write-Warning "Store this secret securely - it won't be shown again!"

# Step 8: Deploy Azure Infrastructure
Write-Step "Deploying Azure infrastructure with Bicep..."

$deploymentName = "teams-assistant-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

$deployment = az deployment group create `
    --name $deploymentName `
    --resource-group $ResourceGroupName `
    --template-file "./infrastructure/main.bicep" `
    --parameters environmentName=$Environment `
    --parameters appName="teams-meeting-assistant" `
    --query "properties.outputs" | ConvertFrom-Json

$apiAppUrl = $deployment.apiAppUrl.value
$webAppUrl = $deployment.webAppUrl.value
$keyVaultName = $deployment.keyVaultName.value
$openAIEndpoint = $deployment.openAIEndpoint.value

Write-Success "Infrastructure deployed successfully!"
Write-Host "  API URL: $apiAppUrl" -ForegroundColor Gray
Write-Host "  Web URL: $webAppUrl" -ForegroundColor Gray
Write-Host "  Key Vault: $keyVaultName" -ForegroundColor Gray

# Step 9: Store Secrets in Key Vault
Write-Step "Storing secrets in Key Vault..."

# Wait for Key Vault RBAC to propagate
Write-Host "Waiting for Key Vault RBAC propagation (30 seconds)..."
Start-Sleep -Seconds 30

az keyvault secret set --vault-name $keyVaultName --name "AzureAdClientId" --value $appId --output none
az keyvault secret set --vault-name $keyVaultName --name "AzureAdClientSecret" --value $secret --output none

# Get SignalR connection string
$signalRName = "teams-meeting-assistant-signalr-$Environment"
$signalRConnectionString = az signalr key list `
    --name $signalRName `
    --resource-group $ResourceGroupName `
    --query "primaryConnectionString" --output tsv

az keyvault secret set --vault-name $keyVaultName --name "SignalRConnectionString" --value $signalRConnectionString --output none

# Get OpenAI API key
$openAIName = "teams-meeting-assistant-openai-$Environment"
$openAIApiKey = az cognitiveservices account keys list `
    --name $openAIName `
    --resource-group $ResourceGroupName `
    --query "key1" --output tsv

az keyvault secret set --vault-name $keyVaultName --name "OpenAIApiKey" --value $openAIApiKey --output none

Write-Success "Secrets stored in Key Vault"

# Step 10: Build and Publish Applications
Write-Step "Building .NET applications..."

# Build API
Push-Location "./src/TeamsMeetingAssistant.Api"
dotnet publish -c Release -o "../../publish/api"
Pop-Location
Write-Success "API application built"

# Build Web
Push-Location "./src/TeamsMeetingAssistant.Web"
dotnet publish -c Release -o "../../publish/web"
Pop-Location
Write-Success "Web application built"

# Step 11: Deploy to Azure App Service
Write-Step "Deploying API to Azure App Service..."

$apiAppName = "teams-meeting-assistant-api-$Environment"
Push-Location "./publish/api"
Compress-Archive -Path * -DestinationPath "../api.zip" -Force
Pop-Location

az webapp deployment source config-zip `
    --resource-group $ResourceGroupName `
    --name $apiAppName `
    --src "./publish/api.zip"

Write-Success "API deployed to $apiAppUrl"

Write-Step "Deploying Web to Azure App Service..."

$webAppName = "teams-meeting-assistant-web-$Environment"
Push-Location "./publish/web"
Compress-Archive -Path * -DestinationPath "../web.zip" -Force
Pop-Location

az webapp deployment source config-zip `
    --resource-group $ResourceGroupName `
    --name $webAppName `
    --src "./publish/web.zip"

Write-Success "Web deployed to $webAppUrl"

# Step 12: Restart App Services
Write-Step "Restarting App Services..."

az webapp restart --name $apiAppName --resource-group $ResourceGroupName
az webapp restart --name $webAppName --resource-group $ResourceGroupName

Write-Success "App Services restarted"

# Step 13: Verify Deployment
Write-Step "Verifying deployment..."

Start-Sleep -Seconds 10

$healthCheck = Invoke-RestMethod -Uri "$apiAppUrl/health" -ErrorAction SilentlyContinue
if ($healthCheck) {
    Write-Success "Health check passed!"
} else {
    Write-Warning "Health check failed - check application logs"
}

# Step 14: Generate Teams App Manifest
Write-Step "Generating Teams app manifest..."

$teamsAppId = [Guid]::NewGuid().ToString()

$manifest = @{
    "`$schema" = "https://developer.microsoft.com/json-schemas/teams/v1.16/MicrosoftTeams.schema.json"
    manifestVersion = "1.16"
    version = "1.0.0"
    id = $teamsAppId
    packageName = "com.company.teamsmeetingassistant"
    developer = @{
        name = "Your Company"
        websiteUrl = $webAppUrl
        privacyUrl = "$webAppUrl/privacy"
        termsOfUseUrl = "$webAppUrl/terms"
    }
    icons = @{
        color = "color.png"
        outline = "outline.png"
    }
    name = @{
        short = "Meeting Assistant"
        full = "Teams Meeting Assistant - AI-Powered Question Suggestions"
    }
    description = @{
        short = "Real-time meeting transcript monitoring with AI question suggestions"
        full = "Monitor Teams meeting transcripts in real-time and receive AI-powered question suggestions to drive productive conversations"
    }
    accentColor = "#4A90E2"
    staticTabs = @(
        @{
            entityId = "dashboard"
            name = "Dashboard"
            contentUrl = "$webAppUrl/meeting/{meetingId}"
            websiteUrl = "$webAppUrl"
            scopes = @("personal", "groupchat")
        }
    )
    permissions = @("identity", "messageTeamMembers")
    validDomains = @(
        $apiAppUrl.Replace("https://", ""),
        $webAppUrl.Replace("https://", "")
    )
    webApplicationInfo = @{
        id = $appId
        resource = "api://$($apiAppUrl.Replace('https://', ''))"
    }
}

# Create Teams app directory
$teamsAppDir = "./teams-app"
New-Item -ItemType Directory -Path $teamsAppDir -Force | Out-Null

$manifest | ConvertTo-Json -Depth 10 | Set-Content "$teamsAppDir/manifest.json"

# Create placeholder icons (user should replace these)
Write-Warning "Creating placeholder icons - replace with actual app icons!"

# Create simple color icon (192x192)
$colorIconBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
[System.IO.File]::WriteAllBytes("$teamsAppDir/color.png", [System.Convert]::FromBase64String($colorIconBase64))

# Create simple outline icon (32x32)
$outlineIconBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
[System.IO.File]::WriteAllBytes("$teamsAppDir/outline.png", [System.Convert]::FromBase64String($outlineIconBase64))

# Create Teams app package
Push-Location $teamsAppDir
Compress-Archive -Path * -DestinationPath "../teams-app.zip" -Force
Pop-Location

Write-Success "Teams app package created: teams-app.zip"

# Final Output
Write-Host @"

╔════════════════════════════════════════════════════════════╗
║              Deployment Complete! 🎉                        ║
╚════════════════════════════════════════════════════════════╝

📋 Deployment Summary
════════════════════════════════════════════════════════════

Azure Resources:
  Resource Group:    $ResourceGroupName
  Location:          $Location
  Environment:       $Environment

Applications:
  API URL:           $apiAppUrl
  Web URL:           $webAppUrl
  Health Check:      $apiAppUrl/health

Azure AD:
  App Registration:  $appName
  Client ID:         $appId
  Tenant ID:         $TenantId

Azure Services:
  Key Vault:         $keyVaultName
  OpenAI Endpoint:   $openAIEndpoint

Teams App:
  Package:           teams-app.zip
  App ID:            $teamsAppId

════════════════════════════════════════════════════════════

⚠️  IMPORTANT NEXT STEPS:

1. Grant Admin Consent for Graph API permissions:
   https://portal.azure.com/#blade/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/CallAnAPI/appId/$appId/isMSAApp/

2. Replace placeholder icons in ./teams-app/ directory:
   - color.png (192x192 pixels)
   - outline.png (32x32 pixels, transparent background)

3. Upload teams-app.zip to Teams Admin Center:
   https://admin.teams.microsoft.com/policies/manage-apps

4. Approve the app for your organization

5. Test the deployment:
   - Create a Teams meeting with transcription enabled
   - Navigate to: $webAppUrl/meeting/{meetingId}
   - Click "Start Monitoring"

════════════════════════════════════════════════════════════

📚 Documentation:
   - API Docs:     $apiAppUrl/swagger
   - Logs:         https://portal.azure.com/#@$TenantId/resource/subscriptions/$($account.id)/resourceGroups/$ResourceGroupName
   - Key Vault:    https://portal.azure.com/#@$TenantId/resource/subscriptions/$($account.id)/resourceGroups/$ResourceGroupName/providers/Microsoft.KeyVault/vaults/$keyVaultName

"@ -ForegroundColor Green

# Save deployment info
$deploymentInfo = @{
    DeploymentDate = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    ResourceGroup = $ResourceGroupName
    Location = $Location
    Environment = $Environment
    ApiUrl = $apiAppUrl
    WebUrl = $webAppUrl
    AppId = $appId
    TenantId = $TenantId
    KeyVault = $keyVaultName
}

$deploymentInfo | ConvertTo-Json | Set-Content "./deployment-info.json"
Write-Success "Deployment info saved to: deployment-info.json"

Write-Host "`nDeployment script completed successfully! ✓" -ForegroundColor Green