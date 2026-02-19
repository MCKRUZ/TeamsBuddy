using Azure;
using Azure.AI.OpenAI.Assistants;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// Azure OpenAI Assistants implementation of IMeetingDocumentService (v1 Assistants API).
/// Uploads documents, attaches them directly to a per-meeting assistant using RetrievalToolDefinition,
/// and creates a persistent thread for question generation.
/// Requires "AzureOpenAI:AssistantsEnabled": true in configuration.
/// </summary>
public class AssistantsDocumentService : IMeetingDocumentService
{
    private readonly AssistantsClient _client;
    private readonly string _deploymentName;
    private readonly bool _cleanupOnStop;
    private readonly IOrgKnowledgeService _orgKnowledgeService;
    private readonly ILogger<AssistantsDocumentService> _logger;

    public AssistantsDocumentService(
        IConfiguration configuration,
        IOrgKnowledgeService orgKnowledgeService,
        ILogger<AssistantsDocumentService> logger)
    {
        _orgKnowledgeService = orgKnowledgeService;
        _logger = logger;

        var endpoint = configuration["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured");
        var apiKey = configuration["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is not configured");

        _deploymentName = configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4";
        _cleanupOnStop = bool.TryParse(configuration["AzureOpenAI:AssistantsCleanupOnStop"], out var cleanup)
            ? cleanup : true;

        _client = new AssistantsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    }

    public async Task<MeetingSession> InitialiseAssistantAsync(
        MeetingSession session,
        IReadOnlyList<DocumentUpload> documents,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Initialising Assistants for meeting {MeetingId} with {DocCount} document(s)",
            session.MeetingId, documents.Count);

        // Upload files, collecting their IDs to attach to the assistant
        var fileIds = new List<string>();
        foreach (var doc in documents)
        {
            using var stream = new MemoryStream(doc.Content);
            var uploadResponse = await _client.UploadFileAsync(stream, OpenAIFilePurpose.Assistants, doc.FileName, ct);
            fileIds.Add(uploadResponse.Value.Id);
            _logger.LogInformation("Uploaded file '{FileName}' as Assistants file {FileId}", doc.FileName, uploadResponse.Value.Id);

            if (doc.IndexInOrgKnowledge)
            {
                await _orgKnowledgeService.IndexDocumentAsync(doc, ct);
                _logger.LogInformation("Indexed '{FileName}' into org knowledge base", doc.FileName);
            }
        }

        // Create a per-meeting assistant with files attached via the Retrieval tool (v1 API)
        var assistantOptions = new AssistantCreationOptions(_deploymentName)
        {
            Name = $"MeetingAssistant-{session.MeetingId}",
            Instructions =
                "You are an expert meeting facilitator. Given a transcript of a live meeting " +
                "and relevant organisational documents, generate insightful questions to deepen " +
                "understanding, clarify ambiguities, and drive the discussion forward.",
            Tools = { new RetrievalToolDefinition() }
        };
        foreach (var id in fileIds)
            assistantOptions.FileIds.Add(id);

        var assistantResponse = await _client.CreateAssistantAsync(assistantOptions, ct);
        var assistantId = assistantResponse.Value.Id;

        // Create a persistent thread for this meeting's conversation
        var threadResponse = await _client.CreateThreadAsync(new AssistantThreadCreationOptions(), ct);
        var threadId = threadResponse.Value.Id;

        _logger.LogInformation(
            "Created assistant {AssistantId} and thread {ThreadId} for meeting {MeetingId}",
            assistantId, threadId, session.MeetingId);

        return session with
        {
            AssistantId = assistantId,
            ThreadId = threadId,
            UploadedFileIds = fileIds.AsReadOnly()
        };
    }

    public async Task<IReadOnlyList<string>> AddDocumentsAsync(
        string assistantId,
        IReadOnlyList<DocumentUpload> documents,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Adding {Count} document(s) to assistant {AssistantId}",
            documents.Count, assistantId);

        var newFileIds = new List<string>();
        foreach (var doc in documents)
        {
            using var stream = new MemoryStream(doc.Content);
            var uploadResponse = await _client.UploadFileAsync(stream, OpenAIFilePurpose.Assistants, doc.FileName, ct);
            newFileIds.Add(uploadResponse.Value.Id);

            if (doc.IndexInOrgKnowledge)
                await _orgKnowledgeService.IndexDocumentAsync(doc, ct);
        }

        // Fetch the assistant's current file IDs, then update with the new ones appended
        var currentAssistant = (await _client.GetAssistantAsync(assistantId, ct)).Value;
        var allFileIds = currentAssistant.FileIds.Concat(newFileIds).ToList();

        var updateOptions = new UpdateAssistantOptions();
        foreach (var id in allFileIds)
            updateOptions.FileIds.Add(id);

        await _client.UpdateAssistantAsync(assistantId, updateOptions, ct);

        return newFileIds.AsReadOnly();
    }

    public async Task CleanupAsync(MeetingSession session, CancellationToken ct)
    {
        if (!_cleanupOnStop)
        {
            _logger.LogDebug("Assistants cleanup skipped (AssistantsCleanupOnStop=false) for meeting {MeetingId}", session.MeetingId);
            return;
        }

        _logger.LogInformation("Cleaning up Assistants resources for meeting {MeetingId}", session.MeetingId);

        if (!string.IsNullOrEmpty(session.ThreadId))
        {
            try { await _client.DeleteThreadAsync(session.ThreadId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete thread {ThreadId}", session.ThreadId); }
        }

        if (!string.IsNullOrEmpty(session.AssistantId))
        {
            try { await _client.DeleteAssistantAsync(session.AssistantId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete assistant {AssistantId}", session.AssistantId); }
        }

        foreach (var fileId in session.UploadedFileIds)
        {
            try { await _client.DeleteFileAsync(fileId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete file {FileId}", fileId); }
        }
    }
}
