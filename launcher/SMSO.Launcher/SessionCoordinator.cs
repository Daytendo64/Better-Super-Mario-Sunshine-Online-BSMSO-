using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SMSO.Bridge;
using SMSO.Net;
using SMSO.Net.MarioPack;
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
    private readonly HashSet<byte> _previousRosterSlots = new();
    private readonly Dictionary<byte, int> _rosterMissStrikes = new();
    private readonly object _sessionLock = new();
    private readonly SemaphoreSlim _networkOpLock = new(1, 1);
    private PlayerRosterEntry[] _roster = Array.Empty<PlayerRosterEntry>();
    private readonly string?[] _rosterNamesBySlot =
        new string?[ProtocolConstants.MaxRemoteSlots];
    private bool _sessionHasSeenRoster;
    private volatile bool _shuttingDown;
    private int _clientGeneration;
    private int _sessionEndHandling;
    private int _gameCloseCheckGeneration;
    private CancellationTokenSource? _gameCloseCts;
    private CancellationTokenSource? _worldReplayCts;
    private DolphinLinkState _previousLinkState = DolphinLinkState.NotRunning;
    private const int GameCloseGraceMs = 1500;
    private PlayerSnapshot _lastLocalSnapshot;
    private bool _hasLastLocalSnapshot;
    private readonly List<WorldEventPacket> _pendingEpisodeWorldEvents = new();
    private readonly object _pendingEpisodeWorldEventsLock = new();
    private volatile bool _acceptWorldEventApplies;
    private readonly HashSet<string> _ensuredMarioPackIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _packEnsureLock = new();
    private int _packEnsureRunning;

    public event Action<string>? StatusChanged;
    public event Action<string>? Log;
    public event Action<PlayerRosterEntry[]>? RosterUpdated;
    public event Action? HostingStateChanged;
    public event Action? ClientTeleportPolicyChanged;
    public event Action? SyncSettingsChanged;
    public event Action? DolphinClosed;
    public event Action<GameModeStatePacket>? GameModeStateChanged;
    public event Action<DolphinLinkState>? DolphinLinkStateChanged;
    public event Action<string>? DisconnectNotice;
    /// <summary>Fired when the host warps everyone (or this client via warp-all) to a stage.</summary>
    public event Action<byte, byte>? WarpEveryoneReceived;

    public bool IsHosting => _server?.IsRunning == true;
    public bool IsConnected => _client?.IsConnected == true;
    public bool AllowClientTeleport { get; private set; }
    public bool ClientTeleportPolicyKnown { get; private set; }
    public bool SyncFlagsEnabled { get; private set; }
    public bool SyncObjectsEnabled { get; private set; }
    public bool SyncProgressEnabled { get; private set; }
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
        _bridgeWorker.LinkStateChanged += OnBridgeLinkStateChanged;
        _monitor.Log += m => Log?.Invoke(m);
        _monitor.DolphinStopped += OnDolphinStopped;
        _monitor.DolphinStarted += OnDolphinStarted;

        _bridgeWorker.LocalSnapshotReady += OnLocalSnapshot;
        _bridgeWorker.ModuleProgressResyncRequested += OnModuleProgressResyncRequested;
        _bridgeWorker.LocalMarioVoiceReady += OnLocalMarioVoice;
        _bridgeWorker.LocalWorldEventReady += OnLocalWorldEvent;
        UpdateSyncSettingsState(_config.Config.SyncFlags, _config.Config.SyncObjects, _config.Config.SyncProgress);
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

        PlayerNameValidator.ValidateOrThrow(_config.Config.Username);

        StatusChanged?.Invoke("Starting server...");
        await DisconnectAsync(endSession: true).ConfigureAwait(true);

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
                _server.SetHideSeekGraceDurationMs(_config.Config.HideSeekGraceSeconds * 1000);
                ApplyConfiguredSyncSettings();
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

        PlayerNameValidator.ValidateOrThrow(_config.Config.Username);
        await ConnectClientAsync(_config.Config.ServerIp, _config.Config.ServerPort, isHost: false);
    }

    /// <summary>
    /// Connect to a server using the same lifecycle as a first-time join:
    /// fully tear down any prior client, reset bridge/roster state, then create a fresh NetClient.
    /// </summary>
    private async Task ConnectClientAsync(string host, int port, bool isHost)
    {
        await _networkOpLock.WaitAsync().ConfigureAwait(true);
        try
        {
            await TearDownClientAsync(DisconnectReason.UserRequest, sendGoodbye: true).ConfigureAwait(true);

            var generation = Interlocked.Increment(ref _clientGeneration);
            var client = new NetClient();
            client.Log += m => Log?.Invoke(m);
            client.JoinRejected += reason =>
            {
                if (reason == JoinRejectReason.NameTaken)
                    Log?.Invoke($"Join rejected: username '{_config.Config.Username}' is already in use — set a unique name in Settings (e.g. Player{InstanceIndex + 1})");
                else if (reason == JoinRejectReason.InvalidName)
                    Log?.Invoke($"Join rejected: {PlayerNameValidator.InvalidNameHint}");
                else if (reason == JoinRejectReason.VersionMismatch)
                    Log?.Invoke(NetJoinRejectedException.GetUserMessage(reason));
                else
                    Log?.Invoke($"Join rejected: {reason}");
            };
            client.JoinAccepted += () =>
            {
                if (generation != Volatile.Read(ref _clientGeneration))
                    return;

                try
                {
                    _bridgeWorker.SetConnected(true, client.AssignedSlot, _config.Config.Username, isHost);
                    _acceptWorldEventApplies = true;
                    if (isHost)
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
            client.WorldEventReceived += OnWorldEventReceived;
            client.WorldStateReplayReceived += OnWorldStateReplayReceived;
            client.SyncSettingsReceived += (f, o, p) =>
            {
                UpdateSyncSettingsState(f, o, p);
                _bridgeWorker.ApplySyncSettings(f, o, p);
                Log?.Invoke($"Sync settings from host: flags={f} objects={o} progress={p}");
            };
            client.ClientTeleportSettingsReceived += allowed =>
            {
                AllowClientTeleport = allowed;
                ClientTeleportPolicyKnown = true;
                ClientTeleportPolicyChanged?.Invoke();
            };
            client.GameModeStateReceived += state => ApplyGameModeState(state);
            client.Disconnected += reason =>
            {
                if (generation != Volatile.Read(ref _clientGeneration) || _shuttingDown)
                    return;

                var message = DisconnectMessages.GetUserMessage(reason);
                Log?.Invoke(message);
                DisconnectNotice?.Invoke(message);
                _ = HandleUnexpectedDisconnectAsync(isHost, reason);
            };

            _client = client;
            StatusChanged?.Invoke("Connecting");
            try
            {
                await client.ConnectAsync(host, port, _config.Config.Username,
                    marioModelId: _config.Config.SelectedMarioModelId).ConfigureAwait(true);
                FlushSnapshotsAfterConnect();
            }
            catch (NetJoinRejectedException ex)
            {
                await TearDownClientAsync(DisconnectReason.UserRequest, sendGoodbye: false).ConfigureAwait(true);
                if (isHost)
                    StopServer();
                StatusChanged?.Invoke("Disconnected");
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch
            {
                await TearDownClientAsync(DisconnectReason.UserRequest, sendGoodbye: false).ConfigureAwait(true);
                if (isHost)
                    StopServer();
                StatusChanged?.Invoke("Disconnected");
                throw;
            }
        }
        finally
        {
            _networkOpLock.Release();
        }
    }

    /// <summary>Push local + remote sync to Dolphin and UDP immediately after TCP/UDP join completes.</summary>
    private void FlushSnapshotsAfterConnect()
    {
        try
        {
            var client = _client;
            if (client == null)
                return;

            _bridgeWorker.SetConnected(true, client.AssignedSlot, _config.Config.Username, IsHosting);
            _acceptWorldEventApplies = true;

            if (_hasLastLocalSnapshot)
            {
                var snap = _lastLocalSnapshot;
                snap.Connected = 1;
                SendLocalSnapshot(snap);
            }

            client.SendSnapshotNow();
            _bridgeWorker.FlushRemoteSnapshotsToDolphin();
            // #region agent log
            AgentDebugLog.Write("D", "SessionCoordinator.FlushSnapshotsAfterConnect", "post-connect flush", new
            {
                slot = client.AssignedSlot,
                hasLastLocalSnapshot = _hasLastLocalSnapshot,
                linkState = _bridgeWorker.LinkState.ToString(),
            });
            // #endregion
        }
        catch (Exception ex) when (!_shuttingDown)
        {
            Log?.Invoke($"Post-connect snapshot flush error: {ex.Message}");
        }
    }

    public async Task DisconnectAsync(DisconnectReason reason = DisconnectReason.UserRequest, bool endSession = false)
    {
        await _networkOpLock.WaitAsync().ConfigureAwait(true);
        try
        {
            ResetHideSeekIfActiveOnServer();
            await TearDownClientAsync(reason, sendGoodbye: true).ConfigureAwait(true);
            ForceGameModeToNormalLocally();
            StatusChanged?.Invoke("Disconnected");
        }
        finally
        {
            if (endSession)
                StopServer();
            _networkOpLock.Release();
        }
    }

    /// <summary>
    /// Fully destroy the current NetClient and reset all client-side session state so the next
    /// connect starts from a clean slate identical to a first-time join.
    /// </summary>
    private async Task TearDownClientAsync(DisconnectReason reason, bool sendGoodbye)
    {
        Interlocked.Increment(ref _clientGeneration);

        var client = Interlocked.Exchange(ref _client, null);
        if (client != null)
        {
            if (sendGoodbye)
            {
                try
                {
                    await client.DisconnectAsync(reason).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Log?.Invoke("Client disconnect timed out — forcing cleanup");
                    client.ForceDispose();
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Client disconnect error: {ex.Message}");
                    client.ForceDispose();
                }
            }
            else
            {
                client.ForceDispose();
            }
        }

        ResetClientSessionState();
    }

    private void ResetClientSessionState()
    {
        // Cancel any in-flight DrainWorldEventReplayAsync so disconnect mid-replay cannot
        // keep pushing shine/red/blue events into Dolphin after SetConnected(false).
        CancelWorldEventReplay("session reset");
        _acceptWorldEventApplies = false;

        _bridgeWorker.SetConnected(false, 0, "", false);
        AllowClientTeleport = false;
        ClientTeleportPolicyKnown = false;
        _remoteSnapshots.Clear();
        _activeRosterSlots.Clear();
        _previousRosterSlots.Clear();
        _rosterMissStrikes.Clear();
        _sessionHasSeenRoster = false;
        _roster = Array.Empty<PlayerRosterEntry>();
        Array.Clear(_rosterNamesBySlot);
        lock (_pendingEpisodeWorldEventsLock)
            _pendingEpisodeWorldEvents.Clear();
        _bridgeWorker.ClearPendingIncomingWorldEvents();
        _bridgeWorker.ClearRemoteSnapshots();
        try
        {
            _bridgeWorker.FlushRemoteSnapshotsToDolphin();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Remote snapshot flush skipped: {ex.Message}");
        }

        ClientTeleportPolicyChanged?.Invoke();
    }

    private void CancelWorldEventReplay(string reason)
    {
        var cts = Interlocked.Exchange(ref _worldReplayCts, null);
        if (cts == null)
            return;
        try
        {
            cts.Cancel();
            Log?.Invoke($"World sync: cancelled in-flight replay ({reason})");
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }
        finally
        {
            cts.Dispose();
        }
    }

    private CancellationToken BeginWorldEventReplay()
    {
        CancelWorldEventReplay("new replay");
        var cts = new CancellationTokenSource();
        _worldReplayCts = cts;
        return cts.Token;
    }

    private async Task HandleUnexpectedDisconnectAsync(bool stopServer, DisconnectReason reason)
    {
        await _networkOpLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shuttingDown)
                return;

            ResetHideSeekIfActiveOnServer();
            await TearDownClientAsync(reason, sendGoodbye: false).ConfigureAwait(false);
            if (stopServer)
                StopServer();
            ForceGameModeToNormalLocally();
            StatusChanged?.Invoke("Disconnected");
        }
        finally
        {
            _networkOpLock.Release();
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

    private void ApplyConfiguredSyncSettings()
    {
        var syncFlags = _config.Config.SyncFlags;
        var syncObjects = _config.Config.SyncObjects;
        var syncProgress = _config.Config.SyncProgress;
        _server?.SetSyncSettings(syncFlags, syncObjects, syncProgress);
        _bridgeWorker.ApplySyncSettings(syncFlags, syncObjects, syncProgress);
        UpdateSyncSettingsState(syncFlags, syncObjects, syncProgress);
    }

    private void UpdateSyncSettingsState(bool syncFlags, bool syncObjects, bool syncProgress)
    {
        SyncFlagsEnabled = syncFlags;
        SyncObjectsEnabled = syncObjects;
        SyncProgressEnabled = syncProgress;
        SyncSettingsChanged?.Invoke();
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
            _bridgeWorker.InvalidateMailboxWriteCaches();
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

        if (!DolphinConfigService.EnsureBsmsGameIdentity(isoPath, m => Log?.Invoke(m), out _, out error))
            return false;

        if (!DolphinConfigService.EnsureBsmsGameBanner(isoPath, m => Log?.Invoke(m), out var bannerError))
            Log?.Invoke($"Warning: {bannerError}");

        if (!DolphinConfigService.EnsureBsmsGameCover(dolphinPath, m => Log?.Invoke(m), out var coverError))
            Log?.Invoke($"Warning: {coverError}");

        DolphinConfigService.ClearDolphinGameListCache(dolphinPath, m => Log?.Invoke(m));

        if (!DolphinConfigService.ApplyLaunchDolphinSettings(
                dolphinPath,
                _config.Config.ApplyRecommendedDolphinSettings,
                m => Log?.Invoke(m),
                out error))
            return false;

        if (!_config.Config.ApplyRecommendedDolphinSettings)
            Log?.Invoke("Skipped recommended Dolphin performance profile (disabled in Connection).");

        // Ensure custom packs are on the disc/folder before Dolphin opens the image.
        try
        {
            MarioPackInstaller.EnsureAllLibraryPacksPresent(isoPath, m => Log?.Invoke(m));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Custom model install before launch skipped: {ex.Message}");
        }

        ApplySelectedMarioModelToBridge();

        if (!DolphinProcessMonitor.TryLaunchDolphin(dolphinPath, isoPath, out var processId, out error))
            return false;

        _monitor.RegisterLaunchedProcess(processId);
        _bridge.SetTrackedProcessId(processId);
        _bridge.PrepareForRelink();
        _bridgeWorker.InvalidateMailboxWriteCaches();
        _bridgeWorker.NotifyDolphinRunning(true);
        _bridge.TryAttach();

        Log?.Invoke($"Launched Dolphin: {dolphinPath} (PID {processId})");
        if (!string.IsNullOrWhiteSpace(isoPath) &&
            GameIdentity.TryResolveDolphinLaunchPath(isoPath.Trim().Trim('"'), out var launchPath))
            Log?.Invoke($"Loading game: {launchPath}");
        else if (!string.IsNullOrWhiteSpace(isoPath))
            Log?.Invoke("Game path not found — Dolphin opened without loading a game");

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

        // WarpCommand broadcast (including back to host) applies the bridge intent once.
        _server?.RequestWarp(LocalSlot, targetSlot, courseId, episodeId);
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
        UpdateSyncSettingsState(syncFlags, syncObjects, syncProgress);
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

        _server.SetHideSeekGraceDurationMs(_config.Config.HideSeekGraceSeconds * 1000);
        if (!_server.TryStartHideSeekTag(out error))
            return false;

        ApplyGameModeState(_server.GetGameModeState());
        return true;
    }

    public void SetHideSeekGraceSeconds(int seconds)
    {
        _config.Config.HideSeekGraceSeconds = Math.Clamp(seconds, 15, 60) switch
        {
            <= 22 => 15,
            <= 37 => 30,
            <= 52 => 45,
            _ => 60,
        };
        if (IsHosting && _server != null)
            _server.SetHideSeekGraceDurationMs(_config.Config.HideSeekGraceSeconds * 1000);
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
        // Always reset — even when already Normal. ResetHideSeekIfActiveOnServer may have
        // applied Normal at a high Seq first; skipping ForceReset left _lastGameModeSeq stale
        // so the next session's Seq=1 HideSeek enable was rejected until Seq caught up.
        _bridgeWorker.ForceResetGameModeToNormal(LocalSlot);
        GameModeStateChanged?.Invoke(_bridgeWorker.CurrentGameModeState);
    }

    private void OnRosterUpdated(PlayerRosterEntry[] entries)
    {
        PlayerRosterEntry[] rosterCopy;
        List<(byte Slot, string Name)>? departedPlayers = null;
        List<(byte Slot, string Name)>? joinedPlayers = null;
        lock (_sessionLock)
        {
            var incomingSlots = new HashSet<byte>(entries.Select(e => e.Slot));

            if (IsConnected && _sessionHasSeenRoster)
            {
                foreach (var previous in _roster)
                {
                    if (previous.Slot == LocalSlot || incomingSlots.Contains(previous.Slot))
                        continue;

                    departedPlayers ??= new List<(byte, string)>();
                    var name = string.IsNullOrWhiteSpace(previous.Username)
                        ? $"Player {previous.Slot + 1}"
                        : previous.Username;
                    departedPlayers.Add((previous.Slot, name));
                }
            }

            _roster = entries;
            Array.Clear(_rosterNamesBySlot);
            foreach (var entry in entries)
            {
                if (entry.Slot < _rosterNamesBySlot.Length)
                    _rosterNamesBySlot[entry.Slot] = entry.Username;
            }
            rosterCopy = entries;

            // Always push remote model ids into the CommBuffer, even during the brief
            // JoinAccepted window before IsConnected is observed on this thread.
            foreach (var entry in entries)
            {
                if (entry.Slot == LocalSlot)
                    continue;
                _bridgeWorker.SetRemoteMarioModelId(entry.Slot, entry.MarioModelId);
            }

            if (IsConnected)
            {
                if (_sessionHasSeenRoster)
                {
                    foreach (var entry in entries)
                    {
                        if (entry.Slot == LocalSlot || _previousRosterSlots.Contains(entry.Slot))
                            continue;

                        joinedPlayers ??= new List<(byte, string)>();
                        var name = string.IsNullOrWhiteSpace(entry.Username)
                            ? $"Player {entry.Slot + 1}"
                            : entry.Username;
                        joinedPlayers.Add((entry.Slot, name));
                        ResetRemotePlayerForSlot(entry.Slot);
                    }
                }

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

                        _rosterMissStrikes.Remove(slot);
                        EvictRemotePlayer(slot);
                    }
                }
                else
                {
                    _rosterMissStrikes.Clear();
                }

                _previousRosterSlots.Clear();
                foreach (var slot in incomingSlots)
                    _previousRosterSlots.Add(slot);

                _sessionHasSeenRoster = true;
            }
        }

        if (departedPlayers != null)
        {
            foreach (var (slot, name) in departedPlayers)
            {
                _bridgeWorker.EnqueueRosterHudEvent(RosterHudEventKind.Disconnected, slot, name);
                Log?.Invoke($"{name} disconnected.");
            }
        }

        if (joinedPlayers != null)
        {
            foreach (var (slot, name) in joinedPlayers)
                _bridgeWorker.EnqueueRosterHudEvent(RosterHudEventKind.Connected, slot, name);
        }

        foreach (var entry in rosterCopy)
        {
            if (entry.Slot == LocalSlot)
                continue;
            // Re-apply after ResetRemotePlayerForSlot/Evict which clear bridge model ids.
            _bridgeWorker.SetRemoteMarioModelId(entry.Slot, entry.MarioModelId);
        }

        // Never stall the roster/network callback with ISO extract/rebuild.
        QueueEnsureRemotePacksBackground(rosterCopy);

        RosterUpdated?.Invoke(rosterCopy);
    }

    private void QueueEnsureRemotePacksBackground(PlayerRosterEntry[] roster)
    {
        var isoPath = _config.Config.IsoPath;
        if (string.IsNullOrWhiteSpace(isoPath) || roster.Length == 0)
            return;

        List<string>? needed = null;
        foreach (var entry in roster)
        {
            if (entry.Slot == LocalSlot)
                continue;
            var id = CharacterPack.NormalizeModelId(entry.MarioModelId);
            if (id.Length == 0)
                continue;

            lock (_packEnsureLock)
            {
                if (_ensuredMarioPackIds.Contains(id))
                    continue;
            }

            needed ??= new List<string>();
            if (!needed.Contains(id, StringComparer.OrdinalIgnoreCase))
                needed.Add(id);
        }

        if (needed == null || needed.Count == 0)
            return;

        if (Interlocked.CompareExchange(ref _packEnsureRunning, 1, 0) != 0)
            return;

        var path = isoPath;
        var ids = needed;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        MarioPackInstaller.EnsurePackPresent(path, id, m => Log?.Invoke(m));
                        lock (_packEnsureLock)
                            _ensuredMarioPackIds.Add(id);
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"Model pack ensure for {id} skipped: {ex.Message}");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _packEnsureRunning, 0);
            }
        });
    }

    private void ResetRemotePlayerForSlot(byte slot)
    {
        _remoteSnapshots.Remove(slot);
        _bridgeWorker.PrepareRemoteSlotForJoin(slot);
        Log?.Invoke($"Player rejoined slot {slot} — reset remote sync state");
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

        if (targetSlot == ProtocolConstants.WarpAllSlots)
            WarpEveryoneReceived?.Invoke(courseId, episodeId);

        if (!_bridgeWorker.ApplyWarp(targetSlot, courseId, episodeId, IsHosting))
        {
            Log?.Invoke("Warp queued — waiting for Dolphin link");
            return;
        }

        FlushPendingEpisodeWorldEvents(courseId, episodeId);
        RequestWorldProgressResync($"warp {courseId}/{episodeId}");
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

                // Strip any legacy color overlay from the wire name before it reaches
                // Dolphin. copyPurePlayerName truncates overlay names to 5 characters.
                // Install into a fresh Name[] so NetClient's next UDP decode cannot
                // overwrite the stripped display name held by the bridge.
                var rosterName = slot < _rosterNamesBySlot.Length
                    ? _rosterNamesBySlot[slot]
                    : null;
                string displayName;
                if (!string.IsNullOrWhiteSpace(rosterName))
                {
                    displayName = rosterName;
                }
                else if (snap.Name != null &&
                         snap.Name.Length >= 16 &&
                         NameTagColorCodec.HasAppearanceMarker(snap.Name[15]))
                {
                    // Overlay-packed wire irrevocably truncates gradient text.
                    // Keep the last stripped name for this slot; never bake "Playe".
                    if (_remoteSnapshots.TryGetValue(slot, out var previous) &&
                        previous.Name != null &&
                        previous.Name.Length >= 16 &&
                        !NameTagColorCodec.HasAppearanceMarker(previous.Name[15]))
                    {
                        displayName = previous.GetName();
                    }
                    else
                    {
                        displayName = $"Player{slot + 1}";
                    }
                }
                else
                {
                    displayName = snap.GetPureName();
                    if (string.IsNullOrWhiteSpace(displayName))
                        displayName = $"Player{slot + 1}";
                }

                snap.Name = new byte[16];
                snap.SetName(displayName);

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
        ApplySelectedMarioModelToBridge();
        if (!_hasLastLocalSnapshot || !IsConnected)
            return;

        SendLocalSnapshot(_lastLocalSnapshot);
    }

    public void ApplySelectedMarioModelToBridge()
    {
        var modelId = CharacterPack.NormalizeModelId(_config.Config.SelectedMarioModelId);
        _bridgeWorker.ApplyLocalMarioModelId(modelId);
        var isoPath = _config.Config.IsoPath;
        if (string.IsNullOrWhiteSpace(isoPath) || modelId.Length == 0)
            return;

        lock (_packEnsureLock)
        {
            if (_ensuredMarioPackIds.Contains(modelId))
                return;
        }

        // Disc rebuilds must not block UI / join paths.
        _ = Task.Run(() =>
        {
            try
            {
                MarioPackInstaller.EnsurePackPresent(isoPath, modelId, m => Log?.Invoke(m));
                lock (_packEnsureLock)
                    _ensuredMarioPackIds.Add(modelId);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Model pack install skipped: {ex.Message}");
            }
        });
    }

    public void NotifyLocalMarioModelChanged(string? modelId)
    {
        _config.Config.SelectedMarioModelId = CharacterPack.NormalizeModelId(modelId);
        ApplySelectedMarioModelToBridge();
        // Dedicated TCP intent updates the authoritative roster immediately.
        // Each receiving client prepares/commits independently; the heartbeat
        // still re-advertises this id as a compatibility fallback.
        _client?.SetMarioModelId(_config.Config.SelectedMarioModelId);
        Log?.Invoke(string.IsNullOrEmpty(_config.Config.SelectedMarioModelId)
            ? "Mario model set to Retail (applies after stage reload)."
            : $"Mario model set to {_config.Config.SelectedMarioModelId} (applies after stage reload).");
    }

    private void OnLocalSnapshot(PlayerSnapshot snap)
    {
        var logicalStage = YoshiSnapshotCodec.LogicalStageId(snap, snap.StageId);
        var logicalEpisode = YoshiSnapshotCodec.LogicalEpisodeId(snap, snap.EpisodeId);
        var firstSnapshot = !_hasLastLocalSnapshot;
        var lastLogicalStage = _hasLastLocalSnapshot
            ? YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId)
            : logicalStage;
        var lastLogicalEpisode = _hasLastLocalSnapshot
            ? YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot, _lastLocalSnapshot.EpisodeId)
            : logicalEpisode;

        var stageChanged = _hasLastLocalSnapshot &&
                           (logicalStage != lastLogicalStage || logicalEpisode != lastLogicalEpisode);

        // Same-stage death reload does not change course/episode, so stageChanged is false —
        // but the module clears its red-coin mask on stageInit. Detect revive (Dead vfx
        // clearing) and force an immediate progress catch-up instead of waiting ~45s.
        var wasDead = _hasLastLocalSnapshot &&
                      (_lastLocalSnapshot.VfxFlags & (ushort)VfxFlags.Dead) != 0;
        var isDead = (snap.VfxFlags & (ushort)VfxFlags.Dead) != 0;
        var sameStageRevive = _hasLastLocalSnapshot && !stageChanged && !firstSnapshot &&
                              wasDead && !isDead && logicalStage != 0;

        _lastLocalSnapshot = snap;
        _hasLastLocalSnapshot = true;
        if (firstSnapshot || stageChanged)
        {
            FlushPendingEpisodeWorldEvents(logicalStage, logicalEpisode);
            RequestWorldProgressResync(
                $"{(firstSnapshot ? "initial-stage" : "stage-enter")} {logicalStage}/{logicalEpisode}");
        }
        else if (sameStageRevive)
        {
            RequestWorldProgressResync($"same-stage-revive {logicalStage}/{logicalEpisode}");
        }
        SendLocalSnapshot(snap);
        // BridgeWorker's poll loop flushes immediately after this callback with
        // the already-read mailbox. Forcing here performed a second
        // ReadProcessMemory plus duplicate serialization every 60 Hz tick.
    }

    private void RequestWorldProgressResync(string reason)
    {
        if (_client?.IsConnected != true)
            return;
        Log?.Invoke($"World sync: requesting progress resync ({reason})");
        _ = _client.SendWorldProgressRequestAsync();
    }

    private DateTime _lastModuleProgressResyncUtc = DateTime.MinValue;

    private void OnModuleProgressResyncRequested()
    {
        // Module holds BF_REQUEST_PROGRESS for several frames — debounce to one TCP request.
        if ((DateTime.UtcNow - _lastModuleProgressResyncUtc).TotalMilliseconds < 2000)
            return;
        _lastModuleProgressResyncUtc = DateTime.UtcNow;
        RequestWorldProgressResync("module-request-progress");
    }

    private void OnLocalMarioVoice(MarioVoiceEvent voiceEvent)
    {
        if (_client?.IsConnected != true || voiceEvent.IsEmpty)
            return;

        _ = _client.SendMarioVoiceEventAsync(voiceEvent);
    }

    private void OnLocalWorldEvent(WorldEventRequest worldEvent)
    {
        if (_client?.IsConnected != true || worldEvent.IsEmpty)
            return;

        Log?.Invoke(
            $"World sync: sending type={worldEvent.Type} course={worldEvent.CourseId}/{worldEvent.EpisodeId} payload0={worldEvent.Payload0} reserved={worldEvent.Reserved} payload1={worldEvent.Payload1}");
        _ = _client.SendWorldEventAsync(worldEvent);
    }

    /// <summary>
    /// Plaza hub Type5 allowlist coalesced to episode 0xFF by StoryFlagAuthority.
    /// Apply live like StoryFlag — module admits overlay off-plaza and writes on plaza.
    /// </summary>
    private static bool IsPlazaHubTriggerEvent(WorldEventPacket worldEvent) =>
        worldEvent.Type == WorldEventType.TriggerFlag &&
        (worldEvent.EpisodeId == StoryFlagAuthority.PlazaHubEpisode ||
         StoryFlagAuthority.IsPlazaHubTrigger(worldEvent.CourseId, worldEvent.Payload1));

    private static bool IsEpisodeScopedWorldEvent(WorldEventPacket worldEvent) =>
        worldEvent.Type is WorldEventType.GoldCoinCollected
            or WorldEventType.RedCoinCollected
            or WorldEventType.NpcCleaned
            or WorldEventType.GraffitiCleaned
            or WorldEventType.MarioFruitKicked
            or WorldEventType.MarioFruitPicked
            or WorldEventType.MarioFruitThrown
            or WorldEventType.MarioFruitDropped
            or WorldEventType.MarioFruitSync
            // HipDropObject is intentionally NOT episode-deferred: it is non-durable, and
            // flushing a stale pound after casino/stage enter replays THipDropHideObj
            // touchPlayer on virgin panels (Ep5 purple roulette pad vanishes on spawn).
            or WorldEventType.NpcReact
        || (worldEvent.Type == WorldEventType.TriggerFlag && !IsPlazaHubTriggerEvent(worldEvent));

    /// <summary>
    /// Global / course-keyed ownership flags — never episode-defer. Flag writes + HUD
    /// must apply on any stage; actor FX may no-op until the matching course loads.
    /// Plaza hub TriggerFlags (episode 0xFF) are live-applied the same way.
    /// </summary>
    private static bool IsLiveOwnershipWorldEvent(WorldEventPacket worldEvent) =>
        worldEvent.Type is WorldEventType.ShineCollected
            or WorldEventType.BlueCoinCollected
            or WorldEventType.StoryFlag
            or WorldEventType.SecretComplete
        || IsPlazaHubTriggerEvent(worldEvent);

    private void OnWorldEventReceived(WorldEventPacket worldEvent)
    {
        ApplyWorldEventToBridge(worldEvent);
    }

    private void OnWorldStateReplayReceived(WorldEventPacket[] events)
    {
        Log?.Invoke($"World sync: received {events.Length} replayed/resync events from server");
        if (events.Length == 0)
            return;

        // Full authority replay replaces pending state — clear queues + Dolphin incoming
        // so a stuck durable visual retry cannot block live shine/blue ownership applies.
        _bridgeWorker.ClearPendingIncomingWorldEvents();
        lock (_pendingEpisodeWorldEventsLock)
            _pendingEpisodeWorldEvents.Clear();

        // Apply live ownership flags first (shine/blue/story), then episode-scoped visuals.
        // Avoid forceApply so FlushPendingEpisodeWorldEvents can re-deliver on stage entry
        // after the module resets per-stage red-coin trackers (late join).
        foreach (var worldEvent in events.OrderBy(e => IsLiveOwnershipWorldEvent(e) ? 0 : 1)
                     .ThenBy(e => e.EventId))
            ApplyWorldEventToBridge(worldEvent, forceApply: false);

        // Also kick a drained apply for any events that match the current stage so red-coin
        // switch recovery runs promptly after a mid-run resync.
        if (_hasLastLocalSnapshot)
        {
            var stageId = YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId);
            var episodeId = YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot, _lastLocalSnapshot.EpisodeId);
            FlushPendingEpisodeWorldEvents(stageId, episodeId);
        }
    }

    private async Task DrainWorldEventReplayAsync(WorldEventPacket[] events, CancellationToken ct)
    {
        foreach (var worldEvent in events)
        {
            if (ct.IsCancellationRequested)
                return;

            // Session teardown rejects applies; leave events queued (reset clears the list).
            if (!_acceptWorldEventApplies)
                return;

            ApplyWorldEventToBridge(worldEvent, forceApply: true);

            // Remove only after a successful push so a cancelled drain (resync /
            // overlapping flush) can re-deliver undelivered episode events.
            lock (_pendingEpisodeWorldEventsLock)
                _pendingEpisodeWorldEvents.RemoveAll(pending => pending.EventId == worldEvent.EventId);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (_bridgeWorker.TryGetLastAppliedEventId(out var lastApplied) &&
                    lastApplied >= worldEvent.EventId)
                {
                    break;
                }

                try
                {
                    await Task.Delay(16, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (!ct.IsCancellationRequested)
            Log?.Invoke($"World sync: replay drain finished ({events.Length} events)");
    }

    private void ApplyWorldEventToBridge(WorldEventPacket worldEvent, bool forceApply = false)
    {
        if (worldEvent.EventId == 0)
            return;

        // Drop applies after disconnect so a cancelled drain / late TCP packet cannot
        // resurrect collectible state into an offline session.
        if (!_acceptWorldEventApplies)
            return;

        if (!forceApply &&
            IsEpisodeScopedWorldEvent(worldEvent) &&
            (!_hasLastLocalSnapshot ||
             !MatchesEpisodeScopedApply(worldEvent,
                 YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId),
                 YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot, _lastLocalSnapshot.EpisodeId))))
        {
            lock (_pendingEpisodeWorldEventsLock)
            {
                if (_pendingEpisodeWorldEvents.All(pending => pending.EventId != worldEvent.EventId))
                    _pendingEpisodeWorldEvents.Add(worldEvent);
            }
            var localStage = _hasLastLocalSnapshot
                ? $"{_lastLocalSnapshot.StageId}/{_lastLocalSnapshot.EpisodeId}"
                : "not-ready";
            Log?.Invoke(
                $"World sync: queued episode-local eventId={worldEvent.EventId} type={worldEvent.Type} — local stage {localStage}, event {worldEvent.CourseId}/{worldEvent.EpisodeId}");
            return;
        }

        // HipDrop is live-only: never queue for later stage-enter flush (would hide virgin
        // THipDropHideObj panels, e.g. Sirena Ep5 purple roulette pad).
        if (!forceApply && worldEvent.Type == WorldEventType.HipDropObject)
        {
            if (!_hasLastLocalSnapshot)
            {
                Log?.Invoke(
                    $"World sync: drop HipDropObject eventId={worldEvent.EventId} — local stage not-ready");
                return;
            }

            var localStage = YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId);
            var localEpisode = YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot,
                _lastLocalSnapshot.EpisodeId);
            var stageOk = worldEvent.CourseId == localStage;
            var episodeOk = worldEvent.EpisodeId == localEpisode ||
                            (localStage == SirenaCasinoMapping.AreaId &&
                             SirenaCasinoMapping.EpisodesEquivalent(worldEvent.EpisodeId, localEpisode));
            if (!stageOk || !episodeOk)
            {
                Log?.Invoke(
                    $"World sync: drop HipDropObject eventId={worldEvent.EventId} — local stage {localStage}/{localEpisode}, event {worldEvent.CourseId}/{worldEvent.EpisodeId}");
                return;
            }
        }

        Log?.Invoke(
            $"World sync: applying eventId={worldEvent.EventId} type={worldEvent.Type} course={worldEvent.CourseId}/{worldEvent.EpisodeId} payload0={worldEvent.Payload0} reserved={worldEvent.Reserved} payload1={worldEvent.Payload1}{(forceApply ? " (forced)" : "")}");
        _bridgeWorker.PushIncomingWorldEvent(worldEvent);
    }

    /// <summary>
    /// Live apply gate for episode-scoped events. Exact episode match, plus casino
    /// catalog↔mission aliases (module sameStage) and graffiti plaza/casino aliases so
    /// co-op partners on the same physical stage get live red-coin hides — not a queue
    /// that only flushes on reload.
    /// </summary>
    private static bool MatchesEpisodeScopedApply(WorldEventPacket worldEvent, byte stageId,
        byte episodeId)
    {
        if (worldEvent.CourseId != stageId)
            return false;
        if (worldEvent.EpisodeId == episodeId)
            return true;

        if (worldEvent.Type == WorldEventType.GraffitiCleaned)
            return GraffitiEpisodesEquivalent(stageId, worldEvent.EpisodeId, episodeId);

        // Red coins / gold / NPC clean / fruit / NPC react: same casino episode aliases as
        // MatchesPendingEpisodeFlush and module red_coin_sync::sameStage.
        if (stageId == SirenaCasinoMapping.AreaId &&
            SirenaCasinoMapping.EpisodesEquivalent(worldEvent.EpisodeId, episodeId))
            return true;

        return false;
    }

    /// <summary>
    /// Plaza: all dolpic episodes share one physical pollution canvas (no soft-reload).
    /// Casino: catalog 0/1 ↔ beach mission 3/4.
    /// </summary>
    private static bool GraffitiEpisodesEquivalent(byte courseId, byte a, byte b)
    {
        if (a == b)
            return true;
        if (courseId == StoryFlagAuthority.PlazaAreaId)
            return true;
        if (courseId == SirenaCasinoMapping.AreaId)
            return SirenaCasinoMapping.EpisodesEquivalent(a, b);
        return false;
    }

    private static bool MatchesPendingEpisodeFlush(WorldEventPacket worldEvent, byte stageId,
        byte episodeId)
    {
        if (worldEvent.CourseId != stageId)
            return false;
        if (worldEvent.EpisodeId == episodeId)
            return true;

        // Graffiti: plaza hub episode aliases + casino catalog↔mission (same as module apply).
        if (worldEvent.Type == WorldEventType.GraffitiCleaned &&
            GraffitiEpisodesEquivalent(stageId, worldEvent.EpisodeId, episodeId))
            return true;

        // Sirena casino: module publishes mission 3/4; warp flush uses catalog 0/1.
        if (stageId == SirenaCasinoMapping.AreaId &&
            SirenaCasinoMapping.EpisodesEquivalent(worldEvent.EpisodeId, episodeId))
            return true;

        // StoryFlagAuthority coalesces plaza Type5 to PlazaHubEpisode (0xFF). Drain those
        // (and any allowlist plaza hub triggers) on any local plaza visit.
        return stageId == StoryFlagAuthority.PlazaAreaId &&
               worldEvent.Type == WorldEventType.TriggerFlag &&
               (worldEvent.EpisodeId == StoryFlagAuthority.PlazaHubEpisode ||
                StoryFlagAuthority.IsPlazaHubTrigger(worldEvent.CourseId, worldEvent.Payload1));
    }

    private void FlushPendingEpisodeWorldEvents(byte stageId, byte episodeId)
    {
        WorldEventPacket[] ready;
        lock (_pendingEpisodeWorldEventsLock)
        {
            // Discard any legacy-queued HipDropObject (pre-fix builds / in-flight sessions).
            // Never stage-enter replay pounds — virgin HipDropHideObj panels must stay up.
            _pendingEpisodeWorldEvents.RemoveAll(e => e.Type == WorldEventType.HipDropObject);

            if (_pendingEpisodeWorldEvents.Count == 0)
                return;

            // Snapshot matching events but leave them queued until DrainWorldEventReplayAsync
            // pushes each one. Cancelling an in-flight drain (resync / new flush) must not
            // permanently drop events that never reached the bridge.
            // Plaza hub TriggerFlags use episode 0xFF — drain them on any plaza visit.
            ready = _pendingEpisodeWorldEvents
                .Where(worldEvent => MatchesPendingEpisodeFlush(worldEvent, stageId, episodeId))
                .OrderBy(worldEvent => worldEvent.EventId)
                .ToArray();

            if (ready.Length == 0)
                return;
        }

        Log?.Invoke(
            $"World sync: flushing {ready.Length} queued episode events for stage {stageId}/{episodeId}");
        var ct = BeginWorldEventReplay();
        _ = DrainWorldEventReplayAsync(ready, ct);
    }

    private void SendLocalSnapshot(PlayerSnapshot snap)
    {
        try
        {
            NameTagAppearance appearance = NameTagAppearance.CreateDefault();
            var hasCustomAppearance = false;
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
                hasCustomAppearance = true;
            }

            // Pure username first, then legacy wire encoding of colors into Name[].
            // Receivers decode appearance from the overlay, then SetName() again so
            // Dolphin never displays the truncated 5-char overlay form.
            snap.SetName(_config.Config.Username);
            if (hasCustomAppearance)
            {
                snap.SetNameTagAppearance(
                    appearance.TextTopR, appearance.TextTopG, appearance.TextTopB,
                    appearance.TextBottomR, appearance.TextBottomG, appearance.TextBottomB,
                    appearance.OutlineR, appearance.OutlineG, appearance.OutlineB,
                    appearance.GradientEnabled);
            }

            _bridgeWorker.ApplyLocalNameTagAppearance(_config.Config.Username, appearance);
            // Model id is applied on join / combo change / launch — not every ~60 Hz snapshot.
            if (_client?.IsConnected == true)
                _client.PublishSnapshot(snap);

            if (_server != null && IsHosting && IsConnected)
            {
                var state = _bridgeWorker.LinkState == DolphinLinkState.ModuleReady
                    ? DolphinState.Active
                    : DolphinState.Booting;
                _server.UpdatePlayerState(LocalSlot,
                    YoshiSnapshotCodec.LogicalStageId(snap, snap.StageId),
                    YoshiSnapshotCodec.LogicalEpisodeId(snap, snap.EpisodeId),
                    state,
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

    private void OnBridgeLinkStateChanged(DolphinLinkState state)
    {
        var previous = _previousLinkState;
        _previousLinkState = state;
        DolphinLinkStateChanged?.Invoke(state);

        if (state == DolphinLinkState.ModuleReady)
        {
            CancelPendingGameCloseCheck();
            return;
        }

        if (previous != DolphinLinkState.ModuleReady || !_monitor.IsDolphinRunning)
            return;

        if (!IsHosting && !IsConnected)
            return;

        ScheduleGameCloseCheck();
    }

    private void CancelPendingGameCloseCheck()
    {
        Interlocked.Increment(ref _gameCloseCheckGeneration);
        _gameCloseCts?.Cancel();
        _gameCloseCts?.Dispose();
        _gameCloseCts = null;
    }

    private void ScheduleGameCloseCheck()
    {
        if (_shuttingDown)
            return;

        CancelPendingGameCloseCheck();
        var generation = Volatile.Read(ref _gameCloseCheckGeneration);
        _gameCloseCts = new CancellationTokenSource();
        var token = _gameCloseCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(GameCloseGraceMs, token).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _gameCloseCheckGeneration) || _shuttingDown)
                    return;
                if (!_monitor.IsDolphinRunning)
                    return;
                if (_bridgeWorker.LinkState == DolphinLinkState.ModuleReady)
                    return;
                if (!IsHosting && !IsConnected)
                    return;

                await EndSessionFromGameOrDolphinClosedAsync(
                    dolphinProcessEnded: false,
                    logMessage: IsHosting
                        ? "Game closed — stopping server and disconnecting."
                        : "Game closed — disconnecting from server.").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer link-state change or shutdown
            }
        }, token);
    }

    private async Task EndSessionFromGameOrDolphinClosedAsync(bool dolphinProcessEnded, string logMessage)
    {
        if (_shuttingDown)
            return;

        if (Interlocked.CompareExchange(ref _sessionEndHandling, 1, 0) != 0)
            return;

        CancelPendingGameCloseCheck();

        try
        {
            var hadSession = IsConnected || IsHosting;
            if (!hadSession)
            {
                Log?.Invoke(dolphinProcessEnded ? "Dolphin closed." : "Game closed.");
                return;
            }

            Log?.Invoke(logMessage);
            await DisconnectAsync(DisconnectReason.DolphinClosed, endSession: IsHosting).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Session teardown after {(dolphinProcessEnded ? "Dolphin" : "game")} close: {ex.Message}");
            StopServer();
        }
        finally
        {
            Interlocked.Exchange(ref _sessionEndHandling, 0);
            SafeRaise(DolphinClosed);
        }
    }

    private void OnDolphinStarted()
    {
        CancelPendingGameCloseCheck();
        _previousLinkState = DolphinLinkState.NotRunning;
        _bridgeWorker.NotifyDolphinRunning(true);
        _bridge.PrepareForRelink();
        _bridgeWorker.InvalidateMailboxWriteCaches();
        _bridge.TryAttach();
        if (IsConnected || IsHosting)
        {
            _bridgeWorker.SetConnected(true, LocalSlot, _config.Config.Username, IsHosting);
            _acceptWorldEventApplies = true;
        }
    }

    private void OnDolphinStopped() => HandleDolphinStopped();

    private void HandleDolphinStopped()
    {
        if (_shuttingDown)
            return;

        CancelPendingGameCloseCheck();
        _previousLinkState = DolphinLinkState.NotRunning;

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

        _ = EndSessionFromGameOrDolphinClosedAsync(
            dolphinProcessEnded: true,
            logMessage: IsHosting
                ? "Dolphin closed — stopping server and disconnecting."
                : "Dolphin closed — disconnecting from server.");
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
        CancelPendingGameCloseCheck();
        CancelWorldEventReplay("shutdown");

        try
        {
            DisconnectAsync(DisconnectReason.UserRequest, endSession: true).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            Interlocked.Increment(ref _clientGeneration);
            var client = Interlocked.Exchange(ref _client, null);
            client?.ForceDispose();
            StopServer();
            ResetClientSessionState();
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
        _networkOpLock.Dispose();
    }
}
