namespace SMSO.Net;

/// <summary>
/// Sirena Beach casino (area 14). timenoe/RAScripts courseStateIDs:
/// casino0 = load 0 (Ep4 The Secret of Casino Delfino — slots),
/// casino1 = load 1 (Ep5 King Boo Down Below — purple HipDrop on roulette).
/// Beach/hotel mission ids remain 3 (Ep4) and 4 (Ep5). The module loads the matching
/// archive and keeps MarDirector.mEpisodeID on the archive index (0/1) so MapObj
/// scenario gating matches vanilla; 0x40003 retains the beach mission for hotel return.
/// </summary>
public static class SirenaCasinoMapping
{
    public const byte AreaId = 14;

    /// <summary>Catalog/archive index → beach mission id used in 0x40003.</summary>
    public static bool TryCatalogToMission(byte catalogOrLoadId, out byte missionId)
    {
        if (catalogOrLoadId == 0 || catalogOrLoadId == 3)
        {
            missionId = 3;
            return true;
        }

        if (catalogOrLoadId == 1 || catalogOrLoadId == 4)
        {
            missionId = 4;
            return true;
        }

        missionId = catalogOrLoadId;
        return false;
    }

    /// <summary>Catalog/archive or mission id → casino0/casino1 load index.</summary>
    public static bool TryCatalogToLoad(byte catalogOrMissionId, out byte loadId)
    {
        if (catalogOrMissionId == 0 || catalogOrMissionId == 3)
        {
            loadId = 0;
            return true;
        }

        if (catalogOrMissionId == 1 || catalogOrMissionId == 4)
        {
            loadId = 1;
            return true;
        }

        loadId = catalogOrMissionId;
        return false;
    }

    /// <summary>Director/mission or load id → launcher catalog episode (0 or 1).</summary>
    public static bool TryScenarioToCatalog(byte scenarioId, out byte catalogId)
    {
        if (scenarioId == 0 || scenarioId == 3)
        {
            catalogId = 0;
            return true;
        }

        if (scenarioId == 1 || scenarioId == 4)
        {
            catalogId = 1;
            return true;
        }

        catalogId = scenarioId;
        return false;
    }

    /// <summary>
    /// True when both ids name the same casino layout (catalog 0/1 ↔ beach mission 3/4).
    /// Module world events and snapshots use director mission ids; warp flush uses catalog.
    /// </summary>
    public static bool EpisodesEquivalent(byte episodeA, byte episodeB)
    {
        if (episodeA == episodeB)
            return true;
        return TryScenarioToCatalog(episodeA, out var catalogA) &&
               TryScenarioToCatalog(episodeB, out var catalogB) &&
               catalogA == catalogB;
    }
}
