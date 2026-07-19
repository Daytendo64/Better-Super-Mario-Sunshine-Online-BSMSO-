using SMSO.Launcher;
using SMSO.Net;

namespace SMSO.Tests;

/// <summary>
/// Bundled-module tests stage <c>_BSMSO.kxe</c> into the shared test output directory;
/// run them serially so they do not clobber each other.
/// </summary>
[CollectionDefinition(nameof(BundledModuleCollection), DisableParallelization = true)]
public class BundledModuleCollection;

[Collection(nameof(BundledModuleCollection))]
public class BundledModuleSyncTests
{
    [Fact]
    public void SyncBundledModulesIntoGame_IdenticalBytes_NoChange()
    {
        var root = CreateTempGameRoot();
        var modsDir = Path.Combine(root, "files", "Kuribo!", "Mods");
        Directory.CreateDirectory(modsDir);

        var payload = new byte[] { 0x42, 0x53, 0x4D, 0x53, 0x4F, 0x01 };
        var stagedSource = StageBundledModuleBesideTests(payload);
        var destPath = Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName);
        File.WriteAllBytes(destPath, payload);

        try
        {
            var first = ModuleInstaller.SyncBundledModulesIntoGame(root);
            Assert.True(first.BundledModuleAvailable);
            Assert.True(first.InstalledMatchesBundled);
            Assert.False(first.BsmsoModuleChanged);

            var second = ModuleInstaller.SyncBundledModulesIntoGame(root);
            Assert.True(second.InstalledMatchesBundled);
            Assert.False(second.BsmsoModuleChanged);
        }
        finally
        {
            TryDeleteFile(stagedSource);
            TryDelete(root);
        }
    }

    [Fact]
    public void SyncBundledModulesIntoGame_DifferentBytes_CopiesAndFlagsChanged()
    {
        var root = CreateTempGameRoot();
        var modsDir = Path.Combine(root, "files", "Kuribo!", "Mods");
        Directory.CreateDirectory(modsDir);

        var bundled = new byte[] { 0x4E, 0x45, 0x57, 0x01 };
        var installed = new byte[] { 0x4F, 0x4C, 0x44, 0x00 };
        var stagedSource = StageBundledModuleBesideTests(bundled);
        File.WriteAllBytes(Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName), installed);

        try
        {
            var result = ModuleInstaller.SyncBundledModulesIntoGame(root);
            Assert.True(result.BundledModuleAvailable);
            Assert.True(result.BsmsoModuleChanged);
            Assert.True(result.InstalledMatchesBundled);
            Assert.True(result.SyncedCount >= 1);
            Assert.Equal(bundled, File.ReadAllBytes(Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName)));
        }
        finally
        {
            TryDeleteFile(stagedSource);
            TryDelete(root);
        }
    }

    /// <summary>
    /// Place a fake bundled module in the test output directory so
    /// <see cref="ModuleInstaller.TryFindSourceModule"/> finds it first.
    /// </summary>
    private static string StageBundledModuleBesideTests(byte[] payload)
    {
        var path = Path.Combine(AppContext.BaseDirectory, ModuleVersionMessages.ModuleFileName);
        File.WriteAllBytes(path, payload);
        return path;
    }

    private static string CreateTempGameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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
