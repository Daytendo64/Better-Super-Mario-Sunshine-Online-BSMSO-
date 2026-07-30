using SMSO.Net;

namespace SMSO.Tests;

public class SirenaHotelInteriorMappingTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 2, 3)]
    [InlineData(4, 2, 4)]
    [InlineData(6, 3, 6)]
    [InlineData(7, 4, 4)]
    public void CatalogToScenario_MapsHotelInteriorStates(byte catalogId, byte loadScenario, byte missionScenario)
    {
        Assert.True(SirenaHotelInteriorMapping.TryCatalogToLoadScenario(catalogId, out var load));
        Assert.True(SirenaHotelInteriorMapping.TryCatalogToMissionScenario(catalogId, out var mission));
        Assert.Equal(loadScenario, load);
        Assert.Equal(missionScenario, mission);
    }

    [Theory]
    [InlineData(4, 7)] // identity load==mission → Red Coins catalog
    [InlineData(6, 6)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
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
    public void ResolveEpisodeForWarp_MysteriousHotelUsesDelfinoOne()
    {
        Assert.Equal(2, LevelCatalog.ResolveEpisodeForWarp(SirenaHotelInteriorMapping.AreaId, 2));
        Assert.True(SirenaHotelInteriorMapping.TryCatalogToLoadScenario(2, out var load));
        Assert.Equal(1, load);
    }

    [Fact]
    public void ResolveEpisodeForWarp_KingBooUsesCasinoPathLoadTwoMissionFour()
    {
        Assert.Equal(4, LevelCatalog.ResolveEpisodeForWarp(SirenaHotelInteriorMapping.AreaId, 4));
        Assert.True(SirenaHotelInteriorMapping.TryCatalogToLoadScenario(4, out var load));
        Assert.Equal(2, load);
    }

    [Fact]
    public void ResolveEpisodeForWarp_ShadowMarioUsesMissionSixLoadThree()
    {
        Assert.Equal(6, LevelCatalog.ResolveEpisodeForWarp(SirenaHotelInteriorMapping.AreaId, 6));
        Assert.True(SirenaHotelInteriorMapping.TryCatalogToLoadScenario(6, out var load));
        Assert.Equal(3, load);
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

        // Warp path keeps catalog episode ids so the module hotel load table sees 6/7.
        LevelCatalog.ResolveWarpDestination(courseId, catalogId, out var warpArea, out var warpEpisode);
        Assert.Equal(expectedArea, warpArea);
        Assert.Equal(catalogId, warpEpisode);
        Assert.Equal(expectedMission, LevelCatalog.ResolveEpisodeForWarp(courseId, catalogId));
    }

    // Random Level / beach scrubbing must stay on sirenaN — not hotel remaps.
    [Theory]
    [InlineData(6, 0)]
    [InlineData(6, 5)]
    public void TryResolveWarpDestination_SirenaBeachEpisodesStayOnBeach(byte courseId, byte catalogId)
    {
        Assert.False(LevelCatalog.TryResolveWarpDestination(courseId, catalogId, out var areaId, out var mission));
        Assert.Equal(courseId, areaId);
        Assert.Equal(catalogId, mission);
        Assert.Equal(catalogId, LevelCatalog.ResolveEpisodeForWarp(courseId, catalogId));

        LevelCatalog.ResolveWarpDestination(courseId, catalogId, out var warpArea, out var warpEpisode);
        Assert.Equal(courseId, warpArea);
        Assert.Equal(catalogId, warpEpisode);
    }
}
