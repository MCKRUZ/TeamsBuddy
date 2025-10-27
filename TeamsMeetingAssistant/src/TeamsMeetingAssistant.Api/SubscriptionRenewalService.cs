using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using System.Collections.Concurrent;

namespace TeamsMeetingAssistant.Api;

public class SubscriptionRenewalService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionRenewalService> _logger;

    // Track subscriptions that need renewal
    private readonly ConcurrentDictionary<string, (string SubscriptionId, DateTimeOffset ExpiresAt)>
        _subscriptions = new();

    public SubscriptionRenewalService(
        IServiceProvider serviceProvider,
        ILogger<SubscriptionRenewalService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check every 30 minutes for subscriptions that need renewal
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RenewExpiringSubscriptionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in subscription renewal loop");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    private async Task RenewExpiringSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var expiringThreshold = DateTimeOffset.UtcNow.AddMinutes(60);
        var expiringSubs = _subscriptions
            .Where(kvp => kvp.Value.ExpiresAt <= expiringThreshold)
            .ToList();

        if (!expiringSubs.Any())
        {
            return;
        }

        _logger.LogInformation("Renewing {Count} expiring subscriptions", expiringSubs.Count);

        using var scope = _serviceProvider.CreateScope();
        var transcriptService = scope.ServiceProvider.GetRequiredService<ITranscriptService>();

        foreach (var (meetingId, (subscriptionId, _)) in expiringSubs)
        {
            try
            {
                await transcriptService.RenewSubscriptionAsync(subscriptionId, cancellationToken);

                // Update expiration time
                _subscriptions[meetingId] = (subscriptionId, DateTimeOffset.UtcNow.AddHours(1));

                _logger.LogInformation("Renewed subscription {SubscriptionId}", subscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to renew subscription {SubscriptionId}", subscriptionId);
            }
        }
    }

    public void TrackSubscription(string meetingId, string subscriptionId, DateTimeOffset expiresAt)
    {
        _subscriptions[meetingId] = (subscriptionId, expiresAt);
        _logger.LogInformation("Tracking subscription {SubscriptionId} for meeting {MeetingId}, expires at {ExpiresAt}",
            subscriptionId, meetingId, expiresAt);
    }

    public void UntrackSubscription(string meetingId)
    {
        _subscriptions.TryRemove(meetingId, out _);
        _logger.LogInformation("Untracking subscription for meeting {MeetingId}", meetingId);
    }

    public async Task RenewAllSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var allSubscriptions = _subscriptions.ToList();
        _logger.LogInformation("Renewing all {Count} subscriptions", allSubscriptions.Count);

        using var scope = _serviceProvider.CreateScope();
        var transcriptService = scope.ServiceProvider.GetRequiredService<ITranscriptService>();

        foreach (var (meetingId, (subscriptionId, _)) in allSubscriptions)
        {
            try
            {
                await transcriptService.RenewSubscriptionAsync(subscriptionId, cancellationToken);
                _subscriptions[meetingId] = (subscriptionId, DateTimeOffset.UtcNow.AddHours(1));
                _logger.LogInformation("Renewed subscription {SubscriptionId}", subscriptionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to renew subscription {SubscriptionId}", subscriptionId);
            }
        }
    }
}