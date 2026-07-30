#pragma once

#include <Dolphin/types.h>

/// Episode equivalence for co-op progress / mission apply.
/// Mirrors launcher <c>LevelCatalog.EpisodesEquivalent</c> /
/// <c>NormalizeEpisodeFromGame</c> (plaza hub, casino catalog↔mission,
/// hotel / Ricco / Pinna scenario aliases). Keep in lockstep with C#.
namespace smso {
namespace episode_equiv {

constexpr u8 kDelfinoPlazaAreaId = 1;
constexpr u8 kRiccoHarborAreaId = 3;
constexpr u8 kSirenaHotelAreaId = 7;
constexpr u8 kPinnaParkAreaId = 13;
constexpr u8 kSirenaCasinoAreaId = 14;

inline bool normalizeHotelScenario(u8 scenarioId, u8 &catalogId) {
    // Prefer identity load==mission (Red Coins catalog 7, load/mission 4)
    // before catalog==mission (King Boo catalog 4, load 2 / mission 4).
    // Matches SirenaHotelInteriorMapping.TryScenarioToCatalog.
    struct Row {
        u8 catalog;
        u8 load;
        u8 mission;
    };
    static constexpr Row kRows[] = {
        {0, 0, 0}, {2, 1, 2}, {3, 2, 3}, {4, 2, 4}, {6, 3, 6}, {7, 4, 4},
    };

    for (const auto &row : kRows) {
        if (row.mission == scenarioId && row.load == scenarioId) {
            catalogId = row.catalog;
            return true;
        }
    }
    for (const auto &row : kRows) {
        if (row.mission == scenarioId && row.catalog == scenarioId) {
            catalogId = row.catalog;
            return true;
        }
    }
    for (const auto &row : kRows) {
        if (row.mission == scenarioId) {
            catalogId = row.catalog;
            return true;
        }
    }
    // Load-only ids (death pin / CurrentScene): map load → first matching catalog.
    for (const auto &row : kRows) {
        if (row.load == scenarioId) {
            catalogId = row.catalog;
            return true;
        }
    }
    catalogId = scenarioId;
    return false;
}

/// True when two hotel episode ids refer to the same delfino archive (remote visibility).
/// King Boo load=2 ↔ mission=4 share delfino2; catalog/load/mission on one row match.
inline bool hotelEpisodesSameArchive(u8 a, u8 b) {
    if (a == b)
        return true;
    struct Row {
        u8 catalog;
        u8 load;
        u8 mission;
    };
    static constexpr Row kRows[] = {
        {0, 0, 0}, {2, 1, 2}, {3, 2, 3}, {4, 2, 4}, {6, 3, 6}, {7, 4, 4},
    };
    for (const auto &row : kRows) {
        const bool aIn = a == row.catalog || a == row.load || a == row.mission;
        const bool bIn = b == row.catalog || b == row.load || b == row.mission;
        if (aIn && bIn)
            return true;
    }
    return false;
}

inline bool normalizePinnaScenario(u8 scenarioId, u8 &catalogId) {
    // Ep1 cutscene archives share catalog 0 with Mecha-Bowser.
    if (scenarioId == 6 || scenarioId == 7) {
        catalogId = 0;
        return true;
    }

    struct Row {
        u8 catalog;
        u8 mission;
    };
    static constexpr Row kRows[] = {
        {0, 0}, {2, 1}, {4, 2}, {5, 3}, {6, 4}, {7, 5},
    };
    for (const auto &row : kRows) {
        if (row.mission == scenarioId) {
            catalogId = row.catalog;
            return true;
        }
    }
    catalogId = scenarioId;
    return false;
}

inline bool normalizeCasinoScenario(u8 scenarioId, u8 &catalogId) {
    if (scenarioId == 0 || scenarioId == 3) {
        catalogId = 0;
        return true;
    }
    if (scenarioId == 1 || scenarioId == 4) {
        catalogId = 1;
        return true;
    }
    catalogId = scenarioId;
    return false;
}

inline bool normalizeRiccoScenario(u8 scenarioId, u8 &catalogId) {
    if (scenarioId == 8) {
        catalogId = 0;
        return true;
    }
    catalogId = scenarioId;
    return false;
}

/// In-game / director / catalog episode → canonical catalog id for comparison.
inline u8 normalizeEpisode(u8 courseId, u8 episodeId) {
    u8 catalogId = episodeId;
    if (courseId == kSirenaHotelAreaId)
        normalizeHotelScenario(episodeId, catalogId);
    else if (courseId == kPinnaParkAreaId)
        normalizePinnaScenario(episodeId, catalogId);
    else if (courseId == kSirenaCasinoAreaId)
        normalizeCasinoScenario(episodeId, catalogId);
    else if (courseId == kRiccoHarborAreaId)
        normalizeRiccoScenario(episodeId, catalogId);
    return catalogId;
}

inline bool casinoEpisodesEquivalent(u8 a, u8 b) {
    if (a == b)
        return true;
    u8 catalogA = 0;
    u8 catalogB = 0;
    return normalizeCasinoScenario(a, catalogA) && normalizeCasinoScenario(b, catalogB) &&
           catalogA == catalogB;
}

/// True when two episode ids name the same co-op progress stage for <paramref name="courseId"/>.
inline bool episodesEquivalent(u8 courseId, u8 episodeA, u8 episodeB) {
    if (episodeA == episodeB)
        return true;
    // All Delfino Plaza scenarios share one hub for co-op progress / mission apply.
    if (courseId == kDelfinoPlazaAreaId)
        return true;
    if (courseId == kSirenaCasinoAreaId)
        return casinoEpisodesEquivalent(episodeA, episodeB);
    // Hotel: same delfino archive (load↔mission) so remotes stay visible across
    // mission override / death reload, including King Boo load=2 ↔ mission=4.
    if (courseId == kSirenaHotelAreaId)
        return hotelEpisodesSameArchive(episodeA, episodeB);
    return normalizeEpisode(courseId, episodeA) == normalizeEpisode(courseId, episodeB);
}

/// Course must match; episodes compared via <see cref="episodesEquivalent"/>.
inline bool sameStage(u8 eventCourse, u8 eventEpisode, u8 localCourse, u8 localEpisode) {
    if (eventCourse != localCourse)
        return false;
    return episodesEquivalent(eventCourse, eventEpisode, localEpisode);
}

} // namespace episode_equiv
} // namespace smso
