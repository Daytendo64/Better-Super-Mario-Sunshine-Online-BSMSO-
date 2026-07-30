using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GCNTools;
using SMSO.Net;

namespace SMSO.Launcher;

/// <summary>
/// Extracts a GameCube .iso/.gcm, installs the full BSE / Kuribo runtime plus
/// Moveset and <c>_BSMSO.kxe</c>, rebuilds the disc image (default: in place with
/// backup; optional <paramref name="outputDiscPath"/> writes a new file and leaves
/// the source unchanged), and ensures the GMSE90 game ID patch.
/// </summary>
internal static class DiscImagePatcher
{
    public static async Task<(bool Success, string Message, bool ModelsWarning)> PatchAsync(
        string discPath,
        OfficialBsePayload payload,
        string bseSource,
        string? movesetSource,
        string bsmsSource,
        Action<string>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool patchBseMoveset = false,
        string? outputDiscPath = null)
    {
        if (string.IsNullOrWhiteSpace(discPath) || !File.Exists(discPath))
            return (false, $"Disc image not found:\n{discPath}", false);

        if (ModuleInstallValidator.IsCompressedDiscImage(discPath))
        {
            return (false,
                "Compressed .gcz disc images are not supported for module install.\n\n" +
                "Convert to .iso or .gcm in Dolphin, or use an extracted game folder.",
                false);
        }

        if (!ModuleInstallValidator.IsPatchableDiscImage(discPath))
            return (false, $"Not a patchable disc image (.iso/.gcm):\n{discPath}", false);

        var fullDiscPath = Path.GetFullPath(discPath.Trim().Trim('"'));
        string finalDiscPath;
        try
        {
            finalDiscPath = string.IsNullOrWhiteSpace(outputDiscPath)
                ? fullDiscPath
                : Path.GetFullPath(outputDiscPath.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            return (false, $"Invalid patched disc save path:\n{ex.Message}", false);
        }

        if (!IsSupportedDiscExtension(finalDiscPath))
        {
            return (false,
                "Patched disc must be saved as .iso or .gcm.\n\n" +
                $"Chosen path:\n{finalDiscPath}",
                false);
        }

        var writeInPlace = string.Equals(fullDiscPath, finalDiscPath, StringComparison.OrdinalIgnoreCase);
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "smso-disc-patch-" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(tempRoot, "extract");
        var rebuiltPath = Path.Combine(tempRoot, "patched" + Path.GetExtension(finalDiscPath));

        try
        {
            Directory.CreateDirectory(extractDir);
            cancellationToken.ThrowIfCancellationRequested();

            string? backupPath = null;
            if (writeInPlace)
            {
                progress?.Invoke("Backing up disc image…");
                backupPath = CreateBackup(fullDiscPath, log);
                log?.Invoke($"Disc backup: {backupPath}");
            }
            else
            {
                log?.Invoke($"Source disc (unchanged): {fullDiscPath}");
                log?.Invoke($"Patched disc will be written to: {finalDiscPath}");
                var outDir = Path.GetDirectoryName(finalDiscPath);
                if (!string.IsNullOrWhiteSpace(outDir))
                    Directory.CreateDirectory(outDir);
            }

            progress?.Invoke("Extracting…");
            log?.Invoke($"Extracting disc image to temporary folder…");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new FileStream(
                    fullDiscPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                using var image = new DiscImage(stream);
                image.ExtractToDirectory(extractDir, ExtractionType.ALL);
            }, cancellationToken).ConfigureAwait(false);

            if (!ModuleInstallValidator.IsValidExtractedGameRoot(extractDir))
            {
                return (false,
                    "Disc extract did not produce a valid GameCube tree (sys\\ / files\\).\n" +
                    "Confirm the image is a raw NTSC-U SMS .iso or .gcm.",
                    false);
            }

            progress?.Invoke("Installing BSE runtime…");
            // Patch GMSE90 on the rebuilt disc header below; keep BSE boot.bin as shipped here.
            var install = ModuleInstaller.InstallBseRuntimeIntoGameRoot(
                extractDir,
                payload,
                bseSource,
                movesetSource,
                bsmsSource,
                patchGameId: false,
                log,
                patchBseMoveset);
            if (!install.Success)
                return (install.Success, install.Message, false);

            // Fail closed before rebuilding the ISO if sizes/presence are wrong.
            var preRebuildProbe = ModuleInstallValidator.ProbeBseRuntime(extractDir);
            var sizeCheck = ModuleInstallValidator.ValidateInstalledRuntimeSizes(
                preRebuildProbe, patchBseMoveset);
            if (sizeCheck != null)
                return (false, sizeCheck, false);

            if (preRebuildProbe.BsmsPath == null ||
                !ModuleInstaller.FilesMatch(bsmsSource, preRebuildProbe.BsmsPath))
            {
                return (false,
                    $"{ModuleVersionMessages.ModuleFileName} in the extracted disc tree does not match the " +
                    "Install source — refusing to rebuild the ISO or stamp a build marker.\n" +
                    $"Source:\n{bsmsSource}\nExtracted:\n{preRebuildProbe.BsmsPath}",
                    false);
            }

            // Title / options UI archives (assets/data → files/data).
            progress?.Invoke("Installing disc data overlays…");
            DiscDataInstaller.EnsureBundledDiscDataPresent(extractDir, log);

            // Embed AppData custom Mario packs so remotes can remount on ISO play.
            // Caller (InstallAsync) seeds AppData first; still report incomplete packs.
            progress?.Invoke("Installing custom Mario packs…");
            var packSync = MarioPackInstaller.EnsureAllLibraryPacksPresentDetailed(extractDir, log);

            // Match extracted-folder Install: never inject better_movement.prm (release
            // only shipped Moveset.kxe). Strip leftovers so Moveset-ON stays release-feel.
            progress?.Invoke("Ensuring release-matching movement params…");
            MarioPackInstaller.RemoveBetterMovementPrm(extractDir, log);

            progress?.Invoke("Rebuilding ISO…");
            log?.Invoke($"Rebuilding disc image…");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiscImage.CreateFile(extractDir, rebuiltPath);
            }, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(rebuiltPath) || new FileInfo(rebuiltPath).Length < 1024)
                return (false, "Disc rebuild failed — output image was missing or too small.", packSync.HasWarning);

