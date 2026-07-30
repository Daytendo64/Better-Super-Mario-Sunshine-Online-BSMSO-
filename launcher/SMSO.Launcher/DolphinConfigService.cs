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
    private const string GfxIniName = "GFX.ini";
    private const string CoreSection = "Core";
    private const string VideoSettingsSection = "Video_Settings";
    private const string VideoEnhancementsSection = "Video_Enhancements";
    private const string VideoHacksSection = "Video_Hacks";
    private const string GfxSettingsSection = "Settings";
    private const string GfxEnhancementsSection = "Enhancements";
    private const string GfxHacksSection = "Hacks";
    private const string RamOverrideEnableKey = "RAMOverrideEnable";
    private const string Mem1SizeKey = "MEM1Size";
    private const string Mem2SizeKey = "MEM2Size";
    private const string TargetMem1Size = "0x03000000"; // 48 MiB, conservative GDEV-size MEM1.
    private const string TargetMem2Size = "0x04000000";
    private const string BackupRootFolderName = "dolphin-settings-backup";
    private const string BackupMarkerName = ".backed-up";
    /// <summary>
    /// Present after the recommended performance profile has been written once for this
    /// Dolphin User directory. Later launches with the toggle on only enforce required
    /// Core keys so manual Dolphin graphics/settings tweaks survive Launch Dolphin.
    /// Cleared when the Connection toggle is turned off (so the next ON re-applies).
    /// </summary>
    private const string RecommendedAppliedMarkerName = ".recommended-applied";

    // Always applied for GMSE90 (even when the recommended performance profile is off).
    // Overclock=2.0 ⇒ Dolphin UI "Emulated CPU Clock Override" at 200%.
    // Fast Disc Speed is NOT forced here — it is part of the recommended profile only
    // (no separate launcher toggle); when recommended is off the user chooses in Dolphin.
    private static readonly (string Key, string Value)[] CoreRequiredKeys =
    [
        ("OverclockEnable", "True"),
        ("Overclock", "2.0"),
        (RamOverrideEnableKey, "True"),
        (Mem1SizeKey, TargetMem1Size),
        (Mem2SizeKey, TargetMem2Size),
    ];

    // BSMSO GameINI / Dolphin.ini [Core] performance + stability profile.
    // Does not force GFXBackend (GPU-dependent).
    // FastDiscSpeed=True ⇒ Dolphin UI "Emulate Disc Speed" OFF (INI key is inverted).
    // Prefer Fast Disc Speed with the recommended profile (avoids BSE black-screen hangs).
    private static readonly (string Key, string Value)[] CorePerformanceKeys =
    [
        ("CPUThread", "True"),
        ("DSPHLE", "True"),
        ("OverclockEnable", "True"),
        ("Overclock", "2.0"),
        ("EmulationSpeed", "1.0"),
        ("FastDiscSpeed", "True"), // Dolphin UI: Emulate Disc Speed OFF
        ("SyncGPU", "False"),
        (RamOverrideEnableKey, "True"),
        (Mem1SizeKey, TargetMem1Size),
        (Mem2SizeKey, TargetMem2Size),
    ];

    // GameINI uses Video_* section names; GFX.ini uses Settings/Enhancements/Hacks.
    private static readonly (string Key, string Value)[] VideoSettingsKeys =
    [
        ("InternalResolution", "1"),
        ("MSAA", "0"),
        ("SSAA", "False"),
        ("MaxAnisotropy", "0"),
        ("ShaderCompilationMode", "2"),
        ("WaitForShadersBeforeStarting", "False"),
        ("BackendMultithreading", "True"),
        ("EnableGPUTextureDecoding", "True"),
        ("VSync", "False"),
    ];

    private static readonly (string Key, string Value)[] VideoEnhancementsKeys =
    [
        ("ForceFiltering", "False"),
        ("DisableCopyFilter", "True"),
        ("ArbitraryMipmapDetection", "False"),
    ];

    private static readonly (string Key, string Value)[] VideoHacksKeys =
    [
        ("EFBAccessEnable", "False"),
        ("EFBToTextureEnable", "False"),
        ("XFBToTextureEnable", "True"),
        ("ImmediateXFBEnable", "True"),
        ("SkipDuplicateXFBs", "False"),
        ("EFBEmulateFormatChanges", "False"),
        ("DeferEFBCopies", "True"),
        ("EFBScaledCopy", "True"),
        ("FastDepthCalc", "True"),
        ("VertexRounding", "False"),
        ("BBoxEnable", "False"),
    ];

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

            // Never write icon.png — Dolphin treats icon.png as a Homebrew-style banner for
            // EVERY game file in that folder (shared ISO directories would all show BSMSO art).
            RemoveSharedIconBannerIfOurs(sysDirectory, bannerAssetPath, log);
            if (File.Exists(trimmed))
            {
                var isoDirectory = Path.GetDirectoryName(Path.GetFullPath(trimmed));
                if (!string.IsNullOrEmpty(isoDirectory) &&
                    !string.Equals(isoDirectory, sysDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveSharedIconBannerIfOurs(isoDirectory, bannerAssetPath, log);
                }
            }

            var deployed = 0;
            // Side-car named after this game file only (e.g. main.png / MySms.iso → MySms.png).
            if (gameFileStem.Length > 0 &&
                CopyBannerIfChanged(bannerAssetPath, Path.Combine(sysDirectory, $"{gameFileStem}.png")))
            {
                deployed++;
            }

            log?.Invoke(
                deployed > 0
                    ? $"Installed BSMSO Dolphin banner in {sysDirectory} ({gameFileStem}.png)."
                    : $"BSMSO Dolphin banner already installed in {sysDirectory} ({gameFileStem}.png).");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to install BSMSO Dolphin banner: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Deletes a leftover <c>icon.png</c> only when it matches the BSMSO banner asset, so other
    /// games in the same folder stop incorrectly using it as their custom banner.
    /// </summary>
    private static void RemoveSharedIconBannerIfOurs(
        string directory,
        string bannerAssetPath,
        Action<string>? log)
    {
        var iconPath = Path.Combine(directory, "icon.png");
        if (!File.Exists(iconPath) || !File.Exists(bannerAssetPath))
            return;

        try
        {
            var iconInfo = new FileInfo(iconPath);
            var bannerInfo = new FileInfo(bannerAssetPath);
            if (iconInfo.Length != bannerInfo.Length)
                return;

            var iconBytes = File.ReadAllBytes(iconPath);
            var bannerBytes = File.ReadAllBytes(bannerAssetPath);
            if (!iconBytes.AsSpan().SequenceEqual(bannerBytes))
                return;

            File.Delete(iconPath);
            log?.Invoke(
                $"Removed shared icon.png from {directory} (it was overriding banners for every game in that folder).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not remove shared icon.png in {directory}: {ex.Message}");
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

    /// <summary>
    /// Launch-time Dolphin settings: always keeps Emulated CPU Clock Override at 200% and
    /// MEM1/MEM2. Fast Disc Speed is applied only with the recommended performance profile
    /// (no separate launcher toggle) — when that toggle is off, Dolphin’s own disc-speed
    /// setting is left alone.
    /// Optionally applies the recommended performance profile once (later launches keep your
    /// Dolphin tweaks). When the recommended toggle is off: restore the pre-profile backup only
    /// when leaving the recommended profile, otherwise keep live settings and refresh the
    /// pre-profile backup so Dolphin edits stick.
    /// </summary>
    public static bool ApplyLaunchDolphinSettings(
        string dolphinPath,
        bool applyRecommended,
        Action<string>? log,
        out string? error)
    {
        if (applyRecommended)
        {
            TryBackupOriginalSettings(dolphinPath, log);
            if (!IsRecommendedProfileApplied(dolphinPath))
            {
                // Performance profile includes Fast Disc Speed + CPU 200% / MEM; re-apply
                // CoreRequired afterward so always-forced keys win even if the profile list drifts.
                if (!EnsurePerformanceStabilityConfig(dolphinPath, log, out error))
                    return false;
                MarkRecommendedProfileApplied(dolphinPath, log);
            }
            else
            {
                log?.Invoke(
                    "Keeping your current Dolphin settings (recommended profile already applied). " +
                    "CPU clock 200% and MEM1/MEM2 still enforced. " +
                    "Turn the Connection toggle off and on to re-apply the full recommended profile " +
                    "(including Fast Disc Speed).");
            }

            return EnsureMultiplayerMemoryConfig(dolphinPath, log, out error);
        }

        // Leaving recommended (or already off): only restore when the profile was active so we
        // do not clobber settings the user just changed in Dolphin.
        var wasRecommendedApplied = IsRecommendedProfileApplied(dolphinPath);
        ClearRecommendedProfileApplied(dolphinPath);

        if (wasRecommendedApplied)
        {
            if (!TryRestoreOriginalSettings(dolphinPath, log, out var hadBackup, out error))
                return false;

            if (!hadBackup)
            {
                log?.Invoke(
                    "No backed-up Dolphin settings to restore — keeping current files " +
                    "(CPU clock 200% + MEM1/MEM2 still applied; Fast Disc Speed left as-is).");
            }
        }
        else
        {
            log?.Invoke(
                "Recommended profile off — keeping your current Dolphin settings " +
                "and updating the pre-profile backup.");
        }

        if (!EnsureMultiplayerMemoryConfig(dolphinPath, log, out error))
            return false;

        // Track live personal settings so a later ON→OFF restore uses what you last used.
        RefreshPreProfileBackup(dolphinPath, log);
        return true;
    }

    /// <summary>
    /// Clears the once-applied marker so the next Launch with recommended on writes the
    /// full performance profile again. Call when the Connection toggle is turned off.
    /// </summary>
    public static void ClearRecommendedProfileApplied(string dolphinPath)
    {
        try
        {
            var marker = ResolveRecommendedAppliedMarkerPath(dolphinPath);
            if (marker is not null && File.Exists(marker))
                File.Delete(marker);
        }
        catch
        {
            // Best-effort — next Launch still enforces required Core keys.
        }
    }

    internal static bool IsRecommendedProfileApplied(string dolphinPath)
    {
        var marker = ResolveRecommendedAppliedMarkerPath(dolphinPath);
        return marker is not null && File.Exists(marker);
    }

    private static void MarkRecommendedProfileApplied(string dolphinPath, Action<string>? log)
    {
        try
        {
            var marker = ResolveRecommendedAppliedMarkerPath(dolphinPath);
            if (marker is null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            log?.Invoke(
                "Recommended Dolphin profile marked applied — later launches will keep your " +
                "manual Dolphin setting changes (required Core keys still enforced).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not mark recommended Dolphin profile as applied: {ex.Message}");
        }
    }

    private static string? ResolveRecommendedAppliedMarkerPath(string dolphinPath)
    {
        var exePath = dolphinPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;

        var backupDirectory = ResolveBackupDirectory(ResolveUserDirectory(exePath));
        return Path.Combine(backupDirectory, RecommendedAppliedMarkerName);
    }

    /// <summary>
    /// Applies always-required GMSE90 Core keys: Emulated CPU Clock Override at 200%, and RAM.
    /// Does not change Fast Disc Speed — that is only set by the recommended profile.
    /// </summary>
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
            var changed = EnsureIniFile(dolphinIni, lines =>
                UpsertIniValues(lines, CoreSection, CoreRequiredKeys));

            string? configuredIni = null;
            // Always write root GameSettings first — that is what Dolphin loads for GMSE90.
            foreach (var gameSettingsDirectory in ResolveGameSettingsDirectories(userDirectory))
            {
                Directory.CreateDirectory(gameSettingsDirectory);
                var bsmsGameIni = Path.Combine(gameSettingsDirectory, $"{GameIdentity.BsmsGameId}.ini");
                changed |= EnsureIniFile(bsmsGameIni, lines =>
                    UpsertIniValues(lines, CoreSection, CoreRequiredKeys));
                configuredIni ??= bsmsGameIni;
            }

            var verb = changed ? "Configured" : "Dolphin required Core already configured for";
            log?.Invoke(
                $"{verb} BSMSO: CPU clock override 200% (Overclock=2.0), " +
                $"MEM1={TargetMem1Size}, MEM2={TargetMem2Size} " +
                "(Fast Disc Speed only via recommended Dolphin settings)");
            log?.Invoke($"Dolphin User directory: {userDirectory}");
            if (configuredIni is not null)
                log?.Invoke($"BSMSO GameINI: {configuredIni}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to configure Dolphin memory settings: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Upserts GMSE90 GameINI + matching Dolphin.ini [Core] / GFX.ini keys for performance and
    /// stability. Preserves unrelated sections (controls, [Gecko], EnableCheats, etc.).
    /// Does not force GFXBackend. Includes Fast Disc Speed, CPU clock 200%, and MEM1/MEM2.
    /// </summary>
    public static bool EnsurePerformanceStabilityConfig(
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
            var gfxIni = Path.Combine(configDirectory, GfxIniName);
            var changed = false;

            changed |= EnsureIniFile(dolphinIni, ApplyCorePerformanceKeys);
            changed |= EnsureIniFile(gfxIni, ApplyGfxPerformanceKeys);

            string? configuredGameIni = null;
            // Always write root GameSettings first — that is what Dolphin loads for GMSE90.
            foreach (var gameSettingsDirectory in ResolveGameSettingsDirectories(userDirectory))
            {
                Directory.CreateDirectory(gameSettingsDirectory);
                var bsmsGameIni = Path.Combine(gameSettingsDirectory, $"{GameIdentity.BsmsGameId}.ini");
                // Do not seed from GMS.ini / GMSE01.ini — that would import vanilla GFX/controls into BSMSO.
                changed |= EnsureIniFile(bsmsGameIni, ApplyGameIniPerformanceKeys);
                configuredGameIni ??= bsmsGameIni;
            }

            log?.Invoke(
                changed
                    ? "Applied BSMSO Dolphin performance profile (1x IR, Dual Core, Hybrid shaders, CPU 200%, MEM1/MEM2…)"
                    : "BSMSO Dolphin performance profile already applied " +
                      $"(1x IR, Dual Core, Hybrid shaders, CPU 200%, MEM1={TargetMem1Size}, MEM2={TargetMem2Size})");
            log?.Invoke($"Dolphin User directory: {userDirectory}");

            if (configuredGameIni is not null)
                log?.Invoke($"BSMSO GameINI: {configuredGameIni}");

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to configure Dolphin performance settings: {ex.Message}";
            return false;
        }
    }

    internal static string ResolveUserDirectory(string dolphinExePath)
    {
        var exeDirectory = Path.GetDirectoryName(Path.GetFullPath(dolphinExePath))
            ?? Environment.CurrentDirectory;

        // Match Dolphin: portable only with portable.txt or LocalUserConfig=1.
        // A bare User/ folder next to Dolphin.exe is NOT enough — Dolphin still uses
        // AppData / UserConfigPath in that case (writing to User/ silently does nothing).
        if (File.Exists(Path.Combine(exeDirectory, "portable.txt")) ||
            IsRegistryLocalUserConfigEnabled())
        {
            var localUser = Path.Combine(exeDirectory, "User");
            Directory.CreateDirectory(localUser);
            return localUser;
        }

        var registryPath = ReadRegistryUserConfigPath();
        if (!string.IsNullOrWhiteSpace(registryPath))
            return registryPath.TrimEnd('\\', '/');

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
        // Always write both. Dolphin loads User/GameSettings/<id>.ini for per-game overrides;
        // Config/GameSettings is a secondary layout some installs also keep.
        yield return Path.Combine(userDirectory, GameSettingsDirectoryName);
        yield return Path.Combine(userDirectory, ConfigDirectoryName, GameSettingsDirectoryName);
    }

    /// <summary>
    /// Copies Dolphin.ini / GFX.ini / GMSE90.ini once before the first recommended-profile apply.
    /// Skips if a backup marker already exists (including backups refreshed while recommended is off).
    /// </summary>
    internal static void TryBackupOriginalSettings(string dolphinPath, Action<string>? log)
    {
        try
        {
            var exePath = dolphinPath.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            var userDirectory = ResolveUserDirectory(exePath);
            var backupDirectory = ResolveBackupDirectory(userDirectory);
            var markerPath = Path.Combine(backupDirectory, BackupMarkerName);
            if (File.Exists(markerPath))
                return;

            Directory.CreateDirectory(backupDirectory);
            var copied = CopySettingsToBackup(userDirectory, backupDirectory);
            File.WriteAllText(markerPath, userDirectory);
            log?.Invoke(
                copied > 0
                    ? $"Backed up original Dolphin settings ({copied} files) to {backupDirectory}"
                    : $"No existing Dolphin settings to back up yet (marker created at {backupDirectory})");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not back up original Dolphin settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Overwrites the pre-profile backup from the live Dolphin User INIs. Used while the
    /// recommended toggle is off so Launch Dolphin keeps tracking manual Dolphin edits.
    /// </summary>
    internal static void RefreshPreProfileBackup(string dolphinPath, Action<string>? log)
    {
        try
        {
            var exePath = dolphinPath.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return;

            var userDirectory = ResolveUserDirectory(exePath);
            var backupDirectory = ResolveBackupDirectory(userDirectory);
            Directory.CreateDirectory(backupDirectory);
            var copied = CopySettingsToBackup(userDirectory, backupDirectory);
            File.WriteAllText(Path.Combine(backupDirectory, BackupMarkerName), userDirectory);
            log?.Invoke(
                copied > 0
                    ? $"Updated pre-profile Dolphin settings backup ({copied} files)"
                    : "Pre-profile Dolphin settings backup refreshed (no INI files present yet)");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not update pre-profile Dolphin settings backup: {ex.Message}");
        }
    }

    private static int CopySettingsToBackup(string userDirectory, string backupDirectory)
    {
        var copied = 0;
        foreach (var relativePath in EnumerateBackupRelativePaths())
        {
            var source = Path.Combine(userDirectory, relativePath);
            if (!File.Exists(source))
                continue;

            var destination = Path.Combine(backupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Restores files from the one-time backup. Returns false with error on I/O failure.
    /// </summary>
    internal static bool TryRestoreOriginalSettings(
        string dolphinPath,
        Action<string>? log,
        out bool hadBackup,
        out string? error)
    {
        error = null;
        hadBackup = false;

        try
        {
            var exePath = dolphinPath.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                error = "Dolphin path not found.";
                return false;
            }

            var userDirectory = ResolveUserDirectory(exePath);
            var backupDirectory = ResolveBackupDirectory(userDirectory);
            var markerPath = Path.Combine(backupDirectory, BackupMarkerName);
            if (!File.Exists(markerPath) || !Directory.Exists(backupDirectory))
                return true;

            hadBackup = true;
            var restored = 0;
            foreach (var relativePath in EnumerateBackupRelativePaths())
            {
                var source = Path.Combine(backupDirectory, relativePath);
                if (!File.Exists(source))
                    continue;

                var destination = Path.Combine(userDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                restored++;
            }

            log?.Invoke(
                restored > 0
                    ? $"Restored original Dolphin settings ({restored} files) from backup"
                    : "Dolphin settings backup exists but contained no INI files to restore");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to restore original Dolphin settings: {ex.Message}";
            return false;
        }
    }

    internal static string ResolveBackupDirectory(string userDirectory)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SMSO",
            BackupRootFolderName);
        var key = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(userDirectory.TrimEnd('\\', '/').ToLowerInvariant())))
            [..16];
        return Path.Combine(root, key);
    }

    private static IEnumerable<string> EnumerateBackupRelativePaths()
    {
        yield return Path.Combine(ConfigDirectoryName, DolphinIniName);
        yield return Path.Combine(ConfigDirectoryName, GfxIniName);
        yield return Path.Combine(GameSettingsDirectoryName, $"{GameIdentity.BsmsGameId}.ini");
        yield return Path.Combine(
            ConfigDirectoryName, GameSettingsDirectoryName, $"{GameIdentity.BsmsGameId}.ini");
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

    // Exposed for unit tests — applies the GMSE90 GameINI performance profile in-memory.
    internal static bool ApplyGameIniPerformanceProfile(List<string> lines) =>
        ApplyGameIniPerformanceKeys(lines);

    internal static bool ApplyGfxIniPerformanceProfile(List<string> lines) =>
        ApplyGfxPerformanceKeys(lines);

    private static bool EnsureIniFile(string path, Func<List<string>, bool> apply)
    {
        var lines = File.Exists(path)
            ? new List<string>(File.ReadAllLines(path))
            : new List<string>();

        var changed = apply(lines);
        if (changed)
            File.WriteAllLines(path, lines);

        return changed;
    }

    private static bool ApplyCorePerformanceKeys(List<string> lines) =>
        UpsertIniValues(lines, CoreSection, CorePerformanceKeys);

    private static bool ApplyGameIniPerformanceKeys(List<string> lines)
    {
        var changed = false;
        changed |= UpsertIniValues(lines, CoreSection, CorePerformanceKeys);
        changed |= UpsertIniValues(lines, VideoSettingsSection, VideoSettingsKeys);
        changed |= UpsertIniValues(lines, VideoEnhancementsSection, VideoEnhancementsKeys);
        changed |= UpsertIniValues(lines, VideoHacksSection, VideoHacksKeys);
        return changed;
    }

    private static bool ApplyGfxPerformanceKeys(List<string> lines)
    {
        var changed = false;
        changed |= UpsertIniValues(lines, GfxSettingsSection, VideoSettingsKeys);
        changed |= UpsertIniValues(lines, GfxEnhancementsSection, VideoEnhancementsKeys);
        changed |= UpsertIniValues(lines, GfxHacksSection, VideoHacksKeys);
        return changed;
    }

    private static bool UpsertIniValues(
        List<string> lines,
        string section,
        IReadOnlyList<(string Key, string Value)> values)
    {
        var changed = false;
        foreach (var (key, value) in values)
            changed |= UpsertIniValue(lines, section, key, value);
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

    /// <summary>Upserts a key under [section], preserving other keys/sections. Exposed for tests.</summary>
    internal static bool UpsertIniValue(List<string> lines, string section, string key, string value)
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
