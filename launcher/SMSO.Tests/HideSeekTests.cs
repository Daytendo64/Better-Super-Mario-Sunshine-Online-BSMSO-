using SMSO.Bridge;
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
        state.Flags = GameModeFlags.TagActive | GameModeFlags.GraceActive;
        state.Seq = 42;
        state.RoundStartMs = 123456;
        state.SetRole(0, HideSeekRole.Seeker);
        state.SetRole(1, HideSeekRole.Hider);
        state.LastTaggedSlot = 1;
        state.TagEventId = 3;
        state.GraceRemainingMs = 25000;

        var frame = PacketSerializer.BuildGameModeState(state);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
        Assert.Equal(TcpPacketId.GameModeState, id);
        Assert.True(PacketSerializer.TryReadGameModeState(payload, out var decoded));
        Assert.Equal(GameMode.HideSeek, decoded.GameMode);
        Assert.Equal(GameModeFlags.TagActive | GameModeFlags.GraceActive, decoded.Flags);
        Assert.Equal((ushort)42, decoded.Seq);
        Assert.Equal(123456u, decoded.RoundStartMs);
        Assert.Equal(HideSeekRole.Seeker, decoded.Roles[0]);
        Assert.Equal(HideSeekRole.Hider, decoded.Roles[1]);
        Assert.Equal((byte)1, decoded.LastTaggedSlot);
        Assert.Equal((byte)3, decoded.TagEventId);
        Assert.Equal((ushort)25000, decoded.GraceRemainingMs);
        Assert.True(decoded.GraceActive);
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
    public void TagDetection_IgnoresProximityDuringStartImmunity()
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
                [2] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));
            Assert.True(service.IsProximityTagImmunityActive);

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
            var clustered = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                Position = new Vec3 { X = 40f, Y = 0f, Z = 0f },
            };

            // Clustered lobby/spawn positions must not mass-promote at Start Tag.
            service.ProcessSnapshot(0, seeker, 1, hider);
            service.ProcessSnapshot(0, seeker, 2, clustered);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[2]);
            Assert.True(service.CurrentState.TagActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TagDetection_TagsAfterStartImmunityExpires()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27115);
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
            service.ExpireProximityTagImmunityForTests();

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
            service.ExpireProximityTagImmunityForTests();

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
            service.ExpireProximityTagImmunityForTests();

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
    public void TagDetection_TagsAtVisualBodyContact()
    {
        var seeker = new PlayerSnapshot
        {
            Connected = 1,
            Position = new Vec3 { X = 0f, Y = 0f, Z = 0f },
        };
        var hider = new PlayerSnapshot
        {
            Connected = 1,
            Position = new Vec3 { X = 105f, Y = 0f, Z = 0f },
        };

        Assert.True(HideSeekService.IsWithinTagRange(seeker, hider));
    }

    [Fact]
    public void TagDetection_MissesJustBeyondBodyContact()
    {
        var seeker = new PlayerSnapshot
        {
            Connected = 1,
            Position = new Vec3 { X = 0f, Y = 0f, Z = 0f },
        };
        var hider = new PlayerSnapshot
        {
            Connected = 1,
            Position = new Vec3 { X = HideSeekTagConstants.MaxHorizontalReach + 10f, Y = 0f, Z = 0f },
        };

        Assert.False(HideSeekService.IsWithinTagRange(seeker, hider));
    }

    [Fact]
    public void TagDetection_TagsJumpingSeekerWithinHorizontalRange()
    {
        var seeker = new PlayerSnapshot
        {
            Connected = 1,
            Position = new Vec3 { X = 0f, Y = 50f, Z = 0f },
        };
        var hider = new PlayerSnapshot
        {
            Connected = 1,
            Position = new Vec3 { X = 45f, Y = 0f, Z = 0f },
        };

        Assert.True(HideSeekService.IsWithinTagRange(seeker, hider));
    }

    [Fact]
    public void TagDetection_IgnoresDifferentFloors()
    {
        var seeker = new PlayerSnapshot
        {
            Connected = 1,
            Position = new Vec3 { X = 0f, Y = 0f, Z = 0f },
        };
        var hider = new PlayerSnapshot
        {
            Connected = 1,
            Position = new Vec3 { X = 10f, Y = 700f, Z = 0f },
        };

        Assert.False(HideSeekService.IsWithinTagRange(seeker, hider));
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
            service.ExpireProximityTagImmunityForTests();

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
                Position = new Vec3 { X = 58f, Y = 300f, Z = 0f },
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
    public void HiderDeath_PromotesToSeekerOnFirstDeadSnapshot()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27106);
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

            var alive = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                VfxFlags = 0,
            };
            service.ProcessHiderDeath(1, alive);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);
            Assert.Equal((byte)0, service.CurrentState.TagEventId);

            var dead = alive with { VfxFlags = (ushort)VfxFlags.Dead };
            service.ProcessHiderDeath(1, dead);
            var state = service.CurrentState;
            Assert.Equal(HideSeekRole.Seeker, state.Roles[1]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[2]);
            Assert.Equal((byte)1, state.LastTaggedSlot);
            Assert.Equal((byte)1, state.TagEventId);
            Assert.True(state.TagActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void HiderDeath_DoesNotPromoteTwiceWhileDead()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27107);
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

            var dead = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                VfxFlags = (ushort)VfxFlags.Dead,
            };

            service.ProcessHiderDeath(1, dead);
            Assert.Equal((byte)1, service.CurrentState.TagEventId);

            service.ProcessHiderDeath(1, dead);
            Assert.Equal((byte)1, service.CurrentState.TagEventId);
            Assert.True(service.CurrentState.TagActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void HiderDeath_EndsRoundWhenLastHiderDies()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27108);
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

            var dead = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                VfxFlags = (ushort)VfxFlags.Dead,
            };

            service.ProcessHiderDeath(1, dead);
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
            service.ExpireProximityTagImmunityForTests();

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

    [Fact]
    public void HiderDeath_StillPromotesDuringStartImmunity()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27116);
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
            Assert.True(service.IsProximityTagImmunityActive);

            var dead = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                VfxFlags = (ushort)VfxFlags.Dead,
            };

            service.ProcessHiderDeath(1, dead);
            Assert.Equal(HideSeekRole.Seeker, service.CurrentState.Roles[1]);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[2]);
            Assert.True(service.CurrentState.TagActive);
            Assert.True(service.IsProximityTagImmunityActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void NotifyPlayersWarped_RearmsProximityImmunityWhileTagActive()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27117);
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
            service.ExpireProximityTagImmunityForTests();
            Assert.False(service.IsProximityTagImmunityActive);

            service.NotifyPlayersWarped();
            Assert.True(service.IsProximityTagImmunityActive);

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
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);

            service.ExpireProximityTagImmunityForTests();
            service.ProcessSnapshot(0, seeker, 1, hider);
            Assert.Equal(HideSeekRole.Seeker, service.CurrentState.Roles[1]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TryStartTag_Arms30SecondGraceAndBlocksProximity()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27140);
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

            var state = service.CurrentState;
            Assert.True(state.TagActive);
            Assert.True(state.GraceActive);
            Assert.True(state.GraceRemainingMs > 29000);
            Assert.True(state.GraceRemainingMs <= HideSeekService.StartTagGraceMs);
            Assert.True(service.IsStartTagGraceActive);
            Assert.True(service.IsProximityTagImmunityActive);

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
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);

            service.ExpireProximityTagImmunityForTests();
            Assert.False(service.IsStartTagGraceActive);
            Assert.False(service.IsProximityTagImmunityActive);
            Assert.False(service.CurrentState.GraceActive);
            Assert.Equal((ushort)0, service.CurrentState.GraceRemainingMs);
            Assert.True(service.CurrentState.TagActive);

            service.ProcessSnapshot(0, seeker, 1, hider);
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
    public void StartTag_UsesConfiguredGraceDuration()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27149);
        try
        {
            var service = server.HideSeek;
            service.StartTagGraceDurationMs = 10_000;
            service.SetGameMode(GameMode.HideSeek);
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));

            var state = service.CurrentState;
            Assert.True(state.GraceActive);
            Assert.True(state.GraceRemainingMs > 9000);
            Assert.True(state.GraceRemainingMs <= 10_000);
            Assert.Equal(10_000, service.StartTagGraceDurationMs);
            Assert.Equal(HideSeekService.MinStartTagGraceMs,
                HideSeekService.ClampGraceMs(1_000));
            Assert.Equal(HideSeekService.MaxStartTagGraceMs,
                HideSeekService.ClampGraceMs(120_000));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void NotifyPlayersWarped_DoesNotRearmFullStartTagGrace()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27141);
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
            service.ExpireProximityTagImmunityForTests();
            Assert.False(service.IsStartTagGraceActive);

            service.NotifyPlayersWarped();
            Assert.True(service.IsProximityTagImmunityActive);
            Assert.False(service.IsStartTagGraceActive);
            Assert.False(service.CurrentState.GraceActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void StopTag_ClearsGraceImmediately()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27142);
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
            Assert.True(service.CurrentState.GraceActive);

            service.StopTag();
            Assert.False(service.CurrentState.TagActive);
            Assert.False(service.CurrentState.GraceActive);
            Assert.Equal((ushort)0, service.CurrentState.GraceRemainingMs);
            Assert.False(service.IsStartTagGraceActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TryStartTag_DoesNotConvertRolesToSeekers()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27118);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
                [2] = HideSeekRole.Hider,
                [3] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));

            var state = service.CurrentState;
            Assert.Equal(HideSeekRole.Seeker, state.Roles[0]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[1]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[2]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[3]);
            Assert.True(state.TagActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void OnPlayerDisconnected_KeepsTagActiveWhenHidersRemain()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27109);
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

            service.OnPlayerDisconnected(2);

            var state = service.CurrentState;
            Assert.Equal(GameMode.HideSeek, state.GameMode);
            Assert.True(state.TagActive);
            Assert.Equal(HideSeekRole.Hider, state.Roles[1]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void OnPlayerDisconnected_KeepsTagActiveWhenLastHiderLeaves()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27110);
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

            service.OnPlayerDisconnected(1);

            var state = service.CurrentState;
            Assert.Equal(GameMode.HideSeek, state.GameMode);
            Assert.True(state.TagActive);
            Assert.Equal(HideSeekRole.Seeker, state.Roles[0]);
            Assert.Equal(HideSeekRole.Hider, state.Roles[1]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void SetRoles_DoesNotStopTagWhenDisconnectedSlotOmitted()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27111);
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

            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            });

            Assert.True(service.CurrentState.TagActive);
            Assert.Equal(GameMode.HideSeek, service.CurrentState.GameMode);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void SetRoles_StopsTagWhenConnectedRoleChanges()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27112);
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

            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Seeker,
            });

            Assert.False(service.CurrentState.TagActive);
            Assert.Equal(GameMode.HideSeek, service.CurrentState.GameMode);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void StopTag_PreservesElapsedTimeForResume()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27113);
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
            Thread.Sleep(100);

            service.StopTag();
            var paused = service.CurrentState;
            Assert.False(paused.TagActive);
            Assert.True(paused.RoundStartMs > 0);

            Assert.True(service.TryStartTag(out _));
            var resumed = service.CurrentState;
            Assert.True(resumed.TagActive);
            Assert.Equal(paused.RoundStartMs, resumed.RoundStartMs);
            // Resume must not re-arm Start Tag grace (wash / seeker freeze).
            Assert.False(resumed.GraceActive);
            Assert.Equal((ushort)0, resumed.GraceRemainingMs);
            Assert.False(service.IsStartTagGraceActive);
            Assert.False(service.IsProximityTagImmunityActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TryStartTag_ResumeAfterStop_DoesNotRearmGrace()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27143);
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
            Assert.True(service.CurrentState.GraceActive);

            // Let the round accumulate elapsed time, then clear grace so we are
            // mid-round (same state as after grace naturally expires).
            Thread.Sleep(50);
            service.ExpireProximityTagImmunityForTests();
            Assert.False(service.IsStartTagGraceActive);
            Assert.True(service.CurrentState.TagActive);

            service.StopTag();
            Assert.False(service.CurrentState.TagActive);
            Assert.True(service.CurrentState.RoundStartMs > 0);

            Assert.True(service.TryStartTag(out _));
            var resumed = service.CurrentState;
            Assert.True(resumed.TagActive);
            Assert.False(resumed.GraceActive);
            Assert.Equal((ushort)0, resumed.GraceRemainingMs);
            Assert.False(service.IsStartTagGraceActive);
            Assert.False(service.IsProximityTagImmunityActive);

            // Proximity tags must work immediately on resume (no grace / warp immunity).
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
            Assert.Equal(HideSeekRole.Seeker, service.CurrentState.Roles[1]);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[2]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TryStartTag_ResumeAfterStopDuringGrace_DoesNotRearmGrace()
    {
        // Bugbot: Stop on the same tick as Start leaves elapsed at 0; resume must
        // still skip grace (do not treat as a fresh Start Tag).
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27145);
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
            Assert.True(service.CurrentState.GraceActive);

            service.StopTag();
            Assert.False(service.CurrentState.TagActive);
            Assert.Equal(0u, service.CurrentState.RoundStartMs);

            Assert.True(service.TryStartTag(out _));
            var resumed = service.CurrentState;
            Assert.True(resumed.TagActive);
            Assert.False(resumed.GraceActive);
            Assert.Equal((ushort)0, resumed.GraceRemainingMs);
            Assert.False(service.IsStartTagGraceActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void TryStartTag_AfterReset_ArmsGraceAgain()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27144);
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
            Thread.Sleep(50);
            service.StopTag();
            Assert.True(service.CurrentState.RoundStartMs > 0);

            service.ResetTag();
            Assert.Equal(0u, service.CurrentState.RoundStartMs);

            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));
            var fresh = service.CurrentState;
            Assert.True(fresh.TagActive);
            Assert.True(fresh.GraceActive);
            Assert.True(fresh.GraceRemainingMs > 29000);
            Assert.True(service.IsStartTagGraceActive);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void ResetTag_ClearsElapsedTime()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27114);
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
            Thread.Sleep(100);
            service.StopTag();
            Assert.True(service.CurrentState.RoundStartMs > 0);

            service.ResetTag();
            Assert.Equal(0u, service.CurrentState.RoundStartMs);
            Assert.False(service.CurrentState.TagActive);

            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));
            Assert.Equal(0u, service.CurrentState.RoundStartMs);
        }
        finally
        {
            server.Stop();
        }
    }
}

