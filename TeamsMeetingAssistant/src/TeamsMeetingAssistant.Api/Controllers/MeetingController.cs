using Microsoft.AspNetCore.Mvc;
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
    private readonly ITokenExchangeService _tokenExchangeService;
    private readonly IServiceScopeFactory _serviceScopeFactory;


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
        IHttpClientFactory httpClientFactory,
        ITokenExchangeService tokenExchangeService,
        IServiceScopeFactory serviceScopeFactory)
    {
        _sessionStore = sessionStore;
        _transcriptService = transcriptService;
        _signalRService = signalRService;
        _questionService = questionService;
        _logger = logger;
        _renewalService = renewalService;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _tokenExchangeService = tokenExchangeService;
        _serviceScopeFactory = serviceScopeFactory;
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartMonitoring(
        [FromBody] StartMonitoringRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting monitoring for meeting URL/ID: {MeetingUrlOrId}", request.MeetingId);

            string? accessToken = null;
            bool usedSso = false;

            // Determine authentication mode: SSO or fallback
            if (!string.IsNullOrWhiteSpace(request.IdToken))
            {
                _logger.LogInformation("SSO token provided, using On-Behalf-Of flow");

                try
                {
                    // Validate and extract user info from ID token
                    var isValid = await _tokenExchangeService.ValidateTokenAsync(request.IdToken);
                    if (!isValid)
                    {
                        return Unauthorized(new { error = "Invalid ID token" });
                    }

                    // Exchange ID token for Graph API access token
                    accessToken = await _tokenExchangeService.ExchangeTokenAsync(request.IdToken, cancellationToken);
                    usedSso = true;

                    _logger.LogInformation("SSO authentication successful");
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError(ex, "User consent required for SSO");
                    return Unauthorized(new
                    {
                        error = "consent_required",
                        message = "User consent is required. Please consent to the required permissions.",
                        details = ex.Message
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SSO token exchange failed");
                    return BadRequest(new
                    {
                        error = "sso_failed",
                        message = "Failed to exchange SSO token",
                        details = ex.Message
                    });
                }
            }
            else
            {
                // Fallback mode: Use app-only token
                _logger.LogInformation("No SSO token provided, using fallback mode");
                accessToken = await _tokenExchangeService.GetAppOnlyTokenAsync(cancellationToken);
                usedSso = false;
            }

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
                    // Set the access token in the transcript service before making any calls
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        _transcriptService.SetAccessToken(accessToken);
                    }

                    var meetingInfo = await _transcriptService.GetMeetingInfoAsync(joinWebUrl, cancellationToken);

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

            // Set the access token in the transcript service before making any calls
            if (!string.IsNullOrEmpty(accessToken))
            {
                _transcriptService.SetAccessToken(accessToken);
            }

            var initialSegments = await _transcriptService.GetNewTranscriptSegmentsAsync(
                meetingId,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                cancellationToken);

            var initialSegmentsList = initialSegments.ToList();

            _logger.LogInformation("Initial fetch returned {Count} transcript segments", initialSegmentsList.Count);

            // Step 3: Extract interviewer user ID from ID token (if SSO is used)
            string? interviewerUserId = null;
            string? interviewerDisplayName = null;
            if (!string.IsNullOrWhiteSpace(request.IdToken))
            {
                try
                {
                    var userInfo = await _tokenExchangeService.GetUserInfoFromTokenAsync(request.IdToken);
                    interviewerUserId = userInfo.UserId;
                    interviewerDisplayName = userInfo.Name ?? userInfo.UserPrincipalName;
                    _logger.LogInformation("Extracted interviewer from OBO token: ID={UserId}, Name={Name}",
                        userInfo.UserId, interviewerDisplayName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract user info from ID token");
                }
            }

            // Step 4: Create or update session
            var session = new MeetingSession(
                meetingId,
                $"meeting-organizer@domain.com", // Organizer email is tracked in the session but not needed for monitoring
                DateTimeOffset.UtcNow,
                null,
                true,
                null,
                DateTimeOffset.UtcNow,
                MeetingStatus.Active,
                FirstSpeakerId: null,
                InterviewerUserId: interviewerUserId,
                InterviewerDisplayName: interviewerDisplayName,
                State: ConversationState.WaitingForInterviewerQuestion,
                LastQuestionGeneratedAt: null
            );

            await _sessionStore.AddOrUpdateAsync(session);

            // Step 5: Configure polling interval
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

            await StartRealTimeTranscriptMonitoringAsync(meetingId, pollingInterval);

            // Step 6: Subscribe to webhooks for real-time updates
            bool webhooksEnabled = request.UseWebhooks;
            string? subscriptionId = null;

            if (webhooksEnabled)
            {
                try
                {
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
                }
            }

            _logger.LogInformation("Started monitoring meeting {MeetingId} - Auth: {AuthMode}, Polling: {Interval}s, Webhooks: {Webhooks}",
                meetingId, usedSso ? "SSO" : "Fallback", pollingInterval, webhooksEnabled ? "Enabled" : "Disabled");

            return Ok(new
            {
                meetingId = meetingId,
                meetingUrl = joinWebUrl,
                status = "monitoring",
                authenticationMode = usedSso ? "sso" : "fallback",
                pollingInterval = pollingInterval,
                initialTranscriptCount = initialSegmentsList.Count,
                webhooks = new
                {
                    enabled = webhooksEnabled,
                    subscriptionId = subscriptionId
                },
                message = usedSso
                    ? "Monitoring active with Teams SSO authentication"
                    : "Monitoring active with fallback authentication (debugging mode)",
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
                return NotFound(new { error = $"No session found for meeting {meetingId}" });
            }

            var isMonitoring = _activeMonitoringTasks.ContainsKey(meetingId);
            var processedSegmentCount = ProcessedSegmentIds.TryGetValue(meetingId, out var ids) ? ids.Count : 0;

            return Ok(new
            {
                session,
                isMonitoring,
                processedSegmentCount,
                monitoring = _activeMonitoringTasks.TryGetValue(meetingId, out var task) ? new
                {
                    startedAt = task.StartedAt,
                    lastUpdateAt = task.LastUpdateAt,
                    pollingIntervalSeconds = task.PollingIntervalSeconds,
                    isActive = !task.CancellationTokenSource.Token.IsCancellationRequested
                } : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get session for meeting {MeetingId}", meetingId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{meetingId}/debug")]
    public IActionResult GetDebugInfo(string meetingId)
    {
        try
        {
            var hasProcessedSegments = ProcessedSegmentIds.TryGetValue(meetingId, out var segmentIds);
            var hasMonitoringTask = _activeMonitoringTasks.TryGetValue(meetingId, out var task);

            return Ok(new
            {
                meetingId,
                timestamp = DateTimeOffset.UtcNow,
                sharedSegmentCache = new
                {
                    exists = hasProcessedSegments,
                    segmentCount = hasProcessedSegments ? segmentIds!.Count : 0,
                    recentSegments = hasProcessedSegments
                        ? segmentIds!.Take(10).ToList()
                        : new List<string>()
                },
                pollingStatus = new
                {
                    isActive = hasMonitoringTask && !task!.CancellationTokenSource.Token.IsCancellationRequested,
                    startedAt = hasMonitoringTask ? task!.StartedAt : (DateTimeOffset?)null,
                    lastUpdateAt = hasMonitoringTask ? task!.LastUpdateAt : (DateTimeOffset?)null,
                    intervalSeconds = hasMonitoringTask ? task!.PollingIntervalSeconds : (int?)null
                },
                webhookStatus = new
                {
                    // Note: Webhook uses same shared segment cache
                    message = "Webhooks share the same segment deduplication cache as polling"
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get debug info for meeting {MeetingId}", meetingId);
            return StatusCode(500, new { error = ex.Message });
        }
    }



    private async Task StartRealTimeTranscriptMonitoringAsync(string meetingId, int pollingIntervalSeconds)
    {
        // Stop existing monitoring task if any
        if (_activeMonitoringTasks.TryGetValue(meetingId, out var existingTask))
        {
            existingTask.CancellationTokenSource.Cancel();
            _activeMonitoringTasks.Remove(meetingId);
            _logger.LogInformation("[POLLING] Stopped existing monitoring task for meeting {MeetingId}", meetingId);
        }

        // Initialize deduplication set for this meeting (shared with webhook)
        ProcessedSegmentIds.TryAdd(meetingId, new HashSet<string>());
        _logger.LogInformation("[POLLING] Initialized shared segment deduplication for meeting {MeetingId}", meetingId);

        // New CTS for this monitoring run
        var cts = new CancellationTokenSource();
        var monitoringTask = new RealTimeMonitoringTask
        {
            CancellationTokenSource = cts,
            StartedAt = DateTimeOffset.UtcNow,
            LastUpdateAt = DateTimeOffset.UtcNow,
            PollingIntervalSeconds = pollingIntervalSeconds
        };

        _activeMonitoringTasks[meetingId] = monitoringTask;

        // Start background task for polling
        _ = Task.Run(async () =>
        {
            _logger.LogInformation("[POLLING] Started monitoring task for meeting {MeetingId} with {Interval}s interval",
                meetingId, pollingIntervalSeconds);

            var pollCount = 0;

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    pollCount++;
                    var executionId = Guid.NewGuid().ToString("N")[..8];

                    try
                    {
                        _logger.LogDebug("[POLLING-{ExecutionId}] Poll #{PollCount} starting for meeting {MeetingId}",
                            executionId, pollCount, meetingId);

                        // Get the latest session state
                        var session = await _sessionStore.GetAsync(meetingId);
                        if (session == null || session.Status != MeetingStatus.Active)
                        {
                            _logger.LogWarning("[POLLING-{ExecutionId}] Session not found or not active, stopping monitoring",
                                executionId);
                            break;
                        }

                        // 5s overlap to tolerate clock skew & late arrivals
                        var fetchSince = monitoringTask.LastUpdateAt.AddSeconds(-5);
                        _logger.LogDebug("[POLLING-{ExecutionId}] Fetching segments since {Since} (5s overlap)",
                            executionId, fetchSince);

                        // Fetch new transcript segments using delta query
                        var newSegments = await _transcriptService.GetNewTranscriptSegmentsAsync(
                            meetingId,
                            fetchSince,
                            cts.Token);

                        var segmentsList = newSegments.ToList();
                        _logger.LogDebug("[POLLING-{ExecutionId}] Retrieved {Count} total segments from transcript service",
                            executionId, segmentsList.Count);

                        if (!segmentsList.Any())
                        {
                            _logger.LogDebug("[POLLING-{ExecutionId}] No segments retrieved", executionId);
                            goto DelayAndContinue;
                        }

                        // Segment-level deduplication using SHARED cache with webhook
                        var processedIds = ProcessedSegmentIds[meetingId];
                        List<TranscriptSegment> unprocessedSegments;

                        lock (processedIds)
                        {
                            var beforeCount = processedIds.Count;
                            unprocessedSegments = segmentsList
                                .Where(s => processedIds.Add(s.Id))
                                .ToList();
                            var afterCount = processedIds.Count;

                            _logger.LogInformation(
                                "[POLLING-{ExecutionId}] Segment deduplication: {Total} retrieved, {New} new, {Duplicate} duplicates. Shared cache: {Before} → {After}",
                                executionId, segmentsList.Count, unprocessedSegments.Count,
                                segmentsList.Count - unprocessedSegments.Count, beforeCount, afterCount);
                        }

                        if (!unprocessedSegments.Any())
                        {
                            _logger.LogInformation(
                                "[POLLING-{ExecutionId}] ALL DUPLICATES - All {Count} segments already processed by polling/webhook",
                                executionId, segmentsList.Count);
                            goto DelayAndContinue;
                        }

                        // Determine interviewer identifier: from OBO token or first speaker's display name
                        var interviewerIdentifier = session.InterviewerDisplayName;
                        var firstSpeakerName = session.FirstSpeakerId; // stores the first speaker's display name

                        if (string.IsNullOrEmpty(interviewerIdentifier) &&
                            string.IsNullOrEmpty(firstSpeakerName) &&
                            unprocessedSegments.Any())
                        {
                            // Fallback: Use first speaker's name
                            firstSpeakerName = unprocessedSegments.First().SpeakerId;
                            _logger.LogWarning(
                                "[POLLING-{ExecutionId}] No interviewer from OBO token. Using first speaker as interviewer: {SpeakerName}",
                                executionId, firstSpeakerName);

                            // Update session with first speaker name
                            var updatedSession = session with { FirstSpeakerId = firstSpeakerName };
                            await _sessionStore.AddOrUpdateAsync(updatedSession);
                            session = updatedSession;
                        }

                        // Process segments with role assignment and state machine
                        var currentState = session.State;
                        var currentQAExchange = session.CurrentQAExchange;
                        var segmentsWithRoles = new List<TranscriptSegment>();

                        _logger.LogInformation("[POLLING-{ExecutionId}] Broadcasting {Count} NEW segments to SignalR clients",
                            executionId, unprocessedSegments.Count);
                        _logger.LogInformation(
                            "[POLLING-{ExecutionId}] Role assignment identifiers - Interviewer: '{Interviewer}', Fallback: '{Fallback}'",
                            executionId, interviewerIdentifier ?? "(none)", firstSpeakerName ?? "(none)");

                        foreach (var segment in unprocessedSegments)
                        {
                            // IMPORTANT: SpeakerId/Name are display names from VTT (e.g., "Efrain Goyzueta")
                            bool isInterviewer = false;

                            if (!string.IsNullOrEmpty(interviewerIdentifier))
                            {
                                isInterviewer =
                                    string.Equals(segment.SpeakerId, interviewerIdentifier, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(segment.SpeakerName, interviewerIdentifier, StringComparison.OrdinalIgnoreCase);
                            }
                            else if (!string.IsNullOrEmpty(firstSpeakerName))
                            {
                                isInterviewer =
                                    string.Equals(segment.SpeakerId, firstSpeakerName, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(segment.SpeakerName, firstSpeakerName, StringComparison.OrdinalIgnoreCase);
                            }

                            var updatedSegment = segment with
                            {
                                Role = isInterviewer ? SpeakerRole.Interviewer : SpeakerRole.Interviewee
                            };
                            segmentsWithRoles.Add(updatedSegment);

                            // Update conversation state & Q&A exchange
                            (currentState, currentQAExchange) = UpdateConversationStateWithQA(
                                currentState,
                                currentQAExchange,
                                updatedSegment.Role,
                                segment.Content);

                            _logger.LogDebug(
                                "[POLLING-{ExecutionId}] Segment from {Speaker} (Name: {SpeakerName}, ID: {SpeakerId}, Role: {Role}). State: {State}",
                                executionId, segment.SpeakerName, segment.SpeakerName, segment.SpeakerId,
                                updatedSegment.Role, currentState);

                            // Send to SignalR with role assigned
                            await _signalRService.SendTranscriptUpdateAsync(meetingId, updatedSegment);
                        }

                        // Decide if we should evaluate the candidate's response
                        bool shouldEvaluate =
                            currentState == ConversationState.CandidateResponding &&
                            currentQAExchange != null &&
                            currentQAExchange.CandidateResponses.Any();

                        // Timeout: if candidate hasn't spoken in 10s, evaluate accumulated responses
                        if (shouldEvaluate)
                        {
                            var timeSinceLastSegment = DateTimeOffset.UtcNow - unprocessedSegments.Last().Timestamp;
                            var evaluationTimeout = TimeSpan.FromSeconds(10);

                            if (timeSinceLastSegment >= evaluationTimeout)
                            {
                                _logger.LogInformation(
                                    "[POLLING-{ExecutionId}] ⏰ Candidate response timeout. Evaluating accumulated responses",
                                    executionId);
                                currentState = ConversationState.EvaluatingResponse;
                            }
                        }

                        // Evaluate response & optionally generate follow-ups (in isolated background scope)
                        if (currentState == ConversationState.EvaluatingResponse && currentQAExchange != null)
                        {
                            _logger.LogInformation(
                                "[POLLING-{ExecutionId}] 🔍 Triggering Q&A evaluation (non-blocking). Question: '{Question}', Responses: {Count}",
                                executionId,
                                currentQAExchange.InterviewerQuestion.Substring(0,
                                    Math.Min(50, currentQAExchange.InterviewerQuestion.Length)),
                                currentQAExchange.CandidateResponses.Count);

                            // Defensive copies (immutable/isolated)
                            var capturedExecutionId = executionId;
                            var capturedMeetingId = meetingId;
                            var capturedQAExchange = currentQAExchange;
                            var capturedSegments = segmentsWithRoles.TakeLast(10).ToList(); // recent context
                            var capturedLogger = _logger;

                            // Fire-and-forget in a new scope; DO NOT use Task.Factory.StartNew
                            _ = Task.Run(async () =>
                            {
                                using var scope = _serviceScopeFactory.CreateScope();
                                try
                                {
                                    var qaEvaluationService = scope.ServiceProvider.GetRequiredService<IQAEvaluationService>();
                                    var questionService = scope.ServiceProvider.GetRequiredService<IQuestionGenerationService>();
                                    var signalRService = scope.ServiceProvider.GetRequiredService<ISignalRService>();
                                    var sessionStore = scope.ServiceProvider.GetRequiredService<IMeetingSessionStore>();

                                    capturedLogger.LogDebug(
                                        "[POLLING-{ExecutionId}] Evaluating Q&A response quality in isolated scope",
                                        capturedExecutionId);

                                    var evaluation = await qaEvaluationService.EvaluateResponseAsync(
                                        capturedQAExchange.InterviewerQuestion,
                                        capturedQAExchange.CandidateResponses,
                                        "Technical Interview",
                                        CancellationToken.None);

                                    capturedLogger.LogInformation(
                                        "[POLLING-{ExecutionId}] Q&A Evaluation Complete: IsAnswered={IsAnswered}, Quality={Quality}, NeedsFollowUp={NeedsFollowUp}, Reasoning: {Reasoning}",
                                        capturedExecutionId, evaluation.IsAnswered, evaluation.Quality,
                                        evaluation.NeedsFollowUp, evaluation.Reasoning);

                                    if (evaluation.NeedsFollowUp)
                                    {
                                        // Re-fetch session to respect cooldown
                                        var latestSession = await sessionStore.GetAsync(capturedMeetingId);
                                        if (latestSession == null)
                                        {
                                            capturedLogger.LogWarning(
                                                "[POLLING-{ExecutionId}] Session not found during question generation",
                                                capturedExecutionId);
                                            return;
                                        }

                                        var timeSinceLastQuestion = latestSession.LastQuestionGeneratedAt.HasValue
                                            ? DateTimeOffset.UtcNow - latestSession.LastQuestionGeneratedAt.Value
                                            : TimeSpan.MaxValue;

                                        var questionCooldown = TimeSpan.FromSeconds(
                                            _configuration.GetValue<int>("TranscriptProcessing:QuestionGenerationCooldownSeconds", 30));

                                        if (timeSinceLastQuestion >= questionCooldown)
                                        {
                                            capturedLogger.LogInformation(
                                                "[POLLING-{ExecutionId}] Generating follow-up questions in background",
                                                capturedExecutionId);

                                            var questions = await questionService.GenerateQuestionsAsync(
                                                capturedSegments,
                                                latestSession.OrganizerEmail,
                                                CancellationToken.None);

                                            if (questions.Any())
                                            {
                                                await signalRService.SendQuestionSuggestionsAsync(capturedMeetingId, questions);
                                                capturedLogger.LogInformation(
                                                    "[POLLING-{ExecutionId}] Sent {Count} AI-generated follow-up questions",
                                                    capturedExecutionId, questions.Count);

                                                var updatedSession = latestSession with
                                                {
                                                    LastQuestionGeneratedAt = DateTimeOffset.UtcNow
                                                };
                                                await sessionStore.AddOrUpdateAsync(updatedSession);
                                            }
                                        }
                                        else
                                        {
                                            capturedLogger.LogDebug(
                                                "[POLLING-{ExecutionId}] ⏸Follow-up needed but in cooldown. Time since last: {TimeSince}s",
                                                capturedExecutionId, timeSinceLastQuestion.TotalSeconds);
                                        }
                                    }
                                    else
                                    {
                                        capturedLogger.LogInformation(
                                            "[POLLING-{ExecutionId}] No follow-up needed. Reason: {Reasoning}",
                                            capturedExecutionId, evaluation.Reasoning);
                                    }
                                }
                                catch (ObjectDisposedException ex)
                                {
                                    capturedLogger.LogError(
                                        ex,
                                        "[POLLING-{ExecutionId}] Scope disposed during Q&A evaluation (should not happen with new scope)",
                                        capturedExecutionId);
                                }
                                catch (Exception ex)
                                {
                                    capturedLogger.LogError(
                                        ex,
                                        "[POLLING-{ExecutionId}] Failed to evaluate Q&A in background",
                                        capturedExecutionId);
                                }
                            }, CancellationToken.None);

                            // Reset state immediately (do not wait on evaluation)
                            currentState = ConversationState.WaitingForInterviewerQuestion;
                            currentQAExchange = null;

                            _logger.LogDebug(
                                "[POLLING-{ExecutionId}] State reset to WaitingForInterviewerQuestion (evaluation running in background)",
                                executionId);
                        }

                        // Update session with latest state
                        var finalSession = session with
                        {
                            LastProcessedTime = unprocessedSegments.Max(s => s.Timestamp),
                            FirstSpeakerId = firstSpeakerName,
                            InterviewerUserId = session.InterviewerUserId,
                            State = currentState,
                            CurrentQAExchange = currentQAExchange
                        };
                        await _sessionStore.AddOrUpdateAsync(finalSession);

                        monitoringTask.LastUpdateAt = DateTimeOffset.UtcNow;
                        _logger.LogInformation("[POLLING-{ExecutionId}] Poll complete - processed {Count} new segments, State: {State}",
                            executionId, unprocessedSegments.Count, currentState);

                    DelayAndContinue:
                        _logger.LogDebug("[POLLING-{ExecutionId}] Waiting {Interval}s until next poll",
                            executionId, pollingIntervalSeconds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[POLLING] Error during poll #{PollCount} for meeting {MeetingId}",
                            pollCount, meetingId);
                    }

                    // Wait for next polling interval (cooperative cancellation)
                    await Task.Delay(TimeSpan.FromSeconds(pollingIntervalSeconds), cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[POLLING] Monitoring cancelled for meeting {MeetingId} after {PollCount} polls",
                    meetingId, pollCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[POLLING] Fatal error in monitoring for meeting {MeetingId}", meetingId);
            }
            finally
            {
                _activeMonitoringTasks.Remove(meetingId);
                _logger.LogInformation("[POLLING] Monitoring task terminated for meeting {MeetingId}", meetingId);
            }
        }, cts.Token);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Updates conversation state and Q&A exchange based on who spoke and what they said
    /// </summary>
    private (ConversationState newState, QAExchange? qaExchange) UpdateConversationStateWithQA(
        ConversationState currentState,
        QAExchange? currentQAExchange,
        SpeakerRole speakerRole,
        string content)
    {
        return (currentState, speakerRole) switch
        {
            // Interviewer asks a question
            (ConversationState.WaitingForInterviewerQuestion, SpeakerRole.Interviewer) =>
                (ConversationState.InterviewerAsked,
                 new QAExchange(content, new List<string>(), DateTimeOffset.UtcNow)),

            // Candidate starts responding
            (ConversationState.InterviewerAsked, SpeakerRole.Interviewee) =>
                (ConversationState.CandidateResponding,
                 currentQAExchange?.AddResponse(content) ?? currentQAExchange),

            // Candidate continues responding (accumulate)
            (ConversationState.CandidateResponding, SpeakerRole.Interviewee) =>
                (ConversationState.CandidateResponding,
                 currentQAExchange?.AddResponse(content) ?? currentQAExchange),

            // Interviewer interrupts or asks follow-up before evaluation
            (ConversationState.CandidateResponding, SpeakerRole.Interviewer) =>
                (ConversationState.InterviewerAsked,
                 new QAExchange(content, new List<string>(), DateTimeOffset.UtcNow)),

            // Default: no state change
            _ => (currentState, currentQAExchange)
        };
    }
}

// Request/Response Models
public class StartMonitoringRequest
{
    public required string MeetingId { get; set; }
    public string? IdToken { get; set; }
    public bool UseWebhooks { get; set; } = true;
    public int? PollingIntervalSeconds { get; set; }
}

public class StopMonitoringRequest
{
    public required string MeetingId { get; set; }
}

// Helper class for tracking monitoring tasks
public class RealTimeMonitoringTask
{
    public required CancellationTokenSource CancellationTokenSource { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastUpdateAt { get; set; }
    public int PollingIntervalSeconds { get; set; }
}