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
}