public sealed class BridgeWorkerGameModeTests
{
    [Fact]
    public void ForceReset_AfterHighSeqNormal_AllowsLowSeqHideSeek()
    {
        // Reproduces host disconnect/rehost: Apply HideSeek@50 → Normal@51 (seq gate stores 51),
        // then ForceReset (session teardown). New session HideSeek@1 must apply immediately.
        using var worker = new BridgeWorker(new DolphinBridge());

        var hideSeek = GameModeStatePacket.CreateDefault();
        hideSeek.GameMode = GameMode.HideSeek;
        hideSeek.Seq = 50;
        worker.ApplyGameModeState(0, hideSeek);
        Assert.Equal(GameMode.HideSeek, worker.CurrentGameModeState.GameMode);

        var normal = GameModeStatePacket.CreateDefault();
        normal.GameMode = GameMode.Normal;
        normal.Seq = 51;
        worker.ApplyGameModeState(0, normal);
        Assert.Equal(GameMode.Normal, worker.CurrentGameModeState.GameMode);

        // Previously ForceGameModeToNormalLocally skipped ForceReset when already Normal,
        // leaving _lastGameModeSeq=51 so Seq=1 was rejected.
        worker.ForceResetGameModeToNormal(0);
        Assert.Equal(GameMode.Normal, worker.CurrentGameModeState.GameMode);
        Assert.Equal((ushort)0, worker.CurrentGameModeState.Seq);

        var hideSeekAgain = GameModeStatePacket.CreateDefault();
        hideSeekAgain.GameMode = GameMode.HideSeek;
        hideSeekAgain.Seq = 1;
        worker.ApplyGameModeState(0, hideSeekAgain);
        Assert.Equal(GameMode.HideSeek, worker.CurrentGameModeState.GameMode);
        Assert.Equal((ushort)1, worker.CurrentGameModeState.Seq);
    }

