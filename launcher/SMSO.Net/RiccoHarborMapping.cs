namespace SMSO.Net;

/// <summary>
/// Ricco Harbor (area 3). Catalog episodes 0–7 match ricco0–ricco7, except mid-fight
/// Episode 1 switches the director to scenario 8 (<c>ricco8.arc</c> after Gooper Blooper
/// is unleashed — TCRF Internal Name Oddities). Display/normalize maps 8 → catalog 0;
/// warps must still load scenario 0 (<c>ricco0</c>), never 8.
/// </summary>
public static class RiccoHarborMapping
{
    public const byte AreaId = 3;

    /// <summary>
    /// In-game scenario → catalog episode for roster/display.
    /// Scenario 8 is Episode 1 mid-fight, not a ninth catalog episode.
    /// </summary>
    public static bool TryScenarioToCatalog(byte scenarioId, out byte catalogEpisodeId)
    {
        if (scenarioId == 8)
        {
            catalogEpisodeId = 0;
            return true;
        }

        catalogEpisodeId = scenarioId;
        return false;
    }

    /// <summary>
    /// True when two scenarios are the same Ricco co-op progress stage (Ep1 catalog 0
    /// and mid-fight scenario 8 share mission/red-coin state).
    /// </summary>
    public static bool EpisodesEquivalent(byte episodeA, byte episodeB)
    {
        if (episodeA == episodeB)
            return true;
        TryScenarioToCatalog(episodeA, out var a);
        TryScenarioToCatalog(episodeB, out var b);
        return a == b;
    }

    /// <summary>Normalize game/director scenario onto the catalog episode id.</summary>
    public static byte NormalizeEpisodeFromGame(byte episodeId)
    {
        TryScenarioToCatalog(episodeId, out var catalog);
        return catalog;
    }
}
