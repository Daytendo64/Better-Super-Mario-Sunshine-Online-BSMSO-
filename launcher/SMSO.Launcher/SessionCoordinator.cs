using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SMSO.Bridge;
using SMSO.Net;
using SMSO.Server;

namespace SMSO.Launcher;

public sealed class SessionCoordinator : IDisposable
{
    private readonly ConfigService _config;
    private int InstanceIndex => _config.InstanceIndex;
    private readonly DolphinBridge _bridge = new();
    private readonly BridgeWorker _bridgeWorker;
    private readonly DolphinProcessMonitor _monitor = new();
    private GameServer? _server;
    private NetClient? _client;
    private LevelCatalog? _levels;
    private readonly Dictionary<byte, PlayerSnapshot> _remoteSnapshots = new();
    private readonly HashSet<byte> _activeRosterSlots = new();
    private readonly Dictionary<byte, int> _rosterMissStrikes = new();
    private readonly object _sessionLock = new();
    private PlayerRosterEntry[] _roster = Array.Empty<PlayerRosterEntry>();
    private bool _sessionHasSeenRoster;
    private volatile bool _shuttingDown;
    private int _networkCleanupGate;
    private int _dolphinStopHandling;
    private DisconnectReason? _pendingDisconnectReason;
    private PlayerSnapshot _lastLocalSnapshot;
    private bool _hasLastLocalSnapshot;

    public event Action<string>? StatusChanged;
    public event Action<string>? Log;
    public event Action<PlayerRosterEntry[]>? RosterUpdated;
    public event Action? HostingStateChanged;
    public event Action? ClientTeleportPolicyChanged;
    public event Action? DolphinClosed;
    public event Action<GameModeStatePacket>? GameModeStateChanged;
    public event Action<DolphinLinkState>? DolphinLinkStateChanged;

    public bool IsHosting => _server?.IsRunning == true;
    public bool IsConnected => _client?.IsConnected == true;
    public bool AllowClientTeleport { get; private set; }
    public bool ClientTeleportPolicyKnown { get; private set; }
    public bool CanUseClientTeleport => IsHosting || AllowClientTeleport;
    public GameModeStatePacket GameModeState => _bridgeWorker.CurrentGameModeState;
    public byte LocalSlot => _client?.AssignedSlot ?? 0;
    public bool IsDolphinRunning => _monitor.IsDolphinRunning;
    public DolphinLinkState DolphinLinkState => _bridgeWorker.LinkState;
    public string? DolphinLinkError => _bridgeWorker.LastDolphinLinkError;
    public TimeSpan DolphinMailboxSearchDuration => _bridge.MailboxSearchDuration;

    public SessionCoordinator(ConfigService config)
    {
        _config = config;
        _bridgeWorker = new BridgeWorker(_bridge);
        _bridge.Log += m => Log?.Invoke(m);
        _bridgeWorker.Log += m => Log?.Invoke(m);
        _bridgeWorker.LinkStateChanged += state => DolphinLinkStateChanged?.Invoke(state);
        _monitor.Log += m => Log?.Invoke(m);
        _monitor.DolphinStopped += OnDolphinStopped;
        _monitor.DolphinStarted += OnDolphinStarted;

        _bridgeWorker.LocalSnapshotReady += OnLocalSnapshot;
        _bridgeWorker.LocalMarioVoiceReady += OnLocalMarioVoice;
    }

    public void Initialize(string levelsPath)
    {
        if (File.Exists(levelsPath))
            _levels = LevelCatalog.Load(levelsPath);
        else
            _levels = new LevelCatalog();

        ApplyDolphinPathsFromConfig();
        _monitor.Start();
        _bridgeWorker.NotifyDolphinRunning(_monitor.IsDolphinRunning);
        _bridgeWorker.Start();
    }

