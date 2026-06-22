using SMSO.Net;
using SMSO.Server;
using Xunit;

namespace SMSO.Tests;

public sealed class GameModeStatePacketTests
{
    [Fact]
    public void GameModeState_RoundTrip()
    {
        var state = GameModeStatePacket.CreateDefault();
        state.GameMode = GameMode.HideSeek;
        state.Flags = GameModeFlags.TagActive;
        state.Seq = 42;
        state.RoundStartMs = 123456;
        state.SetRole(0, HideSeekRole.Seeker);
        state.SetRole(1, HideSeekRole.Hider);
        state.LastTaggedSlot = 1;
        state.TagEventId = 3;

        var frame = PacketSerializer.BuildGameModeState(state);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.GameModeState, id);
        Assert.True(PacketSerializer.TryReadGameModeState(payload, out var decoded));
        Assert.Equal(GameMode.HideSeek, decoded.GameMode);
        Assert.Equal(GameModeFlags.TagActive, decoded.Flags);
        Assert.Equal((ushort)42, decoded.Seq);
        Assert.Equal(123456u, decoded.RoundStartMs);
        Assert.Equal(HideSeekRole.Seeker, decoded.Roles[0]);
        Assert.Equal(HideSeekRole.Hider, decoded.Roles[1]);
        Assert.Equal((byte)1, decoded.LastTaggedSlot);
        Assert.Equal((byte)3, decoded.TagEventId);
    }
}

public sealed class HideSeekServiceTests
{
    [Fact]
    public void ResetTag_SetsEveryoneToHiderAndStopsTag()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27104);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));
            service.ResetTag();

            var state = service.CurrentState;
            Assert.False(state.TagActive);
            Assert.Equal(HideSeekRole.Hider, state.Roles[0]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[1]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void SetRoles_DoesNotStopTagWhenUnchanged()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27103);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            var roles = new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            };
            service.SetRoles(roles);
            Assert.True(service.TryStartTag(out _));

            service.SetRoles(roles);
            Assert.True(service.CurrentState.TagActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TagDetection_RespectsStartGracePeriod()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27102);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));

            var seeker = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 0f, Y = 0f, Z = 0f },
            };
            var hider = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 0f, Y = 0f, Z = 0f },
            };

            service.ProcessSnapshot(0, seeker, 1, hider);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);
            Assert.True(service.CurrentState.TagActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TagDetection_FlipsHiderWithinRadius()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27100);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            var roles = new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            };
            service.SetRoles(roles);
            Assert.True(service.TryStartTag(out _));
            service.EndTagGraceForTesting();

            var seeker = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 0f, Y = 0f, Z = 0f },
            };
            var hider = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 20f, Y = 0f, Z = 0f },
            };

            service.ProcessSnapshot(0, seeker, 1, hider);
            var state = service.CurrentState;
            Assert.Equal(HideSeekRole.Hider, state.Roles[0]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[1]);
            Assert.Equal(0xFF, state.LastTaggedSlot);
            Assert.Equal((byte)0, state.TagEventId);
            Assert.False(state.TagActive);
            Assert.False(state.RoundComplete);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TagDetection_AutoResetsWhenAllHidersFound()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27105);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
                [2] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));
            service.EndTagGraceForTesting();

            var seeker = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 0f, Y = 0f, Z = 0f },
            };
            var hiderOne = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 10f, Y = 0f, Z = 0f },
            };
            var hiderTwo = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 20f, Y = 0f, Z = 0f },
            };

            service.ProcessSnapshot(0, seeker, 1, hiderOne);
            Assert.True(service.CurrentState.TagActive);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[2]);

            service.ProcessSnapshot(0, seeker, 2, hiderTwo);
            var state = service.CurrentState;
            Assert.False(state.TagActive);
            Assert.False(state.RoundComplete);
            Assert.Equal(HideSeekRole.Hider, state.Roles[0]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[1]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[2]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TagDetection_TagsWithinObservedTouchRange()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27103);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
                [2] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));
            service.EndTagGraceForTesting();

            var seeker = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 0f, Y = 300f, Z = 0f },
            };
            var hider = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 55f, Y = 300f, Z = 0f },
            };
            var otherHider = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 500f, Y = 300f, Z = 0f },
            };

            service.ProcessSnapshot(0, seeker, 1, hider);
            service.ProcessSnapshot(0, seeker, 2, otherHider);
            Assert.Equal(HideSeekRole.Seeker, service.CurrentState.Roles[1]);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[2]);
            Assert.True(service.CurrentState.TagActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TagDetection_IgnoresOutOfRange()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27101);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));
            service.EndTagGraceForTesting();

            var seeker = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 0f, Y = 0f, Z = 0f },
            };
            var hider = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 500f, Y = 0f, Z = 0f },
            };

            service.ProcessSnapshot(0, seeker, 1, hider);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);
        }
        finally
        {
            server.Stop();
        }
    }
}
