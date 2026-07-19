using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GCNTools;
using SMSO.Net;

namespace SMSO.Launcher;

/// <summary>
/// Extracts a GameCube .iso/.gcm, installs the full BSE / Kuribo runtime plus
/// Moveset and <c>_BSMSO.kxe</c>, rebuilds the disc image in place (with backup),
/// and ensures the GMSE90 game ID patch.
/// </summary>
internal static class DiscImagePatcher
{
    public static async Task<(bool Success, string Message)> PatchAsync(
        string discPath,
        OfficialBsePayload payload,
        string bseSource,
        string movesetSource,
        string bsmsSource,
        Action<string>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discPath) || !File.Exists(discPath))
            return (false, $"Disc image not found:\n{discPath}");

        if (ModuleInstallValidator.IsCompressedDiscImage(discPath))
        {
            return (false,
                "Compressed .gcz disc images are not supported for module install.\n\n" +
                "Convert to .iso or .gcm in Dolphin, or use an extracted game folder.");
        }

        if (!ModuleInstallValidator.IsPatchableDiscImage(discPath))
            return (false, $"Not a patchable disc image (.iso/.gcm):\n{discPath}");

        var fullDiscPath = Path.GetFullPath(discPath.Trim().Trim('"'));
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "smso-disc-patch-" + Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(tempRoot, "extract");
        var rebuiltPath = Path.Combine(tempRoot, "patched" + Path.GetExtension(fullDiscPath));

        try
        {
            Directory.CreateDirectory(extractDir);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Invoke("Backing up disc image…");
            var backupPath = CreateBackup(fullDiscPath, log);
            log?.Invoke($"Disc backup: {backupPath}");

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
                    "Confirm the image is a raw NTSC-U SMS .iso or .gcm.");
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
                log);
            if (!install.Success)
                return install;

            // Embed AppData custom Mario packs so remotes can remount on ISO play.
            progress?.Invoke("Installing custom Mario packs…");
            MarioPackInstaller.EnsureAllLibraryPacksPresent(extractDir, log);

            progress?.Invoke("Rebuilding ISO…");
            log?.Invoke($"Rebuilding disc image…");
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiscImage.CreateFile(extractDir, rebuiltPath);
            }, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(rebuiltPath) || new FileInfo(rebuiltPath).Length < 1024)
                return (false, "Disc rebuild failed — output image was missing or too small.");

            progress?.Invoke("Replacing disc image…");
            ReplaceFileAtomically(rebuiltPath, fullDiscPath, log);

            MarioPackInstaller.RecordAllLibraryPacksOnDisc(fullDiscPath);
            ModuleInstaller.WriteDiscImageModBuildIdMarker(fullDiscPath, log);

            progress?.Invoke("Patching game ID…");
            if (GameIdentity.TryPatchGameId(fullDiscPath, GameIdentity.BsmsGameId, out var patchError))
            {
                log?.Invoke($"Patched disc game ID to {GameIdentity.BsmsGameId}");
            }
            else if (!string.IsNullOrWhiteSpace(patchError))
            {
                return (false,
                    $"BSE runtime was written but game ID patch failed:\n{patchError}\n\n" +
                    $"Disc image:\n{fullDiscPath}\nBackup:\n{backupPath}");
            }
            else
            {
                log?.Invoke($"Disc game ID already {GameIdentity.BsmsGameId}");
            }

            var probe = ModuleInstallValidator.ProbeBseRuntime(extractDir);
            var kernelSize = probe.KernelPath != null ? new FileInfo(probe.KernelPath).Length : 0;
            var mainSize = probe.MainDolPath != null ? new FileInfo(probe.MainDolPath).Length : 0;
            var bootSize = probe.BootBinPath != null ? new FileInfo(probe.BootBinPath).Length : 0;
            var bseSize = probe.BsePath != null ? new FileInfo(probe.BsePath).Length : 0;
            var movesetSize = probe.MovesetPath != null ? new FileInfo(probe.MovesetPath).Length : 0;
            var bsmsSize = probe.BsmsPath != null ? new FileInfo(probe.BsmsPath).Length : 0;
            return (true,
                $"Patched disc image:\n{fullDiscPath}\n\n" +
                $"Backup:\n{backupPath}\n\n" +
                $"KuriboKernel.bin ({kernelSize} bytes)\n" +
                $"sys\\main.dol ({mainSize} bytes)\n" +
                $"sys\\boot.bin ({bootSize} bytes)\n" +
                $"{ModuleVersionMessages.BseModuleFileName} ({bseSize} bytes)\n" +
                $"{ModuleVersionMessages.MovesetModuleFileName} ({movesetSize} bytes)\n" +
                $"{ModuleVersionMessages.ModuleFileName} ({bsmsSize} bytes)\n" +
                $"Game ID: {GameIdentity.BsmsGameId}\n\n" +
                "Restart Dolphin to load the updated modules.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Disc patch cancelled.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to patch disc image:\n{ex.Message}");
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
        var tempReplace = Path.Combine(destDir, Path.GetFileName(destinationPath) + ".smso-new");
        try
        {
            if (File.Exists(tempReplace))
                File.Delete(tempReplace);

            File.Copy(sourcePath, tempReplace, overwrite: true);

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
