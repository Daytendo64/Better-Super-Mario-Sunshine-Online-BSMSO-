using SMSO.Net;

namespace SMSO.Bridge;

public sealed class BridgeWorker : IDisposable
{
    private readonly DolphinBridge _bridge;
    private readonly RemoteInterpolation _interpolation = new();
    private readonly object _bufferLock = new();

    /// <summary>Reusable disconnected remote slot (shares a single Name buffer; never mutated).</summary>
    private static readonly PlayerSnapshot s_emptyRemote = new() { Name = new byte[16], Connected = 0 };

    private readonly Dictionary<byte, PlayerSnapshot> _remoteRaw = new();
    private readonly Dictionary<byte, NameTagAppearance> _remoteAppearances = new();
    private readonly MarioVoiceEvent[] _remoteMarioVoiceEvents = CommBuffer.CreateRemoteMarioVoiceEventArray();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private CommBuffer _workingBuffer = CommBuffer.CreateDefault();
    private bool _hasWorkingBuffer;
    private bool _pendingWarp;
    private byte _pendingWarpTargetSlot;
    private byte _pendingWarpCourseId;
    private byte _pendingWarpEpisodeId;
    private bool _pendingWarpIsHost;
    private bool _pendingWarpStageChange = true;
    private bool _pendingWarpToPoint;
    private float _pendingWarpPosX;
    private float _pendingWarpPosY;
    private float _pendingWarpPosZ;
    private float _pendingWarpFacingY;
    private bool _dolphinRunning;
    private bool _pendingConnectionWrite;
    private bool _loggedConnectionPending;
    private bool _pendingSyncWrite;
    private bool _pendingSyncFlags;
    private bool _pendingSyncObjects;
    private bool _pendingSyncProgress;
    private bool _remoteClearPending;
    private PlayerSnapshot[]? _cachedRemoteSnapshots;
    private byte _lastRestoredStageId;
    private byte _lastRestoredEpisodeId;
    private ushort _lastLocalMarioVoiceSequence;
    private ushort _lastLocalWorldEventSequence;
    private readonly Queue<CommWorldEvent> _pendingIncomingWorldEvents = new();
    private readonly object _incomingWorldEventLock = new();
    private GameModeStatePacket _gameModeState = GameModeStatePacket.CreateDefault();
    private ushort _lastGameModeSeq;
    private NameTagAppearance _savedLocalAppearance = NameTagAppearance.CreateDefault();
    private readonly Dictionary<byte, NameTagAppearance> _savedRemoteAppearances = new();
    private int _remoteSyncWriteFailStreak;
    private DateTime _lastRemoteSyncFailLogUtc = DateTime.MinValue;
    private ushort _rosterHudNextSequence;

    public GameModeStatePacket CurrentGameModeState => _gameModeState.Clone();

    public event Action<CommBuffer>? BufferUpdated;
    public event Action<PlayerSnapshot>? LocalSnapshotReady;
    public event Action<MarioVoiceEvent>? LocalMarioVoiceReady;
    public event Action<WorldEventRequest>? LocalWorldEventReady;
    public event Action<string>? Log;
    public event Action<DolphinLinkState>? LinkStateChanged;

    public DolphinLinkState LinkState { get; private set; } = DolphinLinkState.NotRunning;
    public string? LastDolphinLinkError => _bridge.LastResolveError;

    public BridgeWorker(DolphinBridge bridge) => _bridge = bridge;

