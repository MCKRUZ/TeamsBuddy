using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// Azure AI Search implementation of IOrgKnowledgeService.
/// Provides semantic search over an org-wide knowledge base and document indexing.
/// Requires "AzureAISearch:Endpoint" and "AzureAISearch:ApiKey" in configuration.
/// </summary>
public class AzureSearchOrgKnowledgeService : IOrgKnowledgeService
{
    private readonly SearchClient _searchClient;
    private readonly ILogger<AzureSearchOrgKnowledgeService> _logger;
    private readonly string _semanticConfigName;

    public AzureSearchOrgKnowledgeService(
        IConfiguration configuration,
        ILogger<AzureSearchOrgKnowledgeService> logger)
    {
        _logger = logger;

        var endpoint = configuration["AzureAISearch:Endpoint"]!;
        var apiKey = configuration["AzureAISearch:ApiKey"]!;
        var indexName = configuration["AzureAISearch:IndexName"] ?? "org-knowledge";
        _semanticConfigName = configuration["AzureAISearch:SemanticConfigName"] ?? "org-knowledge-semantic";

        _searchClient = new SearchClient(
            new Uri(endpoint),
            indexName,
            new AzureKeyCredential(apiKey));
    }

    public async Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int topK = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<KnowledgeChunk>();

        try
        {
            var options = new SearchOptions
            {
                Size = topK,
                Select = { "id", "content", "sourceDocument" },
                QueryType = SearchQueryType.Semantic,
                SemanticSearch = new SemanticSearchOptions
                {
                    SemanticConfigurationName = _semanticConfigName,
                    QueryCaption = new QueryCaption(QueryCaptionType.Extractive),
                    QueryAnswer = new QueryAnswer(QueryAnswerType.Extractive)
                }
            };

            var response = await _searchClient.SearchAsync<SearchDocument>(query, options, ct);

            var chunks = new List<KnowledgeChunk>();
            await foreach (var result in response.Value.GetResultsAsync().WithCancellation(ct))
            {
                var id = result.Document.TryGetValue("id", out var idObj) ? idObj?.ToString() ?? "" : "";
                var content = result.Document.TryGetValue("content", out var contentObj) ? contentObj?.ToString() ?? "" : "";
                var source = result.Document.TryGetValue("sourceDocument", out var sourceObj) ? sourceObj?.ToString() ?? "" : "";

                chunks.Add(new KnowledgeChunk(id, content, source, result.Score ?? 0));
            }

            _logger.LogDebug("Org knowledge search for '{Query}' returned {Count} chunks", query, chunks.Count);
            return chunks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search org knowledge base for query: {Query}", query);
            return Array.Empty<KnowledgeChunk>();
        }
    }

    public async Task IndexDocumentAsync(DocumentUpload document, CancellationToken ct = default)
    {
        try
        {
            var chunks = ChunkDocument(document);

            var searchDocs = chunks.Select(c => new SearchDocument
            {
                ["id"] = c.Id,
                ["content"] = c.Content,
                ["sourceDocument"] = document.FileName
            }).ToList();

            await _searchClient.UploadDocumentsAsync(searchDocs, new IndexDocumentsOptions(), ct);

            _logger.LogInformation(
                "Indexed {ChunkCount} chunks from document '{FileName}' into org knowledge base",
                chunks.Count, document.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index document '{FileName}' into org knowledge base", document.FileName);
            throw;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await _searchClient.SearchAsync<SearchDocument>("*", new SearchOptions { Size = 1 }, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<KnowledgeChunk> ChunkDocument(DocumentUpload document)
    {
        // Convert bytes to text (UTF-8 assumed for text-based documents)
        var text = Encoding.UTF8.GetString(document.Content);

        // Simple fixed-size chunking with overlap
        const int chunkSize = 500;
        const int overlap = 50;
        var chunks = new List<KnowledgeChunk>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var i = 0;
        var chunkIndex = 0;
        while (i < words.Length)
        {
            var end = Math.Min(i + chunkSize, words.Length);
            var chunk = string.Join(" ", words, i, end - i);
            var id = $"{document.FileName}_{chunkIndex}_{Guid.NewGuid():N}";

            chunks.Add(new KnowledgeChunk(id, chunk, document.FileName, 0));

            i += chunkSize - overlap;
            chunkIndex++;
        }

        return chunks;
    }
}
