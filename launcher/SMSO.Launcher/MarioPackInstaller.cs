using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using GCNTools;
using SMSO.Net.MarioPack;

namespace SMSO.Launcher;

internal readonly record struct MarioPackInstallResult(
    bool Succeeded,
    bool Deferred,
    int InstalledCount,
    string Message);

/// <summary>
/// Installs merged character packs into the extracted game tree under
/// <c>files/data/bsmso_models/&lt;id&gt;.arc</c>, and optionally patches local
/// <c>files/mario/mario.szs</c> for single-player convenience.
/// Also supports patching packs into .iso/.gcm disc images (extract → write → rebuild).
/// </summary>
internal static class MarioPackInstaller
{
    public const string ModelsFolderRelative = @"files\data\bsmso_models";
    public const string RetailMarioRelative = @"files\mario\mario.szs";
    public const string RetailMarioBackupRelative = @"files\mario\mario.szs.bsmso-retail";
    public const string RetailDataMarioArcRelative = @"files\data\mario.arc";
    public const string RuntimePreloadIndexFileName = "preload.idx";
    private const string DiscPackManifestSuffix = ".bsmso-packs.json";

    private static readonly object DiscPatchLock = new();
    private static readonly ConcurrentDictionary<string, object> InstalledPackLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<byte[], PackValidationResult> PackValidationCache = new();

    private sealed class PackValidationResult
    {
        public required bool Safe { get; init; }
        public required string Reason { get; init; }
    }

    public static bool TryResolveRetailMarioBytes(string isoPath, out byte[] bytes, out string? error)
    {
        bytes = Array.Empty<byte>();
        error = null;
        if (!ModuleInstallValidator.TryResolveGameRoot(isoPath, out var gameRoot) || gameRoot == null)
        {
            error = "Set Game ISO / extracted folder first so retail Mario assets can be located.";
            return false;
        }

        var backup = Path.Combine(gameRoot, RetailMarioBackupRelative);
        var retailSzs = Path.Combine(gameRoot, RetailMarioRelative);
        var dataSzs = Path.Combine(gameRoot, @"files\data\mario.szs");
        var dataArc = Path.Combine(gameRoot, RetailDataMarioArcRelative);

        foreach (var candidate in new[] { backup, retailSzs, dataSzs, dataArc })
        {
            if (!File.Exists(candidate))
                continue;
            bytes = File.ReadAllBytes(candidate);
            return true;
        }

        error = "Could not find retail mario.szs / mario.arc under the game folder.";
        return false;
    }

    public static string GetModelsDirectory(string gameRoot) =>
        Path.Combine(gameRoot, ModelsFolderRelative);

    public static string GetInstalledPackPath(string gameRoot, string modelId) =>
        Path.Combine(GetModelsDirectory(gameRoot), CharacterPack.NormalizeModelId(modelId) + ModelLibrary.PackExtension);

    internal static string GetRuntimePreloadIndexPath(string gameRoot) =>
        Path.Combine(GetModelsDirectory(gameRoot), RuntimePreloadIndexFileName);

    /// <summary>
    /// Removes the legacy all-installed-model prewarm catalog. Current modules
    /// prepare only roster/selection identities; leaving this catalog behind
    /// makes older modules flood the game heap and stall joins.
    /// </summary>
    internal static void RemoveLegacyRuntimePreloadIndex(string gameRoot)
    {
        var modelsDirectory = GetModelsDirectory(gameRoot);
        Directory.CreateDirectory(modelsDirectory);
        var destination = GetRuntimePreloadIndexPath(gameRoot);
        try
        {
            if (File.Exists(destination))
                File.Delete(destination);
        }
        catch (IOException)
        {
            // Dolphin may have an extracted-tree file open. The current module
            // ignores the legacy catalog, so cleanup can safely retry later.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort compatibility cleanup.
        }
    }

