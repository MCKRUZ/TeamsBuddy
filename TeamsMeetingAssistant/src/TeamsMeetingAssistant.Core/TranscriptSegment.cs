namespace TeamsMeetingAssistant.Core;

public record TranscriptSegment(
    string Id,
    string SpeakerName,
    string SpeakerId,
    string Content,
    DateTimeOffset Timestamp,
    TimeSpan StartTime,
    TimeSpan EndTime
);