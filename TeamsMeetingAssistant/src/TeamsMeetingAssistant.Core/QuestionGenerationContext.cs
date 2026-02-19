namespace TeamsMeetingAssistant.Core;

public record QuestionGenerationContext(
    string MeetingId,
    string OrganizerEmail,
    string? AssistantId,
    string? ThreadId,
    IReadOnlyList<KnowledgeChunk> OrgKnowledgeChunks
);
