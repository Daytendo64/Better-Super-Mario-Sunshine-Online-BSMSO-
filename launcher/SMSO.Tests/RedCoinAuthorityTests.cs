using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

public class RedCoinAuthorityTests
{
    [Fact]
    public void AcceptCollected_AssignsAuthoritativeCountAndRejectsDuplicateIndex()
    {
        var authority = new RedCoinAuthority();

        var first = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 255, 0, 0x123456);
        Assert.True(authority.TryAcceptCollected(first, out var payload0, out var reserved, out var payload1));
        Assert.Equal(0, payload0 & 0xF);
        Assert.Equal(0x10, payload0 & 0xF0);
        Assert.Equal(0, reserved);
        Assert.Equal(0x123456u, payload1);
        Assert.Equal(0b0000_0001, authority.CollectedMask(2, 3));

        var duplicate = new WorldEventRequest(2, WorldEventType.RedCoinCollected, 2, 3, 255, 0, 0x123456);
        Assert.False(authority.TryAcceptCollected(duplicate, out _, out _, out _));

        var second = new WorldEventRequest(3, WorldEventType.RedCoinCollected, 2, 3, 255, 1, 0xABCDEF);
        Assert.True(authority.TryAcceptCollected(second, out payload0, out reserved, out payload1));
        Assert.Equal(1, payload0 & 0xF);
        Assert.Equal(0x20, payload0 & 0xF0);
        Assert.Equal(1, reserved);
        Assert.Equal(0xABCDEFu, payload1);
        Assert.Equal(0b0000_0011, authority.CollectedMask(2, 3));
    }

    [Fact]
    public void AcceptCollected_KeepsIndependentStatePerCourseEpisode()
    {
        var authority = new RedCoinAuthority();

        var bianco = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCollected(bianco, out _, out _, out _));

        var ricco = new WorldEventRequest(2, WorldEventType.RedCoinCollected, 3, 1, 0, 0, 0x200);
        Assert.True(authority.TryAcceptCollected(ricco, out _, out _, out _));
        Assert.Equal(0b0000_0001, authority.CollectedMask(2, 3));
        Assert.Equal(0b0000_0001, authority.CollectedMask(3, 1));
    }

    [Fact]
    public void AcceptCollected_AllowsMissingPositionWhenStableIndexPresent()
    {
        var authority = new RedCoinAuthority();
        var indexOnly = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 3, 2, 0);
        Assert.True(authority.TryAcceptCollected(indexOnly, out var payload0, out var reserved, out var payload1));
        Assert.Equal(2, reserved);
        Assert.Equal(0u, payload1);
        Assert.Equal(0x13, payload0);
    }

    [Fact]
    public void AcceptCollected_RejectsUnidentifiedCoin()
    {
        var authority = new RedCoinAuthority();
        var missingIdentity = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 0, 255, 0);
        Assert.False(authority.TryAcceptCollected(missingIdentity, out _, out _, out _));
    }

    [Fact]
    public void ResetStage_AllowsReCollectionAfterEpisodeRetry()
    {
        var authority = new RedCoinAuthority();

        var first = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCollected(first, out _, out _, out _));
        Assert.Equal(0b0000_0001, authority.CollectedMask(2, 3));

        authority.ResetStage(2, 3);
        Assert.Equal(0, authority.CollectedMask(2, 3));

        var retry = new WorldEventRequest(2, WorldEventType.RedCoinCollected, 2, 3, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCollected(retry, out var payload0, out _, out _));
        Assert.Equal(0x10, payload0 & 0xF0);
        Assert.Equal(0b0000_0001, authority.CollectedMask(2, 3));
    }
}
