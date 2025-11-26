using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// Chat service implementation using Azure OpenAI with conversation memory
/// </summary>
public class AzureOpenAIChatService : IChatService
{
    private readonly ILogger<AzureOpenAIChatService> _logger;
    private readonly OpenAIClient _openAIClient;
    private readonly string _deploymentName;
    private readonly IConversationStore _conversationStore;

    public AzureOpenAIChatService(
        IConfiguration configuration,
        ILogger<AzureOpenAIChatService> logger,
        IConversationStore conversationStore)
    {
        _logger = logger;
        _conversationStore = conversationStore;

        // Get Azure OpenAI configuration
        var endpoint = configuration["AzureOpenAI:Endpoint"];
        var apiKey = configuration["AzureOpenAI:ApiKey"];
        _deploymentName = configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4";

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("Azure OpenAI configuration is missing. Check Endpoint and ApiKey in appsettings.json");
        }

        _openAIClient = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _logger.LogInformation("Azure OpenAI Chat Service initialized with deployment: {DeploymentName}", _deploymentName);
    }

    public async Task<ChatResponse> SendMessageAsync(
        string message,
        string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var newConversationId = conversationId ?? Guid.NewGuid().ToString();
            
            _logger.LogInformation("Sending message to Azure OpenAI. ConversationId: {ConversationId}", newConversationId);

            // Get conversation history
            var conversationHistory = await _conversationStore.GetMessagesAsync(newConversationId);
            
            // Build message list with system message and conversation history
            var messages = new List<ChatRequestMessage>
            {
                new ChatRequestSystemMessage("You are a helpful AI assistant. Provide clear, concise, and professional responses.")
            };

            // Add conversation history
            foreach (var historyMessage in conversationHistory)
            {
                if (historyMessage.Role == "user")
                {
                    messages.Add(new ChatRequestUserMessage(historyMessage.Content));
                }
                else if (historyMessage.Role == "assistant")
                {
                    messages.Add(new ChatRequestAssistantMessage(historyMessage.Content));
                }
            }

            // Add current user message
            messages.Add(new ChatRequestUserMessage(message));

            var chatCompletionsOptions = new ChatCompletionsOptions(_deploymentName, messages)
            {
                MaxTokens = 800,
                Temperature = 0.7f,
                NucleusSamplingFactor = 0.95f
            };

            var response = await _openAIClient.GetChatCompletionsAsync(chatCompletionsOptions, cancellationToken);

            var responseMessage = response.Value.Choices[0].Message.Content;

            // Store user message in conversation history
            await _conversationStore.AddMessageAsync(newConversationId, new ChatMessage(
                Guid.NewGuid().ToString(),
                message,
                "user",
                DateTimeOffset.UtcNow
            ));

            // Store assistant response in conversation history
            await _conversationStore.AddMessageAsync(newConversationId, new ChatMessage(
                Guid.NewGuid().ToString(),
                responseMessage,
                "assistant",
                DateTimeOffset.UtcNow
            ));

            _logger.LogInformation("Successfully received response from Azure OpenAI. Length: {Length} characters. History count: {Count}", 
                responseMessage.Length, conversationHistory.Count + 2);

            return new ChatResponse(
                responseMessage,
                newConversationId,
                DateTimeOffset.UtcNow,
                true
            );
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Azure OpenAI request failed: {ErrorCode} - {Message}", ex.ErrorCode, ex.Message);
            return new ChatResponse(
                string.Empty,
                conversationId ?? Guid.NewGuid().ToString(),
                DateTimeOffset.UtcNow,
                false,
                $"Failed to get AI response: {ex.Message}"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in chat service");
            return new ChatResponse(
                string.Empty,
                conversationId ?? Guid.NewGuid().ToString(),
                DateTimeOffset.UtcNow,
                false,
                $"An error occurred: {ex.Message}"
            );
        }
    }
}
