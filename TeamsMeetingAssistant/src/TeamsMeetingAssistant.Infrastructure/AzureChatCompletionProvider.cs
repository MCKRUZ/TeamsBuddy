using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// Production IChatCompletionProvider — delegates to the Azure OpenAI SDK (2.x API).
/// </summary>
internal sealed class AzureChatCompletionProvider : IChatCompletionProvider
{
    private readonly ChatClient _chatClient;

    public AzureChatCompletionProvider(string endpoint, string apiKey, string deploymentName)
    {
        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _chatClient = azureClient.GetChatClient(deploymentName);
    }

    public async Task<string> CompleteChatAsync(
        string systemPrompt,
        string userMessage,
        int maxTokens,
        float temperature,
        CancellationToken ct)
    {
        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userMessage)
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxTokens,
            Temperature = temperature
        };

        var result = await _chatClient.CompleteChatAsync(messages, options, ct);
        return result.Value.Content[0].Text;
    }
}
