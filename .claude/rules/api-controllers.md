---
paths: src/TeamsMeetingAssistant.Api/Controllers/**/*.cs
---

# API Controller Rules

These rules apply when working on ASP.NET Core API controllers.

## Controller Patterns

### Route Naming
```csharp
[ApiController]
[Route("api/[controller]")]
public class MeetingController : ControllerBase
```

### Action Return Types
Always use `IActionResult` or `ActionResult<T>`:
```csharp
[HttpGet("{id}")]
public async Task<ActionResult<MeetingSession>> GetSession(string id)
{
    var session = await _sessionStore.GetAsync(id);
    return session == null ? NotFound() : Ok(session);
}
```

### Cancellation Tokens
Every async action MUST accept CancellationToken:
```csharp
[HttpPost]
public async Task<IActionResult> StartMonitoring(
    [FromBody] StartMonitoringRequest request,
    CancellationToken cancellationToken)
```

## Error Handling

### Standard Error Response Format
```csharp
return StatusCode(500, new { error = ex.Message });
```

### Validation Errors
```csharp
if (!ModelState.IsValid)
{
    return BadRequest(new { errors = ModelState.Values
        .SelectMany(v => v.Errors)
        .Select(e => e.ErrorMessage) });
}
```

## Common Mistakes

### ❌ Missing CancellationToken
```csharp
public async Task<IActionResult> Get()  // Missing CancellationToken!
```

### ✅ Correct
```csharp
public async Task<IActionResult> Get(CancellationToken cancellationToken)
```

### ❌ Hardcoded Status Codes
```csharp
return new StatusCodeResult(404);  // Use NotFound() instead
```

### ✅ Use Helper Methods
```csharp
return NotFound();
return Ok(data);
return BadRequest(error);
```

## Logging

Always log important actions:
```csharp
_logger.LogInformation("Started monitoring meeting {MeetingId}", request.MeetingId);
_logger.LogError(ex, "Failed to process meeting {MeetingId}", request.MeetingId);
```
