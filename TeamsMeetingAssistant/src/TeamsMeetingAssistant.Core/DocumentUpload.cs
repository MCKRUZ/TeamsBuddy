namespace TeamsMeetingAssistant.Core;

public record DocumentUpload(
    string FileName,
    string ContentType,
    byte[] Content,
    bool IndexInOrgKnowledge
);
