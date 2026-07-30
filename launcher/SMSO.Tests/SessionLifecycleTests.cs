using System.Net;
using System.Net.Sockets;
using SMSO.Launcher;
using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

/// <summary>
/// Host identity, connection teardown and multi-instance claims — the paths that decide
/// who may warp / change sync settings and which launcher owns which config file.
/// </summary>
[Collection("Networking")]
public sealed class SessionLifecycleTests
{
    /// <summary>Start a server on a port nobody else in this run is using.</summary>
    private static (GameServer Server, int Port) StartServer(Action<GameServer>? configure = null)
    {
        SocketException? last = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = TestPortAllocator.Next();
            var server = new GameServer(new LevelCatalog());
            configure?.Invoke(server);
            try
            {
                server.Start(port);
                return (server, port);
            }
            catch (SocketException ex)
            {
                last = ex;
                server.Dispose();
            }
        }

        throw new InvalidOperationException("No free test port available.", last);
    }

    [Fact]
    public void ShouldGrantHostSession_RemoteClientCannotStealHostDuringReservation()
    {
        // P0: a client that beat the hosting launcher's self-join used to become host.
        Assert.False(GameServer.ShouldGrantHostSession(
            HostConnectionKind.Remote, launcherHostClaimed: false, anyLiveHost: false,
            reservationActive: true, lobbyEmpty: true));
        Assert.False(GameServer.ShouldGrantHostSession(
            HostConnectionKind.LoopbackOtherProcess, launcherHostClaimed: false, anyLiveHost: false,
            reservationActive: true, lobbyEmpty: true));
    }

    [Fact]
    public void ShouldGrantHostSession_LauncherSelfJoinAlwaysWins()
    {
        // The hosting launcher's own loopback connection is the definitive host signal —
        // even after the window, and even if it arrives late (host reconnect).
        Assert.True(GameServer.ShouldGrantHostSession(
            HostConnectionKind.SameProcess, launcherHostClaimed: false, anyLiveHost: false,
            reservationActive: true, lobbyEmpty: false));
        Assert.True(GameServer.ShouldGrantHostSession(
            HostConnectionKind.SameProcess, launcherHostClaimed: true, anyLiveHost: false,
            reservationActive: false, lobbyEmpty: false));
        // But never two hosts at once.
        Assert.False(GameServer.ShouldGrantHostSession(
            HostConnectionKind.SameProcess, launcherHostClaimed: true, anyLiveHost: true,
            reservationActive: false, lobbyEmpty: false));
    }

    [Fact]
    public void ShouldGrantHostSession_UnverifiedLoopbackIsTheHostingFallback()
    {
        // Owner lookup unavailable: the launcher self-join is still the first loopback
        // connection into an empty lobby, so hosting keeps working.
        Assert.True(GameServer.ShouldGrantHostSession(
            HostConnectionKind.LoopbackUnverified, launcherHostClaimed: false, anyLiveHost: false,
            reservationActive: true, lobbyEmpty: true));
        // A later loopback client must not take it.
        Assert.False(GameServer.ShouldGrantHostSession(
            HostConnectionKind.LoopbackUnverified, launcherHostClaimed: false, anyLiveHost: false,
            reservationActive: true, lobbyEmpty: false));
        Assert.False(GameServer.ShouldGrantHostSession(
            HostConnectionKind.LoopbackUnverified, launcherHostClaimed: true, anyLiveHost: false,
            reservationActive: false, lobbyEmpty: true));
    }

    [Fact]
    public void ShouldGrantHostSession_DedicatedServerLetsFirstArrivalLeadAfterWindow()
    {
        // No launcher ever self-joins a dedicated ServerHost; once the window closes the
        // lobby must still have someone able to warp / change sync settings.
        Assert.True(GameServer.ShouldGrantHostSession(
            HostConnectionKind.Remote, launcherHostClaimed: false, anyLiveHost: false,
            reservationActive: false, lobbyEmpty: true));
        Assert.False(GameServer.ShouldGrantHostSession(
            HostConnectionKind.Remote, launcherHostClaimed: false, anyLiveHost: true,
            reservationActive: false, lobbyEmpty: false));
    }

    [Fact]
    public async Task RemoteClientJoiningBeforeHost_DoesNotBecomeHostAndLeavesSlotZeroFree()
    {
        // Every test connection is same-process; force the remote classification so the
        // "client wins the race to the accept loop" case is reproducible in-process.
        var (server, port) = StartServer(s =>
            s.ConnectionKindOverrideForTests = _ => HostConnectionKind.Remote);
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var early = new NetClient();
        try
        {
            await early.ConnectAsync("127.0.0.1", port, "EarlyClient");
            Assert.True(early.IsConnected);
            // Slot 0 is held for the host's self-join while the reservation stands.
            Assert.Equal(1, early.AssignedSlot);
            Assert.Null(server.HostSlot);

            // Now the hosting launcher self-joins and takes host regardless of arrival order.
            server.ConnectionKindOverrideForTests = _ => HostConnectionKind.SameProcess;
            var host = new NetClient();
            try
            {
                await host.ConnectAsync("127.0.0.1", port, "TheHost");
                Assert.Equal((byte)0, host.AssignedSlot);
                Assert.Equal((byte)0, server.HostSlot);
            }
            finally
            {
                await host.DisconnectAsync();
            }
        }
        finally
        {
            await early.DisconnectAsync();
            server.Stop();
        }
    }

    [Fact]
    public async Task HostConnectionClassifier_IdentifiesThisProcessesOwnLoopbackConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var accepted = await listener.AcceptTcpClientAsync();

            Assert.Equal(HostConnectionKind.SameProcess,
                HostConnectionClassifier.Classify(accepted, port));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HostSelfJoin_TakesHostWithoutAnyOverride()
    {
        // End-to-end host pinning: the launcher's own loopback connection is recognised
        // through the real classifier, and a second connection cannot become host.
        var (server, port) = StartServer();
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var host = new NetClient();
        var peer = new NetClient();
        try
        {
            await host.ConnectAsync("127.0.0.1", port, "SelfJoinHost");
            Assert.Equal((byte)0, server.HostSlot);

            await peer.ConnectAsync("127.0.0.1", port, "SecondPlayer");
            Assert.Equal((byte)0, server.HostSlot);
        }
        finally
        {
            await peer.DisconnectAsync();
            await host.DisconnectAsync();
            server.Stop();
        }
    }

    [Fact]
    public async Task NoHostAfterReservationWindow_PromotesLowestSlot()
    {
        var (server, port) = StartServer(s =>
            s.ConnectionKindOverrideForTests = _ => HostConnectionKind.Remote);
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var client = new NetClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port, "DedicatedPlayer");
            Assert.Null(server.HostSlot);

            server.ExpireHostClaimWindowForTests();
            server.MaybePromoteHost();
            Assert.Equal(client.AssignedSlot, server.HostSlot);
        }
        finally
        {
            await client.DisconnectAsync();
            server.Stop();
        }
    }

    [Fact]
    public async Task DedicatedServer_SkipsTheHostReservationForItsFirstJoiner()
    {
        // A dedicated ServerHost never gets a launcher self-join, so holding slot 0 only
        // left the lobby without anyone able to warp or change sync settings.
        var (server, port) = StartServer(s =>
        {
            s.IsDedicatedServer = true;
            s.ConnectionKindOverrideForTests = _ => HostConnectionKind.Remote;
        });
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var client = new NetClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port, "FirstJoiner");
            Assert.Equal((byte)0, client.AssignedSlot);
            Assert.Equal((byte)0, server.HostSlot);
        }
        finally
        {
            await client.DisconnectAsync();
            server.Stop();
        }
    }

    [Fact]
    public async Task LauncherHostedServer_StillReservesSlotZeroAgainstRemoteClients()
    {
        // Anti-hijack guarantee for launcher hosting must survive the dedicated opt-out.
        var (server, port) = StartServer(s =>
        {
            s.IsDedicatedServer = false;
            s.ConnectionKindOverrideForTests = _ => HostConnectionKind.Remote;
        });
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var client = new NetClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port, "RemoteRacer");
            Assert.Equal(1, client.AssignedSlot);
            Assert.Null(server.HostSlot);
        }
        finally
        {
            await client.DisconnectAsync();
            server.Stop();
        }
    }

    [Fact]
    public async Task FullLobbyReject_ClosesTheRejectedConnection()
    {
        var (server, port) = StartServer(s => s.MaxPlayers = 2);
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var first = new NetClient();
        var second = new NetClient();
        try
        {
            await first.ConnectAsync("127.0.0.1", port, "PlayerOne");
            await second.ConnectAsync("127.0.0.1", port, "PlayerTwo");
            Assert.Equal(2, server.SessionCount);

            using var rejected = new TcpClient();
            await rejected.ConnectAsync(IPAddress.Loopback, port);
            var stream = rejected.GetStream();
            await stream.WriteAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()));

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await ReadOneFrameAsync(stream, readCts.Token);
            Assert.True(PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload));
            Assert.Equal(TcpPacketId.JoinRejected, id);
            Assert.Equal((byte)JoinRejectReason.Full, payload[0]);

            // The rejected connection must be torn down, not leaked half-open: a reconnect
            // storm at a full lobby otherwise piles up sockets and HandleClient tasks.
            var closed = await stream.ReadAsync(new byte[64], readCts.Token);
            Assert.Equal(0, closed);
            Assert.Equal(2, server.SessionCount);
        }
        finally
        {
            await first.DisconnectAsync();
            await second.DisconnectAsync();
            server.Stop();
        }
    }

    [Fact]
    public async Task TransportTeardown_ClearsIsConnectedBeforeDisconnectedHandlersRun()
    {
        var (server, port) = StartServer();
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var client = new NetClient();
        var connectedDuringHandler = true;
        var disconnectCount = 0;
        client.Disconnected += _ =>
        {
            disconnectCount++;
            // Must already be false — previously UDP Dead left IsConnected sticky here.
            connectedDuringHandler = client.IsConnected;
        };

        try
        {
            await client.ConnectAsync("127.0.0.1", port, "StickyCheck");
            Assert.True(client.IsConnected);

            client.BeginTransportTeardownForTests();
            Assert.False(client.IsConnected);
            Assert.False(connectedDuringHandler);
            Assert.Equal(1, disconnectCount);

            // One-shot: a second dead signal must not re-fire Disconnected.
            client.BeginTransportTeardownForTests();
            Assert.Equal(1, disconnectCount);

            client.ForceDispose();
            await client.ConnectAsync("127.0.0.1", port, "StickyCheck");
            Assert.True(client.IsConnected);
        }
        finally
        {
            await client.DisconnectAsync();
            client.ForceDispose();
            server.Stop();
        }
    }

    [Fact]
    public async Task ServerStop_ThenImmediateRehost_FiveTimes_SamePort()
    {
        var port = TestPortAllocator.Next();
        var server = new GameServer(new LevelCatalog());
        try
        {
            for (var i = 0; i < 5; i++)
            {
                server.Start(port);
                await server.WaitUntilAcceptingAsync(timeoutMs: 2000);
                Assert.True(server.IsRunning);

                var client = new NetClient();
                await client.ConnectAsync("127.0.0.1", port, $"Rehost{i}");
                Assert.True(client.IsConnected);
                await client.DisconnectAsync();
                client.ForceDispose();

                server.NotifyShutdown();
                server.Stop();
                Assert.False(server.IsRunning);
            }
        }
        finally
        {
            if (server.IsRunning)
                server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public void SessionLifecycle_TransientPhasesBlockHostAndConnect()
    {
        Assert.True(SessionLifecycle.IsTransient(SessionLifecyclePhase.Hosting));
        Assert.True(SessionLifecycle.IsTransient(SessionLifecyclePhase.Stopping));
        Assert.False(SessionLifecycle.CanHostOrConnect(SessionLifecyclePhase.Hosting));
        Assert.False(SessionLifecycle.CanHostOrConnect(SessionLifecyclePhase.Stopping));
        Assert.False(SessionLifecycle.CanDisconnect(SessionLifecyclePhase.Hosting));
        Assert.False(SessionLifecycle.CanDisconnect(SessionLifecyclePhase.Connecting));
    }

    private static async Task<byte[]> ReadOneFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var pending = new List<byte>();
        var buffer = new byte[512];
        while (true)
        {
            if (pending.Count >= 13)
            {
                var header = pending.GetRange(0, 13).ToArray();
                if (PacketSerializer.TryGetTcpFrameLength(header, out var total) &&
                    pending.Count >= total)
                {
                    return pending.GetRange(0, total).ToArray();
                }
            }

            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0)
                throw new IOException("Connection closed before a full frame arrived.");
            pending.AddRange(buffer.AsSpan(0, read).ToArray());
        }
    }

    [Fact]
    public void SessionLifecycle_IdleAloneEnablesHostAndConnect()
    {
        Assert.True(SessionLifecycle.CanHostOrConnect(SessionLifecyclePhase.Idle));
        Assert.False(SessionLifecycle.CanDisconnect(SessionLifecyclePhase.Idle));

        foreach (var phase in new[]
                 {
                     SessionLifecyclePhase.Connecting,
                     SessionLifecyclePhase.Connected,
                     SessionLifecyclePhase.Disconnecting,
                     SessionLifecyclePhase.Hosting,
                     SessionLifecyclePhase.Hosted,
                     SessionLifecyclePhase.Stopping,
                 })
        {
            Assert.False(SessionLifecycle.CanHostOrConnect(phase));
        }

        Assert.True(SessionLifecycle.CanDisconnect(SessionLifecyclePhase.Connected));
        Assert.True(SessionLifecycle.CanDisconnect(SessionLifecyclePhase.Hosted));
        Assert.False(SessionLifecycle.CanDisconnect(SessionLifecyclePhase.Connecting));
        Assert.False(SessionLifecycle.CanDisconnect(SessionLifecyclePhase.Stopping));
    }

    [Fact]
    public async Task DisconnectThenReconnect_SamePort_SucceedsWithoutRestart()
    {
        var (server, port) = StartServer();
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var client = new NetClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", port, $"Cycle{cycle}");
                Assert.True(client.IsConnected);
                await client.DisconnectAsync();
                Assert.False(client.IsConnected);

                // Same NetClient instance after ForceDispose path used by session teardown.
                client.ForceDispose();
                await client.ConnectAsync("127.0.0.1", port, $"Cycle{cycle}");
                Assert.True(client.IsConnected);
            }
            finally
            {
                await client.DisconnectAsync();
                client.ForceDispose();
            }
        }

        server.Stop();
    }

    [Fact]
    public async Task ServerShutdown_ThenClientReconnects_AfterHostRebindsSamePort()
    {
        var port = TestPortAllocator.Next();
        var server = new GameServer(new LevelCatalog());
        server.Start(port);
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var client = new NetClient();
        var disconnected = new TaskCompletionSource<DisconnectReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => disconnected.TrySetResult(reason);

        try
        {
            await client.ConnectAsync("127.0.0.1", port, "StayOpen");
            Assert.True(client.IsConnected);

            server.NotifyShutdown();
            server.Stop();
            Assert.False(server.IsRunning);

            var reason = await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DisconnectReason.ServerShutdown, reason);

            client.ForceDispose();
            Assert.False(client.IsConnected);

            // Immediate rehost on the same port (exclusive bind + linger-0 + retry).
            server.Start(port);
            await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

            await client.ConnectAsync("127.0.0.1", port, "StayOpen");
            Assert.True(client.IsConnected);
        }
        finally
        {
            await client.DisconnectAsync();
            client.ForceDispose();
            if (server.IsRunning)
                server.Stop();
            server.Dispose();
        }
    }

    [Fact]
    public async Task ForceDispose_DoesNotLeaveStickyConnected()
    {
        var (server, port) = StartServer();
        await server.WaitUntilAcceptingAsync(timeoutMs: 2000);

        var client = new NetClient();
        try
        {
            await client.ConnectAsync("127.0.0.1", port, "ForceClean");
            Assert.True(client.IsConnected);
            client.ForceDispose();
            Assert.False(client.IsConnected);

            // Reconnect on a fresh logical session using the same object (EnsureFullyDisposed).
            await client.ConnectAsync("127.0.0.1", port, "ForceClean");
            Assert.True(client.IsConnected);
        }
        finally
        {
            await client.DisconnectAsync();
            server.Stop();
        }
    }

    [Fact]
    public void InstanceClaim_TakesLowestFreeIndexAndReleasesOnDispose()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bsmso-instance-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = InstanceAllocator.Claim(dir);
            var second = InstanceAllocator.Claim(dir);
            try
            {
                Assert.True(first.IsExclusive);
                Assert.True(second.IsExclusive);
                // Two launchers must never share instance 0 (config.json / username / log file).
                Assert.Equal(0, first.Index);
                Assert.Equal(1, second.Index);

                var third = InstanceAllocator.Claim(dir);
                try
                {
                    Assert.Equal(2, third.Index);
                }
                finally
                {
                    third.Dispose();
                }
            }
            finally
            {
                first.Dispose();
            }

            // Index 0 is reusable as soon as its holder exits.
            var reclaimed = InstanceAllocator.Claim(dir);
            try
            {
                Assert.Equal(0, reclaimed.Index);
            }
            finally
            {
                reclaimed.Dispose();
                second.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
