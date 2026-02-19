using Microsoft.Extensions.Logging.Abstractions;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Infrastructure;

namespace TeamsMeetingAssistant.Tests.Unit.Services;

public class MockOpenAIQuestionServiceTests
{
    private static MockOpenAIQuestionService CreateService()
        => new(NullLogger<MockOpenAIQuestionService>.Instance);

    private static QuestionGenerationContext EmptyContext()
        => new("mtg-1", "user@test.com", null, null, Array.Empty<KnowledgeChunk>());

    private static TranscriptSegment MakeSegment(string speaker, string content)
        => new(Guid.NewGuid().ToString(), speaker, speaker, content,
               DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(3));

    [Fact]
    public async Task GenerateQuestionsAsync_ReturnsExactlyThreeQuestions()
    {
        var service = CreateService();
        var segments = new List<TranscriptSegment> { MakeSegment("Alice", "Let's review the results.") };

        var result = await service.GenerateQuestionsAsync(segments, EmptyContext(), CancellationToken.None);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GenerateQuestionsAsync_AllQuestionsHaveNonEmptyText()
    {
        var service = CreateService();
        var segments = new List<TranscriptSegment> { MakeSegment("Bob", "Revenue up 15%.") };

        var result = await service.GenerateQuestionsAsync(segments, EmptyContext(), CancellationToken.None);

        Assert.All(result, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Question));
            Assert.False(string.IsNullOrWhiteSpace(q.Rationale));
            Assert.False(string.IsNullOrWhiteSpace(q.Category));
            Assert.InRange(q.ConfidenceScore, 0f, 1f);
        });
    }

    [Fact]
    public async Task GenerateQuestionsAsync_RespectsEmptyTranscript()
    {
        // Mock service always generates regardless of transcript length
        var service = CreateService();

        var result = await service.GenerateQuestionsAsync([], EmptyContext(), CancellationToken.None);

        // MockOpenAIQuestionService still returns mock data even for empty input
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GenerateQuestionsAsync_CancellationToken_ThrowsWhenCancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = CreateService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateQuestionsAsync(
                [MakeSegment("Alice", "Text.")],
                EmptyContext(),
                cts.Token));
    }

    [Fact]
    public async Task GenerateQuestionsAsync_EachQuestionHasUniqueId()
    {
        var service = CreateService();
        var segments = new List<TranscriptSegment> { MakeSegment("Alice", "Some discussion.") };

        var result = await service.GenerateQuestionsAsync(segments, EmptyContext(), CancellationToken.None);

        var ids = result.Select(q => q.Id).Distinct();
        Assert.Equal(result.Count, ids.Count());
    }
}
