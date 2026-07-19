using SMSO.Net;
using SMSO.Net.MarioPack;
using SMSO.Server;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SMSO.Tests;

[Collection("Networking")]
public class IntegrationTests
{
    [Fact]
    public async Task TwoClients_CanJoin_AndReceiveRoster()
    {
        var levels = new LevelCatalog();
        var port = GetFreePort();
        var server = new GameServer(levels) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);

        try
        {
            var client1 = new NetClient();
            var client2 = new NetClient();
            var roster2 = new TaskCompletionSource<PlayerRosterEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            client2.RosterUpdated += e =>
            {
                if (e.Length >= 2)
                    roster2.TrySetResult(e);
            };

            await client1.ConnectAsync("127.0.0.1", port, "PlayerOne");
            await client2.ConnectAsync("127.0.0.1", port, "PlayerTwo");

            var roster = await roster2.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(client1.IsConnected);
            Assert.True(client2.IsConnected);
            Assert.NotEqual(client1.AssignedSlot, client2.AssignedSlot);
            Assert.Equal(2, roster.Length);

            await client1.DisconnectAsync();
            await client2.DisconnectAsync();
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task MarioModelIntent_UpdatesRosterBeforeHeartbeat()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog());
        server.Start(port);
        using var sender = new NetClient();
        using var observer = new NetClient();
        try
        {
            await sender.ConnectAsync("127.0.0.1", port, "ModelSender");
            await observer.ConnectAsync("127.0.0.1", port, "ModelObserver");
            await WaitForRosterCountAsync(observer, 2);

            var updated = new TaskCompletionSource<PlayerRosterEntry[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var observedModels = new ConcurrentQueue<string>();
            observer.RosterUpdated += roster =>
            {
                var senderEntry = roster.FirstOrDefault(
                    entry => entry.Slot == sender.AssignedSlot);
                if (senderEntry != null)
                    observedModels.Enqueue(senderEntry.MarioModelId);
                if (senderEntry?.MarioModelId == "4ef21b6e")
                {
                    updated.TrySetResult(roster);
                }
            };

            // Rapid changes may race at the client's serialized TCP writer. The
            // sequenced intent policy must leave the newest choice authoritative.
            sender.SetMarioModelId("aabbccdd");
            sender.SetMarioModelId("4ef21b6e");
            var roster = await updated.Task.WaitAsync(TimeSpan.FromMilliseconds(1500));
            Assert.Contains(roster, entry => entry.Slot == sender.AssignedSlot &&
                                             entry.MarioModelId == "4ef21b6e");
            await Task.Delay(300);
            Assert.Equal("4ef21b6e", observedModels.Last());

            await sender.DisconnectAsync();
            await observer.DisconnectAsync();
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task Heartbeat_UpdatesRosterModel_AfterSequencedIntent()
    {
        // After a sequenced MarioModelIntent is accepted, heartbeats must still
        // advance MarioModelId. Otherwise a dropped/coalesced later intent leaves
        // remotes permanently frozen on the last accepted appearance.
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog());
        server.Start(port);
        using var raw = new TcpClient();
        using var observer = new NetClient();
        try
        {
            await observer.ConnectAsync("127.0.0.1", port, "HbObserver");
            await raw.ConnectAsync(IPAddress.Loopback, port);
            var stream = raw.GetStream();
            await stream.WriteAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()));
            await stream.WriteAsync(PacketSerializer.BuildJoinRequest("HbSender", "retail00"));

            byte senderSlot = 0xFF;
            using (var joinTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                while (senderSlot == 0xFF)
                {
                    var frame = await ReadTcpFrameAsync(stream, joinTimeout.Token);
                    Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
                    if (id == TcpPacketId.JoinAccepted && payload.Length >= 1)
                        senderSlot = payload[0];
                }
            }

            await WaitForRosterCountAsync(observer, 2);

            await stream.WriteAsync(PacketSerializer.BuildMarioModelIntent("aabbccdd", 1));
            await WaitForRosterModelAsync(observer, senderSlot, "aabbccdd");

            // Simulate a lost intent: heartbeat advertises the newer selection.
            var heartbeat = new byte[10 + ProtocolConstants.MarioModelIdSize];
            BinaryPrimitives.WriteInt64LittleEndian(heartbeat.AsSpan(0, 8),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            BinaryPrimitives.WriteUInt16LittleEndian(heartbeat.AsSpan(8, 2), 12);
            CharacterPack.EncodeModelId("4ef21b6e").CopyTo(heartbeat, 10);
            await stream.WriteAsync(PacketSerializer.BuildHeartbeat(heartbeat));

            await WaitForRosterModelAsync(observer, senderSlot, "4ef21b6e");
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task MarioModelIntent_StaleSequence_IsRejected_ButHeartbeatCanAdvance()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog());
        server.Start(port);
        using var raw = new TcpClient();
        using var observer = new NetClient();
        try
        {
            await observer.ConnectAsync("127.0.0.1", port, "SeqObserver");
            await raw.ConnectAsync(IPAddress.Loopback, port);
            var stream = raw.GetStream();
            await stream.WriteAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()));
            await stream.WriteAsync(PacketSerializer.BuildJoinRequest("SeqSender", null));

