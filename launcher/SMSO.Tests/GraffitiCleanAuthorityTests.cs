using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

public class GraffitiCleanAuthorityTests
{
    [Fact]
    public void TryAcceptCleaned_AcceptsUniqueCellsAndRejectsDuplicates()
    {
        var authority = new GraffitiCleanAuthority();
        var first = new WorldEventRequest(1, WorldEventType.GraffitiCleaned, 2, 0, 8, 0, 0x123456,
            PackCell(1, 2, 3));

        Assert.True(authority.TryAcceptCleaned(first, out var p0, out _, out var p1, out var p2));
        Assert.Equal(8, p0);
        Assert.Equal(0x123456u, p1);
        Assert.Equal(PackCell(1, 2, 3), p2);

        var duplicate = new WorldEventRequest(2, WorldEventType.GraffitiCleaned, 2, 0, 8, 0, 0x123456,
            PackCell(1, 2, 3));
        Assert.False(authority.TryAcceptCleaned(duplicate, out _, out _, out _, out _));

        // Same XZ, different Y — wall spray must not collapse into one cell.
        var second = new WorldEventRequest(3, WorldEventType.GraffitiCleaned, 2, 0, 10, 0, 0xABCDEF,
            PackCell(1, 4, 3));
        Assert.True(authority.TryAcceptCleaned(second, out _, out _, out _, out _));
        Assert.Equal(1, authority.AllStages.Count);
        Assert.Equal(2, authority.AllStages[(2, 0)].Count);
    }

    [Fact]
    public void PackCell_RoundTripsSignedAxesAndSetsValidBit()
    {
        var packed = PackCell(-5, 9, 12);
        Assert.True((packed & GraffitiCleanAuthority.CellPackValidBit) != 0);
        Assert.True(GraffitiCleanAuthority.TryUnpackCell(packed, out var x, out var y, out var z));
        Assert.Equal((short)-5, x);
        Assert.Equal((short)9, y);
        Assert.Equal((short)12, z);

        Assert.False(GraffitiCleanAuthority.TryUnpackCell(0, out _, out _, out _));
        // Legacy XZ-only pack (no valid bit) must not parse as 3D.
        Assert.False(GraffitiCleanAuthority.TryUnpackCell(0x00030001u, out _, out _, out _));
    }

    [Fact]
    public void TryAcceptCleaned_Derives3DCellFromPackedPosWhenPayload2Invalid()
    {
        var authority = new GraffitiCleanAuthority();
        // packCollectibleWorldPos: 10-bit each with bias 256, scale 16.
        // x=32 → cellX=1, y=96 → cellY=3, z=64 → cellZ=2
        const float scale = 16f;
        const float bias = 256f;
        var ex = (uint)(32f / scale + bias);
        var ey = (uint)(96f / scale + bias);
        var ez = (uint)(64f / scale + bias);
        var packedPos = (ex & 0x3FFu) | ((ey & 0x3FFu) << 10) | ((ez & 0x3FFu) << 20);

        Assert.True(authority.TryAcceptCleaned(
            new WorldEventRequest(1, WorldEventType.GraffitiCleaned, 1, 0, 8, 0, packedPos, 0),
            out _, out _, out _, out var payload2));
        Assert.True(GraffitiCleanAuthority.TryUnpackCell(payload2, out var cx, out var cy, out var cz));
        Assert.Equal((short)1, cx);
        Assert.Equal((short)3, cy);
        Assert.Equal((short)2, cz);
    }

    [Fact]
    public void ResetStage_ClearsOnlyMatchingStage()
    {
        var authority = new GraffitiCleanAuthority();
        Assert.True(authority.TryAcceptCleaned(
            new WorldEventRequest(1, WorldEventType.GraffitiCleaned, 8, 2, 8, 0, 0x100, PackCell(0, 0, 0)),
            out _, out _, out _, out _));
        Assert.True(authority.TryAcceptCleaned(
            new WorldEventRequest(2, WorldEventType.GraffitiCleaned, 8, 3, 8, 0, 0x200, PackCell(0, 0, 0)),
            out _, out _, out _, out _));

        authority.ResetStage(8, 2);
        Assert.False(authority.AllStages.ContainsKey((8, 2)));
        Assert.True(authority.AllStages.ContainsKey((8, 3)));
    }

