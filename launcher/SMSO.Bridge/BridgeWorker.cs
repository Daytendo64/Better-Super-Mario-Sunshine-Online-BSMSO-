using System.Threading;
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
    /// <summary>
    /// Per-slot owned Name buffers for UDP ingest + Dolphin flush. Avoids allocating
    /// <c>new byte[16]</c> on every remote packet / 60 Hz flush.
    /// </summary>
    private readonly byte[][] _remoteOwnedNames = CreateRemoteNameBuffers();
    private readonly byte[][] _remoteFlushNames = CreateRemoteNameBuffers();
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
    private bool _pendingConnectionWriteRetry;
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
    /// Absolute ceiling on stage-exit remote suppression. Remount/Loading flicker used to
    /// reset the Active grace forever so remotes never republished after clearPuppets
    /// (2026-07-21 stage-enter soft-death). After this wall clock, Loading/Warping no
    /// longer wipes Active grace progress — but MaxDuration must never itself release
    /// on the first Active tick after a long load (that tore down mounted Yoshi rigs).
    /// </summary>
    private static readonly TimeSpan HoldRemotePublishMaxDuration = TimeSpan.FromSeconds(3);
    private DateTime _holdRemotePublishSinceUtc = DateTime.MinValue;
    /// <summary>
    /// True after any live mailbox adopt showed Connected remotes. Distinguishes stageExit
    /// clears (had remotes → none) from stale empty CreateDefault adopts on first link.
    /// </summary>
    private bool _liveSawConnectedRemotes;
    /// <summary>
    /// Force-full / disconnect cleared the progress heal lane. Until the next
    /// <see cref="PushProgressSnapshot"/>, full CommBuffer writes and live adopts must
    /// not resurrect a pre-clear hostSeq/payload (live hostSeq&gt;0 would otherwise win
    /// the merge and undo the clear so the module never bulk-reapplies).
    /// </summary>
    private bool _progressSnapshotLaneCleared;
    /// <summary>
    /// <see cref="PushProgressSnapshot"/> just staged <c>moduleApplied=0</c> for a pending
    /// heal. Until live confirms the parked heal (same hostSeq, moduleApplied still 0) or
    /// brings a newer hostSeq, merge/adopt must not copy a stale Dolphin
    /// <c>moduleApplied &gt;= hostSeq</c> ack over the intentional zero (false catch-up).
    /// </summary>
    private bool _progressSnapshotHealStaged;
    /// <summary>
    /// Monotonic epoch written into <see cref="CommBuffer.ProgressSnapshotFlags"/> on each
    /// Push. Same-hostSeq <c>moduleApplied &gt;= host</c> is only trusted as a finished apply
    /// when live flags still match this epoch — otherwise it is a pre-push stale ack that
    /// would soft-skip the reheal and leave stage-enter force looking hung.
    /// </summary>
    private byte _progressSnapshotHealEpoch;
    /// <summary>
    /// Last ownership / mission incoming successfully partial-written by Drain. Used to
    /// splice across a full CommBuffer write when live still reads empty (write not yet
    /// visible) without resurrecting a stale working event after the module clears the slot.
    /// </summary>
    private CommWorldEvent _stagedIncomingOwnership;
    private CommWorldEvent _stagedIncoming;
    private bool _stagedIncomingOwnershipSeenLive;
    private bool _stagedIncomingSeenLive;
    private PlayerSnapshot[]? _cachedRemoteSnapshots;
    private byte _lastRestoredStageId;
    private byte _lastRestoredEpisodeId;
    private ushort _lastLocalMarioVoiceSequence;
    private ushort _lastLocalOwnershipWorldEventSequence;
    private ushort _lastLocalMissionWorldEventSequence;
    /// <summary>
    /// Sequences published to the network but not yet cleared from Dolphin. Prevents
    /// duplicate TCP sends while still retrying clear so a failed clear cannot stall
    /// an outbound localPending lane forever (shine markShineSet → never re-emitted).
    /// </summary>
    private ushort _publishedUnclearedOwnershipSequence;
    private ushort _publishedUnclearedMissionSequence;
    /// <summary>
    /// Sequences whose TCP send is confirmed (or permanently retained for replay). The
    /// Dolphin localPending lane is only cleared — and last-published only advanced —
    /// once the network leg reports success. Enqueue alone is not an ack: the server's
    /// authorities are the sole heal source and cannot recover a mutation they never saw.
    /// </summary>
    private ushort _ackedOwnershipSequence;
    private ushort _ackedMissionSequence;
    private int _localPendingAbandonCount;
    private int _worldEventSendFailureCount;
    private int _worldEventRetainedCount;
    private int _pollLoopRestartCount;
    private int _localPendingClearAttempts;
    private const int MaxPollLoopRestarts = 8;
    private const int PollLoopRestartBaseDelayMs = 250;
    private const int PollLoopRestartMaxDelayMs = 5000;
    /// <summary>Test seam: substitute the poll body to exercise the restart watchdog.</summary>
    internal Func<CancellationToken, Task>? PollLoopBodyOverrideForTests { get; set; }
    /// <summary>
    /// When true (default), session callbacks run on the thread pool so a hung
    /// SessionCoordinator/TCP path cannot freeze localPending clears. Tests disable
    /// this for synchronous LocalWorldEventReady assertions.
    /// </summary>
    private bool _detachSessionCallbacks = true;
    /// <summary>
    /// Unified detached session outbound: LocalSnapshotReady (latest-wins) runs before
    /// ModuleProgressResyncRequested / LocalWorldEventReady for the same publish tick so
    /// OnLocalSnapshot stage-enter / PublishSnapshot cannot race behind world-event TCP.
    /// Cleared from Dolphin only after world-event enqueue so DrainLocalWorldEventBacklog
    /// cannot race ahead of ThreadPool callbacks.
    /// </summary>
    private readonly object _sessionOutboundLock = new();
    private readonly Queue<OutboundWorldEvent> _outboundWorldEventQueue = new();
    /// <summary>
    /// Durable events whose send exhausted its retries (or that were queued when the
    /// session dropped). Held keyed by identity and replayed on the next connect so a
    /// brief outage cannot silently lose a shine / story flag / red coin.
    /// </summary>
    private readonly List<OutboundWorldEvent> _retainedWorldEvents = new();
    private bool _sessionOutboundRetryArmed;
    private const int MaxWorldEventSendAttempts = 5;
    private const int WorldEventRetryBaseDelayMs = 100;
    private const int WorldEventRetryMaxDelayMs = 2000;
    /// <summary>Bounded so a long outage cannot grow the retention list without limit.</summary>
    private const int MaxRetainedWorldEvents = 64;
    /// <summary>
    /// Slow cadence for re-attempting retained events while the session stays up. Retention
    /// used to drain only on reconnect, so a session that never dropped kept the mutation
    /// forever while the UI looked healthy.
    /// </summary>
    private const int RetainedRetryIntervalMs = 5000;
    /// <summary>Log floor for the periodic drain — a permanently failing send must not spam.</summary>
    private const int RetainedRetryLogIntervalMs = 60000;
    private long _nextRetainedRetryTicks;
    private long _lastRetainedRetryLogTicks;
    private int _retainedRetryCycleCount;
    private PlayerSnapshot _pendingLocalSnapshot;
    private long _localSnapshotPublishSeq;
    private long _localSnapshotAppliedSeq;
    private bool _hasPendingLocalSnapshot;
    private bool _pendingModuleProgressResync;
    private bool _sessionOutboundDrainScheduled;
    private readonly Queue<CommWorldEvent> _pendingOwnershipIncoming = new();
    private readonly Queue<CommWorldEvent> _pendingMissionIncoming = new();
    private readonly Queue<CommWorldEvent> _pendingEphemeralIncoming = new();
    /// <summary>
    /// Cap ephemeral (NpcReact / fruit / hip-drop) depth. Phase A: server should not
    /// fan these out; DropOldest 8 keeps mixed-build leftovers from wedging mission.
    /// </summary>
    private const int MaxPendingEphemeralIncoming = 8;
    /// <summary>
    /// Cap red/NPC mission pending. Heal from <see cref="WorldProgressSnapshot"/> covers drops.
    /// </summary>
    private const int MaxPendingMissionIncoming = 32;
    /// <summary>
    /// Hard cap on ownership pending. Coalesce duplicate shine/blue/story keys first;
    /// if still at cap, drop oldest distinct so new events still land. Snapshot heals
    /// recover dropped ownership bits.
    /// </summary>
    private const int MaxPendingOwnershipIncoming = 64;
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
            or WorldEventType.TriggerFlag
            or WorldEventType.SessionProgressReset;

    /// <summary>
    /// Same-stage mission progress that must jump ahead of graffiti / fruit spam so
    /// co-op partners see hides live (not after a level reload / 45s resync).
    /// </summary>
    /// <summary>
    /// Episode mission traffic — durable for heal, but must never outrank card ownership
    /// (shine/blue/story/secret/trigger) in the pending incoming reorder.
    /// </summary>
    private static bool IsMissionWorldEvent(WorldEventType type) =>
        type is WorldEventType.RedCoinCollected
            or WorldEventType.NpcCleaned;

    private static bool IsEphemeralIncomingWorldEvent(WorldEventType type) =>
        WorldEventTcpPolicy.IsNonNetworkedEphemeral(type);
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
    private byte _musicVolumePercent = ProtocolConstants.CommMusicVolumeDefault;
    private bool _hasAppliedMusicVolume;
    private ushort _rosterHudNextSequence;
    private string? _commVersionMismatchError;

    public GameModeStatePacket CurrentGameModeState => _gameModeState.Clone();

    /// <summary>
    /// One outbound durable world event and its delivery state. Reference type so retry
    /// bookkeeping survives a front-requeue without copying.
    /// </summary>
    private sealed class OutboundWorldEvent
    {
        public WorldEventRequest Request;
        public bool OwnershipLane;
        /// <summary>Dolphin localPending sequence to clear on ack; 0 for retained replays.</summary>
        public ushort LaneSequence;
        public int Attempts;
        public long NotBeforeTicks;
        /// <summary>Came back out of retention: per-attempt logging is left to the throttled drain.</summary>
        public bool Replayed;

        public (WorldEventType, byte, byte, byte, uint) Key =>
            (Request.Type, Request.CourseId, Request.EpisodeId, Request.Payload0, Request.Payload1);
    }

    public event Action<CommBuffer>? BufferUpdated;
    public event Action<PlayerSnapshot>? LocalSnapshotReady;
    /// <summary>Module set BF_REQUEST_PROGRESS (co-op same-stage death reload).</summary>
    public event Action? ModuleProgressResyncRequested;
    public event Action<MarioVoiceEvent>? LocalMarioVoiceReady;
    public event Action<WorldEventRequest>? LocalWorldEventReady;

    /// <summary>
    /// Tracked publish hook. Returns true only when the event actually reached the server
    /// socket; false requeues it (front of FIFO) with backoff. When null the bridge falls
    /// back to the fire-and-forget <see cref="LocalWorldEventReady"/> event.
    /// </summary>
    public Func<WorldEventRequest, Task<bool>>? LocalWorldEventSendAsync { get; set; }
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
        _loop = Task.Run(() => PollLoopSupervisor(_cts.Token));
    }

    /// <summary>
    /// Restarts <see cref="PollLoop"/> after a fault outside its per-tick guard (e.g. a
    /// faulted <see cref="PeriodicTimer"/>). Without this the loop exited silently and every
    /// Dolphin read/write stopped while the UI still showed a healthy session — and
    /// <see cref="Start"/> refuses to relaunch while the old task is incomplete.
    /// </summary>
    private async Task PollLoopSupervisor(CancellationToken ct)
    {
        var restarts = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var body = PollLoopBodyOverrideForTests ?? PollLoop;
                await body(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                restarts++;
                Interlocked.Increment(ref _pollLoopRestartCount);
                if (restarts > MaxPollLoopRestarts)
                {
                    Log?.Invoke(
                        $"Bridge worker STOPPED after {MaxPollLoopRestarts} restarts " +
                        $"({ex.GetType().Name}: {ex.Message}) — Dolphin sync is dead; " +
                        "restart the launcher.");
                    return;
                }

                var delayMs = Math.Min(
                    PollLoopRestartMaxDelayMs, PollLoopRestartBaseDelayMs * (1 << (restarts - 1)));
                Log?.Invoke(
                    $"Bridge poll loop faulted ({ex.GetType().Name}: {ex.Message}) — " +
                    $"restart {restarts}/{MaxPollLoopRestarts} in {delayMs} ms");
                try
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
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

        // Fresh connect and disconnect both must drop the progress snapshot lane so a
        // join heal with hostSeq=1 is not soft-skipped against a stale moduleAppliedSeq
        // left in Dolphin RAM from a prior session (same class as world-event / game-mode).
        var clearProgressSnapshotMailbox = false;
        var invalidateRemoteWriteCaches = false;

        lock (_bufferLock)
        {
            // Use launcher-session state, not a possibly stale mailbox bit. If the prior
            // disconnect write missed Dolphin, the live buffer can still say Connected;
            // treating that as the same session preserves stale sequence/incoming state.
            var wasConnected = _sessionConnected;

            if (connected)
            {
                // Mark connected before adopting the live mailbox so bridge-authored remote
                // snapshots/appearances are preserved. Adopting first left a client-only write
                // that pushed Dolphin's stale nametag sidecars until the next interpolated flush.
                _sessionConnected = true;
            }
            else
            {
                // Drop session BEFORE adopt so disconnect cannot arm HoldRemotePublish
                // (adopt used to run while _sessionConnected was still true, then clear
                // hold afterward — a poll tick racing the window could keep remotes
                // suppressed across rehost until Dolphin restart).
                _sessionConnected = false;
                _holdRemotePublishForStageExit = false;
                _holdRemotePublishActivePolls = 0;
                _holdRemotePublishSinceUtc = DateTime.MinValue;
                _liveSawConnectedRemotes = false;
                _remoteRaw.Clear();
                _remoteAppearances.Clear();
                _remotePureNames.Clear();
                _interpolation.Clear();
                _remoteClearPending = true;
                invalidateRemoteWriteCaches = true;
            }

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
                    // Rehost / fresh join while Dolphin stays open: never inherit a stuck
                    // stage-exit hold or stale Connected remotes from the prior lobby.
                    _holdRemotePublishForStageExit = false;
                    _holdRemotePublishActivePolls = 0;
                    _holdRemotePublishSinceUtc = DateTime.MinValue;
                    _liveSawConnectedRemotes = false;
                    _workingBuffer.RemoteSnapshots = CommBuffer.CreateRemoteArray();
                    _workingBuffer.RemoteNameTagAppearances =
                        CommBuffer.CreateRemoteAppearanceArray();
                    _workingBuffer.RemoteMarioVoiceEvents =
                        CommBuffer.CreateRemoteMarioVoiceEventArray();
                    _remoteClearPending = true;
                    invalidateRemoteWriteCaches = true;

                    _workingBuffer.WorldSync.LastAppliedEventId = 0;
                    _workingBuffer.WorldSync.IncomingOwnership = default;
                    _workingBuffer.WorldSync.Incoming = default;
                    _workingBuffer.WorldSync.LocalPendingOwnership = default;
                    _workingBuffer.WorldSync.LocalPendingMission = default;
                    _lastLocalOwnershipWorldEventSequence = 0;
                    _lastLocalMissionWorldEventSequence = 0;
                    _publishedUnclearedOwnershipSequence = 0;
                    _publishedUnclearedMissionSequence = 0;
                    RetainOutboundWorldEventsForReconnect_NoLockNeeded();

                    // Mirror disconnect: clear after live adopt so Dolphin's stale
                    // moduleAppliedSeq cannot no-op the upcoming join heal.
                    ClearProgressSnapshotFields_NoLock();
                    _progressSnapshotLaneCleared = true;
                    _progressSnapshotHealStaged = false;
                    clearProgressSnapshotMailbox = true;
                }
            }
            else
            {
                _workingBuffer.BridgeFlags &= ~BridgeFlags.Connected;
                // Zero remotes in the same Connected-clear write so the module never
                // observes BF_CONNECTED=0 with leftover Connected remoteSnapshots (and
                // so a later rehost adopt cannot arm Hold from authored leftovers).
                _workingBuffer.RemoteSnapshots = CommBuffer.CreateRemoteArray();
                _workingBuffer.RemoteNameTagAppearances =
                    CommBuffer.CreateRemoteAppearanceArray();
                _workingBuffer.RemoteMarioVoiceEvents =
                    CommBuffer.CreateRemoteMarioVoiceEventArray();
                Array.Clear(_remoteMarioVoiceEvents, 0, _remoteMarioVoiceEvents.Length);
                _rosterHudNextSequence = 0;
                _workingBuffer.RosterHud = CommRosterHudSync.CreateDefault();
                // Drop any staged/in-flight world event so a disconnect mid-replay cannot
                // leave a collectible apply sitting in the mailbox for the next session.
                _workingBuffer.WorldSync.IncomingOwnership = default;
                _workingBuffer.WorldSync.Incoming = default;
                _workingBuffer.WorldSync.LocalPendingOwnership = default;
                _workingBuffer.WorldSync.LocalPendingMission = default;
                _workingBuffer.WorldSync.LastAppliedEventId = 0;
                _lastLocalOwnershipWorldEventSequence = 0;
                _lastLocalMissionWorldEventSequence = 0;
                _publishedUnclearedOwnershipSequence = 0;
                _publishedUnclearedMissionSequence = 0;
                // Hold (do not wipe) unsent durable events — a brief drop must not lose a
                // shine / story flag the module already marked published.
                RetainOutboundWorldEventsForReconnect_NoLockNeeded();

                // Clear game-mode seq so a rehost's Seq=1 is not rejected against a stale high seq
                // left by ResetHideSeekIfActiveOnServer applying Normal before teardown.
                var wasHideSeek = _gameModeState.GameMode == GameMode.HideSeek;
                _gameModeState = GameModeStatePacket.CreateDefault();
                _lastGameModeSeq = 0;
                if (wasHideSeek)
                    RestoreSavedNameTagColors();
                _workingBuffer.GameModeState = GameModeStatePacket.ToCommGameMode(slot, _gameModeState);

                // Same class of bug as game-mode seq: drop progress snapshot so join heals
                // with hostSeq=1 are not skipped against a stale moduleAppliedSeq.
                ClearProgressSnapshotFields_NoLock();
                _progressSnapshotLaneCleared = true;
                _progressSnapshotHealStaged = false;
                clearProgressSnapshotMailbox = true;
                _stagedIncomingOwnership = default;
                _stagedIncoming = default;
                _stagedIncomingOwnershipSeenLive = false;
                _stagedIncomingSeenLive = false;
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

        if (invalidateRemoteWriteCaches)
            _bridge.InvalidateWriteCaches();
        if (clearProgressSnapshotMailbox)
            _bridge.TryClearProgressSnapshot();
        if (!TryWriteWorkingBuffer())
        {
            // Always retry — previously `_pendingConnectionWrite = connected` left disconnect
            // (false) unretried, so BF_CONNECTED never cleared in Dolphin.
            _pendingConnectionWriteRetry = true;
            if (!_loggedConnectionPending)
            {
                _loggedConnectionPending = true;
                Log?.Invoke("Dolphin not linked — connection flags queued until attach");
            }
        }
        else
        {
            _pendingConnectionWriteRetry = false;
            _loggedConnectionPending = false;
        }

        // Force a remote-sync partial after the Connected flag write so empty (disconnect)
        // or a clean slate (fresh connect) cannot be SequenceEqual-skipped against a stale
        // full-buffer remote region left by TryWriteBuffer.
        if (invalidateRemoteWriteCaches)
            FlushInterpolatedRemotes(force: true);

        // Durable events that never reached the previous session's server go out now.
        if (connected)
            FlushRetainedWorldEvents();
    }

    /// <summary>Store latest raw network sample; smoothing runs on bridge poll (~60 Hz).</summary>
    public void PushRemoteSnapshot(byte slot, in PlayerSnapshot snap, in NameTagAppearance appearance)
    {
        if (slot >= ProtocolConstants.MaxRemoteSlots)
            return;

        // Deep-copy Name into a per-slot owned buffer so later UDP receives cannot mutate
        // this stored snapshot (and so interpolation's LastRaw.Name is not aliased to a
        // live decode buffer) — without allocating every packet.
        var owned = CloneSnapshotNameForSlot(slot, snap);
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

    private static byte[][] CreateRemoteNameBuffers()
    {
        var arr = new byte[ProtocolConstants.MaxRemoteSlots][];
        for (var i = 0; i < arr.Length; i++)
            arr[i] = new byte[16];
        return arr;
    }

    private PlayerSnapshot CloneSnapshotNameForSlot(byte slot, in PlayerSnapshot snap)
    {
        var owned = snap;
        var dest = slot < _remoteOwnedNames.Length
            ? _remoteOwnedNames[slot]
            : new byte[16];
        Array.Clear(dest, 0, dest.Length);
        if (snap.Name != null && snap.Name.Length > 0)
        {
            var copyLen = Math.Min(16, snap.Name.Length);
            Buffer.BlockCopy(snap.Name, 0, dest, 0, copyLen);
        }

        owned.Name = dest;
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

        // Always install a per-slot owned buffer so flush never mutates interpolation storage
        // and never writes legacy overlay markers into Dolphin.
        var name = slot < _remoteFlushNames.Length
            ? _remoteFlushNames[slot]
            : new byte[16];
        Array.Clear(name, 0, name.Length);
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

    /// <summary>
    /// Push in-game BGM volume (0–100 percent) into the mailbox. Applies live while Dolphin is linked.
    /// </summary>
    public void ApplyMusicVolume(int percent)
    {
        var clamped = CommBufferEndian.ClampMusicVolumePercent(percent);
        lock (_bufferLock)
        {
            var changed = !_hasAppliedMusicVolume || _musicVolumePercent != clamped;
            _musicVolumePercent = clamped;
            _hasAppliedMusicVolume = true;
            if (_hasWorkingBuffer)
                _workingBuffer.MusicVolume = clamped;
            if (!changed)
                return;
        }

        if (!_bridge.TryWriteMusicVolumeOnly(clamped))
            TryWriteWorkingBuffer();
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
        // Pending force-heal: preserve intentional moduleApplied=0 + payload across adopt.
        var preserveStagedHeal = hadWorking && _progressSnapshotHealStaged &&
                                 !_progressSnapshotLaneCleared;
        var authoredProgressHostSeq = _workingBuffer.ProgressSnapshotHostSeq;
        var authoredProgressModuleApplied = _workingBuffer.ProgressSnapshotModuleAppliedSeq;
        var authoredProgressPayloadLen = _workingBuffer.ProgressSnapshotPayloadLen;
        var authoredProgressFlags = _workingBuffer.ProgressSnapshotFlags;
        byte[]? authoredProgressPayload = null;
        if (preserveStagedHeal &&
            _workingBuffer.ProgressSnapshotPayload != null &&
            authoredProgressPayloadLen > 0)
        {
            authoredProgressPayload = new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
            var copyLen = Math.Min(authoredProgressPayloadLen,
                _workingBuffer.ProgressSnapshotPayload.Length);
            copyLen = Math.Min(copyLen, authoredProgressPayload.Length);
            Buffer.BlockCopy(_workingBuffer.ProgressSnapshotPayload, 0,
                authoredProgressPayload, 0, copyLen);
        }

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
                {
                    _holdRemotePublishActivePolls = 0;
                    _holdRemotePublishSinceUtc = DateTime.UtcNow;
                }
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

            // Never adopt Dolphin's LatestSequence into the enqueue counter. A higher
            // live sequence with empty event names advanced the ring past unread
            // authored joins and made the module fall back to "Player N" toasts.
        }

        // Model ids are bridge-owned and use their own partial-write generation/cache.
        // Merge them on every adoption so any intervening full-buffer write is also safe.
        MergeMarioModelIdsIntoWorkingBuffer_NoLock();

        // Music volume is launcher-authored (like model ids).
        _workingBuffer.MusicVolume = _musicVolumePercent;

        // Force-full cleared the heal lane — do not let a pre-clear live hostSeq resurrect
        // into the working buffer (next full write would undo the clear).
        if (_progressSnapshotLaneCleared)
        {
            ClearProgressSnapshotFields_NoLock();
        }
        else if (preserveStagedHeal)
        {
            // Keep the intentional pending heal until live confirms applied=0 (parked),
            // applied >= staged hostSeq (module finished, possibly before we saw 0), or
            // advertises a newer hostSeq with payload.
            if (live.ProgressSnapshotHostSeq > authoredProgressHostSeq &&
                live.ProgressSnapshotPayloadLen > 0 &&
                live.ProgressSnapshotPayload != null)
            {
                // Newer heal already in Dolphin — trust live, drop the staged latch.
                _progressSnapshotHealStaged = false;
            }
            else if (live.ProgressSnapshotHostSeq == authoredProgressHostSeq &&
                     authoredProgressHostSeq != 0 &&
                     live.ProgressSnapshotModuleAppliedSeq == 0)
            {
                // Mailbox matches Push intent — further moduleApplied acks are real.
                _progressSnapshotHealStaged = false;
            }
            else if (live.ProgressSnapshotHostSeq == authoredProgressHostSeq &&
                     authoredProgressHostSeq != 0 &&
                     live.ProgressSnapshotModuleAppliedSeq >= authoredProgressHostSeq &&
                     live.ProgressSnapshotFlags == authoredProgressFlags &&
                     authoredProgressFlags != 0)
            {
                // Fast module apply skipped the applied=0 window, but live still carries
                // this Push's epoch — trust the ack. Mismatched/zero flags are a stale
                // pre-push applied>=host that must not clear the latch.
                _progressSnapshotHealStaged = false;
                // Keep live.moduleApplied (already in _workingBuffer from adopt).
            }
            else
            {
                _workingBuffer.ProgressSnapshotHostSeq = authoredProgressHostSeq;
                _workingBuffer.ProgressSnapshotModuleAppliedSeq = authoredProgressModuleApplied;
                _workingBuffer.ProgressSnapshotPayloadLen = authoredProgressPayloadLen;
                _workingBuffer.ProgressSnapshotFlags = authoredProgressFlags;
                if (authoredProgressPayload != null)
                {
                    _workingBuffer.ProgressSnapshotPayload ??=
                        new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
                    Array.Clear(_workingBuffer.ProgressSnapshotPayload, 0,
                        _workingBuffer.ProgressSnapshotPayload.Length);
                    Buffer.BlockCopy(authoredProgressPayload, 0,
                        _workingBuffer.ProgressSnapshotPayload, 0,
                        Math.Min(authoredProgressPayload.Length,
                            _workingBuffer.ProgressSnapshotPayload.Length));
                }
            }
        }
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

        for (byte slot = 0; slot < ProtocolConstants.MaxPlayers; slot++)
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

            // Start Tag grace / roles travel in the bundled remote-sync game-mode
            // bytes. When that write fails during Active play, fall back so the
            // module does not keep a stale mailbox without GMF_GRACE_ACTIVE
            // (seeker input lock released until the next TickGrace rebroadcast).
            if (ShouldFallbackGameModeWriteOnRemoteSyncFail(commGameMode))
                _bridge.TryWriteGameModeStateOnly(commGameMode);

            return;
        }

        _remoteSyncWriteFailStreak = 0;
        _remoteClearPending = false;
    }

    /// <summary>
    /// When the full remote-sync payload write fails, still push Hide &amp; Seek
    /// game-mode (roles / grace / tag) so Start Tag freeze cannot miss Dolphin.
    /// </summary>
    internal static bool ShouldFallbackGameModeWriteOnRemoteSyncFail(in CommGameModeState gm)
        => gm.Mode == (byte)GameMode.HideSeek;

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
            {
                _holdRemotePublishActivePolls = 0;
                _holdRemotePublishSinceUtc = DateTime.UtcNow;
            }
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
    /// (vertex stretch / ModelWater juice HUD leak / mounted Yoshi initInLoadAfter).
    /// If the module forced Active through teardown and skipped Loading, the same Active
    /// grace still releases so remotes cannot stay suppressed forever. Stage/episode
    /// changes arm the hold via <see cref="MaybeRestoreRemoteSnapshotsAfterStageChange"/> —
    /// they do not release it.
    /// After <see cref="HoldRemotePublishMaxDuration"/>, transitional Loading/Warping
    /// flicker no longer resets the Active grace counter (soft-death fix), but MaxDuration
    /// alone never republishes remotes.
    /// </summary>
    private void MaybeReleaseRemotePublishHold_NoLock(in CommBuffer live)
    {
        if (!_holdRemotePublishForStageExit)
            return;

        var pastMaxDuration = _holdRemotePublishSinceUtc != DateTime.MinValue &&
                              DateTime.UtcNow - _holdRemotePublishSinceUtc >= HoldRemotePublishMaxDuration;

        // During Booting/Loading/Warping: normally reset Active grace so a brief Active
        // blip mid-teardown cannot accumulate toward release. After MaxDuration, keep any
        // grace progress so remount flicker cannot soft-kill republish forever — but still
        // do not release while transitional.
        if (live.DolphinState is DolphinState.Booting or DolphinState.Loading or DolphinState.Warping)
        {
            if (!pastMaxDuration)
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
        _holdRemotePublishSinceUtc = DateTime.MinValue;
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
                            // Snapshot / module-resync / world-event share one ordered handoff:
                            // latest-wins snapshot (+ optional progress resync) before outbound
                            // world events for this tick — without blocking the poll thread.
                            if (buffer.LocalSnapshot.Connected != 0 || (buffer.BridgeFlags & BridgeFlags.Connected) != 0)
                                PublishLocalSnapshotReady(buffer.LocalSnapshot);
                            if ((buffer.BridgeFlags & BridgeFlags.RequestProgress) != 0)
                            {
                                lock (_bufferLock)
                                    _workingBuffer.BridgeFlags &= ~BridgeFlags.RequestProgress;
                                PublishModuleProgressResyncRequested();
                            }
                            MaybePublishLocalMarioVoice(buffer.LocalMarioVoiceEvent);
                            // Ownership outbound first — never blocked by mission/ephemeral.
                            MaybePublishLocalWorldEvent(
                                buffer.WorldSync.LocalPendingOwnership, ownershipLane: true);
                            MaybePublishLocalWorldEvent(
                                buffer.WorldSync.LocalPendingMission, ownershipLane: false);
                            DrainLocalWorldEventBacklog();
                            DrainPendingIncomingWorldEvents(buffer);

                            MaybeRestoreRemoteSnapshotsAfterStageChange(buffer);
                        }

                        UpdateLinkStateFromBridge(liveBuffer);
                        FlushInterpolatedRemotes(prefetchedLive: liveBuffer);
                        TryApplyPendingWarp();
                    }

                    // Session-scoped, not Dolphin-scoped: durable events that exhausted
                    // their send retries must keep draining while we are connected.
                    MaybeRetryRetainedWorldEvents();
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
        finally
        {
            timer?.Dispose();
        }

        // Anything outside the per-tick guard (timer fault, cancellation source disposal)
        // propagates to PollLoopSupervisor, which restarts with backoff instead of leaving
        // the session silently unsynced.
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
            TryWriteMusicVolume();
        }
        else
        {
            _moduleReadySeen = false;
        }
    }

    private void TryWriteMusicVolume()
    {
        byte percent;
        lock (_bufferLock)
        {
            if (!_hasAppliedMusicVolume)
                return;
            percent = _musicVolumePercent;
            if (_hasWorkingBuffer)
                _workingBuffer.MusicVolume = percent;
        }

        if (!_bridge.TryWriteMusicVolumeOnly(percent))
            TryWriteWorkingBuffer();
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
        if (!_pendingConnectionWriteRetry)
            return;

        if (LinkState != DolphinLinkState.ModuleReady)
            return;

        if (!TryWriteWorkingBuffer())
            return;

        _pendingConnectionWriteRetry = false;
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
        InvokeDetached(LocalMarioVoiceReady, voiceEvent);
    }

    private void MaybePublishLocalWorldEvent(CommWorldEvent worldEvent, bool ownershipLane)
    {
        var laneName = ownershipLane ? "ownership" : "mission";
        var lastSeq = ownershipLane
            ? _lastLocalOwnershipWorldEventSequence
            : _lastLocalMissionWorldEventSequence;
        var publishedUncleared = ownershipLane
            ? _publishedUnclearedOwnershipSequence
            : _publishedUnclearedMissionSequence;

        if (worldEvent.IsEmpty)
        {
            // Module abandoned or bridge cleared elsewhere while we still held an uncleared
            // publish — count for field grep (`localPendingAbandon=`).
            if (publishedUncleared != 0)
            {
                if (ownershipLane)
                    _publishedUnclearedOwnershipSequence = 0;
                else
                    _publishedUnclearedMissionSequence = 0;
                var n = Interlocked.Increment(ref _localPendingAbandonCount);
                Log?.Invoke(
                    $"World sync: localPendingAbandon={n} lane={laneName} (slot emptied without clear ack)");
            }
            return;
        }

        var seq = worldEvent.Sequence;

        // Fully consumed previously but slot still occupied (clear raced / failed): keep
        // clearing without re-entering the network path.
        if (seq == lastSeq)
        {
            _ = ClearLocalPendingLane(ownershipLane);
            return;
        }

        // Publish once per sequence. Do not advance last-seq until the Dolphin slot is
        // actually cleared — otherwise a failed clear permanently stalls outbound ownership
        // (module markShineSet / markBlueCoinSet already fired; event never re-emitted).
        // Detached path: durable ordered queue handoff BEFORE clear so DrainLocalWorldEventBacklog
        // and ThreadPool reordering cannot drop/reorder TCP world events across reconnect.
        if (seq != publishedUncleared)
        {
            if (ownershipLane)
                _publishedUnclearedOwnershipSequence = seq;
            else
                _publishedUnclearedMissionSequence = seq;
            ClearLaneAck(ownershipLane);
            var request = new WorldEventRequest(
                worldEvent.Sequence,
                worldEvent.Type,
                worldEvent.CourseId,
                worldEvent.EpisodeId,
                worldEvent.Payload0,
                worldEvent.Reserved,
                worldEvent.Payload1,
                worldEvent.Payload2);
            if (!_detachSessionCallbacks)
            {
                // Synchronous test path: the callback itself is the ack.
                LocalWorldEventReady?.Invoke(request);
            }
            else
            {
                EnqueueOutboundWorldEvent(new OutboundWorldEvent
                {
                    Request = request,
                    OwnershipLane = ownershipLane,
                    LaneSequence = seq,
                });

                // Hold the Dolphin lane until the send is confirmed. Clearing here made a
                // failed / raced TCP write indistinguishable from a delivered one, and the
                // module's authority caches were already marked published — so the mutation
                // was lost for the rest of the session (peers stuck pre-flood, missing 0x77).
                return;
            }
        }
        else if (_detachSessionCallbacks && !LaneSendAcked(ownershipLane, seq))
        {
            // Still in flight (or backing off) — leave the lane occupied.
            return;
        }

        if (ClearLocalPendingLane(ownershipLane))
        {
            // Defend against a racing full-buffer write that resurrected the same seq
            // before we advanced last-seq. Re-read; if still occupied with this seq, clear again.
            if (ReadLocalPendingLane(ownershipLane, out var afterClear) &&
                !afterClear.IsEmpty &&
                afterClear.Sequence == seq)
            {
                if (!ClearLocalPendingLane(ownershipLane))
                {
                    Log?.Invoke(
                        $"World sync: localPending re-clear failed lane={laneName} seq={seq} type={worldEvent.Type} — will retry");
                    return;
                }
            }

            if (ownershipLane)
            {
                _lastLocalOwnershipWorldEventSequence = seq;
                _publishedUnclearedOwnershipSequence = 0;
            }
            else
            {
                _lastLocalMissionWorldEventSequence = seq;
                _publishedUnclearedMissionSequence = 0;
            }

            ClearLaneAck(ownershipLane);
            return;
        }

        Log?.Invoke(
            $"World sync: localPending clear failed lane={laneName} seq={seq} type={worldEvent.Type} — will retry");
    }

    /// <summary>
    /// Latest-wins LocalSnapshotReady handoff. Detached path coalesces to the newest snap
    /// under a monotonic publish seq and drains on the shared session-outbound ThreadPool
    /// worker so stage-enter / PublishSnapshot always precede world-event callbacks queued
    /// later on the same poll tick.
    /// </summary>
    private void PublishLocalSnapshotReady(in PlayerSnapshot snap)
    {
        if (LocalSnapshotReady == null)
            return;

        if (!_detachSessionCallbacks)
        {
            try
            {
                LocalSnapshotReady.Invoke(snap);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Bridge LocalSnapshotReady callback error: {ex.Message}");
            }

            lock (_sessionOutboundLock)
            {
                _localSnapshotPublishSeq++;
                _localSnapshotAppliedSeq = _localSnapshotPublishSeq;
            }

            return;
        }

        var schedule = false;
        lock (_sessionOutboundLock)
        {
            _localSnapshotPublishSeq++;
            _pendingLocalSnapshot = snap;
            _hasPendingLocalSnapshot = true;
            if (!_sessionOutboundDrainScheduled)
            {
                _sessionOutboundDrainScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
            ScheduleSessionOutboundDrain();
    }

    /// <summary>
    /// Module BF_REQUEST_PROGRESS — sequenced after any pending LocalSnapshotReady on the
    /// shared outbound drain (same-tick ordering with stage-enter snapshot side effects).
    /// </summary>
    private void PublishModuleProgressResyncRequested()
    {
        if (ModuleProgressResyncRequested == null)
            return;

        if (!_detachSessionCallbacks)
        {
            try
            {
                ModuleProgressResyncRequested.Invoke();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Bridge ModuleProgressResyncRequested callback error: {ex.Message}");
            }

            return;
        }

        var schedule = false;
        lock (_sessionOutboundLock)
        {
            _pendingModuleProgressResync = true;
            if (!_sessionOutboundDrainScheduled)
            {
                _sessionOutboundDrainScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
            ScheduleSessionOutboundDrain();
    }

    /// <summary>
    /// Enqueue for FIFO ThreadPool drain. Callers keep the Dolphin lane occupied until the
    /// drain reports a confirmed send, so the event cannot be lost if disconnect or an
    /// I/O error races the callback. Snapshot handoff queued earlier on the same tick is
    /// drained first (shared <see cref="_sessionOutboundLock"/>).
    /// </summary>
    private void EnqueueOutboundWorldEvent(OutboundWorldEvent item)
    {
        var schedule = false;
        lock (_sessionOutboundLock)
        {
            _outboundWorldEventQueue.Enqueue(item);
            if (!_sessionOutboundDrainScheduled)
            {
                _sessionOutboundDrainScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
            ScheduleSessionOutboundDrain();
    }

    private void ScheduleSessionOutboundDrain()
    {
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            _ = ((BridgeWorker)state!).DrainSessionOutboundQueueAsync();
        }, this);
    }

    /// <summary>Re-schedule a drain when one is not already running (retry timer / reconnect flush).</summary>
    private void EnsureSessionOutboundDrain()
    {
        var schedule = false;
        lock (_sessionOutboundLock)
        {
            if (!_sessionOutboundDrainScheduled &&
                (_outboundWorldEventQueue.Count > 0 || _hasPendingLocalSnapshot ||
                 _pendingModuleProgressResync))
            {
                _sessionOutboundDrainScheduled = true;
                schedule = true;
            }
        }

        if (schedule)
            ScheduleSessionOutboundDrain();
    }

    /// <summary>
    /// Releases the drain and re-arms it after the head event's backoff. Sleeping inside the
    /// drain instead would stall the latest-wins local snapshot handoff behind a retrying
    /// world event (remote peers would see us freeze for the whole backoff).
    /// </summary>
    private void ArmOutboundRetry(int delayMs)
    {
        lock (_sessionOutboundLock)
        {
            if (_sessionOutboundRetryArmed)
                return;
            _sessionOutboundRetryArmed = true;
        }

        _ = Task.Delay(Math.Max(1, delayMs)).ContinueWith(static (_, state) =>
        {
            var worker = (BridgeWorker)state!;
            lock (worker._sessionOutboundLock)
                worker._sessionOutboundRetryArmed = false;
            worker.EnsureSessionOutboundDrain();
        }, this, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// Single ThreadPool consumer: latest pending snapshot → optional module progress
    /// resync → FIFO world events. Drops stale snapshot callbacks by only ever dequeuing
    /// the current pending slot (overwrite = latest-wins).
    /// </summary>
    private async Task DrainSessionOutboundQueueAsync()
    {
        while (true)
        {
            PlayerSnapshot snap = default;
            long snapSeq = 0;
            var haveSnap = false;
            var progressResync = false;
            OutboundWorldEvent? worldEvent = null;
            var retryDelayMs = 0;

            lock (_sessionOutboundLock)
            {
                if (_hasPendingLocalSnapshot)
                {
                    snap = _pendingLocalSnapshot;
                    snapSeq = _localSnapshotPublishSeq;
                    _hasPendingLocalSnapshot = false;
                    haveSnap = true;
                }
                else if (_pendingModuleProgressResync)
                {
                    _pendingModuleProgressResync = false;
                    progressResync = true;
                }
                else if (_outboundWorldEventQueue.Count > 0)
                {
                    var head = _outboundWorldEventQueue.Peek();
                    var now = Environment.TickCount64;
                    if (head.NotBeforeTicks > now)
                    {
                        // Head is backing off — release the drain so snapshots keep flowing.
                        retryDelayMs = (int)Math.Min(
                            head.NotBeforeTicks - now, WorldEventRetryMaxDelayMs);
                        _sessionOutboundDrainScheduled = false;
                    }
                    else
                    {
                        worldEvent = _outboundWorldEventQueue.Dequeue();
                    }
                }
                else
                {
                    _sessionOutboundDrainScheduled = false;
                    return;
                }
            }

            if (retryDelayMs > 0)
            {
                ArmOutboundRetry(retryDelayMs);
                return;
            }

            try
            {
                if (haveSnap)
                {
                    // Latest-wins: if a newer snapshot parked while we held this one, drop the
                    // stale callback so _lastLocalSnapshot / PublishSnapshot never move backwards.
                    lock (_sessionOutboundLock)
                    {
                        if (_hasPendingLocalSnapshot && _localSnapshotPublishSeq > snapSeq)
                            continue;
                    }

                    LocalSnapshotReady?.Invoke(snap);
                    lock (_sessionOutboundLock)
                    {
                        if (snapSeq > _localSnapshotAppliedSeq)
                            _localSnapshotAppliedSeq = snapSeq;
                    }
                }
                else if (progressResync)
                {
                    ModuleProgressResyncRequested?.Invoke();
                }
                else if (worldEvent != null)
                {
                    await SendOutboundWorldEventAsync(worldEvent).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Bridge session outbound callback error: {ex.Message}");
                if (worldEvent != null)
                {
                    // Never drop a durable event on a callback fault — the mutation only
                    // exists on this client until the server accepts it.
                    HandleOutboundWorldEventFailure(worldEvent, ex.Message);
                }
            }
        }
    }

    private async Task SendOutboundWorldEventAsync(OutboundWorldEvent item)
    {
        var send = LocalWorldEventSendAsync;
        if (send == null)
        {
            LocalWorldEventReady?.Invoke(item.Request);
            AckOutboundWorldEvent(item);
            return;
        }

        if (await send(item.Request).ConfigureAwait(false))
        {
            AckOutboundWorldEvent(item);
            return;
        }

        HandleOutboundWorldEventFailure(item, "send reported failure");
    }

    /// <summary>
    /// Marks the Dolphin lane clearable for this sequence. The poll thread owns every
    /// mailbox write, so the clear itself happens on the next poll tick.
    /// </summary>
    private void AckOutboundWorldEvent(OutboundWorldEvent item)
    {
        if (item.LaneSequence == 0)
            return;

        lock (_sessionOutboundLock)
        {
            if (item.OwnershipLane)
                _ackedOwnershipSequence = item.LaneSequence;
            else
                _ackedMissionSequence = item.LaneSequence;
        }
    }

    private bool LaneSendAcked(bool ownershipLane, ushort seq)
    {
        lock (_sessionOutboundLock)
            return seq != 0 &&
                   seq == (ownershipLane ? _ackedOwnershipSequence : _ackedMissionSequence);
    }

    private void ClearLaneAck(bool ownershipLane)
    {
        lock (_sessionOutboundLock)
        {
            if (ownershipLane)
                _ackedOwnershipSequence = 0;
            else
                _ackedMissionSequence = 0;
        }
    }

    /// <summary>
    /// Bounded retry with backoff, then retention. Requeue is at the FRONT so ordering
    /// within a lane is preserved; duplicate sends are safe because every server accept
    /// (shine / story / red coin) is grow-only and idempotent.
    /// </summary>
    private void HandleOutboundWorldEventFailure(OutboundWorldEvent item, string reason)
    {
        Interlocked.Increment(ref _worldEventSendFailureCount);
        item.Attempts++;
        if (item.Attempts < MaxWorldEventSendAttempts)
        {
            var delay = Math.Min(
                WorldEventRetryMaxDelayMs,
                WorldEventRetryBaseDelayMs * (1 << (item.Attempts - 1)));
            item.NotBeforeTicks = Environment.TickCount64 + delay;
            lock (_sessionOutboundLock)
                RequeueOutboundFront_NoLock(item);
            // Replays are already reported by the throttled retention drain; logging every
            // attempt of a permanently failing event would flood the log file.
            if (!item.Replayed)
            {
                Log?.Invoke(
                    $"World sync: publish failed ({reason}) type={item.Request.Type} " +
                    $"payload0={item.Request.Payload0} payload1={item.Request.Payload1} — " +
                    $"retry {item.Attempts}/{MaxWorldEventSendAttempts} in {delay} ms");
            }
            ArmOutboundRetry(delay);
            return;
        }

        // Retries exhausted: keep the event for the next connect instead of dropping it,
        // and release the Dolphin lane so the module's ownership queue keeps draining.
        // Retention detaches the lane sequence, so release the lane using the original one.
        var laneSequence = item.LaneSequence;
        var ownershipLane = item.OwnershipLane;
        RetainOutboundWorldEvent(item);
        if (laneSequence != 0)
        {
            lock (_sessionOutboundLock)
            {
                if (ownershipLane)
                    _ackedOwnershipSequence = laneSequence;
                else
                    _ackedMissionSequence = laneSequence;
            }
        }

        if (!item.Replayed)
        {
            Log?.Invoke(
                $"World sync: publish exhausted retries ({reason}) type={item.Request.Type} " +
                $"payload0={item.Request.Payload0} payload1={item.Request.Payload1} — " +
                $"retained for replay (retained={_worldEventRetainedCount})");
        }
    }

    private void RequeueOutboundFront_NoLock(OutboundWorldEvent item)
    {
        if (_outboundWorldEventQueue.Count == 0)
        {
            _outboundWorldEventQueue.Enqueue(item);
            return;
        }

        var rest = new List<OutboundWorldEvent>(_outboundWorldEventQueue.Count + 1) { item };
        while (_outboundWorldEventQueue.Count > 0)
            rest.Add(_outboundWorldEventQueue.Dequeue());
        foreach (var pending in rest)
            _outboundWorldEventQueue.Enqueue(pending);
    }

    /// <summary>
    /// Keyed by (type, course, episode, payload0, payload1) so repeated attempts at the
    /// same shine / flag / coin collapse to one entry. Bounded: a long outage evicts the
    /// oldest rather than growing without limit.
    /// </summary>
    private void RetainOutboundWorldEvent(OutboundWorldEvent item)
    {
        item.Attempts = 0;
        item.NotBeforeTicks = 0;
        // Replays are no longer tied to a Dolphin lane sequence (SetConnected resets it).
        item.LaneSequence = 0;

        lock (_sessionOutboundLock)
        {
            var key = item.Key;
            for (var i = 0; i < _retainedWorldEvents.Count; i++)
            {
                if (!_retainedWorldEvents[i].Key.Equals(key))
                    continue;
                _retainedWorldEvents[i] = item;
                return;
            }

            _retainedWorldEvents.Add(item);
            while (_retainedWorldEvents.Count > MaxRetainedWorldEvents)
                _retainedWorldEvents.RemoveAt(0);
            _worldEventRetainedCount = _retainedWorldEvents.Count;
            // Give the send path a full cadence before the next attempt so exhaustion
            // cannot roll straight into another burst.
            _nextRetainedRetryTicks = Environment.TickCount64 + RetainedRetryIntervalMs;
        }
    }

    /// <summary>
    /// Drains retention on a slow cadence for as long as the session is live. Without this
    /// an event that exhausted its retries mid-session (transient stream fault, brief server
    /// hiccup, five fast failures during a stage load) was only ever replayed by
    /// <see cref="SetConnected"/>, so a session that never dropped lost the mutation
    /// permanently while the UI still showed a healthy session.
    /// Called from the poll loop outside <see cref="_bufferLock"/>; the send itself still
    /// happens on the shared outbound ThreadPool worker.
    /// </summary>
    private void MaybeRetryRetainedWorldEvents()
    {
        if (!Volatile.Read(ref _sessionConnected))
            return;

        lock (_sessionOutboundLock)
        {
            if (_retainedWorldEvents.Count == 0)
                return;

            var now = Environment.TickCount64;
            if (now < _nextRetainedRetryTicks)
                return;
            _nextRetainedRetryTicks = now + RetainedRetryIntervalMs;
        }

        FlushRetainedWorldEvents(periodic: true);
    }

    /// <summary>
    /// Connect/disconnect used to wipe the outbound queue outright, discarding durable
    /// mutations that Dolphin had already marked published. Move them to retention instead.
    /// </summary>
    private void RetainOutboundWorldEventsForReconnect_NoLockNeeded()
    {
        List<OutboundWorldEvent> pending;
        lock (_sessionOutboundLock)
        {
            _hasPendingLocalSnapshot = false;
            _pendingModuleProgressResync = false;
            _ackedOwnershipSequence = 0;
            _ackedMissionSequence = 0;
            if (_outboundWorldEventQueue.Count == 0)
                return;

            pending = new List<OutboundWorldEvent>(_outboundWorldEventQueue.Count);
            while (_outboundWorldEventQueue.Count > 0)
                pending.Add(_outboundWorldEventQueue.Dequeue());
        }

        foreach (var item in pending)
        {
            if (WorldEventTcpPolicy.ShouldSendLocalWorldEvent(item.Request.Type))
                RetainOutboundWorldEvent(item);
        }

        if (_worldEventRetainedCount > 0)
            Log?.Invoke($"World sync: retained {_worldEventRetainedCount} unsent world event(s) for reconnect");
    }

    /// <summary>
    /// Replays retained durable events: on reconnect bootstrap and, periodically, while the
    /// session stays connected. An entry whose key is already waiting in the outbound queue
    /// is dropped rather than re-added — the queued copy carries the same mutation, and it
    /// returns to retention by itself if it also fails, so nothing is lost and the same
    /// event can never be in flight twice.
    /// </summary>
    private void FlushRetainedWorldEvents(bool periodic = false)
    {
        var replayed = 0;
        var deduped = 0;
        lock (_sessionOutboundLock)
        {
            if (_retainedWorldEvents.Count == 0)
                return;

            var queuedKeys = new HashSet<(WorldEventType, byte, byte, byte, uint)>();
            foreach (var queued in _outboundWorldEventQueue)
                queuedKeys.Add(queued.Key);

            var replay = new List<OutboundWorldEvent>(_retainedWorldEvents);
            _retainedWorldEvents.Clear();
            foreach (var item in replay)
            {
                if (!queuedKeys.Add(item.Key))
                {
                    deduped++;
                    continue;
                }

                item.Attempts = 0;
                item.NotBeforeTicks = 0;
                item.Replayed = true;
                _outboundWorldEventQueue.Enqueue(item);
                replayed++;
            }

            _worldEventRetainedCount = _retainedWorldEvents.Count;
        }

        if (!periodic)
        {
            Log?.Invoke(
                $"World sync: replaying {replayed} retained world event(s) after connect" +
                (deduped > 0 ? $" ({deduped} already queued)" : string.Empty));
        }
        else
        {
            var cycle = Interlocked.Increment(ref _retainedRetryCycleCount);
            var now = Environment.TickCount64;
            if (_lastRetainedRetryLogTicks == 0 ||
                now - _lastRetainedRetryLogTicks >= RetainedRetryLogIntervalMs)
            {
                _lastRetainedRetryLogTicks = now;
                Log?.Invoke(
                    $"World sync: re-sending {replayed} retained world event(s) mid-session " +
                    $"(drain cycle {cycle})");
            }
        }

        if (replayed > 0)
            EnsureSessionOutboundDrain();
    }

    /// <summary>
    /// Run session/network callbacks off the bridge poll thread. A synchronous hang inside
    /// LocalSnapshotReady / LocalWorldEventReady previously froze localPending clears for
    /// the rest of the session (2026-07-21: stage-enter force then silence).
    /// Snapshot + world-event use <see cref="DrainSessionOutboundQueue"/> instead.
    /// </summary>
    private void InvokeDetached(Action? handler)
    {
        if (handler == null)
            return;
        if (!_detachSessionCallbacks)
        {
            handler();
            return;
        }

        var h = handler;
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            try
            {
                ((Action)state!)();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Bridge detached callback error: {ex.Message}");
            }
        }, h);
    }

    private void InvokeDetached<T>(Action<T>? handler, T arg)
    {
        if (handler == null)
            return;
        if (!_detachSessionCallbacks)
        {
            handler(arg);
            return;
        }

        var h = handler;
        var a = arg;
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                h(a);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Bridge detached callback error: {ex.Message}");
            }
        }, null);
    }

    private bool ClearLocalPendingLane(bool ownershipLane)
    {
        Interlocked.Increment(ref _localPendingClearAttempts);
        return ownershipLane
            ? _bridge.TryClearLocalPendingOwnershipWorldEvent()
            : _bridge.TryClearLocalPendingMissionWorldEvent();
    }

    private bool ReadLocalPendingLane(bool ownershipLane, out CommWorldEvent worldEvent) =>
        ownershipLane
            ? _bridge.TryReadLocalPendingOwnershipWorldEvent(out worldEvent)
            : _bridge.TryReadLocalPendingMissionWorldEvent(out worldEvent);

    /// <summary>
    /// Test hook: same localPending publish/clear handshake as the poll loop.
    /// Runs session callbacks synchronously so unit tests can assert without waiting
    /// on the thread pool.
    /// </summary>
    internal void DebugPublishLocalWorldEvent(CommWorldEvent worldEvent, bool ownershipLane = true)
    {
        var prior = _detachSessionCallbacks;
        _detachSessionCallbacks = false;
        try
        {
            MaybePublishLocalWorldEvent(worldEvent, ownershipLane);
        }
        finally
        {
            _detachSessionCallbacks = prior;
        }
    }

    /// <summary>
    /// Test hook: detached (production) handoff path — enqueues then clears; drain is FIFO.
    /// </summary>
    internal void DebugPublishLocalWorldEventDetached(CommWorldEvent worldEvent, bool ownershipLane = true)
    {
        var prior = _detachSessionCallbacks;
        _detachSessionCallbacks = true;
        try
        {
            MaybePublishLocalWorldEvent(worldEvent, ownershipLane);
        }
        finally
        {
            _detachSessionCallbacks = prior;
        }
    }

    /// <summary>
    /// Test hook: detached (production) LocalSnapshotReady handoff — latest-wins coalesce.
    /// </summary>
    internal void DebugPublishLocalSnapshotReadyDetached(in PlayerSnapshot snap)
    {
        var prior = _detachSessionCallbacks;
        _detachSessionCallbacks = true;
        try
        {
            PublishLocalSnapshotReady(snap);
        }
        finally
        {
            _detachSessionCallbacks = prior;
        }
    }

    /// <summary>
    /// Test hook: detached ModuleProgressResyncRequested on the shared session outbound drain.
    /// </summary>
    internal void DebugPublishModuleProgressResyncDetached()
    {
        var prior = _detachSessionCallbacks;
        _detachSessionCallbacks = true;
        try
        {
            PublishModuleProgressResyncRequested();
        }
        finally
        {
            _detachSessionCallbacks = prior;
        }
    }

    /// <summary>Test hook: wait briefly for the session outbound drain to idle.</summary>
    internal bool DebugWaitOutboundWorldEventDrainIdle(int timeoutMs = 1000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_sessionOutboundLock)
            {
                if (_outboundWorldEventQueue.Count == 0 &&
                    !_hasPendingLocalSnapshot &&
                    !_pendingModuleProgressResync &&
                    !_sessionOutboundDrainScheduled &&
                    !_sessionOutboundRetryArmed)
                    return true;
            }
            Thread.Sleep(5);
        }

        lock (_sessionOutboundLock)
        {
            return _outboundWorldEventQueue.Count == 0 &&
                   !_hasPendingLocalSnapshot &&
                   !_pendingModuleProgressResync &&
                   !_sessionOutboundDrainScheduled &&
                   !_sessionOutboundRetryArmed;
        }
    }

    internal long DebugLocalSnapshotPublishSeq
    {
        get { lock (_sessionOutboundLock) return _localSnapshotPublishSeq; }
    }

    internal long DebugLocalSnapshotAppliedSeq
    {
        get { lock (_sessionOutboundLock) return _localSnapshotAppliedSeq; }
    }

    internal int DebugOutboundWorldEventQueueCount
    {
        get
        {
            lock (_sessionOutboundLock)
                return _outboundWorldEventQueue.Count;
        }
    }

    internal ushort DebugLastLocalWorldEventSequence => _lastLocalOwnershipWorldEventSequence;
    internal ushort DebugLastLocalMissionWorldEventSequence => _lastLocalMissionWorldEventSequence;
    internal ushort DebugPublishedUnclearedLocalWorldEventSequence =>
        _publishedUnclearedOwnershipSequence;
    internal int DebugLocalPendingAbandonCount => _localPendingAbandonCount;
    internal int DebugWorldEventSendFailureCount => Volatile.Read(ref _worldEventSendFailureCount);
    internal int DebugLocalPendingClearAttempts => Volatile.Read(ref _localPendingClearAttempts);
    internal int DebugPollLoopRestartCount => Volatile.Read(ref _pollLoopRestartCount);

    internal ushort DebugAckedOwnershipSequence
    {
        get { lock (_sessionOutboundLock) return _ackedOwnershipSequence; }
    }

    internal int DebugRetainedWorldEventCount
    {
        get { lock (_sessionOutboundLock) return _retainedWorldEvents.Count; }
    }

    internal IReadOnlyList<WorldEventRequest> DebugRetainedWorldEvents
    {
        get
        {
            lock (_sessionOutboundLock)
                return _retainedWorldEvents.Select(e => e.Request).ToList();
        }
    }

    /// <summary>Test hook: start the poll-loop watchdog without a real Dolphin poll body.</summary>
    internal Task DebugRunPollLoopSupervisorAsync(CancellationToken ct) => PollLoopSupervisor(ct);

    /// <summary>Test hook: simulate a session drop/reconnect without touching Dolphin state.</summary>
    internal void DebugRetainOutboundForReconnect() =>
        RetainOutboundWorldEventsForReconnect_NoLockNeeded();

    /// <summary>Test hook: replay retained events (fresh-connect path).</summary>
    internal void DebugFlushRetainedWorldEvents() => FlushRetainedWorldEvents();

    /// <summary>Test hook: one poll-loop retention drain tick (respects connect + cadence gates).</summary>
    internal void DebugRetryRetainedWorldEvents() => MaybeRetryRetainedWorldEvents();

    /// <summary>Test hook: pull the retention cadence forward instead of sleeping it out.</summary>
    internal void DebugExpireRetainedRetryCadence()
    {
        lock (_sessionOutboundLock)
            _nextRetainedRetryTicks = 0;
    }

    /// <summary>Test hook: snapshot + clear the pending incoming world-event queues.</summary>
    internal List<CommWorldEvent> DebugDrainPendingIncomingWorldEvents()
    {
        lock (_incomingWorldEventLock)
        {
            var drained = new List<CommWorldEvent>(
                _pendingOwnershipIncoming.Count +
                _pendingMissionIncoming.Count +
                _pendingEphemeralIncoming.Count);
            while (_pendingOwnershipIncoming.Count > 0)
                drained.Add(_pendingOwnershipIncoming.Dequeue());
            while (_pendingMissionIncoming.Count > 0)
                drained.Add(_pendingMissionIncoming.Dequeue());
            while (_pendingEphemeralIncoming.Count > 0)
                drained.Add(_pendingEphemeralIncoming.Dequeue());
            return drained;
        }
    }

    /// <summary>
    /// After clearing a localPending lane, the module may flush the next queued world event.
    /// Re-read and publish up to N more events per lane in this same poll.
    /// </summary>
    private void DrainLocalWorldEventBacklog()
    {
        const int maxExtra = 8;
        DrainLocalPendingBacklogLane(ownershipLane: true, maxExtra);
        DrainLocalPendingBacklogLane(ownershipLane: false, maxExtra);
    }

    private void DrainLocalPendingBacklogLane(bool ownershipLane, int maxExtra)
    {
        var lastSeq = ownershipLane
            ? _lastLocalOwnershipWorldEventSequence
            : _lastLocalMissionWorldEventSequence;
        for (int i = 0; i < maxExtra; i++)
        {
            if (!ReadLocalPendingLane(ownershipLane, out var next) || next.IsEmpty)
                break;
            if (next.Sequence == lastSeq)
                break;
            MaybePublishLocalWorldEvent(next, ownershipLane);
            lastSeq = ownershipLane
                ? _lastLocalOwnershipWorldEventSequence
                : _lastLocalMissionWorldEventSequence;
        }
    }

    private void DrainPendingIncomingWorldEvents(CommBuffer liveBuffer)
    {
        // Ownership lane is independent — drain even while mission occupies the general slot.
        DrainOwnershipIncomingLane(liveBuffer);
        DrainMissionEphemeralIncomingLane(liveBuffer);
    }

    private void DrainOwnershipIncomingLane(CommBuffer liveBuffer)
    {
        CommWorldEvent next;
        lock (_incomingWorldEventLock)
        {
            if (_pendingOwnershipIncoming.Count == 0)
                return;
            if (liveBuffer.WorldSync.IncomingOwnership.EventId != 0)
                return;
            next = _pendingOwnershipIncoming.Dequeue();
        }

        if (_bridge.TryWriteIncomingOwnershipWorldEventOnly(next))
        {
        lock (_bufferLock)
            {
                _stagedIncomingOwnership = next;
                _stagedIncomingOwnershipSeenLive = false;
                if (_hasWorkingBuffer)
                    _workingBuffer.WorldSync.IncomingOwnership = next;
            }

            return;
        }

        // RPM write failed — put back at front (never drop ownership).
        lock (_incomingWorldEventLock)
            RequeueFront(_pendingOwnershipIncoming, next);
    }

    private void DrainMissionEphemeralIncomingLane(CommBuffer liveBuffer)
    {
        CommWorldEvent next;
        bool fromMission;
        lock (_incomingWorldEventLock)
        {
            if (liveBuffer.WorldSync.Incoming.EventId != 0)
                return;

            if (_pendingMissionIncoming.Count > 0)
            {
                next = _pendingMissionIncoming.Dequeue();
                fromMission = true;
            }
            else if (_pendingEphemeralIncoming.Count > 0)
            {
                next = _pendingEphemeralIncoming.Dequeue();
                fromMission = false;
            }
            else
                return;
        }

        if (_bridge.TryWriteIncomingWorldEventOnly(next))
        {
            lock (_bufferLock)
            {
                _stagedIncoming = next;
                _stagedIncomingSeenLive = false;
                if (_hasWorkingBuffer)
                    _workingBuffer.WorldSync.Incoming = next;
            }

            return;
        }

        lock (_incomingWorldEventLock)
        {
            if (fromMission)
                RequeueFront(_pendingMissionIncoming, next);
            else
                RequeueFront(_pendingEphemeralIncoming, next);
        }
    }

    private static void RequeueFront(Queue<CommWorldEvent> queue, in CommWorldEvent evt)
    {
        if (queue.Count == 0)
        {
            queue.Enqueue(evt);
            return;
        }

        var rest = new List<CommWorldEvent>(queue.Count + 1) { evt };
        while (queue.Count > 0)
            rest.Add(queue.Dequeue());
        foreach (var pending in rest)
            queue.Enqueue(pending);
    }

    public void PushIncomingWorldEvent(in WorldEventPacket packet)
    {
        lock (_incomingWorldEventLock)
        {
            var evt = packet.ToIncomingEvent();
            if (IsLiveOwnershipWorldEvent(packet.Type))
            {
                // Hard-cap: coalesce duplicate shine/blue/story keys first; if still at
                // cap, drop oldest distinct so the new event still lands (snapshot heals
                // recover dropped bits). Never grow unbounded past MaxPendingOwnershipIncoming.
                if (_pendingOwnershipIncoming.Count >= MaxPendingOwnershipIncoming)
                {
                    TryCoalesceOldestOwnershipDuplicateUnlocked();
                    while (_pendingOwnershipIncoming.Count >= MaxPendingOwnershipIncoming)
                        _pendingOwnershipIncoming.Dequeue();
                }
                _pendingOwnershipIncoming.Enqueue(evt);
                return;
            }

            if (IsMissionWorldEvent(packet.Type))
            {
                while (_pendingMissionIncoming.Count >= MaxPendingMissionIncoming)
                {
                    if (!TryDropOldestHealableMissionIncomingUnlocked())
                        break;
                }
                _pendingMissionIncoming.Enqueue(evt);
                return;
            }

            // Phase A: hard-drop ephemeral — never enqueue fruit/react/hip-drop/gold leftovers.
            if (IsEphemeralIncomingWorldEvent(packet.Type))
                return;

            // Unknown unclassified — DropOldest into ephemeral lane.
            while (_pendingEphemeralIncoming.Count >= MaxPendingEphemeralIncoming)
                _pendingEphemeralIncoming.Dequeue();
            _pendingEphemeralIncoming.Enqueue(evt);
        }
    }

    /// <summary>
    /// Red/NPC mission bits are recoverable from progress authority heals; gold coins are not
    /// in <see cref="WorldProgressSnapshot"/>. Prefer dropping the oldest healable event.
    /// </summary>
    private static bool IsHealableMissionWorldEvent(WorldEventType type) =>
        type is WorldEventType.RedCoinCollected or WorldEventType.NpcCleaned;

    /// <summary>
    /// Drops the oldest red/NPC mission event under mission-cap pressure.
    /// Heals from <see cref="WorldProgressSnapshot"/> cover dropped bits.
    /// </summary>
    private bool TryDropOldestHealableMissionIncomingUnlocked()
    {
        if (_pendingMissionIncoming.Count == 0)
            return false;

        var kept = new List<CommWorldEvent>(_pendingMissionIncoming.Count);
        var dropped = false;
        while (_pendingMissionIncoming.Count > 0)
        {
            var pending = _pendingMissionIncoming.Dequeue();
            if (!dropped && IsHealableMissionWorldEvent(pending.Type))
            {
                dropped = true;
                continue;
            }

            kept.Add(pending);
        }

        foreach (var pending in kept)
            _pendingMissionIncoming.Enqueue(pending);
        return dropped;
    }

    /// <summary>
    /// When ownership pending is at hard-cap, drop older duplicates of the same ownership
    /// key (same shine id / blue course+idx / story flag) so newer distinct events still
    /// enqueue. Returns false if nothing could be coalesced — caller then drop-oldest.
    /// </summary>
    private bool TryCoalesceOldestOwnershipDuplicateUnlocked()
    {
        if (_pendingOwnershipIncoming.Count == 0)
            return false;

        var kept = new List<CommWorldEvent>(_pendingOwnershipIncoming.Count);
        var seen = new HashSet<string>();
        var dropped = false;
        while (_pendingOwnershipIncoming.Count > 0)
        {
            var pending = _pendingOwnershipIncoming.Dequeue();
            var key = OwnershipCoalesceKey(pending);
            if (!seen.Add(key))
            {
                // First occurrence stays; subsequent duplicates of same key are dropped.
                dropped = true;
                continue;
            }

            kept.Add(pending);
        }

        foreach (var pending in kept)
            _pendingOwnershipIncoming.Enqueue(pending);
        return dropped;
    }

    private static string OwnershipCoalesceKey(in CommWorldEvent evt) => evt.Type switch
    {
        WorldEventType.ShineCollected => $"s:{evt.Payload0}",
        WorldEventType.BlueCoinCollected => $"b:{evt.CourseId}:{evt.Payload0}",
        WorldEventType.StoryFlag => $"st:{evt.Payload1}",
        WorldEventType.SecretComplete => $"sc:{evt.Payload1}",
        WorldEventType.TriggerFlag => $"t:{evt.CourseId}:{evt.EpisodeId}:{evt.Payload1}",
        _ => $"e:{evt.EventId}",
    };

    /// <summary>
    /// Drops ephemeral remote world events from the pending queue and clears the Dolphin
    /// mission/ephemeral incoming slot only when it currently holds an ephemeral event.
    /// Ownership / mission pending (including gold) and the ownership mailbox lane are kept.
    /// </summary>
    public void ClearNonOwnershipIncomingWorldEvents()
    {
        lock (_incomingWorldEventLock)
            _pendingEphemeralIncoming.Clear();

        // Only clear the Dolphin mission slot when it is blocking on ephemeral traffic.
        if (_bridge.TryPeekIncomingWorldEvent(out var incoming) &&
            IsEphemeralIncomingWorldEvent(incoming.Type))
        {
            _bridge.TryClearIncomingWorldEvent();
            lock (_bufferLock)
            {
                if (_hasWorkingBuffer &&
                    IsEphemeralIncomingWorldEvent(_workingBuffer.WorldSync.Incoming.Type))
                {
                    _workingBuffer.WorldSync.Incoming = default;
                }
            }
        }
    }

    /// <summary>
    /// Drops all queued remote world events and clears both Dolphin incoming mailbox slots.
    /// Call on disconnect / SessionProgressReset so a stuck durable visual retry cannot keep
    /// blocking live shine/blue ownership applies after the session replaces pending state.
    /// Prefer <see cref="ClearNonOwnershipIncomingWorldEvents"/> for authority heals.
    /// </summary>
    public void ClearPendingIncomingWorldEvents()
    {
        lock (_incomingWorldEventLock)
        {
            _pendingOwnershipIncoming.Clear();
            _pendingMissionIncoming.Clear();
            _pendingEphemeralIncoming.Clear();
        }

        _bridge.TryClearIncomingOwnershipWorldEvent();
        _bridge.TryClearIncomingWorldEvent();
        lock (_bufferLock)
        {
            _stagedIncomingOwnership = default;
            _stagedIncoming = default;
            _stagedIncomingOwnershipSeenLive = false;
            _stagedIncomingSeenLive = false;
            if (_hasWorkingBuffer)
            {
                _workingBuffer.WorldSync.IncomingOwnership = default;
                _workingBuffer.WorldSync.Incoming = default;
            }
        }
    }

    /// <summary>
    /// Latest-wins compact progress heal. Module bulk-applies when hostSeq &gt; moduleAppliedSeq.
    /// Stages <c>moduleAppliedSeq=0</c> so a same-seq force-full cannot soft-skip.
    /// Clears the heal-staged latch when live reports <c>applied==0</c> (parked), a matching
    /// heal-epoch <c>applied &gt;= hostSeq</c> (fast apply), or a newer hostSeq.
    /// </summary>
    public bool PushProgressSnapshot(uint progressSeq, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ProtocolConstants.CommProgressSnapshotMaxPayload)
            return false;

        byte healEpoch;
        lock (_bufferLock)
        {
            _progressSnapshotLaneCleared = false;
            _progressSnapshotHealStaged = true;
            // Epoch 0 means "unset/legacy"; skip so stale live flags=0 cannot match.
            _progressSnapshotHealEpoch++;
            if (_progressSnapshotHealEpoch == 0)
                _progressSnapshotHealEpoch = 1;
            healEpoch = _progressSnapshotHealEpoch;
            if (_hasWorkingBuffer)
            {
                _workingBuffer.ProgressSnapshotHostSeq = progressSeq;
                // Match TryWriteProgressSnapshotOnly — pending heal must not carry a stale ack.
                _workingBuffer.ProgressSnapshotModuleAppliedSeq = 0;
                _workingBuffer.ProgressSnapshotPayloadLen = (ushort)payload.Length;
                _workingBuffer.ProgressSnapshotFlags = healEpoch;
                _workingBuffer.ProgressSnapshotPayload ??=
                    new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
                Array.Clear(_workingBuffer.ProgressSnapshotPayload, 0,
                    _workingBuffer.ProgressSnapshotPayload.Length);
                payload.CopyTo(_workingBuffer.ProgressSnapshotPayload.AsSpan(0, payload.Length));
            }
        }

        return _bridge.TryWriteProgressSnapshotOnly(progressSeq, payload, healEpoch);
    }

    public void ClearProgressSnapshot()
    {
        lock (_bufferLock)
        {
            if (_hasWorkingBuffer)
                ClearProgressSnapshotFields_NoLock();
            _progressSnapshotLaneCleared = true;
            _progressSnapshotHealStaged = false;
        }

        _bridge.TryClearProgressSnapshot();
    }

    private void ClearProgressSnapshotFields_NoLock()
    {
        _workingBuffer.ProgressSnapshotHostSeq = 0;
        _workingBuffer.ProgressSnapshotModuleAppliedSeq = 0;
        _workingBuffer.ProgressSnapshotPayloadLen = 0;
        _workingBuffer.ProgressSnapshotFlags = 0;
        if (_workingBuffer.ProgressSnapshotPayload != null)
            Array.Clear(_workingBuffer.ProgressSnapshotPayload, 0,
                _workingBuffer.ProgressSnapshotPayload.Length);
    }

    /// <summary>
    /// Before a full CommBuffer write: keep an intentional force-clear, preserve a just-staged
    /// heal's moduleApplied=0 against stale Dolphin acks, otherwise prefer the higher live
    /// hostSeq so a just-pushed heal is not overwritten by a stale working copy.
    /// </summary>
    private void MergeProgressSnapshotLaneFromLive_NoLock(in CommBuffer live)
    {
        if (_progressSnapshotLaneCleared)
        {
            // Keep the intentional clear — do not adopt live hostSeq/moduleApplied or a
            // full write will resurrect the pre-clear heal and the next push with the same
            // authority seq will no-op (hostSeq <= moduleAppliedSeq).
            ClearProgressSnapshotFields_NoLock();
            return;
        }

        if (_progressSnapshotHealStaged)
        {
            var stagedHost = _workingBuffer.ProgressSnapshotHostSeq;
            if (live.ProgressSnapshotHostSeq > stagedHost &&
                live.ProgressSnapshotPayloadLen > 0 &&
                live.ProgressSnapshotPayload != null)
            {
                _progressSnapshotHealStaged = false;
                _workingBuffer.ProgressSnapshotModuleAppliedSeq =
                    live.ProgressSnapshotModuleAppliedSeq;
                _workingBuffer.ProgressSnapshotHostSeq = live.ProgressSnapshotHostSeq;
                _workingBuffer.ProgressSnapshotPayloadLen = live.ProgressSnapshotPayloadLen;
                _workingBuffer.ProgressSnapshotFlags = live.ProgressSnapshotFlags;
                _workingBuffer.ProgressSnapshotPayload ??=
                    new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
                Array.Clear(_workingBuffer.ProgressSnapshotPayload, 0,
                    _workingBuffer.ProgressSnapshotPayload.Length);
                var newerLen = Math.Min(live.ProgressSnapshotPayloadLen,
                    live.ProgressSnapshotPayload.Length);
                newerLen = Math.Min(newerLen, _workingBuffer.ProgressSnapshotPayload.Length);
                Buffer.BlockCopy(live.ProgressSnapshotPayload, 0,
                    _workingBuffer.ProgressSnapshotPayload, 0, newerLen);
                return;
            }

            if (live.ProgressSnapshotHostSeq == stagedHost && stagedHost != 0)
            {
                if (live.ProgressSnapshotModuleAppliedSeq == 0)
                {
                    // Push write visible with intentional zero — further acks are trustworthy.
                    _progressSnapshotHealStaged = false;
                }
                else if (live.ProgressSnapshotModuleAppliedSeq >= stagedHost &&
                         live.ProgressSnapshotFlags == _workingBuffer.ProgressSnapshotFlags &&
                         _workingBuffer.ProgressSnapshotFlags != 0)
                {
                    // Fast apply before poll saw applied=0, but live still carries this Push's
                    // epoch. Do NOT adopt a pre-push stale applied>=host (flags mismatch/0).
                    _progressSnapshotHealStaged = false;
                    _workingBuffer.ProgressSnapshotModuleAppliedSeq =
                        live.ProgressSnapshotModuleAppliedSeq;
                }
            }

            // Keep working hostSeq / payload. Mid-apply (0 < applied < host) stays staged
            // with moduleApplied=0 so the module retries.
            return;
        }

        _workingBuffer.ProgressSnapshotModuleAppliedSeq =
            live.ProgressSnapshotModuleAppliedSeq;
        if (live.ProgressSnapshotHostSeq > _workingBuffer.ProgressSnapshotHostSeq &&
            live.ProgressSnapshotPayloadLen > 0 &&
            live.ProgressSnapshotPayload != null)
        {
            _workingBuffer.ProgressSnapshotHostSeq = live.ProgressSnapshotHostSeq;
            _workingBuffer.ProgressSnapshotPayloadLen = live.ProgressSnapshotPayloadLen;
            _workingBuffer.ProgressSnapshotFlags = live.ProgressSnapshotFlags;
            _workingBuffer.ProgressSnapshotPayload ??=
                new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
            Array.Clear(_workingBuffer.ProgressSnapshotPayload, 0,
                _workingBuffer.ProgressSnapshotPayload.Length);
            var len = Math.Min(live.ProgressSnapshotPayloadLen,
                live.ProgressSnapshotPayload.Length);
            len = Math.Min(len, _workingBuffer.ProgressSnapshotPayload.Length);
            Buffer.BlockCopy(live.ProgressSnapshotPayload, 0,
                _workingBuffer.ProgressSnapshotPayload, 0, len);
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

    /// <summary>
    /// Progress mailbox ack pair used by periodic catch-up. Refreshes from live Dolphin when
    /// attached so <c>moduleAppliedSeq</c> reflects what the module has actually bulk-applied,
    /// not merely what the launcher last pushed.
    /// </summary>
    public bool TryGetProgressSnapshotAck(out uint hostSeq, out uint moduleAppliedSeq)
    {
        lock (_bufferLock)
        {
            if (_bridge.IsAttached && _bridge.TryReadBuffer(out var buffer) &&
                buffer.Magic == ProtocolConstants.Magic)
                AdoptLiveBufferPreservingBridgeState_NoLock(buffer);

            if (!_hasWorkingBuffer)
            {
                hostSeq = 0;
                moduleAppliedSeq = 0;
                return false;
            }

            hostSeq = _workingBuffer.ProgressSnapshotHostSeq;
            moduleAppliedSeq = _workingBuffer.ProgressSnapshotModuleAppliedSeq;
        return true;
        }
    }

    /// <summary>
    /// Re-write a still-pending progress heal (<c>hostSeq &gt; moduleAppliedSeq</c>) so the
    /// module retries bulk-apply without a TCP round-trip. Returns false when there is no
    /// pending payload to push, or when <see cref="PushProgressSnapshot"/> fails to write
    /// Dolphin RAM — callers must escalate to force-full rather than treating a write miss
    /// as a successful re-push (which would skip catch-up for the full interval).
    /// </summary>
    public bool TryRepushPendingProgressSnapshot()
    {
        byte[] payload;
        uint hostSeq;
        lock (_bufferLock)
        {
            if (_bridge.IsAttached && _bridge.TryReadBuffer(out var buffer) &&
                buffer.Magic == ProtocolConstants.Magic)
                AdoptLiveBufferPreservingBridgeState_NoLock(buffer);

            if (!_hasWorkingBuffer)
                return false;

            hostSeq = _workingBuffer.ProgressSnapshotHostSeq;
            var moduleAppliedSeq = _workingBuffer.ProgressSnapshotModuleAppliedSeq;
            if (hostSeq == 0 || hostSeq <= moduleAppliedSeq)
                return false;

            var len = _workingBuffer.ProgressSnapshotPayloadLen;
            if (len == 0 || _workingBuffer.ProgressSnapshotPayload == null)
                return false;

            len = Math.Min(len, (ushort)_workingBuffer.ProgressSnapshotPayload.Length);
            if (len == 0)
                return false;

            payload = new byte[len];
            Buffer.BlockCopy(_workingBuffer.ProgressSnapshotPayload, 0, payload, 0, len);
        }

        // Only report success when Dolphin actually received the restaged heal. A false
        // positive here stalls MaybeRequestProgressCatchup from force-full escalation.
        return PushProgressSnapshot(hostSeq, payload);
    }

    /// <summary>Test hook: ordered types currently waiting (ownership, then mission, then ephemeral).</summary>
    internal WorldEventType[] DebugGetPendingIncomingTypes()
    {
        lock (_incomingWorldEventLock)
        {
            var types = new WorldEventType[
                _pendingOwnershipIncoming.Count +
                _pendingMissionIncoming.Count +
                _pendingEphemeralIncoming.Count];
            var i = 0;
            foreach (var e in _pendingOwnershipIncoming)
                types[i++] = e.Type;
            foreach (var e in _pendingMissionIncoming)
                types[i++] = e.Type;
            foreach (var e in _pendingEphemeralIncoming)
                types[i++] = e.Type;
            return types;
        }
    }

    /// <summary>Test hook: seed world-sync mailbox fields without Dolphin attached.</summary>
    internal void DebugSeedWorldSync(uint lastAppliedEventId, uint incomingEventId = 0,
        uint ownershipIncomingEventId = 0)
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            _workingBuffer.WorldSync.LastAppliedEventId = lastAppliedEventId;
            if (ownershipIncomingEventId != 0)
            {
                _workingBuffer.WorldSync.IncomingOwnership =
                    new CommWorldEvent { EventId = ownershipIncomingEventId };
            }

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
    /// Test hook: backdate the stage-exit hold arm timestamp so MaxDuration logic can be
    /// exercised without real-time sleeps.
    /// </summary>
    internal void DebugBackdateHoldRemotePublishSinceUtc(TimeSpan age)
    {
        lock (_bufferLock)
        {
            if (_holdRemotePublishSinceUtc == DateTime.MinValue)
                _holdRemotePublishSinceUtc = DateTime.UtcNow;
            _holdRemotePublishSinceUtc -= age;
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

    /// <summary>
    /// Test hook: run the progress-lane merge used immediately before a full CommBuffer write.
    /// </summary>
    internal void DebugMergeProgressLaneFromLive(in CommBuffer live)
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            MergeProgressSnapshotLaneFromLive_NoLock(live);
        }
    }

    /// <summary>
    /// Test hook: same empty-live adopt + staged splice used by <see cref="TryWriteWorkingBuffer"/>.
    /// </summary>
    internal void DebugAdoptIncomingLanesFromLive(
        in CommWorldEvent liveOwnership,
        in CommWorldEvent liveIncoming,
        uint lastAppliedEventId = 0)
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            _workingBuffer.WorldSync.IncomingOwnership = liveOwnership;
            _workingBuffer.WorldSync.Incoming = liveIncoming;
            _workingBuffer.WorldSync.LastAppliedEventId = lastAppliedEventId;
            var live = _workingBuffer;
            live.WorldSync.IncomingOwnership = liveOwnership;
            live.WorldSync.Incoming = liveIncoming;
            live.WorldSync.LastAppliedEventId = lastAppliedEventId;
            SpliceStagedIncomingLanesFromLive_NoLock(live);
        }
    }

    /// <summary>Test hook: mark an ownership/mission event as Drain-staged for splice tests.</summary>
    internal void DebugStageIncomingForSplice(in CommWorldEvent ownership, in CommWorldEvent mission)
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            if (ownership.EventId != 0)
            {
                _stagedIncomingOwnership = ownership;
                _stagedIncomingOwnershipSeenLive = false;
                _workingBuffer.WorldSync.IncomingOwnership = ownership;
            }

            if (mission.EventId != 0)
            {
                _stagedIncoming = mission;
                _stagedIncomingSeenLive = false;
                _workingBuffer.WorldSync.Incoming = mission;
            }
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

    /// <summary>
    /// After always-adopting live Incoming lanes (including empty): keep a just-Drained
    /// partial write visible across the full buffer write when live has not reflected it
    /// yet, without resurrecting a stale event after the module has cleared the slot.
    /// </summary>
    private void SpliceStagedIncomingLanesFromLive_NoLock(in CommBuffer live)
    {
        UpdateStagedIncomingLane_NoLock(
            live.WorldSync.IncomingOwnership,
            live.WorldSync.LastAppliedEventId,
            ref _stagedIncomingOwnership,
            ref _stagedIncomingOwnershipSeenLive,
            ref _workingBuffer.WorldSync.IncomingOwnership);

        UpdateStagedIncomingLane_NoLock(
            live.WorldSync.Incoming,
            live.WorldSync.LastAppliedEventId,
            ref _stagedIncoming,
            ref _stagedIncomingSeenLive,
            ref _workingBuffer.WorldSync.Incoming);
    }

    private static void UpdateStagedIncomingLane_NoLock(
        in CommWorldEvent liveSlot,
        uint lastAppliedEventId,
        ref CommWorldEvent staged,
        ref bool seenLive,
        ref CommWorldEvent workingSlot)
    {
        if (staged.EventId == 0)
            return;

        if (liveSlot.EventId == staged.EventId)
        {
            // Partial write visible — further clears must be adopted, not spliced.
            seenLive = true;
            staged = default;
            return;
        }

        if (liveSlot.EventId != 0)
        {
            // Different live event occupies the slot — abandon staging.
            seenLive = false;
            staged = default;
            return;
        }

        // live empty: module may have applied before we observed the non-empty slot.
        if (seenLive || lastAppliedEventId >= staged.EventId)
        {
            staged = default;
            seenLive = false;
            return;
        }

        // Write not yet visible and not applied — splice so the full write does not undo Drain.
        workingSlot = staged;
    }

    private bool TryWriteWorkingBuffer()
    {
        if (!_bridge.IsAttached)
            return false;

        CommBuffer copy;
        lock (_bufferLock)
        {
            MergeMarioModelIdsIntoWorkingBuffer_NoLock();

            // Full-buffer writes must not stomp module handshake lanes.
            // Dual localPending (ownership + mission) are module→bridge only: ALWAYS mirror
            // live, including empty. Incoming / IncomingOwnership are bridge→module: same
            // empty-live adopt + staged splice. When Drain just partial-wrote and live still
            // reads empty, splice the staged event back.
            if (_bridge.TryReadBuffer(out var live) && live.Magic == ProtocolConstants.Magic)
            {
                _workingBuffer.WorldSync.LocalPendingOwnership =
                    live.WorldSync.LocalPendingOwnership;
                _workingBuffer.WorldSync.LocalPendingMission =
                    live.WorldSync.LocalPendingMission;
                _workingBuffer.WorldSync.IncomingOwnership = live.WorldSync.IncomingOwnership;
                _workingBuffer.WorldSync.Incoming = live.WorldSync.Incoming;
                _workingBuffer.WorldSync.LastAppliedEventId = live.WorldSync.LastAppliedEventId;
                SpliceStagedIncomingLanesFromLive_NoLock(live);
                MergeProgressSnapshotLaneFromLive_NoLock(live);
            }
            else
            {
                // Cannot confirm live — never risk resurrecting module-cleared events or
                // writing a stale moduleApplied over a staged heal / intentional clear.
                _workingBuffer.WorldSync.LocalPendingOwnership = default;
                _workingBuffer.WorldSync.LocalPendingMission = default;
                _workingBuffer.WorldSync.IncomingOwnership = default;
                _workingBuffer.WorldSync.Incoming = default;
                ProtectProgressLaneOnReadMiss_NoLock();
            }

            copy = _workingBuffer;
        }

        return _bridge.TryWriteBuffer(copy);
    }

    /// <summary>
    /// On TryReadBuffer miss, keep progress-lane clear/heal-staged policy consistent so a
    /// full write cannot restore moduleApplied &gt;= hostSeq and skip heals for the run.
    /// </summary>
    private void ProtectProgressLaneOnReadMiss_NoLock()
    {
        if (_progressSnapshotLaneCleared)
        {
            ClearProgressSnapshotFields_NoLock();
            return;
        }

        if (_progressSnapshotHealStaged)
        {
            // Keep intentional moduleApplied=0 + staged payload; do not adopt unknown live.
            _workingBuffer.ProgressSnapshotModuleAppliedSeq = 0;
        }
    }

    /// <summary>
    /// Test hook: apply the TryReadBuffer-miss policy used by <see cref="TryWriteWorkingBuffer"/>.
    /// </summary>
    internal void DebugApplyReadMissFullWritePolicy()
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            _workingBuffer.WorldSync.LocalPendingOwnership = default;
            _workingBuffer.WorldSync.LocalPendingMission = default;
            _workingBuffer.WorldSync.IncomingOwnership = default;
            _workingBuffer.WorldSync.Incoming = default;
            ProtectProgressLaneOnReadMiss_NoLock();
        }
    }

    /// <summary>Test hook: mark progress heal as staged (moduleApplied must stay 0).</summary>
    internal void DebugMarkProgressHealStaged(uint hostSeq, ushort payloadLen = 1)
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            _progressSnapshotLaneCleared = false;
            _progressSnapshotHealStaged = true;
            _progressSnapshotHealEpoch++;
            if (_progressSnapshotHealEpoch == 0)
                _progressSnapshotHealEpoch = 1;
            _workingBuffer.ProgressSnapshotHostSeq = hostSeq;
            _workingBuffer.ProgressSnapshotModuleAppliedSeq = 0;
            _workingBuffer.ProgressSnapshotPayloadLen = payloadLen;
            _workingBuffer.ProgressSnapshotFlags = _progressSnapshotHealEpoch;
            _workingBuffer.ProgressSnapshotPayload ??=
                new byte[ProtocolConstants.CommProgressSnapshotMaxPayload];
            if (payloadLen > 0)
                _workingBuffer.ProgressSnapshotPayload[0] = 1;
        }
    }

    /// <summary>Test hook: mark progress lane as force-cleared.</summary>
    internal void DebugMarkProgressLaneCleared()
    {
        lock (_bufferLock)
        {
            EnsureWorkingBuffer();
            ClearProgressSnapshotFields_NoLock();
            _progressSnapshotLaneCleared = true;
            _progressSnapshotHealStaged = false;
        }
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
