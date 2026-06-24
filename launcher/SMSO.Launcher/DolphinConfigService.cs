using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace SMSO.Launcher;

internal static class DolphinConfigService
{
    private const string DolphinRegistryKey = @"Software\Dolphin Emulator";
    private const string ConfigDirectoryName = "Config";
    private const string GameSettingsDirectoryName = "GameSettings";
    private const string DolphinIniName = "Dolphin.ini";
    private const string SunshineGameSettingsIniName = "GMS.ini";
    private const string CoreSection = "Core";
    private const string RamOverrideEnableKey = "RAMOverrideEnable";
    private const string Mem1SizeKey = "MEM1Size";
    private const string Mem2SizeKey = "MEM2Size";
    private const string TargetMem1Size = "0x03000000"; // 48 MiB, conservative GDEV-size MEM1.
    private const string TargetMem2Size = "0x04000000";

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
            var gameSettingsDirectory = Path.Combine(configDirectory, GameSettingsDirectoryName);
            Directory.CreateDirectory(gameSettingsDirectory);
            var sunshineIni = Path.Combine(gameSettingsDirectory, SunshineGameSettingsIniName);

            var dolphinChanged = EnsureRamOverrideIni(dolphinIni);
            var gameChanged = EnsureRamOverrideIni(sunshineIni);
            var verb = dolphinChanged || gameChanged ? "Configured" : "Dolphin RAM override already configured for";
            log?.Invoke($"{verb} BSMSO: MEM1={TargetMem1Size}, MEM2={TargetMem2Size} ({dolphinIni}; {sunshineIni})");

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
