# Teams Meeting Transcript Real-Time Monitor

A C# prototype application that monitors Microsoft Teams meeting transcripts in near real-time using Microsoft Graph API, streams the transcript data to a web application via Azure SignalR, and uses Azure OpenAI to generate intelligent "next question" suggestions for meeting participants.

## 🚀 Project Status

**✅ Completed (Working Prototype)**
- ✅ Solution structure and core domain models
- ✅ ASP.NET Core Web API with background services
- ✅ SignalR hub and real-time streaming
- ✅ Mock implementations for demonstration
- ✅ Full VTT transcript parser
- ✅ In-memory meeting session store
- ✅ Complete API endpoints for meeting management
- ✅ Error handling and logging

**🚧 Next Steps (To Complete)**
- 🔄 Real Microsoft Graph API integration (requires API compatibility fixes)
- 🔄 Real Azure OpenAI integration (requires API compatibility fixes)
- ⏳ Blazor Server web application
- ⏳ Azure Bicep infrastructure templates
- ⏳ Production configuration and secrets management

## 📋 Solution Architecture

```
Microsoft Teams Meeting (with transcription enabled)
            ↓
    Microsoft Graph API (transcript polling/webhooks)
            ↓
    ASP.NET Core Web API (Background Services + Controllers)
            ↓
    Azure SignalR Service ← Blazor Server Web App
            ↓
    Azure OpenAI (GPT-4 for question suggestions)
            ↓
    Optional: Cosmos DB (transcript history)
```

## 🏗️ Project Structure

```
TeamsMeetingAssistant/
├── src/
│   ├── TeamsMeetingAssistant.Core/               # Domain models, interfaces
│   ├── TeamsMeetingAssistant.Api/                # ASP.NET Core Web API ✅
│   ├── TeamsMeetingAssistant.Web/                # Blazor Server app (pending)
│   ├── TeamsMeetingAssistant.Services/           # Business logic services (pending)
│   └── TeamsMeetingAssistant.Infrastructure/     # Graph API, SignalR clients ✅
├── tests/
│   ├── TeamsMeetingAssistant.Tests.Unit/         # Unit tests (pending)
│   └── TeamsMeetingAssistant.Tests.Integration/  # Integration tests (pending)
└── README.md
```

## 🛠️ Technology Stack

**✅ Currently Implemented**
- **.NET 8.0** (latest LTS)
- **C# 12** with modern patterns (records, pattern matching, async/await)
- **ASP.NET Core Web API** (minimal APIs and controllers)
- **Background Services** (IHostedService for polling)
- **SignalR** (real-time communication)
- **VTT Parser** (WebVTT transcript format parsing)
- **Mock Services** (for demonstration)

**🔄 Implemented but Requires API Updates**
- **Microsoft Graph SDK 5.x** (API compatibility issues)
- **Azure AI OpenAI SDK 2.x** (API compatibility issues)
- **Polly** (retry policies)

**⏳ To Be Implemented**
- **Blazor Server** for web UI
- **Bicep** for infrastructure as code
- **Azure Key Vault** for secrets management
- **Application Insights** for monitoring

## 🚀 Getting Started

### Prerequisites
- .NET 8 SDK
- Visual Studio 2022 or VS Code
- Azure subscription (for production deployment)

### Local Development Setup

1. **Clone and Build**
   ```bash
   git clone <repository-url>
   cd TeamsMeetingAssistant
   dotnet build
   ```

2. **Run the API**
   ```bash
   cd src/TeamsMeetingAssistant.Api
   dotnet run
   ```

3. **Explore the API**
   - Swagger UI: `https://localhost:<port>/swagger`
   - Health Check: `https://localhost:<port>/health`
   - API Root: `https://localhost:<port>/`

### API Endpoints

**Meeting Management**
- `POST /api/meeting/start` - Start monitoring a meeting
- `POST /api/meeting/stop` - Stop monitoring a meeting
- `GET /api/meeting/sessions` - Get all active sessions
- `GET /api/meeting/{meetingId}` - Get specific meeting session
- `DELETE /api/meeting/{meetingId}` - Delete meeting session

**Real-time Communication**
- SignalR Hub: `/transcripthub`
- Webhook Handler: `/api/webhook/transcript`

**Health Monitoring**
- Health Check: `/health`

## 📁 Core Components

### Domain Models (`TeamsMeetingAssistant.Core`)

**TranscriptSegment.cs**
```csharp
public record TranscriptSegment(
    string Id,
    string SpeakerName,
    string SpeakerId,
    string Content,
    DateTimeOffset Timestamp,
    TimeSpan StartTime,
    TimeSpan EndTime
);
```

**MeetingSession.cs**
```csharp
public record MeetingSession(
    string MeetingId,
    string OrganizerEmail,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    bool IsTranscriptionEnabled,
    string? ActiveTranscriptId,
    DateTimeOffset LastProcessedTime,
    MeetingStatus Status
);
```

