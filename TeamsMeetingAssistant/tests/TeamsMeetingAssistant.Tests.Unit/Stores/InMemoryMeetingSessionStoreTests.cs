using Microsoft.Extensions.Logging.Abstractions;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Infrastructure;

namespace TeamsMeetingAssistant.Tests.Unit.Stores;

public class InMemoryMeetingSessionStoreTests
{
    private static InMemoryMeetingSessionStore CreateStore()
        => new(NullLogger<InMemoryMeetingSessionStore>.Instance);

    private static MeetingSession MakeSession(
        string meetingId = "mtg-1",
        MeetingStatus status = MeetingStatus.Active)
        => new(meetingId, "organiser@test.com",
               DateTimeOffset.UtcNow, null,
               true, null, DateTimeOffset.UtcNow, status);

    // ------------------------------------------------------------------
    // Add + Get round-trip
    // ------------------------------------------------------------------
    [Fact]
    public async Task AddOrUpdateAsync_ThenGetAsync_ReturnsSameSession()
    {
        var store = CreateStore();
        var session = MakeSession("meeting-abc");

        await store.AddOrUpdateAsync(session);
        var retrieved = await store.GetAsync("meeting-abc");

        Assert.NotNull(retrieved);
        Assert.Equal("meeting-abc", retrieved!.MeetingId);
        Assert.Equal(MeetingStatus.Active, retrieved.Status);
    }

    [Fact]
    public async Task GetAsync_NonExistentMeeting_ReturnsNull()
    {
        var store = CreateStore();

        var result = await store.GetAsync("does-not-exist");

        Assert.Null(result);
    }

    // ------------------------------------------------------------------
    // Update replaces the existing session
    // ------------------------------------------------------------------
    [Fact]
    public async Task AddOrUpdateAsync_UpdatesExistingSession()
    {
        var store = CreateStore();
        var original = MakeSession("mtg-1", MeetingStatus.Active);
        await store.AddOrUpdateAsync(original);

        var updated = original with { Status = MeetingStatus.Completed };
        await store.AddOrUpdateAsync(updated);

        var result = await store.GetAsync("mtg-1");

        Assert.NotNull(result);
        Assert.Equal(MeetingStatus.Completed, result!.Status);
    }

    // ------------------------------------------------------------------
    // GetActiveSessionsAsync filters by status
    // ------------------------------------------------------------------
    [Fact]
    public async Task GetActiveSessionsAsync_OnlyReturnsActiveSessions()
    {
        var store = CreateStore();
        await store.AddOrUpdateAsync(MakeSession("active-1", MeetingStatus.Active));
        await store.AddOrUpdateAsync(MakeSession("active-2", MeetingStatus.Active));
        await store.AddOrUpdateAsync(MakeSession("completed-1", MeetingStatus.Completed));
        await store.AddOrUpdateAsync(MakeSession("pending-1", MeetingStatus.Pending));

        var active = (await store.GetActiveSessionsAsync()).ToList();

        Assert.Equal(2, active.Count);
        Assert.All(active, s => Assert.Equal(MeetingStatus.Active, s.Status));
    }

    [Fact]
    public async Task GetActiveSessionsAsync_EmptyStore_ReturnsEmptyEnumerable()
    {
        var store = CreateStore();

        var active = await store.GetActiveSessionsAsync();

        Assert.Empty(active);
    }

    // ------------------------------------------------------------------
    // Remove deletes the session
    // ------------------------------------------------------------------
    [Fact]
    public async Task RemoveAsync_RemovesSession()
    {
        var store = CreateStore();
        await store.AddOrUpdateAsync(MakeSession("to-remove"));

        await store.RemoveAsync("to-remove");
        var result = await store.GetAsync("to-remove");

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_NonExistentKey_DoesNotThrow()
    {
        var store = CreateStore();

        var exception = await Record.ExceptionAsync(() => store.RemoveAsync("ghost"));

        Assert.Null(exception);
    }

    // ------------------------------------------------------------------
    // Multiple concurrent sessions
    // ------------------------------------------------------------------
    [Fact]
    public async Task Store_CanHoldMultipleDifferentSessions()
    {
        var store = CreateStore();
        for (int i = 0; i < 10; i++)
            await store.AddOrUpdateAsync(MakeSession($"mtg-{i}"));

        var all = await store.GetAllSessionsAsync();

        Assert.Equal(10, all.Count());
    }
}
