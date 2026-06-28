using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

public class CollectibleAuthorityTests
{
    [Fact]
    public void ShineAuthority_RejectsDuplicateShine()
    {
        var authority = new ShineAuthority();
        Assert.True(authority.TryAccept(117));
        Assert.False(authority.TryAccept(117));
        Assert.True(authority.TryAccept(42));
    }

    [Fact]
    public void BlueCoinAuthority_RejectsDuplicateIndexPerCourse()
    {
        var authority = new BlueCoinAuthority();
        Assert.True(authority.TryAccept(1, 14));
        Assert.False(authority.TryAccept(1, 14));
        Assert.True(authority.TryAccept(1, 15));
        Assert.True(authority.TryAccept(2, 14));
    }
}
