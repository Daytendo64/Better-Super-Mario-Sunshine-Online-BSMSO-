using System;
using System.IO;
using System.Text.Json;
using SMSO.Net;

namespace SMSO.Launcher;

public sealed class AppConfig
{
    public string Username { get; set; } = "Player";
    public string DolphinPath { get; set; } = "";
    public string IsoPath { get; set; } = "";
    public string ServerIp { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; } = 27015;
    public int MaxPlayers { get; set; } = ProtocolConstants.StableMaxPlayers;
    public string NameTagColor { get; set; } = "FFFFFF";
    public string NameTagGradientColor { get; set; } = "888888";
    public string NameTagOutlineColor { get; set; } = "000000";
    public bool NameTagGradientEnabled { get; set; }
    /// <summary>Empty = retail Mario; otherwise 8-char hex pack id from CustomModels library.</summary>
    public string SelectedMarioModelId { get; set; } = "";
    public uint MailboxAddress { get; set; } = 0x817FC000;
    public bool SyncFlags { get; set; }
    public bool SyncObjects { get; set; }
    public bool SyncProgress { get; set; }
    public bool AllowClientTeleporting { get; set; }
    /// <summary>
    /// Start Tag hide-grace duration in seconds (seekers frozen).
    /// Allowed values: 15, 30, 45, 60.
    /// </summary>
    public int HideSeekGraceSeconds { get; set; } = 30;
    /// <summary>
    /// When true, Launch Dolphin applies the BSMSO performance/stability profile.
    /// When false, restores the backed-up original Dolphin settings (RAM override still kept).
    /// </summary>
    public bool ApplyRecommendedDolphinSettings { get; set; }
}

public sealed class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMSO");
    private static readonly string SharedConfigPath = Path.Combine(ConfigDir, "config.json");
    private static readonly string InstancesDir = Path.Combine(ConfigDir, "instances");
    private static readonly string LogDir = Path.Combine(ConfigDir, "logs");

    private readonly string _configPath;
    private readonly string _logSuffix;
    private System.Timers.Timer? _debounce;
    private AppConfig _config = new();

    public AppConfig Config => _config;
    public string LogDirectory => LogDir;
    public int InstanceIndex { get; }
    public string InstanceLabel => InstanceIndex == 0 ? "Instance 1" : $"Instance {InstanceIndex + 1}";

    public ConfigService()
    {
        InstanceIndex = InstanceAllocator.GetInstanceIndex();
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(InstancesDir);
        Directory.CreateDirectory(LogDir);

        if (InstanceIndex == 0)
        {
            _configPath = SharedConfigPath;
            _logSuffix = "";
        }
        else
        {
            _configPath = Path.Combine(InstancesDir, $"instance{InstanceIndex}.json");
            _logSuffix = $".instance{InstanceIndex}";
        }
    }

    public void Load()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<AppConfig>(json) ?? CreateDefaults();
                if (MigrateLegacySyncDefaults(_config))
                    Save();
                NormalizeConfig(_config);
                return;
            }
            catch { /* fall through to defaults */ }
        }

        _config = CreateDefaults();
        if (InstanceIndex > 0)
            SeedFromSharedConfig();
        Save();
    }

    private AppConfig CreateDefaults()
    {
        var cfg = new AppConfig
        {
            AllowClientTeleporting = false,
            SyncFlags = true,
            SyncObjects = true,
            SyncProgress = true,
        };
        if (InstanceIndex > 0)
            cfg.Username = $"Player{InstanceIndex + 1}";
        return cfg;
    }

    private void SeedFromSharedConfig()
    {
        if (!File.Exists(SharedConfigPath))
            return;

        try
        {
            var shared = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(SharedConfigPath));
            if (shared == null) return;

            _config.DolphinPath = shared.DolphinPath;
            _config.IsoPath = shared.IsoPath;
            _config.ServerIp = shared.ServerIp;
            _config.ServerPort = shared.ServerPort;
            _config.MaxPlayers = ClampMaxPlayers(shared.MaxPlayers);
            _config.NameTagColor = shared.NameTagColor;
            _config.NameTagGradientColor = shared.NameTagGradientColor;
            _config.NameTagOutlineColor = shared.NameTagOutlineColor;
            _config.NameTagGradientEnabled = shared.NameTagGradientEnabled;
            _config.SelectedMarioModelId = shared.SelectedMarioModelId;
            _config.MailboxAddress = shared.MailboxAddress;
            _config.SyncFlags = shared.SyncFlags;
            _config.SyncObjects = shared.SyncObjects;
            _config.SyncProgress = shared.SyncProgress;
        }
        catch { /* ignore */ }
    }

    private static int ClampMaxPlayers(int value) =>
        Math.Clamp(value, 2, ProtocolConstants.StableMaxPlayers);

    /// <summary>Start Tag hide grace: 15 / 30 / 45 / 60 seconds.</summary>
    private static int ClampHideSeekGraceSeconds(int value)
    {
        ReadOnlySpan<int> options = [15, 30, 45, 60];
        var best = options[0];
        var bestDist = Math.Abs(value - best);
        foreach (var opt in options)
        {
            var dist = Math.Abs(value - opt);
            if (dist < bestDist)
            {
                best = opt;
                bestDist = dist;
            }
        }

        return best;
    }

    private static void NormalizeConfig(AppConfig config)
    {
        config.MaxPlayers = ClampMaxPlayers(config.MaxPlayers);
        config.HideSeekGraceSeconds = ClampHideSeekGraceSeconds(config.HideSeekGraceSeconds);
    }

    /// <summary>
    /// Older configs saved all sync toggles as false before world-sync defaults existed.
    /// Treat that as unset and opt into multiplayer progress sync.
    /// </summary>
    private static bool MigrateLegacySyncDefaults(AppConfig config)
    {
        if (config.SyncFlags || config.SyncObjects || config.SyncProgress)
            return false;

        config.SyncFlags = true;
        config.SyncObjects = true;
        config.SyncProgress = true;
        return true;
    }

    public void SaveDebounced()
    {
        if (_debounce == null)
        {
            _debounce = new System.Timers.Timer(300) { AutoReset = false };
            _debounce.Elapsed += (_, _) => Save();
        }

        _debounce.Stop();
        _debounce.Start();
    }

    public void Save()
    {
        NormalizeConfig(_config);
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);

        if (InstanceIndex == 0)
            return;

        // Keep shared dolphin/iso paths in sync for convenience when testing with multiple windows.
        try
        {
            AppConfig shared;
            if (File.Exists(SharedConfigPath))
            {
                shared = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(SharedConfigPath)) ?? new AppConfig();
            }
            else
            {
                shared = new AppConfig();
            }

            shared.DolphinPath = _config.DolphinPath;
            shared.IsoPath = _config.IsoPath;
            shared.NameTagColor = _config.NameTagColor;
            shared.NameTagGradientColor = _config.NameTagGradientColor;
            shared.NameTagOutlineColor = _config.NameTagOutlineColor;
            shared.NameTagGradientEnabled = _config.NameTagGradientEnabled;
            shared.SelectedMarioModelId = _config.SelectedMarioModelId;
            var sharedJson = JsonSerializer.Serialize(shared, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SharedConfigPath, sharedJson);
        }
        catch { /* ignore */ }
    }

    public void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{InstanceLabel}] {message}";
        var path = Path.Combine(LogDir, $"smso-{DateTime.Now:yyyy-MM-dd}{_logSuffix}.log");
        File.AppendAllText(path, line + Environment.NewLine);
    }
}
