using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using SMSO.Net;

namespace SMSO.Launcher;

internal static class DolphinConfigService
{
    private const string DolphinRegistryKey = @"Software\Dolphin Emulator";
    private const string ConfigDirectoryName = "Config";
    private const string GameSettingsDirectoryName = "GameSettings";
    private const string DolphinIniName = "Dolphin.ini";
    private const string CoreSection = "Core";
    private const string RamOverrideEnableKey = "RAMOverrideEnable";
    private const string Mem1SizeKey = "MEM1Size";
    private const string Mem2SizeKey = "MEM2Size";
    private const string TargetMem1Size = "0x03000000"; // 48 MiB, conservative GDEV-size MEM1.
    private const string TargetMem2Size = "0x04000000";

    private static readonly string[] LegacyGameSettingsIniNames =
    {
        "GMS.ini",
        $"{GameIdentity.VanillaNtscUGameId}.ini",
    };

    public static bool EnsureBsmsGameIdentity(
        string gamePath,
        Action<string>? log,
        out bool gameIdChanged,
        out string? error)
    {
        error = null;
        gameIdChanged = false;

        if (string.IsNullOrWhiteSpace(gamePath))
            return true;

        var trimmed = gamePath.Trim().Trim('"');
        if (!File.Exists(trimmed) && !Directory.Exists(trimmed))
        {
            error = $"Game path not found: {trimmed}";
            return false;
        }

        try
        {
            if (!GameIdentity.TryResolveBootBinPath(trimmed, out var bootBinPath))
            {
                error = "Could not locate sys/boot.bin for the configured game path.";
                return false;
            }

            if (GameIdentity.TryReadGameId(bootBinPath, out var currentId) &&
                string.Equals(currentId, GameIdentity.BsmsGameId, StringComparison.Ordinal))
            {
                log?.Invoke($"BSMSO game ID already set to {GameIdentity.BsmsGameId} ({bootBinPath})");
                return true;
            }

            if (!GameIdentity.TryPatchGameId(bootBinPath, GameIdentity.BsmsGameId, out error))
                return false;

            gameIdChanged = true;
            log?.Invoke(
                $"Patched BSMSO game ID to {GameIdentity.BsmsGameId} ({bootBinPath}; was {currentId})");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to patch BSMSO game ID: {ex.Message}";
            return false;
        }
    }

    public static void ClearDolphinGameListCache(string dolphinPath, Action<string>? log = null)
    {
        try
        {
            var cleared = 0;
            foreach (var cacheDirectory in ResolveCacheDirectories(dolphinPath))
            {
                cleared += ClearCacheDirectory(cacheDirectory);
            }

            if (cleared > 0)
                log?.Invoke($"Cleared Dolphin game list cache ({cleared} files) so GMSE90 is detected.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not clear Dolphin game list cache: {ex.Message}");
        }
    }

    public static bool EnsureBsmsGameBanner(
        string gamePath,
        Action<string>? log,
        out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(gamePath))
            return true;

        var trimmed = gamePath.Trim().Trim('"');
        if (!File.Exists(trimmed) && !Directory.Exists(trimmed))
        {
            error = $"Game path not found: {trimmed}";
            return false;
        }

        var bannerAssetPath = ResolveBannerAssetPath();
        if (!File.Exists(bannerAssetPath))
        {
            error = $"BSMSO banner asset not found: {bannerAssetPath}";
            return false;
        }

