#include "graffiti_clean_sync.hpp"

#include <Dolphin/OS.h>

// ---------------------------------------------------------------------------
// Graffiti / goop sync is PERMANENTLY DISABLED (2026-07-19).
//
// Live spray published one durable WE_GRAFFITI_CLEANED cell per 32u XYZ stamp.
// Long co-op runs flooded durable history / TCP / the single CommBuffer mailbox
// (78k+ GraffitiCleaned in one session log) and starved shine/story/red apply.
//
// This file keeps the public API as no-ops so call sites still link. Retail
// TPollutionManager::clean is NOT hooked — local spray works; remotes do not
// sync goop clears.
// ---------------------------------------------------------------------------

namespace smso {

void initGraffitiCleanSync() {
    static bool sLogged = false;
    if (!sLogged) {
        sLogged = true;
        OSReport("[SMSOBB] graffiti-clean sync DISABLED (no publish / apply / assist)\n");
    }
}

void notifyGraffitiCleanStageEnter() {}

void updateGraffitiCleanSync() {}

bool applyGraffitiCleanWorldEvent(const CommWorldEvent & /*event*/) {
    return false;
}

void onLocalPollutionClean(f32 /*x*/, f32 /*y*/, f32 /*z*/, f32 /*size*/) {}

void notifyRemoteSprayEmit(f32 /*ox*/, f32 /*oy*/, f32 /*oz*/, f32 /*dx*/, f32 /*dy*/,
                           f32 /*dz*/) {}

} // namespace smso