    public async Task HostAsync()
    {
        if (_levels == null)
            throw new InvalidOperationException("Level data is not loaded.");

        ValidateUsername(_config.Config.Username);

        StatusChanged?.Invoke("Starting server...");
        await DisconnectAsync().ConfigureAwait(true);

        try
        {
            var port = _config.Config.ServerPort;
            var levels = _levels;
            var maxPlayers = _config.Config.MaxPlayers;

            await Task.Run(() =>
            {
                _server = new GameServer(levels) { MaxPlayers = maxPlayers };
                _server.Log += m => Log?.Invoke(m);
                _server.Start(port);
                _config.Config.AllowClientTeleporting = false;
                _server.SetAllowClientTeleport(false);
            }).ConfigureAwait(true);

            await ConnectClientAsync("127.0.0.1", port, isHost: true).ConfigureAwait(true);

            HostingStateChanged?.Invoke();
            Log?.Invoke($"Hosting on port {port}");
        }
        catch (SocketException ex)
        {
            StopServer();
            throw new InvalidOperationException(
                $"Port {_config.Config.ServerPort} is already in use or blocked ({ex.SocketErrorCode}).", ex);
        }
        catch
        {
            StopServer();
            throw;
        }
    }

    public async Task ConnectAsync()
    {
        if (IsHosting)
        {
            Log?.Invoke("Already hosting — use Disconnect first to join another server.");
            return;
        }

        ValidateUsername(_config.Config.Username);
        await ConnectClientAsync(_config.Config.ServerIp, _config.Config.ServerPort, isHost: false);
    }

