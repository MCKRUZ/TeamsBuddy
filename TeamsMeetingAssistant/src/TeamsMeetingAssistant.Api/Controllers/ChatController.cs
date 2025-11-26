using Microsoft.AspNetCore.Mvc;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    private readonly IConversationStore _conversationStore;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        IChatService chatService,
        IConversationStore conversationStore,
        ILogger<ChatController> logger)
    {
        _chatService = chatService;
        _conversationStore = conversationStore;
        _logger = logger;
    }

    /// <summary>
    /// Send a message to the AI chatbot
    /// </summary>
    [HttpPost("send")]
    public async Task<IActionResult> SendMessage(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { error = "Message cannot be empty" });
            }

            _logger.LogInformation("Received chat message. Length: {Length} characters", request.Message.Length);

            var response = await _chatService.SendMessageAsync(
                request.Message,
                request.ConversationId,
                cancellationToken);

            if (!response.IsSuccess)
            {
                return BadRequest(new
                {
                    error = response.ErrorMessage,
                    conversationId = response.ConversationId
                });
            }

            return Ok(new
            {
                message = response.Message,
                conversationId = response.ConversationId,
                timestamp = response.Timestamp
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing chat message");
            return StatusCode(500, new { error = "An error occurred processing your message" });
        }
    }

    /// <summary>
    /// Clear conversation history
    /// </summary>
    [HttpDelete("conversation/{conversationId}")]
    public async Task<IActionResult> ClearConversation(string conversationId)
    {
        try
        {
            await _conversationStore.ClearConversationAsync(conversationId);
            _logger.LogInformation("Cleared conversation: {ConversationId}", conversationId);
            
            return Ok(new
            {
                message = "Conversation cleared successfully",
                conversationId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing conversation {ConversationId}", conversationId);
            return StatusCode(500, new { error = "Failed to clear conversation" });
        }
    }

    /// <summary>
    /// Get conversation history
    /// </summary>
    [HttpGet("conversation/{conversationId}")]
    public async Task<IActionResult> GetConversation(string conversationId)
    {
        try
        {
            var messages = await _conversationStore.GetMessagesAsync(conversationId);
            
            return Ok(new
            {
                conversationId,
                messageCount = messages.Count,
                messages = messages.Select(m => new
                {
                    m.Id,
                    m.Content,
                    m.Role,
                    m.Timestamp
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving conversation {ConversationId}", conversationId);
            return StatusCode(500, new { error = "Failed to retrieve conversation" });
        }
    }

    /// <summary>
    /// Health check endpoint for chat service
    /// </summary>
    [HttpGet("health")]
    public IActionResult HealthCheck()
    {
        return Ok(new
        {
            status = "healthy",
            service = "Chat Service",
            timestamp = DateTimeOffset.UtcNow
        });
    }
}

#region DTOs

public record ChatRequest(
    string Message,
    string? ConversationId = null
);

#endregion
