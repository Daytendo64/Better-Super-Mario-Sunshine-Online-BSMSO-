using SMSO.Launcher;
using SMSO.Net;

namespace SMSO.Tests;

public class LauncherUpdateCheckerTests
{
    [Fact]
    public void ResolveBundledManifestPath_FindsRootThenAssets()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-update-" + Guid.NewGuid().ToString("N"));
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);
        try
        {
            Assert.Null(LauncherUpdateChecker.ResolveBundledManifestPath(root));

            var assetsPath = Path.Combine(assets, LauncherUpdateManifest.FileName);
            File.WriteAllText(assetsPath, """{"modBuildId":1}""");
            Assert.Equal(assetsPath, LauncherUpdateChecker.ResolveBundledManifestPath(root));

            var rootPath = Path.Combine(root, LauncherUpdateManifest.FileName);
            File.WriteAllText(rootPath, """{"modBuildId":2}""");
            Assert.Equal(rootPath, LauncherUpdateChecker.ResolveBundledManifestPath(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_UsesBundledJsonWhenNoUrlOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("BSMSO_UPDATE_MANIFEST_URL");
        try
        {
            Environment.SetEnvironmentVariable("BSMSO_UPDATE_MANIFEST_URL", null);

            // Point AppContext.BaseDirectory isn't overridable; exercise file evaluate
            // via ResolveBundledManifestPath + direct parse path used by CheckAsync.
            var newer = (ushort)(ProtocolConstants.ModBuildId + 1);
            var json = LauncherUpdateManifest.BuildJson(newer, versionLabel: "test");
            var path = Path.Combine(root, LauncherUpdateManifest.FileName);
            await File.WriteAllTextAsync(path, json);

            Assert.Equal(path, LauncherUpdateChecker.ResolveBundledManifestPath(root));
            Assert.True(LauncherUpdateManifest.TryParse(json, out var manifest));
            Assert.True(manifest!.IsNewerThan(ProtocolConstants.ModBuildId));
            Assert.Null(LauncherUpdateChecker.ResolveManifestUrl());
            Assert.Null(LauncherUpdateChecker.ResolveManifestUrl(""));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSMSO_UPDATE_MANIFEST_URL", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveManifestUrl_PrefersEnvThenConfig()
    {
        var previous = Environment.GetEnvironmentVariable("BSMSO_UPDATE_MANIFEST_URL");
        try
        {
            Environment.SetEnvironmentVariable("BSMSO_UPDATE_MANIFEST_URL", null);
            Assert.Equal("https://example.com/m.json",
                LauncherUpdateChecker.ResolveManifestUrl("https://example.com/m.json"));

            Environment.SetEnvironmentVariable(
                "BSMSO_UPDATE_MANIFEST_URL", "https://env.example/m.json");
            Assert.Equal("https://env.example/m.json",
                LauncherUpdateChecker.ResolveManifestUrl("https://config.example/m.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BSMSO_UPDATE_MANIFEST_URL", previous);
        }
    }

    [Fact]
    public void ResolveProductVersionLabel_ReadsBundledSidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), "bsmso-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(
                LauncherUpdateManifest.DefaultVersionLabel,
                LauncherUpdateChecker.ResolveProductVersionLabel(root));

            File.WriteAllText(
                Path.Combine(root, LauncherUpdateManifest.FileName),
                LauncherUpdateManifest.BuildJson(ProtocolConstants.ModBuildId, versionLabel: "2.0"));
            Assert.Equal("2.0", LauncherUpdateChecker.ResolveProductVersionLabel(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
