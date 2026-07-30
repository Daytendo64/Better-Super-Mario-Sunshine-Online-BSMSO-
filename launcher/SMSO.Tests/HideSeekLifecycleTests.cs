using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

/// <summary>
/// Hide &amp; Seek round lifecycle: mode switches, slot reclaim, and rounds that would
/// otherwise never end because the players who could end them disconnected.
/// </summary>
public sealed class HideSeekLifecycleTests
{
    private static HideSeekService NewService() => new GameServer(new LevelCatalog()).HideSeek;

    private static Dictionary<byte, HideSeekRole> Roles(params (byte Slot, HideSeekRole Role)[] roles)
    {
        var map = new Dictionary<byte, HideSeekRole>();
        foreach (var (slot, role) in roles)
            map[slot] = role;
        return map;
    }

    private static void Join(HideSeekService service, params (byte Slot, string Name)[] players)
    {
        foreach (var (slot, name) in players)
            service.OnPlayerJoined(slot, name);
    }

    [Fact]
    public void SetGameModeNormal_ClearsRoundState_SoTheNextStartTagStillGetsHideGrace()
    {
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        service.SetRoles(Roles((0, HideSeekRole.Seeker), (1, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));
        Assert.True(service.CurrentState.GraceActive);

        service.SetGameMode(GameMode.Normal);
        var cleared = service.CurrentState;
        Assert.False(cleared.TagActive);
        Assert.False(cleared.GraceActive);
        Assert.Equal(0u, cleared.RoundStartMs);
        Assert.All(cleared.Roles, role => Assert.Equal(HideSeekRole.Hider, role));

        // Hide & Seek → Normal → Hide & Seek used to be treated as a resume, which skipped
        // the whole hide grace on the next Start Tag.
        service.SetGameMode(GameMode.HideSeek);
        service.SetRoles(Roles((0, HideSeekRole.Seeker), (1, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));

        var restarted = service.CurrentState;
        Assert.True(restarted.TagActive);
        Assert.True(restarted.GraceActive);
        Assert.True(restarted.GraceRemainingMs > 0);
    }

    [Fact]
    public void StopThenStartTag_StillResumesWithoutRegrantingGrace()
    {
        // Guard for the resume path the round-state cleanup must not break.
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        service.SetRoles(Roles((0, HideSeekRole.Seeker), (1, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));
        service.StopTag();
        Assert.True(service.TryStartTag(out _));

        var state = service.CurrentState;
        Assert.True(state.TagActive);
        Assert.False(state.GraceActive);
    }

    [Fact]
    public void SlotReclaimedByADifferentPlayer_StartsAsHider()
    {
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        Join(service, (0, "Alice"), (1, "Bob"), (2, "Carol"), (3, "Dave"));
        service.SetRoles(Roles(
            (0, HideSeekRole.Seeker), (1, HideSeekRole.Seeker),
            (2, HideSeekRole.Hider), (3, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));

        service.OnPlayerDisconnected(1, "Bob");
        Assert.True(service.CurrentState.TagActive);

        service.OnPlayerJoined(1, "Newcomer");
        Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);
        Assert.True(service.CurrentState.TagActive);
    }

    [Fact]
    public void SameNameReconnect_KeepsTheSeekerRole()
    {
        // The 30s slot-restore window exists so a dropped seeker comes back as a seeker.
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        Join(service, (0, "Alice"), (1, "Bob"), (2, "Carol"), (3, "Dave"));
        service.SetRoles(Roles(
            (0, HideSeekRole.Seeker), (1, HideSeekRole.Seeker),
            (2, HideSeekRole.Hider), (3, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));

        service.OnPlayerDisconnected(1, "Bob");
        service.OnPlayerJoined(1, "bob");
        Assert.Equal(HideSeekRole.Seeker, service.CurrentState.Roles[1]);
    }

    [Fact]
    public void LastHiderDisconnecting_CompletesTheRound()
    {
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        Join(service, (0, "Seeker"), (1, "Hider"));
        service.SetRoles(Roles((0, HideSeekRole.Seeker), (1, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));

        service.OnPlayerDisconnected(1, "Hider");

        // Same fanfare + role reset path as a normal "all hiders found".
        var state = service.CurrentState;
        Assert.False(state.TagActive);
        Assert.True((state.Flags & GameModeFlags.RoundFanfare) != 0);
        Assert.All(state.Roles, role => Assert.Equal(HideSeekRole.Hider, role));
    }

    [Fact]
    public void LastSeekerDisconnecting_StopsTheTag()
    {
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        Join(service, (0, "Seeker"), (1, "HiderOne"), (2, "HiderTwo"));
        service.SetRoles(Roles(
            (0, HideSeekRole.Seeker), (1, HideSeekRole.Hider), (2, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));

        service.OnPlayerDisconnected(0, "Seeker");

        // Nobody left who can tag — stop rather than run forever, but this is not a win.
        var state = service.CurrentState;
        Assert.False(state.TagActive);
        Assert.False(state.GraceActive);
        Assert.True((state.Flags & GameModeFlags.RoundFanfare) == 0);
        Assert.Equal(HideSeekRole.Hider, state.Roles[1]);
        Assert.Equal(HideSeekRole.Hider, state.Roles[2]);
    }

    [Fact]
    public void DisconnectWithPlayersLeftOnBothSides_KeepsTheTagRunning()
    {
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        Join(service, (0, "Seeker"), (1, "HiderOne"), (2, "HiderTwo"));
        service.SetRoles(Roles(
            (0, HideSeekRole.Seeker), (1, HideSeekRole.Hider), (2, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));

        service.OnPlayerDisconnected(2, "HiderTwo");
        Assert.True(service.CurrentState.TagActive);
    }

    [Fact]
    public void EveryoneDisconnecting_StopsTheTag()
    {
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        Join(service, (0, "Seeker"), (1, "Hider"));
        service.SetRoles(Roles((0, HideSeekRole.Seeker), (1, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));

        service.OnPlayerDisconnected(0, "Seeker");
        service.OnPlayerDisconnected(1, "Hider");
        Assert.False(service.CurrentState.TagActive);
    }

    [Fact]
    public void TagEventId_RestartsAtOneForEveryRound()
    {
        var service = NewService();
        service.SetGameMode(GameMode.HideSeek);
        Join(service, (0, "Seeker"), (1, "HiderOne"), (2, "HiderTwo"), (3, "HiderThree"));
        service.SetRoles(Roles(
            (0, HideSeekRole.Seeker), (1, HideSeekRole.Hider),
            (2, HideSeekRole.Hider), (3, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));
        Assert.Equal((byte)0, service.CurrentState.TagEventId);
        service.ExpireProximityTagImmunityForTests();

        var dead = new PlayerSnapshot { Connected = 1, VfxFlags = (ushort)VfxFlags.Dead };
        service.ProcessHiderDeath(1, dead);
        Assert.Equal((byte)1, service.CurrentState.TagEventId);

        // Stop/start and reset must not let a client mistake a reused id for one it applied.
        service.StopTag();
        Assert.Equal((byte)0, service.CurrentState.TagEventId);
        Assert.True(service.TryStartTag(out _));
        Assert.Equal((byte)0, service.CurrentState.TagEventId);
        service.ExpireProximityTagImmunityForTests();

        service.ProcessHiderDeath(2, dead);
        Assert.Equal((byte)1, service.CurrentState.TagEventId);

        service.ResetTag();
        Assert.Equal((byte)0, service.CurrentState.TagEventId);
    }

    [Fact]
    public void GraceDurationChange_AppliesToTheNextStartTagOnly()
    {
        var service = NewService();
        service.StartTagGraceDurationMs = 15_000;
        service.SetGameMode(GameMode.HideSeek);
        service.SetRoles(Roles((0, HideSeekRole.Seeker), (1, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));

        var before = service.CurrentState.GraceRemainingMs;
        service.StartTagGraceDurationMs = 60_000;
        var after = service.CurrentState.GraceRemainingMs;

        // An in-flight countdown is never re-armed (or truncated) under the players.
        Assert.True(after <= before);
        Assert.True(after > 10_000);

        service.ResetTag();
        service.SetRoles(Roles((0, HideSeekRole.Seeker), (1, HideSeekRole.Hider)));
        Assert.True(service.TryStartTag(out _));
        Assert.True(service.CurrentState.GraceRemainingMs > 50_000);
    }
}