        try
        {
            if (!TryResolveSysDirectory(trimmed, out var sysDirectory, out var gameFileStem))
            {
                error = "Could not locate sys/ for the configured game path.";
                return false;
            }

            var deployed = 0;
            if (CopyBannerIfChanged(bannerAssetPath, Path.Combine(sysDirectory, $"{gameFileStem}.png")))
                deployed++;
            if (CopyBannerIfChanged(bannerAssetPath, Path.Combine(sysDirectory, "icon.png")))
                deployed++;

            log?.Invoke(
                deployed > 0
                    ? $"Installed BSMSO Dolphin banner in {sysDirectory} ({gameFileStem}.png, icon.png)."
                    : $"BSMSO Dolphin banner already installed in {sysDirectory}.");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to install BSMSO Dolphin banner: {ex.Message}";
            return false;
        }
    }

    public static bool EnsureBsmsGameCover(
        string dolphinPath,
        Action<string>? log,
        out string? error)
    {
        error = null;

        var bannerAssetPath = ResolveBannerAssetPath();
        if (!File.Exists(bannerAssetPath))
        {
            error = $"BSMSO banner asset not found: {bannerAssetPath}";
            return false;
        }

        try
        {
            var deployed = 0;
            foreach (var cacheDirectory in ResolveCacheDirectories(dolphinPath))
            {
                var coverDirectory = Path.Combine(cacheDirectory, "GameCovers");
                Directory.CreateDirectory(coverDirectory);
                var coverPath = Path.Combine(coverDirectory, $"{GameIdentity.BsmsGameId}.png");
                if (CopyBannerIfChanged(bannerAssetPath, coverPath))
                    deployed++;
            }

            if (deployed > 0)
                log?.Invoke($"Installed BSMSO cover art as {GameIdentity.BsmsGameId}.png in Dolphin GameCovers.");

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to install BSMSO Dolphin cover art: {ex.Message}";
            return false;
        }
    }

    public static bool EnsureMultiplayerMemoryConfig(
        string dolphinPath,
        Action<string>? log,
        out string? error)
    {
        error = null;

        var exePath = dolphinPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            error = "Dolphin path not found.";
            return false;
        }

        try
        {
            var userDirectory = ResolveUserDirectory(exePath);
            var configDirectory = Path.Combine(userDirectory, ConfigDirectoryName);
            Directory.CreateDirectory(configDirectory);

            var dolphinIni = Path.Combine(configDirectory, DolphinIniName);
            var dolphinChanged = EnsureRamOverrideIni(dolphinIni);

            var gameChanged = false;
            string? configuredIni = null;
            foreach (var gameSettingsDirectory in ResolveGameSettingsDirectories(userDirectory))
            {
                Directory.CreateDirectory(gameSettingsDirectory);
                var bsmsGameIni = Path.Combine(gameSettingsDirectory, $"{GameIdentity.BsmsGameId}.ini");
                MigrateLegacyGameSettings(gameSettingsDirectory, bsmsGameIni);
                gameChanged |= EnsureRamOverrideIni(bsmsGameIni);
                configuredIni ??= bsmsGameIni;
            }

            var verb = dolphinChanged || gameChanged ? "Configured" : "Dolphin RAM override already configured for";
            log?.Invoke($"{verb} BSMSO: MEM1={TargetMem1Size}, MEM2={TargetMem2Size} ({dolphinIni}; {configuredIni})");

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to configure Dolphin memory settings: {ex.Message}";
            return false;
        }
    }

    internal static string ResolveUserDirectory(string dolphinExePath)
    {
        var exeDirectory = Path.GetDirectoryName(Path.GetFullPath(dolphinExePath))
            ?? Environment.CurrentDirectory;

        if (Directory.Exists(Path.Combine(exeDirectory, "User")) ||
            File.Exists(Path.Combine(exeDirectory, "portable.txt")) ||
            IsRegistryLocalUserConfigEnabled())
        {
            return Path.Combine(exeDirectory, "User");
        }

        var registryPath = ReadRegistryUserConfigPath();
        if (!string.IsNullOrWhiteSpace(registryPath))
            return registryPath!;

        var roamingPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dolphin Emulator");
        var documentsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Dolphin Emulator");

        return Directory.Exists(documentsPath) ? documentsPath : roamingPath;
    }

    internal static IEnumerable<string> ResolveGameSettingsDirectories(string userDirectory)
    {
        var directories = new List<string>();
        var rootSettings = Path.Combine(userDirectory, GameSettingsDirectoryName);
        var configSettings = Path.Combine(userDirectory, ConfigDirectoryName, GameSettingsDirectoryName);

        if (Directory.Exists(rootSettings))
            directories.Add(rootSettings);

        if (Directory.Exists(configSettings) ||
            Directory.Exists(Path.Combine(userDirectory, ConfigDirectoryName)))
        {
            directories.Add(configSettings);
        }

        if (directories.Count == 0)
            directories.Add(configSettings);

        return directories;
    }

    private static IEnumerable<string> ResolveCacheDirectories(string dolphinExePath)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var userDirectory = ResolveUserDirectory(dolphinExePath);
        directories.Add(Path.Combine(userDirectory, "Cache"));

        var roamingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Dolphin Emulator");
        if (!string.Equals(userDirectory, roamingDirectory, StringComparison.OrdinalIgnoreCase))
            directories.Add(Path.Combine(roamingDirectory, "Cache"));

        return directories;
    }

    private static int ClearCacheDirectory(string cacheDirectory)
    {
        if (!Directory.Exists(cacheDirectory))
            return 0;

        var removed = 0;
        foreach (var cacheFile in Directory.EnumerateFiles(cacheDirectory))
        {
            var name = Path.GetFileName(cacheFile);
            if (string.Equals(name, "gamelist.cache", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".uidcache", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(cacheFile);
                removed++;
            }
        }

        return removed;
    }

    internal static string ResolveBannerAssetPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "bsmso-banner.png");

    private static bool TryResolveSysDirectory(
        string gamePath,
        out string sysDirectory,
        out string gameFileStem)
    {
        sysDirectory = string.Empty;
        gameFileStem = string.Empty;

        if (File.Exists(gamePath))
        {
            sysDirectory = Path.GetDirectoryName(gamePath) ?? string.Empty;
            gameFileStem = Path.GetFileNameWithoutExtension(gamePath);
            return Directory.Exists(sysDirectory) && gameFileStem.Length > 0;
        }

        if (!GameIdentity.TryResolveBootBinPath(gamePath, out var bootBinPath))
            return false;

        sysDirectory = Path.GetDirectoryName(bootBinPath) ?? string.Empty;
        gameFileStem = "main";
        return Directory.Exists(sysDirectory);
    }

    private static bool CopyBannerIfChanged(string sourcePath, string destinationPath)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (File.Exists(destinationPath))
        {
            var destinationInfo = new FileInfo(destinationPath);
            if (sourceInfo.Length == destinationInfo.Length &&
                sourceInfo.LastWriteTimeUtc <= destinationInfo.LastWriteTimeUtc)
            {
                return false;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return true;
    }

    private static void MigrateLegacyGameSettings(string gameSettingsDirectory, string targetIniPath)
    {
        if (File.Exists(targetIniPath))
            return;

        foreach (var legacyName in LegacyGameSettingsIniNames)
        {
            var legacyPath = Path.Combine(gameSettingsDirectory, legacyName);
            if (!File.Exists(legacyPath))
                continue;

            File.Copy(legacyPath, targetIniPath);
            return;
        }
    }

    private static bool EnsureRamOverrideIni(string path)
    {
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : new List<string>();

        var changed = false;
        changed |= UpsertIniValue(lines, CoreSection, RamOverrideEnableKey, "True");
        changed |= UpsertIniValue(lines, CoreSection, Mem1SizeKey, TargetMem1Size);
        changed |= UpsertIniValue(lines, CoreSection, Mem2SizeKey, TargetMem2Size);

        if (changed)
            File.WriteAllLines(path, lines);

        return changed;
    }

    private static bool IsRegistryLocalUserConfigEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DolphinRegistryKey);
            var value = key?.GetValue("LocalUserConfig");
            return value switch
            {
                int i => i == 1,
                string s => s.Trim() == "1",
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadRegistryUserConfigPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DolphinRegistryKey);
            return key?.GetValue("UserConfigPath") as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool UpsertIniValue(List<string> lines, string section, string key, string value)
    {
        var (sectionStart, sectionEnd) = FindSection(lines, section);
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");
            return true;
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (!TryReadIniKey(lines[i], out var existingKey))
                continue;
            if (!string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
                continue;

            var newLine = $"{key} = {value}";
            if (string.Equals(lines[i].Trim(), newLine, StringComparison.Ordinal))
                return false;

            lines[i] = newLine;
            return true;
        }

        lines.Insert(sectionEnd, $"{key} = {value}");
        return true;
    }

    private static (int Start, int End) FindSection(IReadOnlyList<string> lines, string section)
    {
        var start = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Trim();
            if (!text.StartsWith("[", StringComparison.Ordinal) ||
                !text.EndsWith("]", StringComparison.Ordinal))
            {
                continue;
            }

            if (start >= 0)
                return (start, i);

            var name = text[1..^1].Trim();
            if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                start = i;
        }

        return start >= 0 ? (start, lines.Count) : (-1, -1);
    }

    private static bool TryReadIniKey(string line, out string key)
    {
        key = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 ||
            trimmed.StartsWith(";", StringComparison.Ordinal) ||
            trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return false;
        }

        var equals = trimmed.IndexOf('=');
        if (equals <= 0)
            return false;

        key = trimmed[..equals].Trim();
        return key.Length > 0;
    }
}
