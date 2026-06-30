using SMSO.Net;
using SMSO.Server;
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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
