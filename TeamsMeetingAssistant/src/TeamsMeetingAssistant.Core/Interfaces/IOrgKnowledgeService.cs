namespace TeamsMeetingAssistant.Core.Interfaces;

public interface IOrgKnowledgeService
{
    /// <summary>
    /// Semantically searches the org-wide knowledge base and returns the top-K chunks.
    /// Returns an empty list when the service is unavailable or unconfigured.
    /// </summary>
    Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int topK = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Chunks, embeds, and indexes a document into the org knowledge base.
    /// </summary>
    Task IndexDocumentAsync(DocumentUpload document, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the backing Azure AI Search index is reachable and configured.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
