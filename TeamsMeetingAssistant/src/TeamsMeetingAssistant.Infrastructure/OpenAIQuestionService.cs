using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using TeamsMeetingAssistant.Core.Interfaces;
using TeamsMeetingAssistant.Core;
using OpenAI.Chat;

namespace TeamsMeetingAssistant.Infrastructure;

public class OpenAIQuestionService : IQuestionGenerationService
{
    private readonly AzureOpenAIClient _client;
    private readonly string _deploymentName;

    public OpenAIQuestionService(IConfiguration configuration)
    {
        var endpoint = configuration["AzureOpenAI:Endpoint"] ?? throw new ArgumentNullException("AzureOpenAI:Endpoint");
        var apiKey = configuration["AzureOpenAI:ApiKey"] ?? throw new ArgumentNullException("AzureOpenAI:ApiKey");
        _deploymentName = configuration["AzureOpenAI:DeploymentName"] ?? throw new ArgumentNullException("AzureOpenAI:DeploymentName");

        _client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    }

    public async Task<IEnumerable<QuestionSuggestion>> GenerateQuestionsAsync(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return Enumerable.Empty<QuestionSuggestion>();
        }

        var chatClient = _client.GetChatClient(_deploymentName);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage("You are an AI assistant that helps summarize meeting transcripts and suggest relevant questions. Your goal is to identify key topics and formulate questions that were not answered or need further clarification. Provide 3-5 questions based on the following transcript. Format the output as a simple, un-numbered list with each question on a new line."),
            new UserChatMessage(transcript)
        };

        var response = await chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            MaxOutputTokenCount = 1024,
            Temperature = 0.7f
        });
        
        if (response.Value.Content.Count == 0)
        {
            return Enumerable.Empty<QuestionSuggestion>();
        }

        var rawQuestions = response.Value.Content[0].Text;
        var questions = rawQuestions.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(q => new QuestionSuggestion(
                                        Guid.NewGuid().ToString(),
                                        q.Trim(),
                                        "Generated from transcript",
                                        "AI-Generated",
                                        0.8f,
                                        DateTimeOffset.UtcNow
                                    ));

        return questions;
    }

    public Task<List<QuestionSuggestion>> GenerateQuestionsAsync(List<TranscriptSegment> recentTranscript, string meetingContext, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
