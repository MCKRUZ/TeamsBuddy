using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Infrastructure;

namespace TeamsMeetingAssistant.Tests.Unit.Services;

public class NullOrgKnowledgeServiceTests
{
    private static readonly NullOrgKnowledgeService Service = new();

    [Fact]
    public async Task SearchAsync_AlwaysReturnsEmptyList()
    {
        var result = await Service.SearchAsync("any query", topK: 5);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_TopKIsIgnored_ReturnsEmpty()
    {
        var result = await Service.SearchAsync("query", topK: 100);

        Assert.Empty(result);
    }

    [Fact]
    public async Task IsAvailableAsync_ReturnsFalse()
    {
        var available = await Service.IsAvailableAsync();

        Assert.False(available);
    }

    [Fact]
    public async Task IndexDocumentAsync_CompletesWithoutThrowing()
    {
        var doc = new DocumentUpload("test.pdf", "application/pdf",
            new byte[] { 0x25, 0x50, 0x44, 0x46 }, false);

        var exception = await Record.ExceptionAsync(() =>
            Service.IndexDocumentAsync(doc));

        Assert.Null(exception);
    }

    [Fact]
    public async Task SearchAsync_CancellationToken_CompletesSuccessfully()
    {
        using var cts = new CancellationTokenSource();

        var result = await Service.SearchAsync("q", ct: cts.Token);

        Assert.Empty(result);
    }
}