    [Fact]
    public void ApplyGameModeState_RejectsStaleOrEqualSeq()
    {
        using var worker = new BridgeWorker(new DolphinBridge());

        var hideSeek = GameModeStatePacket.CreateDefault();
        hideSeek.GameMode = GameMode.HideSeek;
        hideSeek.Seq = 10;
        worker.ApplyGameModeState(0, hideSeek);

        var stale = GameModeStatePacket.CreateDefault();
        stale.GameMode = GameMode.Normal;
        stale.Seq = 10;
        worker.ApplyGameModeState(0, stale);
        Assert.Equal(GameMode.HideSeek, worker.CurrentGameModeState.GameMode);

        var older = GameModeStatePacket.CreateDefault();
        older.GameMode = GameMode.Normal;
        older.Seq = 5;
        worker.ApplyGameModeState(0, older);
        Assert.Equal(GameMode.HideSeek, worker.CurrentGameModeState.GameMode);
    }

    [Fact]
    public void SetConnected_False_ResetsGameModeSeqSoLowSeqApplies()
    {
        using var worker = new BridgeWorker(new DolphinBridge());

        var hideSeek = GameModeStatePacket.CreateDefault();
        hideSeek.GameMode = GameMode.HideSeek;
        hideSeek.Seq = 40;
        worker.ApplyGameModeState(0, hideSeek);

        var normal = GameModeStatePacket.CreateDefault();
        normal.GameMode = GameMode.Normal;
        normal.Seq = 41;
        worker.ApplyGameModeState(0, normal);

        // Disconnect path (ResetClientSessionState) must clear seq even without ForceReset.
        worker.SetConnected(false, 0, "", false);

        var hideSeekAgain = GameModeStatePacket.CreateDefault();
        hideSeekAgain.GameMode = GameMode.HideSeek;
        hideSeekAgain.Seq = 1;
        worker.ApplyGameModeState(0, hideSeekAgain);
        Assert.Equal(GameMode.HideSeek, worker.CurrentGameModeState.GameMode);
        Assert.Equal((ushort)1, worker.CurrentGameModeState.Seq);
    }

