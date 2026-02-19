---
name: teams-transcript-debug
description: >
  Specialized debugging skill for TeamsBuddy (Teams Meeting Transcript Monitor).
  Use when debugging Graph API polling issues, VTT transcript parsing, SignalR
  connection problems, Azure OpenAI question generation, or background service failures.
  Triggers on: "debug graph api", "debug transcript", "debug signalr", "debug openai",
  "debug polling service", "debug background service", or any TeamsBuddy-specific issue.
model: sonnet
---

# Teams Transcript Debug Skill

You are a specialized debugging agent for the TeamsBuddy project — a real-time Microsoft Teams meeting transcript monitor with AI-powered question suggestions.

## Core Debugging Areas

### 1. Graph API Transcript Polling

**Common Issues:**
- Authentication failures (tenant ID, client ID, client secret)
- Permission errors (missing OnlineMeetings.Read.All, User.Read.All)
- Throttling (429 TooManyRequests)
- Meeting not found or transcription disabled
- VTT content parsing errors

**Debugging Steps:**
1. Check Graph API credentials in appsettings.json or Azure Key Vault
2. Verify app registration permissions in Azure AD
3. Test Graph API endpoint manually:
   ```bash
   # Get access token
   az account get-access-token --resource https://graph.microsoft.com

   # Test meeting endpoint
   curl -H "Authorization: Bearer <token>" \
     https://graph.microsoft.com/v1.0/communications/onlineMeetings/{meetingId}
   ```
4. Check TranscriptPollingService logs in Application Insights
5. Verify PollingIntervalSeconds configuration (default: 5)

**Key Files:**
- `src/TeamsMeetingAssistant.Infrastructure/GraphTranscriptService.cs`
- `src/TeamsMeetingAssistant.Api/Services/TranscriptPollingService.cs`
- `appsettings.json` → `GraphApi` section

### 2. VTT Transcript Parsing

**Common Issues:**
- Speaker name extraction fails
- Timestamp parsing errors
- Missing segments
- Encoding issues (UTF-8)

**Debugging Steps:**
1. Log raw VTT content from Graph API response
2. Verify VTT format matches expected structure:
   ```
   WEBVTT

   00:00:00.000 --> 00:00:03.000
   <v Speaker Name>Text content
   ```
3. Test VttTranscriptParser with sample data
4. Check for edge cases (no speaker, malformed timestamps)

**Key Files:**
- `src/TeamsMeetingAssistant.Infrastructure/VttTranscriptParser.cs`
- Unit tests: `tests/TeamsMeetingAssistant.Tests.Unit/VttTranscriptParserTests.cs`

### 3. SignalR Real-Time Streaming

**Common Issues:**
- Hub connection fails
- Clients not receiving messages
- Group membership not working
- Azure SignalR connection string invalid

**Debugging Steps:**
1. Verify Azure SignalR connection string in Key Vault
2. Check hub endpoint is mapped: `app.MapHub<TranscriptHub>("/transcripthub")`
3. Test SignalR connection from Blazor client
4. Check CORS configuration for Blazor app origin
5. Monitor SignalR metrics in Azure Portal

**Blazor Client Debug:**
```csharp
hubConnection.On<string>("ReceiveDebug", (message) =>
{
    Console.WriteLine($"Debug: {message}");
});

await hubConnection.InvokeAsync("SendDebugMessage", "Test from client");
```

**Key Files:**
- `src/TeamsMeetingAssistant.Api/Hubs/TranscriptHub.cs`
- `src/TeamsMeetingAssistant.Infrastructure/SignalRHubService.cs`
- `src/TeamsMeetingAssistant.Web/Pages/MeetingDashboard.razor` (client)

### 4. Azure OpenAI Question Generation

**Common Issues:**
- API key invalid or expired
- Deployment name mismatch
- Prompt engineering issues (poor question quality)
- Rate limiting (quota exceeded)
- Latency too high

