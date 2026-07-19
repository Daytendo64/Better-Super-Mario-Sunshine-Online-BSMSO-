using SMSO.Net;
using SMSO.Net.MarioPack;

namespace SMSO.Bridge;

public sealed class BridgeWorker : IDisposable
{
    private readonly DolphinBridge _bridge;
    private readonly RemoteInterpolation _interpolation = new();
    private readonly object _bufferLock = new();
    private readonly object _modelIdWriteLock = new();

    /// <summary>Reusable disconnected remote slot (shares a single Name buffer; never mutated).</summary>
    private static readonly PlayerSnapshot s_emptyRemote = new() { Name = new byte[16], Connected = 0 };

    private readonly Dictionary<byte, PlayerSnapshot> _remoteRaw = new();
    private readonly Dictionary<byte, NameTagAppearance> _remoteAppearances = new();
    /// <summary>Last known full display name per remote (never the 5-char overlay truncate).</summary>
    private readonly Dictionary<byte, string> _remotePureNames = new();
    private readonly Dictionary<byte, byte[]> _remoteMarioModelIds = new();
    private readonly MarioVoiceEvent[] _remoteMarioVoiceEvents = CommBuffer.CreateRemoteMarioVoiceEventArray();
    private byte[] _localMarioModelId = new byte[ProtocolConstants.MarioModelIdSize];
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
    private bool _sessionConnected;
    private bool _loggedConnectionPending;
    private bool _pendingSyncWrite;
    private bool _pendingSyncFlags;
    private bool _pendingSyncObjects;
    private bool _pendingSyncProgress;
    private bool _remoteClearPending;
    /// <summary>
    /// Module <c>stageExit</c>/<c>clearPuppets</c> zeroed remoteSnapshots while the bridge still
    /// holds live network remotes. Do not resurrect Connected remotes into the mailbox until the
    /// next stage is ready — republishing mid-teardown races freed remote bodies / TexAnim.
    /// </summary>
    private bool _holdRemotePublishForStageExit;
    /// <summary>
    /// Polls spent in Active+Connected while hold is armed. After stageExit / stage-id
    /// flips, Dolphin often reports Active before the module has finished settling the
    /// new stage (clearPuppets, heap remount). Wait this many Active adopts before
    /// republishing Connected remotes — ~500 ms at 60 Hz bridge poll.
    /// </summary>
    private int _holdRemotePublishActivePolls;
    private const int HoldRemotePublishActiveGracePolls = 32;
    /// <summary>
    /// True after any live mailbox adopt showed Connected remotes. Distinguishes stageExit
    /// clears (had remotes → none) from stale empty CreateDefault adopts on first link.
    /// </summary>
    private bool _liveSawConnectedRemotes;
    private PlayerSnapshot[]? _cachedRemoteSnapshots;
    private byte _lastRestoredStageId;
    private byte _lastRestoredEpisodeId;
    private ushort _lastLocalMarioVoiceSequence;
    private ushort _lastLocalWorldEventSequence;
    private readonly Queue<CommWorldEvent> _pendingIncomingWorldEvents = new();
    private readonly object _incomingWorldEventLock = new();

    /// <summary>
    /// Shine / blue / story ownership must beat episode-scoped visual events in the
    /// single-slot mailbox so FlagManager + HUD update live under load.
    /// </summary>
    private static bool IsLiveOwnershipWorldEvent(WorldEventType type) =>
        type is WorldEventType.ShineCollected
            or WorldEventType.BlueCoinCollected
            or WorldEventType.StoryFlag
            or WorldEventType.SecretComplete
            or WorldEventType.TriggerFlag;

    /// <summary>
    /// Same-stage mission progress that must jump ahead of graffiti / fruit spam so
    /// co-op partners see hides live (not after a level reload / 45s resync).
    /// </summary>
    private static bool IsPriorityMissionWorldEvent(WorldEventType type) =>
        IsLiveOwnershipWorldEvent(type) ||
        type is WorldEventType.RedCoinCollected
            or WorldEventType.NpcCleaned;
    private GameModeStatePacket _gameModeState = GameModeStatePacket.CreateDefault();
    private ushort _lastGameModeSeq;
    private NameTagAppearance _savedLocalAppearance = NameTagAppearance.CreateDefault();
    private readonly Dictionary<byte, NameTagAppearance> _savedRemoteAppearances = new();
    private int _remoteSyncWriteFailStreak;
    private DateTime _lastRemoteSyncFailLogUtc = DateTime.MinValue;
    private readonly byte[] _remoteModelIdScratch =
        new byte[ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots];
    private readonly byte[] _modelIdsWriteLocal = new byte[ProtocolConstants.MarioModelIdSize];
    private readonly byte[] _modelIdsWriteRemotes =
        new byte[ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots];
    private readonly byte[] _lastWrittenLocalModelId = new byte[ProtocolConstants.MarioModelIdSize];
    private readonly byte[] _lastWrittenRemoteModelIds =
        new byte[ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots];
    private bool _hasLastWrittenModelIds;
    private int _modelIdVersion;
    private int _modelIdScratchVersion = -1;
    private int _modelIdScratchBuildCount;
    private int _lastWrittenModelIdVersion = -1;
    private int _lastObservedBridgeWriteCacheEpoch = -1;
    private bool _moduleReadySeen;
    private string _appliedLocalModelId = string.Empty;
    private string _appliedLocalNameTagName = string.Empty;
    private NameTagAppearance _appliedLocalNameTagAppearance;
    private bool _hasAppliedLocalNameTag;
    private ushort _rosterHudNextSequence;
    private string? _commVersionMismatchError;

    public GameModeStatePacket CurrentGameModeState => _gameModeState.Clone();

    public event Action<CommBuffer>? BufferUpdated;
    public event Action<PlayerSnapshot>? LocalSnapshotReady;
    /// <summary>Module set BF_REQUEST_PROGRESS (co-op same-stage death reload).</summary>
    public event Action? ModuleProgressResyncRequested;
    public event Action<MarioVoiceEvent>? LocalMarioVoiceReady;
    public event Action<WorldEventRequest>? LocalWorldEventReady;
    public event Action<string>? Log;
    public event Action<DolphinLinkState>? LinkStateChanged;

