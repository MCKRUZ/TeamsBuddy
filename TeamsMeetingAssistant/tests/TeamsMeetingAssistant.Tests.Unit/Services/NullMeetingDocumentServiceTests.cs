using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Infrastructure;

namespace TeamsMeetingAssistant.Tests.Unit.Services;

public class NullMeetingDocumentServiceTests
{
    private static readonly NullMeetingDocumentService Service = new();

    private static MeetingSession MakeSession(string meetingId = "mtg-1")
        => new(meetingId, "organiser@test.com",
               DateTimeOffset.UtcNow, null,
               true, null, DateTimeOffset.UtcNow, MeetingStatus.Active);

    [Fact]
    public async Task InitialiseAssistantAsync_ReturnsSessionUnchanged()
    {
        var session = MakeSession("test-meeting");
        var docs = Array.Empty<DocumentUpload>();

        var result = await Service.InitialiseAssistantAsync(session, docs, CancellationToken.None);

        Assert.Equal(session.MeetingId, result.MeetingId);
        Assert.Equal(session.Status, result.Status);
        Assert.Equal(session.OrganizerEmail, result.OrganizerEmail);
    }

    [Fact]
    public async Task InitialiseAssistantAsync_DoesNotAlterAssistantId()
    {
        var session = MakeSession();
        var result = await Service.InitialiseAssistantAsync(session, [], CancellationToken.None);

        // NullMeetingDocumentService never sets an AssistantId
        Assert.Null(result.AssistantId);
        Assert.Null(result.ThreadId);
    }

    [Fact]
    public async Task AddDocumentsAsync_ReturnsEmptyFileIdList()
    {
        var doc = new DocumentUpload("report.pdf", "application/pdf",
            new byte[] { 0x25, 0x50, 0x44, 0x46 }, false);

        var result = await Service.AddDocumentsAsync("any-assistant-id", [doc], CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AddDocumentsAsync_MultipleDocuments_ReturnsEmpty()
    {
        var docs = Enumerable.Range(0, 5)
            .Select(i => new DocumentUpload($"doc{i}.pdf", "application/pdf",
                [0x25], false))
            .ToList();

        var result = await Service.AddDocumentsAsync("asst-id", docs, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CleanupAsync_CompletesWithoutThrowing()
    {
        var session = MakeSession();

        var exception = await Record.ExceptionAsync(() =>
            Service.CleanupAsync(session, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task CleanupAsync_WithCancellation_CompletesSuccessfully()
    {
        var session = MakeSession();
        using var cts = new CancellationTokenSource();

        var exception = await Record.ExceptionAsync(() =>
            Service.CleanupAsync(session, cts.Token));

        Assert.Null(exception);
    }
}
