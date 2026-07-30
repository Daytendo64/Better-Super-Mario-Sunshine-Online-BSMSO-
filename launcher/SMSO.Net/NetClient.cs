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
    private readonly object _udpScratchLock = new();
    private readonly SemaphoreSlim _tcpSendLock = new(1, 1);
    private readonly byte[] _udpSendScratch =
        new byte[ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.PlayerSnapshotSize];
    private readonly byte[] _udpPingScratch =
        new byte[ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.UdpPingPayloadSize];
    private int _udpPingTickCount;
    private readonly uint[] _lastReceivedSnapshotSeq =
        new uint[ProtocolConstants.MaxRemoteSlots];
    private readonly bool[] _hasReceivedSnapshotSeq =
        new bool[ProtocolConstants.MaxRemoteSlots];
    private readonly HashSet<byte> _knownRosterSlots = new();
    private PlayerSnapshot _pendingSnapshot;
    private bool _hasPendingSnapshot;
    private string? _pendingMarioModelId;
    private int _marioModelIntentSequence;

    /// <summary>
    /// UDP liveness. A firewalled UDP path after a successful TCP join leaves remote
    /// players frozen while the session still looks healthy — TCP never times out.
    /// Silence is measured against server Pongs (sent ~1 Hz) plus any remote snapshot,
    /// so it is independent of how many peers are moving.
    /// </summary>
    private DateTime _udpActiveSinceUtc = DateTime.MinValue;
    private DateTime _lastUdpInboundUtc = DateTime.MinValue;
    private int _consecutiveUdpSendFailures;
    private bool _udpDegradedReported;

    /// <summary>Grace after UDP starts before silence counts (join / NAT warm-up).</summary>
    internal static readonly TimeSpan UdpHealthGrace = TimeSpan.FromSeconds(4);
    /// <summary>Sustained silence that warrants a visible warning (not brief packet loss).</summary>
    internal static readonly TimeSpan UdpDegradedSilence = TimeSpan.FromSeconds(6);
    /// <summary>Sustained silence that means the UDP path is dead — drop the session.</summary>
    internal static readonly TimeSpan UdpDeadSilence = TimeSpan.FromSeconds(20);
    /// <summary>Consecutive send failures that mean the local socket is unusable.</summary>
    internal const int UdpDeadSendFailures = 300;

    internal enum UdpHealth
    {
        Healthy,
        Degraded,
        Dead,
    }

    /// <summary>
    /// Pure policy for <see cref="UdpReadLoop"/> / <see cref="UdpSnapshotSendLoop"/> health.
    /// Kept side-effect free so thresholds are unit-testable without sockets.
    /// </summary>
    internal static UdpHealth EvaluateUdpHealth(
        TimeSpan sinceUdpStart, TimeSpan sinceLastInbound, int consecutiveSendFailures)
    {
        if (consecutiveSendFailures >= UdpDeadSendFailures)
            return UdpHealth.Dead;
        if (sinceUdpStart < UdpHealthGrace)
            return UdpHealth.Healthy;
        if (sinceLastInbound >= UdpDeadSilence)
            return UdpHealth.Dead;
        if (sinceLastInbound >= UdpDegradedSilence)
            return UdpHealth.Degraded;
        return UdpHealth.Healthy;
    }

    /// <summary>
    /// Bad-magic / bad-version resync budget. Byte-at-a-time resync could otherwise chew
    /// silently through a permanently corrupt stream for the rest of the session.
    /// </summary>
    internal const int MaxResyncSkippedBytes = 4096;

    public event Action<PlayerRosterEntry[]>? RosterUpdated;
    public event Action<byte, byte, byte, byte>? WarpCommandReceived;
    public event Action<byte, PlayerSnapshot>? SnapshotReceived;
    public event Action<byte, MarioVoiceEvent>? MarioVoiceEventReceived;
    public event Action<WorldEventPacket>? WorldEventReceived;
    public event Action<WorldEventPacket[]>? WorldStateReplayReceived;
    public event Action<WorldProgressSnapshot>? WorldProgressSnapshotReceived;
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

    /// <summary>
    /// Announce desired model immediately over TCP. Heartbeats continue carrying
    /// the same id as compatibility/recovery fallback.
    /// </summary>
    public void SetMarioModelId(string? modelId)
    {
        var normalized = MarioPack.CharacterPack.NormalizeModelId(modelId);
        var sequence = PublishMarioModelIntent(normalized);
        if (IsConnected)
            _ = SendMarioModelIntentSafelyAsync(normalized, sequence);
    }

    public async Task SendMarioModelIntentAsync(string? modelId)
    {
        var normalized = MarioPack.CharacterPack.NormalizeModelId(modelId);
        var sequence = PublishMarioModelIntent(normalized);
        if (!IsConnected)
            return;
        await SendTcpAsync(PacketSerializer.BuildMarioModelIntent(normalized, sequence),
            _cts?.Token ?? default).ConfigureAwait(false);
    }

    private uint PublishMarioModelIntent(string modelId)
    {
        Volatile.Write(ref _pendingMarioModelId, modelId);
        var sequence = Interlocked.Increment(ref _marioModelIntentSequence);
        if (sequence == 0)
            sequence = Interlocked.Increment(ref _marioModelIntentSequence);
        return unchecked((uint)sequence);
    }

    private async Task SendMarioModelIntentSafelyAsync(string modelId, uint sequence)
    {
        try
        {
            // Coalesce a send that has not started yet. If two already entered
            // the TCP writer out of scheduler order, the server sequence still
            // rejects the older frame.
            if (sequence != unchecked((uint)Volatile.Read(ref _marioModelIntentSequence)))
                return;
            await SendTcpAsync(PacketSerializer.BuildMarioModelIntent(modelId, sequence),
                _cts?.Token ?? default).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_isDisconnecting || _cts?.IsCancellationRequested == true)
        {
            // Session teardown superseded the UI selection.
        }
        catch (Exception ex) when (!_isDisconnecting)
        {
            Log?.Invoke($"Mario model intent send failed: {ex.Message}; heartbeat fallback remains active");
        }
    }

    public async Task ConnectAsync(string host, int port, string username, CancellationToken ct = default,
        string? marioModelId = null, ushort? gameProfileId = null)
    {
        EnsureFullyDisposed();

        _isDisconnecting = false;
        _assignedSlot = 0;
        _snapshotSeq = 0;
        _hasPendingSnapshot = false;
        MeasuredPingMs = 0;
        // Always track the join-time model so heartbeats re-advertise it. Leaving this null
        // used to send 8 zero bytes every 2s, which the server treated as "switch to retail"
        // and wiped the roster model id — remotes then always spawned as retail Mario.
        Volatile.Write(ref _pendingMarioModelId,
            MarioPack.CharacterPack.NormalizeModelId(marioModelId));
        Volatile.Write(ref _marioModelIntentSequence, 0);
        Array.Clear(_lastReceivedSnapshotSeq);
        Array.Clear(_hasReceivedSnapshotSeq);
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
            await ConnectTcpWithRetriesAsync(serverAddress, port, ct).ConfigureAwait(false);

            _tcpStream = _tcp!.GetStream();
            _udp = new UdpClient(AddressFamily.InterNetwork);
            ConfigureUdpSocket(_udp.Client);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            _udpServerEndpoint = new IPEndPoint(serverAddress, port);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _tcpReadTask = Task.Run(() => TcpReadLoop(_cts.Token), _cts.Token);

            await SendTcpAsync(PacketSerializer.BuildHandshake(Guid.NewGuid()), _cts.Token);
            await SendTcpAsync(
                PacketSerializer.BuildJoinRequest(username, marioModelId, gameProfileId: gameProfileId),
                _cts.Token);

            using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            joinCts.CancelAfter(TimeSpan.FromMilliseconds(ProtocolConstants.ConnectTimeoutMs));
            await joinTcs.Task.WaitAsync(joinCts.Token).ConfigureAwait(false);

            // A UI selection can occur after TCP connects but before JoinAccepted.
            // The server correctly rejects pre-join intent, so replay the latest
            // sequenced value now that this session is authorized.
            var pendingIntentSequence =
                unchecked((uint)Volatile.Read(ref _marioModelIntentSequence));
            if (pendingIntentSequence != 0)
            {
                var pendingModel = Volatile.Read(ref _pendingMarioModelId);
                await SendTcpAsync(
                    PacketSerializer.BuildMarioModelIntent(
                        pendingModel, pendingIntentSequence),
                    _cts.Token).ConfigureAwait(false);
            }

            var udpPort = (ushort)((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            await SendTcpAsync(PacketSerializer.BuildUdpRegister(udpPort), _cts.Token);

            _udpActiveSinceUtc = DateTime.UtcNow;
            _lastUdpInboundUtc = _udpActiveSinceUtc;
            _consecutiveUdpSendFailures = 0;
            _udpDegradedReported = false;
            _udpReadTask = Task.Run(() => UdpReadLoop(_cts.Token), _cts.Token);
            _udpSendTask = Task.Run(() => UdpSnapshotSendLoop(_cts.Token), _cts.Token);
            _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token), _cts.Token);

            TrySendPendingSnapshot();
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

    /// <summary>Send the latest queued local snapshot immediately (e.g. right after join).</summary>
    public void SendSnapshotNow()
    {
        if (_udp == null || _udpServerEndpoint == null)
            return;

        PlayerSnapshot snap;
        lock (_snapshotLock)
        {
            if (!_hasPendingSnapshot)
                return;
            snap = _pendingSnapshot;
        }

        SendSnapshotPacket(snap);
    }

    public void ForceDispose()
    {
        _isDisconnecting = true;
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            WaitForBackgroundTasksAsync().Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // expected during forced shutdown
        }

        DisposeResources();
    }

    /// <summary>
    /// Test hook mirroring UDP-Dead / TCP-EOF: mark disconnecting before raising
    /// <see cref="Disconnected"/> so <see cref="IsConnected"/> is not sticky.
    /// </summary>
    internal void BeginTransportTeardownForTests(DisconnectReason reason = DisconnectReason.Timeout)
    {
        if (_isDisconnecting)
            return;
        _isDisconnecting = true;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        Disconnected?.Invoke(reason);
    }

    /// <summary>
    /// TCP connect with backoff for ConnectionRefused / timed-out hosts that are still
    /// binding after rehost or AcceptLoop scheduling lag.
    /// </summary>
    private async Task ConnectTcpWithRetriesAsync(IPAddress serverAddress, int port, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < ProtocolConstants.ConnectRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { _tcp?.Dispose(); } catch { /* ignore */ }
            _tcp = null;

            var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            ConfigureTcpSocket(client.Client);
            try { client.Client.LingerState = new LingerOption(true, 0); } catch { /* platform */ }

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var remainingBudget = Math.Max(
                    500,
                    ProtocolConstants.ConnectTimeoutMs / ProtocolConstants.ConnectRetryCount);
                attemptCts.CancelAfter(TimeSpan.FromMilliseconds(remainingBudget));
                await client.ConnectAsync(serverAddress, port, attemptCts.Token).ConfigureAwait(false);
                _tcp = client;
                if (attempt > 0)
                    Log?.Invoke($"Connected to {serverAddress}:{port} on attempt {attempt + 1}");
                return;
            }
            catch (Exception ex) when (IsTransientConnectFailure(ex) &&
                                       attempt + 1 < ProtocolConstants.ConnectRetryCount &&
                                       !ct.IsCancellationRequested)
            {
                last = ex;
                try { client.Dispose(); } catch { /* ignore */ }
                var delay = ProtocolConstants.ConnectRetryBaseDelayMs * (attempt + 1);
                Log?.Invoke(
                    $"Connect attempt {attempt + 1}/{ProtocolConstants.ConnectRetryCount} to " +
                    $"{serverAddress}:{port} failed ({DescribeConnectFailure(ex)}); retry in {delay}ms");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch
            {
                try { client.Dispose(); } catch { /* ignore */ }
                throw;
            }
        }

        throw last ?? new SocketException((int)SocketError.ConnectionRefused);
    }

    private static bool IsTransientConnectFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
            return true;
        if (ex is SocketException sock)
        {
            return sock.SocketErrorCode is SocketError.ConnectionRefused
                or SocketError.TimedOut
                or SocketError.HostUnreachable
                or SocketError.NetworkUnreachable
                or SocketError.AddressNotAvailable
                or SocketError.WouldBlock
                or SocketError.TryAgain;
        }

        return ex.InnerException != null && IsTransientConnectFailure(ex.InnerException);
    }

    private static string DescribeConnectFailure(Exception ex) =>
        ex is SocketException sock ? sock.SocketErrorCode.ToString() : ex.GetType().Name;

    // Best-effort senders. SendTcpAsync now faults on a dead stream (durable publishes must
    // see that), so these log and swallow instead of surfacing to fire-and-forget callers —
    // warps, voices and progress requests are all re-driven by the user or a periodic resync.
    public async Task SendWarpRequestAsync(byte targetSlot, byte courseId, byte episodeId)
    {
        if (!IsConnected)
        {
            Log?.Invoke("Warp request dropped — not connected");
            return;
        }

        try
        {
            await SendTcpAsync(PacketSerializer.BuildWarpRequest(targetSlot, courseId, episodeId),
                _cts?.Token ?? default).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_isDisconnecting)
        {
            Log?.Invoke($"Warp request send failed: {ex.Message}");
        }
    }

    public async Task SendMarioVoiceEventAsync(MarioVoiceEvent voiceEvent)
    {
        if (!IsConnected)
            return;

        try
        {
            await SendTcpAsync(PacketSerializer.BuildMarioVoiceEvent(_assignedSlot, voiceEvent),
                _cts?.Token ?? default).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_isDisconnecting)
        {
            Log?.Invoke($"Mario voice send failed: {ex.Message}");
        }
    }

    public async Task SendWorldEventAsync(WorldEventRequest request)
    {
        await SendTcpAsync(PacketSerializer.BuildWorldEventRequest(request),
            _cts?.Token ?? default);
    }

    /// <summary>
    /// Durable world-event send with an explicit result. False means the frame never
    /// reached the socket, so the caller must keep the event queued (the server's
    /// authorities are the only heal source and cannot recover what they never received).
    /// </summary>
    public async Task<bool> TrySendWorldEventAsync(WorldEventRequest request)
    {
        if (!IsConnected)
            return false;

        try
        {
            await SendTcpAsync(PacketSerializer.BuildWorldEventRequest(request),
                _cts?.Token ?? default).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"World sync: world-event send failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    public async Task SendWorldProgressRequestAsync(uint clientProgressSeq = 0)
    {
        if (!IsConnected)
        {
            Log?.Invoke("World progress request dropped — not connected");
            return;
        }

        try
        {
            await SendTcpAsync(PacketSerializer.BuildWorldProgressRequest(clientProgressSeq),
                _cts?.Token ?? default).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_isDisconnecting)
        {
            Log?.Invoke($"World progress request send failed: {ex.Message}");
        }
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
            TrySendPendingSnapshot();

            // Probe UDP RTT roughly once per second on the same loop so latency shown in the roster
            // reflects the path snapshots actually travel (TCP heartbeat remains a liveness backstop).
            if (++_udpPingTickCount >= 60)
            {
                _udpPingTickCount = 0;
                SendUdpPing();
                CheckUdpHealth();
            }

            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void SendUdpPing()
    {
        if (_udp == null || _udpServerEndpoint == null || _isDisconnecting)
            return;

        lock (_udpScratchLock)
        {
            PacketSerializer.WriteUdpPingInto(_udpPingScratch, _assignedSlot,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            try
            {
                _udp.Send(_udpPingScratch, _udpPingScratch.Length, _udpServerEndpoint);
                _consecutiveUdpSendFailures = 0;
            }
            catch (Exception ex) when (!_isDisconnecting)
            {
                _consecutiveUdpSendFailures++;
                Log?.Invoke($"UDP ping send error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Escalates a dead UDP path. Without this, a firewalled or reset UDP socket left the
    /// session "connected" with every remote frozen in place and no user-visible cause.
    /// </summary>
    private void CheckUdpHealth()
    {
        if (_isDisconnecting || _udpActiveSinceUtc == DateTime.MinValue)
            return;

        var now = DateTime.UtcNow;
        var health = EvaluateUdpHealth(
            now - _udpActiveSinceUtc,
            now - _lastUdpInboundUtc,
            Volatile.Read(ref _consecutiveUdpSendFailures));

        switch (health)
        {
            case UdpHealth.Healthy:
                if (_udpDegradedReported)
                {
                    _udpDegradedReported = false;
                    Log?.Invoke("UDP link recovered — remote player updates resumed");
                }

                break;

            case UdpHealth.Degraded:
                if (!_udpDegradedReported)
                {
                    _udpDegradedReported = true;
                    Log?.Invoke(
                        "UDP link degraded — no player updates received for " +
                        $"{(int)(now - _lastUdpInboundUtc).TotalSeconds}s. Remote players will " +
                        $"appear frozen; check that UDP port {_udpServerEndpoint?.Port} is open.");
                }

                break;

            case UdpHealth.Dead:
                // One-shot: previously left _isDisconnecting=false so IsConnected stayed sticky
                // and CheckUdpHealth re-fired Disconnected every ~1s until TearDown ran.
                if (_isDisconnecting)
                    break;
                _isDisconnecting = true;
                Log?.Invoke(
                    "UDP link dead — no player updates for " +
                    $"{(int)(now - _lastUdpInboundUtc).TotalSeconds}s " +
                    $"(sendFailures={_consecutiveUdpSendFailures}); dropping session.");
                try { _cts?.Cancel(); } catch { /* ignore */ }
                Disconnected?.Invoke(DisconnectReason.Timeout);
                break;
        }
    }

    private void HandleUdpPong(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.UdpPingPayloadSize)
            return;

        var sentMs = BinaryPrimitives.ReadInt64LittleEndian(
            buffer.Slice(ProtocolConstants.UdpSnapshotPayloadOffset, ProtocolConstants.UdpPingPayloadSize));
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rtt = nowMs - sentMs;
        if (rtt >= 0 && rtt < 5000)
        {
            var sample = (ushort)Math.Min(999, rtt);
            MeasuredPingMs = MeasuredPingMs == 0 ? sample : (ushort)((MeasuredPingMs * 3 + sample) / 4);
        }
    }

    private void TrySendPendingSnapshot()
    {
        if (_udp == null || _udpServerEndpoint == null || _isDisconnecting)
            return;

        PlayerSnapshot snap;
        lock (_snapshotLock)
        {
            if (!_hasPendingSnapshot)
                return;
            snap = _pendingSnapshot;
        }

        SendSnapshotPacket(snap);
    }

    private void SendSnapshotPacket(in PlayerSnapshot snap)
    {
        if (_udp == null || _udpServerEndpoint == null || _isDisconnecting)
            return;

        lock (_udpScratchLock)
        {
            PacketSerializer.WriteUdpSnapshotInto(_udpSendScratch, _assignedSlot, ++_snapshotSeq, snap);
            try
            {
                _udp.Send(_udpSendScratch, _udpSendScratch.Length, _udpServerEndpoint);
                _consecutiveUdpSendFailures = 0;
            }
            catch (Exception ex) when (!_isDisconnecting)
            {
                // Logged at most once per second — a broken socket fails 60×/s.
                if (_consecutiveUdpSendFailures++ % 60 == 0)
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
        if (_isDisconnecting)
        {
            await WaitForBackgroundTasksAsync().ConfigureAwait(false);
            DisposeResources();
            return;
        }

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
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        {
            // expected during forced shutdown
        }
    }

    private void EnsureFullyDisposed()
    {
        // Always force-clean leftover tasks/sockets. A prior DisconnectInternalAsync may
        // leave _isDisconnecting=true with null sockets; the old early-return path skipped
        // waiting on background tasks and made the next ConnectAsync flaky until app restart.
        ForceDispose();
    }

    private void DisposeResources()
    {
        lock (_snapshotLock)
        {
            _hasPendingSnapshot = false;
        }

        try
        {
            _udp?.Client?.Close();
        }
        catch
        {
            // ignore
        }

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
        _udpActiveSinceUtc = DateTime.MinValue;
        _lastUdpInboundUtc = DateTime.MinValue;
        _consecutiveUdpSendFailures = 0;
        _udpDegradedReported = false;
    }

    /// <summary>
    /// Throws when the TCP stream is gone. A silent no-op here made durable world-event
    /// publishes look successful to the bridge, which then cleared the Dolphin localPending
    /// lane and advanced the published sequence for a mutation the server never saw.
    /// </summary>
    private async Task SendTcpAsync(byte[] data, CancellationToken ct)
    {
        await _tcpSendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = _tcpStream ??
                         throw new InvalidOperationException("TCP stream is not connected");
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
        }
        finally
        {
            _tcpSendLock.Release();
        }
    }

    private async Task TcpReadLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var pending = new List<byte>(4096);
        var resyncSkipped = 0;
        try
        {
            while (!ct.IsCancellationRequested && _tcpStream != null)
            {
                int read = await _tcpStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    if (!_isDisconnecting)
                    {
                        _isDisconnecting = true;
                        Disconnected?.Invoke(DisconnectReason.Timeout);
                    }
                    break;
                }

                // Avoid ToArray()+AddRange (allocates every TCP read). Append in place.
                if (pending.Capacity < pending.Count + read)
                    pending.Capacity = Math.Max(pending.Capacity * 2, pending.Count + read);
                for (var i = 0; i < read; i++)
                    pending.Add(buffer[i]);
                while (pending.Count >= 13)
                {
                    var extracted = TryExtractFrame(pending, out var frame, out var skipped);
                    if (skipped > 0)
                    {
                        resyncSkipped += skipped;
                        Log?.Invoke(
                            $"TCP resync: skipped {skipped} bad header byte(s) " +
                            $"(total {resyncSkipped}/{MaxResyncSkippedBytes})");
                        if (resyncSkipped >= MaxResyncSkippedBytes)
                        {
                            Log?.Invoke(
                                "TCP stream unrecoverable — too much unframed data; disconnecting.");
                            if (!_isDisconnecting)
                            {
                                _isDisconnecting = true;
                                try { _cts?.Cancel(); } catch { /* ignore */ }
                                Disconnected?.Invoke(DisconnectReason.Timeout);
                            }
                            return;
                        }
                    }

                    if (!extracted)
                        break;

                    // A good frame proves the stream re-synchronised; budget applies to
                    // continuous garbage, not to one corrupt frame across a long session.
                    resyncSkipped = 0;
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
            _isDisconnecting = true;
            Log?.Invoke($"TCP read error: {ex.Message}");
            try { _cts?.Cancel(); } catch { /* ignore */ }
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
                if (PacketSerializer.TryReadHandshakeAck(payload, out var ackSlot, out var serverBuild))
                {
                    _assignedSlot = ackSlot;
                    if (serverBuild is ushort build && build != ProtocolConstants.ModBuildId)
                    {
                        Log?.Invoke(
                            $"HandshakeAck VersionMismatch: server build {build}, " +
                            $"client build {ProtocolConstants.ModBuildId}");
                        JoinRejected?.Invoke(JoinRejectReason.VersionMismatch);
                    }
                }
                else if (payload.Length >= 17)
                {
                    _assignedSlot = payload[16];
                }
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
            case TcpPacketId.WorldEvent:
                if (PacketSerializer.TryReadWorldEventBroadcast(payload, out var worldEvent))
                    InvokeDetached(WorldEventReceived, worldEvent);
                break;
            case TcpPacketId.WorldStateReplay:
                if (PacketSerializer.TryReadWorldStateReplay(payload, out var replayEvents))
                    InvokeDetached(WorldStateReplayReceived, replayEvents);
                break;
            case TcpPacketId.WorldProgressSnapshot:
                if (PacketSerializer.TryReadWorldProgressSnapshot(payload, out var progressSnapshot))
                    InvokeDetached(WorldProgressSnapshotReceived, progressSnapshot);
                break;
            case TcpPacketId.WorldProgressRequest:
                // Server→client should not receive this; ignore if echoed.
                break;
            case TcpPacketId.Disconnect:
            {
                var already = _isDisconnecting;
                _isDisconnecting = true;
                try { _cts?.Cancel(); } catch { /* ignore */ }
                if (!already)
                    Disconnected?.Invoke(payload.Length > 0 ? (DisconnectReason)payload[0] : DisconnectReason.ServerShutdown);
                break;
            }
            case TcpPacketId.PlayerLeft:
                if (payload.Length >= 1)
                    ParseRoster(payload);
                break;
        }
    }

    /// <summary>
    /// Run world-sync handlers off the TCP read loop. A synchronous hang inside
    /// SessionCoordinator (progress apply / gold flood) used to stall reads, back-pressure
    /// the server send path, and leave stage-enter force requests with no reply.
    /// </summary>
    private static void InvokeDetached<T>(Action<T>? handler, T arg)
    {
        if (handler == null)
            return;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                handler(arg);
            }
            catch
            {
                // Session handlers log their own failures; never kill the read loop.
            }
        }, null);
    }

    private void ParseRoster(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            return;

        int count = data[0];
        var entries = new PlayerRosterEntry[count];
        int offset = 1;
        // Prefer v9 roster entries (30 bytes); fall back to legacy 22-byte entries.
        int entrySize = ProtocolConstants.RosterEntrySize;
        if (count > 0 && offset + entrySize * count > data.Length)
            entrySize = 22;

        var parsed = 0;
        for (int i = 0; i < count && offset + entrySize <= data.Length; i++)
        {
            var entry = new PlayerRosterEntry
            {
                Slot = data[offset],
                Username = System.Text.Encoding.UTF8.GetString(TrimNullBytes(data.Slice(offset + 1, 16))),
                StageId = data[offset + 17],
                EpisodeId = data[offset + 18],
                State = (DolphinState)data[offset + 19],
                PingMs = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 20, 2)),
            };
            if (entrySize >= ProtocolConstants.RosterEntrySize)
            {
                entry.MarioModelId = MarioPack.CharacterPack.DecodeModelId(
                    data.Slice(offset + 22, ProtocolConstants.MarioModelIdSize));
            }

            entries[parsed++] = entry;
            offset += entrySize;
        }

        if (parsed != entries.Length)
            Array.Resize(ref entries, parsed);

        // Diff against prior roster without LINQ allocs (roster ticks ~5 Hz keep-alive).
        foreach (var slot in _knownRosterSlots)
        {
            var stillPresent = false;
            for (var j = 0; j < entries.Length; j++)
            {
                if (entries[j].Slot == slot)
                {
                    stillPresent = true;
                    break;
                }
            }

            if (!stillPresent && slot < _hasReceivedSnapshotSeq.Length)
                _hasReceivedSnapshotSeq[slot] = false;
        }

        for (var j = 0; j < entries.Length; j++)
        {
            var slot = entries[j].Slot;
            if (_knownRosterSlots.Contains(slot))
                continue;
            if (slot < _hasReceivedSnapshotSeq.Length)
                _hasReceivedSnapshotSeq[slot] = false;
        }

        _knownRosterSlots.Clear();
        for (var j = 0; j < entries.Length; j++)
            _knownRosterSlots.Add(entries[j].Slot);

        RosterUpdated?.Invoke(entries);
    }

    private async Task UdpReadLoop(CancellationToken ct)
    {
        if (_udp == null)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (SocketException ex) when (!_isDisconnecting && !ct.IsCancellationRequested)
                {
                    // Windows surfaces ICMP port-unreachable as ConnectionReset on the next
                    // receive. Leaving the loop here froze every remote for the rest of the
                    // session; health is tracked separately by EvaluateUdpHealth.
                    Log?.Invoke($"UDP read error: {ex.SocketErrorCode} — continuing");
                    try
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                if (_udpServerEndpoint != null && !result.RemoteEndPoint.Equals(_udpServerEndpoint))
                    continue;
                if (result.Buffer.Length < ProtocolConstants.UdpSnapshotBatchHeaderSize)
                    continue;
                if (BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(0, 4)) != ProtocolConstants.Magic)
                    continue;

                _lastUdpInboundUtc = DateTime.UtcNow;

                var packetId = (UdpPacketId)result.Buffer[4];
                if (packetId == UdpPacketId.Pong)
                {
                    HandleUdpPong(result.Buffer);
                    continue;
                }

                if (packetId == UdpPacketId.SnapshotBatch)
                {
                    HandleUdpSnapshotBatch(result.Buffer);
                    continue;
                }

                if (packetId != UdpPacketId.PlayerSnapshot ||
                    result.Buffer.Length <
                    ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.PlayerSnapshotSize)
                {
                    continue;
                }

                var slot = result.Buffer[5];
                if (slot >= ProtocolConstants.MaxRemoteSlots || slot == _assignedSlot)
                    continue;

                var seq = BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(6, 4));
                if (_hasReceivedSnapshotSeq[slot] &&
                    !SequenceIsNewer(seq, _lastReceivedSnapshotSeq[slot]))
                {
                    continue;
                }

                _lastReceivedSnapshotSeq[slot] = seq;
                _hasReceivedSnapshotSeq[slot] = true;
                // Own a fresh Name[] per packet. A reused per-slot buffer was shared with
                // BridgeWorker/_remoteRaw; the next UDP CopyTo re-poisoned stripped names
                // with legacy color overlay bytes ("Player" → flickering "Playe").
                var snap = PacketSerializer.SnapshotFromBytes(
                    result.Buffer.AsSpan(ProtocolConstants.UdpSnapshotPayloadOffset,
                        ProtocolConstants.PlayerSnapshotSize),
                    new byte[16]);
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

    internal void HandleUdpSnapshotBatch(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < ProtocolConstants.UdpSnapshotBatchHeaderSize)
            return;
        if (BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(0, 4)) != ProtocolConstants.Magic ||
            (UdpPacketId)packet[4] != UdpPacketId.SnapshotBatch)
            return;

        var count = packet[5];
        if (count > ProtocolConstants.StableMaxPlayers)
            return;
        var requiredLength = ProtocolConstants.UdpSnapshotBatchHeaderSize +
                             count * ProtocolConstants.UdpSnapshotBatchEntrySize;
        // A truncated fixed-entry batch has no recoverable later boundary.
        if (packet.Length < requiredLength)
            return;

        for (var index = 0; index < count; index++)
        {
            var entryOffset = ProtocolConstants.UdpSnapshotBatchHeaderSize +
                              index * ProtocolConstants.UdpSnapshotBatchEntrySize;

            var slot = packet[entryOffset];
            if (slot >= ProtocolConstants.MaxRemoteSlots)
                continue; // fixed-size boundary makes the next entry recoverable
            if (slot == _assignedSlot)
                continue;

            if (!PacketSerializer.TryReadUdpSnapshotBatchEntry(
                    packet,
                    index,
                    new byte[16],
                    out var decodedSlot,
                    out var seq,
                    out var snap) ||
                decodedSlot != slot)
            {
                continue;
            }

            if (_hasReceivedSnapshotSeq[slot] &&
                !SequenceIsNewer(seq, _lastReceivedSnapshotSeq[slot]))
            {
                continue;
            }

            _lastReceivedSnapshotSeq[slot] = seq;
            _hasReceivedSnapshotSeq[slot] = true;
            SnapshotReceived?.Invoke(slot, snap);
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
                // timestamp(8)+ping(2)+marioModelId(8) — always include the current model id.
                // Zero-filled trailing bytes previously cleared the server's join-time model.
                var payload = new byte[10 + ProtocolConstants.MarioModelIdSize];
                var sentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), sentMs);
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), MeasuredPingMs);
                MarioPack.CharacterPack.EncodeModelId(
                        Volatile.Read(ref _pendingMarioModelId) ?? string.Empty)
                    .CopyTo(payload, 10);
                await SendTcpAsync(PacketSerializer.BuildHeartbeat(payload), ct).ConfigureAwait(false);
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

    internal static bool TryExtractFrame(List<byte> pending, out byte[] frame) =>
        TryExtractFrame(pending, out frame, out _);

    /// <summary>
    /// Byte-at-a-time resync on a bad magic/version. <paramref name="skippedBytes"/> lets the
    /// caller bound how much garbage a corrupt stream may consume before the session is
    /// declared unrecoverable.
    /// </summary>
    internal static bool TryExtractFrame(List<byte> pending, out byte[] frame, out int skippedBytes)
    {
        frame = Array.Empty<byte>();
        skippedBytes = 0;
        while (pending.Count >= 13)
        {
            Span<byte> header = stackalloc byte[13];
            for (int i = 0; i < header.Length; i++)
                header[i] = pending[i];
            if (!PacketSerializer.TryGetTcpFrameLength(header, out var total))
            {
                pending.RemoveAt(0);
                skippedBytes++;
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
        ForceDispose();
    }
}
