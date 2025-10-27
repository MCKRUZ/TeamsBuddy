namespace TeamsMeetingAssistant.Core;

public record QuestionSuggestion(
    string Id,
    string Question,
    string Rationale,
    string Category, // "Clarification", "Deep-dive", "Follow-up", "Summary"
    float ConfidenceScore,
    DateTimeOffset GeneratedAt
);