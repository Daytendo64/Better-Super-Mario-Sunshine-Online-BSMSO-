using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SMSO.Net;

namespace SMSO.Server;

public sealed class GameServer : IDisposable
{
    private readonly LevelCatalog _levels;
    private readonly ConcurrentDictionary<byte, ClientSession> _sessions = new();
    private readonly Dictionary<string, byte> _usernames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (byte Slot, DateTime ReleasedUtc)> _recentReleases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private TcpListener? _tcpListener;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private bool _syncFlags;
    private bool _syncObjects;
    private bool _syncProgress;
    private bool _allowClientTeleport;
    private int _maxPlayers = ProtocolConstants.StableMaxPlayers;
    private DateTime _lastRosterBroadcastUtc = DateTime.MinValue;
    private readonly HideSeekService _hideSeek;
    private readonly WorldEventRelay _worldEvents = new();
    private readonly RedCoinAuthority _redCoinAuthority = new();
    private readonly ShineAuthority _shineAuthority = new();
    private readonly BlueCoinAuthority _blueCoinAuthority = new();

    public event Action<string>? Log;
    public event Action<PlayerRosterEntry[]>? RosterChanged;

    public HideSeekService HideSeek => _hideSeek;

    public bool IsRunning { get; private set; }
    public int MaxPlayers
    {
        get => _maxPlayers;
        set => _maxPlayers = Math.Clamp(value, 2, ProtocolConstants.StableMaxPlayers);
    }

    public GameServer(LevelCatalog levels)
    {
        _levels = levels;
        _hideSeek = new HideSeekService(this);
    }

