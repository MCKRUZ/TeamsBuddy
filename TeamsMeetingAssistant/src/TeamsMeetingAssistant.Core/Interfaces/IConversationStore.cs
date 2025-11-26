namespace TeamsMeetingAssistant.Core.Interfaces;

/// <summary>
/// Store for managing conversation history and context
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// Add a message to a conversation
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <param name="message">Message to add</param>
    Task AddMessageAsync(string conversationId, ChatMessage message);

    /// <summary>
    /// Get all messages in a conversation
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <returns>List of messages in chronological order</returns>
    Task<List<ChatMessage>> GetMessagesAsync(string conversationId);

    /// <summary>
    /// Clear all messages in a conversation
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    Task ClearConversationAsync(string conversationId);

    /// <summary>
    /// Check if a conversation exists
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <returns>True if conversation exists</returns>
    Task<bool> ExistsAsync(string conversationId);
}
