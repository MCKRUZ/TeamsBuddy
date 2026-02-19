using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// No-op implementation used when Azure OpenAI Assistants is disabled.
/// </summary>
public class NullMeetingDocumentService : IMeetingDocumentService
{
    public Task<MeetingSession> InitialiseAssistantAsync(
        MeetingSession session,
        IReadOnlyList<DocumentUpload> documents,
        CancellationToken ct)
        => Task.FromResult(session);

    public Task<IReadOnlyList<string>> AddDocumentsAsync(
        string assistantId,
        IReadOnlyList<DocumentUpload> documents,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task CleanupAsync(MeetingSession session, CancellationToken ct)
        => Task.CompletedTask;
}
