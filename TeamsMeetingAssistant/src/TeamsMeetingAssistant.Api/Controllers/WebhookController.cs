using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using TeamsMeetingAssistant.Infrastructure;

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
    
    // Note: Now using SHARED deduplication from MeetingController
    // Track processed transcript IDs per meeting to avoid duplicate webhook processing
    private static readonly Dictionary<string, HashSet<string>> _processedTranscriptIds = new();

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
            _logger.LogInformation("Validating webhook subscription with token: {Token}", validationToken);
            return Ok(validationToken);
        }

        try
        {
            // Read notification payload
            using var reader = new StreamReader(Request.Body);
            var json = await reader.ReadToEndAsync(cancellationToken);

            _logger.LogInformation("Received webhook notification");
            _logger.LogDebug("Webhook payload: {Notification}", json);

            var notifications = JsonSerializer.Deserialize<ChangeNotificationCollection>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (notifications?.Value == null)
            {
                _logger.LogWarning("Invalid notification payload received");
                return BadRequest("Invalid notification payload");
            }

            _logger.LogInformation("Processing {Count} webhook notification(s)", notifications.Value.Count);

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
            // Log the notification type
            _logger.LogInformation("Webhook notification - ChangeType: {ChangeType}, Resource: {Resource}",
                notification.ChangeType, notification.Resource);

            // Extract meeting ID and transcript ID from resource path
            // e.g., /communications/onlineMeetings/{meetingId}/transcripts/{transcriptId}
            var parts = notification.Resource?.Split('/') ?? Array.Empty<string>();
            if (parts.Length < 6)
            {
                _logger.LogWarning("Invalid resource path in notification: {Resource}", notification.Resource);
                return;
            }

            var meetingId = parts[3];
            var transcriptId = parts[5];

            // Check if this is a "created" notification (new transcript file)
            if (notification.ChangeType?.ToLower() != "created")
            {
                _logger.LogDebug("Ignoring non-created notification: {ChangeType} for transcript {TranscriptId}",
                    notification.ChangeType, transcriptId);
                return;
            }

            _logger.LogInformation("New transcript created: {TranscriptId} for meeting {MeetingId}",
                transcriptId, meetingId);

            // Check if we've already processed this transcript ID (webhook-level deduplication)
            if (!_processedTranscriptIds.TryGetValue(meetingId, out var processedIds))
            {
                processedIds = new HashSet<string>();
                _processedTranscriptIds[meetingId] = processedIds;
            }

            if (processedIds.Contains(transcriptId))
            {
                _logger.LogDebug("Transcript {TranscriptId} already processed for meeting {MeetingId}, skipping",
                    transcriptId, meetingId);
                return;
            }

            // Mark this transcript as processed (webhook-level)
            processedIds.Add(transcriptId);

            var session = await _sessionStore.GetAsync(meetingId);
            if (session == null || session.Status != MeetingStatus.Active)
            {
                _logger.LogDebug("No active session found for meeting {MeetingId}, skipping notification", meetingId);
                return;
            }

            _logger.LogInformation("Fetching segments from new transcript {TranscriptId}",
                transcriptId);

            // Fetch segments from the entire meeting (Graph API will return all transcripts)
            // But filter by timestamp to only get new ones
            var allSegments = await _transcriptService.GetNewTranscriptSegmentsAsync(
                meetingId,
                session.LastProcessedTime,
                cancellationToken);

            var segmentsList = allSegments.ToList();
            
            if (!segmentsList.Any())
            {
                _logger.LogDebug("No new segments found in transcript {TranscriptId}", transcriptId);
                return;
            }

            // Deduplicate segments using SHARED deduplication cache (segment-level)
            var processedSegmentIds = MeetingController.ProcessedSegmentIds.GetOrAdd(meetingId, _ => new HashSet<string>());
            List<TranscriptSegment> actuallyNewSegments;
            
            lock (processedSegmentIds)
            {
                actuallyNewSegments = segmentsList
                    .Where(s => processedSegmentIds.Add(s.Id)) // Only segments we haven't seen
                    .ToList();
            }

            if (!actuallyNewSegments.Any())
            {
                _logger.LogDebug("All {Count} segments from transcript {TranscriptId} already processed", 
                    segmentsList.Count, transcriptId);
                return;
            }

            _logger.LogInformation("Found {Count} NEW segment(s) from transcript {TranscriptId} (webhook)",
                actuallyNewSegments.Count, transcriptId);

            // Send segments to SignalR clients
            foreach (var segment in actuallyNewSegments)
            {
                await _signalRService.SendTranscriptUpdateAsync(meetingId, segment);
                _logger.LogDebug("Sent segment via SignalR: {Speaker}: {Content}", 
                    segment.SpeakerName, 
                    segment.Content.Length > 50 ? segment.Content.Substring(0, 50) + "..." : segment.Content);
            }

            // Send status update to clients
            await _signalRService.NotifyMeetingStatusAsync(meetingId, 
                $"Received {actuallyNewSegments.Count} new transcript segment(s) via webhook");

            // Generate questions if we have enough segments
            if (actuallyNewSegments.Count >= 3)
            {
                _logger.LogInformation("?? Generating AI questions for {Count} segments", actuallyNewSegments.Count);
                
                var questions = await _questionService.GenerateQuestionsAsync(
                    actuallyNewSegments,
                    session.OrganizerEmail,
                    cancellationToken);

                if (questions.Any())
                {
                    await _signalRService.SendQuestionSuggestionsAsync(meetingId, questions);
                    _logger.LogInformation("Sent {Count} AI question(s) to clients", questions.Count);
                }
            }

            // Update session with latest processed time
            var updatedSession = session with
            {
                LastProcessedTime = actuallyNewSegments.Max(s => s.Timestamp)
            };
            await _sessionStore.AddOrUpdateAsync(updatedSession);

            _logger.LogInformation("Updated session last processed time to {Time}", 
                updatedSession.LastProcessedTime);
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