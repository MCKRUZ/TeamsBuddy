namespace TeamsMeetingAssistant.Core;

public record MeetingSession(
    string MeetingId,
    string OrganizerEmail,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    bool IsTranscriptionEnabled,
    string? ActiveTranscriptId,
    DateTimeOffset LastProcessedTime,
    MeetingStatus Status,
    string? AssistantId,
    string? ThreadId,
    IReadOnlyList<string> UploadedFileIds
)
{
    // Backward-compatible constructor — existing 8-arg call sites continue to compile
    public MeetingSession(
        string meetingId,
        string organizerEmail,
        DateTimeOffset startTime,
        DateTimeOffset? endTime,
        bool isTranscriptionEnabled,
        string? activeTranscriptId,
        DateTimeOffset lastProcessedTime,
        MeetingStatus status)
        : this(meetingId, organizerEmail, startTime, endTime,
               isTranscriptionEnabled, activeTranscriptId,
               lastProcessedTime, status,
               null, null, Array.Empty<string>())
    {
    }
}

public enum MeetingStatus
{
    Pending,
    Active,
    Completed,
    Error
}