    public DolphinLinkState LinkState { get; private set; } = DolphinLinkState.NotRunning;
    public string? LastDolphinLinkError => _commVersionMismatchError ?? _bridge.LastResolveError;

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
                _holdRemotePublishForStageExit = false;
                _holdRemotePublishActivePolls = 0;
                _liveSawConnectedRemotes = false;
                _remoteRaw.Clear();
                Array.Clear(_remoteMarioVoiceEvents, 0, _remoteMarioVoiceEvents.Length);
                _interpolation.Clear();
                _hasLastWrittenModelIds = false;
                _moduleReadySeen = false;
                _hasAppliedLocalNameTag = false;
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
            // Use launcher-session state, not a possibly stale mailbox bit. If the prior
            // disconnect write missed Dolphin, the live buffer can still say Connected;
            // treating that as the same session preserves stale sequence/incoming state.
            var wasConnected = _sessionConnected;

            // Mark connected before adopting the live mailbox so bridge-authored remote
            // snapshots/appearances are preserved. Adopting first left a client-only write
            // that pushed Dolphin's stale nametag sidecars until the next interpolated flush.
            if (connected)
                _sessionConnected = true;

            if (prefetched.HasValue)
                AdoptLiveBufferPreservingBridgeState_NoLock(prefetched.Value);
            else
                EnsureWorkingBuffer();

