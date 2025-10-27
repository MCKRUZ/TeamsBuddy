using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace TeamsMeetingAssistant.Infrastructure;

public class InMemoryMeetingSessionStore : IMeetingSessionStore
{
    private readonly ConcurrentDictionary<string, MeetingSession> _sessions = new();
    private readonly ILogger<InMemoryMeetingSessionStore> _logger;

    public InMemoryMeetingSessionStore(ILogger<InMemoryMeetingSessionStore> logger)
    {
        _logger = logger;
    }

    public Task<MeetingSession?> GetAsync(string meetingId)
    {
        _logger.LogDebug("Getting session for meeting {MeetingId}", meetingId);

        _sessions.TryGetValue(meetingId, out var session);
        return Task.FromResult(session);
    }

    public Task AddOrUpdateAsync(MeetingSession session)
    {
        _logger.LogDebug("Adding/updating session for meeting {MeetingId}, status: {Status}",
            session.MeetingId, session.Status);

        _sessions.AddOrUpdate(session.MeetingId, session, (key, oldValue) => session);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<MeetingSession>> GetActiveSessionsAsync()
    {
        _logger.LogDebug("Getting all active sessions");

        var activeSessions = _sessions.Values
            .Where(s => s.Status == MeetingStatus.Active)
            .ToList();

        _logger.LogDebug("Found {Count} active sessions", activeSessions.Count);

        return Task.FromResult<IEnumerable<MeetingSession>>(activeSessions);
    }

    public Task RemoveAsync(string meetingId)
    {
        _logger.LogDebug("Removing session for meeting {MeetingId}", meetingId);

        _sessions.TryRemove(meetingId, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Get all sessions (for debugging/monitoring purposes)
    /// </summary>
    public Task<IEnumerable<MeetingSession>> GetAllSessionsAsync()
    {
        return Task.FromResult<IEnumerable<MeetingSession>>(_sessions.Values.ToList());
    }

    /// <summary>
    /// Clear all sessions (for testing purposes)
    /// </summary>
    public Task ClearAllSessionsAsync()
    {
        _logger.LogInformation("Clearing all meeting sessions");
        _sessions.Clear();
        return Task.CompletedTask;
    }
}