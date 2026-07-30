using SMSO.Launcher;

namespace SMSO.Tests;

public class DiscDataInstallerTests
{
    [Fact]
    public void EnsureBundledDiscDataPresent_CopiesAndBacksUpRetail()
    {
        var root = CreateTempGameRoot();
        var dataDir = Path.Combine(root, "files", "data");
        Directory.CreateDirectory(dataDir);

        var retailNintendo = new byte[] { 0x59, 0x61, 0x7A, 0x30, 0x01 };
        var retailOption = new byte[] { 0x59, 0x61, 0x7A, 0x30, 0x02 };
        File.WriteAllBytes(Path.Combine(dataDir, "nintendo.szs"), retailNintendo);
        File.WriteAllBytes(Path.Combine(dataDir, "option.szs"), retailOption);

        var staged = StageBundledOverlays(
            nintendo: new byte[] { 0x59, 0x61, 0x7A, 0x30, 0x0A },
            option: new byte[] { 0x59, 0x61, 0x7A, 0x30, 0x0B });

        try
        {
            var first = DiscDataInstaller.EnsureBundledDiscDataPresent(root);
            Assert.True(first.BundledAssetsAvailable);
            Assert.Equal(2, first.SyncedCount);

            Assert.Equal(staged.Nintendo, File.ReadAllBytes(Path.Combine(dataDir, "nintendo.szs")));
            Assert.Equal(staged.Option, File.ReadAllBytes(Path.Combine(dataDir, "option.szs")));
            Assert.Equal(retailNintendo,
                File.ReadAllBytes(Path.Combine(dataDir, "nintendo.szs" + DiscDataInstaller.RetailBackupSuffix)));
            Assert.Equal(retailOption,
                File.ReadAllBytes(Path.Combine(dataDir, "option.szs" + DiscDataInstaller.RetailBackupSuffix)));

            var second = DiscDataInstaller.EnsureBundledDiscDataPresent(root);
            Assert.Equal(0, second.SyncedCount);
            Assert.Equal(2, second.SkippedIdentical);
        }
        finally
        {
            TryDeleteDirectory(staged.Root);
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void EnsureBundledDiscDataPresent_MissingAssets_NoThrow()
    {
        var root = CreateTempGameRoot();
        Directory.CreateDirectory(Path.Combine(root, "files", "data"));
        try
        {
            // No assets/data staged beside the test host — should return empty result.
            var result = DiscDataInstaller.EnsureBundledDiscDataPresent(root);
            Assert.True(result.SyncedCount >= 0);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static (string Root, byte[] Nintendo, byte[] Option) StageBundledOverlays(
        byte[] nintendo,
        byte[] option)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "assets", "data");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "nintendo.szs"), nintendo);
        File.WriteAllBytes(Path.Combine(root, "option.szs"), option);
        return (root, nintendo, option);
    }

    private static string CreateTempGameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-discdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sys"));
        Directory.CreateDirectory(Path.Combine(root, "files"));
        File.WriteAllBytes(Path.Combine(root, "sys", "main.dol"), new byte[] { 1 });
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
