using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly ITranscriptService _transcriptService;
    private readonly ISignalRService _signalRService;
    private readonly IQuestionGenerationService _questionService;
    private readonly IMeetingSessionStore _sessionStore;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(
        ITranscriptService transcriptService,
        ISignalRService signalRService,
        IQuestionGenerationService questionService,
        IMeetingSessionStore sessionStore,
        ILogger<WebhookController> logger)
    {
        _transcriptService = transcriptService;
        _signalRService = signalRService;
        _questionService = questionService;
        _sessionStore = sessionStore;
        _logger = logger;
    }

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

            _logger.LogDebug("Received webhook notification: {Notification}", json);

            var notifications = JsonSerializer.Deserialize<ChangeNotificationCollection>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (notifications?.Value == null)
            {
                _logger.LogWarning("Invalid notification payload received");
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
        try
        {
            // Extract meeting ID from resource path
            // e.g., /communications/onlineMeetings/{meetingId}/transcripts/{transcriptId}
            var parts = notification.Resource?.Split('/') ?? Array.Empty<string>();
            if (parts.Length < 5)
            {
                _logger.LogWarning("Invalid resource path in notification: {Resource}", notification.Resource);
                return;
            }

            var meetingId = parts[3];

            _logger.LogDebug("Processing notification for meeting {MeetingId}", meetingId);

            var session = await _sessionStore.GetAsync(meetingId);
            if (session == null || session.Status != MeetingStatus.Active)
            {
                _logger.LogDebug("No active session found for meeting {MeetingId}", meetingId);
                return;
            }

            // Fetch and process new transcript segments
            var newSegments = await _transcriptService.GetNewTranscriptSegmentsAsync(
                meetingId,
                session.LastProcessedTime,
                cancellationToken);

            _logger.LogInformation("Found {Count} new segments for meeting {MeetingId}",
                newSegments.Count(), meetingId);

            foreach (var segment in newSegments)
            {
                await _signalRService.SendTranscriptUpdateAsync(meetingId, segment);
            }

            if (newSegments.Any())
            {
                // Generate questions if we have enough segments
                if (newSegments.Count() >= 3)
                {
                    var questions = await _questionService.GenerateQuestionsAsync(
                        newSegments.ToList(),
                        session.OrganizerEmail,
                        cancellationToken);

                    await _signalRService.SendQuestionSuggestionsAsync(meetingId, questions);
                }

                // Update session
                var updatedSession = session with
                {
                    LastProcessedTime = newSegments.Max(s => s.Timestamp)
                };
                await _sessionStore.AddOrUpdateAsync(updatedSession);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing notification for resource {Resource}", notification.Resource);
        }
    }
}

// Models for Graph API notifications
public class ChangeNotificationCollection
{
    public List<ChangeNotification>? Value { get; set; }
}

public class ChangeNotification
{
    public string? ChangeType { get; set; }
    public string? Resource { get; set; }
    public string? SubscriptionId { get; set; }
    public DateTimeOffset? SubscriptionExpirationDateTime { get; set; }
    public string? ClientState { get; set; }
    public string? TenantId { get; set; }
}