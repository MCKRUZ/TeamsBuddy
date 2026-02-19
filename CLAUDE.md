# Project: TeamsBuddy (Teams Meeting Transcript Real-Time Monitor)

## Overview
Real-time Microsoft Teams meeting transcript monitoring with AI-powered question suggestions using ASP.NET Core Web API, Blazor Server, Azure SignalR, and Azure OpenAI.

## Stack
- **.NET 9.0** (API & Services) / **.NET 8.0** (Core & Infrastructure)
- **C# 12** with records, pattern matching, async/await
- **ASP.NET Core Web API** with Background Services (IHostedService)
- **Blazor Server** for web UI
- **Microsoft Graph SDK 5.x** for Teams transcript access
- **Azure SignalR** for real-time streaming
- **Azure OpenAI** (GPT-4) for question generation
- **Bicep** for infrastructure as code

## Solution Structure
```
TeamsMeetingAssistant/
├── src/
│   ├── TeamsMeetingAssistant.Core/          # Domain models, interfaces
│   ├── TeamsMeetingAssistant.Api/           # Web API + Background Services
│   ├── TeamsMeetingAssistant.Web/           # Blazor Server UI
│   ├── TeamsMeetingAssistant.Services/      # Business logic
│   └── TeamsMeetingAssistant.Infrastructure/# Graph API, SignalR, OpenAI clients
└── tests/
    ├── TeamsMeetingAssistant.Tests.Unit/
    └── TeamsMeetingAssistant.Tests.Integration/
```

## Core Domain Models
- `TranscriptSegment` — VTT transcript entry (speaker, content, timestamp)
- `MeetingSession` — Active meeting state (polling status, last processed time)
- `QuestionSuggestion` — AI-generated question (category, confidence, rationale)

## Critical Implementation Patterns

### 1. Background Service Polling
- `TranscriptPollingService` polls Graph API every 5 seconds
- Processes active sessions in parallel (max 5 concurrent)
- Updates session store with LastProcessedTime
- **Never block UI thread** — all polling is async

### 2. VTT Transcript Parsing
Graph API returns WEBVTT format:
```
WEBVTT

00:00:00.000 --> 00:00:03.000
<v Speaker Name>Text content here
```
Parse with `VttTranscriptParser.Parse(vttContent, baseTime)` → `List<TranscriptSegment>`

### 3. Azure OpenAI Question Generation
- Trigger: 30+ seconds since last question AND 3+ new segments
- Categories: "Clarification", "Deep-dive", "Follow-up", "Summary"
- Return JSON with question, rationale, category, confidence score

### 4. SignalR Real-Time Streaming
- Hub methods: `SendTranscriptUpdateAsync`, `SendQuestionSuggestionsAsync`
- Clients join meeting-specific groups via `JoinMeeting(meetingId)`
- Blazor components handle `OnNewTranscript` and `QuestionSuggestions` events

## Configuration & Secrets
**appsettings.json (API):**
- `AzureAd:TenantId/ClientId/ClientSecret` — Graph API auth
- `AzureOpenAI:Endpoint/ApiKey/DeploymentName` — GPT-4 endpoint
- `Azure:SignalR:ConnectionString` — SignalR service
- `TranscriptProcessing:PollingIntervalSeconds` — Default 5

**Local dev:** Use `dotnet user-secrets`
**Production:** Azure Key Vault with `@Microsoft.KeyVault(...)` references

## Graph API Requirements
**Permissions needed:**
- `OnlineMeetings.Read.All`
- `User.Read.All`
- `Calls.AccessMedia.All` (for webhooks)

**Authentication:** `ClientSecretCredential` with app-only access

## Build & Test
```bash
# Build solution
dotnet build TeamsMeetingAssistant.sln

# Run API (port 7001)
dotnet run --project src/TeamsMeetingAssistant.Api

# Run Web (port 7002)
dotnet run --project src/TeamsMeetingAssistant.Web

# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Verification Checklist
After making changes:
1. **Build passes:** `dotnet build`
2. **Tests pass:** `dotnet test`
3. **No mutation:** All updates use immutable patterns (`with` keyword for records)
4. **Async all the way:** No `.Result` or `.Wait()` calls
5. **Secrets in Key Vault:** No hardcoded connection strings

## Common Mistakes

### ❌ Mutating Records
```csharp
// WRONG
session.Status = MeetingStatus.Completed; // Can't mutate record!

// CORRECT
var updatedSession = session with { Status = MeetingStatus.Completed };
await _sessionStore.AddOrUpdateAsync(updatedSession);
```

### ❌ Blocking Async Code
```csharp
// WRONG
var result = _transcriptService.GetNewTranscriptSegmentsAsync(...).Result;

// CORRECT
var result = await _transcriptService.GetNewTranscriptSegmentsAsync(...);
```

### ❌ Missing Cancellation Tokens
```csharp
// WRONG
public async Task PollTranscriptsAsync()

// CORRECT
public async Task PollTranscriptsAsync(CancellationToken cancellationToken)
```

### ❌ Hardcoded Secrets
```csharp
// WRONG
var apiKey = "sk-proj-xxxxx";

// CORRECT
var apiKey = _configuration["AzureOpenAI:ApiKey"];
```

### ❌ Not Handling Graph API Throttling
Always use Polly retry policies for Graph API calls (handles 429 TooManyRequests).

## Azure Deployment
```bash
# Deploy infrastructure
az deployment group create \
  --resource-group rg-teams-assistant \
  --template-file infrastructure/main.bicep

# Set secrets in Key Vault
az keyvault secret set --vault-name <kv-name> --name AzureAdClientId --value "<client-id>"
az keyvault secret set --vault-name <kv-name> --name AzureAdClientSecret --value "<client-secret>"
az keyvault secret set --vault-name <kv-name> --name SignalRConnectionString --value "<signalr-conn>"
az keyvault secret set --vault-name <kv-name> --name OpenAIApiKey --value "<openai-key>"
```

## Project Phases
1. ✅ Core API setup with dependency injection
2. ⏳ Graph API transcript polling service
3. ⏳ VTT parser implementation
4. ⏳ SignalR hub and streaming
5. ⏳ Azure OpenAI question generation
6. ⏳ Blazor Server UI components
7. ⏳ Bicep infrastructure templates
8. ⏳ Unit & integration tests

## Known Limitations
- Single tenant only (multi-tenant future)
- 5-second polling delay (Graph API doesn't support push for transcripts)
- Maximum 10 concurrent meetings
- Transcription must be enabled in Teams settings

## References
- Microsoft Graph API: https://learn.microsoft.com/graph/api/resources/callrecording
- Azure SignalR: https://learn.microsoft.com/azure/azure-signalr/
- Azure OpenAI: https://learn.microsoft.com/azure/ai-services/openai/
- Global coding standards: `~/.claude/rules/*.md`