    private async Task ConnectClientAsync(string host, int port, bool isHost)
    {
        StopClientOnly();
        AllowClientTeleport = false;
        ClientTeleportPolicyKnown = false;

        var client = new NetClient();
        client.Log += m => Log?.Invoke(m);
        client.JoinRejected += reason =>
        {
            if (reason == JoinRejectReason.NameTaken)
                Log?.Invoke($"Join rejected: username '{_config.Config.Username}' is already in use — set a unique name in Settings (e.g. Player{InstanceIndex + 1})");
            else
                Log?.Invoke($"Join rejected: {reason}");
        };
        client.JoinAccepted += () =>
        {
            try
            {
                _bridgeWorker.SetConnected(true, client.AssignedSlot, _config.Config.Username, isHost);
                ApplyConfiguredSyncSettings();
                RefreshPlayerAppearance();
                StatusChanged?.Invoke(isHost ? "Hosting" : "Connected");
                Log?.Invoke(isHost
                    ? $"Joined own server as slot {client.AssignedSlot}"
                    : $"Connected as slot {client.AssignedSlot}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Join setup error: {ex.Message}");
            }
        };
        client.RosterUpdated += OnRosterUpdated;
        client.WarpCommandReceived += OnWarpCommand;
        client.SnapshotReceived += OnSnapshotReceived;
        client.MarioVoiceEventReceived += OnMarioVoiceEventReceived;
        client.SyncSettingsReceived += (f, o, p) => _bridgeWorker.ApplySyncSettings(f, o, p);
        client.ClientTeleportSettingsReceived += allowed =>
        {
            AllowClientTeleport = allowed;
            ClientTeleportPolicyKnown = true;
            ClientTeleportPolicyChanged?.Invoke();
        };
        client.GameModeStateReceived += state => ApplyGameModeState(state);
        client.Disconnected += reason =>
        {
            if (_shuttingDown)
                return;

            if (_networkCleanupGate != 0)
            {
                _pendingDisconnectReason = reason;
                return;
            }

            Log?.Invoke($"Disconnected: {reason}");
            CleanupNetworkSession(stopServer: isHost && !_shuttingDown, updateStatus: true);
        };

        _client = client;
        StatusChanged?.Invoke("Connecting");
        try
        {
            await client.ConnectAsync(host, port, _config.Config.Username).ConfigureAwait(true);
        }
        catch (NetJoinRejectedException ex)
        {
            CleanupNetworkSession(stopServer: isHost, updateStatus: true);
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch
        {
            CleanupNetworkSession(stopServer: isHost, updateStatus: true);
            throw;
        }
    }

    public async Task DisconnectAsync(DisconnectReason reason = DisconnectReason.UserRequest)
    {
        if (!TryEnterNetworkCleanup())
            return;

        try
        {
            ResetHideSeekIfActiveOnServer();

            if (_client != null)
            {
                try
                {
                    await _client.DisconnectAsync(reason).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    Log?.Invoke("Disconnect timed out — forcing local cleanup");
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Client disconnect error: {ex.Message}");
                }
            }

            StopClientOnly();
            StopServer();
            _bridgeWorker.SetConnected(false, 0, "", false);
            ForceGameModeToNormalLocally();
            _remoteSnapshots.Clear();
            _activeRosterSlots.Clear();
            _rosterMissStrikes.Clear();
            _sessionHasSeenRoster = false;
            _roster = Array.Empty<PlayerRosterEntry>();
            StatusChanged?.Invoke("Disconnected");
        }
        finally
        {
            ExitNetworkCleanup();
        }
    }

    private void StopServer()
    {
        if (_server == null) return;
        try
        {
            if (_server.IsRunning)
            {
                _server.NotifyShutdown();
                Thread.Sleep(75);
            }
        }
        catch { /* ignore */ }
        _server.Stop();
        _server.Dispose();
        _server = null;
        HostingStateChanged?.Invoke();
    }

    private void StopClientOnly()
    {
        var client = _client;
        _client = null;
        if (client != null)
        {
            try
            {
                client.DisconnectAsync(DisconnectReason.UserRequest)
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // socket may already be closed
            }
        }

        _remoteSnapshots.Clear();
        _activeRosterSlots.Clear();
        _rosterMissStrikes.Clear();
        _sessionHasSeenRoster = false;
        _bridgeWorker.ClearRemoteSnapshots();
        try
        {
            _bridgeWorker.FlushRemoteSnapshotsToDolphin();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Remote snapshot flush skipped: {ex.Message}");
        }
    }

    private void CleanupNetworkSession(bool stopServer, bool updateStatus)
    {
        if (!TryEnterNetworkCleanup())
            return;

        try
        {
            ResetHideSeekIfActiveOnServer();
            StopClientOnly();
            if (stopServer)
                StopServer();
            _bridgeWorker.SetConnected(false, 0, "", false);
            ForceGameModeToNormalLocally();
            AllowClientTeleport = false;
            ClientTeleportPolicyKnown = false;
            _remoteSnapshots.Clear();
            _activeRosterSlots.Clear();
            _rosterMissStrikes.Clear();
            _sessionHasSeenRoster = false;
            _roster = Array.Empty<PlayerRosterEntry>();
            ClientTeleportPolicyChanged?.Invoke();
            if (updateStatus)
                StatusChanged?.Invoke("Disconnected");
        }
        finally
        {
            ExitNetworkCleanup();
        }
    }

    private bool TryEnterNetworkCleanup() =>
        Interlocked.CompareExchange(ref _networkCleanupGate, 1, 0) == 0;

    private void ExitNetworkCleanup()
    {
        Interlocked.Exchange(ref _networkCleanupGate, 0);

        var pending = _pendingDisconnectReason;
        if (!pending.HasValue || _shuttingDown)
            return;

        _pendingDisconnectReason = null;
        Log?.Invoke($"Disconnected: {pending.Value}");
        CleanupNetworkSession(stopServer: IsHosting && !_shuttingDown, updateStatus: true);
    }

    private void ApplyConfiguredSyncSettings()
    {
        var syncFlags = _config.Config.SyncFlags;
        var syncObjects = _config.Config.SyncObjects;
        var syncProgress = _config.Config.SyncProgress;
        _server?.SetSyncSettings(syncFlags, syncObjects, syncProgress);
        _bridgeWorker.ApplySyncSettings(syncFlags, syncObjects, syncProgress);
    }

    public bool TryReconnectDolphin()
    {
        ApplyDolphinPathsFromConfig();
        _bridgeWorker.NotifyDolphinRunning(_monitor.IsDolphinRunning);

        if (!_monitor.IsDolphinRunning || !_monitor.TrackedProcessId.HasValue)
        {
            Log?.Invoke("No Dolphin instance for this launcher — use Launch Dolphin first");
            return false;
        }

        _bridge.SetTrackedProcessId(_monitor.TrackedProcessId);
        if (_bridge.ForceRelink())
        {
            Log?.Invoke("Reconnected to Dolphin memory");
            return true;
        }

        Log?.Invoke(_bridge.LastResolveError ?? "Failed to attach to Dolphin — try running the launcher as administrator");
        return false;
    }

    public bool TryLaunchDolphin(string dolphinPath, string isoPath, out string? error)
    {
        if (_monitor.IsDolphinRunning)
        {
            error = "Dolphin is already running for this launcher. Close it first, or use Reconnect Link.";
            return false;
        }

        _config.Config.DolphinPath = dolphinPath;
        _config.Config.IsoPath = isoPath;
        _config.Save();

        ApplyDolphinPathsFromConfig(dolphinPath);

        if (!DolphinConfigService.EnsureMultiplayerMemoryConfig(dolphinPath, m => Log?.Invoke(m), out error))
            return false;

        if (!DolphinProcessMonitor.TryLaunchDolphin(dolphinPath, isoPath, out var processId, out error))
            return false;

        _monitor.RegisterLaunchedProcess(processId);
        _bridge.SetTrackedProcessId(processId);
        _bridge.PrepareForRelink();
        _bridgeWorker.NotifyDolphinRunning(true);
        _bridge.TryAttach();

        Log?.Invoke($"Launched Dolphin: {dolphinPath} (PID {processId})");
        if (!string.IsNullOrWhiteSpace(isoPath) && File.Exists(isoPath.Trim().Trim('"')))
            Log?.Invoke($"Loading game: {isoPath}");
        else if (!string.IsNullOrWhiteSpace(isoPath))
            Log?.Invoke("ISO path not found — Dolphin opened without loading a game");

        return true;
    }

    public async Task WarpSelfAsync(byte courseId, byte episodeId)
    {
        if (!CanUseClientTeleport)
        {
            Log?.Invoke("Client teleporting is disabled by the host");
            return;
        }

        if (_levels != null && !_levels.IsValidWarp(courseId, episodeId))
        {
            Log?.Invoke($"Invalid warp target: course={courseId} episode={episodeId}");
            return;
        }

        var selfSlot = LocalSlot;
        if (IsHosting)
            _server?.RequestWarp(LocalSlot, selfSlot, courseId, episodeId);
        else if (_client != null)
            await _client.SendWarpRequestAsync(selfSlot, courseId, episodeId);

        if (!_bridgeWorker.ApplyWarp(selfSlot, courseId, episodeId, IsHosting))
            Log?.Invoke($"Warp queued — waiting for BSMSO link (launch game with {ModuleVersionMessages.ModuleFileName} loaded)");
    }

    public async Task WarpToPlayerAsync(byte targetSlot)
    {
        if (!CanUseClientTeleport)
        {
            Log?.Invoke("Client teleporting is disabled by the host");
            return;
        }

        if (targetSlot == LocalSlot)
        {
            Log?.Invoke("Cannot teleport to yourself");
            return;
        }

        var entry = _roster.FirstOrDefault(e => e.Slot == targetSlot);
        if (entry == null)
        {
            Log?.Invoke("Player not found in roster");
            return;
        }

        if (entry.StageId == 0 && entry.State is DolphinState.Booting or DolphinState.Loading)
        {
            Log?.Invoke($"{entry.Username} is still loading — location unknown");
            return;
        }

        var hasPosition = _remoteSnapshots.TryGetValue(targetSlot, out var targetSnap) &&
                          targetSnap.Connected != 0;
        var sameStage = _hasLastLocalSnapshot &&
                        entry.StageId == _lastLocalSnapshot.StageId &&
                        entry.EpisodeId == _lastLocalSnapshot.EpisodeId;

        var selfSlot = LocalSlot;

        if (sameStage && hasPosition)
        {
            if (IsHosting)
                _server?.RequestWarp(LocalSlot, selfSlot, entry.StageId, entry.EpisodeId);
            else if (_client != null)
                await _client.SendWarpRequestAsync(selfSlot, entry.StageId, entry.EpisodeId);

            if (!_bridgeWorker.ApplyWarp(
                    selfSlot,
                    entry.StageId,
                    entry.EpisodeId,
                    IsHosting,
                    stageChange: false,
                    warpToPoint: true,
                    posX: targetSnap.Position.X,
                    posY: targetSnap.Position.Y,
                    posZ: targetSnap.Position.Z,
                    facingY: targetSnap.RotationY))
            {
                Log?.Invoke($"Teleport queued — waiting for BSMSO link (launch game with {ModuleVersionMessages.ModuleFileName} loaded)");
                return;
            }

            Log?.Invoke($"Teleporting to {entry.Username} at their exact location");
            return;
        }

        if (IsHosting)
            _server?.RequestWarp(LocalSlot, selfSlot, entry.StageId, entry.EpisodeId);
        else if (_client != null)
            await _client.SendWarpRequestAsync(selfSlot, entry.StageId, entry.EpisodeId);

        if (!_bridgeWorker.ApplyWarp(
                selfSlot,
                entry.StageId,
                entry.EpisodeId,
                IsHosting,
                stageChange: true,
                warpToPoint: hasPosition,
                posX: hasPosition ? targetSnap.Position.X : 0f,
                posY: hasPosition ? targetSnap.Position.Y : 0f,
                posZ: hasPosition ? targetSnap.Position.Z : 0f,
                facingY: hasPosition ? targetSnap.RotationY : 0f))
        {
            Log?.Invoke($"Warp queued — waiting for BSMSO link (launch game with {ModuleVersionMessages.ModuleFileName} loaded)");
            return;
        }

        Log?.Invoke(hasPosition
            ? $"Warping to {entry.Username}'s stage and teleporting to their position"
            : $"Warping to {entry.Username}'s stage (position not yet known)");
    }

    public void HostWarp(byte targetSlot, byte courseId, byte episodeId)
    {
        if (_levels != null && !_levels.IsValidWarp(courseId, episodeId))
        {
            Log?.Invoke($"Invalid warp target: course={courseId} episode={episodeId}");
            return;
        }

        _server?.RequestWarp(LocalSlot, targetSlot, courseId, episodeId);
        if (targetSlot == ProtocolConstants.WarpAllSlots || targetSlot == LocalSlot)
        {
            if (!_bridgeWorker.ApplyWarp(targetSlot, courseId, episodeId, true))
                Log?.Invoke("Warp queued — waiting for Dolphin link");
        }
    }

    public void SetAllowClientTeleport(bool allowClientTeleport)
    {
        _config.Config.AllowClientTeleporting = allowClientTeleport;
        _config.SaveDebounced();
        _server?.SetAllowClientTeleport(allowClientTeleport);
        if (IsHosting)
        {
            AllowClientTeleport = allowClientTeleport;
            ClientTeleportPolicyKnown = true;
        }

        ClientTeleportPolicyChanged?.Invoke();
    }

    public void SetServerSync(bool syncFlags, bool syncObjects, bool syncProgress)
    {
        _config.Config.SyncFlags = syncFlags;
        _config.Config.SyncObjects = syncObjects;
        _config.Config.SyncProgress = syncProgress;
        _config.SaveDebounced();
        _server?.SetSyncSettings(syncFlags, syncObjects, syncProgress);
        _bridgeWorker.ApplySyncSettings(syncFlags, syncObjects, syncProgress);
    }

    public void SetGameMode(GameMode mode)
    {
        if (!IsHosting || _server == null)
            return;

        _server.SetGameMode(mode);
        ApplyGameModeState(_server.GetGameModeState());
    }

    public void SetHideSeekRoles(IReadOnlyDictionary<byte, HideSeekRole> roles)
    {
        if (!IsHosting || _server == null)
            return;

        _server.SetHideSeekRoles(roles);
        ApplyGameModeState(_server.GetGameModeState());
    }

    public bool TryStartHideSeekTag(out string? error)
    {
        error = null;
        if (!IsHosting || _server == null)
        {
            error = "Host only.";
            return false;
        }

        if (!_server.TryStartHideSeekTag(out error))
            return false;

        ApplyGameModeState(_server.GetGameModeState());
        return true;
    }

    public void StopHideSeekTag()
    {
        if (!IsHosting || _server == null)
            return;

        _server.StopHideSeekTag();
        ApplyGameModeState(_server.GetGameModeState());
    }

    public void ResetHideSeekTag()
    {
        if (!IsHosting || _server == null)
            return;

        _server.ResetHideSeekTag();
        ApplyGameModeState(_server.GetGameModeState());
    }

    private void ApplyGameModeState(GameModeStatePacket state)
    {
        if (!IsConnected)
            return;

        _bridgeWorker.ApplyGameModeState(LocalSlot, state);
        GameModeStateChanged?.Invoke(state);
    }

    private void ResetHideSeekIfActiveOnServer()
    {
        if (_server == null || _server.GetGameModeState().GameMode != GameMode.HideSeek)
            return;

        _server.SetGameMode(GameMode.Normal);
        if (IsConnected)
            ApplyGameModeState(_server.GetGameModeState());
    }

    private void ForceGameModeToNormalLocally()
    {
        if (_bridgeWorker.CurrentGameModeState.GameMode == GameMode.HideSeek)
            _bridgeWorker.ForceResetGameModeToNormal(LocalSlot);

        GameModeStateChanged?.Invoke(_bridgeWorker.CurrentGameModeState);
    }

    private void OnRosterUpdated(PlayerRosterEntry[] entries)
    {
        PlayerRosterEntry[] rosterCopy;
        lock (_sessionLock)
        {
            _roster = entries;
            rosterCopy = entries;

            if (!IsConnected)
                return;

            _activeRosterSlots.Clear();
            foreach (var entry in entries)
                _activeRosterSlots.Add(entry.Slot);

            var activeSlots = _activeRosterSlots;
            if (_sessionHasSeenRoster)
            {
                foreach (var slot in _remoteSnapshots.Keys.ToArray())
                {
                    if (activeSlots.Contains(slot))
                    {
                        _rosterMissStrikes.Remove(slot);
                        continue;
                    }

                    var strikes = _rosterMissStrikes.TryGetValue(slot, out var count) ? count + 1 : 1;
                    if (strikes < ProtocolConstants.RosterMissEvictThreshold)
                    {
                        _rosterMissStrikes[slot] = strikes;
                        continue;
                    }

                    _rosterMissStrikes.Remove(slot);
                    EvictRemotePlayer(slot);
                }
            }
            else
            {
                _rosterMissStrikes.Clear();
            }

            _sessionHasSeenRoster = true;
        }

        RosterUpdated?.Invoke(rosterCopy);
    }

    private void EvictRemotePlayer(byte slot)
    {
        _remoteSnapshots.Remove(slot);
        _bridgeWorker.RemoveRemoteSnapshot(slot);
    }

    private void OnWarpCommand(byte targetSlot, byte courseId, byte episodeId, byte requesterSlot)
    {
        _ = requesterSlot;
        if (targetSlot != ProtocolConstants.WarpAllSlots && targetSlot != LocalSlot) return;
        if (!_bridgeWorker.ApplyWarp(targetSlot, courseId, episodeId, IsHosting))
            Log?.Invoke("Warp queued — waiting for Dolphin link");
    }

    private void OnSnapshotReceived(byte slot, PlayerSnapshot snap)
    {
        try
        {
            if (slot == LocalSlot)
                return;

            lock (_sessionLock)
            {
                if (!_activeRosterSlots.Contains(slot))
                    return;

                snap.Slot = slot;
                snap.Connected = 1;

                var appearance = NameTagAppearance.CreateDefault();
                if (NameTagColorCodec.TryDecodeAppearance(snap.Name, out var decoded))
                    appearance = decoded;

                var rosterName = _roster.FirstOrDefault(r => r.Slot == slot)?.Username;
                if (!string.IsNullOrWhiteSpace(rosterName))
                    snap.SetName(rosterName);
                else if (string.IsNullOrWhiteSpace(snap.GetPureName()))
                    snap.SetName($"Player{slot + 1}");

                _remoteSnapshots[slot] = snap;
                _bridgeWorker.PushRemoteSnapshot(slot, snap, appearance);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Remote snapshot error: {ex.Message}");
        }
    }

    private void OnMarioVoiceEventReceived(byte slot, MarioVoiceEvent voiceEvent)
    {
        if (slot == LocalSlot || voiceEvent.IsEmpty)
            return;

        lock (_sessionLock)
        {
            if (!_activeRosterSlots.Contains(slot))
                return;

            _bridgeWorker.PushRemoteMarioVoiceEvent(slot, voiceEvent);
        }
    }

    public void RefreshPlayerAppearance()
    {
        if (!_hasLastLocalSnapshot || !IsConnected)
            return;

        SendLocalSnapshot(_lastLocalSnapshot);
    }

    private void OnLocalSnapshot(PlayerSnapshot snap)
    {
        _lastLocalSnapshot = snap;
        _hasLastLocalSnapshot = true;
        SendLocalSnapshot(snap);
    }

    private void OnLocalMarioVoice(MarioVoiceEvent voiceEvent)
    {
        if (_client?.IsConnected != true || voiceEvent.IsEmpty)
            return;

        _ = _client.SendMarioVoiceEventAsync(voiceEvent);
    }

    private void SendLocalSnapshot(PlayerSnapshot snap)
    {
        try
        {
            NameTagAppearance appearance = NameTagAppearance.CreateDefault();
            if (TryParseNameTagColor(_config.Config.NameTagColor, out var textR, out var textG, out var textB) &&
                TryParseNameTagColor(_config.Config.NameTagOutlineColor, out var outlineR, out var outlineG,
                    out var outlineB))
            {
                if (!TryParseNameTagColor(_config.Config.NameTagGradientColor, out var bottomR, out var bottomG,
                        out var bottomB))
                {
                    bottomR = textR;
                    bottomG = textG;
                    bottomB = textB;
                }

                appearance = NameTagColorCodec.ToAppearance(textR, textG, textB, bottomR, bottomG, bottomB,
                    outlineR, outlineG, outlineB, _config.Config.NameTagGradientEnabled);
                snap.SetNameTagAppearance(textR, textG, textB, bottomR, bottomG, bottomB, outlineR, outlineG,
                    outlineB, _config.Config.NameTagGradientEnabled);
            }
            else
            {
                snap.SetName(_config.Config.Username);
            }

            _bridgeWorker.ApplyLocalNameTagAppearance(_config.Config.Username, appearance);
            if (_client?.IsConnected == true)
                _client.PublishSnapshot(snap);

            if (_server != null && IsHosting && IsConnected)
            {
                var state = _bridgeWorker.LinkState == DolphinLinkState.ModuleReady
                    ? DolphinState.Active
                    : DolphinState.Booting;
                _server.UpdatePlayerState(LocalSlot, snap.StageId, snap.EpisodeId, state,
                    _client?.MeasuredPingMs ?? 0);
            }
        }
        catch (Exception ex) when (!_shuttingDown)
        {
            if (ex is SocketException { SocketErrorCode: SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported })
                return;
            Log?.Invoke($"Local snapshot error: {ex.Message}");
        }
    }

    private void OnDolphinStarted()
    {
        _bridgeWorker.NotifyDolphinRunning(true);
        _bridge.PrepareForRelink();
        _bridge.TryAttach();
        if (IsConnected || IsHosting)
            _bridgeWorker.SetConnected(true, LocalSlot, _config.Config.Username, IsHosting);
    }

    private void OnDolphinStopped() => HandleDolphinStopped();

    private void HandleDolphinStopped()
    {
        if (_shuttingDown)
            return;

        if (Interlocked.CompareExchange(ref _dolphinStopHandling, 1, 0) != 0)
            return;

        var hadSession = IsConnected || IsHosting;

        try
        {
            _bridgeWorker.NotifyDolphinRunning(false);
            _bridge.SetTrackedProcessId(null);
            _bridge.Detach();
            _monitor.ClearTrackedProcess();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Dolphin closed (cleanup note: {ex.Message}");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (hadSession && !_shuttingDown)
                {
                    Log?.Invoke("Dolphin closed — disconnecting from server.");
                    await DisconnectAsync(DisconnectReason.DolphinClosed).ConfigureAwait(false);
                }
                else
                {
                    Log?.Invoke("Dolphin closed.");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Disconnect after Dolphin close: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _dolphinStopHandling, 0);
                SafeRaise(DolphinClosed);
            }
        });
    }

    private static void SafeRaise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch
        {
            // UI handlers must not take down background threads.
        }
    }

