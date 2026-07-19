using SMSO.Launcher;
using SMSO.Net;

namespace SMSO.Tests;

[Collection(nameof(BundledModuleCollection))]
public class ModuleFreshnessTests
{
    [Fact]
    public void GetInstallStatus_CompleteWithoutMarker_NeedsUpdate()
    {
        var root = CreateCompleteGameRoot();
        try
        {
            var status = ModuleInstaller.GetInstallStatus(root);
            Assert.True(status.CanInstall);
            Assert.True(status.NeedsUpdate);
            Assert.Contains("Update module", status.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void GetInstallStatus_CurrentMarkerAndMatchingBundled_IsUpToDate()
    {
        var root = CreateCompleteGameRoot();
        var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
        var payload = new byte[] { 0x42, 0x53, 0x4D, 0x53, 0x4F, 0x02 };
        File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.ModuleFileName), payload);
        File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.MovesetModuleFileName), payload);
        var staged = StageBundled(payload);
        ModuleInstaller.WriteModBuildIdMarker(mods);

        try
        {
            var status = ModuleInstaller.GetInstallStatus(root);
            Assert.False(status.NeedsUpdate);
            Assert.True(status.IsComplete);
            Assert.Contains($"build {ProtocolConstants.ModBuildId}", status.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(ModuleInstaller.GetExtractedModBuildIdMarkerPath(mods)));
            Assert.False(File.Exists(Path.Combine(mods, ModuleVersionMessages.ModBuildIdMarkerFileName)));
        }
        finally
        {
            TryDeleteFile(staged);
            TryDeleteFile(Path.Combine(AppContext.BaseDirectory, ModuleVersionMessages.MovesetModuleFileName));
            TryDelete(root);
        }
    }

    [Fact]
    public void GetInstallStatus_StaleMarker_NeedsUpdate()
    {
        var root = CreateCompleteGameRoot();
        var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
        var marker = ModuleInstaller.GetExtractedModBuildIdMarkerPath(mods);
        var stale = ProtocolConstants.ModBuildId == 0
            ? (ushort)0
            : (ushort)(ProtocolConstants.ModBuildId - 1);
        File.WriteAllText(marker, stale.ToString());

        try
        {
            var status = ModuleInstaller.GetInstallStatus(root);
            Assert.True(status.NeedsUpdate);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void GetInstallStatus_MismatchedBundledKxe_NeedsUpdate()
    {
        var root = CreateCompleteGameRoot();
        var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
        File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.ModuleFileName), new byte[] { 0x4F, 0x4C, 0x44 });
        ModuleInstaller.WriteModBuildIdMarker(mods);
        var staged = StageBundled(new byte[] { 0x4E, 0x45, 0x57 });

        try
        {
            var status = ModuleInstaller.GetInstallStatus(root);
            Assert.True(status.NeedsUpdate);
        }
        finally
        {
            TryDeleteFile(staged);
            TryDeleteFile(Path.Combine(AppContext.BaseDirectory, ModuleVersionMessages.MovesetModuleFileName));
            TryDelete(root);
        }
    }

    [Fact]
    public void IsDiscImageModuleStale_MissingMarker_IsStale()
    {
        var path = Path.Combine(Path.GetTempPath(), "bsmso-fresh-" + Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            File.WriteAllBytes(path, new byte[64]);
            Assert.True(ModuleInstaller.IsDiscImageModuleStale(path));
        }
        finally
        {
            TryDeleteFile(path);
            TryDeleteFile(ModuleInstaller.GetDiscImageModBuildIdMarkerPath(path));
        }
    }

    [Fact]
    public void WriteDiscImageModBuildIdMarker_ClearsStale()
    {
        var path = Path.Combine(Path.GetTempPath(), "bsmso-fresh-" + Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            File.WriteAllBytes(path, new byte[64]);
            ModuleInstaller.WriteDiscImageModBuildIdMarker(path);
            Assert.False(ModuleInstaller.IsDiscImageModuleStale(path));
        }
        finally
        {
            TryDeleteFile(path);
            TryDeleteFile(ModuleInstaller.GetDiscImageModBuildIdMarkerPath(path));
        }
    }

    [Fact]
    public void SyncBundledModulesIntoGame_WritesModBuildIdMarker()
    {
        var root = CreateCompleteGameRoot();
        var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
        var payload = new byte[] { 0x53, 0x59, 0x4E, 0x43 };
        File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.ModuleFileName), payload);
        var marker = ModuleInstaller.GetExtractedModBuildIdMarkerPath(mods);
        if (File.Exists(marker))
            File.Delete(marker);
        // Legacy Mods placement must be purged so Kuribo can boot.
        var legacy = Path.Combine(mods, ModuleVersionMessages.ModBuildIdMarkerFileName);
        File.WriteAllText(legacy, "1");

        var staged = StageBundled(payload);
        try
        {
            var result = ModuleInstaller.SyncBundledModulesIntoGame(root);
            Assert.True(result.InstalledMatchesBundled);
            Assert.True(File.Exists(marker));
            Assert.False(File.Exists(legacy));
            Assert.True(ModuleInstaller.TryReadModBuildIdMarker(marker, out var buildId));
            Assert.Equal(ProtocolConstants.ModBuildId, buildId);
            Assert.False(ModuleInstaller.GetInstallStatus(root).NeedsUpdate);
        }
        finally
        {
            TryDeleteFile(staged);
            TryDeleteFile(Path.Combine(AppContext.BaseDirectory, ModuleVersionMessages.MovesetModuleFileName));
            TryDelete(root);
        }
    }

    [Fact]
    public void WriteModBuildIdMarker_RemovesLegacyModsCopy()
    {
        var root = CreateCompleteGameRoot();
        var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
        var legacy = Path.Combine(mods, ModuleVersionMessages.ModBuildIdMarkerFileName);
        File.WriteAllText(legacy, "9");
        try
        {
            ModuleInstaller.WriteModBuildIdMarker(mods);
            Assert.False(File.Exists(legacy));
            Assert.True(File.Exists(ModuleInstaller.GetExtractedModBuildIdMarkerPath(mods)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateCompleteGameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-fresh-" + Guid.NewGuid().ToString("N"));
        var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
        var system = Path.Combine(root, "files", "Kuribo!", "System");
        var sys = Path.Combine(root, "sys");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(system);
        Directory.CreateDirectory(sys);

        File.WriteAllBytes(Path.Combine(system, "KuriboKernel.bin"), new byte[32]);
        File.WriteAllBytes(Path.Combine(sys, "main.dol"), new byte[ModuleInstaller.OfficialMainDolSizeBytes]);
        File.WriteAllBytes(Path.Combine(sys, "boot.bin"), new byte[ModuleInstaller.OfficialBootBinSizeBytes]);
        File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.BseModuleFileName), new byte[8]);
        File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.MovesetModuleFileName), new byte[8]);
        File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.ModuleFileName), new byte[8]);
        return root;
    }

    private static string StageBundled(byte[] payload)
    {
        var path = Path.Combine(AppContext.BaseDirectory, ModuleVersionMessages.ModuleFileName);
        File.WriteAllBytes(path, payload);
        var moveset = Path.Combine(AppContext.BaseDirectory, ModuleVersionMessages.MovesetModuleFileName);
        File.WriteAllBytes(moveset, payload);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }
}
