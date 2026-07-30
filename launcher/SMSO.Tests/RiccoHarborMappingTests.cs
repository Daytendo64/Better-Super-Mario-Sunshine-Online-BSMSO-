using SMSO.Net;

namespace SMSO.Tests;

public class RiccoHarborMappingTests
{
    [Fact]
    public void ScenarioEight_MapsToCatalogZero()
    {
        Assert.True(RiccoHarborMapping.TryScenarioToCatalog(8, out var catalog));
        Assert.Equal(0, catalog);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void ListedScenarios_PassThroughWithoutRemap(byte scenarioId)
    {
        Assert.False(RiccoHarborMapping.TryScenarioToCatalog(scenarioId, out var catalog));
        Assert.Equal(scenarioId, catalog);
    }

    [Fact]
    public void NormalizeEpisodeFromGame_ScenarioEightDisplaysAsEpisodeOne()
    {
        Assert.Equal(0, LevelCatalog.NormalizeEpisodeFromGame(RiccoHarborMapping.AreaId, 8));
    }

    [Fact]
    public void GetEpisodeDisplayName_ScenarioEightShowsGooperBlooper()
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        Assert.Contains("Gooper Blooper",
            catalog.GetEpisodeDisplayName(RiccoHarborMapping.AreaId, 8),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveEpisodeForWarp_CatalogZeroStillLoadsScenarioZero()
    {
        // Must not remap catalog Ep1 to ricco8 — warp loads the start-of-episode archive.
        Assert.Equal(0, LevelCatalog.ResolveEpisodeForWarp(RiccoHarborMapping.AreaId, 0));
    }

    [Theory]
    [InlineData(0, 8, true)]
    [InlineData(8, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(8, 1, false)]
    public void EpisodesEquivalent_MatchesCatalogAndMidFight(byte a, byte b, bool expected)
    {
        Assert.Equal(expected, RiccoHarborMapping.EpisodesEquivalent(a, b));
    }

    private static string FindLevelsPath()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "assets", "levels.ntsc-u.json"),
                     Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets",
                         "levels.ntsc-u.json"),
                     Path.Combine(Directory.GetCurrentDirectory(), "assets", "levels.ntsc-u.json"),
                 })
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                return full;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "assets", "levels.ntsc-u.json");
    }
}
