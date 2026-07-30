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
    private volatile SessionLifecyclePhase _phase = SessionLifecyclePhase.Idle;
    private int _gameCloseCheckGeneration;
    private CancellationTokenSource? _gameCloseCts;
    private CancellationTokenSource? _worldReplayCts;
    private DolphinLinkState _previousLinkState = DolphinLinkState.NotRunning;
    private const int GameCloseGraceMs = 1500;
    private PlayerSnapshot _lastLocalSnapshot;
    private bool _hasLastLocalSnapshot;
    private byte _lastProgressResyncStage = 0xFF;
    private byte _lastProgressResyncEpisode = 0xFF;
    private DateTime _lastStageEnterProgressResyncUtc = DateTime.MinValue;
    private static readonly TimeSpan StageEnterProgressResyncDebounce = TimeSpan.FromSeconds(8);
    /// <summary>
    /// Cheap client-driven heal while parked on a stage. Advertises only the seq the module
    /// has actually applied (not launcher <c>_lastAppliedProgressSeq</c>, which advances on
    /// Push / Unchanged) so a backed-up apply cannot soft-kill ownership via Unchanged silence.
    /// </summary>
    private static readonly TimeSpan ProgressCatchupInterval = TimeSpan.FromSeconds(20);
    private DateTime _lastProgressCatchupUtc = DateTime.MinValue;
    private readonly AuthorityHealGovernor _authorityHeal = new();
    private readonly List<WorldEventPacket> _pendingEpisodeWorldEvents = new();
    private readonly object _pendingEpisodeWorldEventsLock = new();
    /// <summary>
    /// Off-stage episode queue bound. Coalesce red/NPC; hard-drop fruit; never retain
    /// ownership (ownership is live-applied). Authorities rebuild red/NPC on stage-enter.
    /// </summary>
    private const int MaxPendingEpisodeWorldEvents = 64;
    /// <summary>Grep-friendly heal telemetry counters (Phase 3).</summary>
    private int _telemetryCacheHeal;
    private int _telemetryTcpForceRetry;
    private int _telemetryCircuitOpen;
    /// <summary>
    /// Independent of OnLocalSnapshot — guarantees force-timeout restage/expand even if
    /// the bridge poll path stalls (2026-07-21 soft-death).
    /// </summary>
    private Timer? _forceHealWatchdog;
    private readonly object _forceHealWatchdogLock = new();
    /// <summary>
    /// Serializes world-progress / stage-enter / force-heal mutations across
    /// <see cref="OnLocalSnapshot"/> (bridge thread-pool),
    /// <see cref="OnWorldProgressSnapshotReceived"/> (NetClient thread-pool),
    /// force-heal watchdog, warp, and module-request paths. Without this, overlapping
    /// callbacks can skip/duplicate force heals or filter mission bits to the wrong stage.
    /// </summary>
    private readonly object _worldProgressLock = new();
    private uint _lastAppliedProgressSeq;
    private readonly ProgressOwnershipTracker _appliedProgress = new();
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
    /// <summary>Fired when Host/Connect/Disconnect lifecycle phase changes (UI button gating).</summary>
    public event Action<SessionLifecyclePhase>? PhaseChanged;

    public bool IsHosting => _server?.IsRunning == true;
    public bool IsConnected => _client?.IsConnected == true;
    /// <summary>Authoritative session phase for UI — prefer over <see cref="IsConnected"/>.</summary>
    public SessionLifecyclePhase Phase => _phase;
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
        // Tracked (not fire-and-forget): the bridge holds the Dolphin localPending lane and
        // retries until this reports the frame actually reached the server.
        _bridgeWorker.LocalWorldEventSendAsync = SendLocalWorldEventAsync;
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

        var hostProfile = GameProfileDetector.Detect(_config.Config.IsoPath);
        if (hostProfile.Kind == GameProfileKind.Unknown)
        {
            throw new InvalidOperationException(
                "Cannot host: the Game ISO path is not a recognized Super Mario Sunshine or " +
                "Super Mario Eclipse target. Set Paths → Game ISO first.");
        }

        await _networkOpLock.WaitAsync().ConfigureAwait(true);
        try
        {
            // Hold the network lock for the entire stop→bind→self-join so StatusChanged
            // "Disconnected" cannot re-enable Host/Connect mid-rehost (double-Host race).
            SetPhase(SessionLifecyclePhase.Hosting);
            StatusChanged?.Invoke("Starting server...");

            ResetHideSeekIfActiveOnServer();
            await TearDownClientAsync(DisconnectReason.UserRequest, sendGoodbye: true)
                .ConfigureAwait(true);
            ForceGameModeToNormalLocally();
            StopServer();

            var port = _config.Config.ServerPort;
            var levels = _levels;
            var maxPlayers = _config.Config.MaxPlayers;
            var expectedProfile = (ushort)hostProfile.Id;

            try
            {
                await Task.Run(() =>
                {
                    _server = new GameServer(levels)
                    {
                        MaxPlayers = maxPlayers,
                        ExpectedGameProfileId = expectedProfile,
                    };
                    _server.Log += m => Log?.Invoke(m);
                    _server.Start(port);
                    _config.Config.AllowClientTeleporting = false;
                    _server.SetAllowClientTeleport(false);
                    _server.SetHideSeekGraceDurationMs(_config.Config.HideSeekGraceSeconds * 1000);
                    ApplyConfiguredSyncSettings();
                }).ConfigureAwait(true);

                StatusChanged?.Invoke("Waiting for listener...");
                await _server!.WaitUntilAcceptingAsync(timeoutMs: 2000).ConfigureAwait(true);

                StatusChanged?.Invoke("Connecting as host...");
                await ConnectClientCoreAsync("127.0.0.1", port, isHost: true).ConfigureAwait(true);

                SetPhase(SessionLifecyclePhase.Hosted);
                HostingStateChanged?.Invoke();
                Log?.Invoke(
                    $"Hosting on port {port} (build {ProtocolConstants.ModBuildId}, " +
                    $"profile: {hostProfile.DisplayName})");
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.AddressAlreadyInUse
                    or SocketError.AccessDenied
                    or SocketError.AddressNotAvailable)
            {
                StopServer();
                SetPhase(SessionLifecyclePhase.Idle);
                StatusChanged?.Invoke("Disconnected");
                throw new InvalidOperationException(
                    $"Port {_config.Config.ServerPort} is already in use or blocked ({ex.SocketErrorCode}). " +
                    "Stop any old BSMSO.ServerHost.exe / previous host, wait a second, then Host again.", ex);
            }
            catch (SocketException ex)
            {
                // Self-join to 127.0.0.1 can fail with ConnectionRefused/TimedOut even after a
                // successful bind — do not mislabel those as "port in use" (looks like Radmin/VPN).
                StopServer();
                SetPhase(SessionLifecyclePhase.Idle);
                StatusChanged?.Invoke("Disconnected");
                throw new InvalidOperationException(
                    $"Could not finish hosting ({ex.SocketErrorCode}): local join to 127.0.0.1:{port} failed. " +
                    "Wait a second and Host again.", ex);
            }
            catch
            {
                StopServer();
                SetPhase(SessionLifecyclePhase.Idle);
                StatusChanged?.Invoke("Disconnected");
                throw;
            }
        }
        finally
        {
            _networkOpLock.Release();
        }
    }

    public async Task ConnectAsync()
    {
        if (IsHosting || _phase is SessionLifecyclePhase.Hosting or SessionLifecyclePhase.Hosted)
        {
            throw new InvalidOperationException(
                "Already hosting — use Disconnect first to join another server.");
        }

        if (SessionLifecycle.IsTransient(_phase) ||
            _phase is SessionLifecyclePhase.Connected)
        {
            throw new InvalidOperationException(
                $"Cannot connect while session is {SessionLifecycle.ToLogLabel(_phase)}.");
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
            await ConnectClientCoreAsync(host, port, isHost).ConfigureAwait(true);
        }
        finally
        {
            _networkOpLock.Release();
        }
    }

    /// <summary>Assumes <see cref="_networkOpLock"/> is already held.</summary>
    private async Task ConnectClientCoreAsync(string host, int port, bool isHost)
    {
        SetPhase(isHost ? SessionLifecyclePhase.Hosting : SessionLifecyclePhase.Connecting);
        await TearDownClientAsync(DisconnectReason.UserRequest, sendGoodbye: true).ConfigureAwait(true);

        var generation = Interlocked.Increment(ref _clientGeneration);
        var client = new NetClient();
        client.Log += m => Log?.Invoke(m);
        client.JoinRejected += reason =>
        {
            if (generation != Volatile.Read(ref _clientGeneration))
                return;
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
        client.RosterUpdated += entries =>
        {
            if (generation != Volatile.Read(ref _clientGeneration))
                return;
            OnRosterUpdated(entries);
        };
        client.WarpCommandReceived += (target, course, episode, requester) =>
        {
            if (generation != Volatile.Read(ref _clientGeneration))
                return;
            OnWarpCommand(target, course, episode, requester);
        };
        client.SnapshotReceived += (slot, snap) =>
        {
            if (generation != Volatile.Read(ref _clientGeneration))
                return;
            OnSnapshotReceived(slot, snap);
        };
        client.MarioVoiceEventReceived += (slot, voice) =>
        {
            if (generation != Volatile.Read(ref _clientGeneration))
                return;
            OnMarioVoiceEventReceived(slot, voice);
        };
        client.WorldEventReceived += worldEvent =>
        {
            if (generation != Volatile.Read(ref _clientGeneration) || !_acceptWorldEventApplies)
                return;
            OnWorldEventReceived(worldEvent);
        };
        client.WorldStateReplayReceived += events =>
        {
            if (generation != Volatile.Read(ref _clientGeneration) || !_acceptWorldEventApplies)
                return;
            OnWorldStateReplayReceived(events);
        };
        client.WorldProgressSnapshotReceived += snapshot =>
        {
            if (generation != Volatile.Read(ref _clientGeneration) || !_acceptWorldEventApplies)
                return;
            OnWorldProgressSnapshotReceived(snapshot);
        };
        client.SyncSettingsReceived += (f, o, p) =>
        {
            if (generation != Volatile.Read(ref _clientGeneration))
                return;
            UpdateSyncSettingsState(f, o, p);
            _bridgeWorker.ApplySyncSettings(f, o, p);
            Log?.Invoke($"Sync settings from host: flags={f} objects={o} progress={p}");
        };
        client.ClientTeleportSettingsReceived += allowed =>
        {
            if (generation != Volatile.Read(ref _clientGeneration))
                return;
            AllowClientTeleport = allowed;
            ClientTeleportPolicyKnown = true;
            ClientTeleportPolicyChanged?.Invoke();
        };
        client.GameModeStateReceived += state =>
        {
            if (generation != Volatile.Read(ref _clientGeneration))
                return;
            ApplyGameModeState(state);
        };
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
                marioModelId: _config.Config.SelectedMarioModelId,
                gameProfileId: (ushort)GameProfileDetector.Detect(_config.Config.IsoPath).Id).ConfigureAwait(true);
            FlushSnapshotsAfterConnect();
            SetPhase(isHost ? SessionLifecyclePhase.Hosted : SessionLifecyclePhase.Connected);
        }
        catch (NetJoinRejectedException ex)
        {
            await TearDownClientAsync(DisconnectReason.UserRequest, sendGoodbye: false).ConfigureAwait(true);
            if (isHost)
                StopServer();
            SetPhase(SessionLifecyclePhase.Idle);
            StatusChanged?.Invoke("Disconnected");
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch
        {
            await TearDownClientAsync(DisconnectReason.UserRequest, sendGoodbye: false).ConfigureAwait(true);
            if (isHost)
                StopServer();
            SetPhase(SessionLifecyclePhase.Idle);
            StatusChanged?.Invoke("Disconnected");
            throw;
        }
    }

    private void SetPhase(SessionLifecyclePhase phase)
    {
        if (_phase == phase)
            return;
        _phase = phase;
        try
        {
            PhaseChanged?.Invoke(phase);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"PhaseChanged handler error: {ex.Message}");
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

            PlayerSnapshot? flushSnap = null;
            lock (_worldProgressLock)
            {
                if (_hasLastLocalSnapshot)
                {
                    var snap = _lastLocalSnapshot;
                    snap.Connected = 1;
                    flushSnap = snap;
                }
            }

            if (flushSnap is { } readySnap)
                SendLocalSnapshot(readySnap);

            client.SendSnapshotNow();
            _bridgeWorker.FlushRemoteSnapshotsToDolphin();
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
            SetPhase(endSession && IsHosting
                ? SessionLifecyclePhase.Stopping
                : SessionLifecyclePhase.Disconnecting);
            ResetHideSeekIfActiveOnServer();
            await TearDownClientAsync(reason, sendGoodbye: true).ConfigureAwait(true);
            ForceGameModeToNormalLocally();
            if (endSession)
                StopServer();
            SetPhase(SessionLifecyclePhase.Idle);
            StatusChanged?.Invoke("Disconnected");
        }
        finally
        {
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
        // Mirror ClearPendingWorldProgressApplies: a rehost/reconnect while Dolphin stays
        // open must drop the progress snapshot lane and launcher seq so join heals with
        // seq=1 are not rejected against a stale moduleAppliedSeq / lastApplied.
        lock (_worldProgressLock)
        {
            ClearPendingWorldProgressAppliesUnlocked();
            _lastProgressResyncStage = 0xFF;
            _lastProgressResyncEpisode = 0xFF;
            _lastStageEnterProgressResyncUtc = DateTime.MinValue;
        }
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

            // Stale callback after intentional TearDown / successful reconnect.
            if (_client == null &&
                _phase is SessionLifecyclePhase.Idle or SessionLifecyclePhase.Disconnecting
                    or SessionLifecyclePhase.Stopping)
                return;

            SetPhase(stopServer ? SessionLifecyclePhase.Stopping : SessionLifecyclePhase.Disconnecting);
            ResetHideSeekIfActiveOnServer();
            await TearDownClientAsync(reason, sendGoodbye: false).ConfigureAwait(false);
            if (stopServer)
                StopServer();
            ForceGameModeToNormalLocally();
            SetPhase(SessionLifecyclePhase.Idle);
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
                // Give writers a moment to flush ServerShutdown before linger-0 close.
                Thread.Sleep(150);
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
        if (GameProfileDetector.Detect(_config.Config.IsoPath).IsEclipse &&
            (syncFlags || syncObjects || syncProgress))
        {
            // Keep the local bridge + UI state honest with the server-side Eclipse coercion.
            syncFlags = false;
            syncObjects = false;
            syncProgress = false;
            Log?.Invoke(
                "Flag/Object/Progress sync disabled for Super Mario Eclipse (Phase 1) — " +
                "Eclipse collectible maps are not measured yet.");
        }
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

        var launchProfile = GameProfileDetector.Detect(isoPath);
        if (launchProfile.IsEclipse)
        {
            // Eclipse keeps its own identity (GMSE04) — never patch it to GMSE90, and skip
            // the GMSE90-keyed BSMSO banner/cover writes.
            Log?.Invoke("Super Mario Eclipse detected — game id, banner, and cover left untouched.");
        }
        else
        {
            if (!DolphinConfigService.EnsureBsmsGameIdentity(isoPath, m => Log?.Invoke(m), out _, out error))
                return false;

            if (!DolphinConfigService.EnsureBsmsGameBanner(isoPath, m => Log?.Invoke(m), out var bannerError))
                Log?.Invoke($"Warning: {bannerError}");

            if (!DolphinConfigService.EnsureBsmsGameCover(dolphinPath, m => Log?.Invoke(m), out var coverError))
                Log?.Invoke($"Warning: {coverError}");
        }

        DolphinConfigService.ClearDolphinGameListCache(dolphinPath, m => Log?.Invoke(m));

        if (!DolphinConfigService.ApplyLaunchDolphinSettings(
                dolphinPath,
                _config.Config.ApplyRecommendedDolphinSettings,
                m => Log?.Invoke(m),
                out error))
            return false;

        if (!_config.Config.ApplyRecommendedDolphinSettings)
            Log?.Invoke("Skipped recommended Dolphin performance profile (disabled in Connection).");

        // Disc/Kuribo Mods / model packs / Moveset PRM are Install-only — Launch must
        // not rewrite the shared game tree (multi-instance races re-injected Moveset).
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

        LevelCatalog.ResolveWarpDestination(courseId, episodeId, out courseId, out episodeId);

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

        LevelCatalog.ResolveWarpDestination(courseId, episodeId, out courseId, out episodeId);

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

    public void ResetSessionProgress()
    {
        if (!IsHosting || _server == null)
            return;

        // Drop queued pre-reset ownership events before the broadcast so a stale
        // ShineCollected cannot re-apply after the module clears FlagManager.
        ClearPendingWorldProgressApplies();
        _server.ResetSessionProgress();
    }

    private void ClearPendingWorldProgressApplies()
    {
        lock (_worldProgressLock)
            ClearPendingWorldProgressAppliesUnlocked();
    }

    private void ClearPendingWorldProgressAppliesUnlocked()
    {
        // Cancel any in-flight DrainWorldEventReplayAsync so a captured ready[] cannot
        // re-apply pre-clear events after SessionProgressReset / empty snapshot.
        CancelWorldEventReplay("pending progress clear");
        _bridgeWorker.ClearPendingIncomingWorldEvents();
        _bridgeWorker.ClearProgressSnapshot();
        lock (_pendingEpisodeWorldEventsLock)
            _pendingEpisodeWorldEvents.Clear();
        _appliedProgress.Clear();
        _lastAppliedProgressSeq = 0;
        _lastProgressCatchupUtc = DateTime.MinValue;
        _authorityHeal.Reset();
        DisarmForceHealWatchdog();
    }

    /// <summary>Legacy name — forwards to <see cref="ResetSessionProgress"/>.</summary>
    public void ResetShineBlueProgress() => ResetSessionProgress();

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

            // Always adopt roster slots for UDP gatekeeping. Gating this on IsConnected
            // dropped the JoinAccepted roster when TCP briefly reported disconnected
            // during rehost, so OnSnapshotReceived forever ignored remotes until restart.
            _activeRosterSlots.Clear();
            foreach (var entry in entries)
                _activeRosterSlots.Add(entry.Slot);

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
            {
                _bridgeWorker.EnqueueRosterHudEvent(RosterHudEventKind.Connected, slot, name);
                Log?.Invoke($"{name} connected.");
            }
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
                        MarioPackInstaller.EnsurePackPresent(
                            path, id, m => Log?.Invoke(m), replaceExisting: !IsDolphinRunning);
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

        // Defense: older hosts may still broadcast beach catalog 6/7; remap so flush /
        // progress keys match hotel interior authority (area 7).
        LevelCatalog.ResolveWarpDestination(courseId, episodeId, out courseId, out episodeId);

        if (targetSlot == ProtocolConstants.WarpAllSlots)
            WarpEveryoneReceived?.Invoke(courseId, episodeId);

        if (!_bridgeWorker.ApplyWarp(targetSlot, courseId, episodeId, IsHosting))
        {
            Log?.Invoke("Warp queued — waiting for Dolphin link");
            return;
        }

        lock (_worldProgressLock)
        {
            FlushPendingEpisodeWorldEvents(courseId, episodeId);
            RequestWorldProgressResyncUnlocked($"warp {courseId}/{episodeId}", forceFull: true);
        }
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
        ApplyMusicVolumeToBridge();
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
                MarioPackInstaller.EnsurePackPresent(
                    isoPath, modelId, m => Log?.Invoke(m), replaceExisting: !IsDolphinRunning);
                lock (_packEnsureLock)
                    _ensuredMarioPackIds.Add(modelId);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Model pack install skipped: {ex.Message}");
            }
        });
    }

    public void ApplyMusicVolumeToBridge()
    {
        _bridgeWorker.ApplyMusicVolume(_config.Config.MusicVolumePercent);
    }

    public void SetMusicVolumePercent(int percent)
    {
        _config.Config.MusicVolumePercent = Math.Clamp(percent, 0, 100);
        _config.SaveDebounced();
        _bridgeWorker.ApplyMusicVolume(_config.Config.MusicVolumePercent);
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
        lock (_worldProgressLock)
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

            // Plaza/casino episode drift (decideNextScenario) is NOT a stage load — treating it
            // as stage-enter spammed WorldProgressRequest ("stage-enter 1/5") and flooded
            // authority replays under 10-player hub play.
            var leftProgressStage = _hasLastLocalSnapshot &&
                                    !SameProgressResyncStage(logicalStage, logicalEpisode,
                                        lastLogicalStage, lastLogicalEpisode);

            // Same-stage death reload does not change course/episode, so leftProgressStage is false —
            // but the module clears its red-coin mask on stageInit. Detect revive (Dead vfx
            // clearing) and force an immediate progress catch-up instead of waiting ~45s.
            var wasDead = _hasLastLocalSnapshot &&
                          (_lastLocalSnapshot.VfxFlags & (ushort)VfxFlags.Dead) != 0;
            var isDead = (snap.VfxFlags & (ushort)VfxFlags.Dead) != 0;
            var sameStageRevive = _hasLastLocalSnapshot && !leftProgressStage && !firstSnapshot &&
                                  wasDead && !isDead && logicalStage != 0;

            _lastLocalSnapshot = snap;
            _hasLastLocalSnapshot = true;
            if (firstSnapshot || leftProgressStage)
            {
                FlushPendingEpisodeWorldEvents(logicalStage, logicalEpisode);
                MaybeRequestStageEnterProgressResync(logicalStage, logicalEpisode, firstSnapshot);
            }
            else if (sameStageRevive)
            {
                // stageInit cleared local red/NPC masks; must force a full mailbox rewrite
                // even when server progressSeq is unchanged.
                RequestWorldProgressResyncUnlocked(
                    $"same-stage-revive {logicalStage}/{logicalEpisode}", forceFull: true);
            }
            else
            {
                MaybeRequestProgressCatchup();
            }
        }

        SendLocalSnapshot(snap);
        // BridgeWorker's poll loop flushes immediately after this callback with
        // the already-read mailbox. Forcing here performed a second
        // ReadProcessMemory plus duplicate serialization every 60 Hz tick.
    }

    private void MaybeRequestProgressCatchup()
    {
        if (_client?.IsConnected != true)
            return;

        var now = DateTime.UtcNow;

        // Authority-first: never storm TCP forever. On timeout restage from the cached
        // authority snapshot (or open a short circuit). This closes the Jul-20 soft-death
        // where force-progress-retry looped 100+ times with an empty mailbox.
        if (_authorityHeal.ForceReplyTimedOut(now))
        {
            HandleForceProgressTimeoutUnlocked(now);
            return;
        }

        if (now - _lastProgressCatchupUtc < ProgressCatchupInterval)
            return;
        // Avoid stacking on top of a recent stage-enter flood.
        if (now - _lastStageEnterProgressResyncUtc < TimeSpan.FromSeconds(5))
            return;

        _lastProgressCatchupUtc = now;

        // Without a mailbox ack we cannot prove module apply — skip rather than advertise
        // launcher lastApplied (Unchanged silence soft-kills ownership mid-run).
        if (!_bridgeWorker.TryGetProgressSnapshotAck(out var hostSeq, out var moduleAppliedSeq))
            return;

        if (ProgressMailboxHealPending(hostSeq, moduleAppliedSeq))
        {
            // Prefer re-push of the still-pending heal; escalate to force-full if the
            // working buffer no longer has a payload to rewrite.
            if (_bridgeWorker.TryRepushPendingProgressSnapshot())
            {
                Log?.Invoke(
                    $"World sync: re-pushed pending progress snapshot hostSeq={hostSeq} (moduleApplied={moduleAppliedSeq})");
                return;
            }

            RequestWorldProgressResyncUnlocked("periodic-catchup-pending-apply", forceFull: true);
            return;
        }

        // Non-force: advertise only what Dolphin has bulk-applied (never synthetic cache seq).
        RequestWorldProgressResyncUnlocked("periodic-catchup", forceFull: false,
            advertiseSeq: PeriodicCatchupAdvertiseSeq(moduleAppliedSeq, _lastAppliedProgressSeq));
    }

    private void HandleForceProgressTimeout(DateTime now)
    {
        // Callers that already hold _worldProgressLock (OnLocalSnapshot) rely on Monitor
        // reentrancy; the watchdog takes the lock here.
        lock (_worldProgressLock)
            HandleForceProgressTimeoutUnlocked(now);
    }

    private void HandleForceProgressTimeoutUnlocked(DateTime now)
    {
        var decision = _authorityHeal.OnForceTimeout(now);
        switch (decision.Action)
        {
            case ForceTimeoutDecision.Kind.RestageFromCacheAndClearAwait:
                Log?.Invoke(
                    $"World sync: force-timeout cache-restage attempt={_authorityHeal.TcpForceAttempts}/{AuthorityHealGovernor.MaxTcpForceAttempts} hostSeq={decision.HostSeq}");
                if (decision.Snapshot != null)
                {
                    if (TryRestageAuthoritySnapshot(decision.Snapshot, decision.HostSeq,
                            "force-timeout-cache"))
                    {
                        var cacheHeal = Interlocked.Increment(ref _telemetryCacheHeal);
                        Log?.Invoke(
                            $"World sync: cacheHeal={cacheHeal} force-timeout healed from authority cache hostSeq={decision.HostSeq}");
                    }
                    else
                    {
                        // Mirror RequestWorldProgressResync: serialize/mailbox miss must
                        // still expand so force-timeout never leaves ownership unhealed.
                        Log?.Invoke(
                            "World sync: force-timeout expand-from-cache — restage missed");
                        ApplyProgressSnapshotViaEvents(decision.Snapshot);
                    }
                }

                // Build 33: governor already cleared await. Do NOT re-arm the watchdog —
                // that was the 2s restage storm when TCP stayed silent after stage-enter.
                // Build 36: no best-effort seq=0 TCP after cache restage — that force-reheal
                // storm (×players on every warp) was the mid-run TCP flood.
                DisarmForceHealWatchdog();
                break;

            case ForceTimeoutDecision.Kind.RetryTcp:
            {
                var tcpRetry = Interlocked.Increment(ref _telemetryTcpForceRetry);
                Log?.Invoke(
                    $"World sync: tcpForceRetry={tcpRetry} attempt={decision.Attempt}/{AuthorityHealGovernor.MaxTcpForceAttempts}");
                // Do not re-enter BeginForce — that would reset the attempt counter.
                if (AuthorityHealGovernor.ShouldClearMailboxBeforeForceTcp(
                        _authorityHeal.HasAuthorityCache()))
                    _bridgeWorker.ClearProgressSnapshot();
                ArmForceHealWatchdog();
                Log?.Invoke("World sync: requesting progress resync (force-progress-retry) seq=0 force");
                _ = _client?.SendWorldProgressRequestAsync(0);
                break;
            }

            case ForceTimeoutDecision.Kind.OpenCircuit:
            {
                var circuitOpen = Interlocked.Increment(ref _telemetryCircuitOpen);
                Log?.Invoke(
                    $"World sync: circuitOpen={circuitOpen} for {AuthorityHealGovernor.CircuitCooldown.TotalSeconds:0}s — abandon TCP storm, heal from expand if cache returns");
                if (_authorityHeal.PeekAuthorityCache() is { } cached)
                    ApplyProgressSnapshotViaEvents(cached);
                break;
            }
        }
    }

    private void RequestWorldProgressResync(string reason, bool forceFull = false,
        uint? advertiseSeq = null)
    {
        lock (_worldProgressLock)
            RequestWorldProgressResyncUnlocked(reason, forceFull, advertiseSeq);
    }

    private void RequestWorldProgressResyncUnlocked(string reason, bool forceFull = false,
        uint? advertiseSeq = null)
    {
        if (_client?.IsConnected != true)
            return;

        if (forceFull)
        {
            var plan = _authorityHeal.BeginForce(DateTime.UtcNow);
            switch (plan.Action)
            {
                case ForceHealPlan.Kind.CircuitBlocked:
                    Log?.Invoke(
                        $"World sync: skipped force resync ({reason}) — heal circuit open");
                    return;

                case ForceHealPlan.Kind.RestageFromCache:
                    // Never ClearProgressSnapshot when authority is cached — that empty
                    // window is the force-progress soft-death. Push forces moduleApplied=0
                    // so same-seq reheal still applies. Build 33: restage IS the heal —
                    // NoteForceSatisfied and do not arm the watchdog. Best-effort TCP
                    // refresh below must not re-create the 2s await storm.
                    if (plan.Snapshot != null)
                    {
                        if (TryRestageAuthoritySnapshot(plan.Snapshot, plan.HostSeq, reason))
                        {
                            var cacheHeal = Interlocked.Increment(ref _telemetryCacheHeal);
                            Log?.Invoke(
                                $"World sync: cacheHeal={cacheHeal} restaged authority ({reason}) hostSeq={plan.HostSeq}");
                            _authorityHeal.NoteForceSatisfied();
                        }
                        else
                        {
                            // Mailbox write failed — expand immediately so stage-enter
                            // force always applies ownership within this call.
                            Log?.Invoke(
                                $"World sync: stage-enter force expand-from-cache ({reason}) — mailbox write missed");
                            ApplyProgressSnapshotViaEvents(plan.Snapshot);
                        }
                    }
                    DisarmForceHealWatchdog();
                    // Build 36: cache restage completed the heal — do NOT send best-effort
                    // seq=0 TCP. That was amplifying force-reheal traffic on every stage-enter
                    // (4 peers × warps → progressSeq hundreds with few shines).
                    return;

                case ForceHealPlan.Kind.ClearAndRequestTcp:
                    // First heal of the session — no cache yet. Await TCP body rewrite.
                    if (AuthorityHealGovernor.ShouldClearMailboxBeforeForceTcp(hasAuthorityCache: false))
                        _bridgeWorker.ClearProgressSnapshot();
                    ArmForceHealWatchdog();
                    break;
            }

            if (!plan.RequestTcpRefresh)
                return;
        }

        var proofSeq = advertiseSeq ?? _lastAppliedProgressSeq;
        var seq = ClientProgressRequestSeq(proofSeq, forceFull);
        Log?.Invoke(
            $"World sync: requesting progress resync ({reason}) seq={seq}{(forceFull ? " force" : "")}");
        _ = _client.SendWorldProgressRequestAsync(seq);
    }

    /// <summary>
    /// Fire force-timeout handling even when LocalSnapshotReady stops ticking.
    /// </summary>
    private void ArmForceHealWatchdog()
    {
        lock (_forceHealWatchdogLock)
        {
            _forceHealWatchdog?.Dispose();
            _forceHealWatchdog = new Timer(_ =>
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (_authorityHeal.ForceReplyTimedOut(now))
                        HandleForceProgressTimeout(now);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"World sync: force-heal watchdog error: {ex.Message}");
                }
            }, null, AuthorityHealGovernor.ForceReplyTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisarmForceHealWatchdog()
    {
        lock (_forceHealWatchdogLock)
        {
            _forceHealWatchdog?.Dispose();
            _forceHealWatchdog = null;
        }
    }

    /// <summary>
    /// Write a cached authority snapshot into the progress mailbox with a synthetic hostSeq.
    /// </summary>
    private bool TryRestageAuthoritySnapshot(WorldProgressSnapshot snapshot, uint hostSeq, string reason)
    {
        var stageId = (byte)0;
        var episodeId = (byte)0;
        var hasStage = _hasLastLocalSnapshot;
        if (hasStage)
        {
            stageId = YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId);
            episodeId = YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot, _lastLocalSnapshot.EpisodeId);
        }

        var mailboxSnap = snapshot.WithMissionFilteredToStage(stageId, episodeId, hasStage);
        byte[] payload;
        try
        {
            payload = PacketSerializer.BuildWorldProgressSnapshotPayload(mailboxSnap);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"World sync: authority cache serialize failed ({reason}): {ex.Message}");
            return false;
        }

        if (payload.Length > ProtocolConstants.CommProgressSnapshotMaxPayload)
        {
            ApplyProgressSnapshotViaEvents(snapshot);
            return true;
        }

        _bridgeWorker.ClearNonOwnershipIncomingWorldEvents();
        var wrote = _bridgeWorker.PushProgressSnapshot(hostSeq == 0 ? 1 : hostSeq, payload);
        if (!wrote)
        {
            Log?.Invoke($"World sync: authority cache mailbox write failed ({reason}) — expand");
            ApplyProgressSnapshotViaEvents(snapshot);
            return true;
        }

        _appliedProgress.NoteSnapshotEvents(mailboxSnap.ExpandToWorldEvents(), replaceMission: true);
        if (hasStage)
        {
            _appliedProgress.PruneMissionToStage(stageId, episodeId);
            FlushPendingEpisodeWorldEvents(stageId, episodeId);
        }

        Log?.Invoke(
            $"World sync: authority cache → mailbox ({reason}) hostSeq={hostSeq} bytes={payload.Length}");
        return true;
    }

    /// <summary>Force-full heals advertise seq 0 so the server cannot reply unchanged.</summary>
    internal static uint ClientProgressRequestSeq(uint lastAppliedProgressSeq, bool forceFull)
        => forceFull ? 0u : lastAppliedProgressSeq;

    /// <summary>
    /// True when the launcher has written a progress heal the module has not yet applied.
    /// Periodic catch-up must not advertise launcher lastApplied in this state — the server
    /// would reply Unchanged and ownership heal soft-dies until stage-enter force-full.
    /// </summary>
    internal static bool ProgressMailboxHealPending(uint hostSeq, uint moduleAppliedSeq)
        => hostSeq > moduleAppliedSeq;

    /// <summary>
    /// Non-force periodic catch-up advertises only what Dolphin bulk-applied — but never a
    /// legacy synthetic cache-heal hostSeq (0x60000000 band). Those poisoned server proof
    /// seq and left catch-up advertising garbage after stage-enter restage (2026-07-21).
    /// </summary>
    internal static uint PeriodicCatchupAdvertiseSeq(uint moduleAppliedSeq,
        uint lastRealProgressSeq = 0)
    {
        if (AuthorityHealGovernor.IsCacheHealHostSeq(moduleAppliedSeq))
            return lastRealProgressSeq;
        return moduleAppliedSeq;
    }

    /// <summary>
    /// True when a failed <c>ApplyWorldEventToBridge</c> during episode flush should stop
    /// the whole ready batch. Only session teardown aborts; episode re-queue / HipDrop drop
    /// must <c>continue</c> so later gold/red/NPC events still drain.
    /// </summary>
    internal static bool ShouldAbortWorldEventDrainOnApplyFailure(bool acceptWorldEventApplies)
        => !acceptWorldEventApplies;

    /// <summary>
    /// Force-full must not clear the mailbox when an authority cache can restage — that
    /// empty window is the force-progress soft-death. Only clear on the first heal
    /// (no cache yet). Cache age does not matter.
    /// </summary>
    internal static bool ShouldClearMailboxBeforeForceTcp(bool hasAuthorityCache)
        => AuthorityHealGovernor.ShouldClearMailboxBeforeForceTcp(hasAuthorityCache);

    /// <summary>
    /// Force-full previously cleared the progress mailbox before the TCP request. Only a
    /// real (changed) snapshot rewrites that lane — an Unchanged ack must not clear the
    /// await flag, or a stale periodic-catch-up reply soft-kills ownership heal until
    /// the next stage-enter.
    /// </summary>
    internal static bool ClearsForceProgressAwait(bool snapshotUnchanged)
        => AuthorityHealGovernor.ClearsForceProgressAwait(snapshotUnchanged);

    /// <summary>
    /// Build 27: force-timeout cache restage must expand when restage returns false
    /// (serialize miss), matching <c>RequestWorldProgressResync</c>.
    /// </summary>
    internal static bool ForceTimeoutRestageExpandsOnFailure => true;

    /// <summary>
    /// Build 27: progress-snapshot serialize failure must expand via events rather than
    /// returning and leaving force-await hung until the watchdog timeout.
    /// </summary>
    internal static bool ProgressSnapshotSerializeFailureExpands => true;

    /// <summary>
    /// True when two snapshots are the same co-op progress stage. Plaza scenarios share one
    /// hub; casino catalog↔mission aliases match. Used so decideNextScenario mid-visit does
    /// not look like a stage-enter resync.
    /// </summary>
    internal static bool SameProgressResyncStage(byte stageA, byte episodeA, byte stageB,
        byte episodeB)
    {
        if (stageA != stageB)
            return false;
        return LevelCatalog.EpisodesEquivalent(stageA, episodeA, episodeB);
    }

    private void MaybeRequestStageEnterProgressResync(byte stageId, byte episodeId, bool firstSnapshot)
    {
        // Ignore transient zero-area snapshots during boot/load — they caused
        // stage-enter 0/1 → 1/5 double requests and flushed episode queues wrongly.
        if (!firstSnapshot && stageId == 0)
            return;

        var now = DateTime.UtcNow;
        if (!firstSnapshot &&
            SameProgressResyncStage(stageId, episodeId, _lastProgressResyncStage,
                _lastProgressResyncEpisode) &&
            now - _lastStageEnterProgressResyncUtc < StageEnterProgressResyncDebounce)
        {
            Log?.Invoke(
                $"World sync: skipped duplicate progress resync (stage-enter {stageId}/{episodeId})");
            return;
        }

        _lastProgressResyncStage = stageId;
        _lastProgressResyncEpisode = episodeId;
        _lastStageEnterProgressResyncUtc = now;
        RequestWorldProgressResyncUnlocked(
            $"{(firstSnapshot ? "initial-stage" : "stage-enter")} {stageId}/{episodeId}",
            forceFull: true);
    }

    private DateTime _lastModuleProgressResyncUtc = DateTime.MinValue;

    private void OnModuleProgressResyncRequested()
    {
        lock (_worldProgressLock)
        {
            // Module holds BF_REQUEST_PROGRESS for several frames — debounce to one TCP request.
            if ((DateTime.UtcNow - _lastModuleProgressResyncUtc).TotalMilliseconds < 2000)
                return;
            _lastModuleProgressResyncUtc = DateTime.UtcNow;
            RequestWorldProgressResyncUnlocked("module-request-progress", forceFull: true);
        }
    }

    private void OnLocalMarioVoice(MarioVoiceEvent voiceEvent)
    {
        if (_client?.IsConnected != true || voiceEvent.IsEmpty)
            return;

        _ = _client.SendMarioVoiceEventAsync(voiceEvent);
    }

    /// <summary>
    /// Publishes one durable world event and reports whether it reached the server. False
    /// keeps the event queued in <see cref="BridgeWorker"/> (retry, then reconnect replay);
    /// the server's authorities heal *from* their own state, so a mutation they never
    /// received is unrecoverable for the rest of the session.
    /// </summary>
    private async Task<bool> SendLocalWorldEventAsync(WorldEventRequest worldEvent)
    {
        if (worldEvent.IsEmpty)
            return true;

        // Phase A TCP durable-only: never send fruit / react / hip-drop / gold. Dropping
        // these is intentional policy, not a failure — ack so the lane clears.
        if (!WorldEventTcpPolicy.ShouldSendLocalWorldEvent(worldEvent.Type))
            return true;

        var client = _client;
        if (client?.IsConnected != true)
            return false;

        Log?.Invoke(
            $"World sync: sending type={worldEvent.Type} course={worldEvent.CourseId}/{worldEvent.EpisodeId} payload0={worldEvent.Payload0} reserved={worldEvent.Reserved} payload1={worldEvent.Payload1}");
        return await client.TrySendWorldEventAsync(worldEvent).ConfigureAwait(false);
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
            // GraffitiCleaned intentionally omitted — goop sync permanently disabled.
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
        lock (_worldProgressLock)
        {
            // Phase A: ignore ephemeral leftovers from mixed-build peers — never wedge mission.
            if (WorldEventTcpPolicy.IsNonNetworkedEphemeral(worldEvent.Type))
                return;

            if (worldEvent.Type == WorldEventType.SessionProgressReset)
                ClearPendingWorldProgressAppliesUnlocked();

            // Note only after a real bridge enqueue. Queued-off-stage mission events must not
            // be marked applied — a later heal would filter them from expand while the pending
            // episode queue (previously wiped on heal) had already dropped them.
            if (ApplyWorldEventToBridge(worldEvent) &&
                worldEvent.Type != WorldEventType.SessionProgressReset)
                _appliedProgress.NoteLiveEvent(worldEvent);
        }
    }

    private void OnWorldProgressSnapshotReceived(WorldProgressSnapshot snapshot)
    {
        lock (_worldProgressLock)
            OnWorldProgressSnapshotReceivedUnlocked(snapshot);
    }

    private void OnWorldProgressSnapshotReceivedUnlocked(WorldProgressSnapshot snapshot)
    {
        if (snapshot.Unchanged)
        {
            // Stale unchanged replies (e.g. late periodic catch-up) must not satisfy a
            // force-full await — mailbox was cleared and still needs a body rewrite.
            if (_authorityHeal.IsAwaitingForce)
            {
                Log?.Invoke(
                    $"World sync: ignoring unchanged progress ack while awaiting force-full seq={snapshot.ProgressSeq}");
                return;
            }

            // Refresh cache stamp only — Unchanged must not clear force-await.
            _authorityHeal.NoteAuthoritySnapshot(snapshot, DateTime.UtcNow);

            if (snapshot.ProgressSeq != 0)
                _lastAppliedProgressSeq = snapshot.ProgressSeq;
            Log?.Invoke(
                $"World sync: progress snapshot unchanged seq={snapshot.ProgressSeq}");
            return;
        }

        // Authority-first: cache the full server snapshot for local restage / circuit heal.
        // Do NOT clear force-await here — NoteAuthoritySnapshot only caches; await clears
        // via NoteForceSatisfied after a successful mailbox write or expand fallback.
        _authorityHeal.NoteAuthoritySnapshot(snapshot, DateTime.UtcNow);

        // Track full authority for live delta filtering, then bulk-apply via mailbox lane
        // (O(1) heal) instead of expanding into N single-slot drains.
        var stageId = (byte)0;
        var episodeId = (byte)0;
        var hasStage = _hasLastLocalSnapshot;
        if (hasStage)
        {
            stageId = YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId);
            episodeId = YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot, _lastLocalSnapshot.EpisodeId);
        }

        var mailboxSnap = snapshot.WithMissionFilteredToStage(stageId, episodeId, hasStage);
        byte[] payload;
        try
        {
            payload = PacketSerializer.BuildWorldProgressSnapshotPayload(mailboxSnap);
        }
        catch (Exception ex)
        {
            // Expand immediately — leaving force-await for timeout only delays ownership
            // heal and skips the same fallback RequestWorldProgressResync already uses.
            Log?.Invoke($"World sync: progress snapshot serialize failed: {ex.Message} — expand");
            ApplyProgressSnapshotViaEvents(snapshot);
            return;
        }

        if (payload.Length > ProtocolConstants.CommProgressSnapshotMaxPayload)
        {
            Log?.Invoke(
                $"World sync: progress snapshot too large for mailbox ({payload.Length} > {ProtocolConstants.CommProgressSnapshotMaxPayload}) — falling back to event expand");
            ApplyProgressSnapshotViaEvents(snapshot);
            return;
        }

        // Do NOT wipe _pendingEpisodeWorldEvents — gold / fruit / NpcReact / non-hub
        // TriggerFlag are absent from WorldProgressSnapshot and would be permanently lost.
        // Do NOT CancelWorldEventReplay here — that races stage-enter drains of those events.
        // Only strip ephemeral bridge spam; ownership/mission pending stays.
        _bridgeWorker.ClearNonOwnershipIncomingWorldEvents();

        var wrote = _bridgeWorker.PushProgressSnapshot(snapshot.ProgressSeq == 0 ? 1 : snapshot.ProgressSeq,
            payload);
        if (wrote)
        {
            _authorityHeal.NoteForceSatisfied();
            DisarmForceHealWatchdog();
            if (snapshot.ProgressSeq != 0)
                _lastAppliedProgressSeq = snapshot.ProgressSeq;

            // Note only what the mailbox actually carries. Ownership is always included;
            // off-stage mission rows were filtered out of mailboxSnap and must stay
            // eligible for a later stage-enter heal / expand fallback.
            // Replace mission notes from the heal so tracker masks cannot grow unbounded.
            _appliedProgress.NoteSnapshotEvents(mailboxSnap.ExpandToWorldEvents(), replaceMission: true);
            if (hasStage)
                _appliedProgress.PruneMissionToStage(stageId, episodeId);

            // Re-drive any deferred episode events still waiting (heal must not strand them).
            if (hasStage)
                FlushPendingEpisodeWorldEvents(stageId, episodeId);
        }
        else
        {
            Log?.Invoke("World sync: progress snapshot mailbox write failed — falling back to event expand");
            ApplyProgressSnapshotViaEvents(snapshot);
            return;
        }

        Log?.Invoke(
            $"World sync: progress snapshot → mailbox seq={snapshot.ProgressSeq} bytes={payload.Length} ownership≈{snapshot.OwnershipEventCount} mission≈{mailboxSnap.MissionEventCount}");
    }

    private void ApplyProgressSnapshotViaEvents(WorldProgressSnapshot snapshot)
    {
        var events = snapshot.ExpandToWorldEvents();
        // Never filter ownership on heal expand — optimistic live notes / lost mailbox
        // applies must not permanently suppress shine/blue/story from authority heals.
        var delta = _appliedProgress.FilterNewEvents(events, filterOwnership: false);
        // Preserve deferred episode queue (gold/fruit/triggers not in snapshot).
        _bridgeWorker.ClearNonOwnershipIncomingWorldEvents();

        var anyApplied = false;
        foreach (var worldEvent in delta.OrderBy(e => IsLiveOwnershipWorldEvent(e) ? 0 : 1)
                     .ThenBy(e => e.EventId))
        {
            if (!ApplyWorldEventToBridge(worldEvent, forceApply: false))
                continue;
            _appliedProgress.NoteLiveEvent(worldEvent);
            anyApplied = true;
        }

        // Count expand as a successful force-full apply even when delta was empty
        // (authority already reflected) — mailbox path was unavailable but we tried.
        _authorityHeal.NoteForceSatisfied();
        DisarmForceHealWatchdog();
        if (snapshot.ProgressSeq != 0)
            _lastAppliedProgressSeq = snapshot.ProgressSeq;

        if (_hasLastLocalSnapshot)
        {
            var stageId = YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId);
            var episodeId = YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot, _lastLocalSnapshot.EpisodeId);
            _appliedProgress.PruneMissionToStage(stageId, episodeId);
            FlushPendingEpisodeWorldEvents(stageId, episodeId);
        }

        if (!anyApplied && delta.Count > 0)
            Log?.Invoke(
                $"World sync: progress expand queued/deferred {delta.Count} events (none pushed live)");
    }

    private void OnWorldStateReplayReceived(WorldEventPacket[] events)
    {
        lock (_worldProgressLock)
            OnWorldStateReplayReceivedUnlocked(events);
    }

    private void OnWorldStateReplayReceivedUnlocked(WorldEventPacket[] events)
    {
        Log?.Invoke($"World sync: received {events.Length} legacy replayed/resync events from server");

        // Legacy WorldStateReplay path (older hosts). Same heal policy as compact snapshots:
        // drop ephemeral only — never clear ownership pending or the deferred episode queue.
        _bridgeWorker.ClearNonOwnershipIncomingWorldEvents();

        if (events.Length == 0)
            return;

        var delta = _appliedProgress.FilterNewEvents(events, filterOwnership: false);
        foreach (var worldEvent in delta.OrderBy(e => IsLiveOwnershipWorldEvent(e) ? 0 : 1)
                     .ThenBy(e => e.EventId))
        {
            if (!ApplyWorldEventToBridge(worldEvent, forceApply: false))
                continue;
            _appliedProgress.NoteLiveEvent(worldEvent);
        }

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

            // false also means intentional episode-scoped re-queue (FlushPending used the
            // destination stage while _lastLocalSnapshot is still the previous one) or a
            // live-only HipDrop drop. Do not abort the rest of the ready batch.
            if (!ApplyWorldEventToBridge(worldEvent, forceApply: false))
                continue;

            _appliedProgress.NoteLiveEvent(worldEvent);

            // Remove only after a successful push so a cancelled drain (resync /
            // overlapping flush) can re-deliver undelivered episode events.
            lock (_pendingEpisodeWorldEventsLock)
                _pendingEpisodeWorldEvents.RemoveAll(pending => pending.EventId == worldEvent.EventId);

            // Brief pace only — dual ownership mailbox means we must not block 5s/event
            // behind mission applies (that previously stalled stage-enter for minutes).
            var deadline = DateTime.UtcNow.AddMilliseconds(120);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (_bridgeWorker.TryGetLastAppliedEventId(out var lastApplied) &&
                    lastApplied >= worldEvent.EventId)
                {
                    break;
                }

                try
                {
                    await Task.Delay(16, ct);
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

    private bool ApplyWorldEventToBridge(WorldEventPacket worldEvent, bool forceApply = false)
    {
        if (worldEvent.EventId == 0)
            return false;

        // Drop applies after disconnect so a cancelled drain / late TCP packet cannot
        // resurrect collectible state into an offline session.
        if (!_acceptWorldEventApplies)
            return false;

        if (!forceApply &&
            IsEpisodeScopedWorldEvent(worldEvent) &&
            (!_hasLastLocalSnapshot ||
             !MatchesEpisodeScopedApply(worldEvent,
                 YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId),
                 YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot, _lastLocalSnapshot.EpisodeId))))
        {
            // Hard-drop fruit / NPC react / gold off-stage — never queue (Phase 3).
            // Red/NPC coalesce into the pending-episode ring; ownership is never deferred here.
            if (IsHardDropPendingEpisodeEvent(worldEvent.Type))
            {
                Log?.Invoke(
                    $"World sync: drop off-stage ephemeral eventId={worldEvent.EventId} type={worldEvent.Type}");
                return false;
            }

            lock (_pendingEpisodeWorldEventsLock)
            {
                if (_pendingEpisodeWorldEvents.All(pending => pending.EventId != worldEvent.EventId))
                {
                    _pendingEpisodeWorldEvents.Add(worldEvent);
                    PrunePendingEpisodeWorldEventsUnlocked();
                }
            }
            var localStage = _hasLastLocalSnapshot
                ? $"{_lastLocalSnapshot.StageId}/{_lastLocalSnapshot.EpisodeId}"
                : "not-ready";
            Log?.Invoke(
                $"World sync: queued episode-local eventId={worldEvent.EventId} type={worldEvent.Type} — local stage {localStage}, event {worldEvent.CourseId}/{worldEvent.EpisodeId}");
            return false;
        }

        // HipDrop is live-only: never queue for later stage-enter flush (would hide virgin
        // THipDropHideObj panels, e.g. Sirena Ep5 purple roulette pad).
        if (!forceApply && worldEvent.Type == WorldEventType.HipDropObject)
        {
            if (!_hasLastLocalSnapshot)
            {
                Log?.Invoke(
                    $"World sync: drop HipDropObject eventId={worldEvent.EventId} — local stage not-ready");
                return false;
            }

            var localStage = YoshiSnapshotCodec.LogicalStageId(_lastLocalSnapshot, _lastLocalSnapshot.StageId);
            var localEpisode = YoshiSnapshotCodec.LogicalEpisodeId(_lastLocalSnapshot,
                _lastLocalSnapshot.EpisodeId);
            var stageOk = worldEvent.CourseId == localStage;
            var episodeOk = LevelCatalog.EpisodesEquivalent(localStage, worldEvent.EpisodeId,
                localEpisode);
            if (!stageOk || !episodeOk)
            {
                Log?.Invoke(
                    $"World sync: drop HipDropObject eventId={worldEvent.EventId} — local stage {localStage}/{localEpisode}, event {worldEvent.CourseId}/{worldEvent.EpisodeId}");
                return false;
            }
        }

        Log?.Invoke(
            $"World sync: applying eventId={worldEvent.EventId} type={worldEvent.Type} course={worldEvent.CourseId}/{worldEvent.EpisodeId} payload0={worldEvent.Payload0} reserved={worldEvent.Reserved} payload1={worldEvent.Payload1}{(forceApply ? " (forced)" : "")}");
        _bridgeWorker.PushIncomingWorldEvent(worldEvent);
        return true;
    }

    /// <summary>
    /// Live apply gate for episode-scoped events. Uses the same plaza / casino / hotel /
    /// Ricco / Pinna equivalence as server <c>StagesEquivalent</c> so catalog-normalized
    /// broadcasts apply while the local snapshot still shows director mission ids.
    /// </summary>
    internal static bool MatchesEpisodeScopedApply(WorldEventPacket worldEvent, byte stageId,
        byte episodeId)
    {
        if (worldEvent.CourseId != stageId)
            return false;
        return LevelCatalog.EpisodesEquivalent(stageId, worldEvent.EpisodeId, episodeId);
    }

    internal static bool MatchesPendingEpisodeFlush(WorldEventPacket worldEvent, byte stageId,
        byte episodeId)
    {
        if (worldEvent.CourseId != stageId)
            return false;
        if (LevelCatalog.EpisodesEquivalent(stageId, worldEvent.EpisodeId, episodeId))
            return true;

        // StoryFlagAuthority coalesces plaza Type5 to PlazaHubEpisode (0xFF). Drain those
        // (and any allowlist plaza hub triggers) on any local plaza visit. Plaza already
        // matches via EpisodesEquivalent; this covers allowlisted hub triggers whose
        // course/episode encoding may still need the explicit IsPlazaHubTrigger check.
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

    /// <summary>
    /// Bound the off-stage queue. Hard-drop fruit/gold/react; coalesce red/NPC;
    /// never retain more than <see cref="MaxPendingEpisodeWorldEvents"/>. Ownership is
    /// never queued here (live-applied), so this path cannot block shine/blue/story.
    /// </summary>
    private void PrunePendingEpisodeWorldEventsUnlocked()
    {
        // Always hard-drop fruit / gold / NPC react — even under the soft cap.
        _pendingEpisodeWorldEvents.RemoveAll(e => IsHardDropPendingEpisodeEvent(e.Type));

        // Always coalesce red/NPC to latest mask per (stage, type, index).
        var seen = new HashSet<(WorldEventType Type, byte Course, byte Episode, byte Index)>();
        for (var i = _pendingEpisodeWorldEvents.Count - 1; i >= 0; i--)
        {
            var e = _pendingEpisodeWorldEvents[i];
            if (e.Type is not (WorldEventType.RedCoinCollected or WorldEventType.NpcCleaned))
                continue;
            var key = (e.Type, e.CourseId, e.EpisodeId, e.Reserved);
            if (!seen.Add(key))
                _pendingEpisodeWorldEvents.RemoveAt(i);
        }

        while (_pendingEpisodeWorldEvents.Count > MaxPendingEpisodeWorldEvents)
            _pendingEpisodeWorldEvents.RemoveAt(0);
    }

    private static bool IsHardDropPendingEpisodeEvent(WorldEventType type) =>
        type is WorldEventType.MarioFruitKicked
            or WorldEventType.MarioFruitPicked
            or WorldEventType.MarioFruitThrown
            or WorldEventType.MarioFruitDropped
            or WorldEventType.MarioFruitSync
            or WorldEventType.YoshiFruitTaken
            or WorldEventType.NpcReact
            or WorldEventType.GoldCoinCollected
            or WorldEventType.HipDropObject
            or WorldEventType.GraffitiCleaned;

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
        DisarmForceHealWatchdog();
        _bridgeWorker.Dispose();
        _bridge.Dispose();
        _monitor.Dispose();
        _networkOpLock.Dispose();
    }
}
