namespace TeamsMeetingAssistant.Core;

public enum SpeakerRole
{
    Interviewer,
    Interviewee
}

public record TranscriptSegment(
    string Id,
    string SpeakerName,
    string SpeakerId,
    string Content,
    DateTimeOffset Timestamp,
    TimeSpan StartTime,
    TimeSpan EndTime,
    SpeakerRole Role = SpeakerRole.Interviewee
);