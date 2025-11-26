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
    string? FirstSpeakerId = null, // First speaker's display name (fallback for interviewer when no OBO token)
    string? InterviewerUserId = null, // Authenticated user ID (OID) from OBO token (interviewer)
    string? InterviewerDisplayName = null, // Authenticated user display name from OBO token (used for role comparison)
    ConversationState State = ConversationState.WaitingForInterviewerQuestion,
    DateTimeOffset? LastQuestionGeneratedAt = null,
    QAExchange? CurrentQAExchange = null // Track the current Q&A being accumulated
);

public enum MeetingStatus
{
    Pending,
    Active,
    Completed,
    Error
}

/// <summary>
/// Tracks the conversation state machine for Q&A pattern detection
/// </summary>
public enum ConversationState
{
    WaitingForInterviewerQuestion,  // Initial state or after generating questions
    InterviewerAsked,                // Interviewer just asked a question
    CandidateResponding,             // Candidate is responding (accumulating responses)
    EvaluatingResponse               // Evaluating if response is adequate
}

/// <summary>
/// Represents a question-answer exchange during the interview
/// </summary>
public record QAExchange(
    string InterviewerQuestion,
    List<string> CandidateResponses,
    DateTimeOffset QuestionAskedAt
)
{
    // Helper to add a response
    public QAExchange AddResponse(string response)
    {
        var newResponses = new List<string>(CandidateResponses) { response };
        return this with { CandidateResponses = newResponses };
    }
}