namespace SMSO.Net;

/// <summary>
/// Sirena Beach hotel interior (area 7). Only delfino0/2/4 stage archives exist;
/// scenarios 6 and 7 fall back to the title screen. timenoe/RAScripts: Sirena ep 8
/// red coins use logical episode 5 (scenario index 4). Shadow Mario (ep 7) loads
/// delfino0 but runs mission scripts for episode index 6.
/// King Boo Down Below is course 0x38, not this area.
/// </summary>
public static class SirenaHotelInteriorMapping
{
    public const byte AreaId = 7;

    private static readonly (byte Catalog, byte LoadScenario, byte MissionScenario)[] Episodes =
    {
        (0, 0, 0), // delfino0 — default lobby
        (2, 2, 2), // Sirena ep 3 — Mysterious Hotel Delfino
        (6, 0, 6), // Sirena ep 7 — Shadow Mario Checks In
        (7, 4, 4), // Sirena ep 8 — Red Coins in the Hotel
    };

    public static bool TryCatalogToMissionScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryLookup(catalogEpisodeId, out _, out scenarioId);

    public static bool TryCatalogToLoadScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryLookup(catalogEpisodeId, out scenarioId, out _);

    public static bool TryCatalogToScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryCatalogToMissionScenario(catalogEpisodeId, out scenarioId);

    public static bool TryScenarioToCatalog(byte scenarioId, out byte catalogEpisodeId)
    {
        foreach (var (catalog, _, mission) in Episodes)
        {
            if (mission == scenarioId)
            {
                catalogEpisodeId = catalog;
                return true;
            }
        }

        catalogEpisodeId = scenarioId;
        return false;
    }

    private static bool TryLookup(byte catalogEpisodeId, out byte loadScenario, out byte missionScenario)
    {
        foreach (var (catalog, load, mission) in Episodes)
        {
            if (catalog == catalogEpisodeId)
            {
                loadScenario = load;
                missionScenario = mission;
                return true;
            }
        }

        loadScenario = catalogEpisodeId;
        missionScenario = catalogEpisodeId;
        return false;
    }
}
