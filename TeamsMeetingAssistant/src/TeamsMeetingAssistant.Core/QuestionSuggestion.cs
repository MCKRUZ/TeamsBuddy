namespace TeamsMeetingAssistant.Core;

public record QuestionSuggestion(
    string Id,
    string Question,
    string Rationale, // Was "Context" in old version
    string Category, // "Clarification", "Deep-dive", "Follow-up", "Summary"
    float ConfidenceScore, // Was "Confidence" in old version
    DateTimeOffset GeneratedAt
);