#include "stage_guard.hpp"
#include "comm_buffer.hpp"
#include "hide_seek.hpp"
#include "puppets.hpp"

#include <BetterSMS/stage.hxx>
#include <Dolphin/OS.h>
#include <SMS/Player/Mario.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/Manager/FlagManager.hxx>
#include <SMS/macros.h>
#include <SMS/System/MarDirector.hxx>

extern TMario *gpMarioAddress;
extern TApplication gpApplication;

namespace smso {

namespace {

static bool s_pendingAuthorizedStageMove = false;

static bool isLocalMarioInStageExitState() {
    if (!gpMarioAddress)
        return false;

    TMario *local = gpMarioAddress;
    const u32 id = local->mState & 0x1FFu;

    switch (id) {
    case 0x336u: // STATE_WARPIN  — doldecomp MarioSpecial.cpp
    case 0x337u: // STATE_WARPOUT
    case 0x33Eu: // STATE_NOMOTION
    case 0x33Fu: // STATE_DISAPPEAR
    case 0x047u: // Pinna cannon / torocco rail (STATUS_TOROCCO low bits)
    case 0x302u: // STATE_SHINE_C
    case 0x321u: // STATE_DOOR_F_O
        return true;
    default:
        break;
    }

    if ((local->mState & TMario::STATE_CUTSCENE) != 0)
        return true;
    if (local->mHolder != nullptr)
        return true;

    return false;
}

using SMSGetShineStageFn = s32 (*)(u32);

static SMSGetShineStageFn shineStageForAreaFn() {
    return reinterpret_cast<SMSGetShineStageFn>(SMS_PORT_REGION(0x802A8AC8, 0x802A0BD0, 0, 0));
}

static void beginEpisodeSelectTransition(TMarDirector *director, s32 curShine, s32 nextShine) {
    if (gpApplication.mFader)
        gpApplication.mFader->setColor({0, 0, 0, 255});

    if (nextShine != curShine)
        TFlagManager::smInstance->setFlag(0x40002, 0);

    director->mNextState = 8;
    *reinterpret_cast<u32 *>(reinterpret_cast<u8 *>(director) + 0xE4) = 8;
}

// Destinations that only have load episode 0. A next-scene of area/0xFF must
// moveStage(ep=0) — NEVER open shine episode-select (Delay Context 8), which
// dumps to title (area 15). dolphin.log: next=23/255 cur=1/2 → Stage init area=15.
//
// Casino hotel stage 14 is NOT here: normalizeSirenaNextSceneForLoad rewrites
// its 0xFF doors to load 0/1 from beach mission 3/4.
// Pinna park interior (13): normalizePinnaParkNextSceneForLoad remaps beach→park
// 0xFF doors to the correct pinnaParco archive; hotel (7) keeps multi-episode routing.
// mareUndersea (16): normalizeMareUnderseaNextSceneForLoad forces load ep0 while
// keeping bay mission 3/7 — must NOT clear 0x40003 via this list.
static bool isSingleEpisodeLoadArea(u8 areaId) {
    // BSE EX-stage range (dolpic_ex* … coro_ex*) covers most plaza/stage secrets.
    if (BetterSMS::Stage::isExStage(areaId, 0))
        return true;

    switch (areaId) {
    case 0:  // Delfino Airstrip
    case 20: // Plaza — Airstrip return
    case 21: // Plaza — Super Slide
    case 22: // Plaza — Pachinko
    case 23: // Plaza — Red Coin Field (log: next=23/255 → title)
    case 24: // Plaza — Lily Pad Ride
    case 29: // Plaza — Turbo Track
    case 30: // Ricco Blooper Surfing Safari
    case 31: // Noki Shell's Secret
    case 32: // Gelato Sand Castle Secret
    case 33: // Gelato Sand Bird
    case 40: // Sirena Casino Delfino Secret
    case 41: // Pinna Yoshi-Go-Round Secret
    case 42: // Pianta Village Underside Secret
    case 46: // Bianco Dirty Lake Secret
    case 47: // Bianco Hillside Cave Secret
    case 48: // Ricco Tower Secret
    case 50: // Pinna Beach Cannon Secret
    case 51: // Sirena Hotel Lobby Secret
    case 52: // Corona Mountain (story, single load)
    case 55: // Bianco Petey boss
    case 56: // Sirena King Boo boss
    case 57: // Noki Mare boss
    case 58: // Pinna Mecha-Bowser
    case 59: // Ricco Gooper Blooper boss
    case 60: // Corona Mountain boss
        return true;
    default:
        return false;
    }
}

} // namespace

bool isNonGameplayStage(u8 areaId) {
    // BSE uses area 15 episode 0 for the file-select / options hub (data/scene/option.szs).
    return areaId == 15;
}

bool isLauncherAuthorizedStageMove() {
    CommBuffer *buf = getCommBuffer();
    const bool launcherWarp = buf && (buf->bridgeFlags & BF_WARP_PENDING) != 0;
    return launcherWarp || s_pendingAuthorizedStageMove;
}

bool isSmsoAuthorizedStageTransition() {
    return isHideSeekAuthorizedStageTransition() || isLauncherAuthorizedStageMove();
}

void authorizeLauncherStageMove() {
    s_pendingAuthorizedStageMove = true;
}

void initStageGuard() {
    s_pendingAuthorizedStageMove = false;
    OSReport("[SMSO] Stage guard installed — hub episode-select routing active\n");
}

void clearAuthorizedStageMovePending() {
    s_pendingAuthorizedStageMove = false;
}

// BSE moveStage_override (area.cpp) inlined here — SMSO is a separate Kuribo module and
// cannot link against new BSE kernel exports without a full engine rebuild.
void performSmsoMoveStage(TMarDirector *director) {
    if (!director)
        return;

    // Last line of defense: every BL'd moveStage path ends here. Mid-tag death must
    // never take plaza hub return (1/0xFF) — rewrite before any branch below.
    // Skip for intentional launcher / warp-all moves (BF_WARP_PENDING is often already
    // cleared by consumeWarpIntent; s_pendingAuthorizedStageMove covers that window).
    if (!isLauncherAuthorizedStageMove())
        redirectHideSeekDeathStageLeave(director);

    // Fix Sirena hotel/casino load ids before vanilla/BSE consumes next-scene
    // (natural doors may pass mission ids 3/4/6/7; casino must pick
    // casino0 vs casino1 from beach mission 3/4). Hotel→casino same-shine doors use
    // episode 0xFF — normalize rewrites that to load 0 or 1 and stashes mission 3/4.
    normalizeSirenaNextSceneForLoad();
    // Pinna beach→park 0xFF must remap catalog→pinnaParco archive (ep6≠Shadow Mario).
    normalizePinnaParkNextSceneForLoad();
    // Noki waterfall→mareUndersea 0xFF must load ep0 (keep mission 3/7 for Ep4/Ep8).
    normalizeMareUnderseaNextSceneForLoad();

    const u8 nextArea = gpApplication.mNextScene.mAreaID;
    const u8 nextEp = gpApplication.mNextScene.mEpisodeID;
    const u8 curArea = gpApplication.mCurrentScene.mAreaID;
    const u8 curEp = gpApplication.mCurrentScene.mEpisodeID;
    if (nextArea != curArea || nextEp != curEp) {
        OSReport("[SMSO] moveStage next=%u/%u cur=%u/%u\n", nextArea, nextEp, curArea, curEp);
    }

    // Destination title/options (area 15): never Delay Context 8 / episode-select.
    // Post-Bowser clear / movie-skip collapse set next=15/255 and fell through to
    // beginEpisodeSelectTransition (pipe swirl → black freeze). dolphin.log:
    // moveStage next=15/255 cur=60/0 → Stage init area=15. Docs: area 15 must
    // vanilla-moveStage; that only ran for CURRENT.
    constexpr u8 kCoronaBossAreaId = 60;
    constexpr u8 kDelfinoPlazaAreaId = 1;

    // Corona boss (60): movie auto-skip may collapse leave to title (15/255) or
    // another broken 0xFF non-plaza destination. Always force plaza hub — this is
    // the freeze preventer; cutscene_skip intentionally allows boss ending skip.
    if (curArea == kCoronaBossAreaId && nextArea != kCoronaBossAreaId &&
        (isNonGameplayStage(nextArea) ||
         (nextEp == 0xFF && nextArea != kDelfinoPlazaAreaId))) {
        gpApplication.mNextScene.mAreaID = kDelfinoPlazaAreaId;
        gpApplication.mNextScene.mEpisodeID = 0xFF;
        OSReport("[SMSO] corona boss leave → plaza hub (was %u/%u)\n", nextArea, nextEp);
        director->moveStage();
        return;
    }

    if (isNonGameplayStage(nextArea)) {
        if (nextEp == 0xFF)
            gpApplication.mNextScene.mEpisodeID = 0;
        OSReport("[SMSO] options/title next → moveStage ep%u\n",
                 gpApplication.mNextScene.mEpisodeID);
        director->moveStage();
        return;
    }

    if (isNonGameplayStage(gpApplication.mCurrentScene.mAreaID)) {
        director->moveStage();
        return;
    }

    // Episode 0xFF on a normal shine stage (area <= 60) must take the shine-select
    // routing below — NOT vanilla moveStage. The previous test used `area <= 60 ||`
    // which inverted the area check and forced moveStage for every story area.
    // Same-area 0xFF soft-reloads then skip exitStageCallbacks; Shadow Mario/Luigi
    // TexAnim MActors survive into the next init and crash mid-rebind (dolphin.log:
    // moveStage next=N/255 cur=N/E → initMarioModelSystem with no Stage exit).
    // After normalizeSirena / Pinna / mareUndersea, casino 0xFF is load 0/1,
    // Pinna beach→park 0xFF is a pinnaParco archive id, and mareUndersea 0xFF is
    // load 0 — all take moveStage here (ep != 0xFF). Test/debug areas > 60 always move.
    if (gpApplication.mNextScene.mAreaID > 60 ||
        gpApplication.mNextScene.mEpisodeID != 0xFF) {
        director->moveStage();
        return;
    }

    SMSGetShineStageFn getShineStage = shineStageForAreaFn();
    if (getShineStage(gpApplication.mNextScene.mAreaID) == -1) {
        director->moveStage();
        return;
    }

    // Delfino Plaza hub returns use next episode 0xFF as "load dolpic via vanilla
    // decideNextScenario" — NOT shine episode-select. Opening Delay Context 8 after
    // stageExit freezes (dolphin.log: next=1/255 cur=2/7 → Delay Context 8, no Stage
    // init). Options→plaza already bypasses via isNonGameplayStage; stage→plaza must
    // call moveStage the same way. Plaza→stage with 0xFF still opens episode-select
    // below (next area is the shine stage, not plaza).
    if (gpApplication.mNextScene.mAreaID == kDelfinoPlazaAreaId &&
        gpApplication.mNextScene.mEpisodeID == 0xFF) {
        OSReport("[SMSO] plaza hub return (ep 0xFF) → moveStage\n");
        director->moveStage();
        return;
    }

    // Single-episode load arenas (plaza secrets, stage secrets, bosses, rides).
    // Must moveStage(ep=0) BEFORE episode-select — Delay Context 8 → title
    // (dolphin.log: next=23/255, 33/255, 55/255 → area=15).
    if (gpApplication.mNextScene.mEpisodeID == 0xFF &&
        isSingleEpisodeLoadArea(gpApplication.mNextScene.mAreaID)) {
        gpApplication.mNextScene.mEpisodeID = 0;
        TFlagManager::smInstance->setFlag(0x40003, 0);
        OSReport("[SMSO] single-ep load area %u (ep 0xFF) → moveStage ep0\n",
                 gpApplication.mNextScene.mAreaID);
        director->moveStage();
        return;
    }

    const s32 nextShine = getShineStage(gpApplication.mNextScene.mAreaID);
    const s32 curShine = getShineStage(gpApplication.mCurrentScene.mAreaID);
    const bool isSameShineStage = curShine == nextShine;
    const bool isSameNormalStage =
        gpApplication.mCurrentScene.mAreaID == gpApplication.mNextScene.mAreaID;

    // After a loading-zone warpin cutscene, open the shine episode select instead of
    // taking BSE's same-shine shortcut that reloads the hub with the current episode.
    if (gpApplication.mNextScene.mEpisodeID == 0xFF && !isSameNormalStage &&
        isLocalMarioInStageExitState()) {
        beginEpisodeSelectTransition(director, curShine, nextShine);
        return;
    }

    if (isSameShineStage && !isSameNormalStage &&
        !TFlagManager::smInstance->getBool(0x50010)) {
        if (gpApplication.mNextScene.mEpisodeID == 0xFF) {
            // Sirena casino: Ep4 → casino0 (load 0), Ep5 → casino1 (load 1).
            // Boss/secret arenas are handled above; remaining same-shine 0xFF keeps
            // the current episode (hub soft-route).
            const u8 destArea = gpApplication.mNextScene.mAreaID;
            if (destArea == 14) {
                // Ep5 (mission 4) → casino1; anything else (incl. Ep4 mission 3) → casino0.
                // normalizeSirenaNextSceneForLoad should already have rewritten 0xFF using
                // the armed hotel mission — this is only a same-shine safety net.
                const u8 mission =
                    static_cast<u8>(TFlagManager::smInstance->getFlag(0x40003));
                gpApplication.mNextScene.mEpisodeID = (mission == 4) ? 1 : 0;
            } else if (isSingleEpisodeLoadArea(destArea)) {
                // Safety net if a single-ep door skipped the earlier branch.
                gpApplication.mNextScene.mEpisodeID = 0;
                TFlagManager::smInstance->setFlag(0x40003, 0);
            } else {
                gpApplication.mNextScene.mEpisodeID = gpApplication.mCurrentScene.mEpisodeID;
            }
            director->moveStage();
            return;
        }
    }

    beginEpisodeSelectTransition(director, curShine, nextShine);
}

} // namespace smso
