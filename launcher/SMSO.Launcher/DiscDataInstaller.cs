using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SMSO.Launcher;

/// <summary>
/// Result of syncing bundled disc-data overlays (<c>assets/data/**</c> → <c>files/data/**</c>).
/// </summary>
public sealed class DiscDataSyncResult
{
    public int SyncedCount { get; init; }
    public int SkippedIdentical { get; init; }
    public bool BundledAssetsAvailable { get; init; }
}

/// <summary>
/// Installs BSMSO disc-file overlays shipped under <c>assets/data/</c> into an
/// extracted game tree (or a disc extract during ISO patch). Layout mirrors the
/// GameCube <c>files/data/</c> tree — e.g. <c>assets/data/nintendo.szs</c> →
/// <c>files/data/nintendo.szs</c>.
/// </summary>
internal static class DiscDataInstaller
{
    public const string AssetsDataFolderName = "data";
    public const string RetailBackupSuffix = ".bsmso-retail";

    /// <summary>
    /// Relative paths under <c>files/data/</c> that BSMSO ships custom replacements for.
    /// Kept explicit so incidental files under <c>assets/data</c> (e.g. an unchanged
    /// <c>scene/params.szs</c> copy) are not required for a successful install.
    /// </summary>
    public static readonly string[] BundledRelativePaths =
    {
        @"nintendo.szs",
        @"option.szs",
    };

    /// <summary>
    /// Copy bundled title/UI archives into <c>files/data/</c>. Backs up the retail
    /// file once as <c>*.bsmso-retail</c> before the first overwrite.
    /// Accepts an extracted game root or any path <see cref="ModuleInstallValidator.TryResolveGameRoot"/>
    /// can resolve. Disc images are skipped here — use Install / patch modules
    /// (<see cref="DiscImagePatcher"/>) which overlays before rebuild.
    /// </summary>
    public static DiscDataSyncResult EnsureBundledDiscDataPresent(
        string isoPathOrGameRoot,
        Action<string>? log = null)
    {
        var result = new DiscDataSyncResult();
        var trimmed = isoPathOrGameRoot?.Trim().Trim('"') ?? string.Empty;
        if (trimmed.Length == 0)
            return result;

        if (ModuleInstallValidator.ClassifyInstallTarget(trimmed) == ModuleInstallTargetKind.DiscImage)
        {
            // ISO/GCM overlays are applied during DiscImagePatcher.PatchAsync.
            return result;
        }

        if (!ModuleInstallValidator.TryResolveGameRoot(trimmed, out var gameRoot) || gameRoot == null)
            return result;

        if (!TryResolveBundledDataRoot(out var assetsDataRoot) || assetsDataRoot == null)
        {
            log?.Invoke("No bundled assets/data overlays found beside the launcher.");
            return result;
        }

        var destDataRoot = Path.Combine(gameRoot, "files", "data");
        Directory.CreateDirectory(destDataRoot);

        var synced = 0;
        var skipped = 0;
        var available = false;

        foreach (var relative in BundledRelativePaths)
        {
            var source = Path.Combine(assetsDataRoot, relative);
            if (!File.Exists(source))
                continue;

            available = true;
            var dest = Path.Combine(destDataRoot, relative);
            try
            {
                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(dest) && ModuleInstaller.FilesMatch(source, dest))
                {
                    skipped++;
                    continue;
                }

                BackupRetailOnce(dest, log);
                File.Copy(source, dest, overwrite: true);
                synced++;
                log?.Invoke(
                    $"Installed disc overlay files\\data\\{relative} " +
                    $"({new FileInfo(dest).Length:N0} bytes)");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Skipped disc overlay {relative}: {ex.Message}");
            }
        }

        return new DiscDataSyncResult
        {
            SyncedCount = synced,
            SkippedIdentical = skipped,
            BundledAssetsAvailable = available,
        };
    }

    /// <summary>
    /// Locates the bundled <c>assets/data</c> directory next to the launcher or
    /// in the repo tree when running from a debug build.
    /// </summary>
    public static bool TryResolveBundledDataRoot(out string? path)
    {
        path = null;
        foreach (var candidate in EnumerateAssetDataCandidates())
        {
            if (!Directory.Exists(candidate))
                continue;

            // Require at least one known overlay so an empty assets/data is ignored.
            foreach (var relative in BundledRelativePaths)
            {
                if (File.Exists(Path.Combine(candidate, relative)))
                {
                    path = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static void BackupRetailOnce(string destPath, Action<string>? log)
    {
        if (!File.Exists(destPath))
            return;

        var backup = destPath + RetailBackupSuffix;
        if (File.Exists(backup))
            return;

        try
        {
            File.Copy(destPath, backup, overwrite: false);
            log?.Invoke($"Backed up retail {Path.GetFileName(destPath)} → {Path.GetFileName(backup)}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not back up retail {Path.GetFileName(destPath)}: {ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateAssetDataCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return;
            try
            {
                var full = Path.GetFullPath(dir);
                if (Directory.Exists(full))
                    seen.Add(full);
            }
            catch
            {
                // ignore invalid paths
            }
        }

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Add(Path.Combine(exeDir ?? string.Empty, "assets", AssetsDataFolderName));
        Add(Path.Combine(AppContext.BaseDirectory, "assets", AssetsDataFolderName));

        var walk = exeDir;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(walk); i++)
        {
            Add(Path.Combine(walk, "assets", AssetsDataFolderName));
            walk = Path.GetDirectoryName(walk);
        }

        Add(Path.Combine(Environment.CurrentDirectory, "assets", AssetsDataFolderName));

        return seen;
    }
}
