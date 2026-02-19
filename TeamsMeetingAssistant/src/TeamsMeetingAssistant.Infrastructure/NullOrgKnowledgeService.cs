using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// No-op implementation used when Azure AI Search is not configured.
/// </summary>
public class NullOrgKnowledgeService : IOrgKnowledgeService
{
    public Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int topK = 5,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<KnowledgeChunk>>(Array.Empty<KnowledgeChunk>());

    public Task IndexDocumentAsync(DocumentUpload document, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(false);
}
