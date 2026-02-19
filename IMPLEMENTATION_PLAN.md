# TeamsBuddy Implementation Plan

> **Goal:** Transform the working prototype (with mocks) into a production-ready Teams meeting intelligence system with real Azure integrations.

---

## 📋 **Implementation Phases Overview**

| Phase | Component | Status | Est. Time | Blockers |
|-------|-----------|--------|-----------|----------|
| 1 | Azure AD App Registration | ⏳ Not Started | 30 min | None |
| 2 | Azure Infrastructure (Bicep) | ⏳ Not Started | 4-6 hrs | Phase 1 |
| 3 | Real Graph API Service | ⏳ Not Started | 8-12 hrs | Phase 1, 2 |
| 4 | Real Azure OpenAI Service | ⏳ Not Started | 4-6 hrs | Phase 2 |
| 5 | Blazor Web Dashboard | ⏳ Not Started | 6-8 hrs | Phase 3, 4 |
| 6 | Comprehensive Testing | ⏳ Not Started | 8-10 hrs | Phase 3, 4, 5 |
| 7 | E2E Real Meeting Test | ⏳ Not Started | 2-4 hrs | All phases |

**Total Estimated Time:** 32-56 hours (4-7 working days)

---

## 🎯 **Phase 1: Azure AD App Registration** ⏳

### **Objective**
Register an Azure AD application to authenticate with Microsoft Graph API for Teams meeting transcript access.

### **Prerequisites**
- ✅ Azure subscription with admin access
- ✅ Microsoft 365 tenant with Teams license
- ✅ Global Administrator or Application Administrator role

### **Step-by-Step Guide**

#### **Step 1.1: Create Azure AD App Registration**

1. **Navigate to Azure Portal:**
   ```
   https://portal.azure.com
   ```

2. **Go to Azure Active Directory:**
   - Click "Azure Active Directory" in left sidebar
   - Or search for "Azure Active Directory" in top search bar

3. **Create New App Registration:**
   - Click "App registrations" in left menu
   - Click "+ New registration"

4. **Configure Registration:**
   ```
   Name: TeamsMeetingAssistant
   Supported account types: Accounts in this organizational directory only (Single tenant)
   Redirect URI: Leave blank for now (backend API doesn't need redirect)
   ```

5. **Click "Register"**

