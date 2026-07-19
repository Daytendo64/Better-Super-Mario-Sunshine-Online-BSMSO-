using SMSO.Launcher;
using SMSO.Net;
using Xunit;

namespace SMSO.Tests;

public sealed class DolphinConfigServiceTests
{
    [Fact]
    public void UpsertIniValue_PreservesUnknownSectionsAndUpdatesTarget()
    {
        var lines = new List<string>
        {
            "[Core]",
            "EnableCheats = True",
            "CPUThread = False",
            "",
            "[Gecko]",
            "$SomeCode",
            "04000000 60000000",
            "",
            "[Controls]",
            "PadType0 = 6",
        };

        Assert.True(DolphinConfigService.UpsertIniValue(lines, "Core", "CPUThread", "True"));
        Assert.True(DolphinConfigService.UpsertIniValue(lines, "Core", "DSPHLE", "True"));
        Assert.False(DolphinConfigService.UpsertIniValue(lines, "Core", "CPUThread", "True"));

        var text = string.Join('\n', lines);
        Assert.Contains("EnableCheats = True", text);
        Assert.Contains("CPUThread = True", text);
        Assert.Contains("DSPHLE = True", text);
        Assert.Contains("[Gecko]", text);
        Assert.Contains("$SomeCode", text);
        Assert.Contains("04000000 60000000", text);
        Assert.Contains("[Controls]", text);
        Assert.Contains("PadType0 = 6", text);
        Assert.DoesNotContain("CPUThread = False", text);
    }

    [Fact]
    public void ApplyGameIniPerformanceProfile_WritesExpectedSections()
    {
        var lines = new List<string>
        {
            "[Core]",
            "EnableCheats = True",
            "",
            "[Gecko]",
            "$KeepMe",
        };

        Assert.True(DolphinConfigService.ApplyGameIniPerformanceProfile(lines));
        Assert.False(DolphinConfigService.ApplyGameIniPerformanceProfile(lines));

        var text = string.Join('\n', lines);
        Assert.Contains("CPUThread = True", text);
        Assert.Contains("DSPHLE = True", text);
        Assert.Contains("FastDiscSpeed = False", text);
        Assert.Contains("OverclockEnable = True", text);
        Assert.Contains("Overclock = 2.0", text);
        Assert.Contains($"RAMOverrideEnable = True", text);
        Assert.Contains($"MEM1Size = 0x03000000", text);
        Assert.Contains($"MEM2Size = 0x04000000", text);
        Assert.Contains("[Video_Settings]", text);
        Assert.Contains("InternalResolution = 1", text);
        Assert.Contains("ShaderCompilationMode = 2", text);
        Assert.Contains("VSync = False", text);
        Assert.Contains("[Video_Enhancements]", text);
        Assert.Contains("DisableCopyFilter = True", text);
        Assert.Contains("[Video_Hacks]", text);
        Assert.Contains("EFBToTextureEnable = False", text);
        Assert.Contains("ImmediateXFBEnable = True", text);
        Assert.Contains("EnableCheats = True", text);
        Assert.Contains("[Gecko]", text);
        Assert.Contains("$KeepMe", text);
    }

    [Fact]
    public void ApplyGfxIniPerformanceProfile_UsesGfxSectionNames()
    {
        var lines = new List<string>();
        Assert.True(DolphinConfigService.ApplyGfxIniPerformanceProfile(lines));

        var text = string.Join('\n', lines);
        Assert.Contains("[Settings]", text);
        Assert.Contains("InternalResolution = 1", text);
        Assert.Contains("[Enhancements]", text);
        Assert.Contains("[Hacks]", text);
        Assert.DoesNotContain("[Video_Settings]", text);
    }

    [Fact]
    public void EnsurePerformanceStabilityConfig_WritesGameAndGlobalInis()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-dolphin-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Portable Dolphin layout: User next to dolphin.exe
            File.WriteAllText(Path.Combine(root, "portable.txt"), string.Empty);
            var exePath = Path.Combine(root, "Dolphin.exe");
            File.WriteAllText(exePath, string.Empty);

            var userDir = Path.Combine(root, "User");
            var configDir = Path.Combine(userDir, "Config");
            var gameSettingsDir = Path.Combine(userDir, "GameSettings");
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(gameSettingsDir);

