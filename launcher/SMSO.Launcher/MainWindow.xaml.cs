using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using SMSO.Bridge;
using SMSO.Launcher.Controls;
using SMSO.Net;

namespace SMSO.Launcher;

public partial class MainWindow : Window
{
    private readonly ConfigService _config = new();
    private readonly SessionCoordinator _session;
    private readonly ObservableCollection<RosterViewModel> _rosterItems = new();
    private readonly ObservableCollection<WarpTargetItem> _warpTargets = new();
    private readonly ObservableCollection<WarpTargetItem> _clientWarpTargets = new();
    private LevelCatalog? _levels;
    private byte[] _lastRosterSlots = Array.Empty<byte>();
    private bool _sessionShutdownComplete;
    private DispatcherTimer? _previewDebounceTimer;
    private DispatcherTimer? _dolphinUiTimer;
    private RosterViewModel? _hideSeekDragSource;
    private Point _hideSeekDragStartPoint;
    private bool _suppressHideSeekUiSync;
    private bool _tagRunning;

    public MainWindow()
    {
        InitializeComponent();
        ClientRosterList.ItemsSource = _rosterItems;
        ServerRosterList.ItemsSource = _rosterItems;
        WarpTargetCombo.ItemsSource = _warpTargets;
        ClientWarpTargetCombo.ItemsSource = _clientWarpTargets;
        _config.Load();
        _session = new SessionCoordinator(_config);
        WireEvents();
        LoadConfigToUi();
        LoadLevels();
        Title = _config.InstanceIndex == 0
            ? "BSMSO — Better Super Mario Sunshine Online"
            : $"BSMSO — Better Super Mario Sunshine Online ({_config.InstanceLabel})";
        VersionText.Text = $"BSMSO v1.0 | comm v{ProtocolConstants.CommVersion} | {_config.InstanceLabel} | .NET {Environment.Version}";
        UpdateConnectionUi();
        UpdateDolphinUi();
        UpdateSessionStatusColor();
        UpdateLiveNameTagPreview();

        _dolphinUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _dolphinUiTimer.Tick += (_, _) => SafeRunOnUiThread(RefreshDolphinStateUi);
        _dolphinUiTimer.Start();

        _previewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _previewDebounceTimer.Tick += (_, _) =>
        {
            _previewDebounceTimer!.Stop();
            SafeRunOnUiThread(UpdateLiveNameTagPreview);
        };

        WireAutoSaveFields();
        BindRosterColumnStretch(ClientRosterList, 1.1, 1.8, 1.8, 0.9);
        BindRosterColumnStretch(ServerRosterList, 1.0, 1.6, 1.6, 0.5, 0.9);
    }

    private static void BindRosterColumnStretch(ListView listView, params double[] weights)
    {
        void Apply()
        {
            if (listView.View is not GridView gridView || gridView.Columns.Count != weights.Length)
                return;

            var width = listView.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8;
            if (width < 160)
                return;

            var totalWeight = weights.Sum();
            for (var i = 0; i < weights.Length; i++)
                gridView.Columns[i].Width = Math.Max(56, width * weights[i] / totalWeight);
        }

        listView.SizeChanged += (_, _) => Apply();
        listView.Loaded += (_, _) => Apply();
    }

    private void WireAutoSaveFields()
    {
        void QueueSave() => _config.SaveDebounced();

        DolphinPathBox.TextChanged += (_, _) => QueueSave();
        IsoPathBox.TextChanged += (_, _) => QueueSave();
        ServerIpBox.TextChanged += (_, _) => QueueSave();
        ServerPortBox.TextChanged += (_, _) => QueueSave();
        MaxPlayersBox.TextChanged += (_, _) => QueueSave();
        UsernameBox.TextChanged += (_, _) =>
        {
            QueuePreviewRefresh();
            QueueSave();
        };
    }

    private void QueuePreviewRefresh()
    {
        _previewDebounceTimer?.Stop();
        _previewDebounceTimer?.Start();
    }

    public void ShowTransientStatus(string message)
    {
        SafeRunOnUiThread(() =>
        {
            LogLine.Text = message;
            _config.Log(message);
        });
    }

    private enum NameTagTarget
    {
        Text,
        Gradient,
        Outline,
    }

    private void RunOnUiThread(Action action) => SafeRunOnUiThread(action);

    private void WireEvents()
    {
        _session.Log += msg => RunOnUiThread(() =>
        {
            LogLine.Text = msg;
            _config.Log(msg);
        });
        _session.StatusChanged += status => RunOnUiThread(() =>
        {
            StatusBadge.Text = status;
            if (status == "Disconnected")
                ClearRoster();
            UpdateConnectionUi();
            UpdateSessionStatusColor();
        });
        _session.RosterUpdated += entries => RunOnUiThread(() => UpdateRosterCore(entries));
        _session.HostingStateChanged += () => RunOnUiThread(() =>
        {
            if (_session.IsHosting)
                AllowClientTeleportToggle.IsChecked = false;
            UpdateConnectionUi();
        });
        _session.ClientTeleportPolicyChanged += () => RunOnUiThread(() =>
        {
            UpdateClientActionsUi();
            UpdateServerClientTeleportStatus();
        });
        _session.DolphinClosed += () => SafeRunOnUiThread(() =>
        {
            RefreshDolphinStateUi();
            ResetGameModeUiToNormal();
            if (!_session.IsConnected && !_session.IsHosting)
                ClearRoster();
        });
        _session.DolphinLinkStateChanged += _ => SafeRunOnUiThread(RefreshDolphinStateUi);
        _session.GameModeStateChanged += state => RunOnUiThread(() => ApplyGameModeStateToUi(state));
        WireNameTagColorControls();
        GameModeCombo.SelectedIndex = 0;
    }