            if (connected)
            {
                _workingBuffer.BridgeFlags |= BridgeFlags.Connected;
                // Only clear world-sync mailbox state on a fresh connect transition.
                // Re-asserting Connected (FlushSnapshotsAfterConnect after JoinAccepted, or
                // Dolphin relink) must not wipe an in-flight Incoming / LastAppliedEventId
                // mid join-replay.
                if (!wasConnected)
                {
                    _workingBuffer.WorldSync.LastAppliedEventId = 0;
                    _workingBuffer.WorldSync.Incoming = default;
                    _workingBuffer.WorldSync.LocalPending = default;
                    _lastLocalWorldEventSequence = 0;
                }
            }
            else
            {
                _sessionConnected = false;
                _holdRemotePublishForStageExit = false;
                _holdRemotePublishActivePolls = 0;
                _liveSawConnectedRemotes = false;
                _workingBuffer.BridgeFlags &= ~BridgeFlags.Connected;
                _rosterHudNextSequence = 0;
                _workingBuffer.RosterHud = CommRosterHudSync.CreateDefault();
                // Drop any staged/in-flight world event so a disconnect mid-replay cannot
                // leave a collectible apply sitting in the mailbox for the next session.
                _workingBuffer.WorldSync.Incoming = default;
                _workingBuffer.WorldSync.LocalPending = default;
                _workingBuffer.WorldSync.LastAppliedEventId = 0;
                _lastLocalWorldEventSequence = 0;

                // Clear game-mode seq so a rehost's Seq=1 is not rejected against a stale high seq
                // left by ResetHideSeekIfActiveOnServer applying Normal before teardown.
                var wasHideSeek = _gameModeState.GameMode == GameMode.HideSeek;
                _gameModeState = GameModeStatePacket.CreateDefault();
                _lastGameModeSeq = 0;
                if (wasHideSeek)
                    RestoreSavedNameTagColors();
                _workingBuffer.GameModeState = GameModeStatePacket.ToCommGameMode(slot, _gameModeState);
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

    /// <summary>Store latest raw network sample; smoothing runs on bridge poll (~60 Hz).</summary>
    public void PushRemoteSnapshot(byte slot, in PlayerSnapshot snap, in NameTagAppearance appearance)
    {
        if (slot >= ProtocolConstants.MaxRemoteSlots)
            return;

        // Deep-copy Name so later UDP receives cannot mutate this stored snapshot
        // (and so interpolation's LastRaw.Name is not aliased to a live decode buffer).
        var owned = CloneSnapshotName(snap);
        RememberPureRemoteName(slot, owned);

        _interpolation.PushPacket(slot, owned);

        lock (_bufferLock)
        {
            _remoteRaw[slot] = owned;
            _remoteAppearances[slot] = appearance;
        }

        // Do NOT force-flush here — every UDP packet used to WriteProcessMemory (~540+/sec
        // at 9 remotes × 60 Hz). The poll loop flushes interpolated remotes once per tick.
    }

    private static PlayerSnapshot CloneSnapshotName(in PlayerSnapshot snap)
    {
        var owned = snap;
        if (snap.Name == null)
        {
            owned.Name = new byte[16];
            return owned;
        }

        owned.Name = new byte[16];
        var copyLen = Math.Min(16, snap.Name.Length);
        Buffer.BlockCopy(snap.Name, 0, owned.Name, 0, copyLen);
        return owned;
    }

    private void RememberPureRemoteName(byte slot, in PlayerSnapshot snap)
    {
        if (snap.Name == null || snap.Name.Length == 0)
            return;

        // Only store when the buffer is already a pure display name. Overlay-packed
        // wire bytes irrevocably truncate gradient names to 5 chars ("Playe").
        if (snap.Name.Length >= 16 && NameTagColorCodec.HasAppearanceMarker(snap.Name[15]))
            return;

        var pure = snap.GetName();
        if (!string.IsNullOrWhiteSpace(pure))
            _remotePureNames[slot] = pure;
    }

    private void EnsurePureNameForDolphin(ref PlayerSnapshot snap, byte slot)
    {
        var pure = _remotePureNames.TryGetValue(slot, out var remembered) &&
                   !string.IsNullOrWhiteSpace(remembered)
            ? remembered
            : null;

        if (pure == null &&
            snap.Name != null &&
            snap.Name.Length >= 16 &&
            !NameTagColorCodec.HasAppearanceMarker(snap.Name[15]))
        {
            pure = snap.GetName();
        }

        // Always install a fresh owned buffer so flush never mutates interpolation storage
        // and never writes legacy overlay markers into Dolphin.
        var name = new byte[16];
        if (!string.IsNullOrWhiteSpace(pure))
            NameTagColorCodec.WritePureName(name, pure);
        else if (snap.Name != null &&
                 snap.Name.Length >= 16 &&
                 NameTagColorCodec.HasAppearanceMarker(snap.Name[15]))
        {
            // Last resort: do not bake GetPureName() ("Playe") — keep prior name if any.
            if (_remotePureNames.TryGetValue(slot, out var fallback) &&
                !string.IsNullOrWhiteSpace(fallback))
                NameTagColorCodec.WritePureName(name, fallback);
        }
        else if (snap.Name != null)
        {
            var copyLen = Math.Min(16, snap.Name.Length);
            Buffer.BlockCopy(snap.Name, 0, name, 0, copyLen);
        }

        snap.Name = name;
    }

    public void SetRemoteMarioModelId(byte slot, string? modelId)
    {
        if (slot >= ProtocolConstants.MaxRemoteSlots)
            return;

        var encoded = CharacterPack.EncodeModelId(modelId);
        var changed = false;
        lock (_bufferLock)
        {
            if (_remoteMarioModelIds.TryGetValue(slot, out var existing) &&
                existing.AsSpan().SequenceEqual(encoded))
            {
                // Unchanged — still ensure mailbox has been written at least once.
                if (_hasLastWrittenModelIds)
                    return;
            }
            else
            {
                _remoteMarioModelIds[slot] = encoded;
                changed = true;
                unchecked
                {
                    ++_modelIdVersion;
                }
                if (_hasWorkingBuffer && _workingBuffer.RemoteMarioModelIds != null)
                {
                    var offset = slot * ProtocolConstants.MarioModelIdSize;
                    if (offset + ProtocolConstants.MarioModelIdSize <= _workingBuffer.RemoteMarioModelIds.Length)
                        encoded.CopyTo(_workingBuffer.RemoteMarioModelIds, offset);
                }
            }
        }

        if (changed || !_hasLastWrittenModelIds)
            TryWriteMarioModelIds();
    }

    public void ApplyLocalMarioModelId(string? modelId)
    {
        var normalized = CharacterPack.NormalizeModelId(modelId);
        lock (_bufferLock)
        {
            var changed = !string.Equals(_appliedLocalModelId, normalized, StringComparison.Ordinal);
            if (!changed && _hasLastWrittenModelIds)
            {
                return;
            }

            if (changed)
            {
                var encoded = CharacterPack.EncodeModelId(normalized);
                _localMarioModelId = encoded;
                _appliedLocalModelId = normalized;
                unchecked
                {
                    ++_modelIdVersion;
                }
                if (_hasWorkingBuffer)
                {
                    _workingBuffer.LocalMarioModelId ??= new byte[ProtocolConstants.MarioModelIdSize];
                    encoded.CopyTo(_workingBuffer.LocalMarioModelId, 0);
                }
            }
        }

        TryWriteMarioModelIds();
    }

    public void RemoveRemoteSnapshot(byte slot)
    {
        lock (_bufferLock)
        {
            _remoteRaw.Remove(slot);
            _remoteAppearances.Remove(slot);
            _remotePureNames.Remove(slot);
            if (_remoteMarioModelIds.Remove(slot))
            {
                unchecked
                {
                    ++_modelIdVersion;
                }
            }
            if (slot < _remoteMarioVoiceEvents.Length)
                _remoteMarioVoiceEvents[slot] = default;
            if (_hasWorkingBuffer && _workingBuffer.RemoteMarioModelIds != null)
            {
                var offset = slot * ProtocolConstants.MarioModelIdSize;
                if (offset + ProtocolConstants.MarioModelIdSize <= _workingBuffer.RemoteMarioModelIds.Length)
                    Array.Clear(_workingBuffer.RemoteMarioModelIds, offset, ProtocolConstants.MarioModelIdSize);
            }
        }

        _interpolation.Remove(slot);
        _remoteClearPending = true;
        FlushInterpolatedRemotes(force: true);
        TryWriteMarioModelIds();
    }

    /// <summary>
    /// Clears interpolation/snapshot state for a slot that is being rejoined
    /// without clearing its roster model id. Avoids publishing a transient
    /// custom-to-retail-to-custom sequence into the mailbox.
    /// </summary>
    public void PrepareRemoteSlotForJoin(byte slot)
    {
        if (slot >= ProtocolConstants.MaxRemoteSlots)
            return;

        lock (_bufferLock)
        {
            _remoteRaw.Remove(slot);
            _remoteAppearances.Remove(slot);
            _remotePureNames.Remove(slot);
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

        CommRosterHudSync rosterCopy;
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
            // Bridge-authored roster HUD must survive live mailbox adoption on the 60 Hz poll.
            rosterCopy = CloneRosterHud(_workingBuffer.RosterHud);
        }

        // Partial poke — full-buffer writes race with the poll loop adopting a stale live
        // mailbox and wiping join/leave names before Dolphin sees them.
        if (!_bridge.TryWriteRosterHudOnly(rosterCopy))
            TryWriteWorkingBuffer();
    }

    /// <summary>
    /// Atomically adopts module-owned fields from a live mailbox read while retaining every
    /// bridge-authored field. The poll thread reads while Dolphin and partial bridge writes
    /// race; restoring only at the later remote flush left a client-only frame where local
    /// appearance (and potentially remote/game-mode state) reverted to Dolphin's stale copy.
    /// </summary>
    private void AdoptLiveBufferPreservingBridgeState_NoLock(in CommBuffer live)
    {
        var hadWorking = _hasWorkingBuffer;
        var authoredRoster = _hasWorkingBuffer ? CloneRosterHud(_workingBuffer.RosterHud) : default;
        var authoredSeq = _rosterHudNextSequence;
        var authoredFlags = _workingBuffer.BridgeFlags;
        var authoredLocalSlot = _workingBuffer.LocalSlot;
        var authoredLocalName = _workingBuffer.LocalPlayerName;
        var authoredRemoteSnapshots = _workingBuffer.RemoteSnapshots;
        var authoredLocalAppearance = _workingBuffer.LocalNameTagAppearance;
        var authoredRemoteAppearances = _workingBuffer.RemoteNameTagAppearances;
        var authoredRemoteVoice = _workingBuffer.RemoteMarioVoiceEvents;
        var authoredGameMode = _workingBuffer.GameModeState;

        _workingBuffer = live;
        _hasWorkingBuffer = true;

        if (_hasAppliedLocalNameTag)
            _workingBuffer.LocalNameTagAppearance = _appliedLocalNameTagAppearance;

        if (_sessionConnected && hadWorking)
        {
            // Stable control/configuration bits are bridge-owned. Loading and warp handshake
            // bits remain live/module-owned so a completed warp is never resurrected.
            const BridgeFlags stableBridgeFlags =
                BridgeFlags.Connected | BridgeFlags.Host |
                BridgeFlags.SyncShine | BridgeFlags.SyncBlueCoin |
                BridgeFlags.SyncEvent | BridgeFlags.SyncStory |
                BridgeFlags.SyncMission | BridgeFlags.SyncSecret |
                BridgeFlags.SyncObjects | BridgeFlags.SyncProgress;
            _workingBuffer.BridgeFlags =
                (_workingBuffer.BridgeFlags & ~stableBridgeFlags) |
                (authoredFlags & stableBridgeFlags);
            _workingBuffer.LocalSlot = authoredLocalSlot;
            _workingBuffer.LocalPlayerName = authoredLocalName ?? new byte[16];

            // stageExit clears remotes (clearPuppets). Hold network republish until
            // Active-after-transition so remotes cannot race freed bodies mid-teardown.
            // Stage identity flips also arm the hold (see MaybeRestore…) — they must
            // not release it while Loading. Arm without requiring Loading when we
            // previously saw live Connected remotes — exportLocalPlayer can force
            // Active (mCurState==STATE_NORMAL) and miss the Loading window. Still
            // arm on Booting/Loading/Warping so the dedicated stage-exit test path
            // works. Do not arm on stale empty CreateDefault (never saw live remotes,
            // DolphinState.None) — that must keep restoring authored remotes.
            var liveHasConnectedRemotes = AnyConnectedRemote(live.RemoteSnapshots);
            var transitional =
                live.DolphinState is DolphinState.Booting or DolphinState.Loading or DolphinState.Warping;
            if (_remoteRaw.Count > 0 &&
                AnyConnectedRemote(authoredRemoteSnapshots) &&
                !liveHasConnectedRemotes &&
                (transitional || _liveSawConnectedRemotes))
            {
                // First arm only — do not reset the Active grace counter on every empty
                // live adopt (post-restore write lag would otherwise re-arm forever).
                if (!_holdRemotePublishForStageExit)
                    _holdRemotePublishActivePolls = 0;
                _holdRemotePublishForStageExit = true;
            }

            if (liveHasConnectedRemotes)
                _liveSawConnectedRemotes = true;

            MaybeReleaseRemotePublishHold_NoLock(live);

            if (_holdRemotePublishForStageExit)
            {
                // Keep the module's cleared remote slots — do not restore authored/_remoteRaw.
                _workingBuffer.RemoteSnapshots = CommBuffer.CreateRemoteArray();
            }
            else
            {
                _workingBuffer.RemoteSnapshots =
                    authoredRemoteSnapshots ?? CommBuffer.CreateRemoteArray();
                foreach (var kvp in _remoteRaw)
                {
                    if (kvp.Key < _workingBuffer.RemoteSnapshots.Length)
                    {
                        var snap = kvp.Value;
                        snap.Connected = 1;
                        snap.Slot = kvp.Key;
                        _workingBuffer.RemoteSnapshots[kvp.Key] = snap;
                    }
                }
            }

            _workingBuffer.LocalNameTagAppearance = _hasAppliedLocalNameTag
                ? _appliedLocalNameTagAppearance
                : authoredLocalAppearance;
            _workingBuffer.RemoteNameTagAppearances =
                authoredRemoteAppearances ?? CommBuffer.CreateRemoteAppearanceArray();
            // Prefer the live per-slot appearance dictionary over a working-buffer
            // copy that may already have been partially clobbered before adopt.
            foreach (var kvp in _remoteAppearances)
            {
                if (kvp.Key < _workingBuffer.RemoteNameTagAppearances.Length)
                    _workingBuffer.RemoteNameTagAppearances[kvp.Key] = kvp.Value;
            }
            _workingBuffer.RemoteMarioVoiceEvents =
                authoredRemoteVoice ?? CommBuffer.CreateRemoteMarioVoiceEventArray();
            _workingBuffer.GameModeState = authoredGameMode;
        }

        // While disconnected, keep the cleared ring so stale Dolphin reads cannot resurrect
        // old join toasts. While connected, Roster HUD is bridge-authored only — never keep
        // Dolphin's ring payload. A higher live LatestSequence with empty event names was
        // overwriting EnqueueRosterHudEvent and causing in-game "Player N" toasts.
        if (!_sessionConnected)
        {
            _workingBuffer.RosterHud = authoredRoster.Events != null
                ? authoredRoster
                : CommRosterHudSync.CreateDefault();
        }
        else if (authoredSeq != 0 || authoredRoster.Events != null)
        {
            _workingBuffer.RosterHud = authoredRoster.Events != null
                ? authoredRoster
                : CommRosterHudSync.CreateDefault();
            if (_workingBuffer.RosterHud.LatestSequence < authoredSeq)
                _workingBuffer.RosterHud.LatestSequence = authoredSeq;

            // Advance the local counter past anything Dolphin already observed so the next
            // enqueue does not reuse a sequence, but do not adopt Dolphin's event bytes.
            if (live.RosterHud.LatestSequence > _rosterHudNextSequence)
                _rosterHudNextSequence = live.RosterHud.LatestSequence;
        }

        // Model ids are bridge-owned and use their own partial-write generation/cache.
        // Merge them on every adoption so any intervening full-buffer write is also safe.
        MergeMarioModelIdsIntoWorkingBuffer_NoLock();
    }

    private static CommRosterHudSync CloneRosterHud(in CommRosterHudSync source)
    {
        var clone = CommRosterHudSync.CreateDefault();
        clone.LatestSequence = source.LatestSequence;
        var srcEvents = source.Events;
        if (srcEvents == null)
            return clone;

        for (int i = 0; i < clone.Events.Length && i < srcEvents.Length; i++)
        {
            var src = srcEvents[i];
            var name = new byte[16];
            if (src.Name != null)
                Array.Copy(src.Name, name, Math.Min(src.Name.Length, name.Length));
            clone.Events[i] = new CommRosterHudEvent
            {
                Sequence = src.Sequence,
                Kind = src.Kind,
                Slot = src.Slot,
                Name = name,
            };
        }

        return clone;
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
        username ??= string.Empty;
        NameTagAppearance[]? remotesToWrite = null;
        NameTagAppearance localToWrite = default;

        lock (_bufferLock)
        {
            if (_hasAppliedLocalNameTag &&
                string.Equals(_appliedLocalNameTagName, username, StringComparison.Ordinal) &&
                NameTagAppearanceEquals(_appliedLocalNameTagAppearance, appearance))
            {
                return;
            }

            if (!EnsureWorkingBuffer())
                return;

            NameTagColorCodec.WritePureName(_workingBuffer.LocalSnapshot.Name, username);
            _workingBuffer.LocalNameTagAppearance = appearance;
            _appliedLocalNameTagName = username;
            _appliedLocalNameTagAppearance = appearance;
            _hasAppliedLocalNameTag = true;

            // Partial poke only — never full-buffer Read/WriteProcessMemory on the 60 Hz path.
            localToWrite = appearance;
            remotesToWrite = _workingBuffer.RemoteNameTagAppearances
                ?? CommBuffer.CreateRemoteAppearanceArray();
        }

        _bridge.TryWriteNameTagAppearancesOnly(localToWrite, remotesToWrite!);
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

    /// <summary>
    /// Call when the in-game mailbox may have been recreated while Dolphin stayed running
    /// (Reconnect Link, title return, ForceRelink). Forces model-id / nametag re-poke.
    /// </summary>
    public void InvalidateMailboxWriteCaches()
    {
        _bridge.InvalidateWriteCaches();
        _lastObservedBridgeWriteCacheEpoch = _bridge.WriteCacheEpoch;
        lock (_bufferLock)
        {
            _hasLastWrittenModelIds = false;
            _lastWrittenModelIdVersion = -1;
            _moduleReadySeen = false;
            _remoteClearPending = true;
        }
    }

    public void ClearRemoteSnapshots()
    {
        lock (_bufferLock)
        {
            _remoteRaw.Clear();
            _remoteAppearances.Clear();
            _remotePureNames.Clear();
            _holdRemotePublishForStageExit = false;
            _holdRemotePublishActivePolls = 0;
            _liveSawConnectedRemotes = false;
            if (_remoteMarioModelIds.Count > 0)
            {
                _remoteMarioModelIds.Clear();
                unchecked
                {
                    ++_modelIdVersion;
                }
            }
            Array.Clear(_remoteMarioVoiceEvents, 0, _remoteMarioVoiceEvents.Length);
            if (_hasWorkingBuffer && _workingBuffer.RemoteMarioModelIds != null)
                Array.Clear(_workingBuffer.RemoteMarioModelIds, 0, _workingBuffer.RemoteMarioModelIds.Length);
        }

        _interpolation.Clear();
        _remoteClearPending = true;
        _hasLastWrittenModelIds = false;
        FlushInterpolatedRemotes(force: true);
        TryWriteMarioModelIds();
    }

    private void EnsureRemoteMarioModelIdScratchCurrent_NoLock()
    {
        if (_modelIdScratchVersion == _modelIdVersion)
            return;

        Array.Clear(_remoteModelIdScratch, 0, _remoteModelIdScratch.Length);
        foreach (var kvp in _remoteMarioModelIds)
        {
            var offset = kvp.Key * ProtocolConstants.MarioModelIdSize;
            if (offset + ProtocolConstants.MarioModelIdSize <= _remoteModelIdScratch.Length)
                kvp.Value.CopyTo(_remoteModelIdScratch, offset);
        }
        _modelIdScratchVersion = _modelIdVersion;
        ++_modelIdScratchBuildCount;
    }

    /// <summary>
    /// Full-buffer writes (connection/sync) must not clobber model ids. Live CommBuffer reads
    /// often arrive before the first model-id write, so merge from the authoritative dictionaries.
    /// </summary>
    private void MergeMarioModelIdsIntoWorkingBuffer_NoLock()
    {
        if (!_hasWorkingBuffer)
            return;

        _workingBuffer.LocalMarioModelId ??= new byte[ProtocolConstants.MarioModelIdSize];
        _localMarioModelId.CopyTo(_workingBuffer.LocalMarioModelId, 0);
        EnsureRemoteMarioModelIdScratchCurrent_NoLock();
        _workingBuffer.RemoteMarioModelIds ??=
            new byte[ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots];
        _remoteModelIdScratch.CopyTo(_workingBuffer.RemoteMarioModelIds, 0);
    }

    private void TryWriteMarioModelIds()
    {
        lock (_modelIdWriteLock)
            TryWriteMarioModelIdsSerialized();
    }

    private void TryWriteMarioModelIdsSerialized()
    {
        int writeVersion;
        lock (_bufferLock)
        {
            // This runs on every bridge tick while ModuleReady. Version-check before clearing
            // or rebuilding the 9-slot scratch map so idle ticks are O(1).
            if (_hasLastWrittenModelIds && _lastWrittenModelIdVersion == _modelIdVersion)
                return;

            EnsureRemoteMarioModelIdScratchCurrent_NoLock();
            writeVersion = _modelIdVersion;

            if (_hasWorkingBuffer)
            {
                _workingBuffer.LocalMarioModelId ??= new byte[ProtocolConstants.MarioModelIdSize];
                _localMarioModelId.CopyTo(_workingBuffer.LocalMarioModelId, 0);
                _workingBuffer.RemoteMarioModelIds ??=
                    new byte[ProtocolConstants.MarioModelIdSize * ProtocolConstants.MaxRemoteSlots];
                _remoteModelIdScratch.CopyTo(_workingBuffer.RemoteMarioModelIds, 0);
            }

            _localMarioModelId.CopyTo(_modelIdsWriteLocal, 0);
            _remoteModelIdScratch.CopyTo(_modelIdsWriteRemotes, 0);
        }

        if (!_bridge.TryWriteMarioModelIdsOnly(_modelIdsWriteLocal, _modelIdsWriteRemotes))
            return;

        lock (_bufferLock)
        {
            if (_modelIdVersion != writeVersion)
            {
                _hasLastWrittenModelIds = false;
                return;
            }

            _modelIdsWriteLocal.CopyTo(_lastWrittenLocalModelId, 0);
            _modelIdsWriteRemotes.CopyTo(_lastWrittenRemoteModelIds, 0);
            _hasLastWrittenModelIds = true;
            _lastWrittenModelIdVersion = writeVersion;
        }
    }

    private static bool NameTagAppearanceEquals(in NameTagAppearance a, in NameTagAppearance b) =>
        a.TextTopR == b.TextTopR && a.TextTopG == b.TextTopG && a.TextTopB == b.TextTopB &&
        a.TextBottomR == b.TextBottomR && a.TextBottomG == b.TextBottomG &&
        a.TextBottomB == b.TextBottomB &&
        a.OutlineR == b.OutlineR && a.OutlineG == b.OutlineG && a.OutlineB == b.OutlineB &&
        a.Flags == b.Flags;

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
                AdoptLiveBufferPreservingBridgeState_NoLock(liveBuffer.Value);
            else if (!EnsureWorkingBuffer())
                return;

            // Live mailbox reads must not clobber the user-selected local nametag / model ids.
            if (_hasAppliedLocalNameTag)
                _workingBuffer.LocalNameTagAppearance = _appliedLocalNameTagAppearance;

            if (liveBuffer.HasValue)
                MaybeReleaseRemotePublishHold_NoLock(liveBuffer.Value);

            // Iterate remote slots by index (0..MaxRemoteSlots-1) instead of clearing the whole
            // array + OrderBy every tick. Reuse a single empty snapshot (shared Name buffer) so
            // we don't allocate 9 PlayerSnapshots + 9 byte[16] every 60 Hz flush.
            var remoteSnapshots = _workingBuffer.RemoteSnapshots;
            var remoteAppearancesBuf = _workingBuffer.RemoteNameTagAppearances;
            for (int slot = 0; slot < remoteSnapshots.Length; slot++)
            {
                if (slot == _workingBuffer.LocalSlot || _holdRemotePublishForStageExit)
                {
                    remoteSnapshots[slot] = s_emptyRemote;
                    if (slot < remoteAppearancesBuf.Length)
                        remoteAppearancesBuf[slot] = default;
                    continue;
                }

                if (_remoteRaw.TryGetValue((byte)slot, out var raw))
                {
                    PlayerSnapshot snap;
                    if (!_interpolation.TryAdvance((byte)slot, out snap))
                        snap = raw;

                    snap.Connected = 1;
                    snap.Slot = (byte)slot;
                    // Never let legacy color-packed Name[] reach Dolphin, and never
                    // mutate shared interpolation Name storage with SetName(GetPureName()).
                    EnsurePureNameForDolphin(ref snap, (byte)slot);
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

        // Only skip full remote sync during boot — Loading/Warping skips left remotes
        // starved when blocked loading zones wedged mGameState in WARPING during
        // otherwise normal play. Hide & Seek still needs game-mode (roles / grace /
        // tag events) written so death/tag paths do not run against a stale mailbox
        // while Dolphin reports Booting (common during stage transitions at 60fps).
        if (liveBuffer.HasValue && liveBuffer.Value.DolphinState is DolphinState.Booting)
        {
            if (commGameMode.Mode == (byte)GameMode.HideSeek)
                _bridge.TryWriteGameModeStateOnly(commGameMode);
            return;
        }

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
        var stageId = YoshiSnapshotCodec.LogicalStageId(buffer.LocalSnapshot, buffer.LocalSnapshot.StageId);
        var episodeId =
            YoshiSnapshotCodec.LogicalEpisodeId(buffer.LocalSnapshot, buffer.LocalSnapshot.EpisodeId);
        if (stageId == _lastRestoredStageId && episodeId == _lastRestoredEpisodeId)
            return;

        _lastRestoredStageId = stageId;
        _lastRestoredEpisodeId = episodeId;

        // Stage identity often flips while Dolphin is still Loading/Warping and the module
        // has already clearPuppets'd remotes. Clearing the hold here used to Flush Connected
        // remotes into that half-torn stage → freed TMario/J3D bodies, vertex stretch, and
        // ModelWater juice tint leaking into the local WATER HUD. Keep remotes suppressed;
        // MaybeReleaseRemotePublishHold_NoLock republishes only after Active grace.
        if (_remoteRaw.Count > 0)
        {
            if (!_holdRemotePublishForStageExit)
                _holdRemotePublishActivePolls = 0;
            _holdRemotePublishForStageExit = true;
        }

        FlushInterpolatedRemotes();
    }

    // Note: _liveSawConnectedRemotes is intentionally kept across stage changes so the
    // next stageExit clear (including Active-missed-Loading) can still arm the hold.

    private static bool AnyConnectedRemote(PlayerSnapshot[]? remotes)
    {
        if (remotes == null)
            return false;
        for (int i = 0; i < remotes.Length; i++)
        {
            if (remotes[i].Connected != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// End the stage-exit remote hold once the module is back in Active gameplay with a
    /// connected local snapshot for <see cref="HoldRemotePublishActiveGracePolls"/> polls.
    /// Always wait that grace — even after Booting/Loading/Warping — so the first Active
    /// frame after a stage-id flip cannot republish remotes into a half-settled stage
    /// (vertex stretch / ModelWater juice HUD leak). If the module forced Active through
    /// teardown and skipped Loading, the same Active grace still releases so remotes
    /// cannot stay suppressed forever. Stage/episode changes arm the hold via
    /// <see cref="MaybeRestoreRemoteSnapshotsAfterStageChange"/> — they do not release it.
    /// </summary>
    private void MaybeReleaseRemotePublishHold_NoLock(in CommBuffer live)
    {
        if (!_holdRemotePublishForStageExit)
            return;
        if (live.DolphinState is DolphinState.Booting or DolphinState.Loading or DolphinState.Warping)
        {
            _holdRemotePublishActivePolls = 0;
            return;
        }
        if (live.DolphinState != DolphinState.Active)
            return;
        if (live.LocalSnapshot.Connected == 0)
            return;

        _holdRemotePublishActivePolls++;
        if (_holdRemotePublishActivePolls < HoldRemotePublishActiveGracePolls)
            return;

        _holdRemotePublishForStageExit = false;
        _holdRemotePublishActivePolls = 0;
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
                            CommBuffer publishedBuffer;
                            lock (_bufferLock)
                            {
                                AdoptLiveBufferPreservingBridgeState_NoLock(buffer);
                                publishedBuffer = _workingBuffer;
                            }

                            // UI observers must see the same atomically merged image used
                            // by the bridge, never the transient Dolphin-authored copy.
                            BufferUpdated?.Invoke(publishedBuffer);
                            if (buffer.LocalSnapshot.Connected != 0 || (buffer.BridgeFlags & BridgeFlags.Connected) != 0)
                                LocalSnapshotReady?.Invoke(buffer.LocalSnapshot);
                            if ((buffer.BridgeFlags & BridgeFlags.RequestProgress) != 0)
                            {
                                lock (_bufferLock)
                                    _workingBuffer.BridgeFlags &= ~BridgeFlags.RequestProgress;
                                ModuleProgressResyncRequested?.Invoke();
                            }
                            MaybePublishLocalMarioVoice(buffer.LocalMarioVoiceEvent);
                            MaybePublishLocalWorldEvent(buffer.WorldSync.LocalPending);
                            DrainLocalWorldEventBacklog();
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
        var bridgeWriteCacheEpoch = _bridge.WriteCacheEpoch;
        if (_lastObservedBridgeWriteCacheEpoch != bridgeWriteCacheEpoch)
        {
            _lastObservedBridgeWriteCacheEpoch = bridgeWriteCacheEpoch;
            lock (_bufferLock)
                _remoteClearPending = true;
        }

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
        {
            _commVersionMismatchError = null;
            SetLinkState(DolphinLinkState.ModuleReady);
        }
        else
        {
            if (liveBuffer.HasValue && liveBuffer.Value.Magic == ProtocolConstants.Magic &&
                liveBuffer.Value.Version != ProtocolConstants.CommVersion)
            {
                _commVersionMismatchError =
                    "Outdated BSMSO module (comm mismatch) — update to the latest zip and restart Dolphin.";
            }
            else
            {
                _commVersionMismatchError = null;
            }

            SetLinkState(DolphinLinkState.Attached);
        }

        TryFlushPendingConnectionWrite();
        TryFlushPendingSyncWrite();
        // Model ids are written via a partial mailbox poke that can fail before attach;
        // re-push once the module mailbox is live so remotes see packs on first stage load.
        // Also re-push after mailbox re-init (title return / ForceRelink) — the skip cache
        // would otherwise think the prior write is still present in a wiped CommBuffer.
        if (LinkState == DolphinLinkState.ModuleReady)
        {
            if (!_moduleReadySeen)
            {
                _bridge.InvalidateWriteCaches();
                _lastObservedBridgeWriteCacheEpoch = _bridge.WriteCacheEpoch;
                lock (_bufferLock)
                {
                    _hasLastWrittenModelIds = false;
                    _lastWrittenModelIdVersion = -1;
                    _remoteClearPending = true;
                    _moduleReadySeen = true;
                }
            }
            else if (liveBuffer.HasValue)
            {
                InvalidateModelIdCacheIfLiveMismatch(liveBuffer.Value);
            }

            TryWriteMarioModelIds();
        }
        else
        {
            _moduleReadySeen = false;
        }
    }

    private void InvalidateModelIdCacheIfLiveMismatch(in CommBuffer live)
    {
        lock (_bufferLock)
        {
            if (!_hasLastWrittenModelIds)
                return;

            var liveLocal = live.LocalMarioModelId;
            if (liveLocal is null ||
                liveLocal.Length < ProtocolConstants.MarioModelIdSize ||
                !liveLocal.AsSpan(0, ProtocolConstants.MarioModelIdSize)
                    .SequenceEqual(_lastWrittenLocalModelId))
            {
                _hasLastWrittenModelIds = false;
                return;
            }

            var liveRemote = live.RemoteMarioModelIds;
            if (liveRemote is null ||
                liveRemote.Length < _lastWrittenRemoteModelIds.Length ||
                !liveRemote.AsSpan(0, _lastWrittenRemoteModelIds.Length)
                    .SequenceEqual(_lastWrittenRemoteModelIds))
            {
                _hasLastWrittenModelIds = false;
            }
        }
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
            worldEvent.Payload1,
            worldEvent.Payload2));

        // Hand the slot back to the module. The module only writes the next queued event once
        // this slot is empty, so clearing it after publishing is what lets outbound events
        // advance one per frame without overwriting each other.
        _bridge.TryClearLocalPendingWorldEvent();
    }

    /// <summary>
    /// After clearing localPending, the module may flush the next queued world event
    /// (flush-on-enqueue during graffiti spray). Re-read and publish up to N more events
    /// in this same poll so a 256-deep graffiti backlog does not stall at 1 event/tick.
    /// </summary>
    private void DrainLocalWorldEventBacklog()
    {
        const int maxExtra = 8;
        for (int i = 0; i < maxExtra; i++)
        {
            if (!_bridge.TryReadLocalPendingWorldEvent(out var next) || next.IsEmpty)
                break;
            if (next.Sequence == _lastLocalWorldEventSequence)
                break;
            MaybePublishLocalWorldEvent(next);
        }
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
        {
            var evt = packet.ToIncomingEvent();
            if (!IsPriorityMissionWorldEvent(packet.Type) || _pendingIncomingWorldEvents.Count == 0)
            {
                _pendingIncomingWorldEvents.Enqueue(evt);
                return;
            }

            // Keep mission/ownership events ahead of graffiti/fruit traffic, preserving FIFO
            // among priority events themselves.
            var priority = new List<CommWorldEvent>();
            var rest = new List<CommWorldEvent>();
            while (_pendingIncomingWorldEvents.Count > 0)
            {
                var pending = _pendingIncomingWorldEvents.Dequeue();
                if (IsPriorityMissionWorldEvent(pending.Type))
                    priority.Add(pending);
                else
                    rest.Add(pending);
            }

            foreach (var pending in priority)
                _pendingIncomingWorldEvents.Enqueue(pending);
            _pendingIncomingWorldEvents.Enqueue(evt);
            foreach (var pending in rest)
                _pendingIncomingWorldEvents.Enqueue(pending);
        }
    }

    /// <summary>
    /// Drops all queued remote world events and clears the Dolphin incoming mailbox slot.
    /// Call on disconnect / authority resync so a stuck durable visual retry cannot keep
    /// blocking live shine/blue ownership applies after the session replaces pending state.
    /// </summary>
    public void ClearPendingIncomingWorldEvents()
    {
        lock (_incomingWorldEventLock)
            _pendingIncomingWorldEvents.Clear();
        _bridge.TryClearIncomingWorldEvent();
        lock (_bufferLock)
        {
            if (_hasWorkingBuffer)
                _workingBuffer.WorldSync.Incoming = default;
        }
    }

    public bool TryGetLastAppliedEventId(out uint lastAppliedEventId)
    {
        lock (_bufferLock)
        {
            if (_bridge.IsAttached && _bridge.TryReadBuffer(out var buffer) && buffer.Magic == ProtocolConstants.Magic)
                AdoptLiveBufferPreservingBridgeState_NoLock(buffer);

            lastAppliedEventId = _workingBuffer.WorldSync.LastAppliedEventId;
            return _hasWorkingBuffer;
        }
    }

    /// <summary>Test hook: ordered types currently waiting for the Dolphin incoming slot.</summary>
    internal WorldEventType[] DebugGetPendingIncomingTypes()
    {
        lock (_incomingWorldEventLock)
            return _pendingIncomingWorldEvents.Select(e => e.Type).ToArray();
    }

    /// <summary>Test hook: seed world-sync mailbox fields without Dolphin attached.</summary>
    internal void DebugSeedWorldSync(uint lastAppliedEventId, uint incomingEventId = 0)
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            _workingBuffer.WorldSync.LastAppliedEventId = lastAppliedEventId;
            if (incomingEventId != 0)
            {
                _workingBuffer.WorldSync.Incoming = new CommWorldEvent { EventId = incomingEventId };
            }
        }
    }

    /// <summary>Test hook: read world-sync mailbox fields without Dolphin attached.</summary>
    internal (uint LastApplied, uint IncomingEventId) DebugGetWorldSync()
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            return (_workingBuffer.WorldSync.LastAppliedEventId, _workingBuffer.WorldSync.Incoming.EventId);
        }
    }

