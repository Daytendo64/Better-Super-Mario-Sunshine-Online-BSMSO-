using SMSO.Launcher;
using SMSO.Net;

namespace SMSO.Tests;

public sealed class GameProfileDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsmso-profile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    [Fact]
    public void Detect_EmptyPath_ReturnsUnknown()
    {
        var profile = GameProfileDetector.Detect(null);
        Assert.Equal(GameProfileKind.Unknown, profile.Kind);
        Assert.Equal(GameProfileId.Unspecified, profile.Id);

        Assert.Equal(GameProfileKind.Unknown, GameProfileDetector.Detect("   ").Kind);
    }

    [Fact]
    public void Detect_VanillaExtractedTree_ReturnsVanilla()
    {
        var gameRoot = CreateExtractedTree(GameIdentity.VanillaNtscUGameId);

        var profile = GameProfileDetector.Detect(gameRoot);

        Assert.Equal(GameProfileKind.VanillaSms, profile.Kind);
        Assert.Equal(GameProfileId.VanillaSms, profile.Id);
        Assert.False(profile.IsEclipse);
        Assert.Equal(GameIdentity.VanillaNtscUGameId, profile.GameId);
    }

    [Fact]
    public void Detect_BsmsPatchedTree_WithoutEclipseModule_ReturnsVanilla()
    {
        var gameRoot = CreateExtractedTree(GameIdentity.BsmsGameId);

        var profile = GameProfileDetector.Detect(gameRoot);

        Assert.Equal(GameProfileKind.VanillaSms, profile.Kind);
        Assert.False(profile.IsEclipse);
    }

    [Fact]
    public void Detect_EclipseModuleTree_ReturnsEclipse_NotBlocked()
    {
        var gameRoot = CreateEclipseTree(GameIdentity.BsmsGameId);

        var profile = GameProfileDetector.Detect(gameRoot);

        Assert.Equal(GameProfileKind.MarioEclipse, profile.Kind);
        Assert.Equal(GameProfileId.MarioEclipse, profile.Id);
        Assert.True(profile.IsEclipse);
        Assert.Empty(profile.BlockingIssues);
        Assert.Contains(profile.Evidence, e => e.Contains(GameProfileDetector.EclipseModuleFileName));
        Assert.Contains("additive", profile.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Detect_EclipseDiscId_ReturnsEclipse_BlocksWhenKuriboMissing()
    {
        // Eclipse ISO header id (GMSE04) but no module files on disk yet.
        var gameRoot = CreateExtractedTree(GameIdentity.MarioEclipseGameId);

        var profile = GameProfileDetector.Detect(gameRoot);

        Assert.Equal(GameProfileKind.MarioEclipse, profile.Kind);
        Assert.True(profile.IsEclipse);
        Assert.Equal(GameIdentity.MarioEclipseGameId, profile.GameId);
        Assert.NotEmpty(profile.BlockingIssues);
    }

    [Fact]
    public void Detect_EclipseTree_MissingKuriboKernel_IsBlocked()
    {
        var gameRoot = CreateEclipseTree(GameIdentity.BsmsGameId, includeKuriboKernel: false);

        var profile = GameProfileDetector.Detect(gameRoot);

        Assert.True(profile.IsEclipse);
        Assert.Contains(profile.BlockingIssues, i => i.Contains("KuriboKernel"));
    }

    [Fact]
    public void Detect_EclipseTree_MissingEclipseBse_IsBlocked()
    {
        var gameRoot = CreateEclipseTree(GameIdentity.BsmsGameId, includeEclipseBse: false);

        var profile = GameProfileDetector.Detect(gameRoot);

        Assert.True(profile.IsEclipse);
        Assert.Contains(profile.BlockingIssues, i => i.Contains(ModuleVersionMessages.BseModuleFileName));
    }

    [Fact]
    public void EclipseInstallStatus_BlockedTree_CannotInstall()
    {
        var gameRoot = CreateEclipseTree(GameIdentity.BsmsGameId, includeKuriboKernel: false);

        var status = ModuleInstaller.GetInstallStatus(gameRoot);

        Assert.False(status.CanInstall);
        Assert.Equal(ModuleInstallTargetKind.ExtractedFolder, status.TargetKind);
    }

    [Fact]
    public void EclipseInstallStatus_HealthyTree_CanInstall_ReportsAdditive()
    {
        var gameRoot = CreateEclipseTree(GameIdentity.BsmsGameId);

        var status = ModuleInstaller.GetInstallStatus(gameRoot);

        Assert.True(status.CanInstall);
        Assert.Equal(ModuleInstallTargetKind.ExtractedFolder, status.TargetKind);
        Assert.Contains("additive", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VanillaInstallStatus_UnchangedByDetector()
    {
        var gameRoot = CreateExtractedTree(GameIdentity.VanillaNtscUGameId);

        var status = ModuleInstaller.GetInstallStatus(gameRoot);

        Assert.True(status.CanInstall);
        Assert.DoesNotContain("Eclipse", status.Message);
    }

    [Fact]
    public void ValidateInstalledModule_EclipseTree_IgnoresEclipseBseAndMovesetSizes()
    {
        // Eclipse ships BSE 571104 / Moveset 43520 — the vanilla official-size pins
        // (583744 / 46976) must not block launching an Eclipse tree.
        var gameRoot = CreateEclipseTree(GameIdentity.MarioEclipseGameId);
        var modsDir = Path.Combine(gameRoot, "files", "Kuribo!", "Mods");
        File.WriteAllBytes(
            Path.Combine(modsDir, ModuleVersionMessages.MovesetModuleFileName),
            new byte[(int)GameProfileDetector.EclipseMovesetSizeBytes]);
        File.WriteAllBytes(
            Path.Combine(modsDir, ModuleVersionMessages.ModuleFileName), new byte[48]);

        Assert.Null(ModuleInstallValidator.ValidateInstalledModule(gameRoot));
    }

    [Fact]
    public void ValidateInstalledModule_EclipseTree_StillRequiresBsmsModule()
    {
        var gameRoot = CreateEclipseTree(GameIdentity.MarioEclipseGameId);

        var error = ModuleInstallValidator.ValidateInstalledModule(gameRoot);

        Assert.NotNull(error);
        Assert.Contains(ModuleVersionMessages.ModuleFileName, error);
    }

    [Fact]
    public void ValidateBootReadyModules_EclipseTree_ReturnsNull()
    {
        var gameRoot = CreateEclipseTree(GameIdentity.MarioEclipseGameId);

        Assert.Null(ModuleInstallValidator.ValidateBootReadyModules(gameRoot, patchBseMoveset: false));
        Assert.Null(ModuleInstallValidator.ValidateBootReadyModules(gameRoot, patchBseMoveset: true));
    }

    private string CreateExtractedTree(string gameId)
    {
        var gameRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var sysDir = Path.Combine(gameRoot, "sys");
        Directory.CreateDirectory(sysDir);
        Directory.CreateDirectory(Path.Combine(gameRoot, "files"));
        WriteBootBin(Path.Combine(sysDir, "boot.bin"), gameId);
        File.WriteAllBytes(Path.Combine(sysDir, "main.dol"), new byte[32]);
        return gameRoot;
    }

    private string CreateEclipseTree(string gameId, bool includeKuriboKernel = true, bool includeEclipseBse = true)
    {
        var gameRoot = CreateExtractedTree(gameId);
        var modsDir = Path.Combine(gameRoot, "files", "Kuribo!", "Mods");
        var systemDir = Path.Combine(gameRoot, "files", "Kuribo!", "System");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(systemDir);

        File.WriteAllBytes(Path.Combine(modsDir, GameProfileDetector.EclipseModuleFileName), new byte[64]);
        if (includeEclipseBse)
            File.WriteAllBytes(Path.Combine(modsDir, ModuleVersionMessages.BseModuleFileName), new byte[64]);
        if (includeKuriboKernel)
            File.WriteAllBytes(Path.Combine(systemDir, "KuriboKernel.bin"), new byte[16]);
        return gameRoot;
    }

    private static void WriteBootBin(string path, string gameId)
    {
        var bytes = new byte[1088];
        System.Text.Encoding.ASCII.GetBytes(gameId).CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
    }
}
