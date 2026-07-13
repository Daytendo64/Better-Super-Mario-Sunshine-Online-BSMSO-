using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

public class NpcCleanAuthorityTests
{
    [Fact]
    public void AcceptCleaned_AssignsAuthoritativeCountAndRejectsDuplicateIndex()
    {
        var authority = new NpcCleanAuthority();

        var first = new WorldEventRequest(1, WorldEventType.NpcCleaned, 8, 5, 255, 0, 0x123456);
        Assert.True(authority.TryAcceptCleaned(first, out var payload0, out var reserved, out var payload1));
        Assert.Equal(0, payload0 & 0xF);
        Assert.Equal(0x10, payload0 & 0xF0);
        Assert.Equal(0, reserved);
        Assert.Equal(0x123456u, payload1);
        Assert.Equal(0b0000_0001, authority.CleanedMask(8, 5));

        var duplicate = new WorldEventRequest(2, WorldEventType.NpcCleaned, 8, 5, 255, 0, 0x123456);
        Assert.False(authority.TryAcceptCleaned(duplicate, out _, out _, out _));

        var second = new WorldEventRequest(3, WorldEventType.NpcCleaned, 8, 5, 255, 1, 0xABCDEF);
        Assert.True(authority.TryAcceptCleaned(second, out payload0, out reserved, out payload1));
        Assert.Equal(1, payload0 & 0xF);
        Assert.Equal(0x20, payload0 & 0xF0);
        Assert.Equal(1, reserved);
        Assert.Equal(0xABCDEFu, payload1);
        Assert.Equal(0b0000_0011, authority.CleanedMask(8, 5));
    }

    [Fact]
    public void AcceptCleaned_KeepsIndependentStatePerCourseEpisode()
    {
        var authority = new NpcCleanAuthority();

        var pianta = new WorldEventRequest(1, WorldEventType.NpcCleaned, 8, 5, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCleaned(pianta, out _, out _, out _));

        var other = new WorldEventRequest(2, WorldEventType.NpcCleaned, 8, 4, 0, 0, 0x200);
        Assert.True(authority.TryAcceptCleaned(other, out _, out _, out _));
        Assert.Equal(0b0000_0001, authority.CleanedMask(8, 5));
        Assert.Equal(0b0000_0001, authority.CleanedMask(8, 4));
    }

    [Fact]
    public void AcceptCleaned_RejectsUnidentifiedNpc()
    {
        var authority = new NpcCleanAuthority();
        var missingIdentity = new WorldEventRequest(1, WorldEventType.NpcCleaned, 8, 5, 0, 255, 0);
        Assert.False(authority.TryAcceptCleaned(missingIdentity, out _, out _, out _));
    }

    [Fact]
    public void ResetStage_AllowsReCleanAfterEpisodeRetry()
    {
        var authority = new NpcCleanAuthority();

        var first = new WorldEventRequest(1, WorldEventType.NpcCleaned, 8, 5, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCleaned(first, out _, out _, out _));
        Assert.Equal(0b0000_0001, authority.CleanedMask(8, 5));

        authority.ResetStage(8, 5);
        Assert.Equal(0, authority.CleanedMask(8, 5));

        var retry = new WorldEventRequest(2, WorldEventType.NpcCleaned, 8, 5, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCleaned(retry, out var payload0, out _, out _));
        Assert.Equal(0x10, payload0 & 0xF0);
    }

    [Fact]
    public void WorldEventRelay_IncludesNpcCleanedInDurableHistory()
    {
        var relay = new WorldEventRelay();
        relay.CreateWorldEvent(WorldEventType.NpcCleaned, 8, 5, 0x13, 3, 0xABCDEF);
        relay.CreateWorldEvent(WorldEventType.NpcReact, 8, 5, 1, 0, 0x111);

        Assert.Single(relay.History);
        Assert.Equal(WorldEventType.NpcCleaned, relay.History[0].Type);
        Assert.Equal(3, relay.History[0].Reserved);
    }
}
