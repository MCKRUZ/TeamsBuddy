---
paths: src/TeamsMeetingAssistant.Api/Services/**/*.cs
---

# Background Service Rules

These rules apply when working on IHostedService implementations.

## Service Lifecycle

### ExecuteAsync Pattern
```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            await DoWorkAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in background service");
        }

        await Task.Delay(interval, stoppingToken);
    }
}
```

### Scoped Service Access
Always create a scope for scoped services:
```csharp
using var scope = _serviceProvider.CreateScope();
var service = scope.ServiceProvider.GetRequiredService<IMyService>();
```

## Polling Best Practices

### Parallel Processing
Use `Parallel.ForEachAsync` with max concurrency:
```csharp
var options = new ParallelOptions
{
    MaxDegreeOfParallelism = 5,
    CancellationToken = cancellationToken
};

await Parallel.ForEachAsync(sessions, options, async (session, ct) =>
{
    await ProcessSessionAsync(session, ct);
});
```

### Graceful Shutdown
Respect cancellation tokens:
```csharp
if (cancellationToken.IsCancellationRequested)
{
    _logger.LogInformation("Service stopping, cleaning up...");
    return;
}
```

## Error Handling

### Never Let Background Service Crash
```csharp
try
{
    await PollTranscriptsAsync(stoppingToken);
}
catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
{
    // Normal shutdown, don't log as error
}
catch (Exception ex)
{
    _logger.LogError(ex, "Error in polling loop");
    // Continue running — don't let background service die
}
```

## Common Mistakes

### ❌ Not Using Scopes for Scoped Services
```csharp
// WRONG: Injecting scoped service into singleton hosted service
public TranscriptPollingService(ITranscriptService service) // Will fail!
```

### ✅ Create Scope in ExecuteAsync
```csharp
using var scope = _serviceProvider.CreateScope();
var service = scope.ServiceProvider.GetRequiredService<ITranscriptService>();
```

### ❌ Blocking the Thread
```csharp
Thread.Sleep(5000);  // WRONG: Blocks thread pool
```

### ✅ Async Delay
```csharp
await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
```
