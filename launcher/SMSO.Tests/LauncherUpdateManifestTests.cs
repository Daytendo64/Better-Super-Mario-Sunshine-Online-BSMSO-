using SMSO.Net;

namespace SMSO.Tests;

public class LauncherUpdateManifestTests
{
    [Fact]
    public void TryParse_ReadsModBuildIdAndDownloadUrl()
    {
        const string json = """
            {
              "modBuildId": 99,
              "versionLabel": "1.0",
              "downloadUrl": "https://example.com/BSMSO_1.0.zip",
              "message": "Get the latest zip"
            }
            """;

        Assert.True(LauncherUpdateManifest.TryParse(json, out var manifest));
        Assert.NotNull(manifest);
        Assert.Equal((ushort)99, manifest!.ModBuildId);
        Assert.Equal("1.0", manifest.VersionLabel);
        Assert.Equal("https://example.com/BSMSO_1.0.zip", manifest.DownloadUrl);
        Assert.True(manifest.IsNewerThan(53));
        Assert.False(manifest.IsNewerThan(99));
        Assert.False(manifest.IsNewerThan(100));
    }

    [Fact]
    public void BuildJson_RoundTripsCurrentBuild()
    {
        var json = LauncherUpdateManifest.BuildJson(
            ProtocolConstants.ModBuildId,
            versionLabel: "1.0",
            downloadUrl: "https://example.com/download");
        Assert.True(LauncherUpdateManifest.TryParse(json, out var manifest));
        Assert.NotNull(manifest);
        Assert.Equal(ProtocolConstants.ModBuildId, manifest!.ModBuildId);
        Assert.Equal("https://example.com/download", manifest.DownloadUrl);
    }

    [Fact]
    public void BuildJson_OmitsDownloadUrlByDefault()
    {
        var json = LauncherUpdateManifest.BuildJson(ProtocolConstants.ModBuildId);
        Assert.True(LauncherUpdateManifest.TryParse(json, out var manifest));
        Assert.NotNull(manifest);
        Assert.True(string.IsNullOrEmpty(manifest!.DownloadUrl));
        Assert.DoesNotContain("github.com", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_RejectsEmptyOrInvalid()
    {
        Assert.False(LauncherUpdateManifest.TryParse("", out _));
        Assert.False(LauncherUpdateManifest.TryParse("{ }", out _));
        Assert.False(LauncherUpdateManifest.TryParse("not-json", out _));
        Assert.False(LauncherUpdateManifest.TryParse("""{"modBuildId":0}""", out _));
    }
}
