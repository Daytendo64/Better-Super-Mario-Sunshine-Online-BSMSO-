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

    [Fact]
    public void GameModeState_RolesArray_MatchesMaxPlayersCapacity()
    {
        // Lock Hide & Seek role capacity at ProtocolConstants.MaxPlayers (10).
        Assert.Equal(10, ProtocolConstants.MaxPlayers);
        Assert.Equal(ProtocolConstants.MaxPlayers, ProtocolConstants.StableMaxPlayers);

        var state = GameModeStatePacket.CreateDefault();
        Assert.Equal(ProtocolConstants.MaxPlayers, state.Roles.Length);

        for (byte slot = 0; slot < ProtocolConstants.MaxPlayers; slot++)
            state.SetRole(slot, slot % 2 == 0 ? HideSeekRole.Seeker : HideSeekRole.Hider);

        // Highest slot must round-trip (no silent 4-player truncate).
        state.SetRole((byte)(ProtocolConstants.MaxPlayers - 1), HideSeekRole.Seeker);
        var wire = GameModeStatePacket.ToCommGameMode(localSlot: 9, state);
        Assert.Equal(ProtocolConstants.MaxPlayers, wire.RoleBySlot.Length);
        Assert.Equal((byte)HideSeekRole.Seeker, wire.LocalRole);
        Assert.Equal((byte)HideSeekRole.Seeker, wire.RoleBySlot[ProtocolConstants.MaxPlayers - 1]);

        var frame = PacketSerializer.BuildGameModeState(state);
        Assert.True(PacketSerializer.TryUnwrapTcp(frame, out _, out var payload));
        Assert.True(PacketSerializer.TryReadGameModeState(payload, out var decoded));
        Assert.Equal(ProtocolConstants.MaxPlayers, decoded.Roles.Length);
        Assert.Equal(HideSeekRole.Seeker, decoded.Roles[ProtocolConstants.MaxPlayers - 1]);
        // Even slots 0,2,4,6,8 seekers + forced slot 9 seeker = 6 seekers / 4 hiders.
        Assert.Equal(6, decoded.CountRole(HideSeekRole.Seeker));
        Assert.Equal(4, decoded.CountRole(HideSeekRole.Hider));
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
            service.ExpireWarpProximityImmunityForTests();

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
            service.ExpireWarpProximityImmunityForTests();

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
            service.ExpireWarpProximityImmunityForTests();

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
    public void HiderDeath_StillPromotesDuringStartGrace_AfterWarpImmunity()
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
            Assert.True(service.IsStartTagGraceActive);
            Assert.True(service.IsProximityTagImmunityActive);

            // Short death-edge window absorbs stale Dead; real deaths still promote
            // during the remaining Start Tag hide grace once that window ends.
            service.ExpireWarpProximityImmunityForTests();
            Assert.True(service.IsStartTagGraceActive);
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
            Assert.True(service.IsStartTagGraceActive);
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
            // Mid-round warp must NOT re-arm the full Start Tag freeze/tint grace —
            // module skips stage-entry demos and seekers stay movable.
            Assert.False(service.CurrentState.GraceActive);
            Assert.False(service.IsStartTagGraceActive);

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
    public void OnPlayerDisconnected_CompletesRoundWhenLastHiderLeaves()
    {
        // A round with nobody left to find can never end on its own, so the last hider
        // leaving takes the normal round-complete path (fanfare + roles reset to hider).
        // Disconnects with players still on both sides keep the tag running — see
        // OnPlayerDisconnected_KeepsTagActive above.
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
            Assert.False(state.TagActive);
            Assert.True((state.Flags & GameModeFlags.RoundFanfare) != 0);
            Assert.Equal(HideSeekRole.Hider, state.Roles[0]);
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
            // Brief warp-style proximity immunity prevents clustered mass-promotes on resume.
            Assert.True(service.IsProximityTagImmunityActive);
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
            Assert.True(service.IsProximityTagImmunityActive);

            // After brief proximity immunity expires, tags work again (no full grace).
            service.ExpireProximityTagImmunityForTests();
            Assert.False(service.IsProximityTagImmunityActive);

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
    public void TryStartTag_Resume_AbsorbsStaleDeadSnapshotWithoutPromoting()
    {
        // Start Tag without Reset Tag arms resume proximity immunity. A leftover
        // VFX_DEAD from a prior death reload must not instantly promote a hider.
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27155);
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
            service.StopTag();

            Assert.True(service.TryStartTag(out _));
            Assert.True(service.IsProximityTagImmunityActive);
            Assert.False(service.IsStartTagGraceActive);

            var dead = new PlayerSnapshot
            {
                Connected = 1,
                StageId = 2,
                EpisodeId = 0,
                VfxFlags = (ushort)VfxFlags.Dead,
            };
            service.ProcessHiderDeath(1, dead);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);
            Assert.Equal((byte)0, service.CurrentState.TagEventId);

            // After immunity expires, still-dead rising edge was absorbed — no promote
            // until they revive and die again.
            service.ExpireProximityTagImmunityForTests();
            service.ProcessHiderDeath(1, dead);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);

            var alive = dead with { VfxFlags = 0 };
            service.ProcessHiderDeath(1, alive);
            service.ProcessHiderDeath(1, dead);
            Assert.Equal(HideSeekRole.Seeker, service.CurrentState.Roles[1]);
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

    [Fact]
    public void SetRoles_DoesNotStopTagWhenRejoiningSeekerDefaultedToHider()
    {
        // Host UI used to push rejoins as Hider on roster growth, demoting a seeker and
        // stopping tag. Server must preserve Seeker and keep TagActive.
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27140);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            // Two seekers so the rejoining one is not also the last seeker (which now ends
            // the round on its own — see OnPlayerDisconnected_CompletesRoundWhenLastHiderLeaves).
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Seeker,
                [2] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));

            service.OnPlayerDisconnected(0);
            service.OnPlayerJoined(0);
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Hider,
                [1] = HideSeekRole.Seeker,
                [2] = HideSeekRole.Hider,
            });

            Assert.True(service.CurrentState.TagActive);
            Assert.Equal(HideSeekRole.Seeker, service.CurrentState.Roles[0]);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[2]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void OnPlayerJoined_RestoresAssignmentWithoutStoppingTag()
    {
        var levels = new LevelCatalog();
        var server = new GameServer(levels);
        server.Start(27141);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);
            // Second hider keeps the round alive when slot 1 drops.
            service.SetRoles(new Dictionary<byte, HideSeekRole>
            {
                [0] = HideSeekRole.Seeker,
                [1] = HideSeekRole.Hider,
                [2] = HideSeekRole.Hider,
            });
            Assert.True(service.TryStartTag(out _));
            service.OnPlayerDisconnected(1);
            Assert.True(service.CurrentState.TagActive);

            service.OnPlayerJoined(1);
            Assert.True(service.CurrentState.TagActive);
            Assert.Equal(HideSeekRole.Hider, service.CurrentState.Roles[1]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void SetRoles_FullLobby_PreservesAllTenSlots()
    {
        // Lock HideSeekService role capacity at ProtocolConstants.MaxPlayers.
        Assert.Equal(10, ProtocolConstants.MaxPlayers);
        var levels = new LevelCatalog();
        var server = new GameServer(levels) { MaxPlayers = ProtocolConstants.MaxPlayers };
        server.Start(27101);
        try
        {
            var service = server.HideSeek;
            service.SetGameMode(GameMode.HideSeek);

            var roles = new Dictionary<byte, HideSeekRole>();
            for (byte slot = 0; slot < ProtocolConstants.MaxPlayers; slot++)
                roles[slot] = slot == 0 || slot == 9 ? HideSeekRole.Seeker : HideSeekRole.Hider;

            service.SetRoles(roles);
            var state = service.CurrentState;
            Assert.Equal(ProtocolConstants.MaxPlayers, state.Roles.Length);
            Assert.Equal(HideSeekRole.Seeker, state.Roles[0]);
            Assert.Equal(HideSeekRole.Seeker, state.Roles[ProtocolConstants.MaxPlayers - 1]);
            Assert.Equal(2, state.CountRole(HideSeekRole.Seeker));
            Assert.Equal(8, state.CountRole(HideSeekRole.Hider));

            Assert.True(service.TryStartTag(out _));
            Assert.True(service.CurrentState.TagActive);
            Assert.True(service.CurrentState.GraceActive);
            Assert.True(service.CurrentState.GraceRemainingMs > 0);

            // Highest-slot seeker survives Start Tag broadcast.
            Assert.Equal(HideSeekRole.Seeker, service.CurrentState.Roles[9]);
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
    public void ShouldFallbackGameModeWriteOnRemoteSyncFail_OnlyHideSeek()
    {
        var hideSeek = CommGameModeState.CreateDefault();
        hideSeek.Mode = (byte)GameMode.HideSeek;
        hideSeek.Flags = (byte)(GameModeFlags.TagActive | GameModeFlags.GraceActive);
        Assert.True(BridgeWorker.ShouldFallbackGameModeWriteOnRemoteSyncFail(hideSeek));

        var normal = CommGameModeState.CreateDefault();
        normal.Mode = (byte)GameMode.Normal;
        Assert.False(BridgeWorker.ShouldFallbackGameModeWriteOnRemoteSyncFail(normal));
    }

    [Fact]
    public void FlushInterpolatedRemotes_RemoteSyncFail_FallsBackToGameModeOnlyDuringGrace()
    {
        // Regression (ModBuildId 49): Active-play TryWriteRemoteSyncPayload failure used to
        // return without TryWriteGameModeStateOnly, so Dolphin kept a stale mailbox lacking
        // GMF_GRACE_ACTIVE until the ~1s TickGrace rebroadcast — intermittent movable seeker.
        var bridge = new DolphinBridge
        {
            DebugSimulateAttached = true,
            DebugFailRemoteSyncWrite = true,
        };
        using var worker = new BridgeWorker(bridge);

        var hideSeek = GameModeStatePacket.CreateDefault();
        hideSeek.GameMode = GameMode.HideSeek;
        hideSeek.Flags = GameModeFlags.TagActive | GameModeFlags.GraceActive;
        hideSeek.GraceRemainingMs = 30_000;
        hideSeek.Seq = 1;
        hideSeek.SetRole(0, HideSeekRole.Seeker);

        worker.ApplyGameModeState(0, hideSeek);

        Assert.True(bridge.DebugRemoteSyncWriteCalls >= 1);
        Assert.True(bridge.DebugGameModeOnlyWriteCalls >= 1);
        Assert.Equal((byte)GameMode.HideSeek, bridge.DebugLastGameModeOnlyWrite.Mode);
        Assert.Equal(
            (byte)(GameModeFlags.TagActive | GameModeFlags.GraceActive),
            bridge.DebugLastGameModeOnlyWrite.Flags);
        Assert.Equal((byte)HideSeekRole.Seeker, bridge.DebugLastGameModeOnlyWrite.LocalRole);
        Assert.Equal((ushort)30_000, bridge.DebugLastGameModeOnlyWrite.GraceRemainingMs);
    }

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

    [Fact]
    public void SetConnected_True_ClearsProgressSnapshotOnFreshConnect()
    {
        // Regression (ModBuildId 29): fresh connect cleared world-event lanes but not the
        // progress snapshot mailbox. Dolphin RAM from a prior session can still hold
        // moduleAppliedSeq=N; join heal hostSeq=1 then soft-skips (hostSeq <= applied).
        using var worker = new BridgeWorker(new DolphinBridge());

        var staleLive = CommBuffer.CreateDefault();
        staleLive.Magic = ProtocolConstants.Magic;
        staleLive.ProgressSnapshotHostSeq = 42;
        staleLive.ProgressSnapshotModuleAppliedSeq = 42;
        staleLive.ProgressSnapshotPayloadLen = 4;
        staleLive.ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        staleLive.ProgressSnapshotPayload[0] = 1;
        worker.DebugAdoptLiveBuffer(staleLive);

        var before = worker.DebugGetWorkingBuffer();
        Assert.Equal(42u, before.ProgressSnapshotHostSeq);
        Assert.Equal(42u, before.ProgressSnapshotModuleAppliedSeq);

        worker.SetConnected(true, 1, "Host", true);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(0u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(0u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(0, buf.ProgressSnapshotPayloadLen);
    }

    [Fact]
    public void SetConnected_True_DoesNotWipeProgressSnapshotWhenAlreadyConnected()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });

        // Re-assert Connected (FlushSnapshotsAfterConnect) must not wipe an in-flight heal.
        worker.SetConnected(true, 1, "Host", true);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(42u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(4, buf.ProgressSnapshotPayloadLen);
    }

    [Fact]
    public void SetConnected_False_ClearsProgressSnapshotLane()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });

        worker.SetConnected(false, 0, "", false);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(0u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(0u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(0, buf.ProgressSnapshotPayloadLen);
    }

    [Fact]
    public void ClearProgressSnapshot_FullWriteMerge_DoesNotResurrectPreClearLane()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });
        worker.ClearProgressSnapshot();

        var staleLive = CommBuffer.CreateDefault();
        staleLive.ProgressSnapshotHostSeq = 42;
        staleLive.ProgressSnapshotModuleAppliedSeq = 42;
        staleLive.ProgressSnapshotPayloadLen = 4;
        staleLive.ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        staleLive.ProgressSnapshotPayload[0] = 1;
        staleLive.ProgressSnapshotPayload[1] = 2;
        staleLive.ProgressSnapshotPayload[2] = 3;
        staleLive.ProgressSnapshotPayload[3] = 4;

        worker.DebugMergeProgressLaneFromLive(staleLive);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(0u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(0u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(0, buf.ProgressSnapshotPayloadLen);
    }

    [Fact]
    public void ClearProgressSnapshot_LiveAdopt_DoesNotResurrectPreClearLane()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 9, 8, 7, 6 });
        worker.ClearProgressSnapshot();

        var staleLive = CommBuffer.CreateDefault();
        staleLive.Magic = ProtocolConstants.Magic;
        staleLive.ProgressSnapshotHostSeq = 42;
        staleLive.ProgressSnapshotModuleAppliedSeq = 42;
        staleLive.ProgressSnapshotPayloadLen = 4;
        staleLive.ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        staleLive.ProgressSnapshotPayload[0] = 9;

        worker.DebugAdoptLiveBuffer(staleLive);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(0u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(0u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(0, buf.ProgressSnapshotPayloadLen);
    }

    [Fact]
    public void PushProgressSnapshot_AfterClear_AllowsLiveMergeAgain()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.ClearProgressSnapshot();
        worker.PushProgressSnapshot(7, new byte[] { 1, 2 });

        var newerLive = CommBuffer.CreateDefault();
        newerLive.ProgressSnapshotHostSeq = 8;
        newerLive.ProgressSnapshotModuleAppliedSeq = 7;
        newerLive.ProgressSnapshotPayloadLen = 3;
        newerLive.ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        newerLive.ProgressSnapshotPayload[0] = 5;
        newerLive.ProgressSnapshotPayload[1] = 6;
        newerLive.ProgressSnapshotPayload[2] = 7;

        worker.DebugMergeProgressLaneFromLive(newerLive);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(8u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(7u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(3, buf.ProgressSnapshotPayloadLen);
    }

    [Fact]
    public void PushProgressSnapshot_ForcesModuleAppliedZero_ForSameSeqReheal()
    {
        // Regression: force-full re-pushes the same progressSeq after clear. If the bridge
        // preserved a stale moduleAppliedSeq == hostSeq, the module soft-skipped the heal
        // while the launcher still advanced _lastAppliedProgressSeq.
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });
        var afterFirst = worker.DebugGetWorkingBuffer();
        Assert.Equal(42u, afterFirst.ProgressSnapshotHostSeq);
        Assert.Equal(0u, afterFirst.ProgressSnapshotModuleAppliedSeq);

        // Simulate module ack, then force-full clear + same-seq push.
        worker.DebugAdoptLiveBuffer(new CommBuffer
        {
            Magic = ProtocolConstants.Magic,
            ProgressSnapshotHostSeq = 42,
            ProgressSnapshotModuleAppliedSeq = 42,
            ProgressSnapshotPayloadLen = 4,
            ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload],
        });
        worker.ClearProgressSnapshot();
        worker.PushProgressSnapshot(42, new byte[] { 5, 6, 7, 8 });

        var afterForce = worker.DebugGetWorkingBuffer();
        Assert.Equal(42u, afterForce.ProgressSnapshotHostSeq);
        Assert.Equal(0u, afterForce.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(4, afterForce.ProgressSnapshotPayloadLen);
        Assert.Equal(5, afterForce.ProgressSnapshotPayload![0]);
    }

    [Fact]
    public void TryGetProgressSnapshotAck_ReportsHostAndModuleApplied()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        Assert.True(worker.TryGetProgressSnapshotAck(out var host0, out var applied0));
        Assert.Equal(0u, host0);
        Assert.Equal(0u, applied0);

        worker.PushProgressSnapshot(17, new byte[] { 9, 8, 7 });
        Assert.True(worker.TryGetProgressSnapshotAck(out var host, out var applied));
        Assert.Equal(17u, host);
        Assert.Equal(0u, applied);
        Assert.True(host > applied);
    }

    [Fact]
    public void TryRepushPendingProgressSnapshot_RewritesWhenHostAheadOfApplied()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(33, new byte[] { 1, 2, 3, 4 });

        // Simulate a stale moduleApplied behind host (pending heal).
        var payload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        payload[0] = 1;
        payload[1] = 2;
        payload[2] = 3;
        payload[3] = 4;
        worker.DebugAdoptLiveBuffer(new CommBuffer
        {
            Magic = ProtocolConstants.Magic,
            ProgressSnapshotHostSeq = 33,
            ProgressSnapshotModuleAppliedSeq = 10,
            ProgressSnapshotPayloadLen = 4,
            ProgressSnapshotPayload = payload,
        });

        // Detached Dolphin → Push write fails; working buffer still restages moduleApplied=0.
        Assert.False(worker.TryRepushPendingProgressSnapshot());
        var after = worker.DebugGetWorkingBuffer();
        Assert.Equal(33u, after.ProgressSnapshotHostSeq);
        Assert.Equal(0u, after.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(4, after.ProgressSnapshotPayloadLen);
        Assert.Equal(1, after.ProgressSnapshotPayload![0]);
    }

    [Fact]
    public void TryRepushPendingProgressSnapshot_ReturnsFalseWhenDolphinWriteFails()
    {
        // Regression: returning true on a Push write miss made MaybeRequestProgressCatchup
        // treat the re-push as success and skip force-full for ~20s (stalled heal loop).
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);

        var payload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        payload[0] = 9;
        payload[1] = 8;
        payload[2] = 7;
        payload[3] = 6;
        // Open the progress lane (SetConnected force-clears it) then leave a pending heal.
        worker.PushProgressSnapshot(55, payload.AsSpan(0, 4));
        Assert.True(worker.TryGetProgressSnapshotAck(out var host, out var applied));
        Assert.True(host > applied);

        // No attached Dolphin process → TryWriteProgressSnapshotOnly returns false.
        Assert.False(worker.TryRepushPendingProgressSnapshot());
        Assert.False(worker.PushProgressSnapshot(55, payload.AsSpan(0, 4)));
    }

    [Fact]
    public void TryRepushPendingProgressSnapshot_NoOpWhenAlreadyApplied()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        var payload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        payload[0] = 5;
        payload[1] = 6;
        worker.DebugAdoptLiveBuffer(new CommBuffer
        {
            Magic = ProtocolConstants.Magic,
            ProgressSnapshotHostSeq = 33,
            ProgressSnapshotModuleAppliedSeq = 33,
            ProgressSnapshotPayloadLen = 2,
            ProgressSnapshotPayload = payload,
        });

        Assert.False(worker.TryRepushPendingProgressSnapshot());
    }

    [Fact]
    public void PushProgressSnapshot_Merge_ClearsLatchWhenModuleApplied()
    {
        // Module may bulk-apply before poll observes applied=0. Treat applied >= hostSeq
        // as done only when live still carries this Push's heal epoch.
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });
        var stagedFlags = worker.DebugGetWorkingBuffer().ProgressSnapshotFlags;

        var appliedLive = CommBuffer.CreateDefault();
        appliedLive.ProgressSnapshotHostSeq = 42;
        appliedLive.ProgressSnapshotModuleAppliedSeq = 42;
        appliedLive.ProgressSnapshotFlags = stagedFlags;
        appliedLive.ProgressSnapshotPayloadLen = 4;
        appliedLive.ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        appliedLive.ProgressSnapshotPayload[0] = 9;

        worker.DebugMergeProgressLaneFromLive(appliedLive);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(42u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(42u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(4, buf.ProgressSnapshotPayloadLen);
        Assert.Equal(1, buf.ProgressSnapshotPayload![0]);
    }

    [Fact]
    public void PushProgressSnapshot_Merge_KeepsPendingOnStaleAppliedAck()
    {
        // Same hostSeq + applied>=host with flags=0 (pre-push stale) must NOT clear the
        // heal latch — that soft-skipped rehals and left stage-enter force looking hung.
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });
        Assert.NotEqual(0, worker.DebugGetWorkingBuffer().ProgressSnapshotFlags);

        var staleLive = CommBuffer.CreateDefault();
        staleLive.ProgressSnapshotHostSeq = 42;
        staleLive.ProgressSnapshotModuleAppliedSeq = 42;
        staleLive.ProgressSnapshotFlags = 0;
        staleLive.ProgressSnapshotPayloadLen = 4;
        staleLive.ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];

        worker.DebugMergeProgressLaneFromLive(staleLive);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(42u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(0u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.True(buf.ProgressSnapshotFlags != 0);
    }

    [Fact]
    public void PushProgressSnapshot_Merge_KeepsPendingWhenMidApply()
    {
        // 0 < moduleApplied < hostSeq: still pending — keep staged moduleApplied=0.
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });

        var midLive = CommBuffer.CreateDefault();
        midLive.ProgressSnapshotHostSeq = 42;
        midLive.ProgressSnapshotModuleAppliedSeq = 10;
        midLive.ProgressSnapshotPayloadLen = 4;
        midLive.ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        midLive.ProgressSnapshotPayload[0] = 9;

        worker.DebugMergeProgressLaneFromLive(midLive);

        var buf = worker.DebugGetWorkingBuffer();
        Assert.Equal(42u, buf.ProgressSnapshotHostSeq);
        Assert.Equal(0u, buf.ProgressSnapshotModuleAppliedSeq);
        Assert.Equal(4, buf.ProgressSnapshotPayloadLen);
        Assert.Equal(1, buf.ProgressSnapshotPayload![0]);
    }

    [Fact]
    public void TryGetProgressSnapshotAck_FastModuleApply_ClearsPendingHeal()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });
        var stagedFlags = worker.DebugGetWorkingBuffer().ProgressSnapshotFlags;

        worker.DebugAdoptLiveBuffer(new CommBuffer
        {
            Magic = ProtocolConstants.Magic,
            ProgressSnapshotHostSeq = 42,
            ProgressSnapshotModuleAppliedSeq = 42,
            ProgressSnapshotFlags = stagedFlags,
            ProgressSnapshotPayloadLen = 4,
            ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload],
        });

        Assert.True(worker.TryGetProgressSnapshotAck(out var host, out var applied));
        Assert.Equal(42u, host);
        Assert.Equal(42u, applied);
        Assert.False(host > applied);
    }

    [Fact]
    public void PushProgressSnapshot_AfterLiveConfirmsZero_AdoptsRealModuleAck()
    {
        using var worker = new BridgeWorker(new DolphinBridge());
        worker.SetConnected(true, 1, "Host", true);
        worker.PushProgressSnapshot(42, new byte[] { 1, 2, 3, 4 });

        // Mailbox matches Push intent — release the heal-staged latch.
        var parked = CommBuffer.CreateDefault();
        parked.Magic = ProtocolConstants.Magic;
        parked.ProgressSnapshotHostSeq = 42;
        parked.ProgressSnapshotModuleAppliedSeq = 0;
        parked.ProgressSnapshotPayloadLen = 4;
        parked.ProgressSnapshotPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
        parked.ProgressSnapshotPayload[0] = 1;
        worker.DebugAdoptLiveBuffer(parked);

        // Module finished bulk-apply — catch-up must see the real ack.
        var applied = CommBuffer.CreateDefault();
        applied.Magic = ProtocolConstants.Magic;
        applied.ProgressSnapshotHostSeq = 42;
        applied.ProgressSnapshotModuleAppliedSeq = 42;
        applied.ProgressSnapshotPayloadLen = 4;
        applied.ProgressSnapshotPayload = parked.ProgressSnapshotPayload;
        worker.DebugAdoptLiveBuffer(applied);

        Assert.True(worker.TryGetProgressSnapshotAck(out var host, out var moduleApplied));
        Assert.Equal(42u, host);
        Assert.Equal(42u, moduleApplied);
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