    [Fact]
    public void TryAcceptCleaned_RejectsWhenStageCapReached()
    {
        var authority = new GraffitiCleanAuthority();
        for (short i = 0; i < GraffitiCleanAuthority.MaxCellsPerStage; i++)
        {
            Assert.True(authority.TryAcceptCleaned(
                new WorldEventRequest((ushort)(i + 1), WorldEventType.GraffitiCleaned, 1, 0, 8, 0, 1u,
                    PackCell(i, 0, 0)),
                out _, out _, out _, out _));
        }

        Assert.False(authority.TryAcceptCleaned(
            new WorldEventRequest(999, WorldEventType.GraffitiCleaned, 1, 0, 8, 0, 1u,
                PackCell(200, 0, 0)),
            out _, out _, out _, out _));
    }

    [Fact]
    public void TryAcceptCleaned_PreservesWallFlagsAndRelaysFinishingOnKnownCell()
    {
        var authority = new GraffitiCleanAuthority();
        var first = new WorldEventRequest(1, WorldEventType.GraffitiCleaned, 1, 0, 16,
            GraffitiCleanAuthority.ReservedWall, 0x111, PackCell(5, 6, 7));
        Assert.True(authority.TryAcceptCleaned(first, out _, out var reserved, out _, out _));
        Assert.Equal(GraffitiCleanAuthority.ReservedWall, reserved);

        var finishing = new WorldEventRequest(2, WorldEventType.GraffitiCleaned, 1, 0, 24,
            (byte)(GraffitiCleanAuthority.ReservedWall | GraffitiCleanAuthority.ReservedFinishing),
            0x222, PackCell(5, 6, 7));
        Assert.True(authority.TryAcceptCleaned(finishing, out var p0, out reserved, out var p1, out _));
        Assert.Equal(24, p0);
        Assert.Equal(0x222u, p1);
        Assert.Equal(
            (byte)(GraffitiCleanAuthority.ReservedWall | GraffitiCleanAuthority.ReservedFinishing),
            reserved);
        // Finishing must not grow the set.
        Assert.Equal(1, authority.AllStages[(1, StoryFlagAuthority.PlazaHubEpisode)].Count);
    }

    [Fact]
    public void TryAcceptCleaned_CoalescesPlazaEpisodesIntoHubBucket()
    {
        var authority = new GraffitiCleanAuthority();
        Assert.True(authority.TryAcceptCleaned(
            new WorldEventRequest(1, WorldEventType.GraffitiCleaned, 1, 8, 8, 0, 0x100, PackCell(1, 2, 3)),
            out _, out _, out _, out _));
        // Same cell from a different plaza episode must hit the hub bucket and reject.
        Assert.False(authority.TryAcceptCleaned(
            new WorldEventRequest(2, WorldEventType.GraffitiCleaned, 1, 0, 8, 0, 0x100, PackCell(1, 2, 3)),
            out _, out _, out _, out _));
        Assert.True(authority.AllStages.ContainsKey((1, StoryFlagAuthority.PlazaHubEpisode)));
        Assert.False(authority.AllStages.ContainsKey((1, 8)));
        Assert.Equal(1, authority.AllStages[(1, StoryFlagAuthority.PlazaHubEpisode)].Count);

        // Distinct cell under another plaza episode still grows the hub set.
        Assert.True(authority.TryAcceptCleaned(
            new WorldEventRequest(3, WorldEventType.GraffitiCleaned, 1, 7, 8, 0, 0x200, PackCell(4, 5, 6)),
            out _, out _, out _, out _));
        Assert.Equal(2, authority.AllStages[(1, StoryFlagAuthority.PlazaHubEpisode)].Count);

        authority.ResetStage(1, 0);
        Assert.False(authority.AllStages.ContainsKey((1, StoryFlagAuthority.PlazaHubEpisode)));
    }

    [Fact]
    public void TryAcceptCleaned_CoalescesSirenaCasinoMissionAndCatalogEpisodes()
    {
        var authority = new GraffitiCleanAuthority();
        Assert.True(authority.TryAcceptCleaned(
            new WorldEventRequest(1, WorldEventType.GraffitiCleaned, SirenaCasinoMapping.AreaId, 3, 8, 0, 0x100,
                PackCell(1, 2, 3)),
            out _, out _, out _, out _));
        Assert.False(authority.TryAcceptCleaned(
            new WorldEventRequest(2, WorldEventType.GraffitiCleaned, SirenaCasinoMapping.AreaId, 0, 8, 0, 0x100,
                PackCell(1, 2, 3)),
            out _, out _, out _, out _));
        Assert.True(authority.AllStages.ContainsKey((SirenaCasinoMapping.AreaId, 0)));
        Assert.False(authority.AllStages.ContainsKey((SirenaCasinoMapping.AreaId, 3)));
    }

    private static uint PackCell(short cellX, short cellY, short cellZ) =>
        GraffitiCleanAuthority.PackCell(cellX, cellY, cellZ);
}
