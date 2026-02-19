---
paths: src/TeamsMeetingAssistant.Infrastructure/**/*.cs
---

# Infrastructure Layer Rules

These rules apply when working on external service integrations (Graph API, Azure OpenAI, SignalR).

## Graph API Integration

### Authentication
Always use ClientSecretCredential:
```csharp
var credential = new ClientSecretCredential(
    tenantId,
    clientId,
    clientSecret);

var graphClient = new GraphServiceClient(credential);
```

### Retry Policies
Use Polly for resilience:
```csharp
var retryPolicy = HttpPolicyExtensions
    .HandleTransientHttpError()
    .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
    .WaitAndRetryAsync(3, retryAttempt =>
        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
```

### Request Batching
Batch Graph API requests when possible:
```csharp
var batch = new BatchRequestContent();
batch.AddBatchRequestStep(request1);
batch.AddBatchRequestStep(request2);

var batchResponse = await graphClient.Batch.Request().PostAsync(batch);
```

## Azure OpenAI Integration

### Error Handling
Handle quota and rate limiting:
```csharp
try
{
    var response = await openAIClient.GetChatCompletionsAsync(...);
}
catch (RequestFailedException ex) when (ex.Status == 429)
{
    _logger.LogWarning("OpenAI rate limit hit, backing off");
    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
    // Retry logic here
}
```

### Token Management
Always set MaxTokens to avoid runaway costs:
```csharp
var options = new ChatCompletionsOptions
{
    Messages = { ... },
    MaxTokens = 800,  // REQUIRED
    Temperature = 0.7f
};
```

## SignalR Integration

### Hub Context Usage
Use IHubContext for sending messages from services:
```csharp
private readonly IHubContext<TranscriptHub> _hubContext;

public async Task SendTranscriptUpdateAsync(string meetingId, TranscriptSegment segment)
{
    await _hubContext.Clients
        .Group(meetingId)
        .SendAsync("NewTranscript", segment);
}
```

### Group Management
Always track group membership:
```csharp
await Groups.AddToGroupAsync(Context.ConnectionId, meetingId);
_logger.LogInformation("Client {ConnectionId} joined meeting {MeetingId}",
    Context.ConnectionId, meetingId);
```

## Common Mistakes

### ❌ Not Handling Graph API Throttling
```csharp
var result = await graphClient.Users.Request().GetAsync();  // Can throw 429!
```

### ✅ Use Polly Retry Policy
```csharp
var result = await retryPolicy.ExecuteAsync(() =>
    graphClient.Users.Request().GetAsync());
```

### ❌ Missing MaxTokens in OpenAI Requests
```csharp
// No MaxTokens — can consume entire quota!
var options = new ChatCompletionsOptions { Messages = { ... } };
```

### ✅ Always Set MaxTokens
```csharp
var options = new ChatCompletionsOptions
{
    Messages = { ... },
    MaxTokens = 800  // Protect against runaway costs
};
```

### ❌ Hardcoded Secrets in Infrastructure
```csharp
var apiKey = "sk-proj-xxxxx";  // NEVER hardcode!
```

### ✅ Configuration Injection
```csharp
var apiKey = _configuration["AzureOpenAI:ApiKey"];
if (string.IsNullOrEmpty(apiKey))
{
    throw new InvalidOperationException("OpenAI API key not configured");
}
```
