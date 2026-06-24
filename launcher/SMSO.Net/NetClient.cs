using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace SMSO.Net;

public sealed class NetClient : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _tcpStream;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _tcpReadTask;
    private Task? _udpSendTask;
    private Task? _udpReadTask;
    private Task? _heartbeatTask;
    private byte _assignedSlot;
    private uint _snapshotSeq;
    private IPEndPoint? _udpServerEndpoint;
    private volatile bool _isDisconnecting;
    private readonly object _snapshotLock = new();
    private readonly Dictionary<byte, uint> _lastReceivedSnapshotSeq = new();
    private readonly HashSet<byte> _knownRosterSlots = new();
    private PlayerSnapshot _pendingSnapshot;
    private bool _hasPendingSnapshot;
    private readonly byte[] _udpSendScratch =
        new byte[ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.PlayerSnapshotSize];
    private readonly byte[] _heartbeatScratch = new byte[10];

    public event Action<PlayerRosterEntry[]>? RosterUpdated;
    public event Action<byte, byte, byte, byte>? WarpCommandReceived;
    public event Action<byte, PlayerSnapshot>? SnapshotReceived;
    public event Action<byte, MarioVoiceEvent>? MarioVoiceEventReceived;
    public event Action<JoinRejectReason>? JoinRejected;
    public event Action? JoinAccepted;
    public event Action<DisconnectReason>? Disconnected;
    public event Action<bool, bool, bool>? SyncSettingsReceived;
    public event Action<bool>? ClientTeleportSettingsReceived;
    public event Action<GameModeStatePacket>? GameModeStateReceived;
    public event Action<string>? Log;

    public byte AssignedSlot => _assignedSlot;
    public bool IsConnected => _tcp?.Connected == true && !_isDisconnecting;
    public ushort MeasuredPingMs { get; private set; }

    public async Task ConnectAsync(string host, int port, string username, CancellationToken ct = default)
    {
        if (_tcp?.Connected == true)
            throw new InvalidOperationException("Already connected.");

        _isDisconnecting = false;
        _assignedSlot = 0;
        _snapshotSeq = 0;
        _lastReceivedSnapshotSeq.Clear();
        _knownRosterSlots.Clear();

        var joinTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnJoinAccepted() => joinTcs.TrySetResult(true);
        void OnJoinRejected(JoinRejectReason reason) =>
            joinTcs.TrySetException(new NetJoinRejectedException(reason));

        JoinAccepted += OnJoinAccepted;
        JoinRejected += OnJoinRejected;

        try
        {
            var serverAddress = await ResolveHostAsync(host, ct).ConfigureAwait(false);
            _tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            ConfigureTcpSocket(_tcp.Client);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromMilliseconds(ProtocolConstants.ConnectTimeoutMs));
            await _tcp.ConnectAsync(serverAddress, port, connectCts.Token).ConfigureAwait(false);

            _tcpStream = _tcp.GetStream();
            _udp = new UdpClient(AddressFamily.InterNetwork);
            ConfigureUdpSocket(_udp.Client);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            _udpServerEndpoint = new IPEndPoint(serverAddress, port);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _tcpReadTask = Task.Run(() => TcpReadLoop(_cts.Token), _cts.Token);

            await SendTcpAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()), _cts.Token);
            await SendTcpAsync(PacketSerializer.BuildJoinRequest(username), _cts.Token);

            using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            joinCts.CancelAfter(TimeSpan.FromMilliseconds(ProtocolConstants.ConnectTimeoutMs));
            await joinTcs.Task.WaitAsync(joinCts.Token).ConfigureAwait(false);

            var udpPort = (ushort)((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            await SendTcpAsync(PacketSerializer.BuildUdpRegister(udpPort), _cts.Token);

            _udpReadTask = Task.Run(() => UdpReadLoop(_cts.Token), _cts.Token);
            _udpSendTask = Task.Run(() => UdpSnapshotSendLoop(_cts.Token), _cts.Token);
            _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token), _cts.Token);
        }
        catch
        {
            await DisconnectInternalAsync(DisconnectReason.Timeout, sendPacket: false).ConfigureAwait(false);
            throw;
        }
        finally
        {
            JoinAccepted -= OnJoinAccepted;
            JoinRejected -= OnJoinRejected;
        }
    }

    /// <summary>Queue the latest local snapshot for the fixed-rate UDP send loop.</summary>
    public void PublishSnapshot(in PlayerSnapshot snap)
    {
        lock (_snapshotLock)
        {
            _pendingSnapshot = snap;
            _hasPendingSnapshot = true;
        }
    }

    public async Task SendWarpRequestAsync(byte targetSlot, byte courseId, byte episodeId)
    {
        await SendTcpAsync(PacketSerializer.BuildWarpRequest(targetSlot, courseId, episodeId),
            _cts?.Token ?? default);
    }

    public async Task SendMarioVoiceEventAsync(MarioVoiceEvent voiceEvent)
    {
        await SendTcpAsync(PacketSerializer.BuildMarioVoiceEvent(_assignedSlot, voiceEvent),
            _cts?.Token ?? default);
    }

    public Task SendSnapshotAsync(PlayerSnapshot snap)
    {
        PublishSnapshot(snap);
        return Task.CompletedTask;
    }

    private async Task UdpSnapshotSendLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ProtocolConstants.UdpSnapshotIntervalMs));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            PlayerSnapshot snap = default;
            var hasSnap = false;
            lock (_snapshotLock)
            {
                if (_hasPendingSnapshot)
                {
                    snap = _pendingSnapshot;
                    hasSnap = true;
                }
            }

            if (!hasSnap || _udp == null || _udpServerEndpoint == null)
                continue;

            PacketSerializer.WriteUdpSnapshot(_udpSendScratch, _assignedSlot, ++_snapshotSeq, snap);
            try
            {
                _udp.Send(_udpSendScratch, _udpSendScratch.Length, _udpServerEndpoint);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!_isDisconnecting)
            {
                Log?.Invoke($"UDP snapshot send error: {ex.Message}");
            }
        }
    }

    private static async Task<IPAddress> ResolveHostAsync(string host, CancellationToken ct)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;

        if (IPAddress.TryParse(host, out var parsed))
            return parsed.AddressFamily == AddressFamily.InterNetwork
                ? parsed
                : IPAddress.IsLoopback(parsed) ? IPAddress.Loopback : parsed.MapToIPv4();

        var entry = await Dns.GetHostEntryAsync(host, ct).ConfigureAwait(false);
        var ipv4 = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4 == null)
            throw new SocketException((int)SocketError.HostNotFound);
        return ipv4;
    }

    public async Task DisconnectAsync(DisconnectReason reason = DisconnectReason.UserRequest)
    {
        await DisconnectInternalAsync(reason, sendPacket: true).ConfigureAwait(false);
    }

    private async Task DisconnectInternalAsync(DisconnectReason reason, bool sendPacket)
    {
        if (_isDisconnecting && sendPacket)
            return;

        _isDisconnecting = true;
        if (sendPacket)
        {
            try
            {
                if (_tcpStream?.CanWrite == true)
                    await SendTcpAsync(PacketSerializer.BuildDisconnect(reason), CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch
            {
                // socket may already be closed
            }
        }

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        await WaitForBackgroundTasksAsync().ConfigureAwait(false);
        DisposeResources();
    }

    private async Task WaitForBackgroundTasksAsync()
    {
        var tasks = new[] { _tcpReadTask, _udpReadTask, _udpSendTask, _heartbeatTask }
            .Where(t => t != null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
        }
        catch
        {
            // expected during forced shutdown
        }
    }

    private void DisposeResources()
    {
        _tcpStream?.Dispose();
        _tcp?.Dispose();
        _udp?.Dispose();
        _cts?.Dispose();
        _tcpStream = null;
        _tcp = null;
        _udp = null;
        _cts = null;
        _tcpReadTask = null;
        _udpReadTask = null;
        _udpSendTask = null;
        _heartbeatTask = null;
        _udpServerEndpoint = null;
    }

    private async Task SendTcpAsync(byte[] data, CancellationToken ct)
    {
        if (_tcpStream == null) return;
        await _tcpStream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    private async Task TcpReadLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var pending = new List<byte>(4096);
        try
        {
            while (!ct.IsCancellationRequested && _tcpStream != null)
            {
                int read = await _tcpStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    if (!_isDisconnecting)
                        Disconnected?.Invoke(DisconnectReason.Timeout);
                    break;
                }

                pending.AddRange(buffer.AsSpan(0, read).ToArray());
                while (pending.Count >= 13)
                {
                    if (!TryExtractFrame(pending, out var frame))
                        break;
                    HandleTcpFrame(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected during disconnect or app shutdown
        }
        catch (IOException) when (_isDisconnecting)
        {
            // expected when the server closes the connection
        }
        catch (Exception ex) when (!_isDisconnecting)
        {
            Log?.Invoke($"TCP read error: {ex.Message}");
            Disconnected?.Invoke(DisconnectReason.Timeout);
        }
    }

    private void HandleTcpFrame(byte[] frame)
    {
        if (!PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload))
            return;

        switch (id)
        {
            case TcpPacketId.HandshakeAck:
                if (payload.Length >= 17)
                    _assignedSlot = payload[16];
                break;
            case TcpPacketId.JoinAccepted:
                _assignedSlot = payload.Length > 0 ? payload[0] : _assignedSlot;
                JoinAccepted?.Invoke();
                if (payload.Length > 1)
                    ParseRoster(payload.AsSpan(1));
                break;
            case TcpPacketId.JoinRejected:
                JoinRejected?.Invoke(payload.Length > 0 ? (JoinRejectReason)payload[0] : JoinRejectReason.None);
                break;
            case TcpPacketId.RosterSnapshot:
                ParseRoster(payload);
                break;
            case TcpPacketId.WarpCommand:
                if (payload.Length >= 3)
                    WarpCommandReceived?.Invoke(payload[0], payload[1], payload[2],
                        payload.Length > 3 ? payload[3] : (byte)0);
                break;
            case TcpPacketId.SyncSettings:
                if (payload.Length >= 3)
                    SyncSettingsReceived?.Invoke(payload[0] != 0, payload[1] != 0, payload[2] != 0);
                break;
            case TcpPacketId.ClientTeleportSettings:
                if (payload.Length >= 1)
                    ClientTeleportSettingsReceived?.Invoke(payload[0] != 0);
                break;
            case TcpPacketId.GameModeState:
                if (PacketSerializer.TryReadGameModeState(payload, out var gameModeState))
                    GameModeStateReceived?.Invoke(gameModeState);
                break;
            case TcpPacketId.Heartbeat:
                HandleHeartbeatEcho(payload);
                break;
            case TcpPacketId.MarioVoiceEvent:
                if (PacketSerializer.TryReadMarioVoiceEvent(payload, out var voiceSlot, out var voiceEvent))
                    MarioVoiceEventReceived?.Invoke(voiceSlot, voiceEvent);
                break;
            case TcpPacketId.Disconnect:
                Disconnected?.Invoke(payload.Length > 0 ? (DisconnectReason)payload[0] : DisconnectReason.ServerShutdown);
                _isDisconnecting = true;
                _cts?.Cancel();
                break;
            case TcpPacketId.PlayerLeft:
                if (payload.Length >= 1)
                    ParseRoster(payload);
                break;
        }
    }

    private void ParseRoster(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            return;

        int count = data[0];
        var entries = new List<PlayerRosterEntry>(count);
        int offset = 1;
        for (int i = 0; i < count && offset + 22 <= data.Length; i++)
        {
            entries.Add(new PlayerRosterEntry
            {
                Slot = data[offset],
                Username = System.Text.Encoding.UTF8.GetString(TrimNullBytes(data.Slice(offset + 1, 16))),
                StageId = data[offset + 17],
                EpisodeId = data[offset + 18],
                State = (DolphinState)data[offset + 19],
                PingMs = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 20, 2)),
            });
            offset += 22;
        }

        var activeSlots = new HashSet<byte>(entries.Select(e => e.Slot));
        foreach (var slot in _knownRosterSlots.Where(s => !activeSlots.Contains(s)).ToArray())
            _lastReceivedSnapshotSeq.Remove(slot);
        foreach (var slot in activeSlots.Where(s => !_knownRosterSlots.Contains(s)))
            _lastReceivedSnapshotSeq.Remove(slot);

        _knownRosterSlots.Clear();
        foreach (var slot in activeSlots)
            _knownRosterSlots.Add(slot);

        RosterUpdated?.Invoke(entries.ToArray());
    }

    private async Task UdpReadLoop(CancellationToken ct)
    {
        if (_udp == null)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (_udpServerEndpoint != null && !result.RemoteEndPoint.Equals(_udpServerEndpoint))
                    continue;
                if (result.Buffer.Length < ProtocolConstants.UdpSnapshotPayloadOffset)
                    continue;
                if (BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(0, 4)) != ProtocolConstants.Magic)
                    continue;

                if ((UdpPacketId)result.Buffer[4] != UdpPacketId.PlayerSnapshot ||
                    result.Buffer.Length <
                    ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.PlayerSnapshotSize)
                {
                    continue;
                }

                var slot = result.Buffer[5];
                var seq = BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(6, 4));
                if (_lastReceivedSnapshotSeq.TryGetValue(slot, out var lastSeq) &&
                    !SequenceIsNewer(seq, lastSeq))
                {
                    continue;
                }

                _lastReceivedSnapshotSeq[slot] = seq;
                var snap = PacketSerializer.SnapshotFromBytes(
                    result.Buffer.AsSpan(ProtocolConstants.UdpSnapshotPayloadOffset,
                        ProtocolConstants.PlayerSnapshotSize));
                SnapshotReceived?.Invoke(slot, snap);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // expected
        }
        catch (Exception ex) when (!_isDisconnecting)
        {
            Log?.Invoke($"UDP read error: {ex.Message}");
        }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ProtocolConstants.HeartbeatIntervalMs));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var sentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                BinaryPrimitives.WriteInt64LittleEndian(_heartbeatScratch.AsSpan(0, 8), sentMs);
                BinaryPrimitives.WriteUInt16LittleEndian(_heartbeatScratch.AsSpan(8, 2), MeasuredPingMs);
                await SendTcpAsync(PacketSerializer.BuildHeartbeat(_heartbeatScratch), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                break;
            }
        }
    }

    private void HandleHeartbeatEcho(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            return;

        var sentMs = BinaryPrimitives.ReadInt64LittleEndian(payload);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rtt = nowMs - sentMs;
        if (rtt >= 0 && rtt < 5000)
            MeasuredPingMs = (ushort)Math.Min(999, rtt);
    }

    private static bool SequenceIsNewer(uint seq, uint lastSeq)
    {
        if (lastSeq == 0)
            return true;
        return (int)(seq - lastSeq) > 0;
    }

    private static byte[] TrimNullBytes(ReadOnlySpan<byte> span)
    {
        int len = span.IndexOf((byte)0);
        if (len < 0)
            len = span.Length;
        return span.Slice(0, len).ToArray();
    }

    private static bool TryExtractFrame(List<byte> pending, out byte[] frame)
    {
        frame = Array.Empty<byte>();
        while (pending.Count >= 13)
        {
            Span<byte> header = stackalloc byte[13];
            for (int i = 0; i < header.Length; i++)
                header[i] = pending[i];
            if (!PacketSerializer.TryGetTcpFrameLength(header, out var total))
            {
                pending.RemoveAt(0);
                continue;
            }

            if (pending.Count < total)
                return false;

            frame = pending.GetRange(0, total).ToArray();
            pending.RemoveRange(0, total);
            return true;
        }

        return false;
    }

    private static void ConfigureTcpSocket(Socket socket)
    {
        socket.NoDelay = true;
        socket.ReceiveBufferSize = 8192;
        socket.SendBufferSize = 8192;
    }

    private static void ConfigureUdpSocket(Socket socket)
    {
        socket.ReceiveBufferSize = 65536;
        socket.SendBufferSize = 65536;
    }

    public void Dispose()
    {
        if (_isDisconnecting)
        {
            DisposeResources();
            return;
        }

        try
        {
            DisconnectInternalAsync(DisconnectReason.UserRequest, sendPacket: false)
                .WaitAsync(TimeSpan.FromMilliseconds(750))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            DisposeResources();
        }
    }
}
