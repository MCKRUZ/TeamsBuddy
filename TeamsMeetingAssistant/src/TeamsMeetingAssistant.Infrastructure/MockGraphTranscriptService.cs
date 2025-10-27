using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace TeamsMeetingAssistant.Infrastructure;

public class MockGraphTranscriptService : ITranscriptService
{
    private readonly ILogger<MockGraphTranscriptService> _logger;

    public MockGraphTranscriptService(ILogger<MockGraphTranscriptService> logger)
    {
        _logger = logger;
    }

    public async Task<IEnumerable<TranscriptSegment>> GetNewTranscriptSegmentsAsync(string meetingId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        _logger.LogInformation("MOCK: Fetching transcripts for meeting {MeetingId} since {Since}", meetingId, since);

        // Simulate some mock transcript data
        await Task.Delay(100, cancellationToken);

        var mockSegments = new List<TranscriptSegment>
        {
            new TranscriptSegment(
                Guid.NewGuid().ToString(),
                "John Doe",
                "john.doe@contoso.com",
                "Welcome everyone to today's meeting. Let's start with the quarterly review.",
                DateTimeOffset.UtcNow.AddSeconds(-30),
                TimeSpan.FromSeconds(0),
                TimeSpan.FromSeconds(5)
            ),
            new TranscriptSegment(
                Guid.NewGuid().ToString(),
                "Jane Smith",
                "jane.smith@contoso.com",
                "Thanks John. I have the quarterly results ready. Revenue is up 15% compared to last quarter.",
                DateTimeOffset.UtcNow.AddSeconds(-25),
                TimeSpan.FromSeconds(6),
                TimeSpan.FromSeconds(10)
            )
        };

        _logger.LogInformation("MOCK: Found {Count} transcript segments", mockSegments.Count);
        return mockSegments;
    }

    public async Task<MeetingSession> GetMeetingInfoAsync(string meetingId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("MOCK: Getting meeting info for {MeetingId}", meetingId);

        await Task.Delay(50, cancellationToken);

        return new MeetingSession(
            meetingId,
            "organizer@contoso.com",
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddMinutes(30),
            true, // Transcription enabled
            null,
            DateTimeOffset.UtcNow,
            MeetingStatus.Active
        );
    }

    public async Task<Subscription> SubscribeToTranscriptChangesAsync(string meetingId, string webhookUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("MOCK: Creating subscription for meeting {MeetingId} with webhook {WebhookUrl}", meetingId, webhookUrl);

        await Task.Delay(100, cancellationToken);

        return new Subscription(
            Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow.AddHours(1),
            $"/communications/onlineMeetings/{meetingId}/transcripts",
            "updated"
        );
    }

    public async Task RenewSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("MOCK: Renewing subscription {SubscriptionId}", subscriptionId);
        await Task.Delay(50, cancellationToken);
    }

    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("MOCK: Unsubscribing {SubscriptionId}", subscriptionId);
        await Task.Delay(50, cancellationToken);
    }
}