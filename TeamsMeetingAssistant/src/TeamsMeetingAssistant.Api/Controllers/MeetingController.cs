using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Client;
using System.Security.Claims;
using System.Text.Json;
using System.Collections.Concurrent;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MeetingController : ControllerBase
{
    private readonly IMeetingSessionStore _sessionStore;
    private readonly ITranscriptService _transcriptService;
    private readonly ISignalRService _signalRService;
    private readonly IQuestionGenerationService _questionService;
    private readonly ILogger<MeetingController> _logger;
    private readonly SubscriptionRenewalService _renewalService;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    // Static dictionary to manage real-time monitoring tasks
    private static readonly Dictionary<string, RealTimeMonitoringTask> _activeMonitoringTasks = new();
    
    // SHARED deduplication tracker - used by both polling and webhooks
    public static readonly ConcurrentDictionary<string, HashSet<string>> ProcessedSegmentIds = new();

    public MeetingController(
        IMeetingSessionStore sessionStore,
        ITranscriptService transcriptService,
        ISignalRService signalRService,
        IQuestionGenerationService questionService,
        ILogger<MeetingController> logger,
        SubscriptionRenewalService renewalService,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _sessionStore = sessionStore;
        _transcriptService = transcriptService;
        _signalRService = signalRService;
        _questionService = questionService;
        _logger = logger;
        _renewalService = renewalService;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartMonitoring(
        [FromBody] StartMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting monitoring for meeting URL/ID: {MeetingUrlOrId}", request.MeetingId);

            // Get user ID from request or use default
            var userId = request.UserId ?? _configuration["AzureAd:DefaultUserId"] ?? "aacdff60-4840-45f4-814e-4243bdd0636e";

            string meetingId;
            string? joinWebUrl = null;

            // Step 1: Determine if input is a URL or meeting ID
            if (request.MeetingId.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                request.MeetingId.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Meeting URL detected, fetching meeting info...");
                joinWebUrl = request.MeetingId;

                try
                {
                    var meetingInfo = await _transcriptService.GetMeetingInfoAsync(joinWebUrl, userId, cancellationToken);
                    
                    if (meetingInfo == null || string.IsNullOrEmpty(meetingInfo.MeetingId))
                    {
                        return BadRequest(new
                        {
                            error = "Could not retrieve meeting information from the provided URL",
                            suggestion = "Ensure the URL is valid and you have access to the meeting"
                        });
                    }

                    meetingId = meetingInfo.MeetingId;
                    _logger.LogInformation("Successfully extracted meeting ID: {MeetingId} from URL", meetingId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to extract meeting ID from URL: {Url}", joinWebUrl);
                    return BadRequest(new
                    {
                        error = "Failed to retrieve meeting information from URL",
                        details = ex.Message,
                        suggestion = "Verify the meeting URL is correct and accessible"
                    });
                }
            }
            else
            {
                // It's already a meeting ID
                meetingId = request.MeetingId;
                _logger.LogInformation("Using provided meeting ID: {MeetingId}", meetingId);
            }

            // Step 2: Test access to meeting transcripts using the delta-enabled service
            // This will establish the delta link for future queries
            _logger.LogInformation("Fetching initial transcripts and establishing delta link for meeting {MeetingId}", meetingId);
            
            var initialSegments = await _transcriptService.GetNewTranscriptSegmentsAsync(
                meetingId,
                DateTimeOffset.UtcNow.AddMinutes(-5), // Start from 5 minutes ago
                cancellationToken);

            var initialSegmentsList = initialSegments.ToList();
            
            _logger.LogInformation("Initial fetch returned {Count} transcript segments", initialSegmentsList.Count);

            // Step 3: Create or update session
            var session = new MeetingSession(
                meetingId,
                $"user-{userId}@domain.com", // Placeholder organizer email
                DateTimeOffset.UtcNow,
                null,
                true,
                null,
                DateTimeOffset.UtcNow,
                MeetingStatus.Active
            );

            await _sessionStore.AddOrUpdateAsync(session);

            // Step 4: Configure polling interval (10-60 seconds for real-time)
            var pollingInterval = request.PollingIntervalSeconds ?? 60;
            if (pollingInterval < 10)
            {
                _logger.LogWarning("Polling interval {Interval}s is too low, setting to minimum 10 seconds", pollingInterval);
                pollingInterval = 10;
            }
            else if (pollingInterval > 60)
            {
                _logger.LogWarning("Polling interval {Interval}s is too high, setting to maximum 60 seconds", pollingInterval);
                pollingInterval = 60;
            }

            await StartRealTimeTranscriptMonitoringAsync(userId, meetingId, pollingInterval);

            // Step 5: Subscribe to webhooks for real-time updates
            bool webhooksEnabled = request.UseWebhooks;
            string? subscriptionId = null;

            if (webhooksEnabled)
            {
                try
                {
                    // Use the public URL or configured webhook endpoint
                    var webhookBaseUrl = _configuration["Webhook:BaseUrl"] ?? $"{Request.Scheme}://{Request.Host}";
                    var webhookUrl = $"{webhookBaseUrl}/api/webhook/transcript";
                    
                    _logger.LogInformation("Creating webhook subscription for meeting {MeetingId} with URL: {WebhookUrl}", 
                        meetingId, webhookUrl);

                    var subscription = await _transcriptService.SubscribeToTranscriptChangesAsync(
                        meetingId,
                        webhookUrl,
                        cancellationToken);

                    subscriptionId = subscription.Id;
                    var expirationTime = subscription.ExpirationDateTime ?? DateTimeOffset.UtcNow.AddMinutes(30);

                    _renewalService.TrackSubscription(meetingId, subscription.Id, expirationTime);
                    
                    _logger.LogInformation("✅ Webhook subscription created: {SubscriptionId}, expires at {ExpirationTime}", 
                        subscription.Id, expirationTime);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Failed to create webhook subscription for meeting {MeetingId}, falling back to polling only", 
                        meetingId);
                    webhooksEnabled = false;
                    // Continue without webhooks - polling will handle updates
                }
            }

            _logger.LogInformation("Started monitoring meeting {MeetingId} - Polling: {Interval}s, Webhooks: {Webhooks}", 
                meetingId, pollingInterval, webhooksEnabled ? "Enabled" : "Disabled");

            return Ok(new { 
                meetingId = meetingId,
                meetingUrl = joinWebUrl,
                status = "monitoring",
                pollingInterval = pollingInterval,
                userId = userId,
                initialTranscriptCount = initialSegmentsList.Count,
                webhooks = new
                {
                    enabled = webhooksEnabled,
                    subscriptionId = subscriptionId
                },
                message = webhooksEnabled 
                    ? "Real-time monitoring active with webhooks and delta polling" 
                    : "Real-time monitoring active with delta polling only",
                deltaQueryEnabled = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start monitoring for: {MeetingUrlOrId}", request.MeetingId);
            return StatusCode(500, new { error = ex.Message, details = ex.StackTrace });
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> StopMonitoring(
        [FromBody] StopMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Stopping monitoring for meeting {MeetingId}", request.MeetingId);

            // Stop real-time monitoring
            if (_activeMonitoringTasks.TryGetValue(request.MeetingId, out var monitoringTask))
            {
                monitoringTask.CancellationTokenSource.Cancel();
                _activeMonitoringTasks.Remove(request.MeetingId);
                _logger.LogInformation("Stopped real-time monitoring for meeting {MeetingId}", request.MeetingId);
            }

            // Clear deduplication cache for this meeting
            if (ProcessedSegmentIds.TryRemove(request.MeetingId, out var processedIds))
            {
                _logger.LogInformation("Cleared {Count} processed segment IDs for meeting {MeetingId}", 
                    processedIds.Count, request.MeetingId);
            }

            // Update session status
            var session = await _sessionStore.GetAsync(request.MeetingId);
            if (session != null)
            {
                var completedSession = session with
                {
                    Status = MeetingStatus.Completed,
                    EndTime = DateTimeOffset.UtcNow
                };

                await _sessionStore.AddOrUpdateAsync(completedSession);
            }

            // Clean up webhooks
            _renewalService.UntrackSubscription(request.MeetingId);
            
            // Clear webhook transcript cache
            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                await httpClient.PostAsync($"/api/webhook/clear-cache/{request.MeetingId}", null, cancellationToken);
                _logger.LogInformation("Cleared webhook transcript cache for meeting {MeetingId}", request.MeetingId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clear webhook cache for meeting {MeetingId}", request.MeetingId);
            }

            // Notify clients that monitoring has stopped
            await _signalRService.NotifyMeetingStatusAsync(request.MeetingId, "Monitoring stopped");

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
        try
        {
            var sessions = await _sessionStore.GetActiveSessionsAsync();
            var monitoringStatus = _activeMonitoringTasks.Select(kvp => new
            {
                MeetingId = kvp.Key,
                IsActive = !kvp.Value.CancellationTokenSource.Token.IsCancellationRequested,
                StartedAt = kvp.Value.StartedAt,
                LastUpdate = kvp.Value.LastUpdateAt,
                PollingInterval = kvp.Value.PollingIntervalSeconds
            }).ToList();

            return Ok(new
            {
                sessions,
                realTimeMonitoring = monitoringStatus
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active sessions");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{meetingId}")]
    public async Task<IActionResult> GetSession(string meetingId)
    {
        try
        {
            var session = await _sessionStore.GetAsync(meetingId);

            if (session == null)
            {
                return NotFound(new { error = "Meeting session not found" });
            }

            var isMonitoring = _activeMonitoringTasks.ContainsKey(meetingId) && 
                              !_activeMonitoringTasks[meetingId].CancellationTokenSource.Token.IsCancellationRequested;

            return Ok(new
            {
                session,
                isRealTimeMonitoring = isMonitoring
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get session for meeting {MeetingId}", meetingId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpDelete("{meetingId}")]
    public async Task<IActionResult> DeleteSession(string meetingId)
    {
        try
        {
            _logger.LogInformation("Deleting session for meeting {MeetingId}", meetingId);

            // Stop monitoring if active
            if (_activeMonitoringTasks.TryGetValue(meetingId, out var monitoringTask))
            {
                monitoringTask.CancellationTokenSource.Cancel();
                _activeMonitoringTasks.Remove(meetingId);
            }

            await _sessionStore.RemoveAsync(meetingId);
            _renewalService.UntrackSubscription(meetingId);

            return Ok(new { message = "Session deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete session for meeting {MeetingId}", meetingId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    #region Real-Time Monitoring Implementation

    private async Task StartRealTimeTranscriptMonitoringAsync(string userId, string meetingId, int pollingIntervalSeconds)
    {
        // Cancel any existing monitoring for this meeting
        if (_activeMonitoringTasks.TryGetValue(meetingId, out var existingTask))
        {
            existingTask.CancellationTokenSource.Cancel();
            _activeMonitoringTasks.Remove(meetingId);
        }

        // Create new monitoring task
        var cts = new CancellationTokenSource();
        var monitoringTask = new RealTimeMonitoringTask
        {
            CancellationTokenSource = cts,
            StartedAt = DateTimeOffset.UtcNow,
            LastUpdateAt = DateTimeOffset.UtcNow,
            PollingIntervalSeconds = pollingIntervalSeconds
        };

        _activeMonitoringTasks[meetingId] = monitoringTask;

        // Start monitoring in background
        _ = Task.Run(async () => await MonitorTranscriptsAsync(userId, meetingId, pollingIntervalSeconds, cts.Token));
    }

    private async Task MonitorTranscriptsAsync(string userId, string meetingId, int pollingIntervalSeconds, CancellationToken cancellationToken)
    {
        var lastProcessedTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        
        // Get or create the shared deduplication set for this meeting
        var processedSegmentIds = ProcessedSegmentIds.GetOrAdd(meetingId, _ => new HashSet<string>());

        _logger.LogInformation("Started real-time transcript monitoring for meeting {MeetingId} with {Interval}s polling interval", 
            meetingId, pollingIntervalSeconds);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for the polling interval BEFORE fetching
                    _logger.LogDebug("Waiting {Interval} seconds before next poll for meeting {MeetingId}", 
                        pollingIntervalSeconds, meetingId);
                    
                    await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds), cancellationToken);

                    // Update monitoring task timestamp
                    if (_activeMonitoringTasks.TryGetValue(meetingId, out var task))
                    {
                        task.LastUpdateAt = DateTimeOffset.UtcNow;
                    }

                    _logger.LogInformation("Polling for new transcripts for meeting {MeetingId} at {Time}", 
                        meetingId, DateTimeOffset.UtcNow.ToString("HH:mm:ss"));

                    // Get new transcript segments using the delta query
                    var newSegments = await _transcriptService.GetNewTranscriptSegmentsAsync(
                        meetingId,
                        lastProcessedTime,
                        cancellationToken);

                    var newSegmentsList = newSegments?.ToList() ?? new List<TranscriptSegment>();

                    if (newSegmentsList.Any())
                    {
                        // Deduplicate using SHARED set (also used by webhooks)
                        List<TranscriptSegment> actuallyNewSegments;
                        lock (processedSegmentIds)
                        {
                            actuallyNewSegments = newSegmentsList
                                .Where(s => processedSegmentIds.Add(s.Id)) // Only segments we haven't seen
                                .ToList();
                        }

                        if (actuallyNewSegments.Any())
                        {
                            _logger.LogInformation("Found {Count} NEW transcript segments for meeting {MeetingId} (polling)", 
                                actuallyNewSegments.Count, meetingId);

                            // Send segments to clients via SignalR
                            foreach (var segment in actuallyNewSegments)
                            {
                                await _signalRService.SendTranscriptUpdateAsync(meetingId, segment);
                                _logger.LogDebug("Sent segment: {Speaker}: {Content}", 
                                    segment.SpeakerName, 
                                    segment.Content.Length > 50 ? segment.Content.Substring(0, 50) + "..." : segment.Content);
                            }

                            // Update last processed time to the newest segment
                            lastProcessedTime = actuallyNewSegments.Max(s => s.Timestamp);
                            _logger.LogDebug("Updated lastProcessedTime to {Time}", lastProcessedTime.ToString("HH:mm:ss"));

                            // Update session last processed time
                            var session = await _sessionStore.GetAsync(meetingId);
                            if (session != null)
                            {
                                var updatedSession = session with
                                {
                                    LastProcessedTime = lastProcessedTime
                                };
                                await _sessionStore.AddOrUpdateAsync(updatedSession);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("No new segments (all {Count} already processed for meeting {MeetingId})", 
                                newSegmentsList.Count, meetingId);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No segments returned from delta query for meeting {MeetingId}", meetingId);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Don't log as error - this is expected when stopping
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in monitoring loop for meeting {MeetingId}", meetingId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Real-time transcript monitoring stopped for meeting {MeetingId}", meetingId);
        }
        finally
        {
            _activeMonitoringTasks.Remove(meetingId);
            _logger.LogInformation("Cleaned up monitoring task for meeting {MeetingId}", meetingId);
        }
    }

    #endregion
}

#region DTOs and Helper Classes

public record StartMonitoringRequest(
    string MeetingId,
    string UserId,
    bool UseWebhooks = true,
    int? PollingIntervalSeconds = null);

public record StopMonitoringRequest(string MeetingId);

public class RealTimeMonitoringTask
{
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdateAt { get; set; } = DateTimeOffset.UtcNow;
    public int PollingIntervalSeconds { get; set; } = 20;
}

#endregion