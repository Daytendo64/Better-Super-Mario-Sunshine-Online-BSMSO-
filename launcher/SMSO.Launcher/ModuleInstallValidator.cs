using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SMSO.Net;

namespace SMSO.Launcher;

internal enum ModuleInstallTargetKind
{
    None,
    ExtractedFolder,
    DiscImage,
    CompressedDiscImage,
}

internal sealed class BseRuntimeProbe
{
    public required string GameRoot { get; init; }
    public required string ModsDirectory { get; init; }
    public bool ModsDirectoryExists { get; init; }
    public string? KernelPath { get; init; }
    public string? MainDolPath { get; init; }
    public string? BootBinPath { get; init; }
    public string? BsePath { get; init; }
    public string? MovesetPath { get; init; }
    public string? BsmsPath { get; init; }
    public bool KuriboKernelInstalled { get; init; }
    public bool MainDolInstalled { get; init; }
    public bool BootBinInstalled { get; init; }
    public bool BseInstalled { get; init; }
    public bool MovesetInstalled { get; init; }
    public bool BsmsInstalled { get; init; }

    public bool IsComplete =>
        KuriboKernelInstalled && MainDolInstalled && BootBinInstalled &&
        BseInstalled && MovesetInstalled && BsmsInstalled;
}

internal static class ModuleInstallValidator
{
    public static bool IsCompressedDiscImage(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(".gcz", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Probes an extracted game root for Kuribo System, BSE DOL/boot, and .kxe modules.
    /// <c>main.dol</c>/<c>boot.bin</c> are treated as BSE-installed when present at the official v4.0.0 sizes
    /// (vanilla SMS also ships those files at different sizes).
    /// </summary>
    public static BseRuntimeProbe ProbeBseRuntime(string gameRoot)
    {
        var modsDir = Path.Combine(gameRoot, "files", "Kuribo!", "Mods");
        var kernelPath = Path.Combine(gameRoot, "files", "Kuribo!", "System", "KuriboKernel.bin");
        var mainDolPath = Path.Combine(gameRoot, "sys", "main.dol");
        var bootBinPath = Path.Combine(gameRoot, "sys", "boot.bin");
        var bsePath = Path.Combine(modsDir, ModuleVersionMessages.BseModuleFileName);
        var movesetPath = Path.Combine(modsDir, ModuleVersionMessages.MovesetModuleFileName);
        var bsmsPath = Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName);

        static bool ExistsWithSize(string path, long expectedSize) =>
            File.Exists(path) && new FileInfo(path).Length == expectedSize;

        var kernelOk = ExistsWithSize(kernelPath, ModuleInstaller.OfficialKuriboKernelSizeBytes) ||
                       File.Exists(kernelPath); // accept any kernel if present (future BSE versions)
        var mainOk = ExistsWithSize(mainDolPath, ModuleInstaller.OfficialMainDolSizeBytes);
        var bootOk = ExistsWithSize(bootBinPath, ModuleInstaller.OfficialBootBinSizeBytes) ||
                     (File.Exists(bootBinPath) &&
                      GameIdentity.TryReadGameId(bootBinPath, out var gid) &&
                      string.Equals(gid, GameIdentity.BsmsGameId, StringComparison.Ordinal));

        return new BseRuntimeProbe
        {
            GameRoot = gameRoot,
            ModsDirectory = modsDir,
            ModsDirectoryExists = Directory.Exists(modsDir),
            KernelPath = File.Exists(kernelPath) ? kernelPath : null,
            MainDolPath = File.Exists(mainDolPath) ? mainDolPath : null,
            BootBinPath = File.Exists(bootBinPath) ? bootBinPath : null,
            BsePath = File.Exists(bsePath) ? bsePath : null,
            MovesetPath = File.Exists(movesetPath) ? movesetPath : null,
            BsmsPath = File.Exists(bsmsPath) ? bsmsPath : null,
            KuriboKernelInstalled = kernelOk,
            MainDolInstalled = mainOk,
            BootBinInstalled = bootOk,
            BseInstalled = File.Exists(bsePath),
            MovesetInstalled = File.Exists(movesetPath),
            BsmsInstalled = File.Exists(bsmsPath),
        };
    }

    public static bool IsPatchableDiscImage(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(path) &&
        (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".gcm", StringComparison.OrdinalIgnoreCase));

    public static ModuleInstallTargetKind ClassifyInstallTarget(string isoPath)
    {
        if (string.IsNullOrWhiteSpace(isoPath))
            return ModuleInstallTargetKind.None;

        var trimmed = isoPath.Trim().Trim('"');
        if (IsCompressedDiscImage(trimmed) && File.Exists(trimmed))
            return ModuleInstallTargetKind.CompressedDiscImage;

        if (IsPatchableDiscImage(trimmed))
            return ModuleInstallTargetKind.DiscImage;

        if (TryResolveGameRoot(trimmed, out _))
            return ModuleInstallTargetKind.ExtractedFolder;

        return ModuleInstallTargetKind.None;
    }

    public static bool TryFindModuleFile(string isoPath, out string? modulePath)
    {
        modulePath = null;
        if (string.IsNullOrWhiteSpace(isoPath))
            return false;

        var kind = ClassifyInstallTarget(isoPath);
        if (kind is ModuleInstallTargetKind.DiscImage or ModuleInstallTargetKind.CompressedDiscImage)
        {
            // Modules live inside the image; cannot probe without extract.
            return false;
        }

        if (TryResolveModsDirectory(isoPath, out var modsDir) && modsDir != null)
        {
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

        // Fall back: scan candidate roots even if Mods does not exist yet (legacy behavior).
        foreach (var root in ResolveIsoRoots(isoPath.Trim().Trim('"')))
        {
            var modsDirScan = Path.Combine(root, "files", "Kuribo!", "Mods");
            if (!Directory.Exists(modsDirScan))
                continue;

            var primary = Path.Combine(modsDirScan, ModuleVersionMessages.ModuleFileName);
            if (File.Exists(primary))
            {
                modulePath = primary;
                return true;
            }

            var legacy = Path.Combine(modsDirScan, ModuleVersionMessages.LegacyModuleFileName);
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
        var kind = ClassifyInstallTarget(isoPath);
        if (kind == ModuleInstallTargetKind.CompressedDiscImage)
        {
            return "Compressed .gcz is not supported for Install modules. Convert to .iso/.gcm or use an extracted folder.";
        }

        if (kind == ModuleInstallTargetKind.DiscImage)
        {
            var trimmed = isoPath.Trim().Trim('"');

            // Prefer a sibling extracted tree when present (same path users often keep).
            if (TryResolveGameRoot(trimmed, out var gameRoot) && gameRoot != null)
            {
                if (!TryFindModuleFile(gameRoot, out var extractedModule) || extractedModule == null)
                {
                    return ModuleVersionMessages.MissingModuleFile(
                        Path.Combine(gameRoot, "files", "Kuribo!", "Mods", ModuleVersionMessages.ModuleFileName));
                }

                if (string.Equals(Path.GetFileName(extractedModule), ModuleVersionMessages.LegacyModuleFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return $"Legacy {ModuleVersionMessages.LegacyModuleFileName} found — rename or replace with {ModuleVersionMessages.ModuleFileName} after updating.";
                }

                return null;
            }

            // Bare .iso/.gcm: scan the FST region for the module filename (cached by mtime/size).
            if (!DiscImageContainsModuleFile(trimmed, ModuleVersionMessages.ModuleFileName))
            {
                if (DiscImageContainsModuleFile(trimmed, ModuleVersionMessages.LegacyModuleFileName))
                {
                    return $"Legacy {ModuleVersionMessages.LegacyModuleFileName} found inside the disc image — run Install / patch modules to replace with {ModuleVersionMessages.ModuleFileName}.";
                }

                return ModuleVersionMessages.MissingModuleFile(
                    Path.Combine(trimmed, "files", "Kuribo!", "Mods", ModuleVersionMessages.ModuleFileName));
            }

            return null;
        }

        if (!TryFindModuleFile(isoPath, out var modulePath) || modulePath == null)
        {
            if (TryResolveGameRoot(isoPath, out var gameRoot) && gameRoot != null)
            {
                return ModuleVersionMessages.MissingModuleFile(
                    Path.Combine(gameRoot, "files", "Kuribo!", "Mods", ModuleVersionMessages.ModuleFileName));
            }

            if (Directory.Exists(isoPath))
            {
                return ModuleVersionMessages.MissingModuleFile(
                    Path.Combine(isoPath, "files", "Kuribo!", "Mods", ModuleVersionMessages.ModuleFileName));
            }

            return null;
        }

        if (string.Equals(Path.GetFileName(modulePath), ModuleVersionMessages.LegacyModuleFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return $"Legacy {ModuleVersionMessages.LegacyModuleFileName} found — rename or replace with {ModuleVersionMessages.ModuleFileName} after updating.";
        }

        return null;
    }

    private static readonly object DiscScanCacheLock = new();
    private static string? _discScanPath;
    private static long _discScanLength;
    private static DateTime _discScanWriteUtc;
    private static string? _discScanNeedle;
    private static bool _discScanHit;

    /// <summary>
    /// Cheap FST probe: GameCube disc images store file names as ASCII in the FST.
    /// Result is cached per path/mtime/size/needle so UI refresh stays free.
    /// </summary>
    internal static bool DiscImageContainsModuleFile(string discPath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(discPath) || string.IsNullOrWhiteSpace(fileName) || !File.Exists(discPath))
            return false;

        FileInfo info;
        try
        {
            info = new FileInfo(discPath);
        }
        catch
        {
            return false;
        }

        lock (DiscScanCacheLock)
        {
            if (string.Equals(_discScanPath, discPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_discScanNeedle, fileName, StringComparison.Ordinal) &&
                _discScanLength == info.Length &&
                _discScanWriteUtc == info.LastWriteTimeUtc)
            {
                return _discScanHit;
            }
        }

        var hit = ScanFileForAsciiNeedle(discPath, fileName);

        lock (DiscScanCacheLock)
        {
            _discScanPath = discPath;
            _discScanNeedle = fileName;
            _discScanLength = info.Length;
            _discScanWriteUtc = info.LastWriteTimeUtc;
            _discScanHit = hit;
        }

        return hit;
    }

    private static bool ScanFileForAsciiNeedle(string path, string needle)
    {
        var needleBytes = System.Text.Encoding.ASCII.GetBytes(needle);
        if (needleBytes.Length == 0)
            return false;

        // GameCube FST + filenames live near the start of the image. Cap the probe so
        // ValidateInstalledModule stays cheap on multi-GB .iso files during UI refresh.
        const long maxProbeBytes = 16L * 1024 * 1024;
        const int chunkSize = 1024 * 1024;
        var buffer = new byte[chunkSize + needleBytes.Length];
        var overlap = needleBytes.Length - 1;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var probed = 0L;
            var carry = 0;
            while (probed < maxProbeBytes)
            {
                var toRead = (int)Math.Min(chunkSize, maxProbeBytes - probed);
                var read = stream.Read(buffer, carry, toRead);
                if (read <= 0)
                    break;

                probed += read;
                var spanLen = carry + read;
                if (IndexOfBytes(buffer.AsSpan(0, spanLen), needleBytes) >= 0)
                    return true;

                if (read < toRead)
                    break;

                if (overlap > 0)
                {
                    Buffer.BlockCopy(buffer, spanLen - overlap, buffer, 0, overlap);
                    carry = overlap;
                }
                else
                {
                    carry = 0;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static int IndexOfBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Resolves the extracted game root from a Game ISO path (folder, main.dol, or disc image beside extract).
    /// A valid root has <c>sys</c> and/or <c>files</c> (or <c>sys\main.dol</c>).
    /// Does not treat a bare .iso/.gcm as a game root — use <see cref="ClassifyInstallTarget"/> for that.
    /// </summary>
    public static bool TryResolveGameRoot(string isoPath, out string? gameRoot)
    {
        gameRoot = null;
        if (string.IsNullOrWhiteSpace(isoPath))
            return false;

        var trimmed = isoPath.Trim().Trim('"');

        // Prefer an extracted tree next to a disc image, but never claim the .iso itself is the root.
        foreach (var candidate in ResolveIsoRoots(trimmed))
        {
            if (IsValidExtractedGameRoot(candidate))
            {
                gameRoot = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves <c>files\Kuribo!\Mods</c> under a valid extracted game root.
    /// Returns true even if the Mods folder does not exist yet (caller may create it).
    /// Returns false for bare disc images (use disc patch flow instead).
    /// </summary>
    public static bool TryResolveModsDirectory(string isoPath, out string? modsDir)
    {
        modsDir = null;
        if (!TryResolveGameRoot(isoPath, out var gameRoot) || gameRoot == null)
            return false;

        modsDir = Path.Combine(gameRoot, "files", "Kuribo!", "Mods");
        return true;
    }

    public static bool IsValidExtractedGameRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        var sysDir = Path.Combine(path, "sys");
        var filesDir = Path.Combine(path, "files");
        if (Directory.Exists(sysDir) || Directory.Exists(filesDir))
            return true;

        return File.Exists(Path.Combine(path, "sys", "main.dol"));
    }

    internal static IEnumerable<string> ResolveIsoRoots(string isoPath)
    {
        var candidates = new List<string>();
        if (isoPath.EndsWith(@"\sys\main.dol", StringComparison.OrdinalIgnoreCase) ||
            isoPath.EndsWith("/sys/main.dol", StringComparison.OrdinalIgnoreCase))
        {
            var sysParent = Path.GetDirectoryName(isoPath);
            var root = sysParent != null ? Path.GetDirectoryName(sysParent) : null;
            if (!string.IsNullOrWhiteSpace(root))
                candidates.Add(root);
        }

        if (File.Exists(isoPath))
        {
            // Disc images are not extracted roots; still allow sibling extract folders.
            var parent = Path.GetDirectoryName(isoPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                candidates.Add(parent);
                var grand = Path.GetDirectoryName(parent);
                if (!string.IsNullOrWhiteSpace(grand))
                    candidates.Add(grand);
            }
        }
        else if (Directory.Exists(isoPath))
        {
            candidates.Add(isoPath);
        }

        return candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
