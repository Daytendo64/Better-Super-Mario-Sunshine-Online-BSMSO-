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
    private Task[]? _loopTasks;
    private bool _syncFlags;
    private bool _syncObjects;
    private bool _syncProgress;
    private bool _allowClientTeleport;
    private int _maxPlayers = ProtocolConstants.StableMaxPlayers;
    private DateTime _lastRosterBroadcastUtc = DateTime.MinValue;
    private ulong? _lastRosterSignature;
    private const int RosterKeepAliveIntervalMs = 1000;
    private DateTime _lastProgressResyncUtc = DateTime.MinValue;
    /// <summary>
    /// After host Reset Progress, reject client durable publishes briefly so peers
    /// can apply the clear before local poll re-fills the emptied authorities.
    /// </summary>
    private DateTime _progressResetAcceptResumeUtc = DateTime.MinValue;
    private static readonly TimeSpan ProgressResetAcceptGrace = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Last SessionProgressReset TCP frame for this server lifetime. Delivered once per
    /// player name (join / first snapshot) so a missed one-shot clear still heals late
    /// joiners without re-wiping a reconnecting player who already applied it.
    /// </summary>
    private byte[]? _lastSessionProgressResetFrame;
    /// <summary>
    /// Usernames that already received SessionProgressReset for the current wipe.
    /// Keyed by name (not slot) so a disconnect/reconnect of the same player does not
    /// re-fire the clear and wipe post-reset co-op progress. A different player joining
    /// under a new name still gets the heal clear once.
    /// </summary>
    private readonly HashSet<string> _sessionProgressResetDeliveredNames =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HideSeekService _hideSeek;
    private readonly WorldEventRelay _worldEvents = new();
    private readonly RedCoinAuthority _redCoinAuthority = new();
    private readonly NpcCleanAuthority _npcCleanAuthority = new();
    private readonly ShineAuthority _shineAuthority = new();
    private readonly BlueCoinAuthority _blueCoinAuthority = new();
    private readonly StoryFlagAuthority _storyFlagAuthority = new();
    // Include acting slot so two players hitting the same NPC are not collapsed forever.
    private readonly Dictionary<(byte CourseId, byte EpisodeId, byte Kind, byte ActingSlot, uint PackedPos), DateTime>
        _npcReactRecent = new();
    // 10-player plaza spray/trample repeats the same NPC hit every ~1s; 700ms let nearly
    // every repeat through and flooded TCP + the single incoming mailbox ahead of flags.
    private static readonly TimeSpan NpcReactDedupWindow = TimeSpan.FromMilliseconds(2500);
    /// <summary>Coalesce duplicate WorldProgressRequest from the same slot (stage-enter spam).</summary>
    private static readonly TimeSpan ClientProgressRequestDebounce = TimeSpan.FromSeconds(3);
    /// <summary>
    /// Debounce never silences — callers must still <c>EnqueueProgressSnapshot</c> (Unchanged
    /// when seq matches) so client catch-up does not stall ~20s without a reply.
    /// </summary>
    internal const bool ProgressRequestDebounceStillReplies = true;

    /// <summary>Force-full (seq=0) is never debounced; non-force may coalesce within the window.</summary>
    /// <summary>
    /// Build 36: force-full (clientSeq==0) must deliver a body but must not bump
    /// <c>progressSeq</c> — stage-enter storms were advancing seq by hundreds with few shines.
    /// </summary>
    internal const bool ForceFullProgressRequestBumpsProgressSeq = false;

    internal static bool ShouldDebounceProgressRequest(bool forceFull, bool withinDebounce)
        => !forceFull && withinDebounce;
    private readonly Dictionary<byte, DateTime> _lastClientProgressRequestUtc = new();
    private readonly Dictionary<(byte CourseId, byte EpisodeId), int> _stageOccupancy = new();
    /// <summary>
    /// Stages that reached occupancy 2+ during the current occupancy window. Cleared when
    /// the stage empties (with red-coin authority). Lets force-full / sticky co-op death
    /// heals still ship red masks after a peer leaves (occupancy 1) without resurrecting
    /// true solo mission-reset stages that never went co-op.
    /// </summary>
    private readonly HashSet<(byte CourseId, byte EpisodeId)> _redCoinCoopStages = new();
    /// <summary>
    /// Monotonic generation of authoritative progress. Clients send their last applied
    /// seq on WorldProgressRequest; matching seq → unchanged compact reply (no re-flood).
    /// </summary>
    private uint _progressSeq = 1;
    /// <summary>
    /// Build 24 primary heal path: push WorldProgressSnapshot to all peers on every
    /// ownership/mission authority mutation (125ms coalesce). Live ownership WorldEvents
    /// are not fanout — snapshot is primary (TCP durable-only Phase A).
    /// </summary>
    private ProgressPushCoalescer? _ownershipPush;
    /// <summary>
    /// Build 37: WorldProgressSnapshot / WorldStateReplay park on LatestProgressFrame and
    /// only pulse the send channel — never enqueue a second body copy for DropOldest.
    /// </summary>
    internal const bool ProgressSnapshotEnqueueIsLatestWinsOnly = true;

    internal const int OwnershipPushCoalesceMs = 200;
    // Reused across UDP relay / hide-seek tag checks so a full lobby does not allocate
    // ClientSession[] on every snapshot (~600/sec at 10×60 Hz).
    private ClientSession[] _peerScratch = new ClientSession[ProtocolConstants.StableMaxPlayers];
    private readonly byte[] _udpPongScratch =
        new byte[ProtocolConstants.UdpSnapshotPayloadOffset + ProtocolConstants.UdpPingPayloadSize];
    private readonly byte[] _udpSnapshotBatchScratch =
        new byte[ProtocolConstants.UdpSnapshotBatchMaxSize];
    private TaskCompletionSource<bool>? _acceptLoopStarted;
    private int _listenPort;

    /// <summary>
    /// Host identity is pinned to the hosting launcher's own loopback self-join, not to
    /// whoever connects first. A client that beat <c>HostAsync</c> to the accept loop used
    /// to become the server-side host and could push SyncSettings and warp everyone.
    /// </summary>
    internal const int HostClaimWindowMs = 10_000;
    /// <summary>Deadline for the hosting launcher to claim host; slot 0 is held until then.</summary>
    private DateTime _hostClaimDeadlineUtc = DateTime.MinValue;
    /// <summary>
    /// Set by <c>SMSO.ServerHost</c>. A dedicated server has no launcher self-join, so the
    /// reservation would only make the first real joiner wait out <see cref="HostClaimWindowMs"/>
    /// with warps and SyncSettings refused. Launcher hosting keeps the reservation (and the
    /// anti-hijack guarantee) untouched.
    /// </summary>
    public bool IsDedicatedServer { get; set; }
    /// <summary>
    /// True once a local (launcher) host claimed the session. Remote clients can then never
    /// take host, and the no-host watchdog promotion stays off.
    /// </summary>
    private bool _launcherHostClaimed;

    public event Action<string>? Log;
    public event Action<PlayerRosterEntry[]>? RosterChanged;

    public HideSeekService HideSeek => _hideSeek;
    public LevelCatalog Levels => _levels;

    public bool IsRunning { get; private set; }
    public int ListenPort => _listenPort;
    /// <summary>True once AcceptLoop has entered its first AcceptTcpClientAsync wait.</summary>
    public bool IsAccepting => _acceptLoopStarted?.Task.IsCompletedSuccessfully == true;
    /// <summary>
    /// Game profile this lobby hosts. Joining clients with any other profile are rejected
    /// with <see cref="JoinRejectReason.ProfileMismatch"/>. Eclipse also disables catalog
    /// warp validation and forces collectible sync off (maps not measured yet).
    /// </summary>
    public ushort ExpectedGameProfileId { get; set; } = ProtocolConstants.CurrentGameProfileId;
    public bool IsEclipseProfile => ExpectedGameProfileId == (ushort)GameProfileId.MarioEclipse;
    private bool _eclipseSyncOffLogged;
    public int MaxPlayers
    {
        get => _maxPlayers;
        set => _maxPlayers = Math.Clamp(value, 2, ProtocolConstants.StableMaxPlayers);
    }

    public GameServer(LevelCatalog levels)
    {
        _levels = levels;
        _hideSeek = new HideSeekService(this);
        _ownershipPush = new ProgressPushCoalescer(
            BroadcastOwnershipProgressPush,
            TimeSpan.FromMilliseconds(OwnershipPushCoalesceMs));
    }

    public void Start(int port)
    {
        if (IsRunning) return;

        Exception? lastBindError = null;
        for (var attempt = 0; attempt < ProtocolConstants.ServerBindRetryCount; attempt++)
        {
            try
            {
                BindListeners(port);
                lastBindError = null;
                break;
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.AddressAlreadyInUse
                    or SocketError.AccessDenied
                    or SocketError.AddressNotAvailable)
            {
                lastBindError = ex;
                ReleaseSockets();
                Log?.Invoke(
                    $"Server bind retry {attempt + 1}/{ProtocolConstants.ServerBindRetryCount} " +
                    $"on port {port}: {ex.SocketErrorCode}");
                if (attempt + 1 >= ProtocolConstants.ServerBindRetryCount)
                    break;
                Thread.Sleep(ProtocolConstants.ServerBindRetryBaseDelayMs * (attempt + 1));
            }
        }

        if (lastBindError != null)
            throw lastBindError;

        // Fresh listen: never inherit reconnect reservations or half-open ghosts from a
        // prior host lifetime (version bumps / rehost after ServerShutdown).
        lock (_lock)
        {
            _sessions.Clear();
            _usernames.Clear();
            _recentReleases.Clear();
            _lastClientProgressRequestUtc.Clear();
            _launcherHostClaimed = false;
            _hostClaimDeadlineUtc = IsDedicatedServer
                ? DateTime.MinValue
                : DateTime.UtcNow.AddMilliseconds(HostClaimWindowMs);
        }

        _cts = new CancellationTokenSource();
        _listenPort = port;
        _acceptLoopStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsRunning = true;
        Log?.Invoke(
            $"Server listening on TCP+UDP port {port} (ModBuildId {ProtocolConstants.ModBuildId})");
        _loopTasks = new[]
        {
            Task.Run(() => AcceptLoop(_cts.Token)),
            Task.Run(() => UdpRelayLoop(_cts.Token)),
            Task.Run(() => UdpSnapshotBroadcastLoop(_cts.Token)),
            Task.Run(() => WatchdogLoop(_cts.Token)),
        };
    }

    /// <summary>
    /// Wait until AcceptLoop is parked on AcceptTcpClientAsync (or timeout). Host self-connect
    /// should call this after Start so the first Join is not ConnectionRefused.
    /// </summary>
    public async Task WaitUntilAcceptingAsync(int timeoutMs = 2000, CancellationToken ct = default)
    {
        var started = _acceptLoopStarted;
        if (started == null)
            throw new InvalidOperationException("Server is not started.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await started.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Listener is bound even if AcceptLoop scheduling is slow — OS backlog still works.
            // Do not fail HostAsync solely on this race.
            Log?.Invoke("Server AcceptLoop not signaled yet — continuing (listener is bound)");
        }
    }

    private void BindListeners(int port)
    {
        TcpListener? tcp = null;
        UdpClient? udp = null;
        try
        {
            tcp = new TcpListener(IPAddress.Any, port);
            // Exclusive bind: two hosts must never silently share a port (half-open lobby).
            // Linger-0 + Start() bind retries recover TIME_WAIT after Stop/rehost without
            // SO_REUSEADDR (which allowed dual listeners on Windows).
            try { tcp.Server.ExclusiveAddressUse = true; }
            catch { /* platform */ }
            try { tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0)); }
            catch { /* platform */ }
            tcp.Start();

            udp = new UdpClient(AddressFamily.InterNetwork);
            try { udp.Client.ExclusiveAddressUse = true; }
            catch { /* platform */ }
            try { udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0)); }
            catch { /* platform */ }
            ConfigureUdpSocketForServer(udp.Client);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            _tcpListener = tcp;
            _udp = udp;
            tcp = null;
            udp = null;
        }
        finally
        {
            if (tcp != null)
            {
                try { tcp.Stop(); } catch { /* ignore */ }
            }

            if (udp != null)
            {
                try { udp.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    public void Stop()
    {
        if (!IsRunning && _tcpListener == null && _udp == null) return;
        IsRunning = false;

        // Drain any coalesced ownership push before tearing down send channels.
        _ownershipPush?.FlushNow();

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
            try { s.Tcp.Client.LingerState = new LingerOption(true, 0); } catch { /* ignore */ }
            try { s.Tcp.Close(); } catch { /* already closed */ }
        }

        _cts?.Cancel();
        ReleaseSockets();

        // Wait for Accept/UDP/watchdog loops to exit so an immediate rehost cannot race a
        // half-closed exclusive bind or a zombie AcceptLoop still parked on the old socket.
        var loops = _loopTasks;
        _loopTasks = null;
        if (loops is { Length: > 0 })
        {
            try
            {
                Task.WhenAll(loops).Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // expected during forced shutdown
            }
        }

        _cts?.Dispose();
        _cts = null;
        _acceptLoopStarted = null;
        _listenPort = 0;
        lock (_lock)
        {
            _sessions.Clear();
            _usernames.Clear();
            // Aggressive clear: version change / ServerShutdown must not leave slot
            // reservations that block a fresh lobby on the next Start.
            _recentReleases.Clear();
            _stageOccupancy.Clear();
            _redCoinCoopStages.Clear();
            _lastClientProgressRequestUtc.Clear();
            _launcherHostClaimed = false;
            _hostClaimDeadlineUtc = DateTime.MinValue;
        }
        _redCoinAuthority.Reset();
        _npcCleanAuthority.Reset();
        _shineAuthority.Reset();
        _blueCoinAuthority.Reset();
        _storyFlagAuthority.Reset();
        _hideSeek.Reset();
        _lastSessionProgressResetFrame = null;
        _progressResetAcceptResumeUtc = DateTime.MinValue;
        _sessionProgressResetDeliveredNames.Clear();
        Log?.Invoke("Server stopped");
    }

    private void ReleaseSockets()
    {
        try
        {
            if (_tcpListener?.Server is { } tcpSock)
            {
                try { tcpSock.LingerState = new LingerOption(true, 0); } catch { /* ignore */ }
                try { tcpSock.Close(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        try { _tcpListener?.Stop(); } catch { /* already stopped */ }
        _tcpListener = null;

        try
        {
            if (_udp?.Client is { } udpSock)
            {
                try { udpSock.LingerState = new LingerOption(true, 0); } catch { /* ignore */ }
                try { udpSock.Close(); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }

        try { _udp?.Dispose(); } catch { /* ignore */ }
        _udp = null;
    }

    public void NotifyShutdown()
    {
        if (!IsRunning) return;
        BroadcastTcp(PacketSerializer.BuildDisconnect(DisconnectReason.ServerShutdown));
        // Drop reconnect reservations immediately — clients must treat this as leave intent
        // for the old session (especially across ModBuildId bumps / rehost).
        lock (_lock)
            _recentReleases.Clear();
    }

    public void SetSyncSettings(bool syncFlags, bool syncObjects, bool syncProgress)
    {
        if (IsEclipseProfile && (syncFlags || syncObjects || syncProgress))
        {
            // Eclipse collectible maps are not measured yet — flag/object/progress sync
            // stays hard-off so authority heals can never write vanilla layouts.
            syncFlags = false;
            syncObjects = false;
            syncProgress = false;
            if (!_eclipseSyncOffLogged)
            {
                _eclipseSyncOffLogged = true;
                Log?.Invoke(
                    "Flag/Object/Progress sync forced OFF for Super Mario Eclipse (Phase 1) — " +
                    "Eclipse maps are not measured yet.");
            }
        }

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

    /// <summary>Test/diagnostics: slot holding host privileges, or null when the lobby has no host.</summary>
    internal byte? HostSlot
    {
        get
        {
            lock (_lock)
            {
                foreach (var session in _sessions.Values)
                {
                    if (session.IsHost)
                        return session.Slot;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Test seam: classify incoming connections without a second process. Instance-scoped
    /// so parallel tests cannot see each other's override.
    /// </summary>
    internal Func<TcpClient, HostConnectionKind>? ConnectionKindOverrideForTests { get; set; }

    /// <summary>Test seam: end the host reservation window immediately.</summary>
    internal void ExpireHostClaimWindowForTests()
    {
        lock (_lock)
            _hostClaimDeadlineUtc = DateTime.UtcNow.AddMilliseconds(-1);
    }

    public void BroadcastGameModeState(GameModeStatePacket state)
        => BroadcastTcp(PacketSerializer.BuildGameModeState(state));

    public GameModeStatePacket GetGameModeState() => _hideSeek.CurrentState;

    public void SetGameMode(GameMode mode) => _hideSeek.SetGameMode(mode);

    public void SetHideSeekRoles(IReadOnlyDictionary<byte, HideSeekRole> roles)
        => _hideSeek.SetRoles(roles);

    public bool TryStartHideSeekTag(out string? error) => _hideSeek.TryStartTag(out error);

    /// <summary>
    /// After Start Tag clears death-edge tracking, mark anyone already Dead so a
    /// leftover VFX_DEAD from a prior death reload cannot instantly promote them.
    /// </summary>
    internal void SeedHideSeekDeathBaseline()
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Values)
            {
                _hideSeek.NoteHiderDeathBaseline(
                    session.Slot,
                    HideSeekService.IsSnapshotDead(session.LastSnapshot));
            }
        }
    }

    public void SetHideSeekGraceDurationMs(int graceMs) =>
        _hideSeek.StartTagGraceDurationMs = graceMs;

    public int GetHideSeekGraceDurationMs() => _hideSeek.StartTagGraceDurationMs;

    public void StopHideSeekTag() => _hideSeek.StopTag();

    public void ResetHideSeekTag() => _hideSeek.ResetTag();

    /// <summary>
    /// Host mid-session "new file" clear of all durable session progress for everyone:
    /// shines, blues, story/secret/plaza Type5, red coins, NPC cleans.
    /// </summary>
    public void ResetSessionProgress()
    {
        if (!IsRunning)
            return;

        _shineAuthority.Reset();
        _blueCoinAuthority.Reset();
        _redCoinAuthority.Reset();
        _npcCleanAuthority.Reset();
        _storyFlagAuthority.Reset();
        _worldEvents.ClearDurableHistory();
        NoteProgressChanged();
        _progressResetAcceptResumeUtc = DateTime.UtcNow + ProgressResetAcceptGrace;

        var broadcast = _worldEvents.CreateWorldEvent(
            WorldEventType.SessionProgressReset, 0, 0, 0, 0, 0);
        _lastSessionProgressResetFrame = broadcast;
        _sessionProgressResetDeliveredNames.Clear();

        // Triple-send the same frame: SessionProgressReset is non-durable and a single
        // TCP drop previously left peers with progressed FlagManager state that
        // re-seeded authorities after the publish grace expired.
        BroadcastTcp(broadcast);
        BroadcastTcp(broadcast);
        BroadcastTcp(broadcast);

        // Also push per-session so join-order / send-channel buffering cannot skip a peer.
        ClientSession[] sessions;
        lock (_lock)
            sessions = _sessions.Values.ToArray();
        foreach (var session in sessions)
        {
            EnqueueSend(session, broadcast);
            if (!string.IsNullOrEmpty(session.Username))
                _sessionProgressResetDeliveredNames.Add(session.Username);
            EnqueueProgressSnapshot(session, reason: "session-progress-reset");
        }

        Log?.Invoke("Session progress reset (new-file scope) for everyone in this session");
    }

    /// <summary>
    /// Marks a username as having received SessionProgressReset. Returns true on first
    /// delivery for that name (case-insensitive). Reconnects of the same name return false.
    /// </summary>
    internal static bool TryMarkSessionProgressResetDelivered(HashSet<string> deliveredNames,
        string? username)
    {
        if (string.IsNullOrEmpty(username))
            return false;
        return deliveredNames.Add(username);
    }

    /// <summary>Legacy name — forwards to <see cref="ResetSessionProgress"/>.</summary>
    public void ResetShineBlueProgress() => ResetSessionProgress();

    private bool InProgressResetGrace => DateTime.UtcNow < _progressResetAcceptResumeUtc;

    public void RequestWarp(byte requesterSlot, byte targetSlot, byte courseId, byte episodeId)
    {
        if (IsEclipseProfile)
        {
            // Phase 1 Eclipse: no catalog yet — pass warps through untouched instead of
            // rejecting stages 61–92 as "invalid" or applying vanilla Sirena remaps.
        }
        else
        {
            if (!_levels.IsValidWarp(courseId, episodeId))
            {
                Log?.Invoke($"Invalid warp: course={courseId} episode={episodeId}");
                return;
            }

            // Beach Shadow Mario / hotel red-coin catalog → hotel interior so teleports and
            // authority keys share area 7 (sirena6/7 archives must not own those missions).
            LevelCatalog.ResolveWarpDestination(courseId, episodeId, out courseId, out episodeId);
        }

        lock (_lock)
        {
            // Never abort a warp-all because one peer is mid-load (common during HnS
            // death reload). Ready clients must still receive the command; BridgeWorker
            // queues warps for clients that are themselves loading.
            if (targetSlot == ProtocolConstants.WarpAllSlots)
            {
                foreach (var s in _sessions.Values)
                {
                    if (s.State is DolphinState.Loading or DolphinState.Warping)
                        Log?.Invoke($"Warp-all: slot {s.Slot} still loading — command still broadcast");
                }
            }
            else if (_sessions.TryGetValue(targetSlot, out var one) &&
                     one.State is DolphinState.Loading or DolphinState.Warping)
            {
                Log?.Invoke($"Warp: slot {targetSlot} loading — command still sent (client queues)");
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
        var normalized = NormalizeEpisodeForProfile(stageId, episodeId);
        var changed = session.StageId != stageId || session.EpisodeId != normalized;
        if (!changed)
            return false;

        var previousStage = session.StageId;
        var previousEpisode = session.EpisodeId;
        session.StageId = stageId;
        session.EpisodeId = normalized;
        UpdateStageOccupancy(session, previousStage, previousEpisode, stageId, normalized);
        return true;
    }

    /// <summary>
    /// Vanilla sessions normalize game scenario ids through the plaza/hotel/park/casino
    /// remap tables. Eclipse stage ids live in a different layout (hub = 78, gameplay
    /// areas 61–92), so Phase 1 passes episode ids through unchanged — the remap tables
    /// would mis-key Eclipse stages onto vanilla interior areas.
    /// </summary>
    private byte NormalizeEpisodeForProfile(byte courseId, byte episodeId) =>
        IsEclipseProfile
            ? episodeId
            : LevelCatalog.NormalizeEpisodeFromGame(courseId, episodeId, _levels);

    private void UpdateStageOccupancy(ClientSession joiningSession, byte previousStage,
        byte previousEpisode, byte newStage, byte newEpisode)
    {
        bool reachedCoop = false;
        lock (_lock)
        {
            if (IsTrackedStage(previousStage, previousEpisode))
                ReleaseStageOccupancyLocked(previousStage, previousEpisode);
            if (IsTrackedStage(newStage, newEpisode))
                reachedCoop = AcquireStageOccupancyLocked(newStage, newEpisode);
        }

        // Second player entered a stage that may already have red-coin authority — heal
        // ONLY the joiner. Broadcasting to every client cleared all pending mailbox queues
        // (WorldStateReplay → ClearPendingIncoming) and starved live shine/story applies
        // whenever any stage in a 10-player lobby hit occupancy 2.
        if (reachedCoop)
            EnqueueProgressSnapshot(joiningSession, reason: "stage-coop-start");
    }

    /// <summary>
    /// Plaza hub scenarios share one physical stage; decideNextScenario advances mEpisodeID
    /// without a reload. Occupancy must not fragment across dolpic archives or every hub
    /// episode drift looks like leave+join (stage-empty reset + coop-start flood).
    /// </summary>
    internal static (byte CourseId, byte EpisodeId) OccupancyKey(byte courseId, byte episodeId)
    {
        if (courseId == StoryFlagAuthority.PlazaAreaId)
            return (courseId, StoryFlagAuthority.PlazaHubEpisode);
        return (courseId, episodeId);
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
        var key = OccupancyKey(courseId, episodeId);
        _stageOccupancy.TryGetValue(key, out var count);
        var next = count + 1;
        _stageOccupancy[key] = next;
        if (next >= 2)
            _redCoinCoopStages.Add(key);
        return next == 2;
    }

    private void ReleaseStageOccupancyLocked(byte courseId, byte episodeId)
    {
        var key = OccupancyKey(courseId, episodeId);
        if (!_stageOccupancy.TryGetValue(key, out var count))
            return;

        count--;
        if (count <= 0)
        {
            _stageOccupancy.Remove(key);
            _redCoinCoopStages.Remove(key);
            if (key.CourseId == StoryFlagAuthority.PlazaAreaId)
            {
                // Occupancy is hub-coalesced; clear every plaza scenario bucket so a
                // leftover catalog-keyed red/npc mask cannot resurrect on re-entry.
                _redCoinAuthority.ResetCourse(key.CourseId);
                _worldEvents.RemoveCourseRedCoinHistory(key.CourseId);
                _npcCleanAuthority.ResetCourse(key.CourseId);
            }
            else
            {
                _redCoinAuthority.ResetStage(key.CourseId, key.EpisodeId);
                _worldEvents.RemoveRedCoinHistory(key.CourseId, key.EpisodeId);
                _npcCleanAuthority.ResetStage(key.CourseId, key.EpisodeId);
            }

            NoteProgressChanged();
            Log?.Invoke(
                $"World sync: reset red-coin/npc-clean state for course={key.CourseId}/{key.EpisodeId} (stage empty)");
        }
        else
        {
            _stageOccupancy[key] = count;
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        _acceptLoopStarted?.TrySetResult(true);
        while (!ct.IsCancellationRequested && _tcpListener != null)
        {
            try
            {
                var client = await _tcpListener.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                client.ReceiveBufferSize = 8192;
                client.SendBufferSize = 8192;
                try { client.Client.LingerState = new LingerOption(true, 0); } catch { /* platform */ }
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
                                // No session was created, so the finally's RemoveSession is a
                                // no-op — this connection must be torn down here or a
                                // reconnect storm at a full lobby leaks half-open sockets.
                                await stream.WriteAsync(PacketSerializer.WrapTcp(TcpPacketId.JoinRejected,
                                    new[] { (byte)JoinRejectReason.Full }), ct);
                                Log?.Invoke("Join rejected: lobby full");
                                return;
                            }

                            // Start the writer before any EnqueueSend so VersionMismatch /
                            // HandshakeAck frames actually reach the client.
                            session.SendTask = StartSendLoop(session, stream, ct);

                            // Early version gate when client includes ModBuildId after Guid.
                            // Legacy Guid-only handshakes still proceed; JoinRequest rejects them.
                            if (PacketSerializer.TryReadHandshakeModBuildId(payload, out var handshakeBuild) &&
                                handshakeBuild != ProtocolConstants.ModBuildId)
                            {
                                Log?.Invoke(
                                    $"Handshake VersionMismatch: client build {handshakeBuild}, " +
                                    $"server build {ProtocolConstants.ModBuildId}");
                                EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.JoinRejected,
                                    new[] { (byte)JoinRejectReason.VersionMismatch }));
                                RemoveSession(session, DisconnectReason.Kicked);
                                session = null;
                                return;
                            }

                            EnqueueSend(session, PacketSerializer.BuildHandshakeAck(session.Slot));
                            break;

                        case TcpPacketId.JoinRequest:
                            if (session == null) break;
                            if (!PacketSerializer.TryReadJoinRequest(payload, out var name, out var joinModelId, out var joinBuildId, out var joinProfileId))
                            {
                                name = System.Text.Encoding.UTF8.GetString(payload).TrimEnd('\0');
                                joinModelId = string.Empty;
                                joinBuildId = 0;
                                joinProfileId = 0;
                            }
                            if (joinBuildId != ProtocolConstants.ModBuildId)
                            {
                                Log?.Invoke(
                                    $"Join VersionMismatch for '{name}': client build {joinBuildId}, " +
                                    $"server build {ProtocolConstants.ModBuildId} " +
                                    "(update launcher + ServerHost.exe + disc _BSMSO.kxe together)");
                                EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.JoinRejected,
                                    new[] { (byte)JoinRejectReason.VersionMismatch }));
                                RemoveSession(session, DisconnectReason.Kicked);
                                session = null;
                                return;
                            }
                            if (joinProfileId != ExpectedGameProfileId)
                            {
                                var clientProfile = Enum.IsDefined(typeof(GameProfileId), joinProfileId)
                                    ? GameProfileIds.DisplayName((GameProfileId)joinProfileId)
                                    : $"unknown ({joinProfileId})";
                                var serverProfile = Enum.IsDefined(typeof(GameProfileId), ExpectedGameProfileId)
                                    ? GameProfileIds.DisplayName((GameProfileId)ExpectedGameProfileId)
                                    : $"unknown ({ExpectedGameProfileId})";
                                Log?.Invoke(
                                    $"Join ProfileMismatch for '{name}': client profile {clientProfile}, " +
                                    $"server profile {serverProfile}");
                                EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.JoinRejected,
                                    new[] { (byte)JoinRejectReason.ProfileMismatch }));
                                RemoveSession(session, DisconnectReason.Kicked);
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
                            _hideSeek.OnPlayerJoined(session.Slot, session.Username);
                            var roster = BuildRoster();
                            var accepted = new byte[1 + roster.Length];
                            accepted[0] = session.Slot;
                            roster.CopyTo(accepted, 1);
                            EnqueueSend(session, PacketSerializer.WrapTcp(TcpPacketId.JoinAccepted, accepted));
                            EnqueueSend(session, PacketSerializer.BuildSyncSettings(_syncFlags, _syncObjects, _syncProgress));
                            EnqueueSend(session, PacketSerializer.BuildClientTeleportSettings(_allowClientTeleport));
                            // Join snapshot must not re-arm round-end fanfare for late joiners —
                            // RoundFanfare sticks on the server until the next Start Tag.
                            var joinMode = _hideSeek.CurrentState;
                            joinMode.Flags &= ~GameModeFlags.RoundFanfare;
                            EnqueueSend(session, PacketSerializer.BuildGameModeState(joinMode));
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
                            // Stage-enter / plaza episode drift used to re-request every tick;
                            // coalesce per slot so 10 clients cannot TCP-flood authority replays.
                            // Force-full (clientSeq==0) must NEVER be coalesced to silence —
                            // the client already cleared its progress mailbox and needs a reply.
                            PacketSerializer.TryReadWorldProgressRequestClientSeq(payload,
                                out var clientProgressSeq);
                            var forceFullRequest = clientProgressSeq == 0;
                            var nowReq = DateTime.UtcNow;
                            var debounced = false;
                            lock (_lock)
                            {
                                if (!forceFullRequest &&
                                    _lastClientProgressRequestUtc.TryGetValue(session.Slot, out var lastReq) &&
                                    nowReq - lastReq < ClientProgressRequestDebounce)
                                {
                                    debounced = true;
                                }
                                else
                                {
                                    _lastClientProgressRequestUtc[session.Slot] = nowReq;
                                }
                            }

                            // Debounce must still reply (Unchanged when seq matches). Silence
                            // lets the client catch-up timer advance with no heal for ~20s.
                            // Force-full (seq=0) never enters the debounce branch above.
                            if (debounced)
                            {
                                Log?.Invoke(
                                    $"World sync: coalesced progress request from slot {session.Slot} (debounce ack)");
                                EnqueueProgressSnapshot(session, reason: "client-request-debounce",
                                    clientProgressSeq: clientProgressSeq);
                            }
                            else
                            {
                                EnqueueProgressSnapshot(session, reason: "client-request",
                                    clientProgressSeq: clientProgressSeq);
                            }
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
                                        var resetEpisode = NormalizeEpisodeForProfile(
                                            worldRequest.CourseId, worldRequest.EpisodeId);
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
                                            NoteProgressChanged();
                                            Log?.Invoke(
                                                $"World sync: red-coin solo mission reset course={resetCourse}/{resetEpisode} slot={session.Slot} occupancy={resetOcc}");
                                        }
                                        else
                                        {
                                            // Peer visible (or occupancy says co-op) — keep sticky
                                            // heal inclusion even if _stageOccupancy never hit 2.
                                            if (peerOnStage || resetOcc >= 2)
                                            {
                                                lock (_lock)
                                                    _redCoinCoopStages.Add(
                                                        OccupancyKey(resetCourse, resetEpisode));
                                            }
                                            Log?.Invoke(
                                                $"World sync: ignored red-coin solo mission reset course={resetCourse}/{resetEpisode} slot={session.Slot} occupancy={resetOcc} peerOnStage={(peerOnStage ? 1 : 0)}");
                                            // Dying client cleared its local mask on stageInit and
                                            // would otherwise wait up to 45s for periodic resync.
                                            EnqueueProgressSnapshot(session, reason: "co-op-death-catchup");
                                        }

                                        break;
                                    }

                                    if (InProgressResetGrace)
                                    {
                                        Log?.Invoke(
                                            $"World sync: rejected red coin during reset grace course={worldRequest.CourseId}/{worldRequest.EpisodeId} slot={session.Slot}");
                                        break;
                                    }

                                    if (!_redCoinAuthority.TryAcceptCollected(worldRequest, out _,
                                            out var coinReserved, out _, out _))
                                    {
                                        Log?.Invoke(
                                            $"World sync: rejected duplicate red coin index course={worldRequest.CourseId}/{worldRequest.EpisodeId} slot={session.Slot} reserved={worldRequest.Reserved} payload1={worldRequest.Payload1}");
                                        break;
                                    }

                                    // Phase A: no per-coin live WorldEvent. Authority + coalesced
                                    // ownership-push snapshot (stage mission bits) is the only path.
                                    NoteProgressChanged();
                                    var redEpisode = NormalizeEpisodeForProfile(
                                        worldRequest.CourseId, worldRequest.EpisodeId);
                                    // Sticky co-op bookkeeping without per-peer snapshot storms.
                                    // Same-stage peers get an immediate compact snapshot (not
                                    // per-coin WorldEvent fanout) so hides/FX are not stuck behind
                                    // the lobby ownership-push coalesce window (~200–500 ms).
                                    NotifySameStageRedCoinPeers(worldRequest.CourseId, redEpisode,
                                        session.Slot);
                                    Log?.Invoke(
                                        $"World sync: red-coin authority slot={session.Slot} course={worldRequest.CourseId}/{redEpisode} reserved={coinReserved} (snapshot-only)");
                                    break;

                                case WorldEventType.NpcCleaned:
                                    if (InProgressResetGrace)
                                    {
                                        Log?.Invoke(
                                            $"World sync: rejected npc-clean during reset grace course={worldRequest.CourseId}/{worldRequest.EpisodeId} slot={session.Slot}");
                                        break;
                                    }

                                    if (!_npcCleanAuthority.TryAcceptCleaned(worldRequest, out _,
                                            out var cleanReserved, out _))
                                    {
                                        Log?.Invoke(
                                            $"World sync: rejected duplicate npc-clean index course={worldRequest.CourseId}/{worldRequest.EpisodeId} slot={session.Slot} reserved={worldRequest.Reserved} payload1={worldRequest.Payload1}");
                                        break;
                                    }

                                    // Phase A: snapshot-only (coalesced ownership-push). No live fanout.
                                    NoteProgressChanged();
                                    var npcEpisode = NormalizeEpisodeForProfile(
                                        worldRequest.CourseId, worldRequest.EpisodeId);
                                    Log?.Invoke(
                                        $"World sync: npc-clean authority slot={session.Slot} course={worldRequest.CourseId}/{npcEpisode} reserved={cleanReserved} (snapshot-only)");
                                    break;

                                case WorldEventType.GraffitiCleaned:
                                    // Goop/graffiti sync permanently disabled — drop silently
                                    // so mixed-build clients cannot flood durable history / TCP.
                                    Log?.Invoke(
                                        $"World sync: ignored legacy graffiti event from slot {session.Slot} course={worldRequest.CourseId}/{worldRequest.EpisodeId}");
                                    break;

                                default:
                                {
                                    switch (worldRequest.Type)
                                    {
                                        case WorldEventType.ShineCollected:
                                            if (InProgressResetGrace)
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected shine during reset grace id={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            if (!_shineAuthority.TryAccept(worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate shine id={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            // Phase A: coalesced WorldProgressSnapshot is primary;
                                            // no live WorldEvent fanout for ownership.
                                            NoteProgressChanged();
                                            Log?.Invoke(
                                                $"World sync: shine authority id={worldRequest.Payload0} slot={session.Slot} (snapshot-only)");
                                            break;

                                        case WorldEventType.BlueCoinCollected:
                                            if (InProgressResetGrace)
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected blue coin during reset grace course={worldRequest.CourseId} index={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            if (!_blueCoinAuthority.TryAccept(worldRequest.CourseId, worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate blue coin course={worldRequest.CourseId} index={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            NoteProgressChanged();
                                            Log?.Invoke(
                                                $"World sync: blue authority course={worldRequest.CourseId} index={worldRequest.Payload0} slot={session.Slot} (snapshot-only)");
                                            break;

                                        case WorldEventType.SessionProgressReset:
                                            // Host-only via GameServer.ResetSessionProgress.
                                            Log?.Invoke(
                                                $"World sync: ignored client session progress reset request slot={session.Slot}");
                                            break;

                                        case WorldEventType.StoryFlag:
                                            if (InProgressResetGrace)
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected story flag during reset grace id=0x{worldRequest.Payload1:X8} slot={session.Slot}");
                                                break;
                                            }
                                            if (!_storyFlagAuthority.TryAcceptStory(worldRequest.Payload1, worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate story flag id=0x{worldRequest.Payload1:X8} val={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            NoteProgressChanged();
                                            Log?.Invoke(
                                                $"World sync: story authority id=0x{worldRequest.Payload1:X8} slot={session.Slot} (snapshot-only)");
                                            break;

                                        case WorldEventType.TriggerFlag:
                                            if (InProgressResetGrace)
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected trigger flag during reset grace id=0x{worldRequest.Payload1:X8} slot={session.Slot}");
                                                break;
                                            }

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
                                            NoteProgressChanged();
                                            Log?.Invoke(
                                                $"World sync: trigger authority id=0x{worldRequest.Payload1:X8} course={worldRequest.CourseId}/{triggerEpisode} slot={session.Slot} (snapshot-only)");
                                            break;

                                        case WorldEventType.SecretComplete:
                                            if (InProgressResetGrace)
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected secret flag during reset grace id=0x{worldRequest.Payload1:X8} slot={session.Slot}");
                                                break;
                                            }

                                            if (!_storyFlagAuthority.TryAcceptSecret(worldRequest.Payload1, worldRequest.Payload0))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: rejected duplicate secret flag id=0x{worldRequest.Payload1:X8} val={worldRequest.Payload0} slot={session.Slot}");
                                                break;
                                            }

                                            NoteProgressChanged();
                                            Log?.Invoke(
                                                $"World sync: secret authority id=0x{worldRequest.Payload1:X8} slot={session.Slot} (snapshot-only)");
                                            break;

                                        case WorldEventType.NpcReact:
                                        case WorldEventType.HipDropObject:
                                        case WorldEventType.YoshiFruitTaken:
                                        case WorldEventType.MarioFruitKicked:
                                        case WorldEventType.MarioFruitPicked:
                                        case WorldEventType.MarioFruitThrown:
                                        case WorldEventType.MarioFruitDropped:
                                        case WorldEventType.MarioFruitSync:
                                        case WorldEventType.GoldCoinCollected:
                                            // Phase A: never network ephemeral VFX / gold.
                                            // Chosen for 120-shine reliability (not UDP either).
                                            break;

                                        default:
                                        {
                                            if (WorldEventTcpPolicy.IsNonNetworkedEphemeral(worldRequest.Type))
                                                break;

                                            // Unknown durable-ish types still get an id for logging,
                                            // but only SessionProgressReset should live-fanout.
                                            if (!WorldEventTcpPolicy.RequiresLiveTcpFanout(worldRequest.Type) &&
                                                !WorldEventTcpPolicy.IsSnapshotOwnership(worldRequest.Type) &&
                                                !WorldEventTcpPolicy.IsSnapshotMission(worldRequest.Type))
                                            {
                                                Log?.Invoke(
                                                    $"World sync: dropped unhandled type={worldRequest.Type} from slot {session.Slot}");
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
        catch (Exception ex)
        {
            // Keep swallowing — a faulted client must never take the server down — but a
            // silent catch made 10-player join failures (framing, channel faults) invisible.
            if (!ct.IsCancellationRequested)
            {
                var who = session == null ? "unassigned" : $"slot {session.Slot}";
                Log?.Invoke($"Client handler ended ({who}): {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            RemoveSession(session);
            // Covers every early-return reject path (Full / VersionMismatch / NameTaken)
            // and any connection that never reached a session.
            CloseClientSocket(tcp);
        }
    }

    /// <summary>
    /// Graceful close for a finished connection: disable linger-0 first so a rejection
    /// frame written moments earlier is actually delivered instead of RST-discarded.
    /// </summary>
    private static void CloseClientSocket(TcpClient tcp)
    {
        try { tcp.Client.LingerState = new LingerOption(false, 0); } catch { /* already closed */ }
        try { tcp.Client.Shutdown(SocketShutdown.Send); } catch { /* already closed */ }
        try { tcp.Close(); } catch { /* already closed */ }
        try { tcp.Dispose(); } catch { /* already disposed */ }
    }

    internal void ProcessHideSeekTagsForSnapshot(byte updatedSlot, in PlayerSnapshot snap)
    {
        _hideSeek.TickGrace();

        // Allocation-free gate: CurrentState clones the packet, and this runs on every
        // inbound snapshot (~600/sec at 10 players).
        if (!_hideSeek.IsTagActive)
            return;

        // Cheap early-out during Start Tag grace / warp proximity immunity.
        if (_hideSeek.IsProximityTagImmunityActive)
            return;

        if (snap.Connected == 0)
            return;

        // Only seeker↔hider pairs on the same stage can tag. Filtering on role and stage
        // before the distance math turns the full n² sweep into the handful of pairs that
        // can actually produce a tag.
        var updatedRole = _hideSeek.GetRoleForSlot(updatedSlot);
        var peers = CopyPeersToScratch(out var peerCount);
        var nowMs = Environment.TickCount64;
        for (var i = 0; i < peerCount; i++)
        {
            var other = peers[i];
            if (other.Slot == updatedSlot)
                continue;
            if (other.LastSnapshot.Connected == 0)
                continue;
            if (other.LastSnapshot.StageId != snap.StageId)
                continue;
            if (_hideSeek.GetRoleForSlot(other.Slot) == updatedRole)
                continue;
            // Stale/loading positions are unreliable for proximity (often still at spawn).
            if (other.State is DolphinState.Loading or DolphinState.Warping or DolphinState.Booting)
                continue;

            var otherLagSeconds = other.LastSnapshotReceivedMs == 0
                ? 0f
                : MathF.Min((nowMs - other.LastSnapshotReceivedMs) / 1000f, HideSeekService.TagLagCompensationMaxSeconds);

            if (updatedRole == (byte)HideSeekRole.Seeker)
                _hideSeek.ProcessSnapshot(updatedSlot, snap, other.Slot, other.LastSnapshot, 0f, otherLagSeconds);
            else
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
        // Classify outside the lock — the owning-process lookup walks the OS TCP table.
        var kind = ConnectionKindOverrideForTests?.Invoke(tcp)
                   ?? HostConnectionClassifier.Classify(tcp, _listenPort);
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

            var reservationActive = !_launcherHostClaimed && DateTime.UtcNow < _hostClaimDeadlineUtc;
            var grantHost = ShouldGrantHostSession(
                kind, _launcherHostClaimed, AnyLiveHostLocked(), reservationActive,
                lobbyEmpty: _sessions.IsEmpty);

            // While the reservation stands, keep slot 0 for the host's self-join so roster
            // and Hide & Seek identity stay stable even if a client connects first.
            var firstSlot = (byte)(!grantHost && reservationActive && HasFreeSlotLocked(1) ? 1 : 0);

            for (var i = firstSlot; i < _maxPlayers; i++)
            {
                if (_sessions.ContainsKey(i))
                    continue;

                slot = i;
                var session = new ClientSession
                {
                    Slot = i,
                    Tcp = tcp,
                    IsHost = grantHost,
                    ConnectionKind = kind,
                    LastSeen = DateTime.UtcNow,
                };
                _sessions[i] = session;
                if (grantHost)
                {
                    if (kind is HostConnectionKind.SameProcess or HostConnectionKind.LoopbackUnverified)
                        _launcherHostClaimed = true;
                    Log?.Invoke($"Host session claimed slot {i} ({kind})");
                }
                return session;
            }
        }
        return null;
    }

    /// <summary>
    /// Host grant rule. The hosting launcher's own loopback connection is the only
    /// definitive host signal; everything else is either held off for the reservation
    /// window or (dedicated ServerHost, where no such connection ever arrives) allowed
    /// to lead once the window expires and nobody is host.
    /// </summary>
    internal static bool ShouldGrantHostSession(
        HostConnectionKind kind,
        bool launcherHostClaimed,
        bool anyLiveHost,
        bool reservationActive,
        bool lobbyEmpty)
    {
        if (anyLiveHost)
            return false;

        // Launcher self-join (also covers the host reconnecting after a transport drop).
        if (kind == HostConnectionKind.SameProcess)
            return true;

        // A launcher already owns this session — a remote client must never inherit host.
        if (launcherHostClaimed)
            return false;

        if (reservationActive)
        {
            // Owner lookup unavailable: fall back to "first loopback connection", which
            // still blocks the remote-client hijack this rule exists for.
            return kind == HostConnectionKind.LoopbackUnverified && lobbyEmpty;
        }

        // Dedicated server: nobody claimed host inside the window, so the lobby leads itself.
        return true;
    }

    /// <summary>Any session currently flagged host whose TCP is still alive. Under <see cref="_lock"/>.</summary>
    private bool AnyLiveHostLocked()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.IsHost && IsSessionTcpAlive(session))
                return true;
        }

        return false;
    }

    private bool HasFreeSlotLocked(byte fromSlot)
    {
        for (var i = fromSlot; i < _maxPlayers; i++)
        {
            if (!_sessions.ContainsKey(i))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Dedicated-server safety net: if the host reservation expired without a launcher
    /// claiming it, hand host to the longest-standing named session so the lobby is never
    /// left with nobody able to warp or change sync settings.
    /// </summary>
    internal void MaybePromoteHost()
    {
        if (_launcherHostClaimed || DateTime.UtcNow < _hostClaimDeadlineUtc)
            return;

        ClientSession? promoted = null;
        lock (_lock)
        {
            if (AnyLiveHostLocked())
                return;

            for (byte i = 0; i < _maxPlayers; i++)
            {
                if (!_sessions.TryGetValue(i, out var session))
                    continue;
                if (string.IsNullOrEmpty(session.Username) || !IsSessionTcpAlive(session))
                    continue;

                promoted = session;
                break;
            }

            if (promoted == null)
                return;

            promoted.IsHost = true;
        }

        Log?.Invoke($"Host privileges granted to slot {promoted.Slot} ('{promoted.Username}') — no host claimed this session");
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
            // Host leaving drops the whole mode (SetGameMode(Normal) performs the full
            // round cleanup, so no half-started round survives into the next session).
            if (session.IsHost)
                _hideSeek.SetGameMode(GameMode.Normal);
            else
                _hideSeek.OnPlayerDisconnected(session.Slot, session.Username);
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
                    // Keep a reconnect reservation for transport drops AND intentional leave
                    // (quick rejoin / same-slot restore). ServerShutdown / Stop clear these
                    // aggressively so stale old-build sessions cannot occupy slots.
                    if (reason != DisconnectReason.ServerShutdown && reason != DisconnectReason.Kicked)
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
        lock (_lock)
            _lastClientProgressRequestUtc.Remove(session.Slot);
        // Do NOT drop SessionProgressReset delivery for this username on disconnect.
        // Removing by slot previously re-sent the wipe on reconnect and erased
        // post-reset co-op progress. Late joiners with a new name still get the clear
        // via EnqueueProgressSnapshot's Add-on-name check.

        if (_hideSeek.CurrentState.GameMode == GameMode.HideSeek)
        {
            if (session.IsHost)
            {
                // Full round cleanup, not just a mode flip — a leftover started-round flag
                // used to make the next Start Tag skip the hide grace.
                _hideSeek.SetGameMode(GameMode.Normal);
                Log?.Invoke("Hide & Seek stopped — host disconnected.");
            }
            else
            {
                _hideSeek.OnPlayerDisconnected(session.Slot, session.Username);
            }
        }

        BroadcastRoster();
        Log?.Invoke($"Player left slot {session.Slot}");
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

    private bool StagesEquivalent(byte courseId, byte episodeA, byte episodeB)
        => IsEclipseProfile
            ? episodeA == episodeB
            : LevelCatalog.EpisodesEquivalent(courseId, episodeA, episodeB);

    /// <summary>
    /// Compact progress heals omit solo (never co-op) red stages so death-reset progress
    /// is not rebroadcast. Once a stage hit occupancy 2+ this window, keep including even
    /// at occupancy 1 — sticky co-op / peer-left / force-full seq=0 revive still needs
    /// authority bits while <c>sStageHadSameStagePeer</c> skips solo mission-reset.
    /// </summary>
    private bool ShouldResyncRedCoinStage(byte courseId, byte episodeId) =>
        ShouldIncludeRedCoinStageInHeal(
            GetEquivalentStageOccupancy(courseId, episodeId),
            StageHadRedCoinCoop(courseId, episodeId));

    /// <summary>
    /// Pure heal-inclusion rule (unit-tested). Occupancy ≥ 2 always; occupancy 1 only when
    /// the stage previously went co-op this occupancy window.
    /// </summary>
    internal static bool ShouldIncludeRedCoinStageInHeal(int equivalentOccupancy, bool stageHadCoop)
        => equivalentOccupancy >= 2 || stageHadCoop;

    private bool StageHadRedCoinCoop(byte courseId, byte episodeId)
    {
        lock (_lock)
        {
            foreach (var key in _redCoinCoopStages)
            {
                if (key.CourseId != courseId)
                    continue;
                if (StagesEquivalent(courseId, key.EpisodeId, episodeId))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Sticky co-op bookkeeping for red-coin heal inclusion without per-peer snapshot storms.
    /// Live hides rely on the coalesced ownership-push (Phase A TCP durable-only).
    /// </summary>
    private void MarkRedCoinCoopIfPeerOnStage(byte courseId, byte episodeId, byte collectorSlot)
    {
        lock (_lock)
        {
            foreach (var other in _sessions.Values)
            {
                if (other.Slot == collectorSlot)
                    continue;
                if (other.StageId == courseId &&
                    StagesEquivalent(courseId, other.EpisodeId, episodeId))
                {
                    _redCoinCoopStages.Add(OccupancyKey(courseId, episodeId));
                    return;
                }
            }
        }
    }

    /// <summary>
    /// After a live red-coin collect, push authority catch-up to every other player already
    /// on that stage so hides/FX are not stuck behind the lobby ownership-push coalesce
    /// window. Still one compact <see cref="WorldProgressSnapshot"/> per peer — not per-coin
    /// live WorldEvent fanout.
    /// </summary>
    private void NotifySameStageRedCoinPeers(byte courseId, byte episodeId, byte collectorSlot)
    {
        MarkRedCoinCoopIfPeerOnStage(courseId, episodeId, collectorSlot);
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

    private void NoteProgressChanged(bool scheduleOwnershipPush = true)
    {
        unchecked
        {
            _progressSeq++;
            if (_progressSeq == 0)
                _progressSeq = 1;
        }

        // Continuous catch-up: every authority mutation schedules a coalesced lobby-wide
        // WorldProgressSnapshot. Live WorldEvents remain best-effort secondary FX.
        if (scheduleOwnershipPush)
            _ownershipPush?.NoteChanged();
    }

    /// <summary>
    /// Primary heal fanout (build 24). Pushes current authority to every session without
    /// bumping seq again — <see cref="NoteProgressChanged"/> already advanced it.
    /// </summary>
    private void BroadcastOwnershipProgressPush()
    {
        if (!_syncFlags || !IsRunning)
            return;

        ClientSession[] sessions;
        lock (_lock)
            sessions = _sessions.Values.ToArray();
        if (sessions.Length == 0)
            return;

        var frame = _worldEvents.BuildAuthorityProgressSnapshotFrame(
            _shineAuthority, _blueCoinAuthority, _redCoinAuthority, _npcCleanAuthority,
            _storyFlagAuthority, _progressSeq, ShouldResyncRedCoinStage, unchanged: false);
        foreach (var session in sessions)
            EnqueueSend(session, frame);

        Log?.Invoke(
            $"World sync: ownership-push → {sessions.Length} peer(s) seq={_progressSeq} shines={_shineAuthority.Collected.Count} blueCourses={_blueCoinAuthority.AllCourses.Count}");
    }

    private void EnqueueProgressSnapshot(ClientSession session, string reason,
        uint clientProgressSeq = 0)
    {
        // Late joiners who never received the clear need SessionProgressReset once.
        // Track by username so the same player reconnecting is not wiped again.
        if (_lastSessionProgressResetFrame != null &&
            TryMarkSessionProgressResetDelivered(_sessionProgressResetDeliveredNames, session.Username))
        {
            EnqueueSend(session, _lastSessionProgressResetFrame);
        }

        var forceFull = clientProgressSeq == 0;
        // Build 36: force-full must still deliver a body (never Unchanged), but must NOT
        // bump progressSeq. Stage-enter / best-effort / coop-start used to force-bump on
        // every seq=0 request — logs showed seq→476 with only ~46 shines and 4 peers,
        // drowning TCP in full lobby snapshots. Same-seq body + client Push(moduleApplied=0)
        // is enough to reheal (see BridgeWorker.PushProgressSnapshot).
        var unchanged = !forceFull && clientProgressSeq != 0 && clientProgressSeq == _progressSeq;
        var frame = _worldEvents.BuildAuthorityProgressSnapshotFrame(
            _shineAuthority, _blueCoinAuthority, _redCoinAuthority, _npcCleanAuthority,
            _storyFlagAuthority, _progressSeq, ShouldResyncRedCoinStage, unchanged);
        EnqueueSend(session, frame);
        if (unchanged)
        {
            Log?.Invoke(
                $"World sync: progress unchanged → slot {session.Slot} ({reason}) seq={_progressSeq}");
            return;
        }

        var shineCount = _shineAuthority.Collected.Count;
        var blueCourses = _blueCoinAuthority.AllCourses.Count;
        var redStages = _redCoinAuthority.AllStages.Count;
        var npcCleanStages = _npcCleanAuthority.AllStages.Count;
        var storyFlags = _storyFlagAuthority.TotalCount;
        Log?.Invoke(
            $"World sync: progress snapshot → slot {session.Slot} ({reason}) seq={_progressSeq}{(forceFull ? " force-reheal" : "")} shines={shineCount} blueCourses={blueCourses} redStages={redStages} npcCleans={npcCleanStages} storyFlags={storyFlags} durableHistory={_worldEvents.History.Count}");
    }

    private void MaybeBroadcastProgressResync(bool force = false)
    {
        if (!_syncFlags)
            return;

        // Lobby-wide periodic full heal deleted as a reliability mechanism — it re-shipped
        // every ownership bit to everyone every 45s and smothered the single mailbox.
        // Keep force path for sync-settings re-enable / explicit heal.
        if (!force)
            return;

        if (_shineAuthority.Collected.Count == 0 &&
            _blueCoinAuthority.AllCourses.Count == 0 &&
            _redCoinAuthority.AllStages.Count == 0 &&
            _npcCleanAuthority.AllStages.Count == 0 &&
            _storyFlagAuthority.TotalCount == 0)
        {
            _lastProgressResyncUtc = DateTime.UtcNow;
            // During publish grace only: help peers that missed the one-shot clear.
            // After grace, stop — forever-rebroadcast was wiping new collects.
            if (_lastSessionProgressResetFrame != null && InProgressResetGrace)
            {
                BroadcastTcp(_lastSessionProgressResetFrame);
                Log?.Invoke("World sync: SessionProgressReset rebroadcast during reset grace");
            }
                return;
        }

        _lastProgressResyncUtc = DateTime.UtcNow;
        var frame = _worldEvents.BuildAuthorityProgressSnapshotFrame(
            _shineAuthority, _blueCoinAuthority, _redCoinAuthority, _npcCleanAuthority,
            _storyFlagAuthority, _progressSeq, ShouldResyncRedCoinStage);
        BroadcastTcp(frame);
        Log?.Invoke(
            $"World sync: forced progress resync seq={_progressSeq} shines={_shineAuthority.Collected.Count} blueCourses={_blueCoinAuthority.AllCourses.Count} redStages={_redCoinAuthority.AllStages.Count} npcCleans={_npcCleanAuthority.AllStages.Count} storyFlags={_storyFlagAuthority.TotalCount}");
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

    /// <summary>
    /// High-priority TCP bound. DropOldest under ownership/mission storms so a slow peer
    /// cannot grow RAM forever; authorities + progress snapshots remain heal truth.
    /// </summary>
    internal const int HighPrioritySendCapacity = 128;

    /// <summary>
    /// Leftover ephemeral lane for mixed-build clients. Phase A never enqueues fruit /
    /// react / hip-drop; DropOldest 8 keeps any leftover from starving ownership.
    /// </summary>
    internal const int LowPrioritySendCapacity = 8;

    /// <summary>
    /// Empty high-pri pulse that wakes <see cref="SendLoop"/> without enqueueing a body.
    /// Progress heals park on <see cref="ClientSession.LatestProgressFrame"/> only.
    /// </summary>
    private static readonly PrioritizedTcpFrame ProgressWakePulse =
        new(Array.Empty<byte>(), HighPriority: true);

    private static void EnqueueSend(ClientSession? session, byte[] frame)
    {
        if (session == null || session.SendChannel == null)
            return;

        // Latest-wins progress heal: park the newest body only. Also enqueueing into the
        // DropOldest channel duplicated every ownership-push (build 36 log: ~1k mailbox
        // applies from stale channel frames after LatestProgressFrame). Pulse wakes SendLoop.
        if (PacketSerializer.TryUnwrapTcp(frame, out var id, out _) &&
            id is TcpPacketId.WorldProgressSnapshot or TcpPacketId.WorldStateReplay)
        {
            Volatile.Write(ref session.LatestProgressFrame, frame);
            session.SendChannel.Writer.TryWrite(ProgressWakePulse);
            return;
        }

        // Ownership / progress / control must not sit behind ephemeral TCP backlog.
        // Phase A: ephemeral should not enqueue at all; still route low-pri defensively.
        if (IsHighPriorityTcpFrame(frame))
            session.SendChannel.Writer.TryWrite(new PrioritizedTcpFrame(frame, HighPriority: true));
        else if (PacketSerializer.TryUnwrapTcp(frame, out _, out var lowPayload) &&
                 lowPayload.Length >= 5 &&
                 WorldEventTcpPolicy.IsNonNetworkedEphemeral((WorldEventType)lowPayload[4]))
        {
            // Hard drop — do not even touch the DropOldest lane.
        }
        else
            session.LowPrioritySendChannel?.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Ownership, progress heals, and session control beat any leftover ephemeral frames.
    /// Phase A: live ownership/mission WorldEvents are not fanout; SessionProgressReset is.
    /// </summary>
    internal static bool IsHighPriorityTcpFrame(byte[] frame)
    {
        if (!PacketSerializer.TryUnwrapTcp(frame, out var id, out var payload))
            return true;

        return id switch
        {
            TcpPacketId.WorldProgressSnapshot or
                TcpPacketId.WorldStateReplay or
                TcpPacketId.WarpCommand or
                TcpPacketId.SyncSettings or
                TcpPacketId.ClientTeleportSettings or
                TcpPacketId.JoinAccepted or
                TcpPacketId.JoinRejected or
                TcpPacketId.Handshake or
                TcpPacketId.HandshakeAck or
                TcpPacketId.Disconnect or
                TcpPacketId.GameModeState or
                TcpPacketId.RosterSnapshot or
                TcpPacketId.Heartbeat or
                TcpPacketId.MarioVoiceEvent or
                TcpPacketId.MarioModelIntent or
                TcpPacketId.UdpRegister => true,
            TcpPacketId.WorldEvent => IsHighPriorityWorldEventPayload(payload),
            _ => true, // unknown control — never DropOldest
        };
    }

    private static bool IsHighPriorityWorldEventPayload(byte[] payload)
    {
        // Broadcast layout: eventId(4)+type(1)+...
        if (payload.Length < 5)
            return true;
        var type = (WorldEventType)payload[4];
        // Phase A: only session-control live WorldEvents remain on TCP. Legacy ownership
        // frames from mixed builds still beat ephemeral DropOldest.
        return WorldEventTcpPolicy.RequiresLiveTcpFanout(type) ||
               WorldEventTcpPolicy.IsSnapshotOwnership(type) ||
               WorldEventTcpPolicy.IsSnapshotMission(type);
    }

    private static Task StartSendLoop(ClientSession session, NetworkStream stream, CancellationToken ct)
    {
        // Phase 1: high-pri was unbounded and could soft-die a slow peer under ownership
        // storms. Bound + DropOldest — live WorldEvents are hints; authorities heal.
        session.SendChannel = Channel.CreateBounded<PrioritizedTcpFrame>(
            new BoundedChannelOptions(HighPrioritySendCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        // Phase A: ephemeral should never enqueue; keep a tiny DropOldest lane for
        // mixed-build leftovers so ownership high-pri cannot be starved.
        session.LowPrioritySendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(LowPrioritySendCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        return Task.Run(() => SendLoop(session, stream, ct), ct);
    }

    private static async Task SendLoop(ClientSession session, NetworkStream stream, CancellationToken ct)
    {
        var high = session.SendChannel!;
        var low = session.LowPrioritySendChannel!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame = null;

                // Prefer the latest parked progress heal over channel DropOldest leftovers.
                var latestProgress = Interlocked.Exchange(ref session.LatestProgressFrame, null);
                if (latestProgress != null)
                    frame = latestProgress;

                while (frame == null && high.Reader.TryRead(out var prioritized))
                {
                    // Skip progress wake pulses (empty body) — real heal was in LatestProgressFrame.
                    if (prioritized.Frame == null || prioritized.Frame.Length == 0)
                        continue;
                    frame = prioritized.Frame;
                    break;
                }

                if (frame == null && low.Reader.TryRead(out var lowFrame))
                    frame = lowFrame;

                if (frame == null)
                {
                    var highWait = high.Reader.WaitToReadAsync(ct).AsTask();
                    var lowWait = low.Reader.WaitToReadAsync(ct).AsTask();
                    var completed = await Task.WhenAny(highWait, lowWait).ConfigureAwait(false);
                    if (ct.IsCancellationRequested)
                        break;
                    // Drain whatever became available; prefer high on the next loop.
                    _ = await completed.ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await stream.WriteAsync(frame, ct).ConfigureAwait(false);
                }
                catch
                {
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
            session.SendChannel.Writer.TryWrite(new PrioritizedTcpFrame(finalFrame, HighPriority: true));
        session.SendChannel.Writer.TryComplete();
        session.LowPrioritySendChannel?.Writer.TryComplete();

        if (session.SendTask is { IsCompleted: false } sendTask)
        {
            try { sendTask.Wait(TimeSpan.FromMilliseconds(250)); }
            catch { /* tearing down anyway */ }
        }
    }

    private readonly record struct PrioritizedTcpFrame(byte[] Frame, bool HighPriority);

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
                Log?.Invoke($"UDP recv reset ({ex.SocketErrorCode}) — continuing relay");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
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

            MaybePromoteHost();
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
        /// <summary>How this peer relates to the server process (host identity pinning).</summary>
        public HostConnectionKind ConnectionKind { get; set; }
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
        public Channel<PrioritizedTcpFrame>? SendChannel { get; set; }
        public Channel<byte[]>? LowPrioritySendChannel { get; set; }
        /// <summary>Latest-wins WorldProgressSnapshot / WorldStateReplay; survives DropOldest.</summary>
        public byte[]? LatestProgressFrame;
        public Task? SendTask { get; set; }
    }
}
