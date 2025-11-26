namespace TeamsMeetingAssistant.Core.Interfaces;

/// <summary>
/// Service for handling AI chat interactions
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Send a message to the AI chatbot and get a response
    /// </summary>
    /// <param name="message">User's message</param>
    /// <param name="conversationId">Optional conversation ID for context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>AI response</returns>
    Task<ChatResponse> SendMessageAsync(
        string message, 
        string? conversationId = null, 
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from the chat service
/// </summary>
public record ChatResponse(
    string Message,
    string ConversationId,
    DateTimeOffset Timestamp,
    bool IsSuccess,
    string? ErrorMessage = null
);

/// <summary>
/// Chat message model
/// </summary>
public record ChatMessage(
    string Id,
    string Content,
    string Role, // "user" or "assistant"
    DateTimeOffset Timestamp
);
