---
paths: src/TeamsMeetingAssistant.Web/**/*.razor
---

# Blazor Component Rules

These rules apply when working on Blazor Server components.

## Component Lifecycle

### Initialization
```csharp
protected override async Task OnInitializedAsync()
{
    // One-time initialization here
    await SetupSignalRConnectionAsync();
}
```

### Disposal
Always implement IAsyncDisposable for SignalR connections:
```csharp
@implements IAsyncDisposable

public async ValueTask DisposeAsync()
{
    if (hubConnection is not null)
    {
        await hubConnection.InvokeAsync("LeaveMeeting", MeetingId);
        await hubConnection.DisposeAsync();
    }
}
```

## SignalR Integration

### Hub Connection Setup
```csharp
hubConnection = new HubConnectionBuilder()
    .WithUrl(signalRUrl)
    .WithAutomaticReconnect()
    .Build();

hubConnection.On<TranscriptSegment>("NewTranscript", segment =>
{
    transcriptSegments.Add(segment);
    InvokeAsync(StateHasChanged);  // MUST use InvokeAsync!
});

await hubConnection.StartAsync();
```

### State Updates from SignalR
Always use `InvokeAsync(StateHasChanged)`:
```csharp
hubConnection.On<List<QuestionSuggestion>>("QuestionSuggestions", suggestions =>
{
    questionSuggestions = suggestions;
    InvokeAsync(StateHasChanged);  // Required for UI update
});
```

## Parameters

### Parameter Validation
```csharp
[Parameter]
public string MeetingId { get; set; } = string.Empty;

protected override void OnParametersSet()
{
    if (string.IsNullOrEmpty(MeetingId))
    {
        throw new ArgumentException("MeetingId is required");
    }
}
```

## Common Mistakes

### ❌ Not Using InvokeAsync for SignalR Callbacks
```csharp
hubConnection.On<string>("Update", data =>
{
    StateHasChanged();  // WRONG: Not thread-safe!
});
```

### ✅ Use InvokeAsync
```csharp
hubConnection.On<string>("Update", data =>
{
    InvokeAsync(StateHasChanged);  // Correct
});
```

### ❌ Forgetting to Dispose SignalR Connection
```csharp
// No DisposeAsync implementation — connection leaks!
```

### ✅ Implement IAsyncDisposable
```csharp
@implements IAsyncDisposable

public async ValueTask DisposeAsync()
{
    await hubConnection?.DisposeAsync();
}
```

### ❌ Async Void Event Handlers
```csharp
private async void OnClick()  // WRONG: async void!
{
    await DoWorkAsync();
}
```

### ✅ Return Task
```csharp
private async Task OnClick()  // Correct
{
    await DoWorkAsync();
}
```
