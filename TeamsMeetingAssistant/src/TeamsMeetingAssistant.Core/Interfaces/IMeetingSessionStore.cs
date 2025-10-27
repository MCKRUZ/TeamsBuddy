namespace TeamsMeetingAssistant.Core.Interfaces;

public interface IMeetingSessionStore
{
    Task<MeetingSession?> GetAsync(string meetingId);
    Task AddOrUpdateAsync(MeetingSession session);
    Task<IEnumerable<MeetingSession>> GetActiveSessionsAsync();
    Task RemoveAsync(string meetingId);
}