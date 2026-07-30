using System.Net.Sockets;
using SMSO.Net;
using SMSO.Server;

namespace SMSO.Tests;

/// <summary>
/// Phase 1 Eclipse lobby behavior: configurable server profile, warp pass-through,
/// and hard-off collectible sync while Eclipse maps are unmeasured.
/// </summary>
public sealed class EclipseProfileServerTests
{
    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void GameProfileIds_TryParse_KnownAliases()
    {
        Assert.True(GameProfileIds.TryParse("eclipse", out var eclipse));
        Assert.Equal(GameProfileId.MarioEclipse, eclipse);
        Assert.True(GameProfileIds.TryParse("SME", out eclipse));
        Assert.Equal(GameProfileId.MarioEclipse, eclipse);
        Assert.True(GameProfileIds.TryParse("vanilla", out var vanilla));
        Assert.Equal(GameProfileId.VanillaSms, vanilla);
        Assert.True(GameProfileIds.TryParse("sms", out vanilla));
        Assert.Equal(GameProfileId.VanillaSms, vanilla);
        Assert.False(GameProfileIds.TryParse("chaos", out _));
        Assert.False(GameProfileIds.TryParse(null, out _));
    }

    [Fact]
    public void EclipseProfile_Warp_PassesThroughUnknownStages()
    {
        var logs = new List<string>();
        var server = new GameServer(new LevelCatalog())
        {
            ExpectedGameProfileId = (ushort)GameProfileId.MarioEclipse,
        };
        server.Log += logs.Add;

        // Eclipse hub area 78 / gameplay 61–92 are not in the vanilla catalog —
        // Phase 1 must not reject them.
        server.RequestWarp(0, 1, 78, 2);
        server.RequestWarp(0, 1, 61, 0);

        Assert.DoesNotContain(logs, m => m.StartsWith("Invalid warp", StringComparison.Ordinal));
    }

    [Fact]
    public void VanillaProfile_Warp_StillRejectsUnknownStages()
    {
        var logs = new List<string>();
        var server = new GameServer(new LevelCatalog());
        server.Log += logs.Add;

        server.RequestWarp(0, 1, 78, 2);

        Assert.Contains(logs, m => m.StartsWith("Invalid warp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EclipseServer_AcceptsEclipseClient_RejectsVanillaClient()
    {
        var server = new GameServer(new LevelCatalog())
        {
            ExpectedGameProfileId = (ushort)GameProfileId.MarioEclipse,
        };
        var port = GetFreePort();
        server.Start(port);
        try
        {
            // Eclipse-profile client joins cleanly.
            using (var accepted = new NetClient())
            {
                await accepted.ConnectAsync(
                    "127.0.0.1", port, "EclipsePlayer",
                    gameProfileId: (ushort)GameProfileId.MarioEclipse);
            }

            // Vanilla-profile client is rejected with ProfileMismatch.
            using (var vanilla = new NetClient())
            {
                var ex = await Assert.ThrowsAsync<NetJoinRejectedException>(() =>
                    vanilla.ConnectAsync("127.0.0.1", port, "VanillaPlayer"));
                Assert.Equal(JoinRejectReason.ProfileMismatch, ex.Reason);
            }
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task EclipseServer_ForcesSyncSettingsOff()
    {
        var server = new GameServer(new LevelCatalog())
        {
            ExpectedGameProfileId = (ushort)GameProfileId.MarioEclipse,
        };
        var port = GetFreePort();
        server.Start(port);
        try
        {
            // Even an explicit "enable everything" call must be coerced off.
            server.SetSyncSettings(syncFlags: true, syncObjects: true, syncProgress: true);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()));
            await stream.WriteAsync(PacketSerializer.BuildJoinRequest(
                "SyncProbe", null, gameProfileId: (ushort)GameProfileId.MarioEclipse));

            var syncFrame = await ReadFrameAsync(stream, TcpPacketId.SyncSettings);
            Assert.NotNull(syncFrame);
            Assert.Equal(3, syncFrame!.Length);
            Assert.Equal(0, syncFrame[0]);
            Assert.Equal(0, syncFrame[1]);
            Assert.Equal(0, syncFrame[2]);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task VanillaServer_KeepsRequestedSyncSettings()
    {
        var server = new GameServer(new LevelCatalog());
        var port = GetFreePort();
        server.Start(port);
        try
        {
            server.SetSyncSettings(syncFlags: true, syncObjects: false, syncProgress: true);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()));
            await stream.WriteAsync(PacketSerializer.BuildJoinRequest("SyncProbeV", null));

            var syncFrame = await ReadFrameAsync(stream, TcpPacketId.SyncSettings);
            Assert.NotNull(syncFrame);
            Assert.Equal(1, syncFrame![0]);
            Assert.Equal(0, syncFrame[1]);
            Assert.Equal(1, syncFrame[2]);
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>Reads frames until the requested packet id arrives (skips ack/accepted/etc.).</summary>
    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, TcpPacketId wanted)
    {
        var pending = new List<byte>();
        var scratch = new byte[1024];
        var deadline = DateTime.UtcNow.AddSeconds(5);
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

                var frame = pending.GetRange(0, frameLength).ToArray();
                pending.RemoveRange(0, frameLength);
                if (PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload) && id == wanted)
                    return payload;
            }

            await Task.Delay(10);
        }

        return null;
    }
}