**QuestionSuggestion.cs**
```csharp
public record QuestionSuggestion(
    string Id,
    string Question,
    string Rationale,
    string Category, // "Clarification", "Deep-dive", "Follow-up", "Summary"
    float ConfidenceScore,
    DateTimeOffset GeneratedAt
);
```

### Infrastructure Layer

**VttTranscriptParser.cs** ✅
- Parses WebVTT transcript format
- Extracts speaker information and timestamps
- Handles complex transcript scenarios

**MockGraphTranscriptService.cs** ✅
- Mock implementation for development
- Simulates transcript data
- Ready for real Graph API integration

**MockOpenAIQuestionService.cs** ✅
- Mock AI question generation
- Simulates intelligent question suggestions
- Ready for real Azure OpenAI integration

**InMemoryMeetingSessionStore.cs** ✅
- In-memory session management
- Thread-safe operations
- Production-ready for Cosmos DB replacement

### API Layer

**TranscriptPollingService.cs** ✅
- Background service for transcript polling
- Configurable polling intervals
- Parallel processing of multiple meetings
- Error handling and retry logic

**SubscriptionRenewalService.cs** ✅
- Manages Graph API subscription lifecycle
- Automatic renewal before expiration
- Handles subscription failures

**SignalRHub.cs** ✅
- Real-time communication hub
- Meeting group management
- Connection lifecycle handling

## 🔧 Configuration

### Development (appsettings.json)
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "AllowedOrigins": "https://localhost:7002",
  "TranscriptProcessing": {
    "PollingIntervalSeconds": 5,
    "MaxSegmentsPerBatch": 50,
    "QuestionGenerationThresholdSeconds": 30,
    "MaxConcurrentMeetings": 10
  }
}
```

### Production Configuration Required
```json
{
  "AzureAd": {
    "TenantId": "",
    "ClientId": "",
    "ClientSecret": ""
  },
  "AzureOpenAI": {
    "Endpoint": "",
    "ApiKey": "",
    "DeploymentName": "gpt-4"
  },
  "Azure": {
    "SignalR": {
      "ConnectionString": ""
    }
  }
}
```

## 🧪 Testing the Prototype

### 1. Start the API
```bash
cd src/TeamsMeetingAssistant.Api
dotnet run
```

### 2. Start Monitoring a Meeting
```bash
curl -X POST "https://localhost:<port>/api/meeting/start" \
  -H "Content-Type: application/json" \
  -d '{
    "meetingId": "test-meeting-123",
    "useWebhooks": false
  }'
```

### 3. View Active Sessions
```bash
curl "https://localhost:<port>/api/meeting/sessions"
```

### 4. Connect to SignalR Hub
Use SignalR client to connect to `/transcripthub` and join meeting groups.

## 🔍 Current Limitations

**API Compatibility Issues**
The current implementations use older API patterns. To upgrade to production:

1. **Microsoft Graph SDK** - Update to use fluent API patterns
2. **Azure OpenAI SDK** - Update to use new chat completion APIs
3. **Package Versions** - Align all packages to compatible versions

**Missing Components**
- Blazor web application UI
- Azure infrastructure deployment
- Production secrets management
- Comprehensive test coverage

## 🚀 Production Deployment Roadmap

### Phase 1: API Compatibility Fixes
1. Update Graph API service to use current SDK patterns
2. Update OpenAI service to use current chat API
3. Update all NuGet packages to compatible versions
4. Add comprehensive error handling

### Phase 2: Web Application
1. Implement Blazor Server web UI
2. Create SignalR client components
3. Add real-time transcript display
4. Implement question suggestion UI

### Phase 3: Azure Deployment
1. Create Bicep infrastructure templates
2. Set up Azure AD application registration
3. Configure Azure SignalR Service
4. Deploy Azure OpenAI resource
5. Set up Azure Key Vault for secrets

### Phase 4: Testing & Monitoring
1. Add comprehensive unit and integration tests
2. Set up Application Insights monitoring
3. Implement production logging
4. Add health checks and monitoring

## 🤝 Contributing

This is a prototype demonstrating enterprise-grade C# patterns and real-time communication architecture. Contributions welcome for:

1. API compatibility fixes
2. Blazor UI implementation
3. Azure infrastructure templates
4. Test coverage improvements
5. Documentation enhancements

## 📄 License

This project is provided as a demonstration prototype. Feel free to use and modify for your own implementations.

## 🔗 Key Learnings

1. **Modern C# Patterns** - Records, pattern matching, async/await
2. **Real-time Communication** - SignalR for live updates
3. **Background Processing** - Hosted services for polling
4. **API Design** - RESTful controllers with proper error handling
5. **Domain-Driven Design** - Clean architecture with interfaces
6. **Mock-First Development** - Prototype with mocks, integrate real services later
7. **Dependency Injection** - Proper service registration and lifetime management

This prototype demonstrates a complete, working architecture for real-time meeting transcript monitoring with AI-powered insights, ready for production deployment after API compatibility updates.