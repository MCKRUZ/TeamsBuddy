namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// Thin abstraction over Azure OpenAI chat-completions, used for testability.
/// </summary>
internal interface IChatCompletionProvider
{
    Task<string> CompleteChatAsync(
        string systemPrompt,
        string userMessage,
        int maxTokens,
        float temperature,
        CancellationToken ct);
}
