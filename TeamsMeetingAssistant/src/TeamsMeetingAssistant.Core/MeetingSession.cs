namespace TeamsMeetingAssistant.Core;

public record MeetingSession(
    string MeetingId,
    string OrganizerEmail,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    bool IsTranscriptionEnabled,
    string? ActiveTranscriptId,
    DateTimeOffset LastProcessedTime,
    MeetingStatus Status
);

public enum MeetingStatus
{
    Pending,
    Active,
    Completed,
    Error
}