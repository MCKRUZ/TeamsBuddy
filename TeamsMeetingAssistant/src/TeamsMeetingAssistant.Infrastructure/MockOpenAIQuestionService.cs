using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace TeamsMeetingAssistant.Infrastructure;

public class MockOpenAIQuestionService : IQuestionGenerationService
{
    private readonly ILogger<MockOpenAIQuestionService> _logger;

    public MockOpenAIQuestionService(ILogger<MockOpenAIQuestionService> logger)
    {
        _logger = logger;
    }

    public async Task<List<QuestionSuggestion>> GenerateQuestionsAsync(List<TranscriptSegment> recentTranscript, QuestionGenerationContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("MOCK: Generating questions for {SegmentCount} transcript segments", recentTranscript.Count);

        await Task.Delay(200, cancellationToken);

        // Generate some mock question suggestions based on the transcript content
        var suggestions = new List<QuestionSuggestion>
        {
            new QuestionSuggestion(
                Guid.NewGuid().ToString(),
                "Could you elaborate on the 15% revenue increase? What were the key drivers?",
                "The speaker mentioned a 15% revenue increase but didn't provide details on the causes.",
                "Deep-dive",
                0.85f,
                DateTimeOffset.UtcNow
            ),
            new QuestionSuggestion(
                Guid.NewGuid().ToString(),
                "What are our main priorities for the next quarter based on these results?",
                "Helps move the conversation from results to actionable next steps.",
                "Follow-up",
                0.92f,
                DateTimeOffset.UtcNow
            ),
            new QuestionSuggestion(
                Guid.NewGuid().ToString(),
                "How does this compare to our competitors' performance?",
                "Provides context for the results by looking at the broader market.",
                "Clarification",
                0.78f,
                DateTimeOffset.UtcNow
            )
        };

        _logger.LogInformation("MOCK: Generated {Count} question suggestions", suggestions.Count);
        return suggestions;
    }
}