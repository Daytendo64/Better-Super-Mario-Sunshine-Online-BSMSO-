namespace SMSO.Net;

/// <summary>
/// Delfino Plaza hub states (area 1). In-game scenario indices do not always match
/// dolpic archive numbers — e.g. dolpic10 loads at scenario 2 (timenoe/RAScripts).
/// </summary>
public static class DelfinoPlazaMapping
{
    public const byte AreaId = 1;

    // Catalog episode ID -> in-game scenario index (mEpisodeID / stageId low byte).
    private static readonly (byte Catalog, byte Scenario, string Archive)[] Episodes =
    {
        (0, 8, "dolpic8"),  // main open hub (Yoshi fruit appears here after story progress)
        (1, 0, "dolpic0"),  // arrival / police court intro
        (2, 1, "dolpic1"),  // after first plaza cleanup shine
        (3, 5, "dolpic5"),  // after 3 shines collected
        (4, 6, "dolpic6"),  // Bianco Hills gate open
        (5, 7, "dolpic7"),  // after 10 shines / expanded hub
        (6, 9, "dolpic9"),  // flooded plaza (temporary high-water event)
        (7, 2, "dolpic10"), // post-flood plaza (archive dolpic10, scenario index 2)
    };

    public static bool TryCatalogToScenario(byte catalogEpisodeId, out byte scenarioId)
    {
        foreach (var (catalog, scenario, _) in Episodes)
        {
            if (catalog == catalogEpisodeId)
            {
                scenarioId = scenario;
                return true;
            }
        }

        scenarioId = catalogEpisodeId;
        return false;
    }

    public static bool TryScenarioToCatalog(byte scenarioId, out byte catalogEpisodeId)
    {
        foreach (var (catalog, scenario, _) in Episodes)
        {
            if (scenario == scenarioId)
            {
                catalogEpisodeId = catalog;
                return true;
            }
        }

        catalogEpisodeId = scenarioId;
        return false;
    }
}
