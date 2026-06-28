#include "stage_guard.hpp"
#include "comm_buffer.hpp"
#include "hide_seek.hpp"

#include <Dolphin/OS.h>
#include <JSystem/JGeometry/JGMVec.hxx>
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

static f32 vecDistSq(const TVec3f &a, const TVec3f &b) {
    const f32 dx = a.x - b.x;
    const f32 dy = a.y - b.y;
    const f32 dz = a.z - b.z;
    return dx * dx + dy * dy + dz * dz;
}

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

} // namespace

bool isNonGameplayStage(u8 areaId) {
    // BSE uses area 15 episode 0 for the file-select / options hub (data/scene/option.szs).
    return areaId == 15;
}

bool isSmsoAuthorizedStageTransition() {
    CommBuffer *buf = getCommBuffer();
    const bool launcherWarp = buf && (buf->bridgeFlags & BF_WARP_PENDING) != 0;
    return isHideSeekAuthorizedStageTransition() || launcherWarp || s_pendingAuthorizedStageMove;
}

bool shouldAllowMoveStage(TMarDirector *director) {
    if (isNonGameplayStage(gpApplication.mCurrentScene.mAreaID))
        return true;

    if (isSmsoAuthorizedStageTransition())
        return true;

    const u8 nextArea = gpApplication.mNextScene.mAreaID;
    const u8 nextEp = gpApplication.mNextScene.mEpisodeID;
    const u8 curArea = gpApplication.mCurrentScene.mAreaID;
    const u8 curEp = gpApplication.mCurrentScene.mEpisodeID;
    if (nextArea == curArea && nextEp == curEp)
        return true;

    // Remote puppets are no longer in Player Group, so in-game loading zones only
    // schedule moveStage for the local Mario. Permit cross-scene moves while online.
    CommBuffer *buf = getCommBuffer();
    if (buf && (buf->bridgeFlags & BF_CONNECTED) != 0)
        return true;

    // Solo fallback: director can sit in WARPING (0x09) after setNextStage even when
    // Mario's status id no longer matches our exit-state table.
    if (director && director->mGameState == 0x09u)
        return true;

    return isLocalMarioInStageExitState();
}

void authorizeLauncherStageMove() {
    s_pendingAuthorizedStageMove = true;
}

void initStageGuard() {
    s_pendingAuthorizedStageMove = false;
    OSReport("[SMSO] Stage guard installed — loading zones are local-only (moveStage gate)\n");
}

void clearAuthorizedStageMovePending() {
    s_pendingAuthorizedStageMove = false;
}

void clearBlockedLoadingZoneTransition(TMarDirector *director) {
    if (!director)
        return;

    // Blocked moveStage leaves mNextScene reverted but mGameState can stay WARPING
    // (0x09) while a loading zone keeps calling setNextStage. Bridge treats that as
    // DS_WARPING and stops writing remote snapshots — remotes never activate.
    if (director->mGameState == 0x09u)
        director->mGameState = 0x04u;
}

// BSE moveStage_override (area.cpp) inlined here — SMSO is a separate Kuribo module and
// cannot link against new BSE kernel exports without a full engine rebuild.
void performSmsoMoveStage(TMarDirector *director) {
    if (!director)
        return;

    if (isNonGameplayStage(gpApplication.mCurrentScene.mAreaID)) {
        director->moveStage();
        return;
    }

    if (gpApplication.mNextScene.mAreaID <= 60 || gpApplication.mNextScene.mEpisodeID != 0xFF) {
        director->moveStage();
        return;
    }

    SMSGetShineStageFn getShineStage = shineStageForAreaFn();
    if (getShineStage(gpApplication.mNextScene.mAreaID) == -1) {
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
            gpApplication.mNextScene.mEpisodeID = gpApplication.mCurrentScene.mEpisodeID;
            director->moveStage();
            return;
        }
    }

    beginEpisodeSelectTransition(director, curShine, nextShine);
}

} // namespace smso
