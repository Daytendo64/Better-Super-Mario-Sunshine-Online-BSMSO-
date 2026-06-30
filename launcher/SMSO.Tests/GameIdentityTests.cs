using SMSO.Net;

namespace SMSO.Tests;

public sealed class GameIdentityTests
{
    [Fact]
    public void TryResolveBootBinPath_FromMainDol_FindsSiblingBootBin()
    {
        var root = Path.Combine(Path.GetTempPath(), "smso-game-id-" + Guid.NewGuid().ToString("N"));
        var sysDir = Path.Combine(root, "sys");
        Directory.CreateDirectory(sysDir);

        var mainDol = Path.Combine(sysDir, "main.dol");
        var bootBin = Path.Combine(sysDir, "boot.bin");
        File.WriteAllBytes(mainDol, new byte[] { 0x01 });
        File.WriteAllBytes(bootBin, new byte[] { 0x47, 0x4D, 0x53, 0x45, 0x30, 0x31 });

        try
        {
            Assert.True(GameIdentity.TryResolveBootBinPath(mainDol, out var resolved));
            Assert.Equal(bootBin, resolved);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryPatchGameId_BootBin_UpdatesFirstSixBytes()
    {
        var bootBin = Path.Combine(Path.GetTempPath(), "smso-boot-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(bootBin, new byte[] { 0x47, 0x4D, 0x53, 0x45, 0x30, 0x31, 0x00, 0x01 });

        try
        {
            Assert.True(GameIdentity.TryPatchGameId(bootBin, GameIdentity.BsmsGameId, out _));
            Assert.True(GameIdentity.TryReadGameId(bootBin, out var gameId));
            Assert.Equal(GameIdentity.BsmsGameId, gameId);
            Assert.False(GameIdentity.TryPatchGameId(bootBin, GameIdentity.BsmsGameId, out _));
        }
        finally
        {
            File.Delete(bootBin);
        }
    }

    [Fact]
    public void TryPatchGameId_DiscImage_UpdatesDiscHeader()
    {
        var discPath = Path.Combine(Path.GetTempPath(), "smso-disc-" + Guid.NewGuid().ToString("N") + ".gcm");
        var bytes = new byte[64];
        Array.Copy(System.Text.Encoding.ASCII.GetBytes(GameIdentity.VanillaNtscUGameId), bytes, 6);
        File.WriteAllBytes(discPath, bytes);

        try
        {
            Assert.True(GameIdentity.TryPatchGameId(discPath, GameIdentity.BsmsGameId, out _));
            Assert.True(GameIdentity.TryReadGameId(discPath, out var gameId));
            Assert.Equal(GameIdentity.BsmsGameId, gameId);
        }
        finally
        {
            File.Delete(discPath);
        }
    }
}
