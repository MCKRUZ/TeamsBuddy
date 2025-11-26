using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using TeamsMeetingAssistant.Core.Interfaces;
using TeamsMeetingAssistant.Core;
using System.Text;

namespace TeamsMeetingAssistant.Infrastructure;

public class OpenAIQuestionService : IQuestionGenerationService
{
    private readonly OpenAIClient _client;
    private readonly string _deploymentName;

    public OpenAIQuestionService(IConfiguration configuration)
    {
        var endpoint = configuration["AzureOpenAI:Endpoint"] ?? throw new ArgumentNullException("AzureOpenAI:Endpoint");
        var apiKey = configuration["AzureOpenAI:ApiKey"] ?? throw new ArgumentNullException("AzureOpenAI:ApiKey");
        _deploymentName = configuration["AzureOpenAI:DeploymentName"] ?? throw new ArgumentNullException("AzureOpenAI:DeploymentName");

        _client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    }

    public async Task<IEnumerable<QuestionSuggestion>> GenerateQuestionsAsync(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return Enumerable.Empty<QuestionSuggestion>();
        }

        var chatCompletionsOptions = new ChatCompletionsOptions(_deploymentName, new ChatRequestMessage[]
        {
            new ChatRequestSystemMessage("You are an AI assistant that helps summarize meeting transcripts and suggest relevant questions. Your goal is to identify key topics and formulate questions that were not answered or need further clarification. Provide 3-5 questions based on the following transcript. Format the output as a simple, un-numbered list with each question on a new line."),
            new ChatRequestUserMessage(transcript)
        })
        {
            MaxTokens = 1024,
            Temperature = 0.7f
        };

        var response = await _client.GetChatCompletionsAsync(chatCompletionsOptions);
        
        if (response.Value.Choices.Count == 0)
        {
            return Enumerable.Empty<QuestionSuggestion>();
        }

        var rawQuestions = response.Value.Choices[0].Message.Content;
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

    public async Task<List<QuestionSuggestion>> GenerateQuestionsAsync(
        List<TranscriptSegment> recentTranscript, 
        string meetingContext, 
        CancellationToken cancellationToken)
    {
        if (recentTranscript == null || !recentTranscript.Any())
        {
            return new List<QuestionSuggestion>();
        }

        // Build conversation context from transcript segments
        var conversationContext = BuildConversationContext(recentTranscript);

        // System prompt for C# technical interviewer
        var systemPrompt = @"You are an expert C# and software engineering technical interviewer conducting a technical interview. 

Your role is to:
1. Analyze the candidate's (interviewee's) responses carefully
2. Generate 3-5 insightful follow-up questions based on their answers
3. Probe deeper into their technical knowledge and experience
4. Focus on C#, .NET, software architecture, design patterns, and best practices
5. Ask questions that reveal their problem-solving approach and depth of understanding

Generate questions that:
- Build naturally on what the candidate just said
- Explore gaps or areas that need clarification
- Test deeper understanding of concepts they mentioned
- Are specific and technical (not generic)
- Help assess their real-world experience

Format: Return only the questions, one per line, without numbering or bullets.";

        var userPrompt = $@"Based on this interview conversation, generate relevant follow-up questions:

{conversationContext}

Generate 3-5 follow-up questions for the interviewer to ask the candidate.";

        var chatCompletionsOptions = new ChatCompletionsOptions(_deploymentName, new ChatRequestMessage[]
        {
            new ChatRequestSystemMessage(systemPrompt),
            new ChatRequestUserMessage(userPrompt)
        })
        {
            MaxTokens = 500,
            Temperature = 0.7f
        };

        try
        {
            var response = await _client.GetChatCompletionsAsync(chatCompletionsOptions, cancellationToken);
            
            if (response.Value.Choices.Count == 0)
            {
                return new List<QuestionSuggestion>();
            }

            var rawQuestions = response.Value.Choices[0].Message.Content;
            var questions = rawQuestions
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.Trim().TrimStart('-', '*', '•').Trim())
                .Where(q => q.Length > 10) // Filter out very short lines
                .Select(q => new QuestionSuggestion(
                    Guid.NewGuid().ToString(),
                    q,
                    "Generated based on candidate's recent responses",
                    "Follow-up",
                    0.85f,
                    DateTimeOffset.UtcNow
                ))
                .ToList();

            return questions;
        }
        catch (Exception)
        {
            // Log error and return empty list
            return new List<QuestionSuggestion>();
        }
    }

    private string BuildConversationContext(List<TranscriptSegment> segments)
    {
        var sb = new StringBuilder();
        
        foreach (var segment in segments.OrderBy(s => s.Timestamp))
        {
            var roleLabel = segment.Role == SpeakerRole.Interviewer ? "INTERVIEWER" : "CANDIDATE";
            sb.AppendLine($"{roleLabel} ({segment.SpeakerName}): {segment.Content}");
            sb.AppendLine();
        }
        
        return sb.ToString();
    }
}
