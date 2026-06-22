using SMSO.Net;

namespace SMSO.Tests;

public class DelfinoPlazaMappingTests
{
    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 5)]
    [InlineData(4, 6)]
    [InlineData(5, 7)]
    [InlineData(6, 9)]
    [InlineData(7, 2)]
    public void CatalogToScenario_MapsAllHubStates(byte catalogId, byte scenarioId)
    {
        Assert.True(DelfinoPlazaMapping.TryCatalogToScenario(catalogId, out var resolved));
        Assert.Equal(scenarioId, resolved);
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 7)]
    [InlineData(9, 6)]
    public void ScenarioToCatalog_RoundTrips(byte scenarioId, byte catalogId)
    {
        Assert.True(DelfinoPlazaMapping.TryScenarioToCatalog(scenarioId, out var resolved));
        Assert.Equal(catalogId, resolved);
    }
}
