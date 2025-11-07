namespace TeamsMeetingAssistant.Core;

public record MeetingInfo(
    string MeetingId,
    string OrganizerEmail,
    DateTimeOffset StartDateTime,
    DateTimeOffset? EndDateTime,
    bool IsTranscriptionEnabled
);