    internal int DebugModelIdScratchBuildCount
    {
        get
        {
            lock (_bufferLock)
                return _modelIdScratchBuildCount;
        }
    }

    /// <summary>Test hook: exercise the same live-mailbox adoption used by the poll loop.</summary>
    internal void DebugAdoptLiveBuffer(in CommBuffer live)
    {
        lock (_bufferLock)
            AdoptLiveBufferPreservingBridgeState_NoLock(live);
    }

    /// <summary>Test hook: same stage-identity restore path as the poll loop.</summary>
    internal void DebugMaybeRestoreRemoteSnapshotsAfterStageChange(in CommBuffer buffer)
    {
        MaybeRestoreRemoteSnapshotsAfterStageChange(buffer);
    }

    /// <summary>Test hook: whether stage-exit remote republish is currently held.</summary>
    internal bool DebugHoldRemotePublishForStageExit
    {
        get
        {
            lock (_bufferLock)
                return _holdRemotePublishForStageExit;
        }
    }

    /// <summary>
    /// Test hook: seed remote snapshot/appearance into the working buffer as the bridge
    /// poll would after a successful interpolated flush.
    /// </summary>
    internal void DebugSeedRemoteNametag(byte slot, in PlayerSnapshot snapshot,
                                         in NameTagAppearance appearance)
    {
        if (slot >= ProtocolConstants.MaxRemoteSlots)
            return;

        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            _remoteRaw[slot] = snapshot;
            _remoteAppearances[slot] = appearance;
            _workingBuffer.RemoteSnapshots[slot] = snapshot;
            _workingBuffer.RemoteNameTagAppearances[slot] = appearance;
        }
    }

    /// <summary>Test hook: inspect the bridge's atomically merged mailbox image.</summary>
    internal CommBuffer DebugGetWorkingBuffer()
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            return _workingBuffer;
        }
    }

    internal string? DebugGetRemotePureName(byte slot)
    {
        lock (_bufferLock)
            return _remotePureNames.TryGetValue(slot, out var name) ? name : null;
    }

    internal PlayerSnapshot DebugGetRemoteRaw(byte slot)
    {
        lock (_bufferLock)
            return _remoteRaw.TryGetValue(slot, out var snap) ? snap : default;
    }

    private bool TryWriteWorkingBuffer()
    {
        CommBuffer copy;
        lock (_bufferLock)
        {
            MergeMarioModelIdsIntoWorkingBuffer_NoLock();
            copy = _workingBuffer;
        }

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
