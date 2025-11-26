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
        var executionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        
        try
        {
            _logger.LogInformation("[WEBHOOK-{ExecutionId}] Starting notification processing - ChangeType: {ChangeType}, Resource: {Resource}",
                executionId, notification.ChangeType, notification.Resource);

            // Extract meeting ID and transcript ID from resource path
            var parts = notification.Resource?.Split('/') ?? Array.Empty<string>();
            if (parts.Length < 6)
            {
                _logger.LogWarning("[WEBHOOK-{ExecutionId}] Invalid resource path: {Resource}", executionId, notification.Resource);
                return;
            }

            var meetingId = parts[3];
            var transcriptId = parts[5];

            _logger.LogInformation("[WEBHOOK-{ExecutionId}] Extracted meetingId: {MeetingId}, transcriptId: {TranscriptId}",
                executionId, meetingId, transcriptId);

            // Check if this is a "created" notification
            if (notification.ChangeType?.ToLower() != "created")
            {
                _logger.LogDebug("[WEBHOOK-{ExecutionId}] Skipping {ChangeType} notification for transcript {TranscriptId}",
                    executionId, notification.ChangeType, transcriptId);
                return;
            }

            // Webhook-level transcript deduplication
            if (!_processedTranscriptIds.TryGetValue(meetingId, out var processedIds))
            {
                processedIds = new HashSet<string>();
                _processedTranscriptIds[meetingId] = processedIds;
                _logger.LogDebug("[WEBHOOK-{ExecutionId}] Initialized webhook transcript tracking for meeting {MeetingId}",
                    executionId, meetingId);
            }

            if (processedIds.Contains(transcriptId))
            {
                _logger.LogInformation("[WEBHOOK-{ExecutionId}] DUPLICATE - Transcript {TranscriptId} already processed by webhook, skipping",
                    executionId, transcriptId);
                return;
            }

            processedIds.Add(transcriptId);
            _logger.LogDebug("[WEBHOOK-{ExecutionId}] Marked transcript {TranscriptId} as processed (webhook level), total tracked: {Count}",
                executionId, transcriptId, processedIds.Count);

            var session = await _sessionStore.GetAsync(meetingId);
            if (session == null || session.Status != MeetingStatus.Active)
            {
                _logger.LogInformation("[WEBHOOK-{ExecutionId}] No active session for meeting {MeetingId}, skipping",
                    executionId, meetingId);
                return;
            }

            _logger.LogInformation("[WEBHOOK-{ExecutionId}] Fetching segments since {LastProcessedTime}",
                executionId, session.LastProcessedTime);

            var allSegments = await _transcriptService.GetNewTranscriptSegmentsAsync(
                meetingId,
                session.LastProcessedTime,
                cancellationToken);

            var segmentsList = allSegments.ToList();
            
            _logger.LogInformation("[WEBHOOK-{ExecutionId}] Retrieved {Count} total segments from transcript service",
                executionId, segmentsList.Count);

            if (!segmentsList.Any())
            {
                _logger.LogInformation("[WEBHOOK-{ExecutionId}] No segments to process", executionId);
                return;
            }

            // Segment-level deduplication using SHARED cache with polling
            var processedSegmentIds = MeetingController.ProcessedSegmentIds.GetOrAdd(meetingId, _ => new HashSet<string>());
            List<TranscriptSegment> actuallyNewSegments;
            
            lock (processedSegmentIds)
            {
                var beforeCount = processedSegmentIds.Count;
                actuallyNewSegments = segmentsList
                    .Where(s => processedSegmentIds.Add(s.Id))
                    .ToList();
                var afterCount = processedSegmentIds.Count;
                
                _logger.LogInformation("[WEBHOOK-{ExecutionId}] Segment deduplication: {Total} retrieved, {New} new, {Duplicate} duplicates. Shared cache: {Before} ? {After}",
                    executionId, segmentsList.Count, actuallyNewSegments.Count, 
                    segmentsList.Count - actuallyNewSegments.Count, beforeCount, afterCount);
            }

            if (!actuallyNewSegments.Any())
            {
                _logger.LogInformation("[WEBHOOK-{ExecutionId}] ALL DUPLICATES - All {Count} segments already processed by polling/webhook",
                    executionId, segmentsList.Count);
                return;
            }

            // Determine interviewer: Use authenticated user display name from OBO token, fallback to first speaker
            var interviewerIdentifier = session.InterviewerDisplayName;
            var firstSpeakerName = session.FirstSpeakerId;
            
            if (string.IsNullOrEmpty(interviewerIdentifier) && string.IsNullOrEmpty(firstSpeakerName) && actuallyNewSegments.Any())
            {
                // Fallback: Use first speaker's name if no OBO token was provided
                // Note: SpeakerId in segments is actually the display name from VTT transcripts
                firstSpeakerName = actuallyNewSegments.First().SpeakerId;
                _logger.LogWarning("[WEBHOOK-{ExecutionId}] No interviewer from OBO token. Using first speaker as interviewer: {SpeakerName}",
                    executionId, firstSpeakerName);
                
                // Update session with first speaker name
                var updatedSession = session with { FirstSpeakerId = firstSpeakerName };
                await _sessionStore.AddOrUpdateAsync(updatedSession);
                session = updatedSession;
            }

            _logger.LogInformation("[WEBHOOK-{ExecutionId}] Broadcasting {Count} NEW segments to SignalR clients",
                executionId, actuallyNewSegments.Count);
            _logger.LogInformation("[WEBHOOK-{ExecutionId}] Role assignment identifiers - Interviewer: '{Interviewer}', Fallback: '{Fallback}'",
                executionId, interviewerIdentifier ?? "(none)", firstSpeakerName ?? "(none)");

            // Assign roles to segments before broadcasting
            foreach (var segment in actuallyNewSegments)
            {
                // Determine if this speaker is the interviewer
                // IMPORTANT: SpeakerId in segments is the display name from VTT (e.g., "Efrain Goyzueta")
                // We compare against InterviewerDisplayName or FirstSpeakerId (both are display names)
                bool isInterviewer = false;
                if (!string.IsNullOrEmpty(interviewerIdentifier))
                {
                    // Compare display names (case-insensitive)
                    isInterviewer = string.Equals(segment.SpeakerId, interviewerIdentifier, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(segment.SpeakerName, interviewerIdentifier, StringComparison.OrdinalIgnoreCase);
                }
                else if (!string.IsNullOrEmpty(firstSpeakerName))
                {
                    // Fallback: Compare with first speaker's display name
                    isInterviewer = string.Equals(segment.SpeakerId, firstSpeakerName, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(segment.SpeakerName, firstSpeakerName, StringComparison.OrdinalIgnoreCase);
                }
                
                var updatedSegment = segment with 
                { 
                    Role = isInterviewer ? SpeakerRole.Interviewer : SpeakerRole.Interviewee 
                };
                
                _logger.LogDebug("[WEBHOOK-{ExecutionId}] Segment from {Speaker} (Name: {SpeakerName}, ID: {SpeakerId}, Role: {Role})",
                    executionId, segment.SpeakerName, segment.SpeakerName, segment.SpeakerId, updatedSegment.Role);
                
                await _signalRService.SendTranscriptUpdateAsync(meetingId, updatedSegment);
            }

            await _signalRService.NotifyMeetingStatusAsync(meetingId, 
                $"Webhook: {actuallyNewSegments.Count} new segment(s)");

            // Update session with latest processed time and speaker tracking
            var newLastProcessedTime = actuallyNewSegments.Max(s => s.Timestamp);
            var finalSession = session with
            {
                LastProcessedTime = newLastProcessedTime,
                FirstSpeakerId = firstSpeakerName,
                InterviewerUserId = session.InterviewerUserId // Keep original user ID
            };
            await _sessionStore.AddOrUpdateAsync(finalSession);

            _logger.LogInformation("[WEBHOOK-{ExecutionId}] ? Complete - Updated session LastProcessedTime: {OldTime} ? {NewTime}",
                executionId, session.LastProcessedTime, newLastProcessedTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WEBHOOK-{ExecutionId}] ? Error processing notification for resource {Resource}",
                executionId, notification.Resource);
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