    public void Start()
    {
        if (_loop is { IsCompleted: false })
            return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoop(_cts.Token));
    }

    public void Stop()
    {
        var loop = _loop;
        _cts?.Cancel();
        if (loop is { IsCompleted: false })
        {
            try { loop.Wait(TimeSpan.FromMilliseconds(500)); }
            catch (AggregateException) { /* cancellation during shutdown */ }
        }
        _cts = null;
        _loop = null;
    }

    public void NotifyDolphinRunning(bool running)
    {
        _dolphinRunning = running;
        if (!running)
        {
            _bridge.Detach();
            lock (_bufferLock)
            {
                _hasWorkingBuffer = false;
                _pendingWarp = false;
                _cachedRemoteSnapshots = null;
                _lastRestoredStageId = 0;
                _lastRestoredEpisodeId = 0;
                _remoteRaw.Clear();
                Array.Clear(_remoteMarioVoiceEvents, 0, _remoteMarioVoiceEvents.Length);
                _interpolation.Clear();
            }
            SetLinkState(DolphinLinkState.NotRunning);
            return;
        }

        if (LinkState == DolphinLinkState.NotRunning)
            SetLinkState(DolphinLinkState.Running);
    }

    public bool ApplyWarp(
        byte targetSlot,
        byte courseId,
        byte episodeId,
        bool isHost,
        bool stageChange = true,
        bool warpToPoint = false,
        float posX = 0f,
        float posY = 0f,
        float posZ = 0f,
        float facingY = 0f)
    {
        lock (_bufferLock)
        {
            _pendingWarp = true;
            _pendingWarpTargetSlot = targetSlot;
            _pendingWarpCourseId = courseId;
            _pendingWarpEpisodeId = episodeId;
            _pendingWarpIsHost = isHost;
            _pendingWarpStageChange = stageChange;
            _pendingWarpToPoint = warpToPoint;
            _pendingWarpPosX = posX;
            _pendingWarpPosY = posY;
            _pendingWarpPosZ = posZ;
            _pendingWarpFacingY = facingY;
        }

        return TryApplyPendingWarp();
    }

    public void SetConnected(bool connected, byte slot, string username, bool isHost)
    {
        var prefetched = _bridge.TryReadBuffer(out var live) && live.Magic == ProtocolConstants.Magic
            ? live
            : (CommBuffer?)null;

        lock (_bufferLock)
        {
            if (prefetched.HasValue)
            {
                _workingBuffer = prefetched.Value;
                _hasWorkingBuffer = true;
            }
            else
            {
                EnsureWorkingBuffer();
            }

            if (connected)
            {
                _workingBuffer.BridgeFlags |= BridgeFlags.Connected;
                _workingBuffer.WorldSync.LastAppliedEventId = 0;
                _workingBuffer.WorldSync.Incoming = default;
                _workingBuffer.WorldSync.LocalPending = default;
                _lastLocalWorldEventSequence = 0;
            }
            else
            {
                _workingBuffer.BridgeFlags &= ~BridgeFlags.Connected;
                _rosterHudNextSequence = 0;
                _workingBuffer.RosterHud = CommRosterHudSync.CreateDefault();
            }

            if (isHost)
                _workingBuffer.BridgeFlags |= BridgeFlags.Host;
            else
                _workingBuffer.BridgeFlags &= ~BridgeFlags.Host;

            _workingBuffer.LocalSlot = slot;
            _workingBuffer.SetLocalPlayerName(username);
            _workingBuffer.Magic = ProtocolConstants.Magic;
            _workingBuffer.Version = ProtocolConstants.CommVersion;
        }

        if (!TryWriteWorkingBuffer())
        {
            _pendingConnectionWrite = connected;
            if (!_loggedConnectionPending)
            {
                _loggedConnectionPending = true;
                Log?.Invoke("Dolphin not linked — connection flags queued until attach");
            }
        }
        else
        {
            _pendingConnectionWrite = false;
            _loggedConnectionPending = false;
        }
    }

    /// <summary>Store latest raw network sample; smoothing runs on bridge poll.</summary>
    public void PushRemoteSnapshot(byte slot, in PlayerSnapshot snap, in NameTagAppearance appearance)
    {
        _interpolation.PushPacket(slot, snap);

        lock (_bufferLock)
        {
            _remoteRaw[slot] = snap;
            _remoteAppearances[slot] = appearance;
        }

        // Push smoothed state to Dolphin immediately instead of waiting for the next poll tick.
        FlushInterpolatedRemotes(force: true);
    }

    public void RemoveRemoteSnapshot(byte slot)
    {
        lock (_bufferLock)
        {
            _remoteRaw.Remove(slot);
            _remoteAppearances.Remove(slot);
            if (slot < _remoteMarioVoiceEvents.Length)
                _remoteMarioVoiceEvents[slot] = default;
        }

        _interpolation.Remove(slot);
        _remoteClearPending = true;
        FlushInterpolatedRemotes(force: true);
    }

    /// <summary>Push roster join/leave to the in-game HUD as soon as TCP roster updates arrive.</summary>
    public void EnqueueRosterHudEvent(RosterHudEventKind kind, byte slot, string username)
    {
        if (kind == RosterHudEventKind.None)
            return;

        lock (_bufferLock)
        {
            if (!EnsureWorkingBuffer())
                return;

            _rosterHudNextSequence++;
            var idx = (ushort)((_rosterHudNextSequence - 1) % ProtocolConstants.CommRosterHudRingSlots);
            var ev = new CommRosterHudEvent
            {
                Sequence = _rosterHudNextSequence,
                Kind = kind,
                Slot = slot,
                Name = new byte[16],
            };
            ev.SetPlayerName(username);
            _workingBuffer.RosterHud.Events ??= CommRosterHudSync.CreateDefault().Events;
            _workingBuffer.RosterHud.Events[idx] = ev;
            _workingBuffer.RosterHud.LatestSequence = _rosterHudNextSequence;
        }

        TryWriteWorkingBuffer();
    }

    public void PushRemoteMarioVoiceEvent(byte slot, in MarioVoiceEvent voiceEvent)
    {
        if (slot >= ProtocolConstants.MaxRemoteSlots || voiceEvent.IsEmpty)
            return;

        lock (_bufferLock)
        {
            if (!_remoteRaw.ContainsKey(slot))
                return;

            _remoteMarioVoiceEvents[slot] = voiceEvent;
            if (_hasWorkingBuffer && _workingBuffer.RemoteMarioVoiceEvents != null)
                _workingBuffer.RemoteMarioVoiceEvents[slot] = voiceEvent;
        }
    }

    public void FlushRemoteSnapshotsToDolphin() => FlushInterpolatedRemotes(force: true);

    public void ApplyLocalNameTagAppearance(string username, in NameTagAppearance appearance)
    {
        var prefetched = _bridge.TryReadBuffer(out var live) && live.Magic == ProtocolConstants.Magic
            ? live
            : (CommBuffer?)null;

        lock (_bufferLock)
        {
            if (prefetched.HasValue)
            {
                _workingBuffer = prefetched.Value;
                _hasWorkingBuffer = true;
            }
            else if (!EnsureWorkingBuffer())
            {
                return;
            }

            NameTagColorCodec.WritePureName(_workingBuffer.LocalSnapshot.Name, username);
            _workingBuffer.LocalNameTagAppearance = appearance;
        }

        TryWriteWorkingBuffer();
    }

    public void ApplyGameModeState(byte localSlot, in GameModeStatePacket packet)
    {
        var wasHideSeek = false;
        lock (_bufferLock)
        {
            if (_lastGameModeSeq != 0 && packet.Seq <= _lastGameModeSeq)
                return;

            wasHideSeek = _gameModeState.GameMode == GameMode.HideSeek;
            _lastGameModeSeq = packet.Seq;
            _gameModeState = packet.Clone();

            EnsureWorkingBuffer();

            if (_gameModeState.GameMode == GameMode.HideSeek)
            {
                if (!wasHideSeek)
                {
                    _savedLocalAppearance = _workingBuffer.LocalNameTagAppearance;
                    _savedRemoteAppearances.Clear();
                    foreach (var kvp in _remoteAppearances)
                        _savedRemoteAppearances[kvp.Key] = kvp.Value;
                }

                ApplyHideSeekNameTagColors(localSlot);
            }
            else if (wasHideSeek)
            {
                RestoreSavedNameTagColors();
            }

            _workingBuffer.GameModeState = GameModeStatePacket.ToCommGameMode(localSlot, _gameModeState);
        }

        if (_gameModeState.GameMode == GameMode.HideSeek)
            FlushInterpolatedRemotes(force: true);
        else
            TryWriteWorkingBuffer();
    }

    public void ForceResetGameModeToNormal(byte localSlot)
    {
        lock (_bufferLock)
        {
            var wasHideSeek = _gameModeState.GameMode == GameMode.HideSeek;
            _gameModeState = GameModeStatePacket.CreateDefault();
            _lastGameModeSeq = 0;

            EnsureWorkingBuffer();

            if (wasHideSeek)
                RestoreSavedNameTagColors();

            _workingBuffer.GameModeState = GameModeStatePacket.ToCommGameMode(localSlot, _gameModeState);
        }

        TryWriteWorkingBuffer();
    }

    private void ApplyHideSeekNameTagColors(byte localSlot)
    {
        var localRole = (HideSeekRole)_gameModeState.GetRole(localSlot);
        _workingBuffer.LocalNameTagAppearance = GameModeStatePacket.HideSeekAppearance(localRole);

        for (byte slot = 0; slot < ProtocolConstants.StableMaxPlayers; slot++)
        {
            if (slot == localSlot)
                continue;

            var role = (HideSeekRole)_gameModeState.GetRole(slot);
            var appearance = GameModeStatePacket.HideSeekAppearance(role);
            _remoteAppearances[slot] = appearance;
            if (slot < _workingBuffer.RemoteNameTagAppearances.Length)
                _workingBuffer.RemoteNameTagAppearances[slot] = appearance;
        }
    }

    private void RestoreSavedNameTagColors()
    {
        _workingBuffer.LocalNameTagAppearance = _savedLocalAppearance;
        foreach (var kvp in _savedRemoteAppearances)
        {
            _remoteAppearances[kvp.Key] = kvp.Value;
            if (kvp.Key < _workingBuffer.RemoteNameTagAppearances.Length)
                _workingBuffer.RemoteNameTagAppearances[kvp.Key] = kvp.Value;
        }
        _savedRemoteAppearances.Clear();
    }

    public void ClearRemoteSnapshots()
    {
        lock (_bufferLock)
        {
            _remoteRaw.Clear();
            _remoteAppearances.Clear();
            Array.Clear(_remoteMarioVoiceEvents, 0, _remoteMarioVoiceEvents.Length);
        }

        _interpolation.Clear();
        _remoteClearPending = true;
        FlushInterpolatedRemotes(force: true);
    }

    /// <summary>Legacy entry: push sample then flush (used when only one update is needed).</summary>
    public void WriteRemoteSnapshots(IReadOnlyDictionary<byte, PlayerSnapshot> remotes)
    {
        foreach (var kv in remotes)
            PushRemoteSnapshot(kv.Key, kv.Value,
                NameTagColorCodec.TryDecodeAppearance(kv.Value.Name, out var appearance)
                    ? appearance
                    : NameTagAppearance.CreateDefault());

        FlushInterpolatedRemotes();
    }

    private void FlushInterpolatedRemotes(bool force = false, CommBuffer? prefetchedLive = null)
    {
        if (!_bridge.IsAttached)
            return;

        CommBuffer? liveBuffer = prefetchedLive;
        if (!liveBuffer.HasValue && _bridge.TryReadBuffer(out var live) && live.Magic == ProtocolConstants.Magic)
            liveBuffer = live;

        PlayerSnapshot[] remoteCopy;
        NameTagAppearance localAppearance;
        NameTagAppearance[] remoteAppearances;
        MarioVoiceEvent[] remoteVoiceEvents;
        CommGameModeState commGameMode;
        lock (_bufferLock)
        {
            if (!force && _remoteRaw.Count == 0 && _gameModeState.GameMode != GameMode.HideSeek &&
                !_remoteClearPending)
                return;

            if (liveBuffer.HasValue)
            {
                _workingBuffer = liveBuffer.Value;
                _hasWorkingBuffer = true;
            }
            else if (!EnsureWorkingBuffer())
            {
                return;
            }

            // Iterate remote slots by index (0..MaxRemoteSlots-1) instead of clearing the whole
            // array + OrderBy every tick. Reuse a single empty snapshot (shared Name buffer) so
            // we don't allocate 9 PlayerSnapshots + 9 byte[16] every 60 Hz flush.
            var remoteSnapshots = _workingBuffer.RemoteSnapshots;
            var remoteAppearancesBuf = _workingBuffer.RemoteNameTagAppearances;
            for (int slot = 0; slot < remoteSnapshots.Length; slot++)
            {
                if (slot == _workingBuffer.LocalSlot)
                {
                    remoteSnapshots[slot] = s_emptyRemote;
                    if (slot < remoteAppearancesBuf.Length)
                        remoteAppearancesBuf[slot] = default;
                    continue;
                }

                if (_remoteRaw.TryGetValue((byte)slot, out _))
                {
                    PlayerSnapshot snap;
                    if (_interpolation.HasSlot((byte)slot))
                        snap = _interpolation.Advance((byte)slot);
                    else
                        snap = _remoteRaw[(byte)slot];

                    snap.Connected = 1;
                    snap.Slot = (byte)slot;
                    remoteSnapshots[slot] = snap;
                    if (slot < remoteAppearancesBuf.Length &&
                        _remoteAppearances.TryGetValue((byte)slot, out var appearance))
                    {
                        remoteAppearancesBuf[slot] = appearance;
                    }
                }
                else
                {
                    remoteSnapshots[slot] = s_emptyRemote;
                    if (slot < remoteAppearancesBuf.Length)
                        remoteAppearancesBuf[slot] = default;
                }
            }

            if (_gameModeState.GameMode == GameMode.HideSeek)
                ApplyHideSeekNameTagColors(_workingBuffer.LocalSlot);

            _workingBuffer.GameModeState =
                GameModeStatePacket.ToCommGameMode(_workingBuffer.LocalSlot, _gameModeState);

            commGameMode = _workingBuffer.GameModeState;
            remoteCopy = _cachedRemoteSnapshots ??= CommBuffer.CreateRemoteArray();
            Array.Copy(_workingBuffer.RemoteSnapshots, remoteCopy, remoteCopy.Length);
            localAppearance = _workingBuffer.LocalNameTagAppearance;
            remoteAppearances = _workingBuffer.RemoteNameTagAppearances
                ?? CommBuffer.CreateRemoteAppearanceArray();
            remoteVoiceEvents = _remoteMarioVoiceEvents;
        }

        // Only skip during boot — Loading/Warping skips left remotes starved when blocked
        // loading zones wedged mGameState in WARPING during otherwise normal play.
        if (liveBuffer.HasValue && liveBuffer.Value.DolphinState is DolphinState.Booting)
            return;

        if (!_bridge.TryWriteRemoteSyncPayload(
                remoteCopy,
                localAppearance,
                remoteAppearances,
                remoteVoiceEvents,
                commGameMode))
        {
            _remoteSyncWriteFailStreak++;
            var now = DateTime.UtcNow;
            if (now - _lastRemoteSyncFailLogUtc >= TimeSpan.FromSeconds(2))
            {
                var suffix = _remoteSyncWriteFailStreak > 1
                    ? $" ({_remoteSyncWriteFailStreak} consecutive failures)"
                    : string.Empty;
                Log?.Invoke($"Failed to write remote sync payload to Dolphin{suffix}");
                _lastRemoteSyncFailLogUtc = now;
                _remoteSyncWriteFailStreak = 0;
            }

            return;
        }

        _remoteSyncWriteFailStreak = 0;
        _remoteClearPending = false;
    }

    private void MaybeRestoreRemoteSnapshotsAfterStageChange(CommBuffer buffer)
    {
        var stageId = buffer.LocalSnapshot.StageId;
        var episodeId = buffer.LocalSnapshot.EpisodeId;
        if (stageId == _lastRestoredStageId && episodeId == _lastRestoredEpisodeId)
            return;

        _lastRestoredStageId = stageId;
        _lastRestoredEpisodeId = episodeId;
        FlushInterpolatedRemotes();
    }

    public void ApplySyncSettings(bool syncFlags, bool syncObjects, bool syncProgress)
    {
        lock (_bufferLock)
        {
            if (!EnsureWorkingBuffer()) return;
            ApplySyncFlagsToWorkingBuffer(syncFlags, syncObjects, syncProgress);
        }

        if (TryWriteWorkingBuffer())
        {
            Log?.Invoke($"Bridge sync flags applied: flags={syncFlags} objects={syncObjects} progress={syncProgress}");
            _pendingSyncWrite = false;
        }
        else
        {
            // Dolphin not linked yet — replay on link restore (same pattern as connection flags).
            _pendingSyncWrite = true;
            _pendingSyncFlags = syncFlags;
            _pendingSyncObjects = syncObjects;
            _pendingSyncProgress = syncProgress;
        }
    }

    private void ApplySyncFlagsToWorkingBuffer(bool syncFlags, bool syncObjects, bool syncProgress)
    {
        if (syncFlags)
        {
            _workingBuffer.BridgeFlags |= BridgeFlags.SyncShine | BridgeFlags.SyncBlueCoin |
                                          BridgeFlags.SyncEvent | BridgeFlags.SyncStory |
                                          BridgeFlags.SyncMission | BridgeFlags.SyncSecret;
        }
        else
        {
            _workingBuffer.BridgeFlags &= ~(BridgeFlags.SyncShine | BridgeFlags.SyncBlueCoin |
                                            BridgeFlags.SyncEvent | BridgeFlags.SyncStory |
                                            BridgeFlags.SyncMission | BridgeFlags.SyncSecret);
        }

        if (syncObjects) _workingBuffer.BridgeFlags |= BridgeFlags.SyncObjects;
        else _workingBuffer.BridgeFlags &= ~BridgeFlags.SyncObjects;
        if (syncProgress) _workingBuffer.BridgeFlags |= BridgeFlags.SyncProgress;
        else _workingBuffer.BridgeFlags &= ~BridgeFlags.SyncProgress;
    }

    private async Task PollLoop(CancellationToken ct)
    {
        // PeriodicTimer is backed by Stopwatch (QueryPerformanceCounter) and ticks on a steady
        // cadence independent of the ~15.6 ms default system timer. Task.Delay(16) instead
        // quantizes to the system tick, making bridge flushes irregular and remote motion choppy.
        PeriodicTimer? timer = null;
        int timerPeriodMs = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var desiredPeriodMs = _dolphinRunning ? ProtocolConstants.BridgePollMs : 250;
                if (timer == null || timerPeriodMs != desiredPeriodMs)
                {
                    timer?.Dispose();
                    timerPeriodMs = desiredPeriodMs;
                    timer = new PeriodicTimer(TimeSpan.FromMilliseconds(desiredPeriodMs));
                }

                try
                {
                    if (_dolphinRunning)
                    {
                        if (!_bridge.IsAttached)
                            _bridge.TryAttach();

                        CommBuffer? liveBuffer = null;
                        if (_bridge.TryReadBuffer(out var buffer) && buffer.Magic == ProtocolConstants.Magic)
                        {
                            liveBuffer = buffer;
                            lock (_bufferLock)
                            {
                                _workingBuffer = buffer;
                                _hasWorkingBuffer = true;
                            }

                            BufferUpdated?.Invoke(buffer);
                            if (buffer.LocalSnapshot.Connected != 0 || (buffer.BridgeFlags & BridgeFlags.Connected) != 0)
                                LocalSnapshotReady?.Invoke(buffer.LocalSnapshot);
                            MaybePublishLocalMarioVoice(buffer.LocalMarioVoiceEvent);
                            MaybePublishLocalWorldEvent(buffer.WorldSync.LocalPending);
                            DrainPendingIncomingWorldEvents(buffer);

                            MaybeRestoreRemoteSnapshotsAfterStageChange(buffer);
                        }

                        UpdateLinkStateFromBridge(liveBuffer);
                        FlushInterpolatedRemotes(prefetchedLive: liveBuffer);
                        TryApplyPendingWarp();
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Bridge poll error: {ex.Message}");
                }

                await timer.WaitForNextTickAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Bridge worker stopped: {ex.Message}");
        }
        finally
        {
            timer?.Dispose();
        }
    }

    private void UpdateLinkStateFromBridge(CommBuffer? liveBuffer = null)
    {
        if (!_dolphinRunning)
        {
            SetLinkState(DolphinLinkState.NotRunning);
            return;
        }

        if (!_bridge.IsAttached)
        {
            if (LinkState is DolphinLinkState.Attached or DolphinLinkState.ModuleReady)
                SetLinkState(DolphinLinkState.Running);
            return;
        }

        if (!_bridge.HasResolvedMailbox)
        {
            _bridge.TryResolveMailboxAddress();
            if (!_bridge.HasResolvedMailbox)
            {
                SetLinkState(DolphinLinkState.Attached);
                return;
            }
        }

        if (liveBuffer.HasValue && liveBuffer.Value.Magic == ProtocolConstants.Magic &&
            liveBuffer.Value.Version == ProtocolConstants.CommVersion)
            SetLinkState(DolphinLinkState.ModuleReady);
        else
            SetLinkState(DolphinLinkState.Attached);

        TryFlushPendingConnectionWrite();
        TryFlushPendingSyncWrite();
    }

    private void TryFlushPendingConnectionWrite()
    {
        if (!_pendingConnectionWrite)
            return;

        if (LinkState != DolphinLinkState.ModuleReady)
            return;

        if (!TryWriteWorkingBuffer())
            return;

        _pendingConnectionWrite = false;
        _loggedConnectionPending = false;
        Log?.Invoke("Dolphin link restored — applied queued connection flags");
    }

    private void TryFlushPendingSyncWrite()
    {
        if (!_pendingSyncWrite)
            return;

        if (LinkState != DolphinLinkState.ModuleReady)
            return;

        lock (_bufferLock)
        {
            if (!EnsureWorkingBuffer())
                return;
            ApplySyncFlagsToWorkingBuffer(_pendingSyncFlags, _pendingSyncObjects, _pendingSyncProgress);
        }

        if (!TryWriteWorkingBuffer())
            return;

        _pendingSyncWrite = false;
        Log?.Invoke($"Dolphin link restored — applied queued sync flags={_pendingSyncFlags} objects={_pendingSyncObjects} progress={_pendingSyncProgress}");
    }

    private bool TryApplyPendingWarp()
    {
        byte targetSlot, courseId, episodeId;
        bool isHost;
        bool stageChange;
        bool warpToPoint;
        float posX, posY, posZ, facingY;
        lock (_bufferLock)
        {
            if (!_pendingWarp) return false;
            targetSlot = _pendingWarpTargetSlot;
            courseId = _pendingWarpCourseId;
            episodeId = _pendingWarpEpisodeId;
            isHost = _pendingWarpIsHost;
            stageChange = _pendingWarpStageChange;
            warpToPoint = _pendingWarpToPoint;
            posX = _pendingWarpPosX;
            posY = _pendingWarpPosY;
            posZ = _pendingWarpPosZ;
            facingY = _pendingWarpFacingY;
        }

        if (!_bridge.IsAttached)
            return false;

        if (!_bridge.TryApplyWarpIntent(
                targetSlot,
                courseId,
                episodeId,
                isHost,
                setWarpPending: stageChange,
                setWarpAll: targetSlot == ProtocolConstants.WarpAllSlots,
                setWarpToPoint: warpToPoint,
                warpPosX: posX,
                warpPosY: posY,
                warpPosZ: posZ,
                warpFacingY: facingY))
        {
            Log?.Invoke($"Warp failed: {_bridge.LastResolveError ?? "Dolphin mailbox not ready"}");
            return false;
        }

        lock (_bufferLock)
        {
            _pendingWarp = false;
        }

        Log?.Invoke($"Warp sent to Dolphin: course={courseId} episode={episodeId}");
        return true;
    }

    private bool EnsureWorkingBuffer()
    {
        if (_hasWorkingBuffer)
            return true;

        _workingBuffer = CommBuffer.CreateDefault();
        _workingBuffer.Magic = ProtocolConstants.Magic;
        _workingBuffer.Version = ProtocolConstants.CommVersion;
        _hasWorkingBuffer = true;
        return true;
    }

    private void MaybePublishLocalMarioVoice(MarioVoiceEvent voiceEvent)
    {
        if (voiceEvent.IsEmpty || voiceEvent.Sequence == _lastLocalMarioVoiceSequence)
            return;

        _lastLocalMarioVoiceSequence = voiceEvent.Sequence;
        LocalMarioVoiceReady?.Invoke(voiceEvent);
    }

    private void MaybePublishLocalWorldEvent(CommWorldEvent worldEvent)
    {
        if (worldEvent.IsEmpty || worldEvent.Sequence == _lastLocalWorldEventSequence)
            return;

        _lastLocalWorldEventSequence = worldEvent.Sequence;
        LocalWorldEventReady?.Invoke(new WorldEventRequest(
            worldEvent.Sequence,
            worldEvent.Type,
            worldEvent.CourseId,
            worldEvent.EpisodeId,
            worldEvent.Payload0,
            worldEvent.Reserved,
            worldEvent.Payload1));

        // Hand the slot back to the module. The module only writes the next queued event once
        // this slot is empty, so clearing it after publishing is what lets outbound events
        // advance one per frame without overwriting each other.
        _bridge.TryClearLocalPendingWorldEvent();
    }

    private void DrainPendingIncomingWorldEvents(CommBuffer liveBuffer)
    {
        CommWorldEvent next;
        lock (_incomingWorldEventLock)
        {
            if (_pendingIncomingWorldEvents.Count == 0)
                return;
            // The module zeroes the incoming slot (EventId/Type) once it has processed the
            // event. Only stage the next queued remote event when the slot is free, otherwise
            // we would overwrite an unprocessed event and lose it.
            if (liveBuffer.WorldSync.Incoming.EventId != 0)
                return;
            next = _pendingIncomingWorldEvents.Dequeue();
        }

        _bridge.TryWriteIncomingWorldEventOnly(next);
    }

    public void PushIncomingWorldEvent(in WorldEventPacket packet)
    {
        lock (_incomingWorldEventLock)
            _pendingIncomingWorldEvents.Enqueue(packet.ToIncomingEvent());
    }

    public bool TryGetLastAppliedEventId(out uint lastAppliedEventId)
    {
        lock (_bufferLock)
        {
            if (_bridge.IsAttached && _bridge.TryReadBuffer(out var buffer) && buffer.Magic == ProtocolConstants.Magic)
            {
                _workingBuffer = buffer;
                _hasWorkingBuffer = true;
            }

            lastAppliedEventId = _workingBuffer.WorldSync.LastAppliedEventId;
            return _hasWorkingBuffer;
        }
    }

    private bool TryWriteWorkingBuffer()
    {
        CommBuffer copy;
        lock (_bufferLock)
            copy = _workingBuffer;

        if (!_bridge.IsAttached)
            return false;

        if (!_bridge.TryWriteBuffer(copy))
            return false;

        return true;
    }

    private void SetLinkState(DolphinLinkState state)
    {
        if (LinkState == state)
            return;

        LinkState = state;
        try
        {
            LinkStateChanged?.Invoke(state);
        }
        catch
        {
            // UI subscribers must not stop the bridge worker.
        }

        if (state == DolphinLinkState.ModuleReady)
            TryFlushPendingConnectionWrite();
    }

    public void Dispose() => Stop();
}
