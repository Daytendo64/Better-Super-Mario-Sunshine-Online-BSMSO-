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
        Assert.True(authority.TryAcceptCollected(first, out var payload0, out var reserved, out var payload1,
            out var payload2));
        Assert.Equal(0, payload0 & 0xF);
        Assert.Equal(0x10, payload0 & 0xF0);
        Assert.Equal(0, reserved);
        Assert.Equal(0b0000_0001u, payload1);
        Assert.Equal(0u, payload2);
        Assert.Equal(0b0000_0001, authority.CollectedMask(2, 3));

        var duplicate = new WorldEventRequest(2, WorldEventType.RedCoinCollected, 2, 3, 255, 0, 0x123456);
        Assert.False(authority.TryAcceptCollected(duplicate, out _, out _, out _, out _));

        var second = new WorldEventRequest(3, WorldEventType.RedCoinCollected, 2, 3, 255, 1, 0xABCDEF);
        Assert.True(authority.TryAcceptCollected(second, out payload0, out reserved, out payload1, out _));
        Assert.Equal(1, payload0 & 0xF);
        Assert.Equal(0x20, payload0 & 0xF0);
        Assert.Equal(1, reserved);
        Assert.Equal(0b0000_0011u, payload1);
        Assert.Equal(0b0000_0011, authority.CollectedMask(2, 3));
    }

    [Fact]
    public void AcceptCollected_KeepsIndependentStatePerCourseEpisode()
    {
        var authority = new RedCoinAuthority();

        var bianco = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCollected(bianco, out _, out _, out _, out _));

        var ricco = new WorldEventRequest(2, WorldEventType.RedCoinCollected, 3, 1, 0, 0, 0x200);
        Assert.True(authority.TryAcceptCollected(ricco, out _, out _, out _, out _));
        Assert.Equal(0b0000_0001, authority.CollectedMask(2, 3));
        Assert.Equal(0b0000_0001, authority.CollectedMask(3, 1));
    }

    [Fact]
    public void AcceptCollected_AllowsMissingPositionWhenStableIndexPresent()
    {
        var authority = new RedCoinAuthority();
        var indexOnly = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 3, 2, 0);
        Assert.True(authority.TryAcceptCollected(indexOnly, out var payload0, out var reserved, out var payload1,
            out _));
        Assert.Equal(2, reserved);
        Assert.Equal(0b0000_0100u, payload1);
        Assert.Equal(0x13, payload0);
    }

    [Fact]
    public void AcceptCollected_RejectsUnidentifiedCoin()
    {
        var authority = new RedCoinAuthority();
        var missingIdentity = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 0, 255, 0);
        Assert.False(authority.TryAcceptCollected(missingIdentity, out _, out _, out _, out _));
    }

    [Fact]
    public void ResetStage_AllowsReCollectionAfterEpisodeRetry()
    {
        var authority = new RedCoinAuthority();

        var first = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 2, 3, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCollected(first, out _, out _, out _, out _));
        Assert.Equal(0b0000_0001, authority.CollectedMask(2, 3));

        authority.ResetStage(2, 3);
        Assert.Equal(0, authority.CollectedMask(2, 3));

        var retry = new WorldEventRequest(2, WorldEventType.RedCoinCollected, 2, 3, 0, 0, 0x100);
        Assert.True(authority.TryAcceptCollected(retry, out var payload0, out _, out _, out _));
        Assert.Equal(0x10, payload0 & 0xF0);
        Assert.Equal(0b0000_0001, authority.CollectedMask(2, 3));
    }

    [Fact]
    public void IsMissionResetRequest_DetectsSoloDeathSentinel()
    {
        var reset = new WorldEventRequest(1, WorldEventType.RedCoinCollected, 23, 0, 0,
            RedCoinAuthority.MissionResetReserved, 0);
        Assert.True(RedCoinAuthority.IsMissionResetRequest(reset));
        Assert.False(authorityAcceptsReset(reset));

        var collect = new WorldEventRequest(2, WorldEventType.RedCoinCollected, 23, 0, 0, 0, 0);
        Assert.False(RedCoinAuthority.IsMissionResetRequest(collect));
    }

    private static bool authorityAcceptsReset(WorldEventRequest reset)
    {
        var authority = new RedCoinAuthority();
        return authority.TryAcceptCollected(reset, out _, out _, out _, out _);
    }

    [Fact]
    public void MissionReset_ClearsAuthoritySoSoloDeathCanRecollect()
    {
        var authority = new RedCoinAuthority();
        Assert.True(authority.TryAcceptCollected(
            new WorldEventRequest(1, WorldEventType.RedCoinCollected, 23, 0, 0, 2, 0), out _, out _,
            out _, out _));
        Assert.Equal(0b0000_0100, authority.CollectedMask(23, 0));

        authority.ResetStage(23, 0);
        Assert.Equal(0, authority.CollectedMask(23, 0));
        Assert.True(authority.TryAcceptCollected(
            new WorldEventRequest(2, WorldEventType.RedCoinCollected, 23, 0, 0, 2, 0), out var payload0,
            out _, out var payload1, out _));
        Assert.Equal(0b0000_0100u, payload1);
        Assert.Equal(0x10, payload0 & 0xF0);
    }

    /// <summary>
    /// Red Coin Field (area 23) and other deferred-spawn stages publish indices as drops
    /// appear (0..7). Authority must accept sparse mid-mission indices and accumulate mask.
    /// </summary>
    [Fact]
    public void AcceptCollected_AccumulatesSparseIndicesForDeferredSpawnStages()
    {
        var authority = new RedCoinAuthority();
        Assert.True(authority.TryAcceptCollected(
            new WorldEventRequest(1, WorldEventType.RedCoinCollected, 23, 0, 0, 0, 0), out _, out _, out _,
            out _));
        Assert.True(authority.TryAcceptCollected(
            new WorldEventRequest(2, WorldEventType.RedCoinCollected, 23, 0, 1, 1, 0), out _, out _, out _,
            out _));
        Assert.True(authority.TryAcceptCollected(
            new WorldEventRequest(3, WorldEventType.RedCoinCollected, 23, 0, 2, 2, 0, 0x00ABCDEFu),
            out var payload0, out var reserved, out var payload1, out var payload2));

        Assert.Equal(2, reserved);
        Assert.Equal(0b0000_0111u, payload1);
        Assert.Equal(0x30, payload0 & 0xF0);
        Assert.Equal(0x00ABCDEFu, payload2);
        Assert.Equal(0x00ABCDEFu, authority.PackedPos(23, 0, 2));
        Assert.Equal(0b0000_0111, authority.CollectedMask(23, 0));
    }

    [Fact]
    public void AcceptCollected_CoalescesSirenaCasinoMissionAndCatalogEpisodes()
    {
        var authority = new RedCoinAuthority();
        // Module publishes director mission 3; roster occupancy uses catalog 0.
        Assert.True(authority.TryAcceptCollected(
            new WorldEventRequest(1, WorldEventType.RedCoinCollected, SirenaCasinoMapping.AreaId, 3, 0, 1, 0x100),
            out _, out _, out _, out _));
        Assert.Equal(0b0000_0010, authority.CollectedMask(SirenaCasinoMapping.AreaId, 3));
        Assert.Equal(0b0000_0010, authority.CollectedMask(SirenaCasinoMapping.AreaId, 0));
        Assert.False(authority.TryAcceptCollected(
            new WorldEventRequest(2, WorldEventType.RedCoinCollected, SirenaCasinoMapping.AreaId, 0, 0, 1, 0x100),
            out _, out _, out _, out _));
        Assert.True(authority.AllStages.ContainsKey((SirenaCasinoMapping.AreaId, 0)));
        Assert.False(authority.AllStages.ContainsKey((SirenaCasinoMapping.AreaId, 3)));

        authority.ResetStage(SirenaCasinoMapping.AreaId, 3);
        Assert.Equal(0, authority.CollectedMask(SirenaCasinoMapping.AreaId, 0));
    }

    [Fact]
    public void AcceptCollected_CoalescesSirenaHotelRedCoinCatalog()
    {
        var authority = new RedCoinAuthority();
        // Hotel red coins: director mission 4 maps to catalog episode 7.
        Assert.True(authority.TryAcceptCollected(
            new WorldEventRequest(1, WorldEventType.RedCoinCollected, SirenaHotelInteriorMapping.AreaId, 4, 0, 0,
                0x200),
            out _, out _, out _, out _));
        Assert.Equal(0b0000_0001, authority.CollectedMask(SirenaHotelInteriorMapping.AreaId, 4));
        Assert.Equal(0b0000_0001, authority.CollectedMask(SirenaHotelInteriorMapping.AreaId, 7));
        Assert.True(authority.AllStages.ContainsKey((SirenaHotelInteriorMapping.AreaId, 7)));
    }
}
