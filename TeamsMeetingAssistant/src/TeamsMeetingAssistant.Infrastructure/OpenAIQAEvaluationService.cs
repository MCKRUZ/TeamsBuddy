using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// OpenAI-based service to evaluate whether candidate responses adequately answer interview questions
/// </summary>
public class OpenAIQAEvaluationService : IQAEvaluationService
{
    private readonly OpenAIClient _client;
    private readonly string _deploymentName;
    private readonly ILogger<OpenAIQAEvaluationService> _logger;

    public OpenAIQAEvaluationService(
        IConfiguration configuration,
        ILogger<OpenAIQAEvaluationService> logger)
    {
        _logger = logger;
        
        var endpoint = configuration["AzureOpenAI:Endpoint"] 
            ?? throw new ArgumentNullException("AzureOpenAI:Endpoint");
        var apiKey = configuration["AzureOpenAI:ApiKey"] 
            ?? throw new ArgumentNullException("AzureOpenAI:ApiKey");
        _deploymentName = configuration["AzureOpenAI:DeploymentName"] 
            ?? throw new ArgumentNullException("AzureOpenAI:DeploymentName");

        _client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    }

    public async Task<QAEvaluationResult> EvaluateResponseAsync(
        string interviewerQuestion,
        List<string> candidateResponses,
        string meetingContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(interviewerQuestion))
        {
            throw new ArgumentException("Interviewer question cannot be empty", nameof(interviewerQuestion));
        }

        if (candidateResponses == null || !candidateResponses.Any())
        {
            return new QAEvaluationResult(
                IsAnswered: false,
                IsAdequate: false,
                NeedsFollowUp: false,
                Reasoning: "No response received from candidate",
                Quality: ResponseQuality.NoResponse,
                IdentifiedGaps: new List<string>()
            );
        }

        try
        {
            var systemPrompt = @"You are an expert technical interviewer evaluating candidate responses during a live interview.

Your task is to analyze whether the candidate's response adequately answers the interviewer's question.

Consider:
1. **Relevance**: Did they address the question asked?
2. **Completeness**: Did they cover the key aspects?
3. **Depth**: Is the answer superficial or demonstrates understanding?
4. **Clarity**: Is the explanation clear and coherent?
5. **Context**: Does the answer make sense for a technical interview?

Evaluate honestly but fairly. A candidate might:
- Answer well but briefly (adequate)
- Start answering but get interrupted (partial)
- Misunderstand the question (not answered)
- Go off-topic (not answered)
- Provide excellent comprehensive answer (comprehensive)
- If you believe the candidate's answer was correct/adequate then you should still provide follow-up suggestions for additional questions that build on top of the asked question.
- If the candidate's answer doesn't meet your criteria of correctness, you can create a follow-up statement to help the interviewer continue iterating through the interview process.

Return your evaluation in this exact JSON format:
{
  ""isAnswered"": true/false,
  ""isAdequate"": true/false,
  ""needsFollowUp"": true/false,
  ""reasoning"": ""Brief explanation of your evaluation"",
  ""quality"": ""NoResponse/Superficial/Partial/Adequate/Comprehensive"",
  ""identifiedGaps"": [""gap1"", ""gap2""]
}";

            var candidateResponseText = string.Join("\n", candidateResponses.Select((r, i) => 
                $"[Response {i + 1}]: {r}"));

            var userPrompt = $@"Context: {meetingContext}

INTERVIEWER'S QUESTION:
{interviewerQuestion}

CANDIDATE'S RESPONSE:
{candidateResponseText}

Evaluate if the candidate adequately answered the question. Return only valid JSON.";

            var chatCompletionsOptions = new ChatCompletionsOptions(_deploymentName, new ChatRequestMessage[]
            {
                new ChatRequestSystemMessage(systemPrompt),
                new ChatRequestUserMessage(userPrompt)
            })
            {
                MaxTokens = 500,
                Temperature = 0.3f, // Lower temperature for more consistent evaluation
                ResponseFormat = ChatCompletionsResponseFormat.JsonObject
            };

            _logger.LogDebug("Evaluating Q&A exchange. Question: '{Question}', Responses: {Count}",
                interviewerQuestion.Substring(0, Math.Min(50, interviewerQuestion.Length)), 
                candidateResponses.Count);

            var response = await _client.GetChatCompletionsAsync(chatCompletionsOptions, cancellationToken);

            if (response.Value.Choices.Count == 0)
            {
                _logger.LogWarning("No response from OpenAI for Q&A evaluation");
                return CreateDefaultResult("No evaluation response from AI");
            }

            var jsonResponse = response.Value.Choices[0].Message.Content;
            
            _logger.LogDebug("Q&A Evaluation response: {Response}", jsonResponse);

            // Parse JSON response
            var evaluation = ParseEvaluationResponse(jsonResponse);
            
            _logger.LogInformation("Q&A Evaluation: IsAnswered={IsAnswered}, Quality={Quality}, NeedsFollowUp={NeedsFollowUp}",
                evaluation.IsAnswered, evaluation.Quality, evaluation.NeedsFollowUp);

            return evaluation;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Q&A evaluation JSON response");
            return CreateDefaultResult("Failed to parse evaluation response");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating Q&A exchange");
            return CreateDefaultResult($"Evaluation error: {ex.Message}");
        }
    }

    private QAEvaluationResult ParseEvaluationResponse(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            var isAnswered = root.GetProperty("isAnswered").GetBoolean();
            var isAdequate = root.GetProperty("isAdequate").GetBoolean();
            var needsFollowUp = root.GetProperty("needsFollowUp").GetBoolean();
            var reasoning = root.GetProperty("reasoning").GetString() ?? "No reasoning provided";
            var qualityStr = root.GetProperty("quality").GetString() ?? "Partial";
            
            var quality = Enum.TryParse<ResponseQuality>(qualityStr, true, out var parsedQuality)
                ? parsedQuality
                : ResponseQuality.Partial;

            var gaps = new List<string>();
            if (root.TryGetProperty("identifiedGaps", out var gapsElement) && 
                gapsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var gap in gapsElement.EnumerateArray())
                {
                    var gapText = gap.GetString();
                    if (!string.IsNullOrEmpty(gapText))
                    {
                        gaps.Add(gapText);
                    }
                }
            }

            return new QAEvaluationResult(
                isAnswered,
                isAdequate,
                needsFollowUp,
                reasoning,
                quality,
                gaps
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing evaluation JSON: {Json}", jsonResponse);
            throw;
        }
    }

    private QAEvaluationResult CreateDefaultResult(string reasoning)
    {
        return new QAEvaluationResult(
            IsAnswered: false,
            IsAdequate: false,
            NeedsFollowUp: false,
            Reasoning: reasoning,
            Quality: ResponseQuality.NoResponse,
            IdentifiedGaps: new List<string>()
        );
    }
}