            // Pre-seed unrelated content that must survive.
            File.WriteAllText(
                Path.Combine(gameSettingsDir, $"{GameIdentity.BsmsGameId}.ini"),
                "[Core]\nEnableCheats = True\n\n[Gecko]\n$Code\n04000000 60000000\n");
            File.WriteAllText(
                Path.Combine(configDir, "Dolphin.ini"),
                "[Interface]\nThemeName = Clean\n\n[Core]\nEnableCheats = True\n");
            File.WriteAllText(
                Path.Combine(configDir, "GFX.ini"),
                "[Settings]\nInternalResolution = 8\n");

            var logs = new List<string>();
            Assert.True(DolphinConfigService.EnsurePerformanceStabilityConfig(
                exePath, logs.Add, out var error), error);
            Assert.Contains(logs, l => l.Contains("Applied BSMSO Dolphin performance profile", StringComparison.Ordinal));

            var gameIni = File.ReadAllText(Path.Combine(gameSettingsDir, $"{GameIdentity.BsmsGameId}.ini"));
            Assert.Contains("InternalResolution = 1", gameIni);
            Assert.Contains("CPUThread = True", gameIni);
            Assert.Contains("OverclockEnable = True", gameIni);
            Assert.Contains("Overclock = 2.0", gameIni);
            Assert.Contains("EnableCheats = True", gameIni);
            Assert.Contains("[Gecko]", gameIni);
            Assert.Contains("$Code", gameIni);

            // Config\GameSettings is also written (secondary layout).
            var configGameIni = File.ReadAllText(Path.Combine(
                configDir, "GameSettings", $"{GameIdentity.BsmsGameId}.ini"));
            Assert.Contains("InternalResolution = 1", configGameIni);

            var dolphinIni = File.ReadAllText(Path.Combine(configDir, "Dolphin.ini"));
            Assert.Contains("ThemeName = Clean", dolphinIni);
            Assert.Contains("CPUThread = True", dolphinIni);
            Assert.Contains("OverclockEnable = True", dolphinIni);
            Assert.Contains("Overclock = 2.0", dolphinIni);
            Assert.Contains("MEM1Size = 0x03000000", dolphinIni);

            var gfxIni = File.ReadAllText(Path.Combine(configDir, "GFX.ini"));
            Assert.Contains("InternalResolution = 1", gfxIni);
            Assert.Contains("[Hacks]", gfxIni);
            Assert.Contains("ImmediateXFBEnable = True", gfxIni);

            // Idempotent
            logs.Clear();
            Assert.True(DolphinConfigService.EnsurePerformanceStabilityConfig(
                exePath, logs.Add, out error), error);
            Assert.Contains(logs, l => l.Contains("already applied", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ApplyLaunchDolphinSettings_BackupAndRestorePreservesOriginals()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-dolphin-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "portable.txt"), string.Empty);
            var exePath = Path.Combine(root, "Dolphin.exe");
            File.WriteAllText(exePath, string.Empty);

            var userDir = Path.Combine(root, "User");
            var configDir = Path.Combine(userDir, "Config");
            var gameSettingsDir = Path.Combine(userDir, "GameSettings");
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(gameSettingsDir);

            // Seed non-BSMSO overclock so restore-then-force can prove 200% is re-applied.
            File.WriteAllText(
                Path.Combine(configDir, "Dolphin.ini"),
                "[Core]\nOverclockEnable = False\nOverclock = 1.0\n");
            File.WriteAllText(
                Path.Combine(configDir, "GFX.ini"),
                "[Settings]\nInternalResolution = 6\n");
            File.WriteAllText(
                Path.Combine(gameSettingsDir, $"{GameIdentity.BsmsGameId}.ini"),
                "[Core]\nFastDiscSpeed = False\n\n[Gecko]\n$Keep\n");

