namespace SMSO.Net;

/// <summary>
/// Pinna Park interior (area 13 / pinnaParco). Stage archive indices are not 1:1 with
/// beach (area 5) episode IDs. timenoe/RAScripts + TCRF:
/// pinnaParco5 (scenario 5) = Episode 8 Roller Coaster Balloons;
/// pinnaParco7 (scenario 7) = Episode 1 post–Mecha-Bowser shine spawn.
/// Warping catalog episode 7 into area 13 without remapping therefore loads the wrong park state.
/// </summary>
public static class PinnaParkInteriorMapping
{
    public const byte AreaId = 13;
    public const byte BeachAreaId = 5;

    private static readonly (byte Catalog, byte LoadScenario, byte MissionScenario)[] Episodes =
    {
        (0, 0, 0), // pinnaParco0 — Mecha-Bowser Appears!
        (7, 5, 5), // pinnaParco5 — Roller Coaster Balloons (Episode 8)
    };

    public static bool TryCatalogToMissionScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryLookup(catalogEpisodeId, out _, out scenarioId);

    public static bool TryCatalogToLoadScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryLookup(catalogEpisodeId, out scenarioId, out _);

    public static bool TryCatalogToScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryCatalogToMissionScenario(catalogEpisodeId, out scenarioId);

    public static bool TryScenarioToCatalog(byte scenarioId, out byte catalogEpisodeId)
    {
        // pinnaParco7 is Episode 1 shine-spawn aftermath (not Balloons). Without this,
        // scenario 7 passthrough collides with catalog episode 7 (Roller Coaster Balloons).
        if (scenarioId == 7)
        {
            catalogEpisodeId = 0;
            return true;
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
