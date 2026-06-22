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
    public async Task FourClients_AssignDistinctSlots_AndRejectFifth()
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

            Assert.Equal(new byte[] { 0, 1, 2, 3 }, clients.Select(c => c.AssignedSlot).OrderBy(s => s).ToArray());

            var fifth = new NetClient();
            var ex = await Assert.ThrowsAsync<NetJoinRejectedException>(() =>
                fifth.ConnectAsync("127.0.0.1", port, "Player5"));
            Assert.Equal(JoinRejectReason.Full, ex.Reason);
            fifth.Dispose();
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

            await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));

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