    public void Start(int port)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _tcpListener = new TcpListener(IPAddress.Any, port);
        _tcpListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _tcpListener.Start();

        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        ConfigureUdpSocketForServer(_udp.Client);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));

        IsRunning = true;
        Log?.Invoke($"Server listening on TCP+UDP port {port}");
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _ = Task.Run(() => UdpRelayLoop(_cts.Token));
        _ = Task.Run(() => WatchdogLoop(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        try { _tcpListener?.Stop(); } catch { /* already stopped */ }
        _tcpListener = null;
        try { _udp?.Dispose(); } catch { /* ignore */ }
        _udp = null;
        _cts?.Dispose();
        _cts = null;
        lock (_lock)
        {
            _sessions.Clear();
            _usernames.Clear();
            _recentReleases.Clear();
        }
        _hideSeek.Reset();
        Log?.Invoke("Server stopped");
    }

    public void NotifyShutdown()
    {
        if (!IsRunning) return;
        BroadcastTcp(PacketSerializer.BuildDisconnect(DisconnectReason.ServerShutdown));
    }

    public void SetSyncSettings(bool syncFlags, bool syncObjects, bool syncProgress)
    {
        _syncFlags = syncFlags;
        _syncObjects = syncObjects;
        _syncProgress = syncProgress;
        BroadcastTcp(PacketSerializer.BuildSyncSettings(syncFlags, syncObjects, syncProgress));
    }

    public bool AllowClientTeleport => _allowClientTeleport;

    public void SetAllowClientTeleport(bool allowClientTeleport)
    {
        _allowClientTeleport = allowClientTeleport;
        BroadcastTcp(PacketSerializer.BuildClientTeleportSettings(allowClientTeleport));
        Log?.Invoke(allowClientTeleport ? "Client teleporting enabled" : "Client teleporting disabled");
    }

    internal void LogMessage(string message) => Log?.Invoke(message);

    internal IReadOnlyList<byte> GetConnectedSlots()
    {
        lock (_lock)
            return _sessions.Keys.OrderBy(k => k).ToArray();
    }

    public void BroadcastGameModeState(GameModeStatePacket state)
        => BroadcastTcp(PacketSerializer.BuildGameModeState(state));

    public GameModeStatePacket GetGameModeState() => _hideSeek.CurrentState;

    public void SetGameMode(GameMode mode) => _hideSeek.SetGameMode(mode);

    public void SetHideSeekRoles(IReadOnlyDictionary<byte, HideSeekRole> roles)
        => _hideSeek.SetRoles(roles);

    public bool TryStartHideSeekTag(out string? error) => _hideSeek.TryStartTag(out error);

    public void StopHideSeekTag() => _hideSeek.StopTag();

    public void ResetHideSeekTag() => _hideSeek.ResetTag();

    public void RequestWarp(byte requesterSlot, byte targetSlot, byte courseId, byte episodeId)
    {
        if (!_levels.IsValidWarp(courseId, episodeId))
        {
            Log?.Invoke($"Invalid warp: course={courseId} episode={episodeId}");
            return;
        }

        lock (_lock)
        {
            if (targetSlot == ProtocolConstants.WarpAllSlots)
            {
                foreach (var s in _sessions.Values)
                {
                    if (s.State is DolphinState.Loading or DolphinState.Warping)
                    {
                        Log?.Invoke($"Warp blocked: slot {s.Slot} loading");
                        return;
                    }
                }
            }
            else if (_sessions.TryGetValue(targetSlot, out var one) &&
                     one.State is DolphinState.Loading or DolphinState.Warping)
            {
                Log?.Invoke($"Warp blocked: slot {targetSlot} loading");
                return;
            }
        }

        var payload = new byte[] { targetSlot, courseId, episodeId, requesterSlot };
        BroadcastTcp(PacketSerializer.WrapTcp(TcpPacketId.WarpCommand, payload));
        Log?.Invoke($"Warp command: target={targetSlot} course={courseId} ep={episodeId}");
    }

    public void UpdatePlayerState(byte slot, byte stageId, byte episodeId, DolphinState state, ushort pingMs)
    {
        if (!_sessions.TryGetValue(slot, out var session))
            return;

        // Area 0 is Delfino Airstrip — only ignore transient zero-stage snapshots while loading.
        if (stageId == 0 && session.StageId != 0 && state is DolphinState.Booting or DolphinState.Loading)
            return;

        var locationChanged = ApplySessionLocation(session, stageId, episodeId);
        session.State = state;
        session.PingMs = pingMs;
        session.LastSeen = DateTime.UtcNow;
        MaybeBroadcastRoster(force: locationChanged);
    }

    private static bool ApplySessionLocation(ClientSession session, byte stageId, byte episodeId)
    {
        var normalized = LevelCatalog.NormalizeEpisodeFromGame(stageId, episodeId);
        var changed = session.StageId != stageId || session.EpisodeId != normalized;
        session.StageId = stageId;
        session.EpisodeId = normalized;
        return changed;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _tcpListener != null)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                client.ReceiveBufferSize = 8192;
                client.SendBufferSize = 8192;
                _ = Task.Run(() => HandleClient(client, ct), ct);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log?.Invoke($"Accept error: {ex.Message}"); }
        }
    }

    private async Task HandleClient(TcpClient tcp, CancellationToken ct)
    {
        ClientSession? session = null;
        var stream = tcp.GetStream();
        var pending = new List<byte>();
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) break;
                pending.AddRange(buffer.AsSpan(0, read).ToArray());

                while (TryExtractFrame(pending, out var frame))
                {
                    if (!PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload)) continue;

                    switch (id)
                    {
                        case TcpPacketId.Handshake:
                            session ??= AssignSlot(tcp, out _);
                            if (session == null)
                            {
                                await stream.WriteAsync(PacketSerializer.WrapTcp(TcpPacketId.JoinRejected,
                                    new[] { (byte)JoinRejectReason.Full }), ct);
                                return;
                            }
                            var ack = new byte[17];
                            ack[16] = session.Slot;
                            await stream.WriteAsync(PacketSerializer.WrapTcp(TcpPacketId.HandshakeAck, ack), ct);
                            break;

                        case TcpPacketId.JoinRequest:
                            if (session == null) break;
                            var name = System.Text.Encoding.UTF8.GetString(payload).TrimEnd('\0');
                            if (!TryRegisterName(session, name, out var reason))
                            {
                                await stream.WriteAsync(PacketSerializer.WrapTcp(TcpPacketId.JoinRejected,
                                    new[] { (byte)reason }), ct);
                                RemoveSession(session);
                                session = null;
                                return;
                            }
                            var roster = BuildRoster();
                            var accepted = new byte[1 + roster.Length];
                            accepted[0] = session.Slot;
                            roster.CopyTo(accepted, 1);
                            await stream.WriteAsync(PacketSerializer.WrapTcp(TcpPacketId.JoinAccepted, accepted), ct);
                            await stream.WriteAsync(PacketSerializer.BuildSyncSettings(_syncFlags, _syncObjects, _syncProgress), ct);
                            await stream.WriteAsync(PacketSerializer.BuildClientTeleportSettings(_allowClientTeleport), ct);
                            await stream.WriteAsync(PacketSerializer.BuildGameModeState(_hideSeek.CurrentState), ct);
                            if (_syncFlags && _worldEvents.History.Count > 0)
                            {
                                await stream.WriteAsync(_worldEvents.BuildWorldStateReplay(), ct);
                                Log?.Invoke(
                                    $"World sync: replayed {_worldEvents.History.Count} events to slot {session.Slot}");
                            }
                            MaybeBroadcastRoster(force: true);
                            break;

                        case TcpPacketId.WarpRequest:
                            if (session == null || payload.Length < 3) break;
                            if (!session.IsHost && payload[0] != session.Slot) break;
                            if (!session.IsHost && !_allowClientTeleport)
                            {
                                Log?.Invoke($"Client warp blocked: slot {session.Slot} (client teleporting disabled)");
                                break;
                            }
                            RequestWarp(session.Slot, payload[0], payload[1], payload[2]);
                            break;

                        case TcpPacketId.SyncSettings:
                            if (session == null || !session.IsHost || payload.Length < 3) break;
                            SetSyncSettings(payload[0] != 0, payload[1] != 0, payload[2] != 0);
                            break;

                        case TcpPacketId.Disconnect:
                            RemoveSession(session, payload.Length > 0
                                ? (DisconnectReason)payload[0]
                                : DisconnectReason.UserRequest);
                            return;

                        case TcpPacketId.Heartbeat:
                            if (session != null)
                            {
                                session.LastSeen = DateTime.UtcNow;
                                if (payload.Length >= 10)
                                {
                                    session.PingMs = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
                                    MaybeBroadcastRoster();
                                }
                                await stream.WriteAsync(PacketSerializer.WrapTcp(TcpPacketId.Heartbeat, payload), ct);
                            }
                            break;

                        case TcpPacketId.UdpRegister:
                            if (session != null && payload.Length >= 2)
                            {
                                var port = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
                                var tcpEp = (IPEndPoint)session.Tcp.Client.RemoteEndPoint!;
                                var endpoint = new IPEndPoint(tcpEp.Address, port);
                                if (session.UdpEndPoint == null ||
                                    !session.UdpEndPoint.Equals(endpoint))
                                {
                                    session.UdpEndPoint = endpoint;
                                    session.LastSnapshotSeq = 0;
                                    Log?.Invoke($"UDP registered for slot {session.Slot} at {session.UdpEndPoint}");
                                }
                            }
                            break;

                        case TcpPacketId.MarioVoiceEvent:
                            if (session == null ||
                                !PacketSerializer.TryReadMarioVoiceEvent(payload, out var voiceSlot, out var voiceEvent) ||
                                voiceSlot != session.Slot ||
                                voiceEvent.IsEmpty)
                            {
                                break;
                            }
                            BroadcastTcp(PacketSerializer.BuildMarioVoiceEvent(session.Slot, voiceEvent));
                            break;

                        case TcpPacketId.WorldEvent:
                            if (session == null || !_syncFlags)
                                break;

                            if (!PacketSerializer.TryReadWorldEventRequest(payload, out var worldRequest))
                                break;

                            byte[]? broadcast = null;
                            switch (worldRequest.Type)
                            {
                                case WorldEventType.RedCoinCollected:
                                    if (!_redCoinAuthority.TryAcceptCollected(worldRequest, out var coinPayload0,
                                            out var coinReserved, out var coinPayload1))
                                    {
                                        Log?.Invoke(
                                            $"World sync: rejected duplicate red coin index course={worldRequest.CourseId}/{worldRequest.EpisodeId} slot={session.Slot} reserved={worldRequest.Reserved} payload1={worldRequest.Payload1}");
                                        break;
                                    }

                                    broadcast = _worldEvents.CreateWorldEvent(
                                        worldRequest.Type,
                                        worldRequest.CourseId,
                                        worldRequest.EpisodeId,
                                        coinPayload0,
                                        coinReserved,
                                        coinPayload1);
                                    break;

                                default:
                                {
                                    switch (worldRequest.Type)
                                    {
                                        case WorldEventType.ShineCollected:
                                            if (!_shineAuthority.TryAccept(worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate shine id={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            broadcast = _worldEvents.CreateWorldEvent(
                                                worldRequest.Type,
                                                worldRequest.CourseId,
                                                worldRequest.EpisodeId,
                                                worldRequest.Payload0,
                                                worldRequest.Reserved,
                                                worldRequest.Payload1);
                                            break;

                                        case WorldEventType.BlueCoinCollected:
                                            if (!_blueCoinAuthority.TryAccept(worldRequest.CourseId, worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate blue coin course={worldRequest.CourseId} index={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            broadcast = _worldEvents.CreateWorldEvent(
                                                worldRequest.Type,
                                                worldRequest.CourseId,
                                                worldRequest.EpisodeId,
                                                worldRequest.Payload0,
                                                worldRequest.Reserved,
                                                worldRequest.Payload1);
                                            break;

                                        default:
                                        {
                                            broadcast = _worldEvents.CreateWorldEvent(
                                                worldRequest.Type,
                                                worldRequest.CourseId,
                                                worldRequest.EpisodeId,
                                                worldRequest.Payload0,
                                                worldRequest.Reserved,
                                                worldRequest.Payload1);
                                            break;
                                        }
                                    }

                                    break;
                                }
                            }

                            if (broadcast == null)
                                break;

                            BroadcastTcp(broadcast);
                            Log?.Invoke(
                                $"World sync: slot {session.Slot} type={worldRequest.Type} course={worldRequest.CourseId}/{worldRequest.EpisodeId} payload0={worldRequest.Payload0} reserved={worldRequest.Reserved} payload1={worldRequest.Payload1}");
                            break;
                    }
                }
            }
        }
        catch { /* disconnect */ }
        finally
        {
            RemoveSession(session);
        }
    }

    internal void ProcessHideSeekTagsForSnapshot(byte updatedSlot, in PlayerSnapshot snap)
    {
        if (!_hideSeek.CurrentState.TagActive)
            return;

        ClientSession[] peers;
        lock (_lock)
            peers = _sessions.Values.ToArray();

        var nowMs = Environment.TickCount64;
        foreach (var other in peers)
        {
            if (other.Slot == updatedSlot)
                continue;
            if (other.LastSnapshot.Connected == 0)
                continue;

            var otherLagSeconds = other.LastSnapshotReceivedMs == 0
                ? 0f
                : MathF.Min((nowMs - other.LastSnapshotReceivedMs) / 1000f, HideSeekService.TagLagCompensationMaxSeconds);

            _hideSeek.ProcessSnapshot(updatedSlot, snap, other.Slot, other.LastSnapshot, 0f, otherLagSeconds);
            _hideSeek.ProcessSnapshot(other.Slot, other.LastSnapshot, updatedSlot, snap, otherLagSeconds, 0f);
        }
    }

    private ClientSession? AssignSlot(TcpClient tcp, out byte slot)
    {
        slot = 0;
        lock (_lock)
        {
            if (_sessions.Count >= _maxPlayers) return null;

            PruneExpiredReleases();

            for (byte i = 0; i < _maxPlayers; i++)
            {
                if (!_sessions.ContainsKey(i))
                {
                    slot = i;
                    var session = new ClientSession
                    {
                        Slot = i,
                        Tcp = tcp,
                        IsHost = _sessions.IsEmpty,
                        LastSeen = DateTime.UtcNow,
                    };
                    _sessions[i] = session;
                    return session;
                }
            }
        }
        return null;
    }

    private void PruneExpiredReleases()
    {
        var now = DateTime.UtcNow;
        foreach (var name in _recentReleases.Keys.ToArray())
        {
            if ((now - _recentReleases[name].ReleasedUtc).TotalMilliseconds > ProtocolConstants.ReconnectWindowMs)
                _recentReleases.Remove(name);
        }
    }

    private bool TryRegisterName(ClientSession session, string name, out JoinRejectReason reason)
    {
        reason = JoinRejectReason.None;
        if (!PlayerNameValidator.TryValidate(name, out _))
        {
            reason = JoinRejectReason.InvalidName;
            return false;
        }
        lock (_lock)
        {
            PruneExpiredReleases();

            if (_usernames.TryGetValue(name, out var existingSlot) && existingSlot != session.Slot)
            {
                if (_sessions.TryGetValue(existingSlot, out var existingSession))
                {
                    if (IsSessionTcpAlive(existingSession))
                    {
                        reason = JoinRejectReason.NameTaken;
                        return false;
                    }

                    EvictSessionLocked(existingSession);
                    BroadcastRoster();
                }
                else
                {
                    _usernames.Remove(name);
                }
            }

            _usernames[name] = session.Slot;
            session.Username = name;
            session.State = DolphinState.Booting;
            session.StageId = 0;
            session.EpisodeId = 0;
            session.LastSnapshotSeq = 0;
            session.LastSnapshot = default;
            session.LastSnapshotReceivedMs = 0;
            session.UdpEndPoint = null;
            _recentReleases.Remove(name);
        }
        return true;
    }

    private static bool IsSessionTcpAlive(ClientSession session)
    {
        try
        {
            return session.Tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private void EvictSessionLocked(ClientSession session)
    {
        _sessions.TryRemove(session.Slot, out _);
        if (!string.IsNullOrEmpty(session.Username))
        {
            _recentReleases[session.Username] = (session.Slot, DateTime.UtcNow);
            _usernames.Remove(session.Username);
        }

        try
        {
            if (session.Tcp.Connected)
                session.Tcp.GetStream().Write(PacketSerializer.BuildDisconnect(DisconnectReason.Timeout));
        }
        catch
        {
            // socket already closed
        }

        try { session.Tcp.Close(); } catch { }

        session.LastSnapshot = default;
        session.LastSnapshotSeq = 0;
        session.LastSnapshotReceivedMs = 0;
        session.UdpEndPoint = null;

        Log?.Invoke($"Evicted stale session for slot {session.Slot}");
    }

    private void RemoveSession(ClientSession? session, DisconnectReason reason = DisconnectReason.Timeout)
    {
        if (session == null) return;

        var removed = false;
        lock (_lock)
        {
            if (_sessions.TryRemove(session.Slot, out _))
            {
                removed = true;
                if (!string.IsNullOrEmpty(session.Username))
                {
                    _recentReleases[session.Username] = (session.Slot, DateTime.UtcNow);
                    _usernames.Remove(session.Username);
                }
            }
        }

        if (!removed)
            return;

        try
        {
            if (session.Tcp.Connected)
                session.Tcp.GetStream().Write(PacketSerializer.BuildDisconnect(reason));
        }
        catch
        {
            // socket already closed
        }

        try { session.Tcp.Close(); } catch { }

        session.LastSnapshot = default;
        session.LastSnapshotSeq = 0;
        session.LastSnapshotReceivedMs = 0;
        session.UdpEndPoint = null;

        if (_hideSeek.CurrentState.GameMode == GameMode.HideSeek)
        {
            if (session.IsHost)
            {
                _hideSeek.SetGameMode(GameMode.Normal);
                Log?.Invoke("Hide & Seek stopped — host disconnected.");
            }
            else
            {
                _hideSeek.OnPlayerDisconnected(session.Slot);
            }
        }

        BroadcastRoster();
        Log?.Invoke($"Player left slot {session.Slot}");
        // #region agent log
        AgentDebugLog.Write("B", "GameServer.RemoveSession", "session removed", new
        {
            slot = session.Slot,
            reason = reason.ToString(),
        });
        // #endregion
    }

    private byte[] BuildRoster()
    {
        var list = new List<byte> { (byte)_sessions.Count };
        foreach (var s in _sessions.Values.OrderBy(s => s.Slot))
        {
            list.Add(s.Slot);
            var nameBytes = new byte[16];
            var raw = System.Text.Encoding.UTF8.GetBytes(s.Username);
            Array.Copy(raw, nameBytes, Math.Min(raw.Length, 15));
            list.AddRange(nameBytes);
            list.Add(s.StageId);
            list.Add(s.EpisodeId);
            list.Add((byte)s.State);
            list.AddRange(BitConverter.GetBytes(s.PingMs));
        }
        return list.ToArray();
    }

    private void MaybeBroadcastRoster(bool force = false)
    {
        if (!force)
        {
            var elapsed = (DateTime.UtcNow - _lastRosterBroadcastUtc).TotalMilliseconds;
            if (elapsed < ProtocolConstants.RosterBroadcastIntervalMs)
                return;
        }

        _lastRosterBroadcastUtc = DateTime.UtcNow;
        BroadcastRoster();
    }

    private void BroadcastRoster()
    {
        var roster = BuildRoster();
        BroadcastTcp(PacketSerializer.WrapTcp(TcpPacketId.RosterSnapshot, roster));
        var entries = _sessions.Values.OrderBy(s => s.Slot).Select(s => new PlayerRosterEntry
        {
            Slot = s.Slot,
            Username = s.Username,
            StageId = s.StageId,
            EpisodeId = s.EpisodeId,
            State = s.State,
            PingMs = s.PingMs,
        }).ToArray();
        RosterChanged?.Invoke(entries);
    }

    private void BroadcastTcp(byte[] frame)
    {
        foreach (var s in _sessions.Values)
        {
            try
            {
                s.Tcp.GetStream().Write(frame);
            }
            catch { /* client gone */ }
        }
    }

    private async Task UdpRelayLoop(CancellationToken ct)
    {
        if (_udp == null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                if (result.Buffer.Length < 7) continue;
                if (BinaryPrimitives.ReadUInt32LittleEndian(result.Buffer.AsSpan(0, 4)) != ProtocolConstants.Magic)
                    continue;

                var applied = TryApplySnapshotFromUdp(result.Buffer, result.RemoteEndPoint);
                if (!applied)
                    continue;

                ClientSession[] peers;
                lock (_lock)
                    peers = _sessions.Values.ToArray();

                var buffer = result.Buffer;
                var length = buffer.Length;
                var sender = result.RemoteEndPoint;
                foreach (var s in peers)
                {
                    if (s.UdpEndPoint == null || s.UdpEndPoint.Equals(sender))
                        continue;
                    try
                    {
                        _udp.Send(buffer, length, s.UdpEndPoint);
                    }
                    catch (Exception ex)
                    {
                        // #region agent log
                        AgentDebugLog.Write("B", "GameServer.UdpRelayLoop", "relay send failed", new
                        {
                            targetSlot = s.Slot,
                            target = s.UdpEndPoint.ToString(),
                            error = ex.Message,
                        });
                        // #endregion
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                                                 or SocketError.NetworkReset
                                                 or SocketError.Interrupted)
            {
                // Windows ICMP port-unreachable after relaying to a dead client poisons UDP recv.
                // #region agent log
                AgentDebugLog.Write("A", "GameServer.UdpRelayLoop", "recv reset recovered", new
                {
                    error = ex.SocketErrorCode.ToString(),
                });
                // #endregion
                Log?.Invoke($"UDP recv reset ({ex.SocketErrorCode}) — continuing relay");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // #region agent log
                AgentDebugLog.Write("A", "GameServer.UdpRelayLoop", "recv error recovered", new
                {
                    error = ex.Message,
                });
                // #endregion
                Log?.Invoke($"UDP relay error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Windows sends ICMP port-unreachable when relaying to a disconnected client's UDP port.
    /// Without SIO_UDP_CONNRESET disabled, the next ReceiveAsync throws and kills the relay loop.
    /// </summary>
    private static void ConfigureUdpSocketForServer(Socket socket)
    {
        socket.ReceiveBufferSize = 65536;
        socket.SendBufferSize = 65536;

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            const int SioUdpConnReset = -1744830452; // SIO_UDP_CONNRESET
            socket.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch
        {
            // Non-fatal — relay loop also catches ConnectionReset above.
        }
    }

    private bool TryApplySnapshotFromUdp(byte[] buffer, IPEndPoint sender)
    {
        if (buffer.Length < ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.PlayerSnapshotSize) return false;
        if ((UdpPacketId)buffer[4] != UdpPacketId.PlayerSnapshot) return false;

        var slot = buffer[5];
        if (!_sessions.TryGetValue(slot, out var session))
            return false;

        if (session.UdpEndPoint == null)
        {
            session.UdpEndPoint = sender;
            session.LastSnapshotSeq = 0;
            Log?.Invoke($"UDP auto-bound for slot {slot} at {sender}");
        }
        else if (!session.UdpEndPoint.Equals(sender))
        {
            // #region agent log
            AgentDebugLog.Write("C", "GameServer.TryApplySnapshotFromUdp", "endpoint mismatch", new
            {
                slot,
                sender = sender.ToString(),
                expected = session.UdpEndPoint.ToString(),
            });
            // #endregion
            return false;
        }

        var seq = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(6, 4));
        if (session.LastSnapshotSeq != 0 && (int)(seq - session.LastSnapshotSeq) <= 0)
            return false;

        var snap = PacketSerializer.SnapshotFromBytes(
            buffer.AsSpan(ProtocolConstants.UdpSnapshotPayloadOffset, ProtocolConstants.PlayerSnapshotSize));
        if (snap.StageId == 0 && session.StageId != 0 && snap.Connected != 0)
        {
            // Keep last known stage during boot flicker; allow valid area 0 (Airstrip) updates.
            if (session.State is DolphinState.Booting or DolphinState.Loading)
                return false;
        }

        session.LastSnapshotSeq = seq;
        var locationChanged = ApplySessionLocation(session, snap.StageId, snap.EpisodeId);
        session.State = DolphinState.Active;
        session.LastSeen = DateTime.UtcNow;
        session.LastSnapshot = snap;
        session.LastSnapshotReceivedMs = Environment.TickCount64;
        ProcessHideSeekTagsForSnapshot(slot, snap);
        _hideSeek.ProcessHiderDeath(slot, snap);
        MaybeBroadcastRoster(force: locationChanged);
        return true;
    }

    private async Task WatchdogLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(ProtocolConstants.HeartbeatIntervalMs, ct);
            var now = DateTime.UtcNow;
            foreach (var s in _sessions.Values.ToArray())
            {
                if (!IsSessionTcpAlive(s) ||
                    (now - s.LastSeen).TotalMilliseconds > ProtocolConstants.DisconnectTimeoutMs)
                {
                    RemoveSession(s);
                }
            }
        }
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

    public void Dispose() => Stop();

    private sealed class ClientSession
    {
        public byte Slot { get; set; }
        public string Username { get; set; } = "";
        public TcpClient Tcp { get; set; } = null!;
        public bool IsHost { get; set; }
        public byte StageId { get; set; }
        public byte EpisodeId { get; set; }
        public DolphinState State { get; set; }
        public ushort PingMs { get; set; }
        public DateTime LastSeen { get; set; }
        public long LastSnapshotReceivedMs { get; set; }
        public PlayerSnapshot LastSnapshot { get; set; }
        public uint LastSnapshotSeq { get; set; }
        public IPEndPoint? UdpEndPoint { get; set; }
    }
}
