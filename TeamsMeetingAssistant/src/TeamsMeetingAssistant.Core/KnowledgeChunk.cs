namespace TeamsMeetingAssistant.Core;

public record KnowledgeChunk(
    string Id,
    string Content,
    string SourceDocument,
    double RelevanceScore
);
