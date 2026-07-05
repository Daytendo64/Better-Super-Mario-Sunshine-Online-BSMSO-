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
    public void TagDetection_TagsImmediatelyOnStart()
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
    public void OnPlayerDisconnected_EndsRoundWhenLastHiderLeaves()
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

public sealed class HideSeekRandomTagExemptionTests
{
    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 3)]
    [InlineData(8, 3)]
    [InlineData(9, 4)]
    [InlineData(10, 4)]
    public void GetExemptRounds_ScalesWithPlayerCount(int playerCount, int expectedRounds)
    {
        Assert.Equal(expectedRounds, HideSeekRandomTagExemption.GetExemptRounds(playerCount));
    }

    [Fact]
    public void RegisterPick_ExemptsPreviousSeekerForOneRoundWithFourPlayers()
    {
        var rounds = new Dictionary<byte, int>();

        HideSeekRandomTagExemption.RegisterPick(rounds, 0, 4);
        Assert.Contains((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));

        HideSeekRandomTagExemption.RegisterPick(rounds, 1, 4);
        Assert.DoesNotContain((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)1, HideSeekRandomTagExemption.GetExemptSlots(rounds));
    }

    [Fact]
    public void RegisterPick_ExemptsPreviousSeekerForTwoRoundsWithSixPlayers()
    {
        var rounds = new Dictionary<byte, int>();

        HideSeekRandomTagExemption.RegisterPick(rounds, 0, 6);
        Assert.Contains((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));

        HideSeekRandomTagExemption.RegisterPick(rounds, 1, 6);
        Assert.Contains((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)1, HideSeekRandomTagExemption.GetExemptSlots(rounds));

        HideSeekRandomTagExemption.RegisterPick(rounds, 2, 6);
        Assert.DoesNotContain((byte)0, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)1, HideSeekRandomTagExemption.GetExemptSlots(rounds));
        Assert.Contains((byte)2, HideSeekRandomTagExemption.GetExemptSlots(rounds));
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
