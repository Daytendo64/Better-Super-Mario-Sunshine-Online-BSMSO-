using SMSO.Net;

namespace SMSO.Tests;

public class SirenaCasinoMappingTests
{
    [Theory]
    [InlineData(0, 0, 3)]
    [InlineData(1, 1, 4)]
    [InlineData(3, 0, 3)]
    [InlineData(4, 1, 4)]
    public void CatalogMapsToLoadAndMission(byte catalogId, byte expectedLoad, byte expectedMission)
    {
        Assert.True(SirenaCasinoMapping.TryCatalogToLoad(catalogId, out var load));
        Assert.True(SirenaCasinoMapping.TryCatalogToMission(catalogId, out var mission));
        Assert.Equal(expectedLoad, load);
        Assert.Equal(expectedMission, mission);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    public void ScenarioToCatalog_MapsMissionOrLoad(byte scenarioId, byte catalogId)
    {
        Assert.True(SirenaCasinoMapping.TryScenarioToCatalog(scenarioId, out var resolved));
        Assert.Equal(catalogId, resolved);
    }

    [Fact]
    public void ResolveEpisodeForWarp_CasinoEpisodesUseBeachMission()
    {
        Assert.Equal(3, LevelCatalog.ResolveEpisodeForWarp(SirenaCasinoMapping.AreaId, 0));
        Assert.Equal(4, LevelCatalog.ResolveEpisodeForWarp(SirenaCasinoMapping.AreaId, 1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    public void NormalizeEpisodeFromGame_MapsMissionToCatalog(byte gameScenario, byte catalogId)
    {
        Assert.Equal(catalogId, LevelCatalog.NormalizeEpisodeFromGame(SirenaCasinoMapping.AreaId, gameScenario));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 3, true)]
    [InlineData(1, 4, true)]
    [InlineData(3, 4, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    public void EpisodesEquivalent_MatchesCatalogAndMission(byte a, byte b, bool expected)
    {
        Assert.Equal(expected, SirenaCasinoMapping.EpisodesEquivalent(a, b));
        Assert.Equal(expected, SirenaCasinoMapping.EpisodesEquivalent(b, a));
    }
}