            byte senderSlot = 0xFF;
            using (var joinTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                while (senderSlot == 0xFF)
                {
                    var frame = await ReadTcpFrameAsync(stream, joinTimeout.Token);
                    Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
                    if (id == TcpPacketId.JoinAccepted && payload.Length >= 1)
                        senderSlot = payload[0];
                }
            }

            await WaitForRosterCountAsync(observer, 2);
            var observed = new ConcurrentQueue<string>();
            observer.RosterUpdated += roster =>
            {
                var entry = roster.FirstOrDefault(e => e.Slot == senderSlot);
                if (entry != null)
                    observed.Enqueue(entry.MarioModelId);
            };

            await stream.WriteAsync(PacketSerializer.BuildMarioModelIntent("aabbccdd", 5));
            await WaitForRosterModelAsync(observer, senderSlot, "aabbccdd");

            // Stale sequenced intent must not roll the roster back.
            await stream.WriteAsync(PacketSerializer.BuildMarioModelIntent("deadbeef", 4));
            await Task.Delay(500);
            Assert.DoesNotContain("deadbeef", observed);

            var heartbeat = new byte[10 + ProtocolConstants.MarioModelIdSize];
            BinaryPrimitives.WriteInt64LittleEndian(heartbeat.AsSpan(0, 8),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            CharacterPack.EncodeModelId("4ef21b6e").CopyTo(heartbeat, 10);
            await stream.WriteAsync(PacketSerializer.BuildHeartbeat(heartbeat));
            await WaitForRosterModelAsync(observer, senderSlot, "4ef21b6e");
            Assert.DoesNotContain("deadbeef", observed);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task MarioModelIntent_BeforeJoin_IsNotAuthorized()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog());
        server.Start(port);
        using var raw = new TcpClient();
        try
        {
            await raw.ConnectAsync(IPAddress.Loopback, port);
            var stream = raw.GetStream();
            await stream.WriteAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()));
            await stream.WriteAsync(PacketSerializer.BuildMarioModelIntent("aabbccdd", 99));
            await stream.WriteAsync(PacketSerializer.BuildJoinRequest("RawIntent", null));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            byte[]? acceptedPayload = null;
            while (acceptedPayload == null)
            {
                var frame = await ReadTcpFrameAsync(stream, timeout.Token);
                Assert.True(PacketSerializer.TryUnwrapTcp(
                    frame, out var id, out var payload));
                if (id == TcpPacketId.JoinAccepted)
                    acceptedPayload = payload;
            }

            Assert.True(acceptedPayload.Length >= 2 + ProtocolConstants.RosterEntrySize);
            Assert.Equal(string.Empty, CharacterPack.DecodeModelId(
                acceptedPayload.AsSpan(2 + 22, ProtocolConstants.MarioModelIdSize)));
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task PunctuationUsername_IsAccepted()
    {
        var server = new GameServer(new LevelCatalog());
        var port = GetFreePort();
        server.Start(port);
        try
        {
            var client = new NetClient();
            await client.ConnectAsync("127.0.0.1", port, "Mr.Smith!");
            Assert.True(client.IsConnected);
            await client.DisconnectAsync();
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task DuplicateUsername_IsRejected()
    {
        var server = new GameServer(new LevelCatalog());
        var port = GetFreePort();
        server.Start(port);
        try
        {
            var c1 = new NetClient();
            var c2 = new NetClient();

            await c1.ConnectAsync("127.0.0.1", port, "SameName");
            var ex = await Assert.ThrowsAsync<NetJoinRejectedException>(() =>
                c2.ConnectAsync("127.0.0.1", port, "SameName"));
            Assert.Equal(JoinRejectReason.NameTaken, ex.Reason);

            await c1.DisconnectAsync();
            c2.Dispose();
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task WrongModBuildId_IsRejectedAsVersionMismatch()
    {
        var server = new GameServer(new LevelCatalog());
        var port = GetFreePort();
        server.Start(port);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()));
            // Drain HandshakeAck
            var ackBuf = new byte[64];
            _ = await stream.ReadAsync(ackBuf);

            var wrongBuild = (ushort)(ProtocolConstants.ModBuildId == ushort.MaxValue
                ? ProtocolConstants.ModBuildId - 1
                : ProtocolConstants.ModBuildId + 1);
            await stream.WriteAsync(PacketSerializer.BuildJoinRequest("WrongBuild", null, wrongBuild));

            var reject = await ReadJoinRejectedReasonAsync(stream);
            Assert.Equal(JoinRejectReason.VersionMismatch, reject);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task LegacyJoinWithoutModBuildId_IsRejectedAsVersionMismatch()
    {
        var server = new GameServer(new LevelCatalog());
        var port = GetFreePort();
        server.Start(port);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()));
            var ackBuf = new byte[64];
            _ = await stream.ReadAsync(ackBuf);

            // Build a current JoinRequest then truncate ModBuildId (legacy 24-byte payload).
            var full = PacketSerializer.BuildJoinRequest("LegacyClient", "retail00");
            Assert.True(PacketSerializer.TryUnwrapTcp(full, out _, out var payload));
            var legacyLen = 16 + ProtocolConstants.MarioModelIdSize;
            var legacyPayload = new byte[legacyLen];
            Array.Copy(payload, 0, legacyPayload, 0, legacyLen);
            await stream.WriteAsync(PacketSerializer.WrapTcp(TcpPacketId.JoinRequest, legacyPayload));

            var reject = await ReadJoinRejectedReasonAsync(stream);
            Assert.Equal(JoinRejectReason.VersionMismatch, reject);
        }
        finally
        {
            server.Stop();
        }
    }

    private static async Task<JoinRejectReason> ReadJoinRejectedReasonAsync(NetworkStream stream)
    {
        var pending = new List<byte>();
        var scratch = new byte[256];
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (stream.DataAvailable || pending.Count == 0)
            {
                var read = await stream.ReadAsync(scratch);
                if (read <= 0)
                    break;
                for (var i = 0; i < read; i++)
                    pending.Add(scratch[i]);
            }

            while (pending.Count >= 9)
            {
                var buffer = pending.ToArray();
                if (!PacketSerializer.TryGetTcpFrameLength(buffer, out var frameLength) ||
                    pending.Count < frameLength)
                    break;

                var frame = new byte[frameLength];
                Array.Copy(buffer, 0, frame, 0, frameLength);
                if (PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload))
                {
                    pending.RemoveRange(0, frameLength);
                    if (id == TcpPacketId.JoinRejected && payload.Length > 0)
                        return (JoinRejectReason)payload[0];
                    continue;
                }

                pending.RemoveAt(0);
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Did not receive JoinRejected.");
    }

    [Fact]
    public async Task MaxPlayers_AssignDistinctSlots_AndRejectOverflow()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);
        var clients = new List<NetClient>();

        try
        {
            for (var i = 0; i < ProtocolConstants.StableMaxPlayers; i++)
            {
                var client = new NetClient();
                clients.Add(client);
                await client.ConnectAsync("127.0.0.1", port, $"Player{i + 1}");
            }

            var expectedSlots = Enumerable.Range(0, ProtocolConstants.StableMaxPlayers)
                .Select(i => (byte)i).ToArray();
            Assert.Equal(expectedSlots, clients.Select(c => c.AssignedSlot).OrderBy(s => s).ToArray());

            var overflow = new NetClient();
            var ex = await Assert.ThrowsAsync<NetJoinRejectedException>(() =>
                overflow.ConnectAsync("127.0.0.1", port, "PlayerOverflow"));
            Assert.Equal(JoinRejectReason.Full, ex.Reason);
            overflow.Dispose();
        }
        finally
        {
            foreach (var client in clients)
                await client.DisconnectAsync();
            server.Stop();
        }
    }

    [Fact]
    public async Task FourPlayerServer_ReusesVacatedSlot()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);
        var clients = new List<NetClient>();
        NetClient? replacement = null;

        try
        {
            for (var i = 0; i < ProtocolConstants.StableMaxPlayers; i++)
            {
                var client = new NetClient();
                clients.Add(client);
                await client.ConnectAsync("127.0.0.1", port, $"Slot{i}");
            }

            var rosterAfterLeave = WaitForRosterCountAsync(clients[0], ProtocolConstants.StableMaxPlayers - 1);
            var releasedSlot = clients[1].AssignedSlot;
            await clients[1].DisconnectAsync();
            clients[1].Dispose();
            clients.RemoveAt(1);

            await rosterAfterLeave;

            replacement = new NetClient();
            var rosterAfterReplacement = WaitForRosterCountAsync(replacement, ProtocolConstants.StableMaxPlayers);
            await replacement.ConnectAsync("127.0.0.1", port, "Replacement");
            await rosterAfterReplacement;

            Assert.Equal(releasedSlot, replacement.AssignedSlot);
        }
        finally
        {
            replacement?.Dispose();
            foreach (var client in clients)
                client.Dispose();
            server.Stop();
        }
    }

    [Fact]
    public async Task FourClients_RelayUdpSnapshotsToEveryOtherClient()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);
        var clients = new List<NetClient>();
        var receivedByClient = new ConcurrentDictionary<byte, ConcurrentDictionary<byte, PlayerSnapshot>>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            for (var i = 0; i < ProtocolConstants.StableMaxPlayers; i++)
            {
                var client = new NetClient();
                clients.Add(client);
                await client.ConnectAsync("127.0.0.1", port, $"UdpPlayer{i}");

                var receiverSlot = client.AssignedSlot;
                receivedByClient[receiverSlot] = new ConcurrentDictionary<byte, PlayerSnapshot>();
                client.SnapshotReceived += (senderSlot, snap) =>
                {
                    if (senderSlot == receiverSlot)
                        return;

                    receivedByClient[receiverSlot][senderSlot] = snap;
                    if (receivedByClient.Count == ProtocolConstants.StableMaxPlayers &&
                        receivedByClient.Values.All(v => v.Count >= ProtocolConstants.StableMaxPlayers - 1))
                    {
                        allReceived.TrySetResult();
                    }
                };
            }

            for (var tick = 0; tick < 12 && !allReceived.Task.IsCompleted; tick++)
            {
                foreach (var client in clients)
                {
                    var slot = client.AssignedSlot;
                    client.PublishSnapshot(new PlayerSnapshot
                    {
                        Connected = 1,
                        Slot = slot,
                        StageId = 1,
                        EpisodeId = 0,
                        Position = new Vec3 { X = slot * 10.0f, Y = 50.0f, Z = slot * 20.0f },
                        Name = new byte[16],
                    });
                }

                await Task.Delay(ProtocolConstants.UdpSnapshotIntervalMs);
            }

            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));

            foreach (var client in clients)
            {
                var receiverSlot = client.AssignedSlot;
                var senders = receivedByClient[receiverSlot].Keys.OrderBy(s => s).ToArray();
                var expected = clients.Select(c => c.AssignedSlot)
                    .Where(s => s != receiverSlot)
                    .OrderBy(s => s)
                    .ToArray();
                Assert.Equal(expected, senders);
            }
        }
        finally
        {
            foreach (var client in clients)
                client.Dispose();
            server.Stop();
        }
    }

    [Fact]
    public async Task SameUsername_CanReconnectImmediately_AfterDisconnect()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);

        try
        {
            var first = new NetClient();
            await first.ConnectAsync("127.0.0.1", port, "ReconnectMe");
            var firstSlot = first.AssignedSlot;
            Assert.True(first.IsConnected);

            await first.DisconnectAsync();
            first.Dispose();

            var second = new NetClient();
            await second.ConnectAsync("127.0.0.1", port, "ReconnectMe");
            Assert.True(second.IsConnected);
            Assert.Equal(firstSlot, second.AssignedSlot);

            await second.DisconnectAsync();
            second.Dispose();

            var third = new NetClient();
            await third.ConnectAsync("127.0.0.1", port, "ReconnectMe");
            Assert.True(third.IsConnected);
            Assert.Equal(firstSlot, third.AssignedSlot);

            await third.DisconnectAsync();
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task RapidReconnectCycle_ThreeTimesWithoutNameTaken()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var client = new NetClient();
                await client.ConnectAsync("127.0.0.1", port, "RapidUser");
                Assert.True(client.IsConnected);
                client.PublishSnapshot(new PlayerSnapshot
                {
                    Connected = 1,
                    Slot = client.AssignedSlot,
                    StageId = 1,
                    EpisodeId = 0,
                    Name = new byte[16],
                });
                client.SendSnapshotNow();
                await client.DisconnectAsync();
                client.Dispose();
            }
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task UdpRelay_SurvivesAbruptClientDisconnect_AndRelaysAfterReconnect()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);

        NetClient? replacement = null;
        try
        {
            var first = new NetClient();
            var observer = new NetClient();
            await first.ConnectAsync("127.0.0.1", port, "Dropper");
            await observer.ConnectAsync("127.0.0.1", port, "Observer");

            var sawDropperSnapshot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            observer.SnapshotReceived += (slot, _) =>
            {
                if (slot == first.AssignedSlot)
                    sawDropperSnapshot.TrySetResult(true);
            };

            first.PublishSnapshot(new PlayerSnapshot
            {
                Connected = 1,
                Slot = first.AssignedSlot,
                StageId = 1,
                Name = new byte[16],
            });
            first.SendSnapshotNow();
            await sawDropperSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(8));

            first.ForceDispose();

            replacement = new NetClient();
            var sawReplacement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            observer.SnapshotReceived += (slot, _) =>
            {
                if (slot == replacement.AssignedSlot)
                    sawReplacement.TrySetResult(true);
            };

            await replacement.ConnectAsync("127.0.0.1", port, "Replacement");
            replacement.PublishSnapshot(new PlayerSnapshot
            {
                Connected = 1,
                Slot = replacement.AssignedSlot,
                StageId = 2,
                Name = new byte[16],
            });
            replacement.SendSnapshotNow();

            await sawReplacement.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await observer.DisconnectAsync();
            await replacement.DisconnectAsync();
        }
        finally
        {
            replacement?.Dispose();
            server.Stop();
        }
    }

    [Fact]
    public async Task NonHostDisconnect_KeepsHideSeekTagActive()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);

        try
        {
            var host = new NetClient();
            var guest = new NetClient();
            var observer = new NetClient();

            await host.ConnectAsync("127.0.0.1", port, "Host");
            await guest.ConnectAsync("127.0.0.1", port, "Guest");
            await observer.ConnectAsync("127.0.0.1", port, "Observer");
            await WaitForRosterCountAsync(observer, 3);

            server.SetGameMode(GameMode.HideSeek);
            server.SetHideSeekRoles(new Dictionary<byte, HideSeekRole>
            {
                [host.AssignedSlot] = HideSeekRole.Seeker,
                [guest.AssignedSlot] = HideSeekRole.Hider,
                [observer.AssignedSlot] = HideSeekRole.Hider,
            });
            Assert.True(server.TryStartHideSeekTag(out _));

            await guest.DisconnectAsync();
            await WaitForRosterCountAsync(host, 2);

            var state = server.GetGameModeState();
            Assert.Equal(GameMode.HideSeek, state.GameMode);
            Assert.True(state.TagActive);

            await host.DisconnectAsync();
            await observer.DisconnectAsync();
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task HostDisconnect_StopsHideSeek()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = ProtocolConstants.StableMaxPlayers };
        server.Start(port);

        try
        {
            var host = new NetClient();
            var guest = new NetClient();

            await host.ConnectAsync("127.0.0.1", port, "Host");
            await guest.ConnectAsync("127.0.0.1", port, "Guest");
            await WaitForRosterCountAsync(guest, 2);

            server.SetGameMode(GameMode.HideSeek);
            server.SetHideSeekRoles(new Dictionary<byte, HideSeekRole>
            {
                [host.AssignedSlot] = HideSeekRole.Seeker,
                [guest.AssignedSlot] = HideSeekRole.Hider,
            });
            Assert.True(server.TryStartHideSeekTag(out _));

            await host.DisconnectAsync();
            await WaitForRosterCountAsync(guest, 1);

            var state = server.GetGameModeState();
            Assert.Equal(GameMode.Normal, state.GameMode);
            Assert.False(state.TagActive);

            await guest.DisconnectAsync();
        }
        finally
        {
            server.Stop();
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public async Task FullLobby_ImmediateReconnectSameName_Succeeds(int maxPlayers)
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = maxPlayers };
        server.Start(port);
        var clients = new List<NetClient>();
        NetClient? reconnected = null;

        try
        {
            for (var i = 0; i < maxPlayers; i++)
                clients.Add(await ConnectAsync(port, $"Plr{i}"));

            var victim = clients[^1];
            var priorSlot = victim.AssignedSlot;
            var victimName = $"Plr{maxPlayers - 1}";

            await victim.DisconnectAsync();
            victim.Dispose();
            clients.RemoveAt(clients.Count - 1);

            // Immediate reconnect — must not stick on Full / NameTaken at capacity.
            reconnected = new NetClient();
            await reconnected.ConnectAsync("127.0.0.1", port, victimName);
            Assert.True(reconnected.IsConnected);
            Assert.Equal(priorSlot, reconnected.AssignedSlot);
            Assert.Equal(maxPlayers, server.SessionCount);
        }
        finally
        {
            reconnected?.Dispose();
            foreach (var c in clients)
                c.Dispose();
            server.Stop();
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public async Task FullLobby_AbruptDisconnectGhost_ReconnectReplaces(int maxPlayers)
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = maxPlayers };
        server.Start(port);
        var clients = new List<NetClient>();
        NetClient? reconnected = null;

        try
        {
            for (var i = 0; i < maxPlayers; i++)
                clients.Add(await ConnectAsync(port, $"Ghost{i}"));

            var victim = clients[^1];
            var priorSlot = victim.AssignedSlot;
            var victimName = $"Ghost{maxPlayers - 1}";

            // Abrupt close leaves a half-open/ghost session until Poll/reclaim runs.
            victim.ForceDispose();

            reconnected = new NetClient();
            // Retry briefly: FIN may need a moment to become Poll-detectable on the server.
            Exception? lastError = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    await reconnected.ConnectAsync("127.0.0.1", port, victimName);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    reconnected.Dispose();
                    reconnected = new NetClient();
                    await Task.Delay(50);
                }
            }

            Assert.Null(lastError);
            Assert.True(reconnected!.IsConnected);
            Assert.Equal(priorSlot, reconnected.AssignedSlot);
            Assert.True(server.SessionCount <= maxPlayers);
        }
        finally
        {
            reconnected?.Dispose();
            foreach (var c in clients)
            {
                try { c.Dispose(); } catch { /* already force-disposed */ }
            }
            server.Stop();
        }
    }

    [Fact]
    public async Task AbandonedHandshake_DoesNotPermanentlyBlockJoinAtCapacity()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = 2 };
        server.Start(port);

        TcpClient? abandoned = null;
        NetClient? keeper = null;
        NetClient? joiner = null;

        try
        {
            keeper = await ConnectAsync(port, "Keeper");

            // Occupy the last slot with Handshake only (no JoinRequest).
            abandoned = new TcpClient();
            await abandoned.ConnectAsync(IPAddress.Loopback, port);
            var stream = abandoned.GetStream();
            var handshake = PacketSerializer.BuildHandshake(Guid.NewGuid());
            await stream.WriteAsync(handshake);

            // Wait until the server has accepted the abandoned handshake.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (server.SessionCount < 2 && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Equal(2, server.SessionCount);

            // Before grace expires, a new join should be rejected as Full.
            joiner = new NetClient();
            var fullEx = await Assert.ThrowsAsync<NetJoinRejectedException>(() =>
                joiner.ConnectAsync("127.0.0.1", port, "Joiner"));
            Assert.Equal(JoinRejectReason.Full, fullEx.Reason);
            joiner.Dispose();
            joiner = null;

            // After abandoned-handshake grace, AssignSlot must reclaim and allow join.
            await Task.Delay(ProtocolConstants.AbandonedHandshakeGraceMs + 250);

            joiner = new NetClient();
            await joiner.ConnectAsync("127.0.0.1", port, "Joiner");
            Assert.True(joiner.IsConnected);
            Assert.Equal(2, server.SessionCount);
            Assert.NotEqual(keeper.AssignedSlot, joiner.AssignedSlot);
        }
        finally
        {
            try { abandoned?.Close(); } catch { }
            joiner?.Dispose();
            keeper?.Dispose();
            server.Stop();
        }
    }

    [Fact]
    public async Task StaleGhostSameName_ReconnectReplacesAndSucceeds()
    {
        var port = GetFreePort();
        var server = new GameServer(new LevelCatalog()) { MaxPlayers = 4 };
        server.Start(port);

        NetClient? reconnected = null;
        try
        {
            var fillers = new List<NetClient>();
            for (var i = 0; i < 3; i++)
                fillers.Add(await ConnectAsync(port, $"Fill{i}"));

            var ghost = await ConnectAsync(port, "GhostName");
            var priorSlot = ghost.AssignedSlot;
            ghost.ForceDispose();

            reconnected = new NetClient();
            Exception? lastError = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    await reconnected.ConnectAsync("127.0.0.1", port, "GhostName");
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    reconnected.Dispose();
                    reconnected = new NetClient();
                    await Task.Delay(50);
                }
            }

            Assert.Null(lastError);
            Assert.True(reconnected!.IsConnected);
            Assert.Equal(priorSlot, reconnected.AssignedSlot);

            foreach (var f in fillers)
                await f.DisconnectAsync();
            await reconnected.DisconnectAsync();
        }
        finally
        {
            reconnected?.Dispose();
            server.Stop();
        }
    }

    private static async Task<NetClient> ConnectAsync(int port, string name)
    {
        var client = new NetClient();
        await client.ConnectAsync("127.0.0.1", port, name);
        return client;
    }

    private static async Task<byte[]> ReadTcpFrameAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[9];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(7, 2));
        var frame = new byte[9 + payloadLength + 4];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(
            frame.AsMemory(9, payloadLength + 4), cancellationToken);
        return frame;
    }

    private static async Task<PlayerRosterEntry[]> WaitForRosterCountAsync(NetClient client, int count)
    {
        var tcs = new TaskCompletionSource<PlayerRosterEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RosterUpdated += roster =>
        {
            if (roster.Length == count)
                tcs.TrySetResult(roster);
        };

        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task WaitForRosterModelAsync(NetClient client, byte slot, string modelId)
    {
        var want = CharacterPack.NormalizeModelId(modelId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRoster(PlayerRosterEntry[] roster)
        {
            var entry = roster.FirstOrDefault(e => e.Slot == slot);
            if (entry != null &&
                string.Equals(entry.MarioModelId, want, StringComparison.Ordinal))
            {
                tcs.TrySetResult(true);
            }
        }

        client.RosterUpdated += OnRoster;
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            client.RosterUpdated -= OnRoster;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
