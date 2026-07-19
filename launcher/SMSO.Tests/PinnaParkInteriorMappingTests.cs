using SMSO.Net;

namespace SMSO.Tests;

public class PinnaParkInteriorMappingTests
{
    [Theory]
    [InlineData(0, 0, 0)] // Mecha-Bowser
    [InlineData(2, 1, 1)] // Pirate Ships
    [InlineData(4, 2, 2)] // Ferris Wheel
    [InlineData(5, 3, 3)] // Yoshi-Go-Round
    [InlineData(6, 4, 4)] // Shadow Mario — NOT pinnaParco6 (Noki)
    [InlineData(7, 5, 5)] // Balloons — NOT pinnaParco7 (Ep1 shine spawn)
    public void CatalogToScenario_MapsParkInteriorStates(byte catalogId, byte loadScenario, byte missionScenario)
    {
        Assert.True(PinnaParkInteriorMapping.TryCatalogToLoadScenario(catalogId, out var load));
        Assert.True(PinnaParkInteriorMapping.TryCatalogToMissionScenario(catalogId, out var mission));
        Assert.Equal(loadScenario, load);
        Assert.Equal(missionScenario, mission);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(4, 6)]
    [InlineData(5, 7)]
    [InlineData(6, 0)] // pinnaParco6 = Ep1 Noki dialogue
    [InlineData(7, 0)] // pinnaParco7 = Ep1 shine-spawn aftermath
    public void ScenarioToCatalog_RoundTrips(byte scenarioId, byte catalogId)
    {
        Assert.True(PinnaParkInteriorMapping.TryScenarioToCatalog(scenarioId, out var resolved));
        Assert.Equal(catalogId, resolved);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void BeachOnlyEpisodes_HaveNoParkRemap(byte catalogId)
    {
        Assert.False(PinnaParkInteriorMapping.TryCatalogToLoadScenario(catalogId, out var load));
        Assert.Equal(catalogId, load);
    }

    [Fact]
    public void NormalizeEpisodeFromGame_ScenarioSixDisplaysAsMechaBowser()
    {
        Assert.Equal(0, LevelCatalog.NormalizeEpisodeFromGame(PinnaParkInteriorMapping.AreaId, 6));
    }

    [Fact]
    public void NormalizeEpisodeFromGame_ScenarioSevenDisplaysAsMechaBowser()
    {
        Assert.Equal(0, LevelCatalog.NormalizeEpisodeFromGame(PinnaParkInteriorMapping.AreaId, 7));
    }

    [Fact]
    public void NormalizeEpisodeFromGame_ScenarioFourDisplaysAsShadowMario()
    {
        Assert.Equal(6, LevelCatalog.NormalizeEpisodeFromGame(PinnaParkInteriorMapping.AreaId, 4));
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
    public void GetEpisodeDisplayName_CatalogSixShowsShadowMarioNotNoki()
    {
        var levelsPath = FindLevelsPath();
        if (!File.Exists(levelsPath))
            return;

        var catalog = LevelCatalog.Load(levelsPath);
        // Catalog 6 must not be reinterpreted as pinnaParco6 (Noki dialogue).
        Assert.Contains("Shadow Mario",
            catalog.GetEpisodeDisplayName(PinnaParkInteriorMapping.AreaId, 6),
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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 4)]
    [InlineData(7, 5)]
    public void ResolveEpisodeForWarp_ParkCatalogUsesPinnaParcoArchive(byte catalogId, byte loadScenario)
    {
        Assert.Equal(loadScenario, LevelCatalog.ResolveEpisodeForWarp(PinnaParkInteriorMapping.AreaId, catalogId));
    }

    [Fact]
    public void ResolveEpisodeForWarp_BalloonsUsesScenarioFiveNotSeven()
    {
        // Catalog Episode 8 (id 7) must load pinnaParco5, not pinnaParco7 (Ep1 shine spawn).
        Assert.Equal(5, LevelCatalog.ResolveEpisodeForWarp(PinnaParkInteriorMapping.AreaId, 7));
    }

    [Fact]
    public void ResolveEpisodeForWarp_ShadowMarioUsesScenarioFourNotSix()
    {
        // Catalog Episode 7 (id 6) must load pinnaParco4, not pinnaParco6 (Noki dialogue).
        Assert.Equal(4, LevelCatalog.ResolveEpisodeForWarp(PinnaParkInteriorMapping.AreaId, 6));
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
        // Beach (area 5) Episode 8 stays 1:1; gate remap happens in-module on 13/255.
        // Random Level excludes beach and warps area 13.
        Assert.Equal(7, LevelCatalog.ResolveEpisodeForWarp(PinnaParkInteriorMapping.BeachAreaId, 7));
        Assert.False(LevelCatalog.TryResolveWarpDestination(
            PinnaParkInteriorMapping.BeachAreaId, 7, out _, out _));
    }

    [Fact]
    public void BeachEpisodeSeven_StillUsesCatalogSixWithoutParkRemap()
    {
        Assert.Equal(6, LevelCatalog.ResolveEpisodeForWarp(PinnaParkInteriorMapping.BeachAreaId, 6));
        Assert.False(LevelCatalog.TryResolveWarpDestination(
            PinnaParkInteriorMapping.BeachAreaId, 6, out _, out _));
    }
}
