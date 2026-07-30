using SMSO.Net;
using SMSO.Server;
using Xunit;

namespace SMSO.Tests;

/// <summary>
/// Graffiti/goop sync is permanently disabled. Authority always rejects;
/// pack helpers remain for wire-compat tooling.
/// </summary>
public class GraffitiCleanAuthorityTests
{
    [Fact]
    public void TryAcceptCleaned_AlwaysRejects()
    {
        var authority = new GraffitiCleanAuthority();
        var first = new WorldEventRequest(1, WorldEventType.GraffitiCleaned, 2, 0, 8, 0, 0x123456,
            GraffitiCleanAuthority.PackCell(1, 2, 3));
        Assert.False(authority.TryAcceptCleaned(first, out _, out _, out _, out _));
        Assert.Empty(authority.AllStages);
    }

    [Fact]
    public void PackCell_RoundTrips()
    {
        var packed = GraffitiCleanAuthority.PackCell(10, -5, 20);
        Assert.True((packed & GraffitiCleanAuthority.CellPackValidBit) != 0);
        Assert.True(GraffitiCleanAuthority.TryUnpackCell(packed, out var x, out var y, out var z));
        Assert.Equal(10, x);
        Assert.Equal(-5, y);
        Assert.Equal(20, z);
    }

    [Fact]
    public void IsDurable_ExcludesGraffitiCleaned()
    {
        Assert.False(WorldEventRelay.IsDurable(WorldEventType.GraffitiCleaned));
        Assert.True(WorldEventRelay.IsDurable(WorldEventType.ShineCollected));
        Assert.True(WorldEventRelay.IsDurable(WorldEventType.StoryFlag));
    }
}