    private void ApplyDolphinPathsFromConfig() =>
        ApplyDolphinPathsFromConfig(_config.Config.DolphinPath);

    private void ApplyDolphinPathsFromConfig(string dolphinPath)
    {
        _bridge.SetPreferredExecutablePath(dolphinPath);
        _bridge.SetGuestMailboxAddress(_config.Config.MailboxAddress);
        _monitor.SetExpectedDolphinPath(dolphinPath);
    }

    private static void ValidateUsername(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || name.Length > 16)
            throw new InvalidOperationException("Username must be 3-16 characters.");
        if (!name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new InvalidOperationException("Username may only use letters, digits, and underscores.");
    }

    private static bool TryParseNameTagColor(string? value, out byte r, out byte g, out byte b)
    {
        r = g = b = 255;
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
            text = text[1..];
        if (text.Length != 6)
            return false;

        try
        {
            r = Convert.ToByte(text[..2], 16);
            g = Convert.ToByte(text.Substring(2, 2), 16);
            b = Convert.ToByte(text.Substring(4, 2), 16);
            return true;
        }
        catch
        {
            r = g = b = 255;
            return false;
        }
    }

    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        try
        {
            DisconnectAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            StopClientOnly();
            StopServer();
        }

        _bridgeWorker.SetConnected(false, 0, "", false);
        _bridgeWorker.Stop();
        _bridge.Detach();
        _monitor.Stop();
    }

    public void Dispose()
    {
        Shutdown();
        _bridgeWorker.Dispose();
        _bridge.Dispose();
        _monitor.Dispose();
    }
}
