using System.Linq;
using SMSO.Net;

namespace SMSO.Bridge;

public sealed class BridgeWorker : IDisposable
{
    private readonly DolphinBridge _bridge;
    private readonly RemoteInterpolation _interpolation = new();
    private readonly object _bufferLock = new();
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
    private PlayerSnapshot[]? _cachedRemoteSnapshots;
    private byte _lastRestoredStageId;
    private byte _lastRestoredEpisodeId;
    private ushort _lastLocalMarioVoiceSequence;
    private GameModeStatePacket _gameModeState = GameModeStatePacket.CreateDefault();
    private ushort _lastGameModeSeq;
    private NameTagAppearance _savedLocalAppearance = NameTagAppearance.CreateDefault();
    private readonly Dictionary<byte, NameTagAppearance> _savedRemoteAppearances = new();

    public GameModeStatePacket CurrentGameModeState => _gameModeState.Clone();

    public event Action<CommBuffer>? BufferUpdated;
    public event Action<PlayerSnapshot>? LocalSnapshotReady;
    public event Action<MarioVoiceEvent>? LocalMarioVoiceReady;
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
        lock (_bufferLock)
        {
            if (_bridge.TryReadBuffer(out var live))
            {
                _workingBuffer = live;
                _hasWorkingBuffer = true;
            }
            else
            {
                EnsureWorkingBuffer();
            }

            if (connected)
                _workingBuffer.BridgeFlags |= BridgeFlags.Connected;
            else
                _workingBuffer.BridgeFlags &= ~BridgeFlags.Connected;

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
        FlushInterpolatedRemotes(force: true);
    }

    public void PushRemoteMarioVoiceEvent(byte slot, in MarioVoiceEvent voiceEvent)
    {
        if (slot >= ProtocolConstants.MaxRemoteSlots || voiceEvent.IsEmpty)
            return;

        MarioVoiceEvent[] remoteCopy;
        lock (_bufferLock)
        {
            if (!_remoteRaw.ContainsKey(slot))
                return;

            _remoteMarioVoiceEvents[slot] = voiceEvent;
            remoteCopy = (MarioVoiceEvent[])_remoteMarioVoiceEvents.Clone();
            if (_hasWorkingBuffer && _workingBuffer.RemoteMarioVoiceEvents != null)
                _workingBuffer.RemoteMarioVoiceEvents[slot] = voiceEvent;
        }

        if (!_bridge.TryWriteRemoteMarioVoiceEventsOnly(remoteCopy))
            Log?.Invoke("Failed to write remote Mario voice events to Dolphin");
    }

    public void FlushRemoteSnapshotsToDolphin() => FlushInterpolatedRemotes(force: true);

