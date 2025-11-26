namespace TeamsMeetingAssistant.Core.Interfaces;

/// <summary>
/// Service that evaluates whether a candidate's response adequately answers an interviewer's question
/// </summary>
public interface IQAEvaluationService
{
    /// <summary>
    /// Analyzes a Q&A exchange to determine if the response adequately addresses the question
    /// </summary>
    /// <param name="interviewerQuestion">The question asked by the interviewer</param>
    /// <param name="candidateResponses">The candidate's response(s) - can be multiple transcript segments</param>
    /// <param name="meetingContext">Context about the meeting (e.g., "Technical C# Interview")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Evaluation result with reasoning</returns>
    Task<QAEvaluationResult> EvaluateResponseAsync(
        string interviewerQuestion,
        List<string> candidateResponses,
        string meetingContext,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of evaluating a candidate's response to an interview question
/// </summary>
public record QAEvaluationResult(
    bool IsAnswered,                    // Did the candidate attempt to answer?
    bool IsAdequate,                    // Is the answer adequate/complete?
    bool NeedsFollowUp,                 // Should we generate follow-up questions?
    string Reasoning,                    // AI's reasoning for the evaluation
    ResponseQuality Quality,            // Quality assessment
    List<string> IdentifiedGaps         // Gaps or areas not covered
);

/// <summary>
/// Quality assessment of the candidate's response
/// </summary>
public enum ResponseQuality
{
    NoResponse,         // Candidate didn't respond or changed topic
    Superficial,        // Answered but very shallow
    Partial,            // Answered partially, missing key points
    Adequate,           // Good answer, covered main points
    Comprehensive       // Excellent, thorough answer
}