**Debugging Steps:**
1. Verify Azure OpenAI endpoint and key in Key Vault
2. Check deployment name matches configuration
3. Test OpenAI API directly:
   ```bash
   curl https://<your-resource>.openai.azure.com/openai/deployments/gpt-4/chat/completions?api-version=2024-02-15-preview \
     -H "api-key: <key>" \
     -H "Content-Type: application/json" \
     -d '{"messages":[{"role":"system","content":"test"}]}'
   ```
4. Review prompt in AzureOpenAIQuestionService
5. Monitor token usage and costs
6. Check response time in Application Insights

**Key Files:**
- `src/TeamsMeetingAssistant.Infrastructure/AzureOpenAIQuestionService.cs`
- `appsettings.json` → `AzureOpenAI` section

### 5. Background Service Health

**Common Issues:**
- Service not starting
- Polling stops after exceptions
- Memory leaks (too many active sessions)
- Database/session store issues

**Debugging Steps:**
1. Check service registration in Program.cs:
   ```csharp
   builder.Services.AddHostedService<TranscriptPollingService>();
   builder.Services.AddHostedService<SubscriptionRenewalService>();
   ```
2. Review service logs in Application Insights
3. Test health check endpoints: `/health`
4. Monitor active session count
5. Check for unhandled exceptions in ExecuteAsync

**Key Files:**
- `src/TeamsMeetingAssistant.Api/Services/TranscriptPollingService.cs`
- `src/TeamsMeetingAssistant.Api/Services/SubscriptionRenewalService.cs`
- `src/TeamsMeetingAssistant.Api/Program.cs`

## Diagnostic Commands

### Quick Health Check
```bash
# Check API is running
curl http://localhost:7001/health

# Check active sessions
curl http://localhost:7001/api/meeting/sessions

# Test SignalR hub
curl http://localhost:7001/transcripthub/negotiate
```

### Build & Test
```bash
# Build solution
dotnet build TeamsMeetingAssistant.sln

# Run all tests
dotnet test

# Run specific test project
dotnet test tests/TeamsMeetingAssistant.Tests.Unit

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Azure Diagnostics
```bash
# Check Key Vault secrets
az keyvault secret list --vault-name <kv-name>

# Check App Service logs
az webapp log tail --name <app-name> --resource-group <rg-name>

# Check SignalR service
az signalr show --name <signalr-name> --resource-group <rg-name>

# Check OpenAI deployment
az cognitiveservices account deployment show \
  --name <openai-name> \
  --resource-group <rg-name> \
  --deployment-name gpt-4
```

## Common Error Patterns

### "Meeting transcription is not enabled"
**Cause:** Teams meeting doesn't have transcription turned on
**Fix:** Enable transcription in Teams meeting settings before starting monitoring

### "Failed to authenticate with Graph API"
**Cause:** Invalid tenant ID, client ID, or client secret
**Fix:** Verify Azure AD app registration credentials, check Key Vault secrets

### "SignalR connection failed"
**Cause:** Connection string invalid or Azure SignalR service not provisioned
**Fix:** Verify Azure SignalR resource exists, check connection string in Key Vault

### "OpenAI quota exceeded"
**Cause:** Too many requests to Azure OpenAI
**Fix:** Increase quota in Azure Portal or reduce question generation frequency

### "VTT parsing failed"
**Cause:** Unexpected VTT format from Graph API
**Fix:** Log raw VTT content, update parser to handle new format

## Debugging Protocol

When debugging TeamsBuddy issues:

1. **Identify the component** (Graph API, VTT parser, SignalR, OpenAI, Background Service)
2. **Check logs** in Application Insights or local console
3. **Verify configuration** (appsettings.json, Key Vault secrets)
4. **Test in isolation** (unit tests, manual API calls)
5. **Monitor metrics** (Azure Portal, health checks)
6. **Review recent changes** (git log, recent commits)

## References

- Project CLAUDE.md: `CLAUDE.md`
- API Endpoints: `src/TeamsMeetingAssistant.Api/Controllers/`
- Infrastructure: `src/TeamsMeetingAssistant.Infrastructure/`
- Tests: `tests/`
- Azure Resources: `infrastructure/main.bicep`
