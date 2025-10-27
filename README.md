# Comprehensive Prompt for Claude Code (ASP.NET Web API Version)

```markdown
# Project: Teams Meeting Transcript Real-Time Monitor

## Project Overview
Build a C# prototype application that monitors Microsoft Teams meeting transcripts in near real-time using Microsoft Graph API, streams the transcript data to a web application via Azure SignalR, and uses Azure OpenAI to generate intelligent "next question" suggestions for meeting participants.

## Solution Architecture

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

## Technical Requirements

### Technology Stack
- **.NET 8.0** (latest LTS)
- **C# 12** with modern patterns (records, pattern matching, async/await)
- **ASP.NET Core Web API** (minimal APIs or controllers)
- **Background Services** (IHostedService for polling)
- **Microsoft Graph SDK 5.x**
- **Azure SignalR Service SDK**
- **Azure OpenAI SDK**
- **Blazor Server** for web UI
- **Bicep** for infrastructure as code

### Project Structure
Create a solution with the following projects:

```
TeamsMeetingAssistant/
├── src/
│   ├── TeamsMeetingAssistant.Core/               # Domain models, interfaces
│   ├── TeamsMeetingAssistant.Api/                # ASP.NET Core Web API
│   ├── TeamsMeetingAssistant.Web/                # Blazor Server app
│   ├── TeamsMeetingAssistant.Services/           # Business logic services
│   └── TeamsMeetingAssistant.Infrastructure/     # Graph API, SignalR clients
├── tests/
│   ├── TeamsMeetingAssistant.Tests.Unit/
│   └── TeamsMeetingAssistant.Tests.Integration/
├── infrastructure/
│   └── main.bicep                                # Azure resources
├── .github/
│   └── workflows/
│       └── deploy.yml                            # CI/CD pipeline
└── README.md
```

## Detailed Implementation Requirements

### 1. Core Domain Models (TeamsMeetingAssistant.Core)

Create the following domain models:

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

public enum MeetingStatus
{
    Pending,
    Active,
    Completed,
    Error
}
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

**Interfaces:**
- `ITranscriptService`
- `IQuestionGenerationService`
- `ISignalRService`
- `IMeetingSessionStore`

### 2. Infrastructure Layer (TeamsMeetingAssistant.Infrastructure)

**GraphTranscriptService.cs**
- Implements ITranscriptService
- Methods:
  - `Task<IEnumerable<TranscriptSegment>> GetNewTranscriptSegmentsAsync(string meetingId, DateTimeOffset since, CancellationToken cancellationToken)`
  - `Task<MeetingSession> GetMeetingInfoAsync(string meetingId, CancellationToken cancellationToken)`
  - `Task<Subscription> SubscribeToTranscriptChangesAsync(string meetingId, string webhookUrl, CancellationToken cancellationToken)`
  - `Task RenewSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)`
  - `Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken)`
- Uses Microsoft.Graph SDK with proper authentication (ClientCredentialProvider)
- Parse VTT transcript format
- Handle Graph API throttling with retry policies (Polly)

**VttTranscriptParser.cs**
```csharp
public class VttTranscriptParser
{
    public List<TranscriptSegment> Parse(string vttContent, DateTimeOffset baseTime)
    {
        // Parse WEBVTT format:
        // WEBVTT
        //
        // 00:00:00.000 --> 00:00:03.000
        // <v Speaker Name>Text content here
        
        var segments = new List<TranscriptSegment>();
        var lines = vttContent.Split('\n');
        
        // Implementation here
        
        return segments;
    }
}
```

**AzureOpenAIQuestionService.cs**
- Implements IQuestionGenerationService
- Use Azure.AI.OpenAI SDK
- Methods:
  - `Task<List<QuestionSuggestion>> GenerateQuestionsAsync(List<TranscriptSegment> recentTranscript, string meetingContext, CancellationToken cancellationToken)`
- Implement smart prompt engineering:
  ```
  System: You are an executive meeting coach. Analyze meeting transcripts and suggest 
  insightful questions that will drive productive conversations.
  
  Context: [Meeting topic/agenda if available]
  Recent Transcript: [Last 2-3 minutes of conversation]
  
  Generate 2-3 questions in these categories:
  1. Clarification - Ask about ambiguous points
  2. Deep-dive - Explore interesting topics further
  3. Action-oriented - Move conversation toward decisions
  
  Return JSON format with question, rationale, category, and confidence score.
  ```

**SignalRHubService.cs**
- Implements ISignalRService
- Methods:
  - `Task SendTranscriptUpdateAsync(string meetingId, TranscriptSegment segment)`
  - `Task SendQuestionSuggestionsAsync(string meetingId, List<QuestionSuggestion> suggestions)`
  - `Task NotifyMeetingStatusAsync(string meetingId, string status)`

**InMemoryMeetingSessionStore.cs** (or CosmosDbMeetingSessionStore.cs)
- Implements IMeetingSessionStore
- Methods:
  - `Task<MeetingSession?> GetAsync(string meetingId)`
  - `Task AddOrUpdateAsync(MeetingSession session)`
  - `Task<IEnumerable<MeetingSession>> GetActiveSessionsAsync()`
  - `Task RemoveAsync(string meetingId)`

### 3. ASP.NET Core Web API (TeamsMeetingAssistant.Api)

**Program.cs**
```csharp
var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// SignalR
builder.Services.AddSignalR()
    .AddAzureSignalR(builder.Configuration["Azure:SignalR:ConnectionString"]);

