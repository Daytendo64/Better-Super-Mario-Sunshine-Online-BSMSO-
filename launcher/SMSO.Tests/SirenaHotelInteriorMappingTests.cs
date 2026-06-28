using SMSO.Net;

namespace SMSO.Tests;

public class SirenaHotelInteriorMappingTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2, 2, 2)]
    [InlineData(6, 0, 6)]
    [InlineData(7, 4, 4)]
    public void CatalogToScenario_MapsHotelInteriorStates(byte catalogId, byte loadScenario, byte missionScenario)
    {
        Assert.True(SirenaHotelInteriorMapping.TryCatalogToLoadScenario(catalogId, out var load));
        Assert.True(SirenaHotelInteriorMapping.TryCatalogToMissionScenario(catalogId, out var mission));
        Assert.Equal(loadScenario, load);
        Assert.Equal(missionScenario, mission);
    }

    [Theory]
    [InlineData(4, 7)]
    [InlineData(6, 6)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    public void ScenarioToCatalog_RoundTrips(byte scenarioId, byte catalogId)
    {
        Assert.True(SirenaHotelInteriorMapping.TryScenarioToCatalog(scenarioId, out var resolved));
        Assert.Equal(catalogId, resolved);
    }

    [Fact]
    public void ResolveEpisodeForWarp_RedCoinsUsesScenarioFourNotSeven()
    {
        Assert.Equal(4, LevelCatalog.ResolveEpisodeForWarp(SirenaHotelInteriorMapping.AreaId, 7));
    }

    [Fact]
    public void ResolveEpisodeForWarp_ShadowMarioUsesMissionSixLoadZero()
    {
        Assert.Equal(6, LevelCatalog.ResolveEpisodeForWarp(SirenaHotelInteriorMapping.AreaId, 6));
        Assert.True(SirenaHotelInteriorMapping.TryCatalogToLoadScenario(6, out var load));
        Assert.Equal(0, load);
    }

    [Theory]
    [InlineData(6, 6, 7, 6)]
    [InlineData(6, 7, 7, 4)]
    public void TryResolveWarpDestination_SirenaBeachHotelMissionsUseAreaSeven(
        byte courseId, byte catalogId, byte expectedArea, byte expectedMission)
    {
        Assert.True(LevelCatalog.TryResolveWarpDestination(courseId, catalogId, out var areaId, out var mission));
        Assert.Equal(expectedArea, areaId);
        Assert.Equal(expectedMission, mission);
    }
}
