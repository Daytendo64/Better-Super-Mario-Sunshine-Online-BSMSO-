using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
using SMSO.Net.MarioPack;

namespace SMSO.Launcher;

public partial class MainWindow : Window
{
    private readonly ConfigService _config = new();
    private readonly SessionCoordinator _session;
    private readonly ObservableCollection<RosterViewModel> _rosterItems = new();
    private readonly ObservableCollection<WarpTargetItem> _warpTargets = new();
    private LevelCatalog? _levels;
    private byte[] _lastRosterSlots = Array.Empty<byte>();
    private bool _sessionShutdownComplete;
    private DispatcherTimer? _previewDebounceTimer;
    private DispatcherTimer? _dolphinUiTimer;
    private RosterViewModel? _hideSeekDragSource;
    private Point _hideSeekDragStartPoint;
    private bool _suppressHideSeekUiSync;
    private bool _suppressMaxPlayersSave;
    private bool _suppressHideSeekGraceSave;
    private bool _syncingMarioModelCombo;
    private bool _syncingMusicVolumeSlider;
    private bool _marioModelInstallInProgress;
    private bool _restartRequiredForModUpdate;
    /// When true, a newer `_BSMSO.kxe` was synced while Dolphin was still running
    /// the old module. Do not clear `_restartRequiredForModUpdate` on ModuleReady
    /// until Dolphin has fully stopped (then a fresh ModuleReady means the new kxe).
    private bool _restartGateAwaitingDolphinStop;
    private bool _updateRequired;
    private bool _launcherUpdateRequired;
    private string? _launcherUpdateDownloadUrl;
    private string _launcherUpdateMessage = "";
    private bool _tagRunning;
    private static readonly Random _random = new();
    private readonly Dictionary<byte, int> _randomTagExemptRoundsBySlot = new();
    private readonly Queue<byte> _recentRandomLevelCourseIds = new();
    private DispatcherTimer? _clientWarpStatusClearTimer;
    /// <summary>
    /// True while Random Level / warp-all is showing the destination on Hide&amp;Seek
    /// status lines. Suppresses FormatHideSeekStatus overwrites until the notice expires.
    /// </summary>
    private bool _hideSeekWarpStatusActive;
    private DispatcherTimer? _tagElapsedUiTimer;
    private bool _tagElapsedLive;
    private uint _tagElapsedBaseMs;
    private long _tagElapsedAnchorTick;