            progress?.Invoke(writeInPlace ? "Replacing disc image…" : "Saving patched disc image…");
            ReplaceFileAtomically(rebuiltPath, finalDiscPath, log);

            // Only record packs that were actually written into the extract — never
            // stamp the full AppData library as present when install skipped/failed.
            MarioPackInstaller.RecordPacksPresentOnExtract(finalDiscPath, extractDir);
            ModuleInstaller.WriteDiscImageModBuildIdMarker(finalDiscPath, log);

            progress?.Invoke("Patching game ID…");
            if (GameIdentity.TryPatchGameId(finalDiscPath, GameIdentity.BsmsGameId, out var patchError))
            {
                log?.Invoke($"Patched disc game ID to {GameIdentity.BsmsGameId}");
            }
            else if (!string.IsNullOrWhiteSpace(patchError))
            {
                var backupLine = backupPath != null ? $"\nBackup:\n{backupPath}" : string.Empty;
                return (false,
                    $"BSE runtime was written but game ID patch failed:\n{patchError}\n\n" +
                    $"Disc image:\n{finalDiscPath}{backupLine}",
                    packSync.HasWarning);
            }
            else
            {
                log?.Invoke($"Disc game ID already {GameIdentity.BsmsGameId}");
            }

            var probe = preRebuildProbe;
            var kernelSize = probe.KernelPath != null ? new FileInfo(probe.KernelPath).Length : 0;
            var mainSize = probe.MainDolPath != null ? new FileInfo(probe.MainDolPath).Length : 0;
            var bootSize = probe.BootBinPath != null ? new FileInfo(probe.BootBinPath).Length : 0;
            var bseSize = probe.BsePath != null ? new FileInfo(probe.BsePath).Length : 0;
            var movesetSize = probe.MovesetPath != null ? new FileInfo(probe.MovesetPath).Length : 0;
            var bsmsSize = probe.BsmsPath != null ? new FileInfo(probe.BsmsPath).Length : 0;
            var movesetLine = probe.MovesetInstalled
                ? $"{ModuleVersionMessages.MovesetModuleFileName} ({movesetSize} bytes)\n"
                : $"{ModuleVersionMessages.MovesetModuleFileName}: skipped\n";
            var backupSection = backupPath != null
                ? $"Backup:\n{backupPath}\n\n"
                : $"Source (unchanged):\n{fullDiscPath}\n\n";
            var message =
                $"Patched disc image:\n{finalDiscPath}\n\n" +
                backupSection +
                $"KuriboKernel.bin ({kernelSize} bytes)\n" +
                $"sys\\main.dol ({mainSize} bytes)\n" +
                $"sys\\boot.bin ({bootSize} bytes)\n" +
                $"{ModuleVersionMessages.BseModuleFileName} ({bseSize} bytes)\n" +
                movesetLine +
                $"{ModuleVersionMessages.ModuleFileName} ({bsmsSize} bytes)\n" +
                $"Game ID: {GameIdentity.BsmsGameId}\n\n" +
                "Restart Dolphin to load the updated modules.";
            message = ModuleInstaller.AppendPackSyncMessage(message, packSync);
            return (true, message, packSync.HasWarning);
        }
        catch (OperationCanceledException)
        {
            return (false, "Disc patch cancelled.", false);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to patch disc image:\n{ex.Message}", false);
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
                // ignore temp cleanup failures
            }
        }
    }

    /// <summary>Suggested save name: <c>Game_BSMSO.iso</c> next to the source image.</summary>
    public static string SuggestPatchedDiscFileName(string sourceDiscPath)
    {
        var full = Path.GetFullPath(sourceDiscPath.Trim().Trim('"'));
        var dir = Path.GetDirectoryName(full) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".iso";
        if (stem.EndsWith("_BSMSO", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(dir, stem + ext);
        return Path.Combine(dir, stem + "_BSMSO" + ext);
    }

    /// <summary>
    /// Eclipse additive disc path: extract → add <c>_BSMSO.kxe</c> + Mario packs → rebuild
    /// in place (caller already made the backup). Eclipse's BSE / Moveset / main.dol /
    /// boot.bin and the GMSE04 game id are never modified.
    /// </summary>
    public static async Task<(bool Success, string Message, bool ModelsWarning)> PatchEclipseAdditiveAsync(
        string discPath,
        string bsmsSource,
        Action<string>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discPath) || !File.Exists(discPath))
            return (false, $"Disc image not found:\n{discPath}", false);

        if (!ModuleInstallValidator.IsPatchableDiscImage(discPath))
            return (false, $"Not a patchable disc image (.iso/.gcm):\n{discPath}", false);

        var fullDiscPath = Path.GetFullPath(discPath.Trim().Trim('"'));
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "smso-eclipse-disc-patch-" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(tempRoot, "extract");
        var rebuiltPath = Path.Combine(tempRoot, "patched" + Path.GetExtension(fullDiscPath));

        try
        {
            Directory.CreateDirectory(extractDir);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Invoke("Extracting Eclipse disc…");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new FileStream(
                    fullDiscPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                using var image = new DiscImage(stream);
                image.ExtractToDirectory(extractDir, ExtractionType.ALL);
            }, cancellationToken).ConfigureAwait(false);

            if (!ModuleInstallValidator.IsValidExtractedGameRoot(extractDir))
            {
                return (false,
                    "Disc extract did not produce a valid GameCube tree (sys\\ / files\\).\n" +
                    "Confirm the image is a raw Super Mario Eclipse .iso or .gcm.",
                    false);
            }

            var eclipseMods = Path.Combine(extractDir, "files", "Kuribo!", "Mods");
            var eclipseModule = Path.Combine(eclipseMods, GameProfileDetector.EclipseModuleFileName);
            if (!File.Exists(eclipseModule))
            {
                return (false,
                    $"{GameProfileDetector.EclipseModuleFileName} not found inside the disc image.\n" +
                    "This does not look like a Super Mario Eclipse disc — refusing to patch.",
                    false);
            }

            progress?.Invoke("Installing BSMSO online module (additive)…");
            Directory.CreateDirectory(eclipseMods);
            var bsmsDest = Path.Combine(eclipseMods, ModuleVersionMessages.ModuleFileName);
            File.Copy(bsmsSource, bsmsDest, overwrite: true);
            log?.Invoke(
                $"Installed {ModuleVersionMessages.ModuleFileName} ({new FileInfo(bsmsDest).Length} bytes) into Eclipse disc tree");
            if (!ModuleInstaller.FilesMatch(bsmsSource, bsmsDest))
            {
                return (false,
                    $"{ModuleVersionMessages.ModuleFileName} copy verification failed in the extracted disc tree — refusing to rebuild.",
                    false);
            }

            progress?.Invoke("Installing custom Mario packs…");
            var packSync = MarioPackInstaller.EnsureAllLibraryPacksPresentDetailed(extractDir, log);

            progress?.Invoke("Rebuilding ISO…");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiscImage.CreateFile(extractDir, rebuiltPath);
            }, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(rebuiltPath) || new FileInfo(rebuiltPath).Length < 1024)
                return (false, "Disc rebuild failed — output image was missing or too small.", packSync.HasWarning);

            progress?.Invoke("Replacing disc image…");
            ReplaceFileAtomically(rebuiltPath, fullDiscPath, log);

            MarioPackInstaller.RecordPacksPresentOnExtract(fullDiscPath, extractDir);
            ModuleInstaller.WriteDiscImageModBuildIdMarker(fullDiscPath, log);

            var message =
                $"Patched Super Mario Eclipse disc image (additive):\n{fullDiscPath}\n\n" +
                $"Backup:\n{fullDiscPath}.bsmso-backup\n\n" +
                $"{ModuleVersionMessages.ModuleFileName} ({new FileInfo(bsmsSource).Length} bytes)\n" +
                "Untouched: Eclipse BSE / Moveset / SuperMarioEclipse.kxe, main.dol, boot.bin, game id (GMSE04).\n\n" +
                "Restart Dolphin to load the updated modules.";
            message = ModuleInstaller.AppendPackSyncMessage(message, packSync);
            return (true, message, packSync.HasWarning);
        }
        catch (OperationCanceledException)
        {
            return (false, "Disc patch cancelled.", false);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to patch Eclipse disc image:\n{ex.Message}", false);
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
                // ignore temp cleanup failures
            }
        }
    }

    private static bool IsSupportedDiscExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gcm", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateBackup(string discPath, Action<string>? log)
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
        log?.Invoke($"Existing .bak found — wrote timestamped backup instead.");
        return stamped;
    }

    private static void ReplaceFileAtomically(string sourcePath, string destinationPath, Action<string>? log)
    {
        var destDir = Path.GetDirectoryName(destinationPath)
                      ?? throw new InvalidOperationException("Disc path has no directory.");
        Directory.CreateDirectory(destDir);
        var tempReplace = Path.Combine(destDir, Path.GetFileName(destinationPath) + ".smso-new");
        try
        {
            if (File.Exists(tempReplace))
                File.Delete(tempReplace);

            File.Copy(sourcePath, tempReplace, overwrite: true);

            if (!File.Exists(destinationPath))
            {
                File.Move(tempReplace, destinationPath);
                log?.Invoke($"Wrote patched disc image to {destinationPath}");
                return;
            }

            // Replace may fail across volumes; fall back to delete+move.
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
}
