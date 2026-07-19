using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SMSO.Net;
using SMSO.Net.MarioPack;

namespace SMSO.Server;

public sealed class GameServer : IDisposable
{
    private readonly LevelCatalog _levels;
    private readonly ConcurrentDictionary<byte, ClientSession> _sessions = new();
    private readonly Dictionary<string, byte> _usernames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (byte Slot, DateTime ReleasedUtc)> _recentReleases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly object _udpSendLock = new();

    private TcpListener? _tcpListener;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private bool _syncFlags;
    private bool _syncObjects;
    private bool _syncProgress;
    private bool _allowClientTeleport;
    private int _maxPlayers = ProtocolConstants.StableMaxPlayers;
    private DateTime _lastRosterBroadcastUtc = DateTime.MinValue;
    private ulong? _lastRosterSignature;
    private const int RosterKeepAliveIntervalMs = 1000;
    private DateTime _lastProgressResyncUtc = DateTime.MinValue;
    private readonly HideSeekService _hideSeek;
    private readonly WorldEventRelay _worldEvents = new();
    private readonly RedCoinAuthority _redCoinAuthority = new();
    private readonly NpcCleanAuthority _npcCleanAuthority = new();
    private readonly GraffitiCleanAuthority _graffitiCleanAuthority = new();
    private readonly ShineAuthority _shineAuthority = new();
    private readonly BlueCoinAuthority _blueCoinAuthority = new();
    private readonly StoryFlagAuthority _storyFlagAuthority = new();
    // Include acting slot so two players hitting the same NPC are not collapsed forever.
    private readonly Dictionary<(byte CourseId, byte EpisodeId, byte Kind, byte ActingSlot, uint PackedPos), DateTime>
        _npcReactRecent = new();
    private static readonly TimeSpan NpcReactDedupWindow = TimeSpan.FromMilliseconds(700);
    private readonly Dictionary<(byte CourseId, byte EpisodeId), int> _stageOccupancy = new();
    // Reused across UDP relay / hide-seek tag checks so a full lobby does not allocate
    // ClientSession[] on every snapshot (~600/sec at 10×60 Hz).
    private ClientSession[] _peerScratch = new ClientSession[ProtocolConstants.StableMaxPlayers];
    private readonly byte[] _udpPongScratch =
        new byte[ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.UdpPingPayloadSize];
    private readonly byte[] _udpSnapshotBatchScratch =
        new byte[ProtocolConstants.UdpSnapshotBatchMaxSize];

    public event Action<string>? Log;
    public event Action<PlayerRosterEntry[]>? RosterChanged;

    public HideSeekService HideSeek => _hideSeek;
    public LevelCatalog Levels => _levels;

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
        _ = Task.Run(() => UdpSnapshotBroadcastLoop(_cts.Token));
        _ = Task.Run(() => WatchdogLoop(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        // Snapshot sessions so we can drain each connection's send queue (delivering any queued
        // disconnect frames from NotifyShutdown) BEFORE yanking the cancellation token, which
        // would otherwise abort the per-session writer tasks mid-flush.
        ClientSession[] sessionsToFlush;
        lock (_lock)
            sessionsToFlush = _sessions.Values.ToArray();

        foreach (var s in sessionsToFlush)
            FlushSendChannel(s);

        foreach (var s in sessionsToFlush)
        {
            try { s.Tcp.Close(); } catch { /* already closed */ }
        }

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
            _stageOccupancy.Clear();
        }
        _redCoinAuthority.Reset();
        _npcCleanAuthority.Reset();
        _graffitiCleanAuthority.Reset();
        _shineAuthority.Reset();
        _blueCoinAuthority.Reset();
        _storyFlagAuthority.Reset();
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
        var wasSyncFlags = _syncFlags;
        _syncFlags = syncFlags;
        _syncObjects = syncObjects;
        _syncProgress = syncProgress;
        BroadcastTcp(PacketSerializer.BuildSyncSettings(syncFlags, syncObjects, syncProgress));
        // Re-enabling flag sync: push authority snapshot so clients heal durable events
        // missed while sync was off (SyncSettings alone does not replay history).
        if (syncFlags && !wasSyncFlags)
            MaybeBroadcastProgressResync(force: true);
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
        var slots = new List<byte>(_maxPlayers);
        lock (_lock)
        {
            for (byte i = 0; i < _maxPlayers; i++)
            {
                if (_sessions.ContainsKey(i))
                    slots.Add(i);
            }
        }
        return slots;
    }

    /// <summary>Test/diagnostics: number of sessions currently holding a slot (including unnamed handshakes).</summary>
    internal int SessionCount => _sessions.Count;

    public void BroadcastGameModeState(GameModeStatePacket state)
        => BroadcastTcp(PacketSerializer.BuildGameModeState(state));

    public GameModeStatePacket GetGameModeState() => _hideSeek.CurrentState;

    public void SetGameMode(GameMode mode) => _hideSeek.SetGameMode(mode);

    public void SetHideSeekRoles(IReadOnlyDictionary<byte, HideSeekRole> roles)
        => _hideSeek.SetRoles(roles);

    public bool TryStartHideSeekTag(out string? error) => _hideSeek.TryStartTag(out error);

    public void SetHideSeekGraceDurationMs(int graceMs) =>
        _hideSeek.StartTagGraceDurationMs = graceMs;

    public int GetHideSeekGraceDurationMs() => _hideSeek.StartTagGraceDurationMs;

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

        // Warp-all (and single warps) often land players on the same spawn; suppress
        // proximity tags briefly so a mid-round warp cannot mass-promote clustered hiders.
        if (_hideSeek.CurrentState.TagActive)
            _hideSeek.NotifyPlayersWarped();
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

    private bool ApplySessionLocation(ClientSession session, byte stageId, byte episodeId)
    {
        var normalized = LevelCatalog.NormalizeEpisodeFromGame(stageId, episodeId, _levels);
        var changed = session.StageId != stageId || session.EpisodeId != normalized;
        if (!changed)
            return false;

        var previousStage = session.StageId;
        var previousEpisode = session.EpisodeId;
        session.StageId = stageId;
        session.EpisodeId = normalized;
        UpdateStageOccupancy(previousStage, previousEpisode, stageId, normalized);
        return true;
    }

