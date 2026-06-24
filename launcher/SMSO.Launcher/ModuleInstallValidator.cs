using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SMSO.Net;

namespace SMSO.Launcher;

internal static class ModuleInstallValidator
{
    public static bool TryFindModuleFile(string isoPath, out string? modulePath)
    {
        modulePath = null;
        if (string.IsNullOrWhiteSpace(isoPath))
            return false;

        var roots = ResolveIsoRoots(isoPath.Trim().Trim('"'));
        foreach (var root in roots)
        {
            var modsDir = Path.Combine(root, "files", "Kuribo!", "Mods");
            if (!Directory.Exists(modsDir))
                continue;

            var primary = Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName);
            if (File.Exists(primary))
            {
                modulePath = primary;
                return true;
            }

            var legacy = Path.Combine(modsDir, ModuleVersionMessages.LegacyModuleFileName);
            if (File.Exists(legacy))
            {
                modulePath = legacy;
                return true;
            }
        }

        return false;
    }

    public static string? ValidateInstalledModule(string isoPath)
    {
        if (!TryFindModuleFile(isoPath, out var modulePath) || modulePath == null)
        {
            if (Directory.Exists(isoPath))
                return ModuleVersionMessages.MissingModuleFile(
                    Path.Combine(isoPath, "files", "Kuribo!", "Mods", ModuleVersionMessages.ModuleFileName));
            return null;
        }

        if (string.Equals(Path.GetFileName(modulePath), ModuleVersionMessages.LegacyModuleFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return $"Legacy {ModuleVersionMessages.LegacyModuleFileName} found — rename or replace with {ModuleVersionMessages.ModuleFileName} after updating.";
        }

        return null;
    }

    private static IEnumerable<string> ResolveIsoRoots(string isoPath)
    {
        var candidates = new List<string>();
        if (isoPath.EndsWith(@"\sys\main.dol", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.GetDirectoryName(Path.GetDirectoryName(isoPath))!);

        if (File.Exists(isoPath))
        {
            candidates.Add(Path.GetDirectoryName(isoPath)!);
            candidates.Add(Path.GetDirectoryName(Path.GetDirectoryName(isoPath))!);
        }
        else if (Directory.Exists(isoPath))
        {
            candidates.Add(isoPath);
        }

        return candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