// Graph API
builder.Services.AddSingleton<IGraphServiceClient>(sp =>
{
    var clientId = builder.Configuration["AzureAd:ClientId"];
    var clientSecret = builder.Configuration["AzureAd:ClientSecret"];
    var tenantId = builder.Configuration["AzureAd:TenantId"];
    
    var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
    return new GraphServiceClient(credential);
});

// Azure OpenAI
builder.Services.AddSingleton(sp =>
{
    var endpoint = new Uri(builder.Configuration["AzureOpenAI:Endpoint"]);
    var credential = new AzureKeyCredential(builder.Configuration["AzureOpenAI:ApiKey"]);
    return new OpenAIClient(endpoint, credential);
});

// Application services
builder.Services.AddSingleton<IMeetingSessionStore, InMemoryMeetingSessionStore>();
builder.Services.AddScoped<ITranscriptService, GraphTranscriptService>();
builder.Services.AddScoped<IQuestionGenerationService, AzureOpenAIQuestionService>();
builder.Services.AddScoped<ISignalRService, SignalRHubService>();

// Background services
builder.Services.AddHostedService<TranscriptPollingService>();
builder.Services.AddHostedService<SubscriptionRenewalService>();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<GraphApiHealthCheck>("graph_api")
    .AddCheck<OpenAIHealthCheck>("openai");

// CORS for Blazor app
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(builder.Configuration["AllowedOrigins"] ?? "*")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Logging
builder.Logging.AddApplicationInsights();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapHub<TranscriptHub>("/transcripthub");

