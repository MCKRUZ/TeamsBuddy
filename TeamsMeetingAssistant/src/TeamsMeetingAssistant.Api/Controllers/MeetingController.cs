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
    private readonly IMeetingDocumentService _documentService;
    private readonly ILogger<MeetingController> _logger;
    private readonly SubscriptionRenewalService _renewalService;

    public MeetingController(
        IMeetingSessionStore sessionStore,
        ITranscriptService transcriptService,
        IMeetingDocumentService documentService,
        ILogger<MeetingController> logger,
        IHostedService renewalService)
    {
        _sessionStore = sessionStore;
        _transcriptService = transcriptService;
        _documentService = documentService;
        _logger = logger;
        _renewalService = (SubscriptionRenewalService)renewalService;
    }

    /// <summary>
    /// Start monitoring a meeting. Accepts an optional set of documents to load into the
    /// Assistants vector store when AssistantsEnabled=true.
    /// </summary>
    [HttpPost("start")]
    [Consumes("multipart/form-data", "application/json")]
    public async Task<IActionResult> StartMonitoring(
        [FromForm] string meetingId,
        [FromForm] bool useWebhooks = false,
        [FromForm] bool useAssistants = false,
        [FromForm] bool indexInOrgKnowledge = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting monitoring for meeting {MeetingId}", meetingId);

            var meetingInfo = await _transcriptService.GetMeetingInfoAsync(meetingId, cancellationToken);

            if (!meetingInfo.IsTranscriptionEnabled)
            {
                return BadRequest(new
                {
                    error = "Meeting transcription is not enabled. Please enable it in Teams settings."
                });
            }

            var session = new MeetingSession(
                meetingInfo.MeetingId,
                meetingInfo.OrganizerEmail,
                DateTimeOffset.UtcNow,
                null,
                true,
                null,
                DateTimeOffset.UtcNow,
                MeetingStatus.Active);

            // Initialise Assistants and upload any provided documents
            if (useAssistants)
            {
                var documents = ReadFormFiles(Request.Form.Files, indexInOrgKnowledge);
                session = await _documentService.InitialiseAssistantAsync(session, documents, cancellationToken);
            }

            await _sessionStore.AddOrUpdateAsync(session);

            if (useWebhooks)
            {
                var webhookUrl = $"{Request.Scheme}://{Request.Host}/api/webhook/transcript";
                var subscription = await _transcriptService.SubscribeToTranscriptChangesAsync(
                    meetingId, webhookUrl, cancellationToken);

                _renewalService.TrackSubscription(
                    meetingId,
                    subscription.Id,
                    subscription.ExpirationDateTime ?? DateTimeOffset.UtcNow.AddHours(1));
            }

            _logger.LogInformation("Started monitoring meeting {MeetingId} (assistants={UseAssistants})", meetingId, useAssistants);

            return Ok(new
            {
                meetingId,
                status = "monitoring",
                assistantId = session.AssistantId,
                threadId = session.ThreadId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start monitoring meeting {MeetingId}", meetingId);
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
                return NotFound(new { error = "Meeting session not found" });

            // Clean up Assistants resources before marking complete
            await _documentService.CleanupAsync(session, cancellationToken);

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
                return NotFound(new { error = "Meeting session not found" });

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

    private static IReadOnlyList<DocumentUpload> ReadFormFiles(IFormFileCollection files, bool indexInOrgKnowledge)
    {
        if (files.Count == 0)
            return Array.Empty<DocumentUpload>();

        var uploads = new List<DocumentUpload>(files.Count);
        foreach (var file in files)
        {
            using var ms = new MemoryStream();
            file.CopyTo(ms);
            uploads.Add(new DocumentUpload(file.FileName, file.ContentType, ms.ToArray(), indexInOrgKnowledge));
        }
        return uploads;
    }
}

public record StopMonitoringRequest(string MeetingId);
