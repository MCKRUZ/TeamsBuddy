using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Api;

public class TranscriptPollingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMeetingSessionStore _sessionStore;
    private readonly ILogger<TranscriptPollingService> _logger;
    private readonly IConfiguration _configuration;

    public TranscriptPollingService(
        IServiceProvider serviceProvider,
        IMeetingSessionStore sessionStore,
        ILogger<TranscriptPollingService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollingInterval = TimeSpan.FromSeconds(
            _configuration.GetValue<int>("TranscriptProcessing:PollingIntervalSeconds", 5));

        _logger.LogInformation("Transcript polling service started with interval {Interval}",
            pollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollTranscriptsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in transcript polling loop");
            }

            await Task.Delay(pollingInterval, stoppingToken);
        }
    }

    private async Task PollTranscriptsAsync(CancellationToken cancellationToken)
    {
        var activeSessions = await _sessionStore.GetActiveSessionsAsync();

        _logger.LogDebug("Polling {Count} active meeting sessions", activeSessions.Count());

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 5,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(activeSessions, options, async (session, ct) =>
        {
            await ProcessMeetingSessionAsync(session, ct);
        });
    }

    private async Task ProcessMeetingSessionAsync(
        MeetingSession session,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();

        var transcriptService = scope.ServiceProvider.GetRequiredService<ITranscriptService>();
        var signalRService = scope.ServiceProvider.GetRequiredService<ISignalRService>();
        var questionService = scope.ServiceProvider.GetRequiredService<IQuestionGenerationService>();
        var orgKnowledgeService = scope.ServiceProvider.GetRequiredService<IOrgKnowledgeService>();

        try
        {
            var newSegments = await transcriptService.GetNewTranscriptSegmentsAsync(
                session.MeetingId,
                session.LastProcessedTime,
                cancellationToken);

            if (!newSegments.Any())
            {
                return;
            }

            _logger.LogInformation(
                "Found {Count} new transcript segments for meeting {MeetingId}",
                newSegments.Count(),
                session.MeetingId);

            foreach (var segment in newSegments)
            {
                await signalRService.SendTranscriptUpdateAsync(session.MeetingId, segment);
            }

            var timeSinceLastQuestion = DateTimeOffset.UtcNow - session.LastProcessedTime;
            var questionThreshold = TimeSpan.FromSeconds(
                _configuration.GetValue<int>("TranscriptProcessing:QuestionGenerationThresholdSeconds", 30));

            if (timeSinceLastQuestion >= questionThreshold && newSegments.Count() >= 3)
            {
                var topK = _configuration.GetValue<int>("AzureAISearch:TopK", 5);
                var query = string.Join(" ", newSegments.TakeLast(3).Select(s => s.Content));
                var orgChunks = await orgKnowledgeService.SearchAsync(query, topK, cancellationToken);

                var context = new QuestionGenerationContext(
                    session.MeetingId,
                    session.OrganizerEmail,
                    session.AssistantId,
                    session.ThreadId,
                    orgChunks);

                var questions = await questionService.GenerateQuestionsAsync(
                    newSegments.ToList(),
                    context,
                    cancellationToken);

                await signalRService.SendQuestionSuggestionsAsync(session.MeetingId, questions);
            }

            var updatedSession = session with
            {
                LastProcessedTime = newSegments.Max(s => s.Timestamp)
            };
            await _sessionStore.AddOrUpdateAsync(updatedSession);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing meeting {MeetingId}", session.MeetingId);

            var errorSession = session with { Status = MeetingStatus.Error };
            await _sessionStore.AddOrUpdateAsync(errorSession);
        }
    }
}
