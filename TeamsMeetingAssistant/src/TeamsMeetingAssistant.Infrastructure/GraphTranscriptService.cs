using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Polly;
using Polly.Retry;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Infrastructure;

public class GraphTranscriptService : ITranscriptService
{
    private readonly GraphServiceClient _graphClient;
    private readonly VttTranscriptParser _parser;
    private readonly ILogger<GraphTranscriptService> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public GraphTranscriptService(
        IConfiguration configuration,
        VttTranscriptParser parser,
        ILogger<GraphTranscriptService> logger)
    {
        var tenantId = configuration["AzureAd:TenantId"]
            ?? throw new InvalidOperationException("AzureAd:TenantId is not configured");
        var clientId = configuration["AzureAd:ClientId"]
            ?? throw new InvalidOperationException("AzureAd:ClientId is not configured");
        var clientSecret = configuration["AzureAd:ClientSecret"]
            ?? throw new InvalidOperationException("AzureAd:ClientSecret is not configured");

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        _graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);

        _parser = parser;
        _logger = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<ODataError>(e => e.ResponseStatusCode == 429 || e.ResponseStatusCode >= 500),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning("Graph API throttled. Retry {Attempt} after {Delay}s",
                        args.AttemptNumber + 1, args.RetryDelay.TotalSeconds);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public async Task<IEnumerable<TranscriptSegment>> GetNewTranscriptSegmentsAsync(
        string meetingId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching transcripts for meeting {MeetingId} since {Since}", meetingId, since);

        var transcripts = await _retryPipeline.ExecuteAsync(async ct =>
            await _graphClient.Communications.OnlineMeetings[meetingId]
                .Transcripts
                .GetAsync(cancellationToken: ct), cancellationToken);

        if (transcripts?.Value == null || transcripts.Value.Count == 0)
        {
            _logger.LogDebug("No transcripts found for meeting {MeetingId}", meetingId);
            return [];
        }

        var allSegments = new List<TranscriptSegment>();

        foreach (var transcript in transcripts.Value)
        {
            if (transcript.Id == null) continue;

            var vttContent = await _retryPipeline.ExecuteAsync(async ct =>
            {
                var stream = await _graphClient.Communications.OnlineMeetings[meetingId]
                    .Transcripts[transcript.Id]
                    .Content
                    .GetAsync(config =>
                    {
                        config.Headers.Add("Accept", "text/vtt");
                    }, ct);

                if (stream == null) return string.Empty;

                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(ct);
            }, cancellationToken);

            if (string.IsNullOrWhiteSpace(vttContent)) continue;

            var baseTime = transcript.CreatedDateTime ?? DateTimeOffset.UtcNow;
            var segments = _parser.Parse(vttContent, baseTime);
            var newSegments = segments.Where(s => s.Timestamp > since);
            allSegments.AddRange(newSegments);
        }

        _logger.LogInformation("Found {Count} new segments for meeting {MeetingId}",
            allSegments.Count, meetingId);

        return allSegments.OrderBy(s => s.Timestamp);
    }

    public async Task<MeetingSession> GetMeetingInfoAsync(
        string meetingId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Getting meeting info for {MeetingId}", meetingId);

        var meeting = await _retryPipeline.ExecuteAsync(async ct =>
            await _graphClient.Communications.OnlineMeetings[meetingId]
                .GetAsync(cancellationToken: ct), cancellationToken);

        if (meeting == null)
            throw new InvalidOperationException($"Meeting {meetingId} not found in Graph API");

        // Determine if transcription has been started by checking for any transcript objects
        var transcriptionEnabled = false;
        try
        {
            var transcripts = await _graphClient.Communications.OnlineMeetings[meetingId]
                .Transcripts
                .GetAsync(cancellationToken: cancellationToken);

            transcriptionEnabled = transcripts?.Value?.Count > 0;
        }
        catch (ODataError ex)
        {
            _logger.LogWarning("Could not check transcription status for {MeetingId}: {Message}",
                meetingId, ex.Message);
        }

        return new MeetingSession(
            meetingId,
            meeting.Participants?.Organizer?.Upn ?? "unknown",
            meeting.StartDateTime ?? DateTimeOffset.UtcNow,
            meeting.EndDateTime,
            transcriptionEnabled,
            null,
            DateTimeOffset.UtcNow,
            MeetingStatus.Active
        );
    }

    public async Task<Subscription> SubscribeToTranscriptChangesAsync(
        string meetingId, string webhookUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating Graph subscription for meeting {MeetingId}", meetingId);

        var subscription = new Microsoft.Graph.Models.Subscription
        {
            ChangeType = "created,updated",
            NotificationUrl = webhookUrl,
            Resource = $"/communications/onlineMeetings/{meetingId}/transcripts",
            ExpirationDateTime = DateTimeOffset.UtcNow.AddHours(1),
            ClientState = Guid.NewGuid().ToString()
        };

        var created = await _retryPipeline.ExecuteAsync(async ct =>
            await _graphClient.Subscriptions.PostAsync(subscription, cancellationToken: ct),
            cancellationToken);

        if (created?.Id == null)
            throw new InvalidOperationException("Graph API returned null subscription");

        _logger.LogInformation("Created subscription {SubscriptionId} expiring {ExpiresAt}",
            created.Id, created.ExpirationDateTime);

        return new Subscription(
            created.Id,
            created.ExpirationDateTime,
            created.Resource ?? string.Empty,
            created.ChangeType ?? string.Empty
        );
    }

    public async Task RenewSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Renewing subscription {SubscriptionId}", subscriptionId);

        var update = new Microsoft.Graph.Models.Subscription
        {
            ExpirationDateTime = DateTimeOffset.UtcNow.AddHours(1)
        };

        await _retryPipeline.ExecuteAsync(async ct =>
            await _graphClient.Subscriptions[subscriptionId]
                .PatchAsync(update, cancellationToken: ct),
            cancellationToken);
    }

    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deleting subscription {SubscriptionId}", subscriptionId);

        await _retryPipeline.ExecuteAsync(async ct =>
        {
            await _graphClient.Subscriptions[subscriptionId].DeleteAsync(cancellationToken: ct);
            return true;
        }, cancellationToken);
    }
}
