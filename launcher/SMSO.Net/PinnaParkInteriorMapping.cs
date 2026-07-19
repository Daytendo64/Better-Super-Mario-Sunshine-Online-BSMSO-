namespace SMSO.Net;

/// <summary>
/// Pinna Park interior (area 13 / pinnaParco). Stage archive indices are not 1:1 with
/// beach (area 5) episode IDs. TCRF Internal Name Oddities + timenoe/RAScripts:
/// <list type="bullet">
/// <item>pinnaParco0 — Episode 1 Mecha-Bowser (main park)</item>
/// <item>pinnaParco1 — Episode 3 Pirate Ships</item>
/// <item>pinnaParco2 — Episode 5 Ferris Wheel</item>
/// <item>pinnaParco3 — Episode 6 Yoshi-Go-Round</item>
/// <item>pinnaParco4 — Episode 7 Shadow Mario</item>
/// <item>pinnaParco5 — Episode 8 Roller Coaster Balloons</item>
/// <item>pinnaParco6 — Episode 1 Noki dialogue (not Shadow Mario)</item>
/// <item>pinnaParco7 — Episode 1 shine-spawn aftermath (not Balloons)</item>
/// </list>
/// Beach→park same-shine doors use episode 0xFF and must remap via
/// <see cref="TryCatalogToLoadScenario"/>; a raw copy of beach ep 6/7 loads Noki/shine-spawn.
/// </summary>
public static class PinnaParkInteriorMapping
{
    public const byte AreaId = 13;
    public const byte BeachAreaId = 5;

    // Catalog (beach shine-select id) → pinnaParco load/mission scenario.
    // Ep2 (1) and Ep4 (3) are beach-only; no park archive row.
    private static readonly (byte Catalog, byte LoadScenario, byte MissionScenario)[] Episodes =
    {
        (0, 0, 0), // pinnaParco0 — Mecha-Bowser Appears!
        (2, 1, 1), // pinnaParco1 — Red Coins of the Pirate Ships
        (4, 2, 2), // pinnaParco2 — The Runaway Ferris Wheel
        (5, 3, 3), // pinnaParco3 — Yoshi-Go-Round's Secret
        (6, 4, 4), // pinnaParco4 — Shadow Mario in the Park
        (7, 5, 5), // pinnaParco5 — Roller Coaster Balloons
    };

    public static bool TryCatalogToMissionScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryLookup(catalogEpisodeId, out _, out scenarioId);

    public static bool TryCatalogToLoadScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryLookup(catalogEpisodeId, out scenarioId, out _);

    public static bool TryCatalogToScenario(byte catalogEpisodeId, out byte scenarioId) =>
        TryCatalogToMissionScenario(catalogEpisodeId, out scenarioId);

    public static bool TryScenarioToCatalog(byte scenarioId, out byte catalogEpisodeId)
    {
        // Ep1 cutscene archives share catalog 0 with Mecha-Bowser.
        if (scenarioId is 6 or 7)
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