app.Run();
```

**Background Services:**

**TranscriptPollingService.cs**
```csharp
public class TranscriptPollingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMeetingSessionStore _sessionStore;
    private readonly ILogger<TranscriptPollingService> _logger;
    private readonly IConfiguration _configuration;
    
    public TranscriptPollingService(
        IServiceProvider serviceProvider,
        IMeetingSessionStore sessionStore,
        ILogger<TranscriptPollingService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
        _logger = logger;
        _configuration = configuration;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollingInterval = TimeSpan.FromSeconds(
            _configuration.GetValue<int>("TranscriptProcessing:PollingIntervalSeconds", 5));
        
        _logger.LogInformation("Transcript polling service started with interval {Interval}", 
            pollingInterval);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollTranscriptsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in transcript polling loop");
            }
            
            await Task.Delay(pollingInterval, stoppingToken);
        }
    }
    
    private async Task PollTranscriptsAsync(CancellationToken cancellationToken)
    {
        var activeSessions = await _sessionStore.GetActiveSessionsAsync();
        
        _logger.LogDebug("Polling {Count} active meeting sessions", activeSessions.Count());
        
        // Process sessions in parallel with max concurrency
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 5,
            CancellationToken = cancellationToken
        };
        
        await Parallel.ForEachAsync(activeSessions, options, async (session, ct) =>
        {
            await ProcessMeetingSessionAsync(session, ct);
        });
    }
    
    private async Task ProcessMeetingSessionAsync(
        MeetingSession session, 
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        
        var transcriptService = scope.ServiceProvider.GetRequiredService<ITranscriptService>();
        var signalRService = scope.ServiceProvider.GetRequiredService<ISignalRService>();
        var questionService = scope.ServiceProvider.GetRequiredService<IQuestionGenerationService>();
        
        try
        {
            // Get new transcript segments
            var newSegments = await transcriptService.GetNewTranscriptSegmentsAsync(
                session.MeetingId,
                session.LastProcessedTime,
                cancellationToken);
            
            if (!newSegments.Any())
            {
                return;
            }
            
            _logger.LogInformation(
                "Found {Count} new transcript segments for meeting {MeetingId}",
                newSegments.Count(),
                session.MeetingId);
            
            // Stream segments to SignalR
            foreach (var segment in newSegments)
            {
                await signalRService.SendTranscriptUpdateAsync(session.MeetingId, segment);
            }
            
            // Generate question suggestions if enough time has passed
            var timeSinceLastQuestion = DateTimeOffset.UtcNow - session.LastProcessedTime;
            var questionThreshold = TimeSpan.FromSeconds(
                _configuration.GetValue<int>("TranscriptProcessing:QuestionGenerationThresholdSeconds", 30));
            
            if (timeSinceLastQuestion >= questionThreshold && newSegments.Count() >= 3)
            {
                var questions = await questionService.GenerateQuestionsAsync(
                    newSegments.ToList(),
                    session.OrganizerEmail, // Can be enhanced with meeting context
                    cancellationToken);
                
                await signalRService.SendQuestionSuggestionsAsync(session.MeetingId, questions);
            }
            
            // Update session
            var updatedSession = session with
            {
                LastProcessedTime = newSegments.Max(s => s.Timestamp)
            };
            await _sessionStore.AddOrUpdateAsync(updatedSession);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing meeting {MeetingId}", session.MeetingId);
            
            // Update session status to error
            var errorSession = session with { Status = MeetingStatus.Error };
            await _sessionStore.AddOrUpdateAsync(errorSession);
        }
    }
}
```

**SubscriptionRenewalService.cs**
```csharp
public class SubscriptionRenewalService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionRenewalService> _logger;
    
    // Track subscriptions that need renewal
    private readonly ConcurrentDictionary<string, (string SubscriptionId, DateTimeOffset ExpiresAt)> 
        _subscriptions = new();
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check every 30 minutes for subscriptions that need renewal
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewExpiringSubscriptionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in subscription renewal loop");
            }
            
            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }
    
    private async Task RenewExpiringSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var expiringThreshold = DateTimeOffset.UtcNow.AddMinutes(60);
        var expiringSubs = _subscriptions
            .Where(kvp => kvp.Value.ExpiresAt <= expiringThreshold)
            .ToList();
        
        if (!expiringSubs.Any())
        {
            return;
        }
        
        _logger.LogInformation("Renewing {Count} expiring subscriptions", expiringSubs.Count);
        
        using var scope = _serviceProvider.CreateScope();
        var transcriptService = scope.ServiceProvider.GetRequiredService<ITranscriptService>();
        
        foreach (var (meetingId, (subscriptionId, _)) in expiringSubs)
        {
            try
            {
                await transcriptService.RenewSubscriptionAsync(subscriptionId, cancellationToken);
                
                // Update expiration time
                _subscriptions[meetingId] = (subscriptionId, DateTimeOffset.UtcNow.AddHours(1));
                
                _logger.LogInformation("Renewed subscription {SubscriptionId}", subscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to renew subscription {SubscriptionId}", subscriptionId);
            }
        }
    }
    
    public void TrackSubscription(string meetingId, string subscriptionId, DateTimeOffset expiresAt)
    {
        _subscriptions[meetingId] = (subscriptionId, expiresAt);
    }
    
    public void UntrackSubscription(string meetingId)
    {
        _subscriptions.TryRemove(meetingId, out _);
    }
}
```

**Controllers:**

**MeetingController.cs**
```csharp
[ApiController]
[Route("api/[controller]")]
public class MeetingController : ControllerBase
{
    private readonly IMeetingSessionStore _sessionStore;
    private readonly ITranscriptService _transcriptService;
    private readonly ILogger<MeetingController> _logger;
    private readonly SubscriptionRenewalService _renewalService;
    
    public MeetingController(
        IMeetingSessionStore sessionStore,
        ITranscriptService transcriptService,
        ILogger<MeetingController> logger,
        IHostedService renewalService)
    {
        _sessionStore = sessionStore;
        _transcriptService = transcriptService;
        _logger = logger;
        _renewalService = (SubscriptionRenewalService)renewalService;
    }
    
