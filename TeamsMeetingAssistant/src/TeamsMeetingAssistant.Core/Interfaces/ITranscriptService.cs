namespace TeamsMeetingAssistant.Core.Interfaces;

public interface ITranscriptService
{
    void SetAccessToken(string accessToken);
    Task<IEnumerable<TranscriptSegment>> GetNewTranscriptSegmentsAsync(string meetingId, DateTimeOffset since, CancellationToken cancellationToken);
    Task<MeetingInfo> GetMeetingInfoAsync(string meetingId, CancellationToken cancellationToken);
    Task<Subscription> SubscribeToTranscriptChangesAsync(string meetingId, string webhookUrl, CancellationToken cancellationToken);
    Task RenewSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken);
    Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken);
}

public record Subscription(
    string Id,
    DateTimeOffset? ExpirationDateTime,
    string Resource,
    string ChangeType
);