    private void UpdateStageOccupancy(byte previousStage, byte previousEpisode, byte newStage, byte newEpisode)
    {
        bool reachedCoop = false;
        lock (_lock)
        {
            if (IsTrackedStage(previousStage, previousEpisode))
                ReleaseStageOccupancyLocked(previousStage, previousEpisode);
            if (IsTrackedStage(newStage, newEpisode))
                reachedCoop = AcquireStageOccupancyLocked(newStage, newEpisode);
        }

        // Second player entered a stage that may already have red-coin authority — push
        // a progress snapshot so the joiner is not stuck waiting for the 45s resync.
        if (reachedCoop)
            MaybeBroadcastProgressResync(force: true);
    }

    private void ReleaseSessionStageOccupancy(ClientSession session)
    {
        lock (_lock)
        {
            if (IsTrackedStage(session.StageId, session.EpisodeId))
                ReleaseStageOccupancyLocked(session.StageId, session.EpisodeId);
        }
    }

    private static bool IsTrackedStage(byte courseId, byte episodeId)
        => courseId != 0;

    /// <returns>True when occupancy transitioned to 2+ (co-op just started on this stage).</returns>
    private bool AcquireStageOccupancyLocked(byte courseId, byte episodeId)
    {
        var key = (courseId, episodeId);
        _stageOccupancy.TryGetValue(key, out var count);
        var next = count + 1;
        _stageOccupancy[key] = next;
        return next == 2;
    }

    private void ReleaseStageOccupancyLocked(byte courseId, byte episodeId)
    {
        var key = (courseId, episodeId);
        if (!_stageOccupancy.TryGetValue(key, out var count))
            return;

        count--;
        if (count <= 0)
        {
            _stageOccupancy.Remove(key);
            _redCoinAuthority.ResetStage(courseId, episodeId);
            _worldEvents.RemoveRedCoinHistory(courseId, episodeId);
            _npcCleanAuthority.ResetStage(courseId, episodeId);
            // Plaza graffiti is hub-global (episode 255). Only reset when no plaza
            // episode bucket still has players — otherwise decideNextScenario splits
            // would wipe stamps while peers remain on another dolpic episode.
            if (courseId == StoryFlagAuthority.PlazaAreaId)
            {
                if (!HasAnyPlazaOccupancyLocked())
                {
                    _graffitiCleanAuthority.ResetStage(courseId, episodeId);
                    Log?.Invoke(
                        $"World sync: reset red-coin/npc-clean + graffiti hub for plaza (last episode vacated was {episodeId})");
                }
                else
                {
                    Log?.Invoke(
                        $"World sync: reset red-coin/npc-clean for course={courseId}/{episodeId} (plaza graffiti hub retained — peers remain)");
                }
            }
            else
            {
                _graffitiCleanAuthority.ResetStage(courseId, episodeId);
                Log?.Invoke($"World sync: reset red-coin/npc-clean/graffiti state for course={courseId}/{episodeId} (stage empty)");
            }
        }
        else
        {
            _stageOccupancy[key] = count;
        }
    }

