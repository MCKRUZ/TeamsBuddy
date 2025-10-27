using Microsoft.AspNetCore.Mvc;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Api.Controllers;

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
            _logger.LogInformation("Starting monitoring for meeting {MeetingId}", request.MeetingId);

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
                    subscription.ExpirationDateTime ?? DateTimeOffset.UtcNow.AddHours(1));
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
            _logger.LogInformation("Stopping monitoring for meeting {MeetingId}", request.MeetingId);

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
        try
        {
            var sessions = await _sessionStore.GetActiveSessionsAsync();
            return Ok(sessions);
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

            return Ok(session);
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
}

public record StartMonitoringRequest(string MeetingId, bool UseWebhooks = false);
public record StopMonitoringRequest(string MeetingId);