    public MainWindow()
    {
        InitializeComponent();
        ClientRosterList.ItemsSource = _rosterItems;
        ServerRosterList.ItemsSource = _rosterItems;
        WarpTargetCombo.ItemsSource = _warpTargets;
        _config.Load();
        _session = new SessionCoordinator(_config);
        WireEvents();
        LoadConfigToUi();
        LoadLevels();
        ApplyClientLiteLayout();
        Title = _config.InstanceIndex == 0
            ? (BuildFeatures.ClientLite
                ? "BSMSO Lite — Better Super Mario Sunshine Online"
                : "BSMSO — Better Super Mario Sunshine Online")
            : (BuildFeatures.ClientLite
                ? $"BSMSO Lite — Better Super Mario Sunshine Online ({_config.InstanceLabel})"
                : $"BSMSO — Better Super Mario Sunshine Online ({_config.InstanceLabel})");
        var productVersion = LauncherUpdateChecker.ResolveProductVersionLabel();
        VersionText.Text = BuildFeatures.ClientLite
            ? $"BSMSO Lite v{productVersion} | build {ProtocolConstants.ModBuildId} | comm v{ProtocolConstants.CommVersion} | {_config.InstanceLabel} | .NET {Environment.Version}"
            : $"BSMSO v{productVersion} | build {ProtocolConstants.ModBuildId} | comm v{ProtocolConstants.CommVersion} | {_config.InstanceLabel} | .NET {Environment.Version}";
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
        BindRosterColumnStretch(ClientRosterList, 1.0, 1.5, 1.5, 1.1, 0.8);
        BindRosterColumnStretch(ServerRosterList, 0.9, 1.4, 1.4, 1.0, 0.5, 0.8);
        _ = CheckForLauncherUpdateAsync();
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
        IsoPathBox.TextChanged += (_, _) =>
        {
            QueueSave();
            UpdateModuleInstallUi();
        };
        ServerIpBox.TextChanged += (_, _) => QueueSave();
        ServerPortBox.TextChanged += (_, _) => QueueSave();
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
            {
                ClearRoster();
                // Stay on the current tab (usually Client Actions). Forcing Settings
                // made disconnect feel like a bounce — users had to click Client again.
            }
            else if (status == "Hosting")
                MainTabControl.SelectedItem = ServerTab;
            else if (status == "Connected")
                MainTabControl.SelectedItem = ClientTab;
            UpdateConnectionUi();
            UpdateSessionStatusColor();
        });
        _session.PhaseChanged += _ => RunOnUiThread(UpdateConnectionUi);
        _session.DisconnectNotice += message => RunOnUiThread(() =>
        {
            LogLine.Text = message;
            MessageBox.Show(message, "BSMSO — Disconnected", MessageBoxButton.OK, MessageBoxImage.Information);
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
        _session.SyncSettingsChanged += () => RunOnUiThread(UpdateClientWorldSyncStatus);
        _session.DolphinClosed += () => SafeRunOnUiThread(() =>
        {
            RefreshDolphinStateUi();
            ResetGameModeUiToNormal();
            if (!_session.IsConnected && !_session.IsHosting)
                ClearRoster();
        });
        _session.DolphinLinkStateChanged += _ => SafeRunOnUiThread(RefreshDolphinStateUi);
        _session.GameModeStateChanged += state => RunOnUiThread(() => ApplyGameModeStateToUi(state));
        _session.WarpEveryoneReceived += (courseId, episodeId) =>
            RunOnUiThread(() => ShowWarpingEveryoneStatus(courseId, episodeId));
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
        PopulateMaxPlayersCombo(_config.Config.MaxPlayers);
        PopulateHideSeekGraceSecondsCombo(_config.Config.HideSeekGraceSeconds);
        ApplyNameTagColorToUi(
            ParseStoredColor(_config.Config.NameTagColor, 255, 255, 255),
            ParseStoredColor(_config.Config.NameTagGradientColor, 136, 136, 136),
            ParseStoredColor(_config.Config.NameTagOutlineColor, 0, 0, 0),
            _config.Config.NameTagGradientEnabled,
            persist: false);
        // Release zips ship CustomModels/ next to the launcher; sync AppData so
        // the dropdown matches the packager's library (overwrites on zip updates).
        // Disc/Kuribo Mods are NOT touched on open — use Install / patch modules.
        ModelLibrary.SeedBundledModels(m => _config.Log(m));
        RefreshMarioModelCombo();

        // Push the saved model id into the bridge immediately so solo Launch Dolphin
        // mounts the configured pack on the first stage (not retail until combo change).
        _session.ApplySelectedMarioModelToBridge();
        ApplyMusicVolumeToUi(_config.Config.MusicVolumePercent);

        AllowClientTeleportToggle.IsChecked = _config.Config.AllowClientTeleporting;
        ApplyRecommendedDolphinSettingsToggle.IsChecked = _config.Config.ApplyRecommendedDolphinSettings;
        if (PatchBseMovesetToggle != null)
            PatchBseMovesetToggle.IsChecked = _config.Config.PatchBseMoveset;
        WorldSyncToggle.IsChecked = IsWorldSyncEnabled(
            _config.Config.SyncFlags,
            _config.Config.SyncObjects,
            _config.Config.SyncProgress);
        UpdateClientWorldSyncStatus();
        UpdateModuleInstallUi();
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
        if (SettingsMarioModelCombo.SelectedItem is ModelLibraryEntry settingsSelected)
            _config.Config.SelectedMarioModelId = CharacterPack.NormalizeModelId(settingsSelected.Id);
        else if (MarioModelCombo.SelectedItem is ModelLibraryEntry selected)
            _config.Config.SelectedMarioModelId = CharacterPack.NormalizeModelId(selected.Id);
        else if (ServerMarioModelCombo.SelectedItem is ModelLibraryEntry serverSelected)
            _config.Config.SelectedMarioModelId = CharacterPack.NormalizeModelId(serverSelected.Id);
        if (int.TryParse(ServerPortBox.Text, out var port))
            _config.Config.ServerPort = Math.Clamp(port, 1024, 65535);
        _config.Config.MaxPlayers = ReadMaxPlayersFromUi();
        _config.Config.HideSeekGraceSeconds = ReadHideSeekGraceSecondsFromUi();
        _config.Config.ApplyRecommendedDolphinSettings =
            ApplyRecommendedDolphinSettingsToggle.IsChecked == true;
        if (PatchBseMovesetToggle != null)
            _config.Config.PatchBseMoveset = PatchBseMovesetToggle.IsChecked == true;
        _config.Config.MusicVolumePercent = ReadMusicVolumePercentFromUi();
        _config.SaveDebounced();
    }

    private int ReadMusicVolumePercentFromUi()
    {
        var slider = ClientMusicVolumeSlider ?? ServerMusicVolumeSlider;
        if (slider == null)
            return Math.Clamp(_config.Config.MusicVolumePercent, 0, 100);
        return (int)Math.Clamp(Math.Round(slider.Value), 0, 100);
    }

    private void ApplyMusicVolumeToUi(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _syncingMusicVolumeSlider = true;
        try
        {
            if (ClientMusicVolumeSlider != null)
                ClientMusicVolumeSlider.Value = percent;
            if (ServerMusicVolumeSlider != null)
                ServerMusicVolumeSlider.Value = percent;
            var label = $"{percent}%";
            if (ClientMusicVolumeValueText != null)
                ClientMusicVolumeValueText.Text = label;
            if (ServerMusicVolumeValueText != null)
                ServerMusicVolumeValueText.Text = label;
        }
        finally
        {
            _syncingMusicVolumeSlider = false;
        }
    }

    private void MusicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _syncingMusicVolumeSlider)
            return;
        if (sender is not Slider slider)
            return;

        var percent = (int)Math.Clamp(Math.Round(slider.Value), 0, 100);
        ApplyMusicVolumeToUi(percent);
        _session.SetMusicVolumePercent(percent);
    }

    private void PopulateMaxPlayersCombo(int selected)
    {
        if (MaxPlayersCombo == null)
            return;

        selected = Math.Clamp(selected, 2, ProtocolConstants.StableMaxPlayers);
        _suppressMaxPlayersSave = true;
        try
        {
            MaxPlayersCombo.Items.Clear();
            for (var n = 2; n <= ProtocolConstants.StableMaxPlayers; n++)
            {
                var label = n == ProtocolConstants.StableMaxPlayers
                    ? $"{n} (max)"
                    : n.ToString();
                MaxPlayersCombo.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag = n,
                });
            }
            MaxPlayersCombo.SelectedIndex = selected - 2;
        }
        finally
        {
            _suppressMaxPlayersSave = false;
        }
    }

    private int ReadMaxPlayersFromUi()
    {
        if (MaxPlayersCombo?.SelectedItem is ComboBoxItem item && item.Tag is int n)
            return Math.Clamp(n, 2, ProtocolConstants.StableMaxPlayers);
        return Math.Clamp(_config.Config.MaxPlayers, 2, ProtocolConstants.StableMaxPlayers);
    }

    private void MaxPlayersCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMaxPlayersSave || MaxPlayersCombo?.SelectedItem is not ComboBoxItem)
            return;
        _config.Config.MaxPlayers = ReadMaxPlayersFromUi();
        _config.SaveDebounced();
    }

    private static readonly int[] HideSeekGraceSecondOptions = { 15, 30, 45, 60 };

    private static int SnapHideSeekGraceSeconds(int value)
    {
        var best = HideSeekGraceSecondOptions[0];
        var bestDist = Math.Abs(value - best);
        for (var i = 1; i < HideSeekGraceSecondOptions.Length; i++)
        {
            var opt = HideSeekGraceSecondOptions[i];
            var dist = Math.Abs(value - opt);
            if (dist < bestDist)
            {
                best = opt;
                bestDist = dist;
            }
        }

        return best;
    }

    private void PopulateHideSeekGraceSecondsCombo(int selected)
    {
        if (HideSeekGraceSecondsCombo == null)
            return;

        selected = SnapHideSeekGraceSeconds(selected);
        _suppressHideSeekGraceSave = true;
        try
        {
            HideSeekGraceSecondsCombo.Items.Clear();
            foreach (var seconds in HideSeekGraceSecondOptions)
            {
                var label = seconds == 60 ? "1 minute" : $"{seconds} seconds";
                HideSeekGraceSecondsCombo.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag = seconds,
                });
            }

            HideSeekGraceSecondsCombo.SelectedIndex =
                Array.IndexOf(HideSeekGraceSecondOptions, selected);
        }
        finally
        {
            _suppressHideSeekGraceSave = false;
        }
    }

    private int ReadHideSeekGraceSecondsFromUi()
    {
        if (HideSeekGraceSecondsCombo?.SelectedItem is ComboBoxItem item && item.Tag is int seconds)
            return SnapHideSeekGraceSeconds(seconds);
        return SnapHideSeekGraceSeconds(_config.Config.HideSeekGraceSeconds);
    }

    private void ApplyHideSeekGraceSecondsFromUi(bool persist)
    {
        var seconds = ReadHideSeekGraceSecondsFromUi();
        _config.Config.HideSeekGraceSeconds = seconds;
        _session.SetHideSeekGraceSeconds(seconds);
        if (persist)
            _config.SaveDebounced();

        // A grace already counting down is never re-armed, so say when this takes effect.
        if (_tagRunning && HideSeekStatusText != null)
            HideSeekStatusText.Text = $"Hider timer set to {seconds}s — applies next Start Tag.";
    }

    private void HideSeekGraceSecondsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHideSeekGraceSave || HideSeekGraceSecondsCombo?.SelectedItem is not ComboBoxItem)
            return;
        ApplyHideSeekGraceSecondsFromUi(persist: true);
    }

    private void RefreshMarioModelCombo(string? selectId = null)
    {
        if (SettingsMarioModelCombo == null && MarioModelCombo == null && ServerMarioModelCombo == null)
            return;

        _syncingMarioModelCombo = true;
        try
        {
            var entries = ModelLibrary.ListEntries(includeRetail: true);
            var want = CharacterPack.NormalizeModelId(selectId ?? _config.Config.SelectedMarioModelId);
            var match = entries.FirstOrDefault(e =>
                CharacterPack.NormalizeModelId(e.Id) == want) ?? entries[0];
            _config.Config.SelectedMarioModelId = CharacterPack.NormalizeModelId(match.Id);

            if (SettingsMarioModelCombo != null)
            {
                SettingsMarioModelCombo.ItemsSource = entries;
                SettingsMarioModelCombo.SelectedItem = match;
            }
            if (MarioModelCombo != null)
            {
                MarioModelCombo.ItemsSource = entries;
                MarioModelCombo.SelectedItem = match;
            }
            if (ServerMarioModelCombo != null)
            {
                ServerMarioModelCombo.ItemsSource = entries;
                ServerMarioModelCombo.SelectedItem = match;
            }
        }
        finally
        {
            _syncingMarioModelCombo = false;
        }
    }

    private async void MarioModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMarioModelCombo)
            return;
        if (sender is not ComboBox combo || combo.SelectedItem is not ModelLibraryEntry entry)
            return;

        var id = CharacterPack.NormalizeModelId(entry.Id);
        var previousId = CharacterPack.NormalizeModelId(_config.Config.SelectedMarioModelId);
        if (id == previousId)
        {
            // Keep both tabs' combos visually aligned when one was just refreshed.
            SyncMarioModelComboSelection(entry);
            return;
        }

        if (_marioModelInstallInProgress)
        {
            RefreshMarioModelCombo(previousId);
            return;
        }

        _marioModelInstallInProgress = true;
        SetMarioModelCombosEnabled(false);
        LogLine.Text = string.IsNullOrEmpty(id)
            ? "Restoring Retail Mario…"
            : $"Installing model {entry.DisplayName}…";
        MarioPackInstallResult result;
        try
        {
            var gamePath = _config.Config.IsoPath;
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                result = string.IsNullOrEmpty(id)
                    ? new MarioPackInstallResult(true, false, 0, "Retail Mario selected.")
                    : new MarioPackInstallResult(
                        false, false, 0, "Set Game ISO / extracted folder before selecting a custom model.");
            }
            else
            {
                IProgress<string> progress = new Progress<string>(message =>
                {
                    _config.Log(message);
                    LogLine.Text = message;
                });
                result = await Task.Run(() =>
                        MarioPackInstaller.InstallPackToGame(
                            gamePath, id, message => progress.Report(message)))
                    .ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            result = new MarioPackInstallResult(false, false, 0, $"Model install failed: {ex.Message}");
        }
        finally
        {
            _marioModelInstallInProgress = false;
            SetMarioModelCombosEnabled(true);
        }

        if (!result.Succeeded)
        {
            var prefix = result.Deferred ? "Model install deferred" : "Model install failed";
            _config.Log($"{prefix}: {result.Message}");
            LogLine.Text = $"{prefix}: {result.Message}";
            RefreshMarioModelCombo(previousId);
            return;
        }

        _config.Config.SelectedMarioModelId = id;
        _config.SaveDebounced();
        SyncMarioModelComboSelection(entry);
        _session.NotifyLocalMarioModelChanged(id);
        LogLine.Text = result.Message;
    }

    private void SyncMarioModelComboSelection(ModelLibraryEntry entry)
    {
        _syncingMarioModelCombo = true;
        try
        {
            if (SettingsMarioModelCombo != null && !ReferenceEquals(SettingsMarioModelCombo.SelectedItem, entry))
                SettingsMarioModelCombo.SelectedItem = entry;
            if (MarioModelCombo != null && !ReferenceEquals(MarioModelCombo.SelectedItem, entry))
                MarioModelCombo.SelectedItem = entry;
            if (ServerMarioModelCombo != null && !ReferenceEquals(ServerMarioModelCombo.SelectedItem, entry))
                ServerMarioModelCombo.SelectedItem = entry;
        }
        finally
        {
            _syncingMarioModelCombo = false;
        }
    }

    private void SetMarioModelCombosEnabled(bool enabled)
    {
        if (SettingsMarioModelCombo != null)
            SettingsMarioModelCombo.IsEnabled = enabled;
        if (MarioModelCombo != null)
            MarioModelCombo.IsEnabled = enabled;
        if (ServerMarioModelCombo != null)
            ServerMarioModelCombo.IsEnabled = enabled;
    }

    private void LoadLevels()
    {
        var levelsPath = FindLevelsPath();
        _session.Initialize(levelsPath);
        if (!File.Exists(levelsPath)) return;
        _levels = LevelCatalog.Load(levelsPath);
        var warpCourses = _levels.GetOrganizedWarpCourses();
        ClientLevelCombo.EnableGroupHeaders = true;
        ServerLevelCombo.EnableGroupHeaders = true;
        ClientLevelCombo.ItemsSource = warpCourses;
        ServerLevelCombo.ItemsSource = warpCourses;
        if (warpCourses.Count > 0)
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

    private static CourseEntry? ResolveSelectedCourse(FastSelector levelCombo) =>
        levelCombo.SelectedItem switch
        {
            WarpCourseListItem item => item.Course,
            CourseEntry course => course,
            _ => null,
        };

    private static void UpdateEpisodeCombo(FastSelector levelCombo, FastSelector episodeCombo)
    {
        var course = ResolveSelectedCourse(levelCombo);
        if (course != null)
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
        _lastRosterSlots = Array.Empty<byte>();
        HideSeekHidersList.ItemsSource = null;
        HideSeekSeekersList.ItemsSource = null;
        ClientHideSeekHidersList.ItemsSource = null;
        ClientHideSeekSeekersList.ItemsSource = null;
        _tagRunning = false;
        StartStopTagButton.Content = "Start Tag";
        ClientGameModeText.Text = "Normal";
        ClientHideSeekPanel.Visibility = Visibility.Collapsed;
        ClientHideSeekStatusText.Text = string.Empty;
        ClearClientWarpStatus();
    }

    private void UpdateRosterCore(PlayerRosterEntry[] entries)
    {
        var selectedWarpSlot = WarpTargetCombo.SelectedItem is WarpTargetItem warp ? warp.Slot : (byte)0;

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
                // Keep list ordered by network slot so 1..N ordinals stay stable and sorted.
                var insertAt = _rosterItems.TakeWhile(r => r.Slot < entry.Slot).Count();
                _rosterItems.Insert(insertAt, row);
            }

            row.Username = entry.Username;
            row.StageId = entry.StageId;
            row.EpisodeId = entry.EpisodeId;
            row.MarioModelId = CharacterPack.NormalizeModelId(entry.MarioModelId);
            row.ModelName = ModelLibrary.ResolveDisplayName(row.MarioModelId);
            if (entry.State is DolphinState.Booting or DolphinState.Loading or DolphinState.Warping)
            {
                row.LevelName = "Loading...";
                row.EpisodeName = "";
            }
            else
            {
                row.LevelName = _levels?.GetCourseName(entry.StageId) ?? entry.StageId.ToString();
                // Roster episode ids are catalog-normalized by the server; still run through
                // GetEpisodeDisplayName so Pinna/hotel/plaza remaps resolve if a raw scenario slips in.
                row.EpisodeName = _levels?.GetEpisodeDisplayName(entry.StageId, entry.EpisodeId)
                                  ?? $"Episode {entry.EpisodeId + 1}";
            }
            row.Status = entry.State.ToString();
            row.PingMs = entry.PingMs.ToString();
        }

        // Connected Players shows 1..N among currently connected players (no slot gaps).
        for (var i = 0; i < _rosterItems.Count; i++)
            _rosterItems[i].Ordinal = i + 1;

        if (!_lastRosterSlots.SequenceEqual(slotSet))
        {
            _lastRosterSlots = slotSet;
            _warpTargets.Clear();
            foreach (var entry in ordered)
                _warpTargets.Add(new WarpTargetItem { Username = entry.Username, Slot = entry.Slot });

            var warpMatch = _warpTargets.FirstOrDefault(w => w.Slot == selectedWarpSlot);
            WarpTargetCombo.SelectedItem = warpMatch ?? _warpTargets.FirstOrDefault();

            if (GameModeCombo.SelectedIndex == 1)
            {
                SyncHideSeekRoleListsFromRoster();
                UpdateStartStopTagButtonState();
            }

            // Keep the client read-only game-mode view in sync when players join/leave.
            if (_session.IsConnected)
                ApplyClientGameModeView(_session.GameModeState);
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
        var dlg = new OpenFileDialog
        {
            Filter = "GameCube disc|*.iso;*.gcm;*.gcz|Extracted game (main.dol)|main.dol|All files|*.*",
            Title = "Select disc image or sys\\main.dol from extracted folder",
        };
        if (dlg.ShowDialog() == true)
        {
            IsoPathBox.Text = dlg.FileName;
            SaveConfigFromUi();
            UpdateModuleInstallUi();
        }
    }

    private async void InstallModules_Click(object sender, RoutedEventArgs e)
    {
        SaveConfigFromUi();
        var isoPath = IsoPathBox.Text.Trim().Trim('"');
        string? discOutputPath = null;
        var targetKind = ModuleInstallValidator.ClassifyInstallTarget(isoPath);
        if (targetKind == ModuleInstallTargetKind.DiscImage)
        {
            var suggested = DiscImagePatcher.SuggestPatchedDiscFileName(isoPath);
            var saveDlg = new SaveFileDialog
            {
                Title = "Save patched disc image",
                Filter = "Disc image|*.iso;*.gcm|ISO|*.iso|GCM|*.gcm",
                FileName = Path.GetFileName(suggested),
                InitialDirectory = Path.GetDirectoryName(suggested) is { Length: > 0 } dir && Directory.Exists(dir)
                    ? dir
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AddExtension = true,
                DefaultExt = Path.GetExtension(suggested).TrimStart('.'),
                OverwritePrompt = true
            };
            if (saveDlg.ShowDialog(this) != true)
            {
                UpdateModuleInstallUi();
                return;
            }

            discOutputPath = saveDlg.FileName.Trim().Trim('"');
            var outExt = Path.GetExtension(discOutputPath);
            if (!outExt.Equals(".iso", StringComparison.OrdinalIgnoreCase) &&
                !outExt.Equals(".gcm", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Patched disc must be saved as .iso or .gcm.",
                    "BSMSO", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateModuleInstallUi();
                return;
            }
        }

        // Post-Install sync / pack retry target the patched output when Install wrote a new file.
        var effectiveGamePath = !string.IsNullOrWhiteSpace(discOutputPath) ? discOutputPath! : isoPath;

        InstallModulesButton.IsEnabled = false;
        OpenModsFolderButton.IsEnabled = false;
        ModuleInstallStatusText.Text = "Installing modules…";
        try
        {
            var (success, message, modelsWarning) = await ModuleInstaller.InstallAsync(
                    isoPath,
                    progress: status => Dispatcher.Invoke(() => ModuleInstallStatusText.Text = status),
                    log: m => _config.Log(m),
                    patchBseMoveset: _config.Config.PatchBseMoveset,
                    discOutputPath: discOutputPath,
                    postInstallUnderLock: async (installMessage, installModelsWarning) =>
                    {
                        // Runs while Install mutex is still held (second launcher can't race).
                        // Refresh dropdown from AppData (Install already seeded + copied packs).
                        // Keep a best-effort extracted-tree sync for kxe/disc-data only — do not
                        // reintroduce Launch/open auto disc writes beyond this Install click.
                        var sync = ModuleInstaller.SyncBundledModulesIntoGame(
                            effectiveGamePath,
                            m => _config.Log(m),
                            patchBseMoveset: _config.Config.PatchBseMoveset);
                        DiscDataInstaller.EnsureBundledDiscDataPresent(effectiveGamePath, m => _config.Log(m));
                        // Safety net: if InstallAsync warned about packs, retry once now
                        // that Dolphin may have closed / AppData is warm.
                        var packRetry = MarioPackInstaller.EnsureAllLibraryPacksPresentDetailed(
                            effectiveGamePath, m => _config.Log(m));
                        var msg = installMessage;
                        var warn = installModelsWarning || packRetry.HasWarning;
                        if (packRetry.HasWarning &&
                            msg.IndexOf("WARNING — custom Mario packs", StringComparison.Ordinal) < 0)
                        {
                            msg = ModuleInstaller.AppendPackSyncMessage(msg, packRetry);
                        }

                        MarioPackInstaller.RemoveBetterMovementPrm(effectiveGamePath, m => _config.Log(m));
                        MarioPackInstaller.ProbeBetterMovementPresence(effectiveGamePath, m => _config.Log(m));

                        await Dispatcher.InvokeAsync(() => ApplyBundledModuleSyncResult(sync));
                        return (msg, warn);
                    })
                .ConfigureAwait(true);
            var installSuccess = success;
            var installMessage = message;
            var installModelsWarning = modelsWarning;
            if (installSuccess)
            {
                Dispatcher.Invoke(() =>
                {
                    // Point Game ISO at the patched image so Launch uses the new file.
                    if (!string.IsNullOrWhiteSpace(discOutputPath) &&
                        !string.Equals(IsoPathBox.Text.Trim().Trim('"'), discOutputPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        IsoPathBox.Text = discOutputPath;
                        SaveConfigFromUi();
                        _config.Log($"Game ISO path updated to patched disc: {discOutputPath}");
                    }

                    RefreshMarioModelCombo();
                });
            }

            _config.Log(
                installSuccess
                    ? (installModelsWarning
                        ? $"Module install succeeded with model warnings: {installMessage}"
                        : $"Module install succeeded: {installMessage}")
                    : $"Module install failed: {installMessage}");

            string title;
            MessageBoxImage icon;
            if (!installSuccess)
            {
                title = "BSMSO — Install failed";
                icon = MessageBoxImage.Error;
            }
            else if (installModelsWarning)
            {
                title = "BSMSO — Modules installed (model packs incomplete)";
                icon = MessageBoxImage.Warning;
            }
            else if (installMessage.StartsWith("Patched disc image", StringComparison.Ordinal))
            {
                title = "BSMSO — Disc image patched";
                icon = MessageBoxImage.Information;
            }
            else
            {
                title = "BSMSO — Modules installed";
                icon = MessageBoxImage.Information;
            }

            MessageBox.Show(installMessage, title, MessageBoxButton.OK, icon);
        }
        catch (Exception ex)
        {
            _config.Log($"Module install error: {ex.Message}");
            MessageBox.Show($"Install failed: {ex.Message}", "BSMSO — Install failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateModuleInstallUi();
        }
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        SaveConfigFromUi();
        var gamePath = IsoPathBox.Text.Trim().Trim('"');
        var kind = ModuleInstallValidator.ClassifyInstallTarget(gamePath);
        if (kind == ModuleInstallTargetKind.DiscImage || kind == ModuleInstallTargetKind.CompressedDiscImage)
        {
            MessageBox.Show(
                "Mods are inside the disc image for .iso/.gcm paths.\n\n" +
                "Use Install / patch modules to update them, or set Game ISO to an extracted folder to browse Mods on disk.",
                "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!ModuleInstallValidator.TryResolveModsDirectory(gamePath, out var modsDir) ||
            modsDir == null)
        {
            MessageBox.Show(
                "Could not find Mods from the Game ISO path. Set Paths → Game ISO to your extracted SMS folder (or sys\\main.dol), or Install / patch modules on a .iso/.gcm.",
                "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(modsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = modsDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open Mods folder:\n{ex.Message}", "BSMSO",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyBundledModuleSyncResult(BundledModuleSyncResult sync)
    {
        if (sync.BundledModuleAvailable && !sync.InstalledMatchesBundled)
        {
            _updateRequired = true;
            _restartRequiredForModUpdate = false;
            _restartGateAwaitingDolphinStop = false;
        }
        else
        {
            _updateRequired = false;
            if (sync.BsmsoModuleChanged &&
                (_session.IsDolphinRunning || _session.DolphinLinkState != DolphinLinkState.NotRunning))
            {
                _restartRequiredForModUpdate = true;
                _restartGateAwaitingDolphinStop = true;
            }
        }

        UpdateModuleInstallUi();
        UpdateDolphinUi();
        UpdateConnectionUi();
    }

    private void ClearModUpdateGatesIfReady()
    {
        // A ModuleReady link after syncing a newer kxe still runs the *old* in-memory
        // module until Dolphin fully restarts. Only clear the restart gate once
        // Dolphin has stopped (armed) and then becomes ModuleReady again.
        if (_restartGateAwaitingDolphinStop)
        {
            if (_session.DolphinLinkState == DolphinLinkState.NotRunning ||
                !_session.IsDolphinRunning)
            {
                _restartGateAwaitingDolphinStop = false;
            }
            return;
        }

        if (_restartRequiredForModUpdate &&
            _session.DolphinLinkState == DolphinLinkState.ModuleReady)
        {
            _restartRequiredForModUpdate = false;
        }
    }

    private void UpdateModuleInstallUi()
    {
        if (ModuleInstallStatusText == null || InstallModulesButton == null || OpenModsFolderButton == null)
            return;

        var status = ModuleInstaller.GetInstallStatus(IsoPathBox.Text.Trim().Trim('"'));
        var needsUpdate = status.NeedsUpdate || _updateRequired;

        if (GameProfileText != null)
        {
            var profile = GameProfileDetector.Detect(IsoPathBox.Text.Trim().Trim('"'));
            GameProfileText.Text = "Game profile: " + profile.DisplayName +
                (profile.GameId != null ? $" ({profile.GameId})" : "") +
                (profile.IsEclipse ? " — additive Install only" : "");
        }

        if (_restartRequiredForModUpdate)
            ModuleInstallStatusText.Text = ModuleVersionMessages.RestartRequiredForUpdate;
        else if (needsUpdate)
            ModuleInstallStatusText.Text = string.IsNullOrWhiteSpace(status.Message) || !status.NeedsUpdate
                ? ModuleVersionMessages.UpdateRequired
                : status.Message;
        else if (status.IsComplete && !IsLauncherUpdateRequired())
            ModuleInstallStatusText.Text = PreferUpToDateInstallMessage(status.Message);
        else if (IsLauncherUpdateRequired())
            // Modules may be current, but never claim "everything" is up to date.
            ModuleInstallStatusText.Text = StripEverythingUpToDateLead(status.Message);
        else
            ModuleInstallStatusText.Text = status.Message;

        InstallModulesButton.Content = needsUpdate
            ? ModuleVersionMessages.UpdateModuleButtonLabel
            : ModuleVersionMessages.InstallModuleButtonLabel;
        InstallModulesButton.ToolTip = needsUpdate
            ? ModuleVersionMessages.UpdateRequired
            : "Install or reinstall BSE / Kuribo and _BSMSO.kxe into the Game ISO path.";
        InstallModulesButton.IsEnabled = status.CanInstall;
        InstallModulesButton.Opacity = status.CanInstall ? 1.0 : 0.45;
        var canOpenMods = status.TargetKind == ModuleInstallTargetKind.ExtractedFolder &&
                          !string.IsNullOrWhiteSpace(status.ModsDirectory);
        OpenModsFolderButton.IsEnabled = canOpenMods;
        OpenModsFolderButton.Opacity = canOpenMods ? 1.0 : 0.45;
    }

    /// <summary>
    /// Ensure the install status line leads with a clear ready-to-play message when
    /// modules are current and the launcher itself does not require an update.
    /// </summary>
    private static string PreferUpToDateInstallMessage(string statusMessage)
    {
        var ready = ModuleVersionMessages.EverythingUpToDateReadyToPlayWithBuild(ProtocolConstants.ModBuildId);
        if (string.IsNullOrWhiteSpace(statusMessage))
            return ready;
        if (statusMessage.StartsWith(ModuleVersionMessages.EverythingUpToDateReadyToPlay, StringComparison.Ordinal))
            return statusMessage;
        return ready + "\n" + statusMessage;
    }

    private static string StripEverythingUpToDateLead(string statusMessage)
    {
        if (string.IsNullOrWhiteSpace(statusMessage))
            return statusMessage;
        if (!statusMessage.StartsWith(ModuleVersionMessages.EverythingUpToDateReadyToPlay, StringComparison.Ordinal))
            return statusMessage;

        var newline = statusMessage.IndexOf('\n');
        if (newline < 0)
            return "Modules match this launcher — get a newer BSMSO zip for the launcher update.";
        return statusMessage[(newline + 1)..].TrimStart();
    }

    private bool IsModuleUpdateRequired()
    {
        if (_updateRequired)
            return true;

        var status = ModuleInstaller.GetInstallStatus(IsoPathBox.Text.Trim().Trim('"'));
        return status.NeedsUpdate;
    }

    private bool IsLauncherUpdateRequired() => _launcherUpdateRequired;

    private bool ModulesCurrentAndLauncherCurrent()
    {
        if (IsLauncherUpdateRequired() || _restartRequiredForModUpdate || IsModuleUpdateRequired())
            return false;
        var status = ModuleInstaller.GetInstallStatus(IsoPathBox.Text.Trim().Trim('"'));
        return status.IsComplete && !status.NeedsUpdate;
    }

    private async Task CheckForLauncherUpdateAsync()
    {
        try
        {
            var result = await LauncherUpdateChecker.CheckAsync(_config.Config.UpdateManifestUrl)
                .ConfigureAwait(true);
            SafeRunOnUiThread(() => ApplyLauncherUpdateCheckResult(result));
        }
        catch (Exception ex)
        {
            SafeRunOnUiThread(() =>
            {
                if (LogLine != null)
                    LogLine.Text = $"Launcher update check failed: {ex.Message}";
            });
        }
    }

    private void ApplyLauncherUpdateCheckResult(LauncherUpdateCheckResult result)
    {
        _launcherUpdateRequired = result.UpdateRequired;
        var rawUrl = result.Manifest?.DownloadUrl;
        _launcherUpdateDownloadUrl =
            TryGetSafeHttpsDownloadUri(rawUrl, out var safeUri) && safeUri != null
                ? safeUri.AbsoluteUri
                : null;
        _launcherUpdateMessage = result.UpdateRequired ? result.UserMessage : "";

        if (LauncherUpdateBanner != null)
        {
            LauncherUpdateBanner.Visibility =
                result.UpdateRequired ? Visibility.Visible : Visibility.Collapsed;
            if (LauncherUpdateBannerText != null && result.UpdateRequired)
                LauncherUpdateBannerText.Text = result.UserMessage;
            if (LauncherUpdateOpenButton != null)
            {
                var hasUrl = result.UpdateRequired &&
                             !string.IsNullOrWhiteSpace(_launcherUpdateDownloadUrl);
                LauncherUpdateOpenButton.Visibility =
                    hasUrl ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        if (LogLine != null)
        {
            if (result.UpdateRequired)
                LogLine.Text = result.UserMessage;
            else if (!result.CheckedSuccessfully && !string.IsNullOrWhiteSpace(result.Detail))
                LogLine.Text = $"Launcher update check skipped: {result.Detail}";
        }

        UpdateDolphinUi();
    }

    private void LauncherUpdateOpen_Click(object sender, RoutedEventArgs e)
    {
        var url = _launcherUpdateDownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (!TryGetSafeHttpsDownloadUri(url, out var uri) || uri == null)
        {
            MessageBox.Show(
                "Download link is missing or not a safe https URL.",
                "BSMSO",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open download page:\n{ex.Message}",
                "BSMSO",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Manifest / config can supply any string; only allow http(s) absolute URIs
    /// so shell-execute cannot be pointed at file:, ms-msdt:, etc.
    /// </summary>
    private static bool TryGetSafeHttpsDownloadUri(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) || parsed == null)
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            return false;
        if (string.IsNullOrWhiteSpace(parsed.Host))
            return false;
        uri = parsed;
        return true;
    }

    private bool TryShowUpdateGateMessage()
    {
        if (IsLauncherUpdateRequired())
        {
            MessageBox.Show(
                _launcherUpdateMessage.Length > 0
                    ? _launcherUpdateMessage
                    : ModuleVersionMessages.LauncherUpdateRequiredGeneric,
                "BSMSO",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return true;
        }

        if (_restartRequiredForModUpdate || IsModuleUpdateRequired())
        {
            MessageBox.Show(
                _restartRequiredForModUpdate
                    ? ModuleVersionMessages.RestartRequiredForUpdate
                    : ModuleVersionMessages.UpdateRequired,
                "BSMSO",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return true;
        }

        return false;
    }

    private async void Host_Click(object sender, RoutedEventArgs e)
    {
        SaveConfigFromUi();
        _config.Save();
        if (TryShowUpdateGateMessage())
        {
            UpdateConnectionUi();
            return;
        }
        if (_session.DolphinLinkState != DolphinLinkState.ModuleReady)
        {
            MessageBox.Show(
                $"Launch Dolphin with {ModuleVersionMessages.ModuleFileName} loaded and wait for the BSMSO link before hosting.",
                "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (TryShowUpdateGateMessage())
        {
            UpdateConnectionUi();
            return;
        }
        if (_session.DolphinLinkState != DolphinLinkState.ModuleReady)
        {
            MessageBox.Show(
                $"Launch Dolphin with {ModuleVersionMessages.ModuleFileName} loaded and wait for the BSMSO link before connecting.",
                "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
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
        HostButton.IsEnabled = false;
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = false;
        try
        {
            await _session.DisconnectAsync(endSession: _session.IsHosting);
        }
        finally
        {
            UpdateConnectionUi();
        }
    }

    private void LaunchDolphin_Click(object sender, RoutedEventArgs e)
    {
        SaveConfigFromUi();
        if (TryShowUpdateGateMessage())
        {
            UpdateDolphinUi();
            return;
        }

        if (!TryGetValidatedLaunchPaths(out var dolphin, out var iso, out var validationError))
        {
            MessageBox.Show(validationError, "BSMSO — Paths Required", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Warn about leftover better_movement.prm on Launch (never auto-inject).
        // PRM raises gravity/jump multipliers — heaviness vs release even with Moveset ON.
        try
        {
            var bootBlock = ModuleInstallValidator.ValidateBootReadyModules(
                iso, _config.Config.PatchBseMoveset);
            if (bootBlock != null)
            {
                _config.Log($"Launch blocked — boot validation failed: {bootBlock}");
                MessageBox.Show(
                    bootBlock + "\n\nFix the install before launching Dolphin.",
                    "BSMSO — Modules not boot-ready",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                UpdateDolphinUi();
                return;
            }

            var presence = MarioPackInstaller.ProbeBetterMovementPresence(iso, m => _config.Log(m));
            if (!_config.Config.PatchBseMoveset &&
                (presence.MovesetKxePresent || presence.ArchiveHits > 0 || presence.LooseHits > 0))
            {
                _config.Log(
                    "WARNING: Patch BSE moveset is OFF but movement leftovers remain. " +
                    "Click Install / patch modules (with the toggle off), then restart Dolphin. " +
                    presence.Summary);
                if (ModuleInstallStatusText != null)
                    ModuleInstallStatusText.Text =
                        "Moveset leftovers detected — Install with Patch BSE moveset OFF, then restart Dolphin.";
            }
            else if (presence.ArchiveHits > 0 || presence.LooseHits > 0)
            {
                _config.Log(
                    "WARNING: better_movement.prm still on disc (makes jumps heavier than the release zip). " +
                    "Click Install / patch modules to strip it — Moveset.kxe alone is enough. " +
                    presence.Summary);
                if (ModuleInstallStatusText != null)
                    ModuleInstallStatusText.Text =
                        "better_movement.prm leftover — Install to strip (release-matching Moveset feel).";
            }
        }
        catch (Exception ex)
        {
            _config.Log($"Moveset presence check skipped: {ex.Message}");
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
            error = "Set Dolphin and Game ISO paths in Paths before launching.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dolphin))
        {
            error = "Set the Dolphin path in Paths before launching.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(iso))
        {
            error = "Set the Game ISO path in Paths before launching.";
            return false;
        }

        if (!File.Exists(dolphin))
        {
            error = $"Dolphin executable not found:\n{dolphin}";
            return false;
        }

        if (!GameIdentity.TryResolveDolphinLaunchPath(iso, out _))
        {
            if (Directory.Exists(iso))
            {
                error =
                    $"Extracted folder is missing sys\\main.dol:\n{iso}\n\nBrowse to sys\\main.dol or fix the folder.";
            }
            else
            {
                error =
                    $"Game path not found or unsupported:\n{iso}\n\nUse a .iso/.gcm, sys\\main.dol, or an extracted folder with sys\\main.dol.";
            }
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
        // Block launch while disc modules are stale — force Update module first.
        // Also block when a newer launcher zip is available (remote ModBuildId).
        // Restart-after-update still allows Launch once Dolphin is closed.
        var moduleUpdateBlocksLaunch = IsModuleUpdateRequired() || IsLauncherUpdateRequired();
        var canLaunch = !running && pathsOk && !moduleUpdateBlocksLaunch;

        LaunchDolphinButton.IsEnabled = canLaunch;
        LaunchDolphinButton.Opacity = canLaunch ? 1.0 : 0.45;
        LaunchDolphinButton.ToolTip = IsLauncherUpdateRequired()
            ? (_launcherUpdateMessage.Length > 0
                ? _launcherUpdateMessage
                : ModuleVersionMessages.LauncherUpdateRequiredGeneric)
            : IsModuleUpdateRequired()
                ? ModuleVersionMessages.UpdateRequired
                : running
                    ? "Dolphin is already running from this launcher."
                    : pathsOk
                        ? "Launch Dolphin with _BSMSO.kxe installed."
                        : "Set Dolphin and Game ISO paths before launching.";

        var processText = running ? "Open" : "Not running";
        var linkText = link switch
        {
            DolphinLinkState.ModuleReady => "Connected",
            DolphinLinkState.Attached => "Attached (resolving mailbox)",
            DolphinLinkState.Running => "Running (not attached)",
            _ => "Disconnected",
        };
        var ok = StatusBrush("SmsStatusOk", StatusOkFallback);
        var bad = StatusBrush("SmsStatusBad", StatusBadFallback);
        var warn = StatusBrush("SmsStatusWarn", StatusWarnFallback);

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
        ClearModUpdateGatesIfReady();
        var upToDatePrefix = ModulesCurrentAndLauncherCurrent()
            ? ModuleVersionMessages.EverythingUpToDateReadyToPlayWithBuild(ProtocolConstants.ModBuildId) + " "
            : "";
        DolphinDetailText.Text = link switch
        {
            _ when IsLauncherUpdateRequired() =>
                (_launcherUpdateMessage.Length > 0
                    ? _launcherUpdateMessage
                    : ModuleVersionMessages.LauncherUpdateRequiredGeneric),
            _ when _restartRequiredForModUpdate => ModuleVersionMessages.RestartRequiredForUpdate,
            _ when IsModuleUpdateRequired() => ModuleVersionMessages.UpdateRequired,
            DolphinLinkState.ModuleReady when !string.IsNullOrWhiteSpace(moduleInstallWarning) => moduleInstallWarning,
            DolphinLinkState.ModuleReady =>
                upToDatePrefix + "BSMSO linked — warps and player sync enabled.",
            DolphinLinkState.Attached when !string.IsNullOrWhiteSpace(linkError) && searchSeconds >= 3 =>
                linkError,
            DolphinLinkState.Attached when searchSeconds < 3 =>
                $"Attached to Dolphin — waiting for the game to load {ModuleVersionMessages.ModuleFileName}.",
            DolphinLinkState.Attached =>
                "Searching for BSMSO mailbox — enter a stage if you have not yet.",
            DolphinLinkState.Running when !string.IsNullOrWhiteSpace(linkError) =>
                linkError,
            DolphinLinkState.Running when running =>
                upToDatePrefix + "Dolphin is running — linking automatically.",
            _ when !string.IsNullOrWhiteSpace(moduleInstallWarning) => moduleInstallWarning,
            _ when running =>
                upToDatePrefix +
                $"Dolphin is open — link restores when the game loads {ModuleVersionMessages.ModuleFileName}.",
            _ =>
                upToDatePrefix +
                "Launch Dolphin here before hosting or connecting (enabled when paths are set).",
        };
        UpdateModuleInstallUi();
        UpdateConnectionUi();
    }

    private static readonly Brush StatusOkFallback = CreateFallbackBrush(0x34, 0xD3, 0x99);
    private static readonly Brush StatusBadFallback = CreateFallbackBrush(0xF8, 0x71, 0x71);
    private static readonly Brush StatusWarnFallback = CreateFallbackBrush(0xFB, 0xBF, 0x24);

    private static Brush CreateFallbackBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// Theme brushes live in the application resource dictionary, which is no
    /// longer reachable once the window leaves the visual tree during shutdown.
    /// FindResource then raises through the dispatcher's exception wrapper and
    /// returns a non-Brush sentinel, so a plain cast throws on a queued UI
    /// update. Resolve leniently and fall back to the theme's literal colors.
    private Brush StatusBrush(string key, Brush fallback)
    {
        try
        {
            return TryFindResource(key) as Brush
                   ?? Application.Current?.TryFindResource(key) as Brush
                   ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private void UpdateSessionStatusColor()
    {
        var ok = StatusBrush("SmsStatusOk", StatusOkFallback);
        var bad = StatusBrush("SmsStatusBad", StatusBadFallback);
        var warn = StatusBrush("SmsStatusWarn", StatusWarnFallback);
        var text = StatusBadge.Text;
        var brush = text is "Connected" or "Hosting" ? ok :
            text == "Connecting" ? warn : bad;
        StatusBadge.Foreground = brush;
        StatusDot.Fill = brush;
    }

    private void UpdateConnectionUi()
    {
        var phase = _session.Phase;
        var gameLinked = _session.DolphinLinkState == DolphinLinkState.ModuleReady;
        var modGateBlocked = _restartRequiredForModUpdate || IsModuleUpdateRequired() ||
                             IsLauncherUpdateRequired();
        // Prefer lifecycle phase over TcpClient.Connected / IsRunning — those stay sticky
        // across half-closed sockets and mid-HostAsync gaps.
        var canHostOrConnect = gameLinked && SessionLifecycle.CanHostOrConnect(phase) && !modGateBlocked;
        var canDisconnect = SessionLifecycle.CanDisconnect(phase);
        DisconnectButton.IsEnabled = canDisconnect;
        ConnectButton.IsEnabled = canHostOrConnect;
        HostButton.IsEnabled = canHostOrConnect;
        ConnectButton.Opacity = ConnectButton.IsEnabled ? 1.0 : 0.45;
        HostButton.Opacity = HostButton.IsEnabled ? 1.0 : 0.45;
        DisconnectButton.Opacity = DisconnectButton.IsEnabled ? 1.0 : 0.45;
        GameLinkOverlay.Visibility = gameLinked ? Visibility.Collapsed : Visibility.Visible;
        var clientActionsActive = phase is SessionLifecyclePhase.Connected or SessionLifecyclePhase.Hosted;
        ClientActionsPanel.IsEnabled = clientActionsActive;
        ClientActionsPanel.Opacity = clientActionsActive ? 1.0 : 0.45;
        ClientActionsOverlay.Visibility = clientActionsActive ? Visibility.Collapsed : Visibility.Visible;
        var serverActionsActive = phase == SessionLifecyclePhase.Hosted;
        ServerActionsPanel.IsEnabled = serverActionsActive;
        ServerActionsPanel.Opacity = serverActionsActive ? 1.0 : 0.45;
        ServerActionsOverlay.Visibility = serverActionsActive ? Visibility.Collapsed : Visibility.Visible;
        UpdateClientActionsUi();
        UpdateServerClientTeleportStatus();
        UpdateClientWorldSyncStatus();
        UpdateSessionStatusColor();
    }

    private void UpdateClientWorldSyncStatus()
    {
        if (!_session.IsConnected)
        {
            ClientWorldSyncStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        ClientWorldSyncStatusText.Visibility = Visibility.Visible;
        if (_session.IsHosting)
        {
            ClientWorldSyncStatusText.Text = WorldSyncToggle.IsChecked == true
                ? "World sync on for this session (host control in Server Actions)."
                : "World sync off — enable Sync collectibles under Server Actions.";
            return;
        }

        if (!IsWorldSyncEnabled(_session.SyncFlagsEnabled, _session.SyncObjectsEnabled, _session.SyncProgressEnabled))
        {
            ClientWorldSyncStatusText.Text = "World sync is off on the host — collectibles won’t update for others.";
            return;
        }

        ClientWorldSyncStatusText.Text = "World sync on — shines/blues everywhere; yellow/red coins only on the same course and episode.";
    }

    private static bool IsWorldSyncEnabled(bool syncFlags, bool syncObjects, bool syncProgress) =>
        syncFlags && syncObjects && syncProgress;

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
        ClientTeleportOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
        ClientTeleportOverlayText.Text = "Host must enable client teleporting.";

#if !BSMSO_CLIENT_LITE
        // Game Modes on Client Actions is always view-only; dim when disconnected.
        ClientGameModesPanel.Opacity = connected ? 1.0 : 0.45;
        if (connected)
            ApplyClientGameModeView(_session.GameModeState);
        else
            ApplyClientGameModeView(GameModeStatePacket.CreateDefault());
#endif
    }

    private void ApplyClientLiteLayout()
    {
#if BSMSO_CLIENT_LITE
        if (ClientGameModesPanel != null)
            ClientGameModesPanel.Visibility = Visibility.Collapsed;
        if (ClientRosterPanel != null)
            ClientRosterPanel.Visibility = Visibility.Collapsed;
        if (ClientActionsMidSpacer != null)
            ClientActionsMidSpacer.Width = new GridLength(0);
        if (ClientGameModesColumn != null)
            ClientGameModesColumn.Width = new GridLength(0);

        // Drop the empty roster row so Teleport fills the tab (Model is above this panel).
        if (ClientActionsPanel?.RowDefinitions.Count >= 2)
            ClientActionsPanel.RowDefinitions[1].Height = new GridLength(0);
#endif
    }

    private async void ClientWarp_Click(object sender, RoutedEventArgs e)
    {
        var course = ResolveSelectedCourse(ClientLevelCombo);
        if (course == null || ClientEpisodeCombo.SelectedItem is not EpisodeEntry episode)
            return;
        await _session.WarpSelfAsync(course.CourseId, episode.EpisodeId);
    }

    private void ServerWarpAll_Click(object sender, RoutedEventArgs e)
    {
        var course = ResolveSelectedCourse(ServerLevelCombo);
        if (course == null || ServerEpisodeCombo.SelectedItem is not EpisodeEntry episode)
            return;
        _session.HostWarp(ProtocolConstants.WarpAllSlots, course.CourseId, episode.EpisodeId);
    }

    private void ServerWarpSelected_Click(object sender, RoutedEventArgs e)
    {
        var course = ResolveSelectedCourse(ServerLevelCombo);
        if (course == null || ServerEpisodeCombo.SelectedItem is not EpisodeEntry episode)
            return;

        if (WarpTargetCombo.SelectedItem is not WarpTargetItem target)
        {
            MessageBox.Show("Select a player in Warp target first.", "BSMSO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _session.HostWarp(target.Slot, course.CourseId, episode.EpisodeId);
    }

    private void AllowClientTeleport_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !_session.IsHosting) return;
        _session.SetAllowClientTeleport(AllowClientTeleportToggle.IsChecked == true);
        UpdateServerClientTeleportStatus();
        UpdateClientActionsUi();
    }

    private void ApplyRecommendedDolphinSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _config.Config.ApplyRecommendedDolphinSettings =
            ApplyRecommendedDolphinSettingsToggle.IsChecked == true;
        _config.SaveDebounced();
    }

    private void PatchBseMoveset_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || PatchBseMovesetToggle == null) return;
        var enabled = PatchBseMovesetToggle.IsChecked == true;
        _config.Config.PatchBseMoveset = enabled;
        _config.SaveDebounced();

        var tip = enabled
            ? "Patch BSE moveset is on. Click Install / patch modules again to add Moveset, then restart Dolphin."
            : "Patch BSE moveset is off. Click Install / patch modules again to remove Moveset (vanilla moveset only), then restart Dolphin.";
        if (ModuleInstallStatusText != null)
            ModuleInstallStatusText.Text = tip;
        _config.Log(tip);

        var result = MessageBox.Show(
            tip + "\n\nInstall now?",
            "BSMSO — Install again",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
            InstallModules_Click(sender, e);
    }

    private void WorldSync_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || !_session.IsHosting)
            return;

        var enabled = WorldSyncToggle.IsChecked == true;
        _session.SetServerSync(enabled, enabled, enabled);
        UpdateClientWorldSyncStatus();
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
                _hideSeekWarpStatusActive = false;
            }

            _tagRunning = state.TagActive;
            StartStopTagButton.Content = state.TagActive ? "Stop Tag" : "Start Tag";
            if (!_hideSeekWarpStatusActive)
                HideSeekStatusText.Text = FormatHideSeekStatus(state, forClient: false);
            UpdateStartStopTagButtonState();
            ApplyClientGameModeView(state);
            SyncTagElapsedUi(state);
        }
        finally
        {
            _suppressHideSeekUiSync = false;
        }
    }

    private void ApplyClientGameModeView(GameModeStatePacket state)
    {
#if BSMSO_CLIENT_LITE
        return;
#else
        if (ClientGameModeText == null)
            return;

        ClientGameModeText.Text = state.GameMode == GameMode.HideSeek ? "Hide and Seek" : "Normal";
        ClientHideSeekPanel.Visibility = state.GameMode == GameMode.HideSeek ? Visibility.Visible : Visibility.Collapsed;

        if (state.GameMode == GameMode.HideSeek)
        {
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

            ClientHideSeekHidersList.ItemsSource = hiders;
            ClientHideSeekSeekersList.ItemsSource = seekers;
            if (!_hideSeekWarpStatusActive)
                ClientHideSeekStatusText.Text = FormatHideSeekStatus(state, forClient: true);
        }
        else
        {
            ClientHideSeekHidersList.ItemsSource = null;
            ClientHideSeekSeekersList.ItemsSource = null;
            ClientHideSeekStatusText.Text = string.Empty;
            _hideSeekWarpStatusActive = false;
        }
#endif
    }

    private void SyncTagElapsedUi(GameModeStatePacket state)
    {
        var live = state.GameMode == GameMode.HideSeek && state.TagActive;
        if (live)
        {
            if (!_tagElapsedLive || state.RoundStartMs != _tagElapsedBaseMs)
            {
                _tagElapsedBaseMs = state.RoundStartMs;
                _tagElapsedAnchorTick = Environment.TickCount64;
            }

            _tagElapsedLive = true;
            EnsureTagElapsedUiTimer();
            _tagElapsedUiTimer!.Start();
        }
        else
        {
            _tagElapsedLive = false;
            _tagElapsedBaseMs = state.GameMode == GameMode.HideSeek ? state.RoundStartMs : 0;
            _tagElapsedUiTimer?.Stop();
        }

        RefreshTagElapsedTexts(state.GameMode == GameMode.HideSeek);
    }

    private void EnsureTagElapsedUiTimer()
    {
        if (_tagElapsedUiTimer != null)
            return;

        _tagElapsedUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tagElapsedUiTimer.Tick += (_, _) => RefreshTagElapsedTexts(hideSeekActive: true);
    }

    private uint CurrentTagElapsedMs()
    {
        if (!_tagElapsedLive)
            return _tagElapsedBaseMs;

        var delta = Environment.TickCount64 - _tagElapsedAnchorTick;
        if (delta <= 0)
            return _tagElapsedBaseMs;
        return _tagElapsedBaseMs + (uint)delta;
    }

    private static string FormatTagElapsed(uint ms)
    {
        var totalSec = ms / 1000u;
        var minutes = totalSec / 60u;
        var seconds = totalSec % 60u;
        return minutes >= 60
            ? $"{minutes / 60}:{minutes % 60:D2}:{seconds:D2}"
            : $"{minutes}:{seconds:D2}";
    }

    private void RefreshTagElapsedTexts(bool hideSeekActive)
    {
        var text = hideSeekActive
            ? $"Tag time: {FormatTagElapsed(CurrentTagElapsedMs())}"
            : "Tag time: —";
        HideSeekTagElapsedText.Text = text;
        ClientHideSeekTagElapsedText.Text = text;
    }

    private static string FormatHideSeekStatus(GameModeStatePacket state, bool forClient)
    {
        if (state.GameMode != GameMode.HideSeek)
            return string.Empty;
        if (state.RoundComplete)
            return "All hiders found!";
        if (state.TagActive && state.GraceActive)
        {
            var seconds = Math.Max(1, (state.GraceRemainingMs + 999) / 1000);
            return $"Hide grace: {seconds}s — seekers frozen.";
        }
        if (state.TagActive)
            return "Tag is running.";
        return forClient
            ? "Waiting for host to assign seekers and start tag."
            : "Assign seekers, then start tag.";
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

            // Prefer server role so a rejoining seeker is not demoted to hider in the UI.
            var gm = _session.GameModeState;
            if (row.Slot < gm.Roles.Length && gm.Roles[row.Slot] == HideSeekRole.Seeker)
                seekers.Add(row);
            else
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

        // During an active tag round, only update list membership — never auto-push.
        // Pushing on roster growth used to default rejoins to Hider and stop tag via SetRoles.
        if (_tagRunning)
        {
            RealignHideSeekListsToServerRoles(hiders, seekers);
            return;
        }

        PushHideSeekRolesToServer();
    }

    /// <summary>
    /// Mid-tag the server owns the roles (a reclaimed slot is forced back to Hider), and
    /// the host UI never pushes. Mirror the server state into the lists so the host is not
    /// looking at a stale side for a player who joined or was reassigned during the round.
    /// </summary>
    private void RealignHideSeekListsToServerRoles(
        ObservableCollection<RosterViewModel> hiders,
        ObservableCollection<RosterViewModel> seekers)
    {
        var state = _session.GameModeState;
        if (state.GameMode != GameMode.HideSeek)
            return;

        foreach (var row in hiders.ToArray())
        {
            if (state.GetRole(row.Slot) != (byte)HideSeekRole.Seeker)
                continue;

            hiders.Remove(row);
            if (!seekers.Any(s => s.Slot == row.Slot))
                seekers.Add(row);
        }

        foreach (var row in seekers.ToArray())
        {
            if (state.GetRole(row.Slot) != (byte)HideSeekRole.Hider)
                continue;

            seekers.Remove(row);
            if (!hiders.Any(h => h.Slot == row.Slot))
                hiders.Add(row);
        }
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
            RandomTagButton.IsEnabled = false;
            RandomLevelButton.IsEnabled = false;
            return;
        }

        var canHostActions = _session.IsHosting;
        RandomLevelButton.IsEnabled = canHostActions;

        var hiderCount = (HideSeekHidersList.ItemsSource as ObservableCollection<RosterViewModel>)?.Count ?? 0;
        var seekerCount = (HideSeekSeekersList.ItemsSource as ObservableCollection<RosterViewModel>)?.Count ?? 0;
        StartStopTagButton.IsEnabled = _tagRunning || (hiderCount >= 1 && seekerCount >= 1);
        RandomTagButton.IsEnabled = canHostActions && _rosterItems.Count >= 2;
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
        HideSeekRandomTagExemption.Clear(_randomTagExemptRoundsBySlot);
        SyncHideSeekRoleListsFromRoster(forceAllHiders: true);
        HideSeekStatusText.Text = "Everyone reset to hiders. Timer cleared.";
    }

    private void ResetFlagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsHosting)
            return;

        var confirm = MessageBox.Show(
            "Reset ALL session progress for everyone (new-file style)?\n\n" +
            "Clears shines, blues, story, secrets, nozzles, plaza gates,\n" +
            "red coins, NPC cleans, and graffiti.\n\n" +
            "Cutscene-watched flags and options are kept.\n" +
            "Re-enter a stage to respawn actors.",
            "BSMSO — Reset Session Progress",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
            return;

        _session.ResetSessionProgress();
        ClientTeleportStatusText.Text =
            "Session progress reset. Re-enter stages to respawn collectibles.";
    }

    private void RandomTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsHosting || GameModeCombo.SelectedIndex != 1)
            return;

        var players = _rosterItems.OrderBy(r => r.Slot).ToArray();
        if (players.Length < 2)
        {
            HideSeekStatusText.Text = "Need at least 2 connected players to pick a random seeker.";
            return;
        }

        HideSeekRandomTagExemption.PruneDisconnected(_randomTagExemptRoundsBySlot, players.Select(p => p.Slot));

        var exempt = HideSeekRandomTagExemption.GetExemptSlots(_randomTagExemptRoundsBySlot).ToHashSet();
        var pool = players.Where(p => !exempt.Contains(p.Slot)).ToArray();
        if (pool.Length == 0)
            pool = players;

        var chosen = pool[_random.Next(pool.Length)];
        HideSeekRandomTagExemption.RegisterPick(_randomTagExemptRoundsBySlot, chosen.Slot, players.Length);

        var roles = new Dictionary<byte, HideSeekRole>();
        foreach (var row in players)
            roles[row.Slot] = row.Slot == chosen.Slot ? HideSeekRole.Seeker : HideSeekRole.Hider;

        _session.SetHideSeekRoles(roles);
        HideSeekStatusText.Text = $"Random seeker: {chosen.Username} (use Start Tag when ready).";
        UpdateStartStopTagButtonState();
    }

    private void RandomLevelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsHosting || _levels == null)
            return;

        const int levelCooldownRounds = 5;
        const byte defaultEpisodeId = 7; // 0-indexed: episodeId 7 == "Episode 8"
        // Per-course episode overrides (0-indexed episode IDs).
        // Pinna: always warp to Park Area (course 13), never the beach (course 5).
        // Catalog ep 7 maps to pinnaParco5 (scenario 5) via PinnaParkInteriorMapping —
        // raw scenario 7 is Episode 1 post–Mecha-Bowser shine spawn, not balloons.
        // Sirena Beach ep 7/8 catalog remaps to Hotel Interior (area 7) via
        // LevelCatalog.ResolveWarpDestination (authority keys match delfino3/4).
        var episodeOverrides = new Dictionary<byte, byte>
        {
            { 13, 7 }, // Pinna Park Area -> Episode 8 catalog (→ scenario 5 / pinnaParco5)
            { 9, 5 },  // Noki Bay -> Episode 6
        };

        static byte TargetEpisode(CourseEntry c, Dictionary<byte, byte> overrides) =>
            overrides.TryGetValue(c.CourseId, out var ep) ? ep : defaultEpisodeId;

        bool HasTargetEpisode(CourseEntry c) =>
            c.CourseId != 5 &&  // Pinna beach — use Park Area (13) instead
            c.CourseId != 16 && // Noki Undersea — excluded from random pool
            c.Warpable &&
            c.Episodes.Any(ep => ep.EpisodeId == TargetEpisode(c, episodeOverrides));

        var all = _levels.Courses.Where(HasTargetEpisode).ToList();
        if (all.Count == 0)
        {
            HideSeekStatusText.Text = "No random levels are available.";
            return;
        }

        // A picked level is exempt for `levelCooldownRounds` subsequent rounds. The queue
        // holds the most recent picks; once it overflows, the oldest becomes eligible again.
        var exempt = _recentRandomLevelCourseIds.ToHashSet();
        var pool = all.Where(c => !exempt.Contains(c.CourseId)).ToList();
        if (pool.Count == 0)
            pool = all;

        var course = pool[_random.Next(pool.Count)];
        var episodeId = TargetEpisode(course, episodeOverrides);

        _recentRandomLevelCourseIds.Enqueue(course.CourseId);
        while (_recentRandomLevelCourseIds.Count > levelCooldownRounds)
            _recentRandomLevelCourseIds.Dequeue();

        _session.HostWarp(ProtocolConstants.WarpAllSlots, course.CourseId, episodeId);
        // Status text is set for host + clients via WarpEveryoneReceived when the
        // warp-all command is applied.
    }

    private void ShowWarpingEveryoneStatus(byte courseId, byte episodeId)
    {
        var courseName = _levels?.GetCourseName(courseId) ?? $"Course {courseId}";
        var episodeLabel = _levels?.GetEpisodeDisplayName(courseId, episodeId) ?? $"Episode {episodeId + 1}";
        var text = $"Warping everyone to {courseName} — {episodeLabel}";

        // Host Hide & Seek status (Server Actions Game Modes panel).
        HideSeekStatusText.Text = text;

        // Client Hide & Seek status (Client Actions Game Modes panel).
        // ClientWarpStatusText alone is under Teleport and is often covered by the
        // client-teleport overlay, so clients never saw the destination there.
        if (ClientHideSeekStatusText != null)
            ClientHideSeekStatusText.Text = text;
        _hideSeekWarpStatusActive = true;

        // Also surface under Teleport when that panel is usable (overlay off).
        ClientWarpStatusText.Text = text;
        ClientWarpStatusText.Visibility = Visibility.Visible;
        _clientWarpStatusClearTimer?.Stop();
        _clientWarpStatusClearTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _clientWarpStatusClearTimer.Tick -= ClientWarpStatusClearTimer_Tick;
        _clientWarpStatusClearTimer.Tick += ClientWarpStatusClearTimer_Tick;
        _clientWarpStatusClearTimer.Start();
    }

    private void ClientWarpStatusClearTimer_Tick(object? sender, EventArgs e)
    {
        ClearClientWarpStatus();
    }

    private void ClearClientWarpStatus()
    {
        _clientWarpStatusClearTimer?.Stop();
        ClientWarpStatusText.Text = string.Empty;
        ClientWarpStatusText.Visibility = Visibility.Collapsed;

        if (!_hideSeekWarpStatusActive)
            return;

        _hideSeekWarpStatusActive = false;
        var state = _session.GameModeState;
        if (state.GameMode != GameMode.HideSeek)
            return;

        if (HideSeekStatusText != null)
            HideSeekStatusText.Text = FormatHideSeekStatus(state, forClient: false);
        if (ClientHideSeekStatusText != null)
            ClientHideSeekStatusText.Text = FormatHideSeekStatus(state, forClient: true);
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
        // Releases this launcher's instance-index lock so the slot is reusable immediately.
        _config.Dispose();
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
