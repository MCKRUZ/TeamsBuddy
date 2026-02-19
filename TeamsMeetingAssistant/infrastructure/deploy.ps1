# TeamsMeetingAssistant Azure Infrastructure Deployment Script
# This script deploys all Azure resources using Bicep

param(
    [Parameter(Mandatory=$false)]
    [string]$SubscriptionId,

    [Parameter(Mandatory=$false)]
    [string]$ResourceGroupName = "rg-teams-assistant-dev",

    [Parameter(Mandatory=$false)]
    [string]$Location = "eastus",

    [Parameter(Mandatory=$false)]
    [string]$EnvironmentName = "dev",

    [Parameter(Mandatory=$false)]
    [string]$AppName = "teams-assistant"
)

# Script settings
$ErrorActionPreference = "Stop"
$WarningPreference = "SilentlyContinue"

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host " Teams Meeting Assistant Deployment" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Read Azure AD credentials from appsettings
Write-Host "[1/6] Reading Azure AD credentials..." -ForegroundColor Yellow
$configPath = Join-Path $PSScriptRoot "..\src\TeamsMeetingAssistant.Api\appsettings.Development.json"
if (-not (Test-Path $configPath)) {
    Write-Error "Configuration file not found: $configPath"
    exit 1
}

$config = Get-Content $configPath | ConvertFrom-Json
$tenantId = $config.AzureAd.TenantId
$clientId = $config.AzureAd.ClientId
$clientSecret = $config.AzureAd.ClientSecret

if ([string]::IsNullOrEmpty($tenantId) -or $tenantId -eq "REPLACE_WITH_YOUR_TENANT_ID") {
    Write-Error "Azure AD credentials not configured in appsettings.Development.json"
    exit 1
}

Write-Host "  ✓ Tenant ID: $tenantId" -ForegroundColor Green
Write-Host "  ✓ Client ID: $clientId" -ForegroundColor Green
Write-Host ""

# Step 2: Login to Azure
Write-Host "[2/6] Connecting to Azure..." -ForegroundColor Yellow
$currentContext = Get-AzContext -ErrorAction SilentlyContinue

if ($null -eq $currentContext) {
    Write-Host "  Please sign in to Azure..." -ForegroundColor Cyan
    Connect-AzAccount
} else {
    Write-Host "  ✓ Already connected as: $($currentContext.Account.Id)" -ForegroundColor Green
}

# Set subscription if provided
if ($SubscriptionId) {
    Set-AzContext -SubscriptionId $SubscriptionId
}

$context = Get-AzContext
Write-Host "  ✓ Subscription: $($context.Subscription.Name)" -ForegroundColor Green
Write-Host ""

# Step 3: Create Resource Group
Write-Host "[3/6] Creating resource group..." -ForegroundColor Yellow
$rg = Get-AzResourceGroup -Name $ResourceGroupName -ErrorAction SilentlyContinue

if ($null -eq $rg) {
    New-AzResourceGroup -Name $ResourceGroupName -Location $Location | Out-Null
    Write-Host "  ✓ Created: $ResourceGroupName in $Location" -ForegroundColor Green
} else {
    Write-Host "  ✓ Already exists: $ResourceGroupName" -ForegroundColor Green
}
Write-Host ""

# Step 4: Validate Bicep template
Write-Host "[4/6] Validating Bicep template..." -ForegroundColor Yellow
$templatePath = Join-Path $PSScriptRoot "main.bicep"

$validationResult = Test-AzResourceGroupDeployment `
    -ResourceGroupName $ResourceGroupName `
    -TemplateFile $templatePath `
    -azureAdTenantId $tenantId `
    -azureAdClientId $clientId `
    -azureAdClientSecret $clientSecret `
    -environmentName $EnvironmentName `
    -appName $AppName

if ($validationResult) {
    Write-Host "  ✗ Validation failed:" -ForegroundColor Red
    $validationResult | Format-List
    exit 1
}

Write-Host "  ✓ Bicep template is valid" -ForegroundColor Green
Write-Host ""

# Step 5: Deploy infrastructure
Write-Host "[5/6] Deploying infrastructure..." -ForegroundColor Yellow
Write-Host "  This will take 5-10 minutes..." -ForegroundColor Cyan
Write-Host ""

$deploymentName = "TeamsMeetingAssistant-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

$deployment = New-AzResourceGroupDeployment `
    -Name $deploymentName `
    -ResourceGroupName $ResourceGroupName `
    -TemplateFile $templatePath `
    -azureAdTenantId $tenantId `
    -azureAdClientId $clientId `
    -azureAdClientSecret $clientSecret `
    -environmentName $EnvironmentName `
    -appName $AppName `
    -Verbose

if ($deployment.ProvisioningState -eq "Succeeded") {
    Write-Host "  ✓ Deployment successful!" -ForegroundColor Green
} else {
    Write-Host "  ✗ Deployment failed: $($deployment.ProvisioningState)" -ForegroundColor Red
    exit 1
}
Write-Host ""

# Step 6: Display outputs
Write-Host "[6/6] Deployment Summary" -ForegroundColor Yellow
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "API URL:          " -NoNewline
Write-Host $deployment.Outputs.apiAppUrl.Value -ForegroundColor Cyan
Write-Host "Web URL:          " -NoNewline
Write-Host $deployment.Outputs.webAppUrl.Value -ForegroundColor Cyan
Write-Host "Key Vault:        " -NoNewline
Write-Host $deployment.Outputs.keyVaultName.Value -ForegroundColor Cyan
Write-Host "OpenAI Endpoint:  " -NoNewline
Write-Host $deployment.Outputs.openAIEndpoint.Value -ForegroundColor Cyan
Write-Host "SignalR Endpoint: " -NoNewline
Write-Host $deployment.Outputs.signalREndpoint.Value -ForegroundColor Cyan
Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "✓ Deployment complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Build and publish the API: dotnet publish src/TeamsMeetingAssistant.Api" -ForegroundColor White
Write-Host "  2. Build and publish the Web: dotnet publish src/TeamsMeetingAssistant.Web" -ForegroundColor White
Write-Host "  3. Deploy to Azure App Services using Azure CLI or Visual Studio" -ForegroundColor White
Write-Host ""

# Save outputs to file
$outputPath = Join-Path $PSScriptRoot "deployment-outputs.json"
$deployment.Outputs | ConvertTo-Json | Out-File $outputPath
Write-Host "Deployment outputs saved to: $outputPath" -ForegroundColor Cyan
