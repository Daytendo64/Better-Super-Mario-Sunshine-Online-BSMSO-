namespace SMSO.Net;

/// <summary>
/// Sirena Beach hotel interior (area 7). Disc archives are delfino0..delfino4;
/// scenario indices 5+ fall back to the title screen. Natural beach→hotel doors
/// (dolphin.log): ep3→delfino1, ep4/5→delfino2. Ep7 Shadow Mario uses delfino3;
/// ep8 Red Coins use delfino4 with logical mission 4 (timenoe/RAScripts).
/// Sirena ep 4 (casino) and ep 5 (King Boo) both reuse delfino2: ep4 keeps mission 3,
/// ep5 keeps mission 4 so the hotel→casino door loads casino0 vs casino1. King Boo's
/// boss arena itself is course 0x38 (area 56), not this area.
/// </summary>
public static class SirenaHotelInteriorMapping
{
    public const byte AreaId = 7;

    private static readonly (byte Catalog, byte LoadScenario, byte MissionScenario)[] Episodes =
    {
        (0, 0, 0), // delfino0 — default lobby
        (2, 1, 2), // Sirena ep 3 — Mysterious Hotel Delfino (delfino1)
        (3, 2, 3), // Sirena ep 4 — Casino path (delfino2 map, ep4 mission)
        (4, 2, 4), // Sirena ep 5 — King Boo path (delfino2 map, ep5 mission)
        (6, 3, 6), // Sirena ep 7 — Shadow Mario Checks In (delfino3)
        (7, 4, 4), // Sirena ep 8 — Red Coins in the Hotel (delfino4)
    };

    public static bool TryCatalogToMissionScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryLookup(catalogEpisodeId, out _, out scenarioId);

    public static bool TryCatalogToLoadScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryLookup(catalogEpisodeId, out scenarioId, out _);

    public static bool TryCatalogToScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryCatalogToMissionScenario(catalogEpisodeId, out scenarioId);

    public static bool TryScenarioToCatalog(byte scenarioId, out byte catalogEpisodeId)
    {
        // Prefer identity load==mission (Red Coins 7→4/4) before catalog==mission
        // (King Boo 4→2/4). Reverse lookup only sees one scenario id.
        foreach (var (catalog, load, mission) in Episodes)
        {
            if (mission == scenarioId && load == scenarioId)
            {
                catalogEpisodeId = catalog;
                return true;
            }
        }

        foreach (var (catalog, _, mission) in Episodes)
        {
            if (mission == scenarioId && catalog == scenarioId)
            {
                catalogEpisodeId = catalog;
                return true;
            }
        }

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
