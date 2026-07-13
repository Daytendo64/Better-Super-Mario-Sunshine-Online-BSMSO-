using SMSO.Net;

namespace SMSO.Tests;

public class PinnaParkInteriorMappingTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(7, 5, 5)]
    public void CatalogToScenario_MapsParkInteriorStates(byte catalogId, byte loadScenario, byte missionScenario)
    {
        Assert.True(PinnaParkInteriorMapping.TryCatalogToLoadScenario(catalogId, out var load));
        Assert.True(PinnaParkInteriorMapping.TryCatalogToMissionScenario(catalogId, out var mission));
        Assert.Equal(loadScenario, load);
        Assert.Equal(missionScenario, mission);
    }

    [Theory]
    [InlineData(5, 7)]
    [InlineData(0, 0)]
    [InlineData(7, 0)] // pinnaParco7 = Ep1 shine-spawn aftermath, not Balloons
    public void ScenarioToCatalog_RoundTrips(byte scenarioId, byte catalogId)
    {
        Assert.True(PinnaParkInteriorMapping.TryScenarioToCatalog(scenarioId, out var resolved));
        Assert.Equal(catalogId, resolved);
    }

    [Fact]
    public void NormalizeEpisodeFromGame_ScenarioSevenDisplaysAsMechaBowser()
    {
        Assert.Equal(0, LevelCatalog.NormalizeEpisodeFromGame(PinnaParkInteriorMapping.AreaId, 7));
    }

    [Fact]
    public void GetEpisodeDisplayName_ScenarioFiveShowsBalloons()
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        var normalized = LevelCatalog.NormalizeEpisodeFromGame(PinnaParkInteriorMapping.AreaId, 5, catalog);
        Assert.Contains("Roller Coaster Balloons",
            catalog.GetEpisodeDisplayName(PinnaParkInteriorMapping.AreaId, normalized),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetEpisodeDisplayName_PlazaRawScenarioEightShowsMainHub()
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        // Raw scenario 8 is not a catalog episode id — display path must remap.
        Assert.Contains("Main Hub",
            catalog.GetEpisodeDisplayName(DelfinoPlazaMapping.AreaId, 8),
            StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ResolveEpisodeForWarp_BalloonsUsesScenarioFiveNotSeven()
    {
        // Catalog Episode 8 (id 7) must load pinnaParco5, not pinnaParco7 (Ep1 shine spawn).
        Assert.Equal(5, LevelCatalog.ResolveEpisodeForWarp(PinnaParkInteriorMapping.AreaId, 7));
    }

    [Fact]
    public void ResolveEpisodeForWarp_MechaBowserUsesScenarioZero()
    {
        Assert.Equal(0, LevelCatalog.ResolveEpisodeForWarp(PinnaParkInteriorMapping.AreaId, 0));
    }

    [Fact]
    public void NormalizeEpisodeFromGame_ScenarioFiveDisplaysAsBalloons()
    {
        Assert.Equal(7, LevelCatalog.NormalizeEpisodeFromGame(PinnaParkInteriorMapping.AreaId, 5));
    }

    [Fact]
    public void BeachEpisodeEight_StillUsesCatalogSevenWithoutParkRemap()
    {
        // Beach (area 5) Episode 8 stays 1:1; Random Level excludes beach and warps area 13.
        Assert.Equal(7, LevelCatalog.ResolveEpisodeForWarp(PinnaParkInteriorMapping.BeachAreaId, 7));
        Assert.False(LevelCatalog.TryResolveWarpDestination(
            PinnaParkInteriorMapping.BeachAreaId, 7, out _, out _));
    }
}