    private void RefreshDolphinStateUi()
    {
        UpdateDolphinUi();
        UpdateConnectionUi();
    }

    private void WireNameTagColorControls()
    {
        NameTagTextRBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Text);
        NameTagTextGBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Text);
        NameTagTextBBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Text);
        NameTagOutlineRBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Outline);
        NameTagOutlineGBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Outline);
        NameTagOutlineBBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Outline);
        NameTagGradientRBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Gradient);
        NameTagGradientGBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Gradient);
        NameTagGradientBBox.TextChanged += (_, _) => OnNameTagRgbChanged(NameTagTarget.Gradient);
        QueuePreviewRefresh();
    }

    private void SafeRunOnUiThread(Action action)
    {
        void Run()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                App.LogException("UI update", ex);
                LogLine.Text = $"UI update skipped: {ex.Message}";
                _config.Log($"UI update skipped: {ex.Message}");
            }
        }

        if (Dispatcher.CheckAccess())
            Run();
        else
            Dispatcher.BeginInvoke(Run);
    }

    private void LoadConfigToUi()
    {
        UsernameBox.Text = _config.Config.Username;
        DolphinPathBox.Text = _config.Config.DolphinPath;
        IsoPathBox.Text = _config.Config.IsoPath;
        ServerIpBox.Text = _config.Config.ServerIp;
        ServerPortBox.Text = _config.Config.ServerPort.ToString();
        MaxPlayersBox.Text = _config.Config.MaxPlayers.ToString();
        ApplyNameTagColorToUi(
            ParseStoredColor(_config.Config.NameTagColor, 255, 255, 255),
            ParseStoredColor(_config.Config.NameTagGradientColor, 136, 136, 136),
            ParseStoredColor(_config.Config.NameTagOutlineColor, 0, 0, 0),
            _config.Config.NameTagGradientEnabled,
            persist: false);
        AllowClientTeleportToggle.IsChecked = _config.Config.AllowClientTeleporting;
    }

    private void SaveConfigFromUi()
    {
        _config.Config.Username = UsernameBox.Text.Trim();
        _config.Config.DolphinPath = DolphinPathBox.Text.Trim();
        _config.Config.IsoPath = IsoPathBox.Text.Trim();
        _config.Config.ServerIp = ServerIpBox.Text.Trim();
        _config.Config.NameTagColor = FormatStoredColor(ReadNameTagColorFromUi(NameTagTarget.Text));
        _config.Config.NameTagGradientColor = FormatStoredColor(ReadNameTagColorFromUi(NameTagTarget.Gradient));
        _config.Config.NameTagOutlineColor = FormatStoredColor(ReadNameTagColorFromUi(NameTagTarget.Outline));
        _config.Config.NameTagGradientEnabled = NameTagGradientToggle.IsChecked == true;
        if (int.TryParse(ServerPortBox.Text, out var port))
            _config.Config.ServerPort = Math.Clamp(port, 1024, 65535);
        if (int.TryParse(MaxPlayersBox.Text, out var max))
            _config.Config.MaxPlayers = Math.Clamp(max, 2, ProtocolConstants.StableMaxPlayers);
        _config.SaveDebounced();
    }

    private void LoadLevels()
    {
        var levelsPath = FindLevelsPath();
        _session.Initialize(levelsPath);
        if (!File.Exists(levelsPath)) return;
        _levels = LevelCatalog.Load(levelsPath);
        ClientLevelCombo.ItemsSource = _levels.Courses;
        ServerLevelCombo.ItemsSource = _levels.Courses;
        if (_levels.Courses.Count > 0)
        {
            ClientLevelCombo.SelectedIndex = 0;
            ServerLevelCombo.SelectedIndex = 0;
        }
        ClientLevelCombo.SelectionChanged += (_, _) => UpdateEpisodeCombo(ClientLevelCombo, ClientEpisodeCombo);
        ServerLevelCombo.SelectionChanged += (_, _) => UpdateEpisodeCombo(ServerLevelCombo, ServerEpisodeCombo);
        UpdateEpisodeCombo(ClientLevelCombo, ClientEpisodeCombo);
        UpdateEpisodeCombo(ServerLevelCombo, ServerEpisodeCombo);
    }

    private static string FindLevelsPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "assets", "levels.ntsc-u.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "levels.ntsc-u.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "levels.ntsc-u.json"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static void UpdateEpisodeCombo(FastSelector levelCombo, FastSelector episodeCombo)
    {
        if (levelCombo.SelectedItem is CourseEntry course)
        {
            episodeCombo.SelectedItem = null;
            episodeCombo.SelectedIndex = -1;
            episodeCombo.ItemsSource = null;
            episodeCombo.ItemsSource = course.Episodes;
            episodeCombo.SelectedIndex = 0;
        }
    }

    private void ClearRoster()
    {
        _rosterItems.Clear();
        _warpTargets.Clear();
        _clientWarpTargets.Clear();
        _lastRosterSlots = Array.Empty<byte>();
        HideSeekHidersList.ItemsSource = null;
        HideSeekSeekersList.ItemsSource = null;
        _tagRunning = false;
        StartStopTagButton.Content = "Start Tag";
    }

    private void UpdateRosterCore(PlayerRosterEntry[] entries)
    {
        var selectedWarpSlot = WarpTargetCombo.SelectedItem is WarpTargetItem warp ? warp.Slot : (byte)0;
        var selectedClientWarpSlot = ClientWarpTargetCombo.SelectedItem is WarpTargetItem clientWarp
            ? clientWarp.Slot
            : (byte)0;

        var ordered = entries.OrderBy(e => e.Slot).ToArray();
        var slotSet = ordered.Select(e => e.Slot).ToArray();

        for (var i = _rosterItems.Count - 1; i >= 0; i--)
        {
            if (!slotSet.Contains(_rosterItems[i].Slot))
                _rosterItems.RemoveAt(i);
        }

        foreach (var entry in ordered)
        {
            var row = _rosterItems.FirstOrDefault(r => r.Slot == entry.Slot);
            if (row == null)
            {
                row = new RosterViewModel { Slot = entry.Slot };
                _rosterItems.Add(row);
            }

            row.Username = entry.Username;
            row.StageId = entry.StageId;
            row.EpisodeId = entry.EpisodeId;
            row.LevelName = _levels?.GetCourseName(entry.StageId) ?? entry.StageId.ToString();
            row.EpisodeName = _levels?.GetEpisodeDisplayName(entry.StageId, entry.EpisodeId)
                              ?? $"Episode {entry.EpisodeId + 1}";
            row.Status = entry.State.ToString();
            row.PingMs = entry.PingMs.ToString();
        }

        if (!_lastRosterSlots.SequenceEqual(slotSet))
        {
            _lastRosterSlots = slotSet;
            _warpTargets.Clear();
            foreach (var entry in ordered)
                _warpTargets.Add(new WarpTargetItem { Username = entry.Username, Slot = entry.Slot });

            _clientWarpTargets.Clear();
            foreach (var entry in ordered.Where(e => e.Slot != _session.LocalSlot))
                _clientWarpTargets.Add(new WarpTargetItem { Username = entry.Username, Slot = entry.Slot });

            var warpMatch = _warpTargets.FirstOrDefault(w => w.Slot == selectedWarpSlot);
            WarpTargetCombo.SelectedItem = warpMatch ?? _warpTargets.FirstOrDefault();

            var clientWarpMatch = _clientWarpTargets.FirstOrDefault(w => w.Slot == selectedClientWarpSlot);
            ClientWarpTargetCombo.SelectedItem = clientWarpMatch ?? _clientWarpTargets.FirstOrDefault();

            if (GameModeCombo.SelectedIndex == 1)
                SyncHideSeekRoleListsFromRoster();
        }
    }

    private void BrowseDolphin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Dolphin|Dolphin.exe" };
        if (dlg.ShowDialog() == true)
        {
            DolphinPathBox.Text = dlg.FileName;
            SaveConfigFromUi();
        }
    }

    private void BrowseIso_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "GameCube ISO|*.iso;*.gcm;*.dol" };
        if (dlg.ShowDialog() == true)
        {
            IsoPathBox.Text = dlg.FileName;
            SaveConfigFromUi();
        }
    }

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        SaveConfigFromUi();
        _config.Save();
        if (_session.DolphinLinkState != DolphinLinkState.ModuleReady)
        {
            MessageBox.Show($"Launch Dolphin with {ModuleVersionMessages.ModuleFileName} loaded and wait until BSMSO is linked to the game before hosting.", "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateConnectionUi();
            return;
        }
        HostButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = false;
        StatusBadge.Text = "Starting server...";
        try
        {
            await _session.HostAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Host failed: {ex.Message}", "BSMSO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateConnectionUi();
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        SaveConfigFromUi();
        if (_session.DolphinLinkState != DolphinLinkState.ModuleReady)
        {
            MessageBox.Show($"Launch Dolphin with {ModuleVersionMessages.ModuleFileName} loaded and wait until BSMSO is linked to the game before connecting.", "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateConnectionUi();
            return;
        }
        HostButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = false;
        try
        {
            await _session.ConnectAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Connect failed: {ex.Message}", "BSMSO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateConnectionUi();
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await _session.DisconnectAsync();
    }

    private void LaunchDolphin_Click(object sender, RoutedEventArgs e)
    {
        SaveConfigFromUi();
        if (!TryGetValidatedLaunchPaths(out var dolphin, out var iso, out var validationError))
        {
            MessageBox.Show(validationError, "BSMSO — Paths Required", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!_session.TryLaunchDolphin(dolphin, iso, out var error))
        {
            MessageBox.Show(error ?? "Failed to launch Dolphin.", "BSMSO", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        UpdateDolphinUi();
    }

    private bool TryGetValidatedLaunchPaths(out string dolphin, out string iso, out string error)
    {
        dolphin = DolphinPathBox.Text.Trim().Trim('"');
        iso = IsoPathBox.Text.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(dolphin) && string.IsNullOrWhiteSpace(iso))
        {
            error = "Set the Dolphin executable and game ISO paths in the Paths section below before launching.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dolphin))
        {
            error = "Set the Dolphin executable path in the Paths section below before launching.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(iso))
        {
            error = "Set the game ISO path in the Paths section below before launching.";
            return false;
        }

        if (!File.Exists(dolphin))
        {
            error = $"Dolphin executable not found:\n{dolphin}";
            return false;
        }

        if (!File.Exists(iso))
        {
            error = $"Game ISO not found:\n{iso}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void UpdateDolphinUi()
    {
        var running = _session.IsDolphinRunning;
        var link = _session.DolphinLinkState;
        var pathsOk = TryGetValidatedLaunchPaths(out _, out _, out _);

        LaunchDolphinButton.IsEnabled = !running && pathsOk;
        LaunchDolphinButton.Opacity = LaunchDolphinButton.IsEnabled ? 1.0 : 0.45;

        var processText = running ? "Open" : "Not running";
        var linkText = link switch
        {
            DolphinLinkState.ModuleReady => "Connected",
            DolphinLinkState.Attached => "Attached (resolving mailbox)",
            DolphinLinkState.Running => "Running (not attached)",
            _ => "Disconnected",
        };
        var ok = (Brush)FindResource("SmsStatusOk");
        var bad = (Brush)FindResource("SmsStatusBad");
        var warn = (Brush)FindResource("SmsStatusWarn");

        DolphinProcessBadge.Text = processText;
        DolphinProcessBadge.Foreground = running ? ok : bad;
        DolphinLinkBadge.Text = linkText;
        DolphinLinkBadge.Foreground = link switch
        {
            DolphinLinkState.ModuleReady => ok,
            DolphinLinkState.Attached => warn,
            _ => bad,
        };

        var moduleInstallWarning = ModuleInstallValidator.ValidateInstalledModule(IsoPathBox.Text);
        var linkError = _session.DolphinLinkError;
        var searchSeconds = _session.DolphinMailboxSearchDuration.TotalSeconds;
        DolphinDetailText.Text = link switch
        {
            DolphinLinkState.ModuleReady when !string.IsNullOrWhiteSpace(moduleInstallWarning) => moduleInstallWarning,
            DolphinLinkState.ModuleReady =>
                "BSMSO link active — warps and player sync enabled.",
            DolphinLinkState.Attached when !string.IsNullOrWhiteSpace(linkError) && searchSeconds >= 3 =>
                linkError,
            DolphinLinkState.Attached when searchSeconds < 3 =>
                $"Attached to Dolphin — waiting for game to boot and load {ModuleVersionMessages.ModuleFileName}.",
            DolphinLinkState.Attached =>
                "Searching for BSMSO mailbox — enter a stage in-game if you have not yet.",
            DolphinLinkState.Running when !string.IsNullOrWhiteSpace(linkError) =>
                linkError,
            DolphinLinkState.Running when running =>
                "Dolphin is running — linking automatically.",
            _ when !string.IsNullOrWhiteSpace(moduleInstallWarning) => moduleInstallWarning,
            _ => running
                ? $"Dolphin is open — link will restore when the game loads {ModuleVersionMessages.ModuleFileName}."
                : "Launch Dolphin here before hosting or connecting (button enables when paths are set).",
        };
        UpdateConnectionUi();
    }

    private void UpdateSessionStatusColor()
    {
        var ok = (Brush)FindResource("SmsStatusOk");
        var bad = (Brush)FindResource("SmsStatusBad");
        var warn = (Brush)FindResource("SmsStatusWarn");
        var text = StatusBadge.Text;
        var brush = text is "Connected" or "Hosting" ? ok :
            text == "Connecting" ? warn : bad;
        StatusBadge.Foreground = brush;
        StatusDot.Fill = brush;
    }

    private void UpdateConnectionUi()
    {
        var connected = _session.IsConnected;
        var hosting = _session.IsHosting;
        var gameLinked = _session.DolphinLinkState == DolphinLinkState.ModuleReady;
        var sessionActive = connected || hosting;
        DisconnectButton.IsEnabled = sessionActive;
        ConnectButton.IsEnabled = gameLinked && !sessionActive;
        HostButton.IsEnabled = gameLinked && !sessionActive;
        ConnectButton.Opacity = ConnectButton.IsEnabled ? 1.0 : 0.45;
        HostButton.Opacity = HostButton.IsEnabled ? 1.0 : 0.45;
        DisconnectButton.Opacity = DisconnectButton.IsEnabled ? 1.0 : 0.45;
        GameLinkOverlay.Visibility = gameLinked ? Visibility.Collapsed : Visibility.Visible;
        var serverActionsActive = hosting && connected;
        ServerActionsPanel.IsEnabled = serverActionsActive;
        ServerActionsPanel.Opacity = serverActionsActive ? 1.0 : 0.45;
        ServerActionsOverlay.Visibility = serverActionsActive ? Visibility.Collapsed : Visibility.Visible;
        UpdateClientActionsUi();
        UpdateServerClientTeleportStatus();
        UpdateSessionStatusColor();
    }

    private void UpdateServerClientTeleportStatus()
    {
        if (!_session.IsHosting || !_session.IsConnected)
        {
            ClientTeleportStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        ClientTeleportStatusText.Visibility = Visibility.Visible;
        ClientTeleportStatusText.Text = AllowClientTeleportToggle.IsChecked == true
            ? "Client teleporting is enabled"
            : "Client teleporting is disabled";
    }

    private void UpdateClientActionsUi()
    {
        var connected = _session.IsConnected;
        var hosting = _session.IsHosting;

        bool teleportActive;
        bool showOverlay;

        if (connected && hosting)
        {
            var allowed = AllowClientTeleportToggle.IsChecked == true;
            teleportActive = allowed;
            showOverlay = !allowed;
        }
        else if (connected)
        {
            var policyKnown = _session.ClientTeleportPolicyKnown;
            teleportActive = policyKnown && _session.AllowClientTeleport;
            showOverlay = !policyKnown || !_session.AllowClientTeleport;
        }
        else
        {
            teleportActive = false;
            showOverlay = false;
        }

        ClientTeleportPanel.IsEnabled = teleportActive;
        ClientTeleportPanel.Opacity = teleportActive ? 1.0 : 0.45;
        ClientLevelCombo.IsEnabled = teleportActive;
        ClientEpisodeCombo.IsEnabled = teleportActive;
        ClientWarpButton.IsEnabled = teleportActive;
        ClientWarpTargetCombo.IsEnabled = teleportActive;
        ClientTeleportToPlayerButton.IsEnabled = teleportActive;
        ClientTeleportOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
        ClientTeleportOverlayText.Text = "The host needs to enable client teleporting to use this.";
    }

    private async void ClientWarp_Click(object sender, RoutedEventArgs e)
    {
        if (ClientLevelCombo.SelectedItem is not CourseEntry course ||
            ClientEpisodeCombo.SelectedItem is not EpisodeEntry episode) return;
        await _session.WarpSelfAsync(course.CourseId, episode.EpisodeId);
    }

    private async void ClientTeleportToPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (ClientWarpTargetCombo.SelectedItem is not WarpTargetItem target)
        {
            MessageBox.Show("Select a player in Warp target first.", "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _session.WarpToPlayerAsync(target.Slot);
    }

    private async void ServerTeleportToPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (WarpTargetCombo.SelectedItem is not WarpTargetItem target)
        {
            MessageBox.Show("Select a player in Warp target first.", "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await _session.WarpToPlayerAsync(target.Slot);
    }

    private void ServerWarpAll_Click(object sender, RoutedEventArgs e)
    {
        if (ServerLevelCombo.SelectedItem is not CourseEntry course ||
            ServerEpisodeCombo.SelectedItem is not EpisodeEntry episode) return;
        _session.HostWarp(ProtocolConstants.WarpAllSlots, course.CourseId, episode.EpisodeId);
    }

    private void ServerWarpSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ServerLevelCombo.SelectedItem is not CourseEntry course ||
            ServerEpisodeCombo.SelectedItem is not EpisodeEntry episode) return;

        if (WarpTargetCombo.SelectedItem is not WarpTargetItem target)
            return;

        _session.HostWarp(target.Slot, course.CourseId, episode.EpisodeId);
    }

    private void AllowClientTeleport_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !_session.IsHosting) return;
        _session.SetAllowClientTeleport(AllowClientTeleportToggle.IsChecked == true);
        UpdateServerClientTeleportStatus();
        UpdateClientActionsUi();
    }

    private void HelpLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start("explorer.exe", _config.LogDirectory);
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticsBox.Text = $"Status: {StatusBadge.Text}\nHosting: {_session.IsHosting}\nConnected: {_session.IsConnected}\n" +
                              $"Dolphin running: {_session.IsDolphinRunning}\nDolphin link: {_session.DolphinLinkState}\n" +
                              $"Mailbox search: {_session.DolphinMailboxSearchDuration.TotalSeconds:F1}s\n" +
                              $"Last link error: {_session.DolphinLinkError ?? "None"}\n" +
                              $"Dolphin: {DolphinPathBox.Text}\nISO: {IsoPathBox.Text}\nLog: {_config.LogDirectory}";
    }

    private void PickNameTagTextColor_Click(object sender, RoutedEventArgs e) =>
        OpenNameTagColorPicker(NameTagTarget.Text);

    private void NameTagTextPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenNameTagColorPicker(NameTagTarget.Text);
    }

    private void PickNameTagOutlineColor_Click(object sender, RoutedEventArgs e) =>
        OpenNameTagColorPicker(NameTagTarget.Outline);

    private void NameTagOutlinePreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenNameTagColorPicker(NameTagTarget.Outline);
    }

    private void PickNameTagGradientColor_Click(object sender, RoutedEventArgs e) =>
        OpenNameTagColorPicker(NameTagTarget.Gradient);

    private void NameTagGradientPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenNameTagColorPicker(NameTagTarget.Gradient);
    }

    private void NameTagGradientToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingNameTagColor)
            return;

        UpdateGradientColumnState();
        _config.Config.NameTagGradientEnabled = NameTagGradientToggle.IsChecked == true;
        _config.SaveDebounced();
        QueuePreviewRefresh();
        _session.RefreshPlayerAppearance();
    }

    private void UpdateGradientColumnState()
    {
        var enabled = NameTagGradientToggle.IsChecked == true;
        NameTagGradientColumn.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(NameTagOutlineColumn, enabled ? 4 : 2);
    }

    private void OnNameTagRgbChanged(NameTagTarget target)
    {
        if (_syncingNameTagColor)
            return;

        if (!TryReadNameTagColorFromUi(target, out var color))
        {
            UpdateNameTagPreview(target, invalid: true);
            UpdateLiveNameTagPreview();
            return;
        }

        UpdateNameTagPreview(target, invalid: false);
        switch (target)
        {
            case NameTagTarget.Text:
                _config.Config.NameTagColor = FormatStoredColor(color);
                break;
            case NameTagTarget.Gradient:
                _config.Config.NameTagGradientColor = FormatStoredColor(color);
                break;
            case NameTagTarget.Outline:
                _config.Config.NameTagOutlineColor = FormatStoredColor(color);
                break;
        }
        _config.SaveDebounced();
        QueuePreviewRefresh();
        _session.RefreshPlayerAppearance();
    }

    private void UpdateLiveNameTagPreview()
    {
        var len = UsernameBox.Text.Length;
        UsernameCharCount.Text = $"{len} / 16 characters";

        var gradientEnabled = NameTagGradientToggle.IsChecked == true;
        var textValid = TryReadNameTagColorFromUi(NameTagTarget.Text, out var textColor);
        var gradientValid = TryReadNameTagColorFromUi(NameTagTarget.Gradient, out var gradientColor);
        var outlineValid = TryReadNameTagColorFromUi(NameTagTarget.Outline, out var outlineColor);
        var colorsValid = textValid && outlineValid && (!gradientEnabled || gradientValid);

        if (!textValid)
            textColor = Colors.White;
        if (!gradientValid)
            gradientColor = Color.FromRgb(136, 136, 136);
        if (!outlineValid)
            outlineColor = Colors.Black;

        LiveNameTagPreview.UpdatePreview(
            UsernameBox.Text,
            textColor,
            gradientColor,
            outlineColor,
            gradientEnabled,
            colorsValid);
    }

    private void OpenNameTagColorPicker(NameTagTarget target)
    {
        var textColor = ReadNameTagColorFromUi(NameTagTarget.Text);
        var gradientColor = ReadNameTagColorFromUi(NameTagTarget.Gradient);
        var outlineColor = ReadNameTagColorFromUi(NameTagTarget.Outline);
        var current = target switch
        {
            NameTagTarget.Text => textColor,
            NameTagTarget.Gradient => gradientColor,
            _ => outlineColor,
        };

        if (!ColorPickerWindow.TryPick(this, current, out var pickedColor))
            return;

        switch (target)
        {
            case NameTagTarget.Text:
                textColor = pickedColor;
                break;
            case NameTagTarget.Gradient:
                gradientColor = pickedColor;
                break;
            default:
                outlineColor = pickedColor;
                break;
        }

        ApplyNameTagColorToUi(textColor, gradientColor, outlineColor, NameTagGradientToggle.IsChecked == true);
    }

    private bool _syncingNameTagColor;

    private void ApplyNameTagColorToUi(Color textColor, Color gradientColor, Color outlineColor,
        bool gradientEnabled, bool persist = true)
    {
        _syncingNameTagColor = true;
        try
        {
            NameTagTextRBox.Text = textColor.R.ToString();
            NameTagTextGBox.Text = textColor.G.ToString();
            NameTagTextBBox.Text = textColor.B.ToString();
            NameTagGradientRBox.Text = gradientColor.R.ToString();
            NameTagGradientGBox.Text = gradientColor.G.ToString();
            NameTagGradientBBox.Text = gradientColor.B.ToString();
            NameTagOutlineRBox.Text = outlineColor.R.ToString();
            NameTagOutlineGBox.Text = outlineColor.G.ToString();
            NameTagOutlineBBox.Text = outlineColor.B.ToString();
            NameTagGradientToggle.IsChecked = gradientEnabled;
        }
        finally
        {
            _syncingNameTagColor = false;
        }

        UpdateGradientColumnState();
        UpdateNameTagPreview(NameTagTarget.Text, invalid: false, textColor);
        UpdateNameTagPreview(NameTagTarget.Gradient, invalid: false, gradientColor);
        UpdateNameTagPreview(NameTagTarget.Outline, invalid: false, outlineColor);
        UpdateLiveNameTagPreview();

        _config.Config.NameTagColor = FormatStoredColor(textColor);
        _config.Config.NameTagGradientColor = FormatStoredColor(gradientColor);
        _config.Config.NameTagOutlineColor = FormatStoredColor(outlineColor);
        _config.Config.NameTagGradientEnabled = gradientEnabled;
        if (persist)
        {
            _config.Save();
            _session.RefreshPlayerAppearance();
        }
    }

    private void UpdateNameTagPreview(NameTagTarget target, bool invalid, Color? color = null)
    {
        var preview = target switch
        {
            NameTagTarget.Text => NameTagTextPreview,
            NameTagTarget.Gradient => NameTagGradientPreview,
            _ => NameTagOutlinePreview,
        };
        var boxes = target switch
        {
            NameTagTarget.Text => new[] { NameTagTextRBox, NameTagTextGBox, NameTagTextBBox },
            NameTagTarget.Gradient => new[] { NameTagGradientRBox, NameTagGradientGBox, NameTagGradientBBox },
            _ => new[] { NameTagOutlineRBox, NameTagOutlineGBox, NameTagOutlineBBox },
        };
        var border = (Brush)FindResource(invalid ? "SmsStatusBad" : "SmsBorder");

        foreach (var box in boxes)
            box.BorderBrush = border;

        if (invalid)
        {
            preview.Background = Brushes.Transparent;
            return;
        }

        var resolved = color ?? ReadNameTagColorFromUi(target);
        preview.Background = new SolidColorBrush(resolved);
    }

    private Color ReadNameTagColorFromUi(NameTagTarget target) =>
        TryReadNameTagColorFromUi(target, out var color) ? color : Colors.White;

    private bool TryReadNameTagColorFromUi(NameTagTarget target, out Color color)
    {
        var rBox = target switch
        {
            NameTagTarget.Text => NameTagTextRBox,
            NameTagTarget.Gradient => NameTagGradientRBox,
            _ => NameTagOutlineRBox,
        };
        var gBox = target switch
        {
            NameTagTarget.Text => NameTagTextGBox,
            NameTagTarget.Gradient => NameTagGradientGBox,
            _ => NameTagOutlineGBox,
        };
        var bBox = target switch
        {
            NameTagTarget.Text => NameTagTextBBox,
            NameTagTarget.Gradient => NameTagGradientBBox,
            _ => NameTagOutlineBBox,
        };
        if (!TryParseRgbChannel(rBox.Text, out var r) ||
            !TryParseRgbChannel(gBox.Text, out var g) ||
            !TryParseRgbChannel(bBox.Text, out var b))
        {
            color = Colors.White;
            return false;
        }

        color = Color.FromRgb(r, g, b);
        return true;
    }

    private static bool TryParseRgbChannel(string? value, out byte channel)
    {
        channel = 0;
        if (!int.TryParse((value ?? string.Empty).Trim(), out var parsed))
            return false;
        if (parsed < 0 || parsed > 255)
            return false;
        channel = (byte)parsed;
        return true;
    }

    private static Color ParseStoredColor(string? stored, byte defaultR, byte defaultG, byte defaultB)
    {
        if (TryParseStoredColor(stored, out var color))
            return color;
        return Color.FromRgb(defaultR, defaultG, defaultB);
    }

    private static bool TryParseStoredColor(string? value, out Color color)
    {
        color = Colors.White;
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
            text = text[1..];
        if (text.Length != 6)
            return false;

        try
        {
            color = Color.FromRgb(
                Convert.ToByte(text[..2], 16),
                Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatStoredColor(Color color) =>
        $"{color.R:X2}{color.G:X2}{color.B:X2}";

    private void GameModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHideSeekUiSync || !_session.IsHosting || GameModeCombo.SelectedItem is not ComboBoxItem item)
            return;

        var mode = item.Tag?.ToString() == "HideSeek" ? GameMode.HideSeek : GameMode.Normal;
        HideSeekPanel.Visibility = mode == GameMode.HideSeek ? Visibility.Visible : Visibility.Collapsed;
        _session.SetGameMode(mode);

        if (mode == GameMode.HideSeek)
            SyncHideSeekRoleListsFromRoster(forceAllHiders: true);
    }

    private void GameModeCombo_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ComboBox combo || combo.Items.Count == 0 || !combo.IsMouseOver)
            return;

        e.Handled = true;
        var next = combo.SelectedIndex;
        if (e.Delta > 0)
            next = Math.Max(0, next - 1);
        else
            next = Math.Min(combo.Items.Count - 1, next + 1);

        if (next != combo.SelectedIndex)
            combo.SelectedIndex = next;
    }

    private void ApplyGameModeStateToUi(GameModeStatePacket state)
    {
        _suppressHideSeekUiSync = true;
        try
        {
            GameModeCombo.SelectedIndex = state.GameMode == GameMode.HideSeek ? 1 : 0;
            HideSeekPanel.Visibility = state.GameMode == GameMode.HideSeek ? Visibility.Visible : Visibility.Collapsed;

            if (state.GameMode == GameMode.HideSeek)
            {
                HideSeekHidersList.ItemsSource = null;
                HideSeekSeekersList.ItemsSource = null;
                var hiders = new ObservableCollection<RosterViewModel>();
                var seekers = new ObservableCollection<RosterViewModel>();
                foreach (var row in _rosterItems.OrderBy(r => r.Slot))
                {
                    if (row.Slot >= state.Roles.Length)
                        hiders.Add(row);
                    else if (state.Roles[row.Slot] == HideSeekRole.Seeker)
                        seekers.Add(row);
                    else
                        hiders.Add(row);
                }

                HideSeekHidersList.ItemsSource = hiders;
                HideSeekSeekersList.ItemsSource = seekers;
            }
            else
            {
                HideSeekHidersList.ItemsSource = null;
                HideSeekSeekersList.ItemsSource = null;
                _tagRunning = false;
                StartStopTagButton.Content = "Start Tag";
                HideSeekStatusText.Text = string.Empty;
            }

            _tagRunning = state.TagActive;
            StartStopTagButton.Content = state.TagActive ? "Stop Tag" : "Start Tag";
            HideSeekStatusText.Text = state.GameMode == GameMode.HideSeek
                ? state.RoundComplete
                    ? "All hiders found!"
                    : state.TagActive
                        ? "Tag is running."
                        : "Assign seekers, then start tag."
                : string.Empty;
            UpdateStartStopTagButtonState();
        }
        finally
        {
            _suppressHideSeekUiSync = false;
        }
    }

    private void ResetGameModeUiToNormal()
    {
        ApplyGameModeStateToUi(GameModeStatePacket.CreateDefault());
    }

    private void SyncHideSeekRoleListsFromRoster(bool forceAllHiders = false)
    {
        if (_suppressHideSeekUiSync || GameModeCombo.SelectedIndex != 1)
            return;

        var hiders = HideSeekHidersList.ItemsSource as ObservableCollection<RosterViewModel>
                     ?? new ObservableCollection<RosterViewModel>();
        var seekers = HideSeekSeekersList.ItemsSource as ObservableCollection<RosterViewModel>
                      ?? new ObservableCollection<RosterViewModel>();

        if (HideSeekHidersList.ItemsSource == null)
        {
            hiders = new ObservableCollection<RosterViewModel>();
            seekers = new ObservableCollection<RosterViewModel>();
            HideSeekHidersList.ItemsSource = hiders;
            HideSeekSeekersList.ItemsSource = seekers;
        }

        var known = hiders.Concat(seekers).Select(r => r.Slot).ToHashSet();
        foreach (var row in _rosterItems.OrderBy(r => r.Slot))
        {
            if (known.Contains(row.Slot))
                continue;
            hiders.Add(row);
        }

        for (var i = hiders.Count - 1; i >= 0; i--)
        {
            if (_rosterItems.All(r => r.Slot != hiders[i].Slot))
                hiders.RemoveAt(i);
        }

        for (var i = seekers.Count - 1; i >= 0; i--)
        {
            if (_rosterItems.All(r => r.Slot != seekers[i].Slot))
                seekers.RemoveAt(i);
        }

        if (forceAllHiders)
        {
            foreach (var row in seekers.ToArray())
            {
                seekers.Remove(row);
                if (!hiders.Any(h => h.Slot == row.Slot))
                    hiders.Add(row);
            }
        }

        PushHideSeekRolesToServer();
    }

    private void PushHideSeekRolesToServer()
    {
        if (_suppressHideSeekUiSync || !_session.IsHosting || GameModeCombo.SelectedIndex != 1)
            return;

        var roles = new Dictionary<byte, HideSeekRole>();
        if (HideSeekHidersList.ItemsSource is ObservableCollection<RosterViewModel> hiders)
        {
            foreach (var row in hiders)
                roles[row.Slot] = HideSeekRole.Hider;
        }

        if (HideSeekSeekersList.ItemsSource is ObservableCollection<RosterViewModel> seekers)
        {
            foreach (var row in seekers)
                roles[row.Slot] = HideSeekRole.Seeker;
        }

        if (RolesMatchCurrentServerState(roles))
            return;

        _session.SetHideSeekRoles(roles);
        UpdateStartStopTagButtonState();
    }

    private bool RolesMatchCurrentServerState(Dictionary<byte, HideSeekRole> roles)
    {
        if (!_session.IsHosting || GameModeCombo.SelectedIndex != 1)
            return false;

        var state = _session.GameModeState;
        if (state.GameMode != GameMode.HideSeek)
            return false;

        foreach (var row in _rosterItems)
        {
            if (!roles.TryGetValue(row.Slot, out var role))
                return false;
            if (state.GetRole(row.Slot) != (byte)role)
                return false;
        }

        return true;
    }

    private void UpdateStartStopTagButtonState()
    {
        if (GameModeCombo.SelectedIndex != 1)
        {
            StartStopTagButton.IsEnabled = false;
            return;
        }

        var hiderCount = (HideSeekHidersList.ItemsSource as ObservableCollection<RosterViewModel>)?.Count ?? 0;
        var seekerCount = (HideSeekSeekersList.ItemsSource as ObservableCollection<RosterViewModel>)?.Count ?? 0;
        StartStopTagButton.IsEnabled = _tagRunning || (hiderCount >= 1 && seekerCount >= 1);
    }

    private void StartStopTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsHosting)
            return;

        if (_tagRunning)
        {
            _session.StopHideSeekTag();
            return;
        }

        if (!_session.TryStartHideSeekTag(out var error))
        {
            HideSeekStatusText.Text = error ?? "Unable to start tag.";
            return;
        }
    }

    private void ResetTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsHosting)
            return;

        _session.ResetHideSeekTag();
        SyncHideSeekRoleListsFromRoster(forceAllHiders: true);
        HideSeekStatusText.Text = "Everyone reset to hiders. Timer cleared.";
    }

    private void HideSeekRoleList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var player = GetHideSeekRosterUnderMouse(listBox, e.GetPosition(listBox));
        if (player == null)
            return;

        listBox.SelectedItem = player;
        _hideSeekDragSource = player;
        _hideSeekDragStartPoint = e.GetPosition(null);
    }

    private void HideSeekRoleList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _hideSeekDragSource == null)
            return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _hideSeekDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _hideSeekDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject(typeof(RosterViewModel), _hideSeekDragSource);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        _hideSeekDragSource = null;
    }

    private void HideSeekRoleList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(RosterViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static RosterViewModel? GetHideSeekRosterUnderMouse(ListBox listBox, Point position)
    {
        var element = listBox.InputHitTest(position) as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = VisualTreeHelper.GetParent(element);

        return (element as ListBoxItem)?.Content as RosterViewModel;
    }

    private void HideSeekHidersList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RosterViewModel)) is RosterViewModel player)
            MoveHideSeekPlayer(player, HideSeekRole.Hider);
        e.Handled = true;
    }

    private void HideSeekSeekersList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RosterViewModel)) is RosterViewModel player)
            MoveHideSeekPlayer(player, HideSeekRole.Seeker);
        e.Handled = true;
    }

    private void MoveHideSeekPlayer(RosterViewModel player, HideSeekRole targetRole)
    {
        var hiders = HideSeekHidersList.ItemsSource as ObservableCollection<RosterViewModel>;
        var seekers = HideSeekSeekersList.ItemsSource as ObservableCollection<RosterViewModel>;
        if (hiders == null || seekers == null)
            return;

        hiders.Remove(player);
        seekers.Remove(player);

        if (targetRole == HideSeekRole.Hider)
            hiders.Add(player);
        else
            seekers.Add(player);

        PushHideSeekRolesToServer();
    }

    public void EnsureSessionShutdown()
    {
        if (_sessionShutdownComplete)
            return;

        _sessionShutdownComplete = true;
        _dolphinUiTimer?.Stop();
        _previewDebounceTimer?.Stop();
        SaveConfigFromUi();
        _config.Save();
        _session.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_sessionShutdownComplete)
        {
            EnsureSessionShutdown();
            _session.Dispose();
        }

        base.OnClosed(e);
    }

}