    [Fact]
    public void SetConnected_True_DoesNotWipeWorldSyncWhenAlreadyConnected()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.DebugSeedWorldSync(lastAppliedEventId: 99, incomingEventId: 100);

        // JoinAccepted already connected; FlushSnapshotsAfterConnect must not clear mid-replay.
        worker.SetConnected(true, 1, "Host", true);

        var (lastApplied, incoming) = worker.DebugGetWorldSync();
        Assert.Equal(99u, lastApplied);
        Assert.Equal(100u, incoming);
    }

    [Fact]
    public void SetConnected_True_ClearsWorldSyncOnFreshConnect()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.DebugSeedWorldSync(lastAppliedEventId: 99, incomingEventId: 100);
        worker.SetConnected(false, 0, "", false);

        worker.SetConnected(true, 1, "Host", true);
        var (lastApplied, incoming) = worker.DebugGetWorldSync();
        Assert.Equal(0u, lastApplied);
        Assert.Equal(0u, incoming);
    }
}

public sealed class HideSeekRandomTagExemptionTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 5)]
    [InlineData(10, 5)]
    public void GetExemptRounds_ScalesWithPlayerCount(int playerCount, int expectedRounds)
    {
        Assert.Equal(expectedRounds, HideSeekRandomTagExemption.GetExemptRounds(playerCount));
    }

    [Fact]
    public void RegisterPick_ExemptsPreviousSeekerForTwoRoundsWithFourPlayers()
    {
        var rounds = new Dictionary<byte, int>();

        HideSeekRandomTagExemption.RegisterPick(rounds, 0, 4);
        Assert.Contains((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));

        HideSeekRandomTagExemption.RegisterPick(rounds, 1, 4);
        Assert.Contains((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)1, HideSeekRandomTagExemption.GetExemptSlots(rounds));

        HideSeekRandomTagExemption.RegisterPick(rounds, 2, 4);
        Assert.DoesNotContain((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)1, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)2, HideSeekRandomTagExemption.GetExemptSlots(rounds));
    }

    [Fact]
    public void RegisterPick_ExemptsPreviousSeekerForThreeRoundsWithSixPlayers()
    {
        var rounds = new Dictionary<byte, int>();

        HideSeekRandomTagExemption.RegisterPick(rounds, 0, 6);
        Assert.Contains((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));

        HideSeekRandomTagExemption.RegisterPick(rounds, 1, 6);
        Assert.Contains((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)1, HideSeekRandomTagExemption.GetExemptSlots(rounds));

        HideSeekRandomTagExemption.RegisterPick(rounds, 2, 6);
        Assert.Contains((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)1, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)2, HideSeekRandomTagExemption.GetExemptSlots(rounds));

        HideSeekRandomTagExemption.RegisterPick(rounds, 3, 6);
        Assert.DoesNotContain((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)1, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)2, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)3, HideSeekRandomTagExemption.GetExemptSlots(rounds));
    }

    [Fact]
    public void PruneDisconnected_RemovesStaleSlots()
    {
        var rounds = new Dictionary<byte, int> { [0] = 2, [3] = 1 };
        HideSeekRandomTagExemption.PruneDisconnected(rounds, new[] { (byte)0, (byte)1 });
        Assert.Single(rounds);
        Assert.True(rounds.ContainsKey(0));
    }
}
