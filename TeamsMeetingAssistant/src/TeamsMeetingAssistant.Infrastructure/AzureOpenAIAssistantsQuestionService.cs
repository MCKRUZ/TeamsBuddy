using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI.Assistants;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// Azure OpenAI Assistants implementation of IQuestionGenerationService (v1 Assistants API).
/// Sends new transcript segments to an existing Assistants thread and runs the assistant to
/// produce contextually-aware question suggestions that reference uploaded meeting documents.
/// Requires "AzureOpenAI:AssistantsEnabled": true in configuration.
/// </summary>
public class AzureOpenAIAssistantsQuestionService : IQuestionGenerationService
{
    private readonly AssistantsClient _client;
    private readonly ILogger<AzureOpenAIAssistantsQuestionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AzureOpenAIAssistantsQuestionService(
        IConfiguration configuration,
        ILogger<AzureOpenAIAssistantsQuestionService> logger)
    {
        _logger = logger;

        var endpoint = configuration["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured");
        var apiKey = configuration["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is not configured");

        _client = new AssistantsClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    }

    public async Task<List<QuestionSuggestion>> GenerateQuestionsAsync(
        List<TranscriptSegment> recentTranscript,
        QuestionGenerationContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.AssistantId) || string.IsNullOrEmpty(context.ThreadId))
        {
            _logger.LogWarning(
                "No Assistants thread for meeting {MeetingId}. Skipping question generation.",
                context.MeetingId);
            return new List<QuestionSuggestion>();
        }

        try
        {
            var prompt = BuildPrompt(recentTranscript, context);

            // v1 API: CreateMessageAsync(threadId, role, content, fileIds, metadata, ct)
            await _client.CreateMessageAsync(
                context.ThreadId,
                MessageRole.User,
                prompt,
                fileIds: null,
                metadata: null,
                cancellationToken);

            var runResponse = await _client.CreateRunAsync(
                context.ThreadId,
                new CreateRunOptions(context.AssistantId),
                cancellationToken);

            var run = await PollRunToCompletionAsync(context.ThreadId, runResponse.Value.Id, cancellationToken);

            if (run.Status != RunStatus.Completed)
            {
                _logger.LogWarning(
                    "Assistants run {RunId} ended with status {Status} for meeting {MeetingId}",
                    run.Id, run.Status, context.MeetingId);
                return new List<QuestionSuggestion>();
            }

            return await ExtractQuestionsAsync(context.ThreadId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate questions via Assistants for meeting {MeetingId}", context.MeetingId);
            return new List<QuestionSuggestion>();
        }
    }

    private static string BuildPrompt(List<TranscriptSegment> segments, QuestionGenerationContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Recent Transcript");
        foreach (var s in segments)
            sb.AppendLine($"[{s.Timestamp:HH:mm:ss}] {s.SpeakerName}: {s.Content}");

        if (context.OrgKnowledgeChunks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Relevant Organisational Context");
            foreach (var chunk in context.OrgKnowledgeChunks)
                sb.AppendLine($"[{chunk.SourceDocument}] {chunk.Content}");
        }

        sb.AppendLine();
        sb.AppendLine(
            "Based on the transcript above and any meeting documents you have access to, " +
            "generate 3 high-value questions a participant could ask right now. " +
            "Return ONLY a JSON array in this exact format:\n" +
            "[{\"question\":\"...\",\"rationale\":\"...\",\"category\":\"Clarification|Deep-dive|Follow-up|Summary\",\"confidence\":0.0}]");

        return sb.ToString();
    }

    private async Task<ThreadRun> PollRunToCompletionAsync(
        string threadId, string runId, CancellationToken cancellationToken)
    {
        ThreadRun run;
        do
        {
            await Task.Delay(1000, cancellationToken);
            run = (await _client.GetRunAsync(threadId, runId, cancellationToken)).Value;
        }
        while (run.Status == RunStatus.Queued || run.Status == RunStatus.InProgress);

        return run;
    }

    private async Task<List<QuestionSuggestion>> ExtractQuestionsAsync(
        string threadId, CancellationToken cancellationToken)
    {
        var messagesResponse = await _client.GetMessagesAsync(threadId, cancellationToken: cancellationToken);
        var latestAssistantMessage = messagesResponse.Value.Data
            .Where(m => m.Role == MessageRole.Assistant)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefault();

        if (latestAssistantMessage == null)
            return new List<QuestionSuggestion>();

        var content = latestAssistantMessage.ContentItems
            .OfType<MessageTextContent>()
            .FirstOrDefault()?.Text ?? string.Empty;

        return ParseQuestionJson(content);
    }

    private List<QuestionSuggestion> ParseQuestionJson(string json)
    {
        try
        {
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start < 0 || end < 0)
                return new List<QuestionSuggestion>();

            var jsonArray = json[start..(end + 1)];
            var items = JsonSerializer.Deserialize<List<QuestionItem>>(jsonArray, JsonOptions)
                        ?? new List<QuestionItem>();

            return items.Select(i => new QuestionSuggestion(
                Guid.NewGuid().ToString(),
                i.Question ?? string.Empty,
                i.Rationale ?? string.Empty,
                i.Category ?? "Follow-up",
                (float)(i.Confidence ?? 0.8),
                DateTimeOffset.UtcNow
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse question JSON from Assistants response");
            return new List<QuestionSuggestion>();
        }
    }

    private sealed class QuestionItem
    {
        public string? Question { get; set; }
        public string? Rationale { get; set; }
        public string? Category { get; set; }
        public double? Confidence { get; set; }
    }
}
