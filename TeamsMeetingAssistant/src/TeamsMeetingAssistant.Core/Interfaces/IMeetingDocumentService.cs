namespace TeamsMeetingAssistant.Core.Interfaces;

public interface IMeetingDocumentService
{
    /// <summary>
    /// Called at session start. Uploads initial documents, creates an Assistants vector store and
    /// thread, then returns the updated session with AssistantId, ThreadId, and UploadedFileIds set.
    /// </summary>
    Task<MeetingSession> InitialiseAssistantAsync(
        MeetingSession session,
        IReadOnlyList<DocumentUpload> documents,
        CancellationToken ct);

    /// <summary>
    /// Called by the document upload endpoint for documents added after session start.
    /// Returns the list of file IDs that were successfully uploaded.
    /// </summary>
    Task<IReadOnlyList<string>> AddDocumentsAsync(
        string assistantId,
        IReadOnlyList<DocumentUpload> documents,
        CancellationToken ct);

    /// <summary>
    /// Called at session stop. Deletes Assistants resources (thread, vector store, files).
    /// </summary>
    Task CleanupAsync(MeetingSession session, CancellationToken ct);
}
