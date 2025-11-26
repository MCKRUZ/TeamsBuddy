using System.Collections.Concurrent;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// In-memory implementation of conversation store for short-term session management
/// </summary>
public class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _conversations = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAccessTimes = new();
    private readonly TimeSpan _expirationTime = TimeSpan.FromHours(1);

    public Task AddMessageAsync(string conversationId, ChatMessage message)
    {
        _conversations.AddOrUpdate(
            conversationId,
            _ => new List<ChatMessage> { message },
            (_, messages) =>
            {
                messages.Add(message);
                return messages;
            });

        _lastAccessTimes[conversationId] = DateTimeOffset.UtcNow;
        
        // Clean up expired conversations
        CleanupExpiredConversations();

        return Task.CompletedTask;
    }

    public Task<List<ChatMessage>> GetMessagesAsync(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var messages))
        {
            _lastAccessTimes[conversationId] = DateTimeOffset.UtcNow;
            return Task.FromResult(new List<ChatMessage>(messages));
        }

        return Task.FromResult(new List<ChatMessage>());
    }

    public Task ClearConversationAsync(string conversationId)
    {
        _conversations.TryRemove(conversationId, out _);
        _lastAccessTimes.TryRemove(conversationId, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string conversationId)
    {
        return Task.FromResult(_conversations.ContainsKey(conversationId));
    }

    private void CleanupExpiredConversations()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredConversations = _lastAccessTimes
            .Where(kvp => now - kvp.Value > _expirationTime)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var conversationId in expiredConversations)
        {
            _conversations.TryRemove(conversationId, out _);
            _lastAccessTimes.TryRemove(conversationId, out _);
        }
    }
}