            var logs = new List<string>();
            Assert.True(DolphinConfigService.ApplyLaunchDolphinSettings(
                exePath, applyRecommended: true, logs.Add, out var error), error);
            Assert.Contains(logs, l => l.Contains("Backed up original", StringComparison.Ordinal));
            Assert.Contains("InternalResolution = 1", File.ReadAllText(Path.Combine(configDir, "GFX.ini")));
            Assert.Contains("OverclockEnable = True", File.ReadAllText(Path.Combine(configDir, "Dolphin.ini")));
            Assert.Contains("Overclock = 2.0", File.ReadAllText(Path.Combine(configDir, "Dolphin.ini")));
            // Recommended path must also force 200% into GMSE90 GameSettings.
            var recommendedGameIni = File.ReadAllText(
                Path.Combine(gameSettingsDir, $"{GameIdentity.BsmsGameId}.ini"));
            Assert.Contains("OverclockEnable = True", recommendedGameIni);
            Assert.Contains("Overclock = 2.0", recommendedGameIni);

            // User raises IR in Dolphin; next Launch with recommended on must keep it.
            File.WriteAllText(
                Path.Combine(configDir, "GFX.ini"),
                "[Settings]\nInternalResolution = 3\nShaderCompilationMode = 2\n");

            logs.Clear();
            Assert.True(DolphinConfigService.ApplyLaunchDolphinSettings(
                exePath, applyRecommended: true, logs.Add, out error), error);
            Assert.Contains(logs, l => l.Contains("Keeping your current Dolphin settings", StringComparison.Ordinal));
            Assert.Contains("InternalResolution = 3", File.ReadAllText(Path.Combine(configDir, "GFX.ini")));
            Assert.DoesNotContain("InternalResolution = 1", File.ReadAllText(Path.Combine(configDir, "GFX.ini")));
            // Required Core keys still enforced.
            Assert.Contains("Overclock = 2.0", File.ReadAllText(Path.Combine(configDir, "Dolphin.ini")));

            logs.Clear();
            Assert.True(DolphinConfigService.ApplyLaunchDolphinSettings(
                exePath, applyRecommended: false, logs.Add, out error), error);
            Assert.Contains(logs, l => l.Contains("Restored original", StringComparison.Ordinal));

            var gfx = File.ReadAllText(Path.Combine(configDir, "GFX.ini"));
            Assert.Contains("InternalResolution = 6", gfx);

            var dolphin = File.ReadAllText(Path.Combine(configDir, "Dolphin.ini"));
            // CPU clock / disc / RAM are re-applied after restore (always-forced).
            Assert.Contains("OverclockEnable = True", dolphin);
            Assert.Contains("Overclock = 2.0", dolphin);
            Assert.Contains("RAMOverrideEnable = True", dolphin);
            Assert.Contains("MEM1Size = 0x03000000", dolphin);
            Assert.Contains("FastDiscSpeed = False", dolphin);

            var gameIni = File.ReadAllText(Path.Combine(gameSettingsDir, $"{GameIdentity.BsmsGameId}.ini"));
            Assert.Contains("FastDiscSpeed = False", gameIni);
            Assert.Contains("OverclockEnable = True", gameIni);
            Assert.Contains("Overclock = 2.0", gameIni);
            Assert.Contains("$Keep", gameIni);
            Assert.Contains("RAMOverrideEnable = True", gameIni);