    [HttpPost("start")]
    public async Task<IActionResult> StartMonitoring(
        [FromBody] StartMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validate meeting and get info
            var meetingInfo = await _transcriptService.GetMeetingInfoAsync(
                request.MeetingId, 
                cancellationToken);
            
            if (!meetingInfo.IsTranscriptionEnabled)
            {
                return BadRequest(new 
                { 
                    error = "Meeting transcription is not enabled. Please enable it in Teams settings." 
                });
            }
            
            // Create session
            var session = new MeetingSession(
                meetingInfo.MeetingId,
                meetingInfo.OrganizerEmail,
                DateTimeOffset.UtcNow,
                null,
                true,
                null,
                DateTimeOffset.UtcNow,
                MeetingStatus.Active
            );
            
            await _sessionStore.AddOrUpdateAsync(session);
            
            // Subscribe to webhooks (optional - can rely on polling only)
            if (request.UseWebhooks)
            {
                var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhook/transcript";
                var subscription = await _transcriptService.SubscribeToTranscriptChangesAsync(
                    request.MeetingId,
                    webhookUrl,
                    cancellationToken);
                
                _renewalService.TrackSubscription(
                    request.MeetingId, 
                    subscription.Id, 
                    subscription.ExpirationDateTime.GetValueOrDefault());
            }
            
            _logger.LogInformation("Started monitoring meeting {MeetingId}", request.MeetingId);
            
            return Ok(new { meetingId = request.MeetingId, status = "monitoring" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start monitoring meeting {MeetingId}", request.MeetingId);
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost("stop")]
    public async Task<IActionResult> StopMonitoring(
        [FromBody] StopMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var session = await _sessionStore.GetAsync(request.MeetingId);
            
            if (session == null)
            {
                return NotFound(new { error = "Meeting session not found" });
            }
            
            // Update session status
            var completedSession = session with
            {
                Status = MeetingStatus.Completed,
                EndTime = DateTimeOffset.UtcNow
            };
            
            await _sessionStore.AddOrUpdateAsync(completedSession);
            
            _renewalService.UntrackSubscription(request.MeetingId);
            
            _logger.LogInformation("Stopped monitoring meeting {MeetingId}", request.MeetingId);
            
            return Ok(new { meetingId = request.MeetingId, status = "stopped" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop monitoring meeting {MeetingId}", request.MeetingId);
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpGet("sessions")]
    public async Task<IActionResult> GetActiveSessions()
    {
        var sessions = await _sessionStore.GetActiveSessionsAsync();
        return Ok(sessions);
    }
    
    [HttpGet("{meetingId}")]
    public async Task<IActionResult> GetSession(string meetingId)
    {
        var session = await _sessionStore.GetAsync(meetingId);
        
        if (session == null)
        {
            return NotFound();
        }
        
        return Ok(session);
    }
}

public record StartMonitoringRequest(string MeetingId, bool UseWebhooks = false);
public record StopMonitoringRequest(string MeetingId);
```

**WebhookController.cs**
```csharp
[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly ITranscriptService _transcriptService;
    private readonly ISignalRService _signalRService;
    private readonly IQuestionGenerationService _questionService;
    private readonly IMeetingSessionStore _sessionStore;
    private readonly ILogger<WebhookController> _logger;
    
    [HttpPost("transcript")]
    public async Task<IActionResult> HandleTranscriptNotification(
        [FromQuery] string? validationToken,
        CancellationToken cancellationToken)
    {
        // Handle Graph API subscription validation
        if (!string.IsNullOrEmpty(validationToken))
        {
            _logger.LogInformation("Validating webhook subscription");
            return Ok(validationToken);
        }
        
        try
        {
            // Read notification payload
            using var reader = new StreamReader(Request.Body);
            var json = await reader.ReadToEndAsync(cancellationToken);
            var notifications = JsonSerializer.Deserialize<ChangeNotificationCollection>(json);
            
            if (notifications?.Value == null)
            {
                return BadRequest("Invalid notification payload");
            }
            
            // Process each notification
            foreach (var notification in notifications.Value)
            {
                await ProcessNotificationAsync(notification, cancellationToken);
            }
            
            return Accepted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook notification");
            return StatusCode(500);
        }
    }
    
    private async Task ProcessNotificationAsync(
        ChangeNotification notification,
        CancellationToken cancellationToken)
    {
        // Extract meeting ID from resource path
        // e.g., /communications/onlineMeetings/{meetingId}/transcripts/{transcriptId}
        var parts = notification.Resource.Split('/');
        var meetingId = parts[3];
        
        var session = await _sessionStore.GetAsync(meetingId);
        if (session == null || session.Status != MeetingStatus.Active)
        {
            return;
        }
        
        // Fetch and process new transcript segments
        var newSegments = await _transcriptService.GetNewTranscriptSegmentsAsync(
            meetingId,
            session.LastProcessedTime,
            cancellationToken);
        
        foreach (var segment in newSegments)
        {
            await _signalRService.SendTranscriptUpdateAsync(meetingId, segment);
        }
        
        if (newSegments.Any())
        {
            var questions = await _questionService.GenerateQuestionsAsync(
                newSegments.ToList(),
                session.OrganizerEmail,
                cancellationToken);
            
            await _signalRService.SendQuestionSuggestionsAsync(meetingId, questions);
            
            var updatedSession = session with
            {
                LastProcessedTime = newSegments.Max(s => s.Timestamp)
            };
            await _sessionStore.AddOrUpdateAsync(updatedSession);
        }
    }
}
```

**SignalR Hub:**

**TranscriptHub.cs**
```csharp
public class TranscriptHub : Hub
{
    private readonly ILogger<TranscriptHub> _logger;
    
    public TranscriptHub(ILogger<TranscriptHub> logger)
    {
        _logger = logger;
    }
    
    public async Task JoinMeeting(string meetingId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, meetingId);
        _logger.LogInformation("Client {ConnectionId} joined meeting {MeetingId}", 
            Context.ConnectionId, meetingId);
    }
    
    public async Task LeaveMeeting(string meetingId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, meetingId);
        _logger.LogInformation("Client {ConnectionId} left meeting {MeetingId}", 
            Context.ConnectionId, meetingId);
    }
    
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
```

### 4. Blazor Server Web Application (TeamsMeetingAssistant.Web)

**Program.cs**
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// HTTP client for API calls
builder.Services.AddHttpClient("API", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7001");
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
```

**Pages/MeetingDashboard.razor**
```csharp
@page "/meeting/{MeetingId}"
@using Microsoft.AspNetCore.SignalR.Client
@inject NavigationManager Navigation
@inject IConfiguration Configuration
@implements IAsyncDisposable

<PageTitle>Meeting Assistant - @MeetingId</PageTitle>

<div class="container-fluid">
    <div class="row">
        <div class="col-md-8">
            <h3>Live Transcript</h3>
            <MeetingControls MeetingId="@MeetingId" 
                           IsMonitoring="@isMonitoring"
                           OnStart="StartMonitoring"
                           OnStop="StopMonitoring" />
            
            <TranscriptViewer Segments="@transcriptSegments" />
        </div>
        
        <div class="col-md-4">
            <h3>Suggested Questions</h3>
            @foreach (var suggestion in questionSuggestions)
            {
                <QuestionCard Suggestion="@suggestion" />
            }
        </div>
    </div>
</div>

@code {
    [Parameter]
    public string MeetingId { get; set; } = string.Empty;
    
    private HubConnection? hubConnection;
    private List<TranscriptSegment> transcriptSegments = new();
    private List<QuestionSuggestion> questionSuggestions = new();
    private bool isMonitoring = false;
    
    protected override async Task OnInitializedAsync()
    {
        // Build SignalR connection
        var signalRUrl = Configuration["Azure:SignalR:Endpoint"] ?? 
                        $"{Navigation.BaseUri}transcripthub";
        
        hubConnection = new HubConnectionBuilder()
            .WithUrl(signalRUrl)
            .WithAutomaticReconnect()
            .Build();
        
        // Register handlers
        hubConnection.On<TranscriptSegment>("NewTranscript", segment =>
        {
            transcriptSegments.Add(segment);
            InvokeAsync(StateHasChanged);
        });
        
        hubConnection.On<List<QuestionSuggestion>>("QuestionSuggestions", suggestions =>
        {
            questionSuggestions = suggestions;
            InvokeAsync(StateHasChanged);
        });
        
        hubConnection.On<string>("MeetingStatus", status =>
        {
            isMonitoring = status == "active";
            InvokeAsync(StateHasChanged);
        });
        
        // Start connection
        await hubConnection.StartAsync();
        
        // Join meeting group
        await hubConnection.InvokeAsync("JoinMeeting", MeetingId);
    }
    
    private async Task StartMonitoring()
    {
        // Call API to start monitoring
        var httpClient = new HttpClient 
        { 
            BaseAddress = new Uri(Configuration["ApiBaseUrl"] ?? "https://localhost:7001") 
        };
        
        var response = await httpClient.PostAsJsonAsync("/api/meeting/start", 
            new { meetingId = MeetingId });
        
        if (response.IsSuccessStatusCode)
        {
            isMonitoring = true;
        }
    }
    
    private async Task StopMonitoring()
    {
        var httpClient = new HttpClient 
        { 
            BaseAddress = new Uri(Configuration["ApiBaseUrl"] ?? "https://localhost:7001") 
        };
        
        var response = await httpClient.PostAsJsonAsync("/api/meeting/stop", 
            new { meetingId = MeetingId });
        
        if (response.IsSuccessStatusCode)
        {
            isMonitoring = false;
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        if (hubConnection is not null)
        {
            await hubConnection.InvokeAsync("LeaveMeeting", MeetingId);
            await hubConnection.DisposeAsync();
        }
    }
}
```

**Components:**

**TranscriptViewer.razor**
```csharp
@using TeamsMeetingAssistant.Core

<div class="transcript-viewer">
    <div class="transcript-container">
        @foreach (var segment in Segments.TakeLast(50))
        {
            <div class="transcript-segment">
                <div class="speaker-info">
                    <SpeakerAvatar SpeakerName="@segment.SpeakerName" />
                    <span class="speaker-name">@segment.SpeakerName</span>
                    <span class="timestamp">@segment.Timestamp.ToString("HH:mm:ss")</span>
                </div>
                <div class="transcript-content">
                    @segment.Content
                </div>
            </div>
        }
    </div>
</div>

@code {
    [Parameter]
    public List<TranscriptSegment> Segments { get; set; } = new();
}
```

**QuestionCard.razor**
```csharp
@using TeamsMeetingAssistant.Core

<div class="question-card @GetCategoryClass()">
    <div class="question-header">
        <span class="category-badge">@Suggestion.Category</span>
        <span class="confidence">@Suggestion.ConfidenceScore.ToString("P0")</span>
    </div>
    <div class="question-text">
        @Suggestion.Question
    </div>
    <div class="question-rationale">
        @Suggestion.Rationale
    </div>
    <div class="question-actions">
        <button class="btn btn-sm btn-primary" @onclick="CopyToClipboard">
            Copy Question
        </button>
    </div>
</div>

@code {
    [Parameter]
    public QuestionSuggestion Suggestion { get; set; } = null!;
    
    private string GetCategoryClass()
    {
        return Suggestion.Category.ToLower() switch
        {
            "clarification" => "category-clarification",
            "deep-dive" => "category-deepdive",
            "follow-up" => "category-followup",
            "summary" => "category-summary",
            _ => ""
        };
    }
    
    private async Task CopyToClipboard()
    {
        // Implementation for copying to clipboard
    }
}
```

**MeetingControls.razor**
```csharp
<div class="meeting-controls">
    @if (!IsMonitoring)
    {
        <button class="btn btn-success" @onclick="OnStart">
            <span class="icon">▶</span> Start Monitoring
        </button>
    }
    else
    {
        <button class="btn btn-danger" @onclick="OnStop">
            <span class="icon">⏹</span> Stop Monitoring
        </button>
        <span class="status-indicator live">● LIVE</span>
    }
    
    <div class="meeting-info">
        <strong>Meeting ID:</strong> @MeetingId
    </div>
</div>

@code {
    [Parameter]
    public string MeetingId { get; set; } = string.Empty;
    
    [Parameter]
    public bool IsMonitoring { get; set; }
    
    [Parameter]
    public EventCallback OnStart { get; set; }
    
    [Parameter]
    public EventCallback OnStop { get; set; }
}
```

**SpeakerAvatar.razor**
```csharp
<div class="speaker-avatar" style="background-color: @GetColorForSpeaker()">
    @GetInitials()
</div>

@code {
    [Parameter]
    public string SpeakerName { get; set; } = string.Empty;
    
    private string GetInitials()
    {
        var parts = SpeakerName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][0].ToString().ToUpper();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpper();
    }
    
    private string GetColorForSpeaker()
    {
        // Generate consistent color based on speaker name
        var hash = SpeakerName.GetHashCode();
        var colors = new[] { "#4A90E2", "#50C878", "#E27A4A", "#9B59B6", "#E74C3C" };
        return colors[Math.Abs(hash) % colors.Length];
    }
}
```

### 5. Infrastructure as Code (Bicep)

**main.bicep**
```bicep
@description('Location for all resources')
param location string = resourceGroup().location

@description('Environment name')
param environmentName string = 'dev'

@description('Application name')
param appName string = 'teams-meeting-assistant'

// Variables
var apiAppName = '${appName}-api-${environmentName}'
var webAppName = '${appName}-web-${environmentName}'
var appServicePlanName = '${appName}-plan-${environmentName}'
var signalRName = '${appName}-signalr-${environmentName}'
var openAIName = '${appName}-openai-${environmentName}'
var appInsightsName = '${appName}-insights-${environmentName}'
var keyVaultName = '${appName}-kv-${environmentName}'
var storageAccountName = replace('${appName}st${environmentName}', '-', '')

// App Service Plan
resource appServicePlan 'Microsoft.Web/serverfarms@2022-09-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: 'B1'  // Basic tier, scale to S1/P1V2 for production
    tier: 'Basic'
    capacity: 1
  }
  kind: 'linux'
  properties: {
    reserved: true
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
          value: subscription().tenantId
        }
        {
          name: 'AzureAd__ClientId'
          value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=AzureAdClientId)'
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
      ]
    }
  }
}

// Web App Service
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
    name: 'Free_F1'  // Use Standard_S1 for production
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

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    Flow_Type: 'Bluefield'
    Request_Source: 'rest'
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
  }
}

// Storage Account (for future transcript history)
resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

// RBAC Assignments
// API App can access Key Vault
resource apiKeyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, apiApp.id, 'Key Vault Secrets User')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: apiApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Web App can access Key Vault
resource webKeyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, webApp.id, 'Key Vault Secrets User')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Outputs
output apiAppUrl string = 'https://${apiApp.properties.defaultHostName}'
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output keyVaultName string = keyVault.name
output openAIEndpoint string = openAI.properties.endpoint
```

### 6. Configuration & Secrets

**appsettings.json (API)**
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
  "AzureAd": {
    "TenantId": "",
    "ClientId": "",
    "ClientSecret": ""
  },
  "GraphApi": {
    "BaseUrl": "https://graph.microsoft.com/v1.0",
    "Scopes": ["https://graph.microsoft.com/.default"]
  },
  "AzureOpenAI": {
    "Endpoint": "",
    "ApiKey": "",
    "DeploymentName": "gpt-4",
    "MaxTokens": 800,
    "Temperature": 0.7
  },
  "Azure": {
    "SignalR": {
      "ConnectionString": ""
    }
  },
  "TranscriptProcessing": {
    "PollingIntervalSeconds": 5,
    "MaxSegmentsPerBatch": 50,
    "QuestionGenerationThresholdSeconds": 30,
    "MaxConcurrentMeetings": 10
  }
}
```

**appsettings.json (Web)**
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "ApiBaseUrl": "https://localhost:7001",
  "Azure": {
    "SignalR": {
      "Endpoint": ""
    }
  }
}
```

### 7. Error Handling & Resilience

**Polly Configuration:**

```csharp
// In Program.cs or ServiceCollectionExtensions.cs
public static class ResilienceExtensions
{
    public static IServiceCollection AddResilientHttpClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Graph API Client with retry
        services.AddHttpClient("GraphAPI", client =>
        {
            client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        })
        .AddPolicyHandler(GetRetryPolicy())
        .AddPolicyHandler(GetCircuitBreakerPolicy());
        
        return services;
    }
    
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => 
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    // Log retry
                });
    }
    
    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30));
    }
}
```

### 8. Health Checks

**GraphApiHealthCheck.cs**
```csharp
public class GraphApiHealthCheck : IHealthCheck
{
    private readonly IGraphServiceClient _graphClient;
    
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Simple check - get organization details
            await _graphClient.Organization.Request().GetAsync(cancellationToken);
            return HealthCheckResult.Healthy("Graph API is accessible");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Graph API is not accessible", ex);
        }
    }
}
```

**OpenAIHealthCheck.cs**
```csharp
public class OpenAIHealthCheck : IHealthCheck
{
    private readonly OpenAIClient _openAIClient;
    private readonly IConfiguration _configuration;
    
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Simple test completion
            var deploymentName = _configuration["AzureOpenAI:DeploymentName"];
            var response = await _openAIClient.GetChatCompletionsAsync(
                deploymentName,
                new ChatCompletionsOptions
                {
                    Messages = { new ChatRequestSystemMessage("test") },
                    MaxTokens = 5
                },
                cancellationToken);
            
            return HealthCheckResult.Healthy("Azure OpenAI is accessible");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Azure OpenAI is not accessible", ex);
        }
    }
}
```

### 9. Testing

**Unit Test Example:**

```csharp
public class VttTranscriptParserTests
{
    [Fact]
    public void Parse_ValidVtt_ReturnsSegments()
    {
        // Arrange
        var vttContent = @"WEBVTT

00:00:00.000 --> 00:00:03.000
<v John Doe>Hello everyone, let's start the meeting.

00:00:03.000 --> 00:00:07.000
<v Jane Smith>Thanks John. I have the quarterly results ready.";
        
        var parser = new VttTranscriptParser();
        var baseTime = DateTimeOffset.UtcNow;
        
        // Act
        var segments = parser.Parse(vttContent, baseTime);
        
        // Assert
        Assert.Equal(2, segments.Count);
        Assert.Equal("John Doe", segments[0].SpeakerName);
        Assert.Contains("Hello everyone", segments[0].Content);
    }
}
```

**Integration Test Example:**

```csharp
public class TranscriptPollingServiceIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    
    [Fact]
    public async Task PollingService_FetchesTranscripts_Successfully()
    {
        // Arrange - requires test Teams meeting with transcription
        var meetingId = "test-meeting-id";
        
        // Act - start monitoring
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/meeting/start", 
            new { meetingId });
        
        // Wait for polling
        await Task.Delay(TimeSpan.FromSeconds(10));
        
        // Assert
        response.EnsureSuccessStatusCode();
        // Additional assertions...
    }
}
```

### 10. README Documentation

**README.md structure:**
```markdown
# Teams Meeting Assistant

Real-time transcript monitoring and AI-powered question suggestion system for Microsoft Teams meetings.

## Prerequisites
- .NET 8 SDK
- Azure subscription
- Microsoft 365 tenant with Teams
- Azure CLI

## Local Development Setup

1. Clone repository
2. Register Azure AD application
3. Configure appsettings.json
4. Run API: `dotnet run --project src/TeamsMeetingAssistant.Api`
5. Run Web: `dotnet run --project src/TeamsMeetingAssistant.Web`

## Azure Deployment

```bash
az login
az deployment group create --resource-group rg-teams-assistant --template-file infrastructure/main.bicep
```

## Configuration

### Required Secrets (Key Vault)
- AzureAdClientId
- AzureAdClientSecret
- SignalRConnectionString
- OpenAIApiKey

### Graph API Permissions
- OnlineMeetings.Read.All
- User.Read.All
- Calls.AccessMedia.All (for webhooks)

## Usage

1. Enable transcription in Teams meeting settings
2. Navigate to `/meeting/{meetingId}`
3. Click "Start Monitoring"
4. View real-time transcript and AI suggestions

## Architecture

[Include ASCII diagram or link to architecture doc]

## Known Limitations
- 5-second polling delay
- Single tenant only
- Requires meeting transcription enabled
- Maximum 10 concurrent meetings

## Troubleshooting
[Common issues and solutions]
```

## Implementation Priorities

### Phase 1: Core API (Days 1-3)
1. Create solution structure
2. Implement domain models
3. Set up dependency injection
4. Build GraphTranscriptService with polling
5. Test with mock data

### Phase 2: Background Processing (Days 4-5)
1. Implement TranscriptPollingService
2. Add VTT parsing
3. Create MeetingSessionStore
4. Test with real Teams meeting

### Phase 3: SignalR Streaming (Days 6-7)
1. Set up SignalR hub
2. Implement SignalRHubService
3. Add webhook controller
4. Test real-time streaming

### Phase 4: AI Integration (Days 8-9)
1. Implement OpenAI service
2. Design question generation prompts
3. Integrate with polling service
4. Test question quality

### Phase 5: Web UI (Days 10-12)
1. Create Blazor components
2. Implement SignalR client
3. Build dashboard UI
4. Add meeting controls

### Phase 6: Infrastructure (Days 13-14)
1. Create Bicep templates
2. Test deployment
3. Configure secrets
4. Set up monitoring

### Phase 7: Testing & Documentation (Days 15-16)
1. Write unit tests
2. Create integration tests
3. Write comprehensive README
4. Document API endpoints

## Key Success Criteria

✅ API successfully authenticates with Graph API  
✅ Background service polls transcripts every 5 seconds  
✅ VTT parsing extracts speaker and content  
✅ Transcript streams to SignalR hub  
✅ Azure OpenAI generates relevant questions  
✅ Blazor UI displays live updates  
✅ Deployable to Azure with Bicep  
✅ Health checks functional  
✅ Proper logging and error handling  

## Out of Scope for Prototype

- Multi-tenant support
- Advanced analytics
- Meeting recording storage
- Mobile app
- Admin portal
- Historical transcript search

Build this as an enterprise-grade C# application following SOLID principles, async best practices, and modern .NET patterns.
```

---

This comprehensive prompt replaces Azure Functions with ASP.NET Core Web API using background services (`IHostedService`) for transcript polling and subscription renewal. The architecture is cleaner for a monolithic API approach with long-running background tasks.
