using Microsoft.AspNetCore.SignalR;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Api;

public class SignalRHubService : ISignalRService
{
    private readonly IHubContext<TranscriptHub> _hubContext;
    private readonly ILogger<SignalRHubService> _logger;

    public SignalRHubService(
        IHubContext<TranscriptHub> hubContext,
        ILogger<SignalRHubService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task SendTranscriptUpdateAsync(string meetingId, TranscriptSegment segment)
    {
        try
        {
            _logger.LogDebug("Sending transcript update for meeting {MeetingId}", meetingId);

            await _hubContext.Clients.Group(meetingId)
                .SendAsync("NewTranscript", segment);

            _logger.LogDebug("Transcript update sent for meeting {MeetingId}, speaker: {Speaker}",
                meetingId, segment.SpeakerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending transcript update for meeting {MeetingId}", meetingId);
            throw;
        }
    }

    public async Task SendQuestionSuggestionsAsync(string meetingId, List<QuestionSuggestion> suggestions)
    {
        try
        {
            _logger.LogDebug("Sending {Count} question suggestions for meeting {MeetingId}",
                suggestions.Count, meetingId);

            await _hubContext.Clients.Group(meetingId)
                .SendAsync("QuestionSuggestions", suggestions);

            _logger.LogInformation("Sent {Count} question suggestions for meeting {MeetingId}",
                suggestions.Count, meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending question suggestions for meeting {MeetingId}", meetingId);
            throw;
        }
    }

    public async Task NotifyMeetingStatusAsync(string meetingId, string status)
    {
        try
        {
            _logger.LogDebug("Sending meeting status notification for {MeetingId}: {Status}",
                meetingId, status);

            await _hubContext.Clients.Group(meetingId)
                .SendAsync("MeetingStatus", status);

            _logger.LogInformation("Meeting status notification sent for {MeetingId}: {Status}",
                meetingId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending meeting status notification for meeting {MeetingId}", meetingId);
            throw;
        }
    }
}