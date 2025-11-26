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

        // Process sessions in parallel with max concurrency
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
        var qaEvaluationService = scope.ServiceProvider.GetRequiredService<IQAEvaluationService>();

        try
        {
            // Get new transcript segments
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

            var segmentsList = newSegments.ToList();
            
            // Determine interviewer: Use authenticated user display name from OBO token, fallback to first speaker
            var interviewerIdentifier = session.InterviewerDisplayName;
            var firstSpeakerName = session.FirstSpeakerId;
            
            if (string.IsNullOrEmpty(interviewerIdentifier) && string.IsNullOrEmpty(firstSpeakerName) && segmentsList.Any())
            {
                // Fallback: Use first speaker's name if no OBO token was provided
                // Note: SpeakerId in segments is actually the display name from VTT transcripts
                firstSpeakerName = segmentsList.First().SpeakerId;
                _logger.LogWarning("No interviewer from OBO token. Falling back to first speaker as interviewer: {SpeakerName} for meeting {MeetingId}", 
                    firstSpeakerName, session.MeetingId);
            }
            
            // Process segments and update conversation state
            var currentState = session.State;
            var currentQAExchange = session.CurrentQAExchange;
            
            foreach (var segment in segmentsList)
            {
                // Determine if this speaker is the interviewer
                // IMPORTANT: SpeakerId in segments is the display name from VTT (e.g., "Efrain Goyzueta")
                // We compare against InterviewerDisplayName or FirstSpeakerId (both are display names)
                bool isInterviewer = false;
                if (!string.IsNullOrEmpty(interviewerIdentifier))
                {
                    // Compare display names (case-insensitive)
                    isInterviewer = string.Equals(segment.SpeakerId, interviewerIdentifier, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(segment.SpeakerName, interviewerIdentifier, StringComparison.OrdinalIgnoreCase);
                }
                else if (!string.IsNullOrEmpty(firstSpeakerName))
                {
                    // Fallback: Compare with first speaker's display name
                    isInterviewer = string.Equals(segment.SpeakerId, firstSpeakerName, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(segment.SpeakerName, firstSpeakerName, StringComparison.OrdinalIgnoreCase);
                }
                
                var updatedSegment = segment with 
                { 
                    Role = isInterviewer ? SpeakerRole.Interviewer : SpeakerRole.Interviewee 
                };
                
                // Update state machine and Q&A exchange
                (currentState, currentQAExchange) = UpdateConversationStateWithQA(
                    currentState, 
                    currentQAExchange,
                    updatedSegment.Role, 
                    segment.Content);
                
                _logger.LogDebug("Segment from {Speaker} (Name: {SpeakerName}, ID: {SpeakerId}, Role: {Role}). State: {State}", 
                    segment.SpeakerName, segment.SpeakerName, segment.SpeakerId, updatedSegment.Role, currentState);
                
                // Stream segments to SignalR with roles assigned
                await signalRService.SendTranscriptUpdateAsync(session.MeetingId, updatedSegment);
            }

            // Check if we should evaluate the candidate's response
            bool shouldEvaluate = currentState == ConversationState.CandidateResponding && 
                                 currentQAExchange != null &&
                                 currentQAExchange.CandidateResponses.Any();

            // Add timeout: If candidate hasn't spoken in 10 seconds, evaluate what we have
            if (shouldEvaluate)
            {
                var timeSinceLastSegment = DateTimeOffset.UtcNow - segmentsList.Last().Timestamp;
                var evaluationTimeout = TimeSpan.FromSeconds(10);
                
                if (timeSinceLastSegment >= evaluationTimeout)
                {
                    _logger.LogInformation("Candidate response timeout. Evaluating accumulated responses for meeting {MeetingId}",
                        session.MeetingId);
                    
                    currentState = ConversationState.EvaluatingResponse;
                }
            }

            // Evaluate response and potentially generate follow-up questions
            if (currentState == ConversationState.EvaluatingResponse && currentQAExchange != null)
            {
                _logger.LogInformation("Evaluating candidate response for meeting {MeetingId}. Question: '{Question}', Responses: {Count}",
                    session.MeetingId, 
                    currentQAExchange.InterviewerQuestion.Substring(0, Math.Min(50, currentQAExchange.InterviewerQuestion.Length)),
                    currentQAExchange.CandidateResponses.Count);

                try
                {
                    var evaluation = await qaEvaluationService.EvaluateResponseAsync(
                        currentQAExchange.InterviewerQuestion,
                        currentQAExchange.CandidateResponses,
                        "Technical C# Interview",
                        cancellationToken);

                    _logger.LogInformation("Q&A Evaluation: IsAnswered={IsAnswered}, Quality={Quality}, NeedsFollowUp={NeedsFollowUp}, Reasoning: {Reasoning}",
                        evaluation.IsAnswered, evaluation.Quality, evaluation.NeedsFollowUp, evaluation.Reasoning);

                    // Generate follow-up questions only if the response warrants it
                    if (evaluation.NeedsFollowUp)
                    {
                        // Respect cooldown period
                        var timeSinceLastQuestion = session.LastQuestionGeneratedAt.HasValue
                            ? DateTimeOffset.UtcNow - session.LastQuestionGeneratedAt.Value
                            : TimeSpan.MaxValue;
                        
                        var questionCooldown = TimeSpan.FromSeconds(
                            _configuration.GetValue<int>("TranscriptProcessing:QuestionGenerationCooldownSeconds", 30));

                        if (timeSinceLastQuestion >= questionCooldown)
                        {
                            _logger.LogInformation("Generating follow-up questions for meeting {MeetingId}", session.MeetingId);
                            
                            // Get recent conversation context (last 10 segments)
                            var recentContext = segmentsList.TakeLast(10).ToList();
                            
                            var questions = await questionService.GenerateQuestionsAsync(
                                recentContext,
                                session.OrganizerEmail,
                                cancellationToken);

                            if (questions.Any())
                            {
                                await signalRService.SendQuestionSuggestionsAsync(session.MeetingId, questions);
                                _logger.LogInformation("Sent {Count} AI-generated follow-up questions for meeting {MeetingId}",
                                    questions.Count, session.MeetingId);
                            }
                            
                            // Reset state and clear current Q&A exchange
                            currentState = ConversationState.WaitingForInterviewerQuestion;
                            currentQAExchange = null;
                        }
                        else
                        {
                            _logger.LogDebug("Follow-up needed but in cooldown. Time since last: {TimeSince}s",
                                timeSinceLastQuestion.TotalSeconds);
                            // Still reset state even if in cooldown
                            currentState = ConversationState.WaitingForInterviewerQuestion;
                            currentQAExchange = null;
                        }
                    }
                    else
                    {
                        _logger.LogInformation("No follow-up needed. Reason: {Reasoning}", evaluation.Reasoning);
                        // Reset state
                        currentState = ConversationState.WaitingForInterviewerQuestion;
                        currentQAExchange = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to evaluate Q&A for meeting {MeetingId}", session.MeetingId);
                    // Reset on error
                    currentState = ConversationState.WaitingForInterviewerQuestion;
                    currentQAExchange = null;
                }
            }

            // Update session with latest state
            var updatedSession = session with
            {
                LastProcessedTime = segmentsList.Max(s => s.Timestamp),
                FirstSpeakerId = firstSpeakerName,
                InterviewerUserId = session.InterviewerUserId, // Keep original user ID from session
                State = currentState,
                CurrentQAExchange = currentQAExchange,
                LastQuestionGeneratedAt = (currentState == ConversationState.WaitingForInterviewerQuestion && 
                                          session.State == ConversationState.EvaluatingResponse)
                    ? DateTimeOffset.UtcNow 
                    : session.LastQuestionGeneratedAt
            };
            await _sessionStore.AddOrUpdateAsync(updatedSession);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing meeting {MeetingId}", session.MeetingId);

            // Update session status to error
            var errorSession = session with { Status = MeetingStatus.Error };
            await _sessionStore.AddOrUpdateAsync(errorSession);
        }
    }

    /// <summary>
    /// Updates conversation state and Q&A exchange based on who spoke and what they said
    /// </summary>
    private (ConversationState newState, QAExchange? qaExchange) UpdateConversationStateWithQA(
        ConversationState currentState,
        QAExchange? currentQAExchange,
        SpeakerRole speakerRole,
        string content)
    {
        return (currentState, speakerRole) switch
        {
            // Interviewer asks a question
            (ConversationState.WaitingForInterviewerQuestion, SpeakerRole.Interviewer) =>
                (ConversationState.InterviewerAsked, 
                 new QAExchange(content, new List<string>(), DateTimeOffset.UtcNow)),
            
            // Candidate starts responding
            (ConversationState.InterviewerAsked, SpeakerRole.Interviewee) =>
                (ConversationState.CandidateResponding,
                 currentQAExchange?.AddResponse(content) ?? currentQAExchange),
            
            // Candidate continues responding (accumulate)
            (ConversationState.CandidateResponding, SpeakerRole.Interviewee) =>
                (ConversationState.CandidateResponding,
                 currentQAExchange?.AddResponse(content) ?? currentQAExchange),
            
            // Interviewer interrupts or asks follow-up before evaluation
            (ConversationState.CandidateResponding, SpeakerRole.Interviewer) =>
                (ConversationState.InterviewerAsked,
                 new QAExchange(content, new List<string>(), DateTimeOffset.UtcNow)),
            
            // Default: no state change
            _ => (currentState, currentQAExchange)
        };
    }
}