    private bool HasAnyPlazaOccupancyLocked()
    {
        foreach (var pair in _stageOccupancy)
        {
            if (pair.Key.Item1 == StoryFlagAuthority.PlazaAreaId && pair.Value > 0)
                return true;
        }

        return false;
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
                            session.SendTask = StartSendLoop(session, stream, ct);
                            var ack = new byte[17];
                            ack[16] = session.Slot;
                            EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.HandshakeAck, ack));
                            break;

                        case TcpPacketId.JoinRequest:
                            if (session == null) break;
                            if (!PacketSerializer.TryReadJoinRequest(payload, out var name, out var joinModelId, out var joinBuildId))
                            {
                                name = System.Text.Encoding.UTF8.GetString(payload).TrimEnd('\0');
                                joinModelId = string.Empty;
                                joinBuildId = 0;
                            }
                            if (joinBuildId != ProtocolConstants.ModBuildId)
                            {
                                EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.JoinRejected,
                                    new[] { (byte)JoinRejectReason.VersionMismatch }));
                                RemoveSession(session);
                                session = null;
                                return;
                            }
                            if (!TryRegisterName(session, name, out var reason))
                            {
                                EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.JoinRejected,
                                    new[] { (byte)reason }));
                                RemoveSession(session);
                                session = null;
                                return;
                            }
                            session.MarioModelId = CharacterPack.NormalizeModelId(joinModelId);
                            var roster = BuildRoster();
                            var accepted = new byte[1 + roster.Length];
                            accepted[0] = session.Slot;
                            roster.CopyTo(accepted, 1);
                            EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.JoinAccepted, accepted));
                            EnqueueSend(session, PacketSerializer.BuildSyncSettings(_syncFlags, _syncObjects, _syncProgress));
                            EnqueueSend(session, PacketSerializer.BuildClientTeleportSettings(_allowClientTeleport));
                            EnqueueSend(session, PacketSerializer.BuildGameModeState(_hideSeek.CurrentState));
                            if (_syncFlags)
                                EnqueueProgressSnapshot(session, reason: "join");
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
                                var rosterDirty = false;
                                var modelChanged = false;
                                if (payload.Length >= 10)
                                {
                                    session.PingMs = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, 2));
                                    rosterDirty = true;
                                }
                                // Model id is always present on modern heartbeats. Empty means
                                // intentional retail; do not treat a short legacy heartbeat as clear.
                                // Heartbeats advertise the client's current selection every tick and
                                // remain authoritative for roster appearance even after sequenced
                                // MarioModelIntent traffic — otherwise a dropped/coalesced intent
                                // permanently freezes remotes on the last accepted id.
                                if (payload.Length >= 10 + ProtocolConstants.MarioModelIdSize)
                                {
                                    var modelId = CharacterPack.DecodeModelId(
                                        payload.AsSpan(10, ProtocolConstants.MarioModelIdSize));
                                    if (!string.Equals(session.MarioModelId, modelId, StringComparison.Ordinal))
                                    {
                                        session.MarioModelId = modelId;
                                        rosterDirty = true;
                                        modelChanged = true;
                                    }
                                }
                                if (rosterDirty)
                                    MaybeBroadcastRoster(force: modelChanged);
                                EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.Heartbeat, payload));
                            }
                            break;

                        case TcpPacketId.MarioModelIntent:
                            if (session == null ||
                                string.IsNullOrEmpty(session.Username) ||
                                !PacketSerializer.TryReadMarioModelIntent(
                                    payload, out var modelIntentSequence, out var modelIntent))
                            {
                                break;
                            }
                            // TCP preserves wire order, but concurrent client tasks
                            // can queue rapid selections in scheduler order. Reject
                            // stale sequenced frames. Legacy/unsequenced intents
                            // cannot override an active sequenced stream; heartbeats
                            // still refresh MarioModelId independently (current look).
                            if (modelIntentSequence == 0)
                            {
                                if (session.LastMarioModelIntentSequence != 0)
                                    break;
                            }
                            else
                            {
                                if (session.LastMarioModelIntentSequence != 0 &&
                                    (int)(modelIntentSequence -
                                          session.LastMarioModelIntentSequence) <= 0)
                                {
                                    break;
                                }
                                session.LastMarioModelIntentSequence = modelIntentSequence;
                            }
                            modelIntent = CharacterPack.NormalizeModelId(modelIntent);
                            session.LastSeen = DateTime.UtcNow;
                            if (!string.Equals(session.MarioModelId, modelIntent,
                                    StringComparison.Ordinal))
                            {
                                session.MarioModelId = modelIntent;
                                Log?.Invoke($"Model intent slot {session.Slot}: " +
                                            $"{(modelIntent.Length == 0 ? "retail" : modelIntent)}");
                                // Bypass the periodic roster throttle so every
                                // client can begin local preparation immediately.
                                MaybeBroadcastRoster(force: true);
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

                        case TcpPacketId.WorldProgressRequest:
                            if (session == null || !_syncFlags)
                                break;
                            EnqueueProgressSnapshot(session, reason: "client-request");
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
                                    // Solo stage-enter / death reload: clear authority so
                                    // periodic resync cannot resurrect vanilla-cleared coins.
                                    // Require occupancy <= 1 so a brief remote-snapshot gap
                                    // during load cannot wipe live co-op progress.
                                    if (RedCoinAuthority.IsMissionResetRequest(worldRequest))
                                    {
                                        var resetCourse = worldRequest.CourseId;
                                        var resetEpisode = LevelCatalog.NormalizeEpisodeFromGame(
                                            worldRequest.CourseId, worldRequest.EpisodeId, _levels);
                                        var resetOcc = GetEquivalentStageOccupancy(resetCourse,
                                            resetEpisode);
                                        // Refuse when any other session is already on this stage
                                        // (occupancy can lag one tick behind a joiner's module reset).
                                        var peerOnStage = false;
                                        foreach (var other in _sessions.Values)
                                        {
                                            if (other.Slot == session.Slot)
                                                continue;
                                            if (other.StageId == resetCourse &&
                                                StagesEquivalent(resetCourse, other.EpisodeId,
                                                    resetEpisode))
                                            {
                                                peerOnStage = true;
                                                break;
                                            }
                                        }

                                        if (resetOcc <= 1 && !peerOnStage)
                                        {
                                            _redCoinAuthority.ResetStage(resetCourse, resetEpisode);
                                            _worldEvents.RemoveRedCoinHistory(resetCourse, resetEpisode);
                                            Log?.Invoke(
                                                $"World sync: red-coin solo mission reset course={resetCourse}/{resetEpisode} slot={session.Slot} occupancy={resetOcc}");
                                        }
                                        else
                                        {
                                            Log?.Invoke(
                                                $"World sync: ignored red-coin solo mission reset course={resetCourse}/{resetEpisode} slot={session.Slot} occupancy={resetOcc} peerOnStage={(peerOnStage ? 1 : 0)}");
                                            // Dying client cleared its local mask on stageInit and
                                            // would otherwise wait up to 45s for periodic resync.
                                            EnqueueProgressSnapshot(session, reason: "co-op-death-catchup");
                                        }

                                        break;
                                    }

                                    if (!_redCoinAuthority.TryAcceptCollected(worldRequest, out var coinPayload0,
                                            out var coinReserved, out var coinPayload1, out var coinPayload2))
                                    {
                                        Log?.Invoke(
                                            $"World sync: rejected duplicate red coin index course={worldRequest.CourseId}/{worldRequest.EpisodeId} slot={session.Slot} reserved={worldRequest.Reserved} payload1={worldRequest.Payload1}");
                                        break;
                                    }

                                    var redEpisode = LevelCatalog.NormalizeEpisodeFromGame(
                                        worldRequest.CourseId, worldRequest.EpisodeId, _levels);
                                    broadcast = _worldEvents.CreateWorldEvent(
                                        worldRequest.Type,
                                        worldRequest.CourseId,
                                        redEpisode,
                                        coinPayload0,
                                        coinReserved,
                                        coinPayload1,
                                        coinPayload2);
                                    // Live co-op: also push a thin progress snapshot to every
                                    // other session already on this stage so a peer stuck behind
                                    // graffiti in the incoming queue still hides coins promptly.
                                    NotifySameStageRedCoinPeers(worldRequest.CourseId,
                                        redEpisode, session.Slot);
                                    break;

                                case WorldEventType.NpcCleaned:
                                    if (!_npcCleanAuthority.TryAcceptCleaned(worldRequest, out var cleanPayload0,
                                            out var cleanReserved, out var cleanPayload1))
                                    {
                                        Log?.Invoke(
                                            $"World sync: rejected duplicate npc-clean index course={worldRequest.CourseId}/{worldRequest.EpisodeId} slot={session.Slot} reserved={worldRequest.Reserved} payload1={worldRequest.Payload1}");
                                        break;
                                    }

                                    var npcEpisode = LevelCatalog.NormalizeEpisodeFromGame(
                                        worldRequest.CourseId, worldRequest.EpisodeId, _levels);
                                    broadcast = _worldEvents.CreateWorldEvent(
                                        worldRequest.Type,
                                        worldRequest.CourseId,
                                        npcEpisode,
                                        cleanPayload0,
                                        cleanReserved,
                                        cleanPayload1);
                                    break;

                                case WorldEventType.GraffitiCleaned:
                                    if (!_graffitiCleanAuthority.TryAcceptCleaned(worldRequest, out var grafPayload0,
                                            out var grafReserved, out var grafPayload1, out var grafPayload2))
                                    {
                                        Log?.Invoke(
                                            $"World sync: rejected duplicate/full graffiti cell course={worldRequest.CourseId}/{worldRequest.EpisodeId} slot={session.Slot} payload2={worldRequest.Payload2}");
                                        break;
                                    }

                                    // Plaza: broadcast under hub episode 255 so late-join /
                                    // decideNextScenario peers share one authority bucket.
                                    var grafEpisode = GraffitiCleanAuthority.NormalizeEpisode(
                                        worldRequest.CourseId, worldRequest.EpisodeId);
                                    broadcast = _worldEvents.CreateWorldEvent(
                                        worldRequest.Type,
                                        worldRequest.CourseId,
                                        grafEpisode,
                                        grafPayload0,
                                        grafReserved,
                                        grafPayload1,
                                        grafPayload2);
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
                                                worldRequest.Payload1,
                                                worldRequest.Payload2);
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
                                                worldRequest.Payload1,
                                                worldRequest.Payload2);
                                            break;

                                        case WorldEventType.StoryFlag:
                                            if (!_storyFlagAuthority.TryAcceptStory(worldRequest.Payload1, worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate story flag id=0x{worldRequest.Payload1:X8} val={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            broadcast = _worldEvents.CreateWorldEvent(
                                                worldRequest.Type,
                                                worldRequest.CourseId,
                                                worldRequest.EpisodeId,
                                                worldRequest.Payload0,
                                                worldRequest.Reserved,
                                                worldRequest.Payload1,
                                                worldRequest.Payload2);
                                            break;

                                        case WorldEventType.TriggerFlag:
                                            if (!_storyFlagAuthority.TryAcceptTrigger(
                                                    worldRequest.CourseId,
                                                    worldRequest.EpisodeId,
                                                    worldRequest.Payload1,
                                                    worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate trigger flag id=0x{worldRequest.Payload1:X8} val={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            // Plaza MapEvent latches are hub-global (episode 255),
                                            // matching StoryFlagAuthority storage and snapshots.
                                            var triggerEpisode =
                                                StoryFlagAuthority.IsPlazaHubTrigger(
                                                    worldRequest.CourseId, worldRequest.Payload1)
                                                    ? StoryFlagAuthority.PlazaHubEpisode
                                                    : worldRequest.EpisodeId;
                                            broadcast = _worldEvents.CreateWorldEvent(
                                                worldRequest.Type,
                                                worldRequest.CourseId,
                                                triggerEpisode,
                                                worldRequest.Payload0,
                                                worldRequest.Reserved,
                                                worldRequest.Payload1,
                                                worldRequest.Payload2);
                                            break;

                                        case WorldEventType.SecretComplete:
                                            if (!_storyFlagAuthority.TryAcceptSecret(worldRequest.Payload1, worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate secret flag id=0x{worldRequest.Payload1:X8} val={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            broadcast = _worldEvents.CreateWorldEvent(
                                                worldRequest.Type,
                                                worldRequest.CourseId,
                                                worldRequest.EpisodeId,
                                                worldRequest.Payload0,
                                                worldRequest.Reserved,
                                                worldRequest.Payload1,
                                                worldRequest.Payload2);
                                            break;

                                        case WorldEventType.NpcReact:
                                            if (!TryAcceptNpcReact(worldRequest))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate npc-react kind={worldRequest.Payload0} course={worldRequest.CourseId}/{worldRequest.EpisodeId} pos=0x{worldRequest.Payload1:X8} slot={session.Slot}");
                                                break;
                                            }

                                            broadcast = _worldEvents.CreateWorldEvent(
                                                worldRequest.Type,
                                                worldRequest.CourseId,
                                                worldRequest.EpisodeId,
                                                worldRequest.Payload0,
                                                worldRequest.Reserved,
                                                worldRequest.Payload1,
                                                worldRequest.Payload2);
                                            break;

                                        default:
                                        {
                                            broadcast = _worldEvents.CreateWorldEvent(
                                                worldRequest.Type,
                                                worldRequest.CourseId,
                                                worldRequest.EpisodeId,
                                                worldRequest.Payload0,
                                                worldRequest.Reserved,
                                                worldRequest.Payload1,
                                                worldRequest.Payload2);
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
        _hideSeek.TickGrace();

        if (!_hideSeek.CurrentState.TagActive)
            return;

        // Cheap early-out during Start Tag grace / warp proximity immunity.
        if (_hideSeek.IsProximityTagImmunityActive)
            return;

        var peers = CopyPeersToScratch(out var peerCount);
        var nowMs = Environment.TickCount64;
        for (var i = 0; i < peerCount; i++)
        {
            var other = peers[i];
            if (other.Slot == updatedSlot)
                continue;
            if (other.LastSnapshot.Connected == 0)
                continue;
            // Stale/loading positions are unreliable for proximity (often still at spawn).
            if (other.State is DolphinState.Loading or DolphinState.Warping or DolphinState.Booting)
                continue;

            var otherLagSeconds = other.LastSnapshotReceivedMs == 0
                ? 0f
                : MathF.Min((nowMs - other.LastSnapshotReceivedMs) / 1000f, HideSeekService.TagLagCompensationMaxSeconds);

            _hideSeek.ProcessSnapshot(updatedSlot, snap, other.Slot, other.LastSnapshot, 0f, otherLagSeconds);
            _hideSeek.ProcessSnapshot(other.Slot, other.LastSnapshot, updatedSlot, snap, otherLagSeconds, 0f);
        }
    }

    /// <summary>
    /// Snapshot connected sessions into a reusable array under lock. Caller must finish
    /// iterating before the next CopyPeersToScratch call on this instance.
    /// </summary>
    private ClientSession[] CopyPeersToScratch(out int count)
    {
        lock (_lock)
        {
            count = _sessions.Count;
            if (_peerScratch.Length < count)
                _peerScratch = new ClientSession[Math.Max(count, ProtocolConstants.StableMaxPlayers)];

            var i = 0;
            foreach (var s in _sessions.Values)
                _peerScratch[i++] = s;
            count = i;
            return _peerScratch;
        }
    }

    private ClientSession? AssignSlot(TcpClient tcp, out byte slot)
    {
        slot = 0;
        lock (_lock)
        {
            PruneExpiredReleases();

            // At capacity, reclaim ghosts / abandoned handshakes / heartbeat timeouts before
            // rejecting Full. Otherwise a half-open TCP or incomplete Handshake permanently
            // blocks reconnects when the lobby is full or nearly full.
            if (_sessions.Count >= _maxPlayers)
                ReclaimStaleSessionsLocked();

            if (_sessions.Count >= _maxPlayers)
                return null;

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

    /// <summary>
    /// Evict sessions that are safe to free for a new Handshake. Must run under <see cref="_lock"/>.
    /// </summary>
    private void ReclaimStaleSessionsLocked()
    {
        var now = DateTime.UtcNow;
        var reclaimed = false;
        foreach (var s in _sessions.Values.ToArray())
        {
            if (!IsSessionReclaimable(s, now))
                continue;
            EvictSessionLocked(s);
            reclaimed = true;
        }

        if (reclaimed)
            BroadcastRoster();
    }

    private static bool IsSessionReclaimable(ClientSession session, DateTime now)
    {
        if (!IsSessionTcpAlive(session))
            return true;
        if ((now - session.LastSeen).TotalMilliseconds > ProtocolConstants.DisconnectTimeoutMs)
            return true;
        // Handshake assigned a slot but JoinRequest never arrived — free it after a short grace
        // so connect-retry storms cannot pin every free slot until the 15s watchdog.
        if (string.IsNullOrEmpty(session.Username) &&
            (now - session.LastSeen).TotalMilliseconds > ProtocolConstants.AbandonedHandshakeGraceMs)
            return true;
        return false;
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

            // Same-name policy: prefer reconnect success over false NameTaken.
            // TcpClient.Connected is unreliable for half-closed sockets, so we do NOT require
            // a dead TCP before replacing. Reject NameTaken only when the existing session is
            // clearly a different live player (truly-alive TCP, recent heartbeats, different
            // endpoint, and outside the reconnect window). Otherwise treat as reconnect and
            // replace — critical when a ghost still occupies the name at high player counts.
            if (_usernames.TryGetValue(name, out var existingSlot) && existingSlot != session.Slot)
            {
                if (_sessions.TryGetValue(existingSlot, out var existingSession))
                {
                    if (IsClearlyDifferentLivePlayer(existingSession, session, name))
                    {
                        reason = JoinRejectReason.NameTaken;
                        return false;
                    }

                    var priorSlot = existingSession.Slot;
                    // Keep hide-seek role on this slot — the reconnecting player is taking it over.
                    EvictSessionLocked(existingSession, notifyHideSeek: false);
                    // Take over the prior slot so roster / hide-seek identity stays stable and
                    // the temporary Handshake slot is released immediately.
                    if (priorSlot != session.Slot)
                        MigrateSessionToSlotLocked(session, priorSlot);
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
            session.LastMarioModelIntentSequence = 0;

            // Reconnect slot preference: if this player just disconnected within the reconnect
            // window and their previous slot is still free, migrate them back so roster identity,
            // hide-&-seek role, and name-tag continuity are preserved across a quick reconnect.
            // The client adopts the slot from JoinAccepted[0], so re-keying here is transparent.
            if (_recentReleases.TryGetValue(name, out var release) &&
                release.Slot != session.Slot &&
                release.Slot < _maxPlayers &&
                !_sessions.ContainsKey(release.Slot))
            {
                MigrateSessionToSlotLocked(session, release.Slot);
                _usernames[name] = release.Slot;
                Log?.Invoke($"Reconnect: slot {release.Slot} restored for '{name}'");
            }

            _recentReleases.Remove(name);
        }
        return true;
    }

    /// <summary>
    /// True only when another connection with this name is almost certainly a distinct live
    /// player — not a reconnect/ghost. Must run under <see cref="_lock"/>.
    /// </summary>
    private bool IsClearlyDifferentLivePlayer(ClientSession existing, ClientSession incoming, string name)
    {
        // Inside the reconnect window → always treat as the same player coming back.
        if (_recentReleases.ContainsKey(name))
            return false;

        if (string.IsNullOrEmpty(existing.Username))
            return false;

        if (!IsSessionTcpAlive(existing))
            return false;

        if ((DateTime.UtcNow - existing.LastSeen).TotalMilliseconds > ProtocolConstants.StaleTimeoutMs)
            return false;

        if (!TryGetRemoteEndPoint(existing, out var existingEp) ||
            !TryGetRemoteEndPoint(incoming, out var incomingEp))
            return false;

        // Same remote endpoint cannot be two distinct live players.
        if (existingEp.Equals(incomingEp))
            return false;

        return true;
    }

    private static bool TryGetRemoteEndPoint(ClientSession session, out IPEndPoint endpoint)
    {
        endpoint = null!;
        try
        {
            if (session.Tcp.Client.RemoteEndPoint is IPEndPoint ep)
            {
                endpoint = ep;
                return true;
            }
        }
        catch
        {
            // socket already disposed
        }
        return false;
    }

    private void MigrateSessionToSlotLocked(ClientSession session, byte targetSlot)
    {
        if (session.Slot == targetSlot)
            return;

        _sessions.TryRemove(session.Slot, out _);
        session.Slot = targetSlot;
        _sessions[targetSlot] = session;
    }

    /// <summary>
    /// Detect half-closed TCP. <see cref="TcpClient.Connected"/> alone is insufficient — it
    /// often stays true after the peer is gone, which previously caused false NameTaken and
    /// capacity ghosts until the 15s watchdog.
    /// </summary>
    private static bool IsSessionTcpAlive(ClientSession session)
    {
        try
        {
            var tcp = session.Tcp;
            if (!tcp.Connected)
                return false;

            var socket = tcp.Client;
            if (socket is not { Connected: true })
                return false;

            // Readable + zero bytes available ⇒ peer closed the connection (half-open/FIN).
            return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    private void EvictSessionLocked(ClientSession session, bool notifyHideSeek = true)
    {
        // Only remove if this object still owns the slot — prevents a racing HandleClient
        // finally from wiping a replacement session that already took over the slot.
        if (!_sessions.TryGetValue(session.Slot, out var current) || !ReferenceEquals(current, session))
            return;

        _sessions.TryRemove(session.Slot, out _);
        if (!string.IsNullOrEmpty(session.Username))
        {
            _recentReleases[session.Username] = (session.Slot, DateTime.UtcNow);
            if (_usernames.TryGetValue(session.Username, out var mapped) && mapped == session.Slot)
                _usernames.Remove(session.Username);
        }

        if (IsTrackedStage(session.StageId, session.EpisodeId))
            ReleaseStageOccupancyLocked(session.StageId, session.EpisodeId);

        // Enqueue the disconnect notice without waiting — eviction runs under _lock and the
        // connection is typically already dead. The send task drains what it can, then exits.
        FlushSendChannel(session, PacketSerializer.BuildDisconnect(DisconnectReason.Timeout));

        try { session.Tcp.Close(); } catch { }

        session.LastSnapshot = default;
        session.LatestUdpSnapshotPacket = null;
        session.LastSnapshotSeq = 0;
        session.LastSnapshotReceivedMs = 0;
        session.UdpEndPoint = null;

        if (notifyHideSeek && _hideSeek.CurrentState.GameMode == GameMode.HideSeek)
        {
            if (session.IsHost)
                _hideSeek.SetGameMode(GameMode.Normal);
            else
                _hideSeek.OnPlayerDisconnected(session.Slot);
        }

        Log?.Invoke($"Evicted stale session for slot {session.Slot}");
    }

    internal bool TryAcceptNpcReact(WorldEventRequest request)
    {
        var key = (request.CourseId, request.EpisodeId, request.Payload0, request.Reserved,
            request.Payload1);
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_npcReactRecent.Count > 64)
            {
                var stale = _npcReactRecent
                    .Where(pair => now - pair.Value > NpcReactDedupWindow)
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (var staleKey in stale)
                    _npcReactRecent.Remove(staleKey);
            }

            if (_npcReactRecent.TryGetValue(key, out var last) && now - last < NpcReactDedupWindow)
                return false;

            _npcReactRecent[key] = now;
            return true;
        }
    }

    private void RemoveSession(ClientSession? session, DisconnectReason reason = DisconnectReason.Timeout)
    {
        if (session == null) return;

        var removed = false;
        lock (_lock)
        {
            // Reference check: after same-name replace, the old HandleClient finally must not
            // remove the replacement session that now owns this slot.
            if (_sessions.TryGetValue(session.Slot, out var current) && ReferenceEquals(current, session))
            {
                _sessions.TryRemove(session.Slot, out _);
                removed = true;
                if (!string.IsNullOrEmpty(session.Username))
                {
                    _recentReleases[session.Username] = (session.Slot, DateTime.UtcNow);
                    if (_usernames.TryGetValue(session.Username, out var mapped) && mapped == session.Slot)
                        _usernames.Remove(session.Username);
                }
            }
        }

        if (!removed)
            return;

        ReleaseSessionStageOccupancy(session);

        // Best-effort: let the dedicated writer flush the disconnect frame before closing the socket.
        FlushSendChannel(session, PacketSerializer.BuildDisconnect(reason));

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
        // Iterate by slot index (0..maxPlayers-1) instead of OrderBy to keep roster builds
        // allocation-light and deterministic at up to 10 players.
        // Entry: slot(1)+name(16)+stage(1)+ep(1)+state(1)+ping(2)+modelId(8) = 30
        var list = new List<byte>(1 + _maxPlayers * ProtocolConstants.RosterEntrySize);
        lock (_lock)
        {
            list.Add((byte)_sessions.Count);
            for (byte i = 0; i < _maxPlayers; i++)
            {
                if (!_sessions.TryGetValue(i, out var s))
                    continue;

                list.Add(s.Slot);
                var nameBytes = new byte[16];
                var raw = System.Text.Encoding.UTF8.GetBytes(s.Username);
                Array.Copy(raw, nameBytes, Math.Min(raw.Length, 15));
                list.AddRange(nameBytes);
                list.Add(s.StageId);
                list.Add(s.EpisodeId);
                list.Add((byte)s.State);
                list.AddRange(BitConverter.GetBytes(s.PingMs));
                list.AddRange(CharacterPack.EncodeModelId(s.MarioModelId));
            }
        }
        return list.ToArray();
    }

    private void MaybeBroadcastRoster(bool force = false)
    {
        _hideSeek.TickGrace();

        var signature = ComputeRosterSignature();
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastRosterBroadcastUtc).TotalMilliseconds;
        if (!force && _lastRosterSignature == signature && elapsed < RosterKeepAliveIntervalMs)
            return;

        if (!force && elapsed < ProtocolConstants.RosterBroadcastIntervalMs)
            return;

        _lastRosterSignature = signature;
        _lastRosterBroadcastUtc = now;
        BroadcastRoster();
    }

    private ulong ComputeRosterSignature()
    {
        var hash = 14695981039346656037ul;

        lock (_lock)
        {
            HashRosterByte(ref hash, (byte)_sessions.Count);
            for (byte slot = 0; slot < _maxPlayers; slot++)
            {
                if (!_sessions.TryGetValue(slot, out var session))
                    continue;

                HashRosterByte(ref hash, session.Slot);
                HashRosterString(ref hash, session.Username);
                HashRosterByte(ref hash, session.StageId);
                HashRosterByte(ref hash, session.EpisodeId);
                HashRosterByte(ref hash, (byte)session.State);
                HashRosterByte(ref hash, (byte)session.PingMs);
                HashRosterByte(ref hash, (byte)(session.PingMs >> 8));
                HashRosterString(ref hash, session.MarioModelId);
            }
        }

        return hash;
    }

    private static void HashRosterByte(ref ulong hash, byte value)
    {
        hash ^= value;
        hash *= 1099511628211ul;
    }

    private static void HashRosterString(ref ulong hash, string value)
    {
        foreach (var ch in value)
        {
            HashRosterByte(ref hash, (byte)ch);
            HashRosterByte(ref hash, (byte)(ch >> 8));
        }
        HashRosterByte(ref hash, 0xFF);
    }

    private int GetStageOccupancy(byte courseId, byte episodeId)
    {
        lock (_lock)
        {
            return _stageOccupancy.TryGetValue((courseId, episodeId), out var count) ? count : 0;
        }
    }

    /// <summary>
    /// Occupancy across Sirena casino/hotel mission↔catalog aliases so co-op death
    /// rules and red-coin snapshot filters match roster stage keys.
    /// </summary>
    private int GetEquivalentStageOccupancy(byte courseId, byte episodeId)
    {
        lock (_lock)
        {
            var total = 0;
            foreach (var pair in _stageOccupancy)
            {
                if (pair.Key.Item1 != courseId)
                    continue;
                if (StagesEquivalent(courseId, pair.Key.Item2, episodeId))
                    total += pair.Value;
            }

            return total;
        }
    }

    private static bool StagesEquivalent(byte courseId, byte episodeA, byte episodeB)
    {
        if (episodeA == episodeB)
            return true;
        return LevelCatalog.NormalizeEpisodeFromGame(courseId, episodeA) ==
               LevelCatalog.NormalizeEpisodeFromGame(courseId, episodeB);
    }

    private bool ShouldResyncRedCoinStage(byte courseId, byte episodeId) =>
        GetEquivalentStageOccupancy(courseId, episodeId) >= 2;

    /// <summary>
    /// After a live red-coin collect, push authority catch-up to every other player already
    /// on that stage so hides are not stuck behind graffiti in the single incoming slot.
    /// </summary>
    private void NotifySameStageRedCoinPeers(byte courseId, byte episodeId, byte collectorSlot)
    {
        ClientSession[] peers;
        lock (_lock)
        {
            peers = _sessions.Values
                .Where(s => s.Slot != collectorSlot &&
                            s.StageId == courseId &&
                            StagesEquivalent(courseId, s.EpisodeId, episodeId))
                .ToArray();
        }

        foreach (var peer in peers)
            EnqueueProgressSnapshot(peer, reason: "live-red-coin-peer");
    }

    private void EnqueueProgressSnapshot(ClientSession session, string reason)
    {
        var frame = _worldEvents.BuildAuthoritySnapshotReplay(
            _shineAuthority, _blueCoinAuthority, _redCoinAuthority, _npcCleanAuthority,
            _graffitiCleanAuthority, _storyFlagAuthority, ShouldResyncRedCoinStage);
        // Empty snapshot (2-byte count) is still useful so clients clear "waiting for sync".
        EnqueueSend(session, frame);
        var shineCount = _shineAuthority.Collected.Count;
        var blueCourses = _blueCoinAuthority.AllCourses.Count;
        var redStages = _redCoinAuthority.AllStages.Count;
        var npcCleanStages = _npcCleanAuthority.AllStages.Count;
        var graffitiStages = _graffitiCleanAuthority.AllStages.Count;
        var storyFlags = _storyFlagAuthority.TotalCount;
        Log?.Invoke(
            $"World sync: progress snapshot → slot {session.Slot} ({reason}) shines={shineCount} blueCourses={blueCourses} redStages={redStages} npcCleans={npcCleanStages} graffitiStages={graffitiStages} storyFlags={storyFlags} durableHistory={_worldEvents.History.Count}");
    }

    private void MaybeBroadcastProgressResync(bool force = false)
    {
        if (!_syncFlags)
            return;

        if (!force)
        {
            var elapsed = (DateTime.UtcNow - _lastProgressResyncUtc).TotalMilliseconds;
            if (elapsed < ProtocolConstants.WorldProgressResyncIntervalMs)
                return;
        }

        if (_shineAuthority.Collected.Count == 0 &&
            _blueCoinAuthority.AllCourses.Count == 0 &&
            _redCoinAuthority.AllStages.Count == 0 &&
            _npcCleanAuthority.AllStages.Count == 0 &&
            _graffitiCleanAuthority.AllStages.Count == 0 &&
            _storyFlagAuthority.TotalCount == 0)
        {
            _lastProgressResyncUtc = DateTime.UtcNow;
            return;
        }

        _lastProgressResyncUtc = DateTime.UtcNow;
        var frame = _worldEvents.BuildAuthoritySnapshotReplay(
            _shineAuthority, _blueCoinAuthority, _redCoinAuthority, _npcCleanAuthority,
            _graffitiCleanAuthority, _storyFlagAuthority, ShouldResyncRedCoinStage);
        BroadcastTcp(frame);
        Log?.Invoke(
            $"World sync: periodic progress resync shines={_shineAuthority.Collected.Count} blueCourses={_blueCoinAuthority.AllCourses.Count} redStages={_redCoinAuthority.AllStages.Count} npcCleans={_npcCleanAuthority.AllStages.Count} graffitiStages={_graffitiCleanAuthority.AllStages.Count} storyFlags={_storyFlagAuthority.TotalCount}");
    }

    private void BroadcastRoster()
    {
        var roster = BuildRoster();
        BroadcastTcp(PacketSerializer.WrapTcp(TcpPacketId.RosterSnapshot, roster));

        PlayerRosterEntry[] entries;
        lock (_lock)
        {
            entries = new PlayerRosterEntry[_sessions.Count];
            int idx = 0;
            for (byte i = 0; i < _maxPlayers && idx < entries.Length; i++)
            {
                if (!_sessions.TryGetValue(i, out var s))
                    continue;
                entries[idx++] = new PlayerRosterEntry
                {
                    Slot = s.Slot,
                    Username = s.Username,
                    StageId = s.StageId,
                    EpisodeId = s.EpisodeId,
                    State = s.State,
                    PingMs = s.PingMs,
                    MarioModelId = s.MarioModelId,
                };
            }
        }
        RosterChanged?.Invoke(entries);
    }

    private void BroadcastTcp(byte[] frame)
    {
        // Enqueue to each session's dedicated writer task. This serializes writes per connection
        // (previously concurrent sync writes from broadcast + per-client echo could interleave on
        // the same NetworkStream and corrupt framing) and avoids blocking the relay/roster threads.
        foreach (var s in _sessions.Values)
            EnqueueSend(s, frame);
    }

    private static void EnqueueSend(ClientSession? session, byte[] frame)
    {
        if (session == null || session.SendChannel == null)
            return;
        session.SendChannel.Writer.TryWrite(frame);
    }

    private static Task StartSendLoop(ClientSession session, NetworkStream stream, CancellationToken ct)
    {
        session.SendChannel = Channel.CreateUnbounded<byte[]>();
        return Task.Run(() => SendLoop(session, stream, ct), ct);
    }

    private static async Task SendLoop(ClientSession session, NetworkStream stream, CancellationToken ct)
    {
        var channel = session.SendChannel!;
        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await stream.WriteAsync(frame, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Stream closed / broken — drain remaining and exit; RemoveSession handles cleanup.
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // server shutdown
        }
        catch
        {
            // writer side or stream fault — exit quietly
        }
    }

    /// <summary>Synchronous best-effort flush used from non-async teardown paths.</summary>
    private static void FlushSendChannel(ClientSession? session, byte[]? finalFrame = null)
    {
        if (session?.SendChannel == null)
            return;

        if (finalFrame != null)
            session.SendChannel.Writer.TryWrite(finalFrame);
        session.SendChannel.Writer.TryComplete();

        if (session.SendTask is { IsCompleted: false } sendTask)
        {
            try { sendTask.Wait(TimeSpan.FromMilliseconds(250)); }
            catch { /* tearing down anyway */ }
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

                var packetId = (UdpPacketId)result.Buffer[4];
                if (packetId == UdpPacketId.Ping)
                {
                    TryHandleUdpPing(result.Buffer, result.RemoteEndPoint);
                    continue;
                }

                if (packetId != UdpPacketId.PlayerSnapshot)
                    continue;

                var applied = TryApplySnapshotFromUdp(result.Buffer, result.RemoteEndPoint);
                if (!applied)
                    continue;
                // Fanout is coalesced by UdpSnapshotBroadcastLoop. At 10 players,
                // this replaces 90 sends per network tick with 10 bounded datagrams.
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

    private async Task UdpSnapshotBroadcastLoop(CancellationToken ct)
    {
        if (_udp == null)
            return;

        using var timer =
            new PeriodicTimer(TimeSpan.FromMilliseconds(ProtocolConstants.UdpSnapshotIntervalMs));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);

                var count = 0;
                for (byte slot = 0; slot < _maxPlayers; slot++)
                {
                    if (!_sessions.TryGetValue(slot, out var session) ||
                        session.UdpEndPoint == null)
                    {
                        continue;
                    }

                    var packet = session.LatestUdpSnapshotPacket;
                    if (packet == null ||
                        packet.Length <
                            ProtocolConstants.UdpSnapshotPayloadOffset +
                            ProtocolConstants.PlayerSnapshotSize ||
                        (UdpPacketId)packet[4] != UdpPacketId.PlayerSnapshot ||
                        packet[5] != slot)
                    {
                        continue;
                    }

                    var offset = ProtocolConstants.UdpSnapshotBatchHeaderSize +
                                 count * ProtocolConstants.UdpSnapshotBatchEntrySize;
                    _udpSnapshotBatchScratch[offset] = slot;
                    packet.AsSpan(6, 4).CopyTo(_udpSnapshotBatchScratch.AsSpan(offset + 1, 4));
                    packet.AsSpan(
                            ProtocolConstants.UdpSnapshotPayloadOffset,
                            ProtocolConstants.PlayerSnapshotSize)
                        .CopyTo(_udpSnapshotBatchScratch.AsSpan(
                            offset + 5,
                            ProtocolConstants.PlayerSnapshotSize));
                    count++;
                }

                if (count == 0)
                    continue;

                PacketSerializer.WriteUdpSnapshotBatchHeader(
                    _udpSnapshotBatchScratch,
                    (byte)count);
                var length = ProtocolConstants.UdpSnapshotBatchHeaderSize +
                             count * ProtocolConstants.UdpSnapshotBatchEntrySize;

                for (byte slot = 0; slot < _maxPlayers; slot++)
                {
                    if (!_sessions.TryGetValue(slot, out var session) ||
                        session.UdpEndPoint == null)
                    {
                        continue;
                    }

                    try
                    {
                        lock (_udpSendLock)
                            _udp.Send(_udpSnapshotBatchScratch, length, session.UdpEndPoint);
                    }
                    catch (SocketException) when (!ct.IsCancellationRequested)
                    {
                        // A stale UDP endpoint is reclaimed by the normal
                        // heartbeat/watchdog path; one failed recipient must not
                        // delay the rest of the batch.
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log?.Invoke($"UDP batch broadcast error: {ex.Message}");
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

    private bool TryHandleUdpPing(byte[] buffer, IPEndPoint sender)
    {
        if (buffer.Length < ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.UdpPingPayloadSize)
            return false;

        var slot = buffer[5];
        if (!_sessions.TryGetValue(slot, out var session))
            return true; // consume but don't reply to unknown slot

        // Validate sender against the session (same NAT-rebind logic as snapshots) so a Ping
        // can also (re)bind the UDP endpoint for a reconnecting client.
        if (session.UdpEndPoint == null)
        {
            session.UdpEndPoint = sender;
            session.LastSnapshotSeq = 0;
            Log?.Invoke($"UDP auto-bound (ping) for slot {slot} at {sender}");
        }
        else if (!session.UdpEndPoint.Equals(sender))
        {
            if (session.UdpEndPoint.Address.Equals(sender.Address))
            {
                session.UdpEndPoint = sender;
                session.LastSnapshotSeq = 0;
            }
            else
            {
                return false;
            }
        }

        // Echo the timestamp back as a Pong to the client's registered endpoint.
        BinaryPrimitives.WriteUInt32LittleEndian(_udpPongScratch.AsSpan(0, 4), ProtocolConstants.Magic);
        _udpPongScratch[4] = (byte)UdpPacketId.Pong;
        _udpPongScratch[5] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(_udpPongScratch.AsSpan(6, 4), 0u);
        buffer.AsSpan(ProtocolConstants.UdpSnapshotPayloadOffset, ProtocolConstants.UdpPingPayloadSize)
            .CopyTo(_udpPongScratch.AsSpan(ProtocolConstants.UdpSnapshotPayloadOffset,
                ProtocolConstants.UdpPingPayloadSize));

        try
        {
            lock (_udpSendLock)
                _udp!.Send(_udpPongScratch, _udpPongScratch.Length, session.UdpEndPoint);
        }
        catch (Exception ex) when (!_cts!.IsCancellationRequested)
        {
            Log?.Invoke($"UDP pong send error: {ex.Message}");
        }

        return true;
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
            // Allow a port-only change (same IP) so a reconnecting client behind NAT resumes
            // snapshot delivery without a fresh UdpRegister round-trip. A fully different address
            // is still rejected to prevent cross-client slot injection.
            if (session.UdpEndPoint.Address.Equals(sender.Address))
            {
                session.UdpEndPoint = sender;
                session.LastSnapshotSeq = 0;
                Log?.Invoke($"UDP rebound for slot {slot} (NAT port remap) -> {sender}");
            }
            else
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
        }

        var seq = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(6, 4));
        if (session.LastSnapshotSeq != 0 && (int)(seq - session.LastSnapshotSeq) <= 0)
            return false;

        var snap = PacketSerializer.SnapshotFromBytes(
            buffer.AsSpan(ProtocolConstants.UdpSnapshotPayloadOffset, ProtocolConstants.PlayerSnapshotSize),
            session.SnapshotNameBuffer);
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
        // ReceiveAsync owns this immutable datagram array. Keep the latest one
        // so the fixed-rate broadcast loop can batch raw payloads without
        // reserializing snapshots or racing the reusable name buffer.
        session.LatestUdpSnapshotPacket = buffer;
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
                if (IsSessionReclaimable(s, now))
                    RemoveSession(s);
            }

            MaybeBroadcastProgressResync();
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
        public string MarioModelId { get; set; } = "";
        public TcpClient Tcp { get; set; } = null!;
        public bool IsHost { get; set; }
        public byte StageId { get; set; }
        public byte EpisodeId { get; set; }
        public DolphinState State { get; set; }
        public ushort PingMs { get; set; }
        public uint LastMarioModelIntentSequence { get; set; }
        public DateTime LastSeen { get; set; }
        public long LastSnapshotReceivedMs { get; set; }
        public byte[] SnapshotNameBuffer { get; } = new byte[16];
        public byte[]? LatestUdpSnapshotPacket { get; set; }
        public PlayerSnapshot LastSnapshot { get; set; }
        public uint LastSnapshotSeq { get; set; }
        public IPEndPoint? UdpEndPoint { get; set; }
        public Channel<byte[]>? SendChannel { get; set; }
        public Task? SendTask { get; set; }
    }
}