            // While recommended is off, Dolphin edits must survive Launch and update the
            // pre-profile backup (not get overwritten by the old one-time snapshot).
            File.WriteAllText(
                Path.Combine(configDir, "GFX.ini"),
                "[Settings]\nInternalResolution = 4\n");
            logs.Clear();
            Assert.True(DolphinConfigService.ApplyLaunchDolphinSettings(
                exePath, applyRecommended: false, logs.Add, out error), error);
            Assert.Contains(logs, l => l.Contains("keeping your current Dolphin settings", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logs, l => l.Contains("Updated pre-profile", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, l => l.Contains("Restored original", StringComparison.Ordinal));
            Assert.Contains("InternalResolution = 4", File.ReadAllText(Path.Combine(configDir, "GFX.ini")));

            // Toggle off cleared the once-applied marker — ON again re-applies full profile.
            logs.Clear();
            Assert.True(DolphinConfigService.ApplyLaunchDolphinSettings(
                exePath, applyRecommended: true, logs.Add, out error), error);
            Assert.Contains("InternalResolution = 1", File.ReadAllText(Path.Combine(configDir, "GFX.ini")));

            // OFF again restores the refreshed pre-profile (IR=4), not the ancient first backup.
            logs.Clear();
            Assert.True(DolphinConfigService.ApplyLaunchDolphinSettings(
                exePath, applyRecommended: false, logs.Add, out error), error);
            Assert.Contains(logs, l => l.Contains("Restored original", StringComparison.Ordinal));
            Assert.Contains("InternalResolution = 4", File.ReadAllText(Path.Combine(configDir, "GFX.ini")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
            try
            {
                var backupDir = DolphinConfigService.ResolveBackupDirectory(Path.Combine(root, "User"));
                if (Directory.Exists(backupDir))
                    Directory.Delete(backupDir, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void EnsureMultiplayerMemoryConfig_ForcesEmulateDiscSpeedCpuClockAndRam_NotGraphics()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-dolphin-ram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "portable.txt"), string.Empty);
            var exePath = Path.Combine(root, "Dolphin.exe");
            File.WriteAllText(exePath, string.Empty);

            var userDir = Path.Combine(root, "User");
            var configDir = Path.Combine(userDir, "Config");
            var gameSettingsDir = Path.Combine(userDir, "GameSettings");
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(gameSettingsDir);
            File.WriteAllText(
                Path.Combine(configDir, "GFX.ini"),
                "[Settings]\nInternalResolution = 6\n");
            File.WriteAllText(
                Path.Combine(configDir, "Dolphin.ini"),
                "[Core]\nOverclockEnable = False\nOverclock = 0.7\n");
            File.WriteAllText(
                Path.Combine(gameSettingsDir, $"{GameIdentity.BsmsGameId}.ini"),
                "[Core]\nFastDiscSpeed = True\nOverclockEnable = False\nOverclock = 1.0\n\n[Gecko]\n$Keep\n");

            Assert.True(DolphinConfigService.EnsureMultiplayerMemoryConfig(
                exePath, _ => { }, out var error), error);

            var gfx = File.ReadAllText(Path.Combine(configDir, "GFX.ini"));
            Assert.Contains("InternalResolution = 6", gfx);
            Assert.DoesNotContain("ShaderCompilationMode", gfx);

            var dolphin = File.ReadAllText(Path.Combine(configDir, "Dolphin.ini"));
            Assert.Contains("MEM1Size = 0x03000000", dolphin);
            Assert.Contains("FastDiscSpeed = False", dolphin);
            Assert.Contains("OverclockEnable = True", dolphin);
            Assert.Contains("Overclock = 2.0", dolphin);
            Assert.DoesNotContain("CPUThread = True", dolphin);

            var gameIni = File.ReadAllText(Path.Combine(gameSettingsDir, $"{GameIdentity.BsmsGameId}.ini"));
            Assert.Contains("FastDiscSpeed = False", gameIni);
            Assert.Contains("OverclockEnable = True", gameIni);
            Assert.Contains("Overclock = 2.0", gameIni);
            Assert.Contains("$Keep", gameIni);

            // Secondary GameSettings layout is synced too.
            var configGameIni = File.ReadAllText(Path.Combine(
                configDir, "GameSettings", $"{GameIdentity.BsmsGameId}.ini"));
            Assert.Contains("FastDiscSpeed = False", configGameIni);
            Assert.Contains("Overclock = 2.0", configGameIni);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ResolveUserDirectory_IgnoresBareUserFolderWithoutPortableMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-dolphin-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exePath = Path.Combine(root, "Dolphin.exe");
            File.WriteAllText(exePath, string.Empty);
            Directory.CreateDirectory(Path.Combine(root, "User"));
            // No portable.txt — must NOT treat as portable (matches Dolphin behavior).

            var resolved = DolphinConfigService.ResolveUserDirectory(exePath);
            Assert.False(
                string.Equals(resolved, Path.Combine(root, "User"), StringComparison.OrdinalIgnoreCase),
                $"Bare User/ folder must not redirect; got {resolved}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ResolveUserDirectory_UsesLocalUserWhenPortableTxtPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-dolphin-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var exePath = Path.Combine(root, "Dolphin.exe");
            File.WriteAllText(exePath, string.Empty);
            File.WriteAllText(Path.Combine(root, "portable.txt"), string.Empty);

            var resolved = DolphinConfigService.ResolveUserDirectory(exePath);
            Assert.Equal(Path.Combine(root, "User"), resolved);
            Assert.True(Directory.Exists(resolved));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
