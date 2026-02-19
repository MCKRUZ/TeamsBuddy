using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Infrastructure;

namespace TeamsMeetingAssistant.Tests.Unit.Services;

public class AzureOpenAIQuestionServiceTests
{
    private static IConfiguration BuildConfig(
        int maxTokens = 800,
        float temperature = 0.7f)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:MaxTokens"] = maxTokens.ToString(),
                ["AzureOpenAI:Temperature"] = temperature.ToString()
            })
            .Build();
    }

    private static AzureOpenAIQuestionService CreateService(
        IChatCompletionProvider provider,
        IConfiguration? config = null)
    {
        return new AzureOpenAIQuestionService(
            provider,
            config ?? BuildConfig(),
            NullLogger<AzureOpenAIQuestionService>.Instance);
    }

    private static QuestionGenerationContext EmptyContext(string meetingId = "mtg-1")
        => new(meetingId, "user@test.com", null, null, Array.Empty<KnowledgeChunk>());

    private static TranscriptSegment MakeSegment(string speaker, string content)
        => new(Guid.NewGuid().ToString(), speaker, speaker, content,
               DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(5));

    // ------------------------------------------------------------------
    // Empty transcript returns early without calling the provider
    // ------------------------------------------------------------------
    [Fact]
    public async Task GenerateQuestionsAsync_EmptyTranscript_ReturnsEmptyWithoutCallingProvider()
    {
        var mockProvider = new Mock<IChatCompletionProvider>(MockBehavior.Strict);
        // Strict mock: any call would throw

        var service = CreateService(mockProvider.Object);
        var result = await service.GenerateQuestionsAsync([], EmptyContext(), CancellationToken.None);

        Assert.Empty(result);
        mockProvider.VerifyNoOtherCalls();
    }

    // ------------------------------------------------------------------
    // Transcript text appears in the user message sent to the provider
    // ------------------------------------------------------------------
    [Fact]
    public async Task GenerateQuestionsAsync_IncludesTranscriptInPrompt()
    {
        string? capturedUserMessage = null;
        var mockProvider = new Mock<IChatCompletionProvider>();
        mockProvider
            .Setup(p => p.CompleteChatAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, float, CancellationToken>((_, user, _, _, _) =>
                capturedUserMessage = user)
            .ReturnsAsync("[]");

        var segments = new List<TranscriptSegment>
        {
            MakeSegment("Alice", "We need to discuss the budget overrun.")
        };

        var service = CreateService(mockProvider.Object);
        await service.GenerateQuestionsAsync(segments, EmptyContext(), CancellationToken.None);

        Assert.NotNull(capturedUserMessage);
        Assert.Contains("Alice", capturedUserMessage);
        Assert.Contains("budget overrun", capturedUserMessage);
    }

    // ------------------------------------------------------------------
    // Org knowledge chunks are included when present
    // ------------------------------------------------------------------
    [Fact]
    public async Task GenerateQuestionsAsync_IncludesOrgChunksWhenPresent()
    {
        string? capturedUserMessage = null;
        var mockProvider = new Mock<IChatCompletionProvider>();
        mockProvider
            .Setup(p => p.CompleteChatAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, float, CancellationToken>((_, user, _, _, _) =>
                capturedUserMessage = user)
            .ReturnsAsync("[]");

        var chunks = new List<KnowledgeChunk>
        {
            new("c1", "Q3 targets are 12% growth.", "strategy-doc.pdf", 0.9)
        };
        var context = new QuestionGenerationContext(
            "mtg-1", "user@test.com", null, null, chunks);
        var segments = new List<TranscriptSegment> { MakeSegment("Bob", "Revenue looks good.") };

        var service = CreateService(mockProvider.Object);
        await service.GenerateQuestionsAsync(segments, context, CancellationToken.None);

        Assert.NotNull(capturedUserMessage);
        Assert.Contains("strategy-doc.pdf", capturedUserMessage);
        Assert.Contains("Q3 targets", capturedUserMessage);
    }

    // ------------------------------------------------------------------
    // Valid JSON is parsed into QuestionSuggestion list
    // ------------------------------------------------------------------
    [Fact]
    public async Task GenerateQuestionsAsync_ValidJson_ReturnsQuestions()
    {
        const string fakeJson =
            "[{\"question\":\"Why is the budget overrun?\",\"rationale\":\"Key concern.\",\"category\":\"Deep-dive\",\"confidence\":0.9}," +
            "{\"question\":\"What are the next steps?\",\"rationale\":\"Action needed.\",\"category\":\"Follow-up\",\"confidence\":0.85}]";

        var mockProvider = new Mock<IChatCompletionProvider>();
        mockProvider
            .Setup(p => p.CompleteChatAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeJson);

        var service = CreateService(mockProvider.Object);
        var result = await service.GenerateQuestionsAsync(
            [MakeSegment("Alice", "Budget discussion.")],
            EmptyContext(),
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("Why is the budget overrun?", result[0].Question);
        Assert.Equal("Deep-dive", result[0].Category);
        Assert.Equal(0.9f, result[0].ConfidenceScore, precision: 2);
        Assert.Equal("Follow-up", result[1].Category);
    }

    // ------------------------------------------------------------------
    // JSON wrapped in markdown fences is still parsed
    // ------------------------------------------------------------------
    [Fact]
    public async Task GenerateQuestionsAsync_JsonWithMarkdownFence_StillParsed()
    {
        const string fenceWrapped =
            "```json\n[{\"question\":\"Q1?\",\"rationale\":\"R1\",\"category\":\"Summary\",\"confidence\":0.75}]\n```";

        var mockProvider = new Mock<IChatCompletionProvider>();
        mockProvider
            .Setup(p => p.CompleteChatAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fenceWrapped);

        var service = CreateService(mockProvider.Object);
        var result = await service.GenerateQuestionsAsync(
            [MakeSegment("Alice", "Some content.")],
            EmptyContext(),
            CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Q1?", result[0].Question);
    }

    // ------------------------------------------------------------------
    // Malformed JSON returns empty list (graceful degradation)
    // ------------------------------------------------------------------
    [Fact]
    public async Task GenerateQuestionsAsync_MalformedJson_ReturnsEmpty()
    {
        var mockProvider = new Mock<IChatCompletionProvider>();
        mockProvider
            .Setup(p => p.CompleteChatAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("not json at all");

        var service = CreateService(mockProvider.Object);
        var result = await service.GenerateQuestionsAsync(
            [MakeSegment("Bob", "Something.")],
            EmptyContext(),
            CancellationToken.None);

        Assert.Empty(result);
    }

    // ------------------------------------------------------------------
    // Provider exception is caught; empty list returned
    // ------------------------------------------------------------------
    [Fact]
    public async Task GenerateQuestionsAsync_ProviderThrows_ReturnsEmpty()
    {
        var mockProvider = new Mock<IChatCompletionProvider>();
        mockProvider
            .Setup(p => p.CompleteChatAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var service = CreateService(mockProvider.Object);
        var result = await service.GenerateQuestionsAsync(
            [MakeSegment("Alice", "Discussion.")],
            EmptyContext(),
            CancellationToken.None);

        Assert.Empty(result);
    }

    // ------------------------------------------------------------------
    // Cancellation token is forwarded to the provider
    // ------------------------------------------------------------------
    [Fact]
    public async Task GenerateQuestionsAsync_CancellationToken_ForwardedToProvider()
    {
        var cts = new CancellationTokenSource();
        CancellationToken? capturedToken = null;

        var mockProvider = new Mock<IChatCompletionProvider>();
        mockProvider
            .Setup(p => p.CompleteChatAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, float, CancellationToken>((_, _, _, _, ct) =>
                capturedToken = ct)
            .ReturnsAsync("[]");

        var service = CreateService(mockProvider.Object);
        await service.GenerateQuestionsAsync(
            [MakeSegment("Alice", "Discussion.")],
            EmptyContext(),
            cts.Token);

        Assert.NotNull(capturedToken);
        Assert.Equal(cts.Token, capturedToken!.Value);
    }
}
