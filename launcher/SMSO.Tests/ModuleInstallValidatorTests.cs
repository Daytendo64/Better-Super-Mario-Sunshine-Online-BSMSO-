using SMSO.Launcher;
using SMSO.Net;

namespace SMSO.Tests;

public class ModuleInstallValidatorTests
{
    [Fact]
    public void DiscImageContainsModuleFile_FindsNeedleNearStart()
    {
        var path = Path.Combine(Path.GetTempPath(), "bsmso-disc-scan-" + Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            var bytes = new byte[64 * 1024];
            var needle = System.Text.Encoding.ASCII.GetBytes(ModuleVersionMessages.ModuleFileName);
            needle.CopyTo(bytes, 1200);
            File.WriteAllBytes(path, bytes);

            Assert.True(ModuleInstallValidator.DiscImageContainsModuleFile(
                path, ModuleVersionMessages.ModuleFileName));
            Assert.False(ModuleInstallValidator.DiscImageContainsModuleFile(
                path, "definitely-not-present.kxe"));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ValidateInstalledModule_BareDiscWithoutModule_ReturnsWarning()
    {
        var path = Path.Combine(Path.GetTempPath(), "bsmso-empty-disc-" + Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            var warning = ModuleInstallValidator.ValidateInstalledModule(path);
            Assert.NotNull(warning);
            Assert.Contains(ModuleVersionMessages.ModuleFileName, warning, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ValidateInstalledModule_BareDiscWithModuleName_Passes()
    {
        var path = Path.Combine(Path.GetTempPath(), "bsmso-patched-disc-" + Guid.NewGuid().ToString("N") + ".iso");
        try
        {
            var bytes = new byte[8192];
            System.Text.Encoding.ASCII.GetBytes(ModuleVersionMessages.ModuleFileName).CopyTo(bytes, 100);
            File.WriteAllBytes(path, bytes);

            Assert.Null(ModuleInstallValidator.ValidateInstalledModule(path));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void OfficialModuleSizes_MatchKnownGoodReleaseBinaries()
    {
        // Guards against reintroducing the 2026-07-11 black-screen sizes
        // (DEBUG BSE 603424 / broken Moveset 44992).
        Assert.Equal(583_744, ModuleInstaller.OfficialBseSizeBytes);
        Assert.Equal(46_976, ModuleInstaller.OfficialMovesetSizeBytes);
    }

    [Fact]
    public void ValidateInstalledRuntimeSizes_RejectsDebugBse()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-size-val-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
            var system = Path.Combine(root, "files", "Kuribo!", "System");
            var sys = Path.Combine(root, "sys");
            Directory.CreateDirectory(mods);
            Directory.CreateDirectory(system);
            Directory.CreateDirectory(sys);

            File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.BseModuleFileName), new byte[603_424]);
            File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.ModuleFileName), new byte[100]);
            File.WriteAllBytes(Path.Combine(system, "KuriboKernel.bin"), new byte[ModuleInstaller.OfficialKuriboKernelSizeBytes]);
            File.WriteAllBytes(Path.Combine(sys, "main.dol"), new byte[ModuleInstaller.OfficialMainDolSizeBytes]);
            File.WriteAllBytes(Path.Combine(sys, "boot.bin"), new byte[ModuleInstaller.OfficialBootBinSizeBytes]);

            var probe = ModuleInstallValidator.ProbeBseRuntime(root);
            Assert.False(probe.BseInstalled);
            var error = ModuleInstallValidator.ValidateInstalledRuntimeSizes(probe, patchBseMovesetExpected: false);
            Assert.NotNull(error);
            Assert.Contains("603424", error, StringComparison.Ordinal);
            Assert.Contains(ModuleVersionMessages.BseModuleFileName, error, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ValidateInstalledRuntimeSizes_RejectsWrongMovesetSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-moveset-val-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
            var system = Path.Combine(root, "files", "Kuribo!", "System");
            var sys = Path.Combine(root, "sys");
            Directory.CreateDirectory(mods);
            Directory.CreateDirectory(system);
            Directory.CreateDirectory(sys);

            File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.BseModuleFileName),
                new byte[ModuleInstaller.OfficialBseSizeBytes]);
            File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.MovesetModuleFileName), new byte[44_992]);
            File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.ModuleFileName), new byte[100]);
            File.WriteAllBytes(Path.Combine(system, "KuriboKernel.bin"),
                new byte[ModuleInstaller.OfficialKuriboKernelSizeBytes]);
            File.WriteAllBytes(Path.Combine(sys, "main.dol"),
                new byte[ModuleInstaller.OfficialMainDolSizeBytes]);
            File.WriteAllBytes(Path.Combine(sys, "boot.bin"),
                new byte[ModuleInstaller.OfficialBootBinSizeBytes]);

            var probe = ModuleInstallValidator.ProbeBseRuntime(root);
            var error = ModuleInstallValidator.ValidateInstalledRuntimeSizes(probe, patchBseMovesetExpected: true);
            Assert.NotNull(error);
            Assert.Contains("44992", error, StringComparison.Ordinal);
            Assert.Contains(ModuleVersionMessages.MovesetModuleFileName, error, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ValidateInstalledRuntimeSizes_RejectsLeftoverMovesetWhenToggleOff()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-moveset-off-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mods = Path.Combine(root, "files", "Kuribo!", "Mods");
            var system = Path.Combine(root, "files", "Kuribo!", "System");
            var sys = Path.Combine(root, "sys");
            Directory.CreateDirectory(mods);
            Directory.CreateDirectory(system);
            Directory.CreateDirectory(sys);

            File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.BseModuleFileName),
                new byte[ModuleInstaller.OfficialBseSizeBytes]);
            File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.MovesetModuleFileName),
                new byte[ModuleInstaller.OfficialMovesetSizeBytes]);
            File.WriteAllBytes(Path.Combine(mods, ModuleVersionMessages.ModuleFileName), new byte[100]);
            File.WriteAllBytes(Path.Combine(system, "KuriboKernel.bin"),
                new byte[ModuleInstaller.OfficialKuriboKernelSizeBytes]);
            File.WriteAllBytes(Path.Combine(sys, "main.dol"),
                new byte[ModuleInstaller.OfficialMainDolSizeBytes]);
            File.WriteAllBytes(Path.Combine(sys, "boot.bin"),
                new byte[ModuleInstaller.OfficialBootBinSizeBytes]);

            var probe = ModuleInstallValidator.ProbeBseRuntime(root);
            var error = ModuleInstallValidator.ValidateInstalledRuntimeSizes(probe, patchBseMovesetExpected: false);
            Assert.NotNull(error);
            Assert.Contains("Patch BSE moveset is OFF", error, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