    public static void EnsureRetailBackup(string gameRoot)
    {
        var retail = Path.Combine(gameRoot, RetailMarioRelative);
        var backup = Path.Combine(gameRoot, RetailMarioBackupRelative);
        if (File.Exists(retail) && !File.Exists(backup))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(retail, backup, overwrite: false);
        }
    }

    /// <summary>
    /// SMSCoop-style runtime loads <c>/data/mario.arc</c>. Ensure an uncompressed
    /// RARC exists at <c>files/data/mario.arc</c> from retail mario.szs when missing.
    /// </summary>
    public static void EnsureRetailDataMarioArc(string gameRoot, Action<string>? log = null)
    {
        var dataArc = Path.Combine(gameRoot, RetailDataMarioArcRelative);
        if (File.Exists(dataArc))
            return;

        byte[] retailBytes = Array.Empty<byte>();
        foreach (var candidate in new[]
                 {
                     Path.Combine(gameRoot, RetailMarioBackupRelative),
                     Path.Combine(gameRoot, RetailMarioRelative),
                     Path.Combine(gameRoot, @"files\data\mario.szs"),
                 })
        {
            if (!File.Exists(candidate))
                continue;
            retailBytes = File.ReadAllBytes(candidate);
            break;
        }

        if (retailBytes.Length == 0)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(dataArc)!);
        var rarc = CharacterPack.OpenToRarcBytes(retailBytes);
        File.WriteAllBytes(dataArc, rarc);
        log?.Invoke($"Created {RetailDataMarioArcRelative} for SMSLoadArchive remount.");
    }

    public static MarioPackInstallResult InstallPackToGame(
        string isoPath,
        string modelId,
        Action<string>? log = null)
    {
        var trimmed = isoPath.Trim().Trim('"');
        var kind = ModuleInstallValidator.ClassifyInstallTarget(trimmed);
        if (kind == ModuleInstallTargetKind.DiscImage)
        {
            var id = CharacterPack.NormalizeModelId(modelId);
            if (id.Length == 0)
                return new MarioPackInstallResult(true, false, 0, "Retail Mario selected.");
            if (!ModelLibrary.TryGetPackBytes(id, out _))
                return new MarioPackInstallResult(
                    false, false, 0, $"Model pack '{id}' is not in the AppData library.");
            return EnsurePacksOnDiscImageWithResult(trimmed, new[] { id }, log);
        }

        if (!ModuleInstallValidator.TryResolveGameRoot(trimmed, out var gameRoot) || gameRoot == null)
            return new MarioPackInstallResult(
                false, false, 0, "Game folder is not an extracted SMS tree or .iso/.gcm.");

        EnsureRetailBackup(gameRoot);
        EnsureRetailDataMarioArc(gameRoot, log);
        Directory.CreateDirectory(GetModelsDirectory(gameRoot));

        var packId = CharacterPack.NormalizeModelId(modelId);
        if (packId.Length == 0)
        {
            RestoreLocalMarioSzs(gameRoot, log);
            return new MarioPackInstallResult(true, false, 0, "Retail Mario restored.");
        }

        if (!ModelLibrary.TryGetPackBytes(packId, out var packBytes))
            return new MarioPackInstallResult(
                false, false, 0, $"Model pack '{packId}' is not in the AppData library.");

        var dest = GetInstalledPackPath(gameRoot, packId);
        if (EnsureInstalledPackFile(dest, ModelLibrary.GetPackPath(packId), packBytes))
            log?.Invoke($"Installed model pack {packId} → {dest}");
        RemoveLegacyRuntimePreloadIndex(gameRoot);

        // Local convenience: also merge into files/mario/mario.szs so solo play
        // without remount still shows the selected skin. Multiplayer remounts
        // per-slot archives from files/data/bsmso_models/.
        PatchLocalMarioSzs(gameRoot, packBytes, log);
        return new MarioPackInstallResult(true, false, 1, $"Installed model pack {packId}.");
    }

    public static void EnsurePackPresent(string isoPath, string modelId, Action<string>? log = null)
    {
        var id = CharacterPack.NormalizeModelId(modelId);
        if (id.Length == 0)
            return;

        var trimmed = isoPath.Trim().Trim('"');
        var kind = ModuleInstallValidator.ClassifyInstallTarget(trimmed);
        if (kind == ModuleInstallTargetKind.DiscImage)
        {
            EnsurePacksOnDiscImage(trimmed, new[] { id }, log);
            return;
        }

        if (!ModuleInstallValidator.TryResolveGameRoot(trimmed, out var gameRoot) || gameRoot == null)
            return;

        EnsureRetailDataMarioArc(gameRoot, log);

        var dest = GetInstalledPackPath(gameRoot, id);
        if (!ModelLibrary.TryGetPackBytes(id, out var packBytes) || packBytes.Length == 0)
            return;

        if (!TryValidatePackCached(packBytes, out var unsafeReason))
        {
            log?.Invoke($"Model pack {id} rejected (unsafe for remotes): {unsafeReason}");
            // Remove a previously-installed crashy pack so SMSLoadArchive misses
            // and the module soft-falls back to retail instead of initValues faulting.
            if (File.Exists(dest))
            {
                try
                {
                    File.Delete(dest);
                    log?.Invoke($"Removed unsafe installed pack {id} from game folder.");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Could not remove unsafe pack {id}: {ex.Message}");
                }
            }

            RemoveLegacyRuntimePreloadIndex(gameRoot);
            return;
        }

        Directory.CreateDirectory(GetModelsDirectory(gameRoot));
        if (EnsureInstalledPackFile(dest, ModelLibrary.GetPackPath(id), packBytes))
            log?.Invoke($"Auto-copied model pack {id} into game folder.");
        RemoveLegacyRuntimePreloadIndex(gameRoot);
    }

    /// <summary>
    /// Copy every AppData library pack into <c>files/data/bsmso_models/</c> so
    /// remount can find them without selecting each model first.
    /// Works for extracted trees and .iso/.gcm disc images.
    /// </summary>
    public static int EnsureAllLibraryPacksPresent(string isoPath, Action<string>? log = null)
    {
        var trimmed = isoPath.Trim().Trim('"');
        var ids = ModelLibrary.ListEntries(includeRetail: false)
            .Select(e => CharacterPack.NormalizeModelId(e.Id))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var kind = ModuleInstallValidator.ClassifyInstallTarget(trimmed);
        if (kind == ModuleInstallTargetKind.DiscImage)
            return EnsurePacksOnDiscImage(trimmed, ids, log);

        if (!ModuleInstallValidator.TryResolveGameRoot(trimmed, out var gameRoot) || gameRoot == null)
            return 0;

        return InstallPacksIntoGameRoot(gameRoot, ids, patchLocalSzs: false, log);
    }

    /// <summary>
    /// Writes requested library packs into an already-extracted game root
    /// (used by disc patchers after extract, before rebuild).
    /// </summary>
    public static int InstallPacksIntoGameRoot(
        string gameRoot,
        IEnumerable<string> modelIds,
        bool patchLocalSzs,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return 0;

        EnsureRetailDataMarioArc(gameRoot, log);
        Directory.CreateDirectory(GetModelsDirectory(gameRoot));

        var installed = 0;
        foreach (var rawId in modelIds)
        {
            var id = CharacterPack.NormalizeModelId(rawId);
            if (id.Length == 0)
                continue;

            var dest = GetInstalledPackPath(gameRoot, id);
            if (!ModelLibrary.TryGetPackBytes(id, out var packBytes) || packBytes.Length == 0)
                continue;

            if (!TryValidatePackCached(packBytes, out var unsafeReason))
            {
                log?.Invoke($"Skipped unsafe model pack {id}: {unsafeReason}");
                if (File.Exists(dest))
                {
                    try { File.Delete(dest); }
                    catch { /* best-effort quarantine */ }
                }

                continue;
            }

            if (!EnsureInstalledPackFile(dest, ModelLibrary.GetPackPath(id), packBytes))
                continue;
            installed++;
            log?.Invoke($"Installed model pack {id} → {dest}");

            if (patchLocalSzs)
                PatchLocalMarioSzs(gameRoot, packBytes, log);
        }

        if (installed > 0)
            log?.Invoke($"Installed {installed} custom model pack(s) into game folder.");
        RemoveLegacyRuntimePreloadIndex(gameRoot);
        return installed;
    }

    private static bool TryValidatePackCached(byte[] packBytes, out string reason)
    {
        var result = PackValidationCache.GetValue(packBytes, bytes =>
        {
            var safe = CharacterPack.TryValidatePackForInit(bytes, out var validationReason);
            return new PackValidationResult
            {
                Safe = safe,
                Reason = validationReason,
            };
        });
        reason = result.Reason;
        return result.Safe;
    }

    /// <summary>
    /// Copy a validated pack with an atomic same-directory rename. The source
    /// timestamp is propagated, turning subsequent launch/session syncs into a
    /// metadata-only check. Older installs get one streaming comparison, without
    /// allocating another destination-sized byte array.
    /// </summary>
    private static bool EnsureInstalledPackFile(string destinationPath, string sourcePath, byte[] bytes)
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        var gate = InstalledPackLocks.GetOrAdd(fullDestination, static _ => new object());
        lock (gate)
        {
            FileInfo? sourceInfo = null;
            try
            {
                sourceInfo = new FileInfo(sourcePath);
                if (!sourceInfo.Exists)
                    sourceInfo = null;
            }
            catch
            {
                sourceInfo = null;
            }

            if (File.Exists(fullDestination))
            {
                try
                {
                    var destinationInfo = new FileInfo(fullDestination);
                    if (destinationInfo.Length == bytes.LongLength &&
                        sourceInfo != null &&
                        sourceInfo.Length == destinationInfo.Length &&
                        sourceInfo.LastWriteTimeUtc == destinationInfo.LastWriteTimeUtc)
                    {
                        return false;
                    }

                    if (destinationInfo.Length == bytes.LongLength &&
                        FileBytesEqual(fullDestination, bytes))
                    {
                        if (sourceInfo != null)
                            File.SetLastWriteTimeUtc(fullDestination, sourceInfo.LastWriteTimeUtc);
                        return false;
                    }
                }
                catch
                {
                    // Fall through to atomic replacement. The old complete file
                    // remains available until the final rename succeeds.
                }
            }

            var directory = Path.GetDirectoryName(fullDestination)
                            ?? throw new InvalidOperationException("Model destination has no directory.");
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(
                directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           bufferSize: 128 * 1024, FileOptions.SequentialScan))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (sourceInfo != null)
                    File.SetLastWriteTimeUtc(tempPath, sourceInfo.LastWriteTimeUtc);
                File.Move(tempPath, fullDestination, overwrite: true);
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    }

    private static bool FileBytesEqual(string path, ReadOnlySpan<byte> expected)
    {
        const int bufferSize = 128 * 1024;
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        if (stream.Length != expected.Length)
            return false;

        var buffer = new byte[bufferSize];
        var offset = 0;
        while (offset < expected.Length)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
            if (read <= 0)
                return false;
            if (!buffer.AsSpan(0, read).SequenceEqual(expected.Slice(offset, read)))
                return false;
            offset += read;
        }

        return stream.ReadByte() == -1;
    }

    /// <summary>
    /// Ensures the given pack ids exist inside a .iso/.gcm by extract → copy → rebuild.
    /// Skips work when a sidecar manifest already lists every requested id.
    /// No-ops safely when Dolphin has the disc open (file locked).
    /// </summary>
    public static int EnsurePacksOnDiscImage(
        string discPath,
        IReadOnlyList<string> modelIds,
        Action<string>? log = null) =>
        EnsurePacksOnDiscImageWithResult(discPath, modelIds, log).InstalledCount;

    private static MarioPackInstallResult EnsurePacksOnDiscImageWithResult(
        string discPath,
        IReadOnlyList<string> modelIds,
        Action<string>? log = null)
    {
        if (!ModuleInstallValidator.IsPatchableDiscImage(discPath))
            return new MarioPackInstallResult(false, false, 0, "Disc image is not patchable.");

        var wanted = modelIds
            .Select(CharacterPack.NormalizeModelId)
            .Where(id => id.Length > 0)
            .Where(id => ModelLibrary.TryGetPackBytes(id, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (wanted.Length == 0)
            return new MarioPackInstallResult(true, false, 0, "No model packs need installation.");

        var manifestPath = discPath + DiscPackManifestSuffix;
        var known = LoadDiscPackManifest(manifestPath);
        var missing = wanted.Where(id => !known.Contains(id)).ToArray();
        if (missing.Length == 0)
            return new MarioPackInstallResult(true, false, 0, "Model pack is already installed.");

        if (!Monitor.TryEnter(DiscPatchLock, TimeSpan.FromSeconds(1)))
        {
            const string message = "Model pack disc patch already in progress — retry the selection when it finishes.";
            log?.Invoke(message);
            return new MarioPackInstallResult(false, true, 0, message);
        }

        try
        {
            // Re-check under lock in case another caller finished.
            known = LoadDiscPackManifest(manifestPath);
            missing = wanted.Where(id => !known.Contains(id)).ToArray();
            if (missing.Length == 0)
                return new MarioPackInstallResult(true, false, 0, "Model pack is already installed.");

            var installed = PatchPacksIntoDiscImage(
                discPath, missing, manifestPath, known, log);
            if (installed > 0)
            {
                return new MarioPackInstallResult(
                    true, false, installed, $"Installed {installed} model pack(s) into the disc image.");
            }

            // PatchPacksIntoDiscImage also records packs that were already present in the
            // extracted image. Treat that verified state as success even though no copy occurred.
            var refreshed = LoadDiscPackManifest(manifestPath);
            if (wanted.All(refreshed.Contains))
                return new MarioPackInstallResult(true, false, 0, "Model pack is already installed.");

            return new MarioPackInstallResult(
                false, false, 0, "Model pack could not be installed into the disc image.");
        }
        finally
        {
            Monitor.Exit(DiscPatchLock);
        }
    }

    /// <summary>
    /// Records that the current AppData library packs are present on a disc image
    /// (call after a successful module/disc rebuild that embedded them).
    /// </summary>
    public static void RecordAllLibraryPacksOnDisc(string discPath)
    {
        if (!ModuleInstallValidator.IsPatchableDiscImage(discPath))
            return;

        var manifestPath = discPath + DiscPackManifestSuffix;
        var known = LoadDiscPackManifest(manifestPath);
        foreach (var entry in ModelLibrary.ListEntries(includeRetail: false))
        {
            var id = CharacterPack.NormalizeModelId(entry.Id);
            if (id.Length > 0)
                known.Add(id);
        }
        SaveDiscPackManifest(manifestPath, known);
    }

    private static int PatchPacksIntoDiscImage(
        string discPath,
        string[] missingIds,
        string manifestPath,
        HashSet<string> known,
        Action<string>? log)
    {
        var fullDiscPath = Path.GetFullPath(discPath);
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "smso-pack-patch-" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(tempRoot, "extract");
        var rebuiltPath = Path.Combine(tempRoot, "patched" + Path.GetExtension(fullDiscPath));

        try
        {
            // Probe writability before the expensive extract.
            try
            {
                using var probe = new FileStream(
                    fullDiscPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                log?.Invoke(
                    "Cannot patch model packs into the disc image while it is in use " +
                    "(close Dolphin, then Launch again or Install / patch modules).");
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                log?.Invoke("Cannot patch model packs into the disc image (access denied).");
                return 0;
            }

            Directory.CreateDirectory(extractDir);
            log?.Invoke($"Patching {missingIds.Length} model pack(s) into disc image…");

            using (var stream = new FileStream(fullDiscPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var image = new DiscImage(stream))
            {
                image.ExtractToDirectory(extractDir, ExtractionType.ALL);
            }

            if (!ModuleInstallValidator.IsValidExtractedGameRoot(extractDir))
            {
                log?.Invoke("Disc extract did not produce a valid GameCube tree — model packs not installed.");
                return 0;
            }

            var installed = InstallPacksIntoGameRoot(extractDir, missingIds, patchLocalSzs: false, log);
            if (installed == 0)
            {
                // Manifest was stale or packs vanished from library; still refresh known set.
                foreach (var id in missingIds)
                {
                    var dest = GetInstalledPackPath(extractDir, id);
                    if (File.Exists(dest))
                        known.Add(id);
                }
                SaveDiscPackManifest(manifestPath, known);
                return 0;
            }

            var backupPath = CreateDiscBackup(fullDiscPath, log);
            log?.Invoke($"Disc backup: {backupPath}");

            DiscImage.CreateFile(extractDir, rebuiltPath);
            if (!File.Exists(rebuiltPath) || new FileInfo(rebuiltPath).Length < 1024)
            {
                log?.Invoke("Disc rebuild failed after model pack install — original image left unchanged.");
                return 0;
            }

            ReplaceDiscFileAtomically(rebuiltPath, fullDiscPath, log);

            foreach (var id in missingIds)
                known.Add(id);
            SaveDiscPackManifest(manifestPath, known);

            log?.Invoke($"Patched {installed} model pack(s) into disc image.");
            return installed;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Model pack disc patch failed: {ex.Message}");
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // ignore temp cleanup
            }
        }
    }

    private static HashSet<string> LoadDiscPackManifest(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
                return set;
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
            if (ids == null)
                return set;
            foreach (var id in ids)
            {
                var n = CharacterPack.NormalizeModelId(id);
                if (n.Length > 0)
                    set.Add(n);
            }
        }
        catch
        {
            // ignore corrupt manifest
        }

        return set;
    }

    private static void SaveDiscPackManifest(string path, HashSet<string> ids)
    {
        try
        {
            var ordered = ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            File.WriteAllText(path, JsonSerializer.Serialize(ordered));
        }
        catch
        {
            // best-effort cache
        }
    }

    private static string CreateDiscBackup(string discPath, Action<string>? log)
    {
        var bakPath = discPath + ".bak";
        if (!File.Exists(bakPath))
        {
            File.Copy(discPath, bakPath, overwrite: false);
            return bakPath;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stamped = $"{discPath}.{stamp}.bak";
        File.Copy(discPath, stamped, overwrite: false);
        log?.Invoke("Existing .bak found — wrote timestamped backup instead.");
        return stamped;
    }

    private static void ReplaceDiscFileAtomically(string sourcePath, string destinationPath, Action<string>? log)
    {
        var destDir = Path.GetDirectoryName(destinationPath)
                      ?? throw new InvalidOperationException("Disc path has no directory.");
        var tempReplace = Path.Combine(destDir, Path.GetFileName(destinationPath) + ".smso-new");
        try
        {
            if (File.Exists(tempReplace))
                File.Delete(tempReplace);

            File.Copy(sourcePath, tempReplace, overwrite: true);

            try
            {
                File.Replace(tempReplace, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(destinationPath);
                File.Move(tempReplace, destinationPath);
            }
            catch (IOException)
            {
                File.Delete(destinationPath);
                File.Move(tempReplace, destinationPath);
            }

            log?.Invoke($"Replaced disc image at {destinationPath}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempReplace))
                    File.Delete(tempReplace);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void PatchLocalMarioSzs(string gameRoot, byte[] packArcBytes, Action<string>? log)
    {
        var retailPath = Path.Combine(gameRoot, RetailMarioRelative);
        var backupPath = Path.Combine(gameRoot, RetailMarioBackupRelative);
        var dataSzs = Path.Combine(gameRoot, @"files\data\mario.szs");

        // Prefer backing up files/mario/mario.szs; if missing, seed from files/data/mario.szs.
        if (!File.Exists(retailPath) && !File.Exists(backupPath) && File.Exists(dataSzs))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(retailPath)!);
            File.Copy(dataSzs, retailPath, overwrite: false);
        }

        if (!File.Exists(retailPath) && !File.Exists(backupPath))
            return;

        EnsureRetailBackup(gameRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(retailPath)!);
        // Write as Yaz0 SZS so the retail path stays a normal .szs.
        var szs = Yaz0.Compress(packArcBytes.AsSpan());
        File.WriteAllBytes(retailPath, szs);
        log?.Invoke("Updated local files/mario/mario.szs for selected model.");
    }

    private static void RestoreLocalMarioSzs(string gameRoot, Action<string>? log)
    {
        var backup = Path.Combine(gameRoot, RetailMarioBackupRelative);
        var retail = Path.Combine(gameRoot, RetailMarioRelative);
        if (!File.Exists(backup))
            return;
        File.Copy(backup, retail, overwrite: true);
        log?.Invoke("Restored retail files/mario/mario.szs.");
    }
}