    public void ApplyLocalNameTagAppearance(string username, in NameTagAppearance appearance)
    {
        lock (_bufferLock)
        {
            if (_bridge.TryReadBuffer(out var live))
            {
                _workingBuffer = live;
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

            if (_bridge.TryReadBuffer(out var live))
            {
                _workingBuffer = live;
                _hasWorkingBuffer = true;
            }
            else
            {
                EnsureWorkingBuffer();
            }

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

        if (!_bridge.TryWriteGameModeStateOnly(GameModeStatePacket.ToCommGameMode(localSlot, _gameModeState)))
            Log?.Invoke("Failed to write game mode state to Dolphin");

        if (_gameModeState.GameMode == GameMode.HideSeek)
            FlushInterpolatedRemotes(force: true);
        else
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

    private void FlushInterpolatedRemotes(bool force = false)
    {
        if (!_bridge.IsAttached)
            return;

        PlayerSnapshot[] remoteCopy;
        NameTagAppearance localAppearance;
        NameTagAppearance[] remoteAppearances;
        MarioVoiceEvent[] remoteVoiceEvents;
        CommGameModeState commGameMode;
        lock (_bufferLock)
        {
            if (!force && _remoteRaw.Count == 0 && _gameModeState.GameMode != GameMode.HideSeek)
                return;

            if (_bridge.TryReadBuffer(out var live))
            {
                _workingBuffer = live;
                _hasWorkingBuffer = true;
            }
            else if (!EnsureWorkingBuffer())
            {
                return;
            }

            for (int i = 0; i < _workingBuffer.RemoteSnapshots.Length; i++)
            {
                _workingBuffer.RemoteSnapshots[i] = new PlayerSnapshot { Name = new byte[16], Connected = 0 };
            }

            for (int i = 0; i < _workingBuffer.RemoteNameTagAppearances.Length; i++)
                _workingBuffer.RemoteNameTagAppearances[i] = default;

            foreach (var slot in _remoteRaw.Keys.OrderBy(s => s))
            {
                if (slot == _workingBuffer.LocalSlot)
                    continue;

                PlayerSnapshot snap;
                if (_interpolation.HasSlot(slot))
                {
                    snap = _interpolation.Advance(slot);
                }
                else if (_remoteRaw.TryGetValue(slot, out var raw))
                {
                    snap = raw;
                }
                else
                {
                    continue;
                }

                snap.Connected = 1;
                snap.Slot = slot;
                if (slot < _workingBuffer.RemoteSnapshots.Length)
                    _workingBuffer.RemoteSnapshots[slot] = snap;
                if (_remoteAppearances.TryGetValue(slot, out var appearance))
                    _workingBuffer.RemoteNameTagAppearances[slot] = appearance;
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
            remoteVoiceEvents = (MarioVoiceEvent[])_remoteMarioVoiceEvents.Clone();
        }

        if (!_bridge.TryWriteRemoteSnapshotsOnly(remoteCopy))
            Log?.Invoke("Failed to write remote player snapshots to Dolphin");

        if (!_bridge.TryWriteNameTagAppearancesOnly(localAppearance, remoteAppearances))
            Log?.Invoke("Failed to write remote name tag appearances to Dolphin");

        if (!_bridge.TryWriteGameModeStateOnly(commGameMode))
            Log?.Invoke("Failed to write game mode state to Dolphin");

        if (!_bridge.TryWriteRemoteMarioVoiceEventsOnly(remoteVoiceEvents))
            Log?.Invoke("Failed to write remote Mario voice events to Dolphin");
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

        TryWriteWorkingBuffer();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_dolphinRunning)
                    {
                        if (!_bridge.IsAttached)
                            _bridge.TryAttach();

                        UpdateLinkStateFromBridge();

                        if (_bridge.TryReadBuffer(out var buffer) && buffer.Magic == ProtocolConstants.Magic)
                        {
                            lock (_bufferLock)
                            {
                                _workingBuffer = buffer;
                                _hasWorkingBuffer = true;
                            }

                            BufferUpdated?.Invoke(buffer);
                            if (buffer.LocalSnapshot.Connected != 0 || (buffer.BridgeFlags & BridgeFlags.Connected) != 0)
                                LocalSnapshotReady?.Invoke(buffer.LocalSnapshot);
                            MaybePublishLocalMarioVoice(buffer.LocalMarioVoiceEvent);

                            MaybeRestoreRemoteSnapshotsAfterStageChange(buffer);
                        }

                        FlushInterpolatedRemotes();
                        TryApplyPendingWarp();
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Bridge poll error: {ex.Message}");
                }

                await Task.Delay(_dolphinRunning ? ProtocolConstants.BridgePollMs : 250, ct);
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
    }

    private void UpdateLinkStateFromBridge()
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

        if (_bridge.TryReadBuffer(out var buffer) && buffer.Magic == ProtocolConstants.Magic)
            SetLinkState(DolphinLinkState.ModuleReady);
        else
            SetLinkState(DolphinLinkState.Attached);

        TryFlushPendingConnectionWrite();
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
            if (_bridge.TryReadBuffer(out var live))
            {
                _workingBuffer = live;
                _hasWorkingBuffer = true;
            }
        }

        Log?.Invoke($"Warp sent to Dolphin: course={courseId} episode={episodeId}");
        return true;
    }

    private bool EnsureWorkingBuffer()
    {
        if (_hasWorkingBuffer) return true;
        if (_bridge.TryReadBuffer(out var buffer))
        {
            _workingBuffer = buffer;
            _hasWorkingBuffer = true;
            return true;
        }

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

    private bool TryWriteWorkingBuffer()
    {
        CommBuffer copy;
        lock (_bufferLock)
            copy = _workingBuffer;

        if (!_bridge.IsAttached)
            return false;

        if (!_bridge.TryWriteBuffer(copy))
            return false;

        UpdateLinkStateFromBridge();
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
