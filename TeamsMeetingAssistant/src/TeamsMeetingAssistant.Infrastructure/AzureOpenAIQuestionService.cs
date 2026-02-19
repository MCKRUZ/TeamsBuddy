using System.Text;
using System.Text.Json;
using Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// Chat-completions implementation of IQuestionGenerationService using Azure OpenAI.
/// Replaces MockOpenAIQuestionService when AzureOpenAI:Endpoint is configured.
/// Uses an internal IChatCompletionProvider for testability.
/// </summary>
public class AzureOpenAIQuestionService : IQuestionGenerationService
{
    private readonly IChatCompletionProvider _chatProvider;
    private readonly ILogger<AzureOpenAIQuestionService> _logger;
    private readonly int _maxTokens;
    private readonly float _temperature;
    private readonly ResiliencePipeline _retryPipeline;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string SystemPrompt =
        "You are an expert meeting facilitator and strategic advisor. Analyse the live meeting " +
        "transcript and suggest 3 high-value questions a participant could ask right now. " +
        "Respond ONLY with a valid JSON array — no markdown fences, no preamble — in this exact format:\n" +
        "[{\"question\":\"...\",\"rationale\":\"...\",\"category\":\"Clarification|Deep-dive|Follow-up|Summary\",\"confidence\":0.0}]";

    // Production constructor — builds a real Azure OpenAI provider.
    public AzureOpenAIQuestionService(
        IConfiguration configuration,
        ILogger<AzureOpenAIQuestionService> logger)
        : this(CreateProvider(configuration), configuration, logger) { }

    // Internal constructor for testing — inject a mock IChatCompletionProvider.
    internal AzureOpenAIQuestionService(
        IChatCompletionProvider chatProvider,
        IConfiguration configuration,
        ILogger<AzureOpenAIQuestionService> logger)
    {
        _chatProvider = chatProvider;
        _logger = logger;
        _maxTokens = int.TryParse(configuration["AzureOpenAI:MaxTokens"], out var mt) ? mt : 800;
        _temperature = float.TryParse(configuration["AzureOpenAI:Temperature"], out var t) ? t : 0.7f;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<RequestFailedException>(e => e.Status == 429 || e.Status >= 500),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Azure OpenAI throttled. Retry {Attempt}", args.AttemptNumber + 1);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    private static IChatCompletionProvider CreateProvider(IConfiguration configuration)
    {
        var endpoint = configuration["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured");
        var apiKey = configuration["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is not configured");
        var deploymentName = configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4";

        return new AzureChatCompletionProvider(endpoint, apiKey, deploymentName);
    }

    public async Task<List<QuestionSuggestion>> GenerateQuestionsAsync(
        List<TranscriptSegment> recentTranscript,
        QuestionGenerationContext context,
        CancellationToken cancellationToken)
    {
        if (recentTranscript.Count == 0)
        {
            _logger.LogDebug(
                "No transcript segments; skipping question generation for {MeetingId}",
                context.MeetingId);
            return [];
        }

        try
        {
            var userMessage = BuildUserMessage(recentTranscript, context);

            var json = await _retryPipeline.ExecuteAsync(async ct =>
                await _chatProvider.CompleteChatAsync(
                    SystemPrompt, userMessage, _maxTokens, _temperature, ct),
                cancellationToken);

            var questions = ParseQuestionJson(json, context.MeetingId);

            _logger.LogInformation(
                "Generated {Count} question suggestions for meeting {MeetingId}",
                questions.Count, context.MeetingId);

            return questions;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to generate questions via Azure OpenAI for meeting {MeetingId}",
                context.MeetingId);
            return [];
        }
    }

    internal static string BuildUserMessage(
        List<TranscriptSegment> segments,
        QuestionGenerationContext context)
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
        sb.AppendLine("Generate 3 high-value questions a participant could ask right now.");
        return sb.ToString();
    }

    internal List<QuestionSuggestion> ParseQuestionJson(string json, string meetingId)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start < 0 || end < 0)
            {
                _logger.LogWarning(
                    "No JSON array found in OpenAI response for meeting {MeetingId}", meetingId);
                return [];
            }

            var jsonArray = json[start..(end + 1)];
            var items = JsonSerializer.Deserialize<List<QuestionItem>>(jsonArray, JsonOptions) ?? [];

            return items.Select(i => new QuestionSuggestion(
                Guid.NewGuid().ToString(),
                i.Question ?? string.Empty,
                i.Rationale ?? string.Empty,
                i.Category ?? "Follow-up",
                (float)(i.Confidence ?? 0.8),
                DateTimeOffset.UtcNow
            )).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse question JSON for meeting {MeetingId}", meetingId);
            return [];
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