6. **SAVE THESE VALUES** (you'll need them for configuration):
   ```
   Tenant ID (Directory ID): ________________________________________
   Application (client) ID:  ________________________________________
   ```

#### **Step 1.2: Configure API Permissions**

1. **Navigate to API Permissions:**
   - In your app registration, click "API permissions" in left menu

2. **Add Microsoft Graph Permissions:**
   - Click "+ Add a permission"
   - Select "Microsoft Graph"
   - Select "Application permissions" (not Delegated)

3. **Add These Permissions:**
   - **OnlineMeetings.Read.All** — Read online meeting details
   - **User.Read.All** — Read user profiles (for speaker info)
   - **Calls.AccessMedia.All** — Access media for transcription webhooks

4. **Grant Admin Consent:**
   - Click "✓ Grant admin consent for [Your Org]"
   - Click "Yes" to confirm
   - ✅ All permissions should show green checkmark "Granted for [Your Org]"

#### **Step 1.3: Create Client Secret**

1. **Navigate to Certificates & Secrets:**
   - Click "Certificates & secrets" in left menu

2. **Create New Client Secret:**
   - Click "+ New client secret"
   - Description: `TeamsMeetingAssistant-Dev`
   - Expires: 24 months (or as per org policy)
   - Click "Add"

3. **IMMEDIATELY COPY THE SECRET VALUE:**
   ```
   Secret Value: ____________________________________________
   (You can't see this again after you leave this page!)
   ```

4. **SAVE TO PASSWORD MANAGER:**
   ```
   Service: Azure AD - TeamsMeetingAssistant
   Tenant ID: [from Step 1.1]
   Client ID: [from Step 1.1]
   Client Secret: [just copied]
   ```

#### **Step 1.4: Verify Configuration**

Run this PowerShell script to test authentication:

```powershell
# Install required module if not already installed
Install-Module -Name Microsoft.Graph -Scope CurrentUser

# Set variables (replace with your actual values)
$TenantId = "YOUR_TENANT_ID"
$ClientId = "YOUR_CLIENT_ID"
$ClientSecret = "YOUR_CLIENT_SECRET"

# Convert secret to secure string
$SecureSecret = ConvertTo-SecureString -String $ClientSecret -AsPlainText -Force
$Credential = New-Object -TypeName System.Management.Automation.PSCredential `
    -ArgumentList $ClientId, $SecureSecret

# Connect to Microsoft Graph
Connect-MgGraph -TenantId $TenantId -ClientSecretCredential $Credential

# Test: Get organization info (should return your org details)
Get-MgOrganization | Select-Object DisplayName, Id

# Disconnect
Disconnect-MgGraph
```

**Expected Output:**
```
DisplayName         Id
-----------         --
Contoso             12345678-abcd-1234-abcd-1234567890ab
```

If you see your organization name, **authentication works!** ✅

#### **Step 1.5: Document Configuration**

Create a secure configuration file (DO NOT COMMIT TO GIT):

**File: `TeamsMeetingAssistant/src/TeamsMeetingAssistant.Api/appsettings.Development.json`**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AzureAd": {
    "TenantId": "YOUR_TENANT_ID",
    "ClientId": "YOUR_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET"
  },
  "GraphApi": {
    "BaseUrl": "https://graph.microsoft.com/v1.0",
    "Scopes": ["https://graph.microsoft.com/.default"]
  },
  "TranscriptProcessing": {
    "PollingIntervalSeconds": 5,
    "MaxSegmentsPerBatch": 50,
    "QuestionGenerationThresholdSeconds": 30,
    "MaxConcurrentMeetings": 10
  }
}
```

**⚠️ IMPORTANT:** Add to `.gitignore`:
```
appsettings.Development.json
appsettings.*.json
!appsettings.json
```

### **Checkpoint ✅**

- [ ] Azure AD app registered
- [ ] Tenant ID, Client ID, Client Secret saved securely
- [ ] API permissions granted (3 permissions with admin consent)
- [ ] Authentication verified with PowerShell test
- [ ] Configuration documented in appsettings.Development.json
- [ ] Sensitive files added to .gitignore

---

## 🏗️ **Phase 2: Azure Infrastructure Deployment** ⏳

### **Objective**
Deploy all Azure resources needed for the application using Bicep IaC.

### **Prerequisites**
- ✅ Phase 1 complete (Azure AD app registered)
- ✅ Azure CLI installed (`az --version`)
- ✅ Bicep CLI installed (`az bicep version`)

### **Resources to Deploy**

1. **Resource Group** — Container for all resources
2. **App Service Plan** — Compute for Web Apps
3. **App Service (API)** — ASP.NET Core Web API
4. **App Service (Web)** — Blazor Server
5. **Azure SignalR Service** — Real-time streaming
6. **Azure OpenAI** — GPT-4 for question generation
7. **Azure Key Vault** — Secret storage
8. **Application Insights** — Monitoring and logs
9. **Storage Account** — Optional for transcript history

### **Step 2.1: Create Bicep Infrastructure File**

**File: `TeamsMeetingAssistant/infrastructure/main.bicep`**

*(This will be created in next step)*

### **Step 2.2: Deploy Infrastructure**

```bash
# Login to Azure
az login

# Set subscription (if you have multiple)
az account set --subscription "Your Subscription Name"

# Create resource group
az group create \
  --name rg-teams-assistant-dev \
  --location eastus

# Deploy Bicep template
az deployment group create \
  --resource-group rg-teams-assistant-dev \
  --template-file infrastructure/main.bicep \
  --parameters environmentName=dev appName=teams-assistant
```

### **Step 2.3: Configure Secrets in Key Vault**

After deployment, add secrets:

```bash
# Get Key Vault name from deployment output
$kvName = "teams-assistant-kv-dev"

# Set Azure AD credentials
az keyvault secret set --vault-name $kvName --name AzureAdTenantId --value "YOUR_TENANT_ID"
az keyvault secret set --vault-name $kvName --name AzureAdClientId --value "YOUR_CLIENT_ID"
az keyvault secret set --vault-name $kvName --name AzureAdClientSecret --value "YOUR_CLIENT_SECRET"

# Set SignalR connection string
$signalRConn = az signalr key list --name "teams-assistant-signalr-dev" --resource-group rg-teams-assistant-dev --query primaryConnectionString -o tsv
az keyvault secret set --vault-name $kvName --name SignalRConnectionString --value $signalRConn

# Set OpenAI API key
$openAIKey = az cognitiveservices account keys list --name "teams-assistant-openai-dev" --resource-group rg-teams-assistant-dev --query key1 -o tsv
az keyvault secret set --vault-name $kvName --name OpenAIApiKey --value $openAIKey
```

### **Checkpoint ✅**

- [ ] All Azure resources deployed
- [ ] Key Vault populated with secrets
- [ ] App Service identity has Key Vault access
- [ ] Deployment outputs saved (API URL, Web URL, Key Vault name)

---

## 🔌 **Phase 3: Implement Real Graph API Service** ⏳

### **Objective**
Replace `MockGraphTranscriptService` with real Microsoft Graph SDK integration.

### **Implementation Files**

1. **GraphTranscriptService.cs** — Real Graph API client
2. **GraphAuthenticationService.cs** — Handle auth
3. **Update Program.cs** — Switch from mock to real service

### **Testing Strategy**

1. Unit tests with mocked Graph SDK
2. Integration tests with test Teams meeting
3. VTT parser validation with real transcript data

### **Checkpoint ✅**

- [ ] GraphTranscriptService implemented
- [ ] Authentication working
- [ ] VTT parsing working with real data
- [ ] Polly retry policies added
- [ ] Unit tests passing

---

## 🤖 **Phase 4: Implement Real Azure OpenAI Service** ⏳

### **Objective**
Replace `MockOpenAIQuestionService` with real Azure OpenAI GPT-4 integration.

### **Implementation Files**

1. **AzureOpenAIQuestionService.cs** — Real OpenAI client
2. **Prompt engineering** — Quality question generation
3. **Update Program.cs** — Switch from mock to real service

### **Checkpoint ✅**

- [ ] OpenAI service implemented
- [ ] Prompt engineering optimized
- [ ] Token limits enforced
- [ ] Question quality validated
- [ ] Unit tests passing

---

## 🎨 **Phase 5: Build Blazor Web Dashboard** ⏳

### **Objective**
Create the web UI for viewing live transcripts and AI question suggestions.

### **Components to Build**

1. **MeetingDashboard.razor** — Main page
2. **TranscriptViewer.razor** — Live transcript display
3. **QuestionCard.razor** — AI question suggestions
4. **MeetingControls.razor** — Start/stop buttons
5. **SpeakerAvatar.razor** — Speaker visualization

### **Checkpoint ✅**

- [ ] All components created
- [ ] SignalR client working
- [ ] Real-time updates displaying
- [ ] UI polished and responsive

---

## ✅ **Phase 6: Write Comprehensive Tests** ⏳

### **Test Coverage Requirements**

- **Unit Tests:** 80%+ coverage
- **Integration Tests:** All API endpoints
- **E2E Tests:** Full user workflows

### **Checkpoint ✅**

- [ ] Unit tests: 80%+ coverage
- [ ] Integration tests passing
- [ ] E2E tests passing
- [ ] Coverage report generated

---

## 🚀 **Phase 7: End-to-End Testing** ⏳

### **Objective**
Test the complete system with a real Teams meeting.

### **Test Workflow**

1. Create Teams meeting with transcription enabled
2. Start monitoring via API
3. Speak in meeting, verify transcript appears
4. Verify AI questions generated
5. Stop monitoring
6. Review Application Insights logs

### **Checkpoint ✅**

- [ ] Real meeting tested end-to-end
- [ ] Transcripts streaming correctly
- [ ] Questions generating with good quality
- [ ] No errors in Application Insights
- [ ] Performance acceptable (< 10 sec latency)

---

## 📝 **Current Status**

**Last Updated:** 2026-02-09

| Phase | Status | Notes |
|-------|--------|-------|
| 1 | ⏳ Ready to start | Awaiting user to begin Azure AD setup |
| 2 | ⏳ Blocked | Needs Phase 1 |
| 3 | ⏳ Blocked | Needs Phase 1, 2 |
| 4 | ⏳ Blocked | Needs Phase 2 |
| 5 | ⏳ Blocked | Needs Phase 3, 4 |
| 6 | ⏳ Blocked | Needs Phase 3, 4, 5 |
| 7 | ⏳ Blocked | Needs all phases |

---

## 🎯 **Next Action**

**Start with Phase 1: Azure AD App Registration**

Follow the step-by-step guide above to:
1. Create Azure AD app
2. Configure permissions
3. Generate client secret
4. Test authentication

Once Phase 1 is complete, we'll move to Phase 2 (Infrastructure deployment).
