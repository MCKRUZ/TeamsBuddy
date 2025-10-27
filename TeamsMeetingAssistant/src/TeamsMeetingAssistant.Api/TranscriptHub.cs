using Microsoft.AspNetCore.SignalR;

namespace TeamsMeetingAssistant.Api;

public class TranscriptHub : Hub
{
    private readonly ILogger<TranscriptHub> _logger;

    public TranscriptHub(ILogger<TranscriptHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinMeeting(string meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            _logger.LogWarning("Client attempted to join meeting with empty ID");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, meetingId);
        _logger.LogInformation("Client {ConnectionId} joined meeting {MeetingId}",
            Context.ConnectionId, meetingId);
    }

    public async Task LeaveMeeting(string meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            _logger.LogWarning("Client attempted to leave meeting with empty ID");
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, meetingId);
        _logger.LogInformation("Client {ConnectionId} left meeting {MeetingId}",
            Context.ConnectionId, meetingId);
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client {ConnectionId} connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception != null)
        {
            _logger.LogError(exception, "Client {ConnectionId} disconnected with error",
                Context.ConnectionId);
        }
        else
        {
            _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}