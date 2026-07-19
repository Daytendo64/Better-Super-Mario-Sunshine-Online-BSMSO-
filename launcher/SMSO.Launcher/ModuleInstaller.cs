using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SMSO.Net;

namespace SMSO.Launcher;

internal sealed class ModuleInstallStatus
{
    public bool CanInstall { get; init; }
    public ModuleInstallTargetKind TargetKind { get; init; }
    public bool ModsDirectoryExists { get; init; }
    public string? ModsDirectory { get; init; }
    public string? GameRoot { get; init; }
    public string? DiscImagePath { get; init; }
    public bool KuriboKernelInstalled { get; init; }
    public bool MainDolInstalled { get; init; }
    public bool BootBinInstalled { get; init; }
    public bool BseInstalled { get; init; }
    public bool MovesetInstalled { get; init; }
    public bool BsmsInstalled { get; init; }
    /// <summary>True when runtime files are present but older than this launcher's bundle / ModBuildId.</summary>
    public bool NeedsUpdate { get; init; }
    public bool IsComplete { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Paths into a cached official Better Sunshine Engine v4.0.0 game payload
/// (Kuribo! runtime + patched main.dol/boot.bin + BetterSunshineEngine.kxe).
/// </summary>
internal sealed class OfficialBsePayload
{
    public required string StagingRoot { get; init; }
    public required string KuriboRoot { get; init; }
    public required string BseKxePath { get; init; }
    public required string MainDolPath { get; init; }
    public required string BootBinPath { get; init; }
    public required string KernelPath { get; init; }
}

internal static class ModuleInstaller
{
    public const long OfficialBseSizeBytes = 583_744;
    public const long OfficialMovesetSizeBytes = 46_976;
    public const long OfficialKuriboKernelSizeBytes = 21_517;
    public const long OfficialMainDolSizeBytes = 4_128_928;
    public const long OfficialBootBinSizeBytes = 1_088;
    public const string BseReleaseZipUrl =
        "https://github.com/DotKuribo/BetterSunshineEngine/releases/download/v4.0.0/BetterSunshineEngine_RELEASE.zip";
    public const string OfficialBseZipFileName = "BetterSunshineEngine_RELEASE.zip";
    public const string OfficialBseStagingFolderName = "bse-v4.0.0";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    public static ModuleInstallStatus GetInstallStatus(string isoPath)
    {
        if (string.IsNullOrWhiteSpace(isoPath))
        {
            return new ModuleInstallStatus
            {
                CanInstall = false,
                TargetKind = ModuleInstallTargetKind.None,
                Message = "Set Game ISO path in Paths to an extracted SMS folder, sys\\main.dol, or a .iso/.gcm disc image.",
            };
        }

        var trimmed = isoPath.Trim().Trim('"');
        var kind = ModuleInstallValidator.ClassifyInstallTarget(trimmed);

        if (kind == ModuleInstallTargetKind.CompressedDiscImage)
        {
            return new ModuleInstallStatus
            {
                CanInstall = false,
                TargetKind = kind,
                DiscImagePath = trimmed,
                Message =
                    "Compressed .gcz is not supported.\nConvert to .iso/.gcm, or set Game ISO to an extracted folder.",
            };
        }

        if (kind == ModuleInstallTargetKind.DiscImage)
        {
            var fullDisc = Path.GetFullPath(trimmed);
            var discHasModule = ModuleInstallValidator.DiscImageContainsModuleFile(
                fullDisc, ModuleVersionMessages.ModuleFileName);
            var discNeedsUpdate = discHasModule && IsDiscImageModuleStale(fullDisc);

            string discMessage;
            if (discNeedsUpdate)
            {
                discMessage = ModuleVersionMessages.UpdateRequired +
                              $"\n{fullDisc}\n" +
                              $"Installed build marker is older than launcher build {ProtocolConstants.ModBuildId}.";
            }
            else if (discHasModule)
            {
                discMessage =
                    $"BSE / Kuribo modules present in disc image (build {ProtocolConstants.ModBuildId}):\n{fullDisc}\n" +
                    "Re-run Install / patch modules to rewrite the image if needed.";
            }
            else
            {
                discMessage =
                    $"Ready to patch disc image:\n{fullDisc}\n" +
                    "Install / patch modules will back up, then install Kuribo System, BSE main.dol/boot.bin, " +
                    "BetterSunshineEngine.kxe, BetterSunshineMoveset.kxe, and _BSMSO.kxe into the image.";
            }

            return new ModuleInstallStatus
            {
                CanInstall = true,
                TargetKind = kind,
                DiscImagePath = fullDisc,
                IsComplete = discHasModule && !discNeedsUpdate,
                NeedsUpdate = discNeedsUpdate,
                Message = discMessage,
            };
        }

        if (kind != ModuleInstallTargetKind.ExtractedFolder ||
            !ModuleInstallValidator.TryResolveGameRoot(trimmed, out var gameRoot) ||
            gameRoot == null)
        {
            return new ModuleInstallStatus
            {
                CanInstall = false,
                TargetKind = ModuleInstallTargetKind.None,
                Message =
                    "Game path is not a valid extracted SMS folder or .iso/.gcm.\n" +
                    "Use a folder with sys\\ and/or files\\, sys\\main.dol, or a raw disc image.",
            };
        }

        var probe = ModuleInstallValidator.ProbeBseRuntime(gameRoot);
        // Older launchers wrote the ModBuildId sidecar inside Mods; Kuribo tries to load it
        // as a module and black-screens. Purge on every status check.
        if (probe.ModsDirectoryExists)
            RemoveLegacyModsFolderMarker(probe.ModsDirectory);
        var needsUpdate = probe.IsComplete && IsExtractedModuleStale(probe);
        var message = needsUpdate
            ? ModuleVersionMessages.UpdateRequired + "\n" + gameRoot
            : BuildExtractedStatusMessage(gameRoot, probe);

        return new ModuleInstallStatus
        {
            CanInstall = true,
            TargetKind = ModuleInstallTargetKind.ExtractedFolder,
            ModsDirectoryExists = probe.ModsDirectoryExists,
            ModsDirectory = probe.ModsDirectory,
            GameRoot = gameRoot,
            KuriboKernelInstalled = probe.KuriboKernelInstalled,
            MainDolInstalled = probe.MainDolInstalled,
            BootBinInstalled = probe.BootBinInstalled,
            BseInstalled = probe.BseInstalled,
            MovesetInstalled = probe.MovesetInstalled,
            BsmsInstalled = probe.BsmsInstalled,
            IsComplete = probe.IsComplete && !needsUpdate,
            NeedsUpdate = needsUpdate,
            Message = message,
        };
    }

    public static async Task<(bool Success, string Message)> InstallAsync(
        string isoPath,
        Action<string>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var status = GetInstallStatus(isoPath);
        if (!status.CanInstall)
            return (false, status.Message);

        if (!TryFindSourceModule(ModuleVersionMessages.ModuleFileName, out var bsmsSource) || bsmsSource == null)
        {
            return (false,
                $"{ModuleVersionMessages.ModuleFileName} not found next to the launcher or in dist\\. " +
                "Place it beside BSMSO.Launcher.exe, or build with tools\\build.ps1.");
        }

        if (!TryFindSourceModule(ModuleVersionMessages.MovesetModuleFileName, out var movesetSource) ||
            movesetSource == null)
        {
            return (false,
                $"{ModuleVersionMessages.MovesetModuleFileName} not found next to the launcher or in dist\\. " +
                "Place it beside BSMSO.Launcher.exe (ships in the release zip).");
        }

        progress?.Invoke("Preparing Better Sunshine Engine…");
        log?.Invoke("Ensuring official BetterSunshineEngine v4.0.0 runtime payload (Kuribo!, main.dol, boot.bin)…");
        var ensure = await EnsureOfficialBsePayloadCachedAsync(log, cancellationToken).ConfigureAwait(false);
        if (!ensure.Success || ensure.Payload == null)
            return (false, ensure.Message);

        var payload = ensure.Payload;
        string bseSource = payload.BseKxePath;
        if (TryFindSourceModule(ModuleVersionMessages.BseModuleFileName, out var localBse) && localBse != null)
        {
            // Prefer a local .kxe beside the launcher/dist, but still install Kuribo + DOL from the official payload.
            bseSource = localBse;
            log?.Invoke($"Using local {ModuleVersionMessages.BseModuleFileName}: {bseSource}");
        }
        else
        {
            log?.Invoke($"Using official {ModuleVersionMessages.BseModuleFileName}: {bseSource}");
        }

        log?.Invoke($"Using {ModuleVersionMessages.MovesetModuleFileName}: {movesetSource}");

        if (status.TargetKind == ModuleInstallTargetKind.DiscImage)
        {
            var discPath = status.DiscImagePath ?? isoPath.Trim().Trim('"');
            return await DiscImagePatcher.PatchAsync(
                    discPath,
                    payload,
                    bseSource,
                    movesetSource,
                    bsmsSource,
                    progress,
                    log,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(status.GameRoot))
            return (false, status.Message);

        var gameRoot = status.GameRoot;
        progress?.Invoke("Installing BSE runtime…");
        var install = InstallBseRuntimeIntoGameRoot(
            gameRoot,
            payload,
            bseSource,
            movesetSource,
            bsmsSource,
            patchGameId: true,
            log);
        if (!install.Success)
            return install;

        // Drop leftover shared icon.png banners so other Dolphin games in the same folder
        // don't pick up BSMSO art (Launch Dolphin also does this).
        DolphinConfigService.EnsureBsmsGameBanner(gameRoot, log, out _);

        progress?.Invoke("Installing custom Mario packs…");
        MarioPackInstaller.EnsureAllLibraryPacksPresent(gameRoot, log);

        var probe = ModuleInstallValidator.ProbeBseRuntime(gameRoot);
        return (true,
            $"Installed BSE / Kuribo runtime into:\n{gameRoot}\n\n" +
            $"KuriboKernel.bin ({new FileInfo(probe.KernelPath!).Length} bytes)\n" +
            $"sys\\main.dol ({new FileInfo(probe.MainDolPath!).Length} bytes)\n" +
            $"sys\\boot.bin ({new FileInfo(probe.BootBinPath!).Length} bytes) → {GameIdentity.BsmsGameId}\n" +
            $"{ModuleVersionMessages.BseModuleFileName} ({new FileInfo(probe.BsePath!).Length} bytes)\n" +
            $"{ModuleVersionMessages.MovesetModuleFileName} ({new FileInfo(probe.MovesetPath!).Length} bytes)\n" +
            $"{ModuleVersionMessages.ModuleFileName} ({new FileInfo(probe.BsmsPath!).Length} bytes)\n\n" +
            "Restart Dolphin to load the updated modules.");
    }

    /// <summary>
    /// Installs the full official BSE game payload into an extracted GameCube tree:
    /// merges <c>files\Kuribo!</c> (System + BSE .kxe), overwrites <c>sys\main.dol</c> /
    /// <c>sys\boot.bin</c>, writes Moveset + <c>_BSMSO.kxe</c>, and optionally patches GMSE90.
    /// </summary>
    internal static (bool Success, string Message) InstallBseRuntimeIntoGameRoot(
        string gameRoot,
        OfficialBsePayload payload,
        string bseSource,
        string movesetSource,
        string bsmsSource,
        bool patchGameId,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return (false, $"Game root not found:\n{gameRoot}");

        try
        {
            var filesDir = Path.Combine(gameRoot, "files");
            var sysDir = Path.Combine(gameRoot, "sys");
            var kuriboDest = Path.Combine(filesDir, "Kuribo!");
            var systemDest = Path.Combine(kuriboDest, "System");
            var modsDest = Path.Combine(kuriboDest, "Mods");

            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(sysDir);
            Directory.CreateDirectory(systemDest);
            Directory.CreateDirectory(modsDest);

            log?.Invoke($"Merging Kuribo! System → {systemDest}");
            CopyDirectoryMerge(Path.Combine(payload.KuriboRoot, "System"), systemDest);

            // Also copy any other top-level Kuribo! entries except Mods (handled selectively).
            foreach (var entry in Directory.EnumerateFileSystemEntries(payload.KuriboRoot))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "System", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Mods", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dest = Path.Combine(kuriboDest, name);
                if (Directory.Exists(entry))
                    CopyDirectoryMerge(entry, dest);
                else
                    File.Copy(entry, dest, overwrite: true);
            }

            var bseCopy = CopyBseKxeIntoModsDirectory(modsDest, bseSource, log);
            if (!bseCopy.Success)
                return bseCopy;

            File.Copy(movesetSource, Path.Combine(modsDest, ModuleVersionMessages.MovesetModuleFileName),
                overwrite: true);
            log?.Invoke(
                $"Installed {ModuleVersionMessages.MovesetModuleFileName} " +
                $"({new FileInfo(movesetSource).Length} bytes) → " +
                $"{Path.Combine(modsDest, ModuleVersionMessages.MovesetModuleFileName)}");

            File.Copy(bsmsSource, Path.Combine(modsDest, ModuleVersionMessages.ModuleFileName), overwrite: true);
            log?.Invoke(
                $"Installed {ModuleVersionMessages.ModuleFileName} " +
                $"({new FileInfo(bsmsSource).Length} bytes) → {Path.Combine(modsDest, ModuleVersionMessages.ModuleFileName)}");

            var mainDest = Path.Combine(sysDir, "main.dol");
            var bootDest = Path.Combine(sysDir, "boot.bin");
            File.Copy(payload.MainDolPath, mainDest, overwrite: true);
            File.Copy(payload.BootBinPath, bootDest, overwrite: true);
            log?.Invoke($"Installed BSE main.dol ({new FileInfo(mainDest).Length} bytes) → {mainDest}");
            log?.Invoke($"Installed BSE boot.bin ({new FileInfo(bootDest).Length} bytes) → {bootDest}");

            if (patchGameId)
            {
                if (GameIdentity.TryPatchGameId(bootDest, GameIdentity.BsmsGameId, out var patchError))
                {
                    log?.Invoke($"Patched sys\\boot.bin game ID to {GameIdentity.BsmsGameId}");
                }
                else if (!string.IsNullOrWhiteSpace(patchError))
                {
                    return (false,
                        $"BSE runtime was written but game ID patch failed:\n{patchError}\n\nboot.bin:\n{bootDest}");
                }
                else
                {
                    log?.Invoke($"sys\\boot.bin game ID already {GameIdentity.BsmsGameId}");
                }
            }

            var kernelDest = Path.Combine(systemDest, "KuriboKernel.bin");
            if (!File.Exists(kernelDest))
                return (false, $"KuriboKernel.bin missing after install:\n{kernelDest}");

            WriteModBuildIdMarker(modsDest, log);
            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to install BSE runtime:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Copies BetterSunshineEngine.kxe into an existing Mods directory, backing up non-release builds.
    /// </summary>
    internal static (bool Success, string Message) CopyBseKxeIntoModsDirectory(
        string modsDir,
        string bseSource,
        Action<string>? log = null)
    {
        var bseDest = Path.Combine(modsDir, ModuleVersionMessages.BseModuleFileName);
        try
        {
            Directory.CreateDirectory(modsDir);
            if (File.Exists(bseDest))
            {
                var existingSize = new FileInfo(bseDest).Length;
                if (existingSize != OfficialBseSizeBytes)
                {
                    var backup = Path.Combine(modsDir, ModuleVersionMessages.BseModuleFileName + ".dev-backup");
                    File.Copy(bseDest, backup, overwrite: true);
                    log?.Invoke(
                        $"Replacing non-release BetterSunshineEngine.kxe ({existingSize} bytes) with install source; backup: {backup}");
                }
            }

            File.Copy(bseSource, bseDest, overwrite: true);
            log?.Invoke(
                $"Installed {ModuleVersionMessages.BseModuleFileName} ({new FileInfo(bseDest).Length} bytes) → {bseDest}");
            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to copy BetterSunshineEngine.kxe into Mods:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Legacy helper: copies BSE + Moveset + BSMSO .kxe files into Mods only (no Kuribo System / DOL).
    /// Prefer <see cref="InstallBseRuntimeIntoGameRoot"/> for full installs.
    /// </summary>
    internal static (bool Success, string Message) CopyModulesIntoModsDirectory(
        string modsDir,
        string bseSource,
        string movesetSource,
        string bsmsSource,
        Action<string>? log = null)
    {
        var bse = CopyBseKxeIntoModsDirectory(modsDir, bseSource, log);
        if (!bse.Success)
            return bse;

        try
        {
            var movesetDest = Path.Combine(modsDir, ModuleVersionMessages.MovesetModuleFileName);
            File.Copy(movesetSource, movesetDest, overwrite: true);
            log?.Invoke(
                $"Installed {ModuleVersionMessages.MovesetModuleFileName} ({new FileInfo(movesetDest).Length} bytes) → {movesetDest}");

            var bsmsDest = Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName);
            File.Copy(bsmsSource, bsmsDest, overwrite: true);
            log?.Invoke(
                $"Installed {ModuleVersionMessages.ModuleFileName} ({new FileInfo(bsmsDest).Length} bytes) → {bsmsDest}");
            WriteModBuildIdMarker(modsDir, log);
            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to copy modules into Mods:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Overwrite Kuribo Mods .kxe files from the ones shipped next to the
    /// launcher/zip (when present and different). Runs on launch so a zip update
    /// picks up a new <c>_BSMSO.kxe</c> without requiring Install Modules again.
    /// Does not re-download BSE or rewrite main.dol — only syncs local kxe files.
    /// </summary>
    public static BundledModuleSyncResult SyncBundledModulesIntoGame(string isoPath, Action<string>? log = null)
    {
        var result = new BundledModuleSyncResult();
        var trimmed = isoPath?.Trim().Trim('"') ?? string.Empty;
        if (trimmed.Length == 0)
            return result;

        if (!ModuleInstallValidator.TryResolveGameRoot(trimmed, out var gameRoot) || gameRoot == null)
            return result;

        var modsDir = Path.Combine(gameRoot, "files", "Kuribo!", "Mods");
        Directory.CreateDirectory(modsDir);

        var synced = 0;
        var bsmsoChanged = false;
        var bsmsoMatches = false;
        var hasBundledBsmso = TryFindSourceModule(ModuleVersionMessages.ModuleFileName, out var bundledBsmso)
                              && bundledBsmso != null;

        foreach (var name in new[]
                 {
                     ModuleVersionMessages.ModuleFileName,
                     ModuleVersionMessages.MovesetModuleFileName,
                     ModuleVersionMessages.BseModuleFileName,
                 })
        {
            if (!TryFindSourceModule(name, out var source) || source == null)
                continue;

            var dest = Path.Combine(modsDir, name);
            try
            {
                var changed = true;
                if (File.Exists(dest))
                {
                    if (FilesMatch(source, dest))
                    {
                        changed = false;
                        if (name == ModuleVersionMessages.ModuleFileName)
                            bsmsoMatches = true;
                    }
                }

                if (!changed)
                    continue;

                File.Copy(source, dest, overwrite: true);
                synced++;
                if (name == ModuleVersionMessages.ModuleFileName)
                {
                    bsmsoChanged = true;
                    bsmsoMatches = true;
                }

                log?.Invoke($"Synced {name} → {dest} ({new FileInfo(dest).Length:N0} bytes)");
            }
            catch (Exception ex)
            {
                log?.Invoke($"Skipped syncing {name}: {ex.Message}");
            }
        }

        if (hasBundledBsmso && !bsmsoMatches)
        {
            var dest = Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName);
            try
            {
                if (File.Exists(dest) && bundledBsmso != null)
                    bsmsoMatches = FilesMatch(bundledBsmso, dest);
            }
            catch
            {
                bsmsoMatches = false;
            }
        }
        else if (!hasBundledBsmso)
        {
            // No bundled module beside the launcher — cannot assert freshness; do not gate.
            bsmsoMatches = true;
        }

        // Keep the ModBuildId sidecar in sync only when we verified against a bundled kxe.
        if (hasBundledBsmso && bsmsoMatches &&
            File.Exists(Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName)))
        {
            WriteModBuildIdMarker(modsDir, log);
        }

        return new BundledModuleSyncResult
        {
            SyncedCount = synced,
            BsmsoModuleChanged = bsmsoChanged,
            InstalledMatchesBundled = bsmsoMatches,
            BundledModuleAvailable = hasBundledBsmso,
        };
    }

    public static bool TryFindSourceModule(string fileName, out string? path)
    {
        path = null;
        foreach (var dir in EnumerateSearchDirectories())
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Marker path for an extracted install: <c>files/Kuribo!/.bsmso-mod-build-id</c>
    /// (parent of Mods). Never write into Mods — Kuribo treats every Mods entry as a .kxe.
    /// </summary>
    internal static string GetExtractedModBuildIdMarkerPath(string modsDirectory)
    {
        var mods = Path.GetFullPath(modsDirectory.Trim());
        var kuribo = Directory.GetParent(mods)?.FullName;
        if (string.IsNullOrEmpty(kuribo))
            return Path.Combine(mods, ModuleVersionMessages.ModBuildIdMarkerFileName);
        return Path.Combine(kuribo, ModuleVersionMessages.ModBuildIdMarkerFileName);
    }

    /// <summary>
    /// Writes the ModBuildId sidecar beside Mods (in Kuribo!), and removes any legacy
    /// marker that was incorrectly placed inside Mods (broke Kuribo boot).
    /// </summary>
    internal static void WriteModBuildIdMarker(string modsDirectory, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory))
            return;

        try
        {
            Directory.CreateDirectory(modsDirectory);
            RemoveLegacyModsFolderMarker(modsDirectory, log);

            var path = GetExtractedModBuildIdMarkerPath(modsDirectory);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(path, ProtocolConstants.ModBuildId.ToString());
            log?.Invoke(
                $"Wrote {ModuleVersionMessages.ModBuildIdMarkerFileName} " +
                $"(build {ProtocolConstants.ModBuildId}) → {path}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not write mod-build marker: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a mistaken Mods-folder marker from older launcher builds so Kuribo can boot.
    /// </summary>
    internal static void RemoveLegacyModsFolderMarker(string modsDirectory, Action<string>? log = null)
    {
        try
        {
            var legacy = Path.Combine(modsDirectory, ModuleVersionMessages.ModBuildIdMarkerFileName);
            if (!File.Exists(legacy))
                return;
            File.Delete(legacy);
            log?.Invoke(
                $"Removed legacy {ModuleVersionMessages.ModBuildIdMarkerFileName} from Mods " +
                "(Kuribo was treating it as a module and blocking boot)");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not remove legacy Mods marker: {ex.Message}");
        }
    }

    /// <summary>
    /// Sidecar next to a patched .iso/.gcm (not inside the image).
    /// </summary>
    internal static string GetDiscImageModBuildIdMarkerPath(string discImagePath) =>
        Path.GetFullPath(discImagePath.Trim().Trim('"')) + ModuleVersionMessages.ModBuildIdMarkerFileName;

    internal static void WriteDiscImageModBuildIdMarker(string discImagePath, Action<string>? log = null)
    {
        try
        {
            var path = GetDiscImageModBuildIdMarkerPath(discImagePath);
            File.WriteAllText(path, ProtocolConstants.ModBuildId.ToString());
            log?.Invoke(
                $"Wrote disc mod-build marker (build {ProtocolConstants.ModBuildId}) → {path}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not write disc mod-build marker: {ex.Message}");
        }
    }

    internal static bool TryReadModBuildIdMarker(string markerPath, out ushort buildId)
    {
        buildId = 0;
        if (string.IsNullOrWhiteSpace(markerPath) || !File.Exists(markerPath))
            return false;

        try
        {
            var text = File.ReadAllText(markerPath).Trim();
            return ushort.TryParse(text, out buildId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the extracted tree has a complete install that is older than this launcher.
    /// Uses ModBuildId sidecar and/or byte compare against bundled .kxe files.
    /// </summary>
    internal static bool IsExtractedModuleStale(BseRuntimeProbe probe)
    {
        if (!probe.IsComplete)
            return false;

        // Prefer Kuribo!-level marker; also accept a legacy Mods copy until Update migrates it.
        var markerPath = GetExtractedModBuildIdMarkerPath(probe.ModsDirectory);
        var hasMarker = TryReadModBuildIdMarker(markerPath, out var installedBuild);
        if (!hasMarker)
        {
            var legacy = Path.Combine(probe.ModsDirectory, ModuleVersionMessages.ModBuildIdMarkerFileName);
            hasMarker = TryReadModBuildIdMarker(legacy, out installedBuild);
        }

        if (hasMarker && installedBuild < ProtocolConstants.ModBuildId)
            return true;

        if (BundledKxeFilesDifferFromInstalled(probe))
            return true;

        // Complete install with no marker (pre-sidecar installs): treat as stale until
        // Update / sync rewrites the marker for the current launcher build.
        if (!hasMarker)
            return true;

        return false;
    }

    internal static bool IsDiscImageModuleStale(string discImagePath)
    {
        var markerPath = GetDiscImageModBuildIdMarkerPath(discImagePath);
        if (!TryReadModBuildIdMarker(markerPath, out var installedBuild))
            return true;

        return installedBuild < ProtocolConstants.ModBuildId;
    }

    private static bool BundledKxeFilesDifferFromInstalled(BseRuntimeProbe probe)
    {
        if (probe.BsmsPath != null &&
            TryFindSourceModule(ModuleVersionMessages.ModuleFileName, out var bundledBsms) &&
            bundledBsms != null &&
            !FilesMatch(bundledBsms, probe.BsmsPath))
        {
            return true;
        }

        if (probe.MovesetPath != null &&
            TryFindSourceModule(ModuleVersionMessages.MovesetModuleFileName, out var bundledMoveset) &&
            bundledMoveset != null &&
            !FilesMatch(bundledMoveset, probe.MovesetPath))
        {
            return true;
        }

        return false;
    }

    /// <summary>Length + SHA-256 compare; avoids loading entire .kxe files into memory twice.</summary>
    internal static bool FilesMatch(string pathA, string pathB)
    {
        try
        {
            var a = new FileInfo(pathA);
            var b = new FileInfo(pathB);
            if (!a.Exists || !b.Exists || a.Length != b.Length)
                return false;

            if (string.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB), StringComparison.OrdinalIgnoreCase))
                return true;

            var hashA = ComputeFileSha256(pathA);
            var hashB = ComputeFileSha256(pathB);
            return hashA.AsSpan().SequenceEqual(hashB.AsSpan());
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ComputeFileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    private static string BuildExtractedStatusMessage(string gameRoot, BseRuntimeProbe probe)
    {
        var missing = new List<string>();
        if (!probe.KuriboKernelInstalled)
            missing.Add("Kuribo!\\System\\KuriboKernel.bin");
        if (!probe.MainDolInstalled)
            missing.Add("sys\\main.dol (BSE)");
        if (!probe.BootBinInstalled)
            missing.Add("sys\\boot.bin (BSE)");
        if (!probe.BseInstalled)
            missing.Add(ModuleVersionMessages.BseModuleFileName);
        if (!probe.MovesetInstalled)
            missing.Add(ModuleVersionMessages.MovesetModuleFileName);
        if (!probe.BsmsInstalled)
            missing.Add(ModuleVersionMessages.ModuleFileName);

        if (missing.Count == 0)
        {
            return
                $"BSE / Kuribo runtime installed (build {ProtocolConstants.ModBuildId}):\n{gameRoot}\n" +
                $"KuriboKernel.bin, sys\\main.dol, sys\\boot.bin, " +
                $"{ModuleVersionMessages.BseModuleFileName}, {ModuleVersionMessages.MovesetModuleFileName}, " +
                $"{ModuleVersionMessages.ModuleFileName}";
        }

        if (!probe.ModsDirectoryExists &&
            !probe.KuriboKernelInstalled &&
            !probe.MainDolInstalled &&
            !probe.BootBinInstalled)
        {
            return
                $"BSE / Kuribo runtime missing — Install / patch modules will create:\n" +
                $"{Path.Combine(gameRoot, "files", "Kuribo!")}\n" +
                "and overwrite sys\\main.dol / sys\\boot.bin.";
        }

        return $"Missing: {string.Join(", ", missing)}\n{gameRoot}";
    }

    private static IEnumerable<string> EnumerateSearchDirectories()
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
        Add(exeDir);

        // Nested publish layouts: walk a few parents from the exe.
        var walk = exeDir;
        for (var i = 0; i < 5 && !string.IsNullOrWhiteSpace(walk); i++)
        {
            walk = Path.GetDirectoryName(walk);
            Add(walk);
            if (!string.IsNullOrWhiteSpace(walk))
                Add(Path.Combine(walk, "dist"));
        }

        Add(Environment.CurrentDirectory);
        Add(Path.Combine(Environment.CurrentDirectory, "dist"));

        // Common repo layout when running from launcher/SMSO.Launcher/bin/...
        walk = exeDir;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(walk); i++)
        {
            Add(Path.Combine(walk, "dist"));
            walk = Path.GetDirectoryName(walk);
        }

        Add(GetSmsoAppDataDirectory());
        Add(GetOfficialBseStagingDirectory());

        return seen;
    }

    internal static string GetSmsoAppDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMSO");

    internal static string GetOfficialBseZipCachePath() =>
        Path.Combine(GetSmsoAppDataDirectory(), OfficialBseZipFileName);

    internal static string GetOfficialBseStagingDirectory() =>
        Path.Combine(GetSmsoAppDataDirectory(), OfficialBseStagingFolderName);

    /// <summary>Legacy single-file cache path (still populated for older tooling).</summary>
    internal static string GetOfficialBseCachePath() =>
        Path.Combine(GetSmsoAppDataDirectory(), "BetterSunshineEngine.release.kxe");

    internal static async Task<(bool Success, OfficialBsePayload? Payload, string Message)> EnsureOfficialBsePayloadCachedAsync(
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(GetSmsoAppDataDirectory());
        }
        catch (Exception ex)
        {
            return (false, null, $"Could not create BSE cache folder:\n{ex.Message}");
        }

        if (TryLoadPayloadFromStaging(GetOfficialBseStagingDirectory(), out var staged) && staged != null)
        {
            EnsureLegacyKxeCache(staged.BseKxePath, log);
            return (true, staged, "Using cached official BetterSunshineEngine v4.0.0 payload.");
        }

        var zipPath = GetOfficialBseZipCachePath();
        if (!File.Exists(zipPath) || new FileInfo(zipPath).Length < 100_000)
        {
            // Prefer a repo/dist copy of the zip if present.
            var localZip = FindLocalReleaseZip();
            if (localZip != null)
            {
                try
                {
                    File.Copy(localZip, zipPath, overwrite: true);
                    log?.Invoke($"Copied official BSE zip from {localZip}");
                }
                catch
                {
                    zipPath = localZip;
                }
            }
            else
            {
                var download = await DownloadOfficialBseZipAsync(zipPath, log, cancellationToken).ConfigureAwait(false);
                if (!download.Success)
                    return (false, null, download.Message);
            }
        }
        else
        {
            log?.Invoke($"Using cached BSE zip: {zipPath}");
        }

        var extract = ExtractOfficialBseZipToStaging(zipPath, GetOfficialBseStagingDirectory(), log);
        if (!extract.Success || extract.Payload == null)
            return (false, null, extract.Message);

        EnsureLegacyKxeCache(extract.Payload.BseKxePath, log);
        return (true, extract.Payload, "Cached official BetterSunshineEngine v4.0.0 payload.");
    }

    private static string? FindLocalReleaseZip()
    {
        foreach (var dir in EnumerateSearchDirectories())
        {
            var candidate = Path.Combine(dir, OfficialBseZipFileName);
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 100_000)
                return candidate;
        }

        return null;
    }

    private static async Task<(bool Success, string Message)> DownloadOfficialBseZipAsync(
        string zipPath,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "smso-bse-release-" + Guid.NewGuid().ToString("N"));
        var tempZip = Path.Combine(tempDir, OfficialBseZipFileName);
        try
        {
            Directory.CreateDirectory(tempDir);
            log?.Invoke("Downloading official BetterSunshineEngine v4.0.0 release zip…");
            using (var response = await Http.GetAsync(BseReleaseZipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempZip);
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            File.Copy(tempZip, zipPath, overwrite: true);
            log?.Invoke($"Cached official BSE zip at {zipPath} ({new FileInfo(zipPath).Length} bytes)");
            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false,
                $"Could not download BetterSunshineEngine_RELEASE.zip.\n{ex.Message}\n\n" +
                $"Place the zip under %AppData%\\SMSO\\, or download from:\n{ModuleVersionMessages.BseReleaseUrl}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }
    }

    private static (bool Success, OfficialBsePayload? Payload, string Message) ExtractOfficialBseZipToStaging(
        string zipPath,
        string stagingRoot,
        Action<string>? log)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "smso-bse-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(tempDir);

            log?.Invoke($"Extracting official BSE payload from {zipPath}…");
            ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

            var kuribo = Directory.EnumerateDirectories(tempDir, "Kuribo!", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (kuribo == null)
                return (false, null, "BetterSunshineEngine_RELEASE.zip did not contain a Kuribo! folder.");

            var releaseRoot = Path.GetDirectoryName(kuribo);
            if (string.IsNullOrWhiteSpace(releaseRoot))
                return (false, null, "Could not resolve BetterSunshineEngine release root from zip.");

            var mainDol = Path.Combine(releaseRoot, "main.dol");
            var bootBin = Path.Combine(releaseRoot, "boot.bin");
            var kernel = Path.Combine(kuribo, "System", "KuriboKernel.bin");
            var bseKxe = Path.Combine(kuribo, "Mods", ModuleVersionMessages.BseModuleFileName);
            if (!File.Exists(bseKxe))
            {
                bseKxe = Directory.EnumerateFiles(tempDir, ModuleVersionMessages.BseModuleFileName, SearchOption.AllDirectories)
                    .FirstOrDefault() ?? "";
            }

            if (!File.Exists(mainDol) || !File.Exists(bootBin) || !File.Exists(kernel) || !File.Exists(bseKxe))
            {
                return (false, null,
                    "BetterSunshineEngine_RELEASE.zip is missing required files " +
                    "(Kuribo!\\System\\KuriboKernel.bin, main.dol, boot.bin, BetterSunshineEngine.kxe).");
            }

            // Stage a clean payload tree: Kuribo!, main.dol, boot.bin, and a top-level kxe copy.
            var stagedKuribo = Path.Combine(stagingRoot, "Kuribo!");
            CopyDirectoryMerge(kuribo, stagedKuribo);
            File.Copy(mainDol, Path.Combine(stagingRoot, "main.dol"), overwrite: true);
            File.Copy(bootBin, Path.Combine(stagingRoot, "boot.bin"), overwrite: true);
            File.Copy(bseKxe, Path.Combine(stagingRoot, ModuleVersionMessages.BseModuleFileName), overwrite: true);

            if (!TryLoadPayloadFromStaging(stagingRoot, out var payload) || payload == null)
                return (false, null, "Failed to validate staged BetterSunshineEngine payload.");

            log?.Invoke($"Staged official BSE payload at {stagingRoot}");
            return (true, payload, "OK");
        }
        catch (Exception ex)
        {
            return (false, null, $"Failed to extract BetterSunshineEngine_RELEASE.zip:\n{ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool TryLoadPayloadFromStaging(string stagingRoot, out OfficialBsePayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(stagingRoot) || !Directory.Exists(stagingRoot))
            return false;

        var kuribo = Path.Combine(stagingRoot, "Kuribo!");
        var kernel = Path.Combine(kuribo, "System", "KuriboKernel.bin");
        var mainDol = Path.Combine(stagingRoot, "main.dol");
        var bootBin = Path.Combine(stagingRoot, "boot.bin");
        var bseKxe = Path.Combine(stagingRoot, ModuleVersionMessages.BseModuleFileName);
        if (!File.Exists(bseKxe))
            bseKxe = Path.Combine(kuribo, "Mods", ModuleVersionMessages.BseModuleFileName);

        if (!File.Exists(kernel) || !File.Exists(mainDol) || !File.Exists(bootBin) || !File.Exists(bseKxe))
            return false;

        // Soft size checks — warn via existence only; sizes can vary across future releases if URL changes.
        payload = new OfficialBsePayload
        {
            StagingRoot = stagingRoot,
            KuriboRoot = kuribo,
            KernelPath = kernel,
            MainDolPath = mainDol,
            BootBinPath = bootBin,
            BseKxePath = bseKxe,
        };
        return true;
    }

    private static void EnsureLegacyKxeCache(string bseKxePath, Action<string>? log)
    {
        try
        {
            var legacy = GetOfficialBseCachePath();
            if (File.Exists(legacy) && new FileInfo(legacy).Length == OfficialBseSizeBytes)
                return;

            File.Copy(bseKxePath, legacy, overwrite: true);
            log?.Invoke($"Updated legacy BSE .kxe cache at {legacy}");
        }
        catch
        {
            // non-fatal
        }
    }

    private static void CopyDirectoryMerge(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectoryMerge(dir, dest);
        }
    }
}

public readonly struct BundledModuleSyncResult
{
    public int SyncedCount { get; init; }
    public bool BsmsoModuleChanged { get; init; }
    public bool InstalledMatchesBundled { get; init; }
    public bool BundledModuleAvailable { get; init; }
}
