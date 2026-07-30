#include "hide_seek.hpp"
#include "stage_guard.hpp"

#include "comm_buffer.hpp"
#include "gx_hud_fence.hpp"
#include "mario_model_system.hpp"
#include "puppets.hpp"

#include <BetterSMS/area.hxx>
#include <BetterSMS/game.hxx>
#include <BetterSMS/loading.hxx>
#include <BetterSMS/module.hxx>
#include <BetterSMS/player.hxx>
#include <Dolphin/GX.h>
#include <Dolphin/MTX.h>
#include <Dolphin/OS.h>
#include <Dolphin/printf.h>
#include <Dolphin/string.h>
#include <JSystem/J2D/J2DOrthoGraph.hxx>
#include <JSystem/J2D/J2DPane.hxx>
#include <JSystem/J2D/J2DPrint.hxx>
#include <JSystem/J3D/J3DModel.hxx>
#include <JSystem/J3D/J3DShape.hxx>
#include <JSystem/JUtility/JUTColor.hxx>
#include <SMS/GC2D/GCConsole2.hxx>
#include <SMS/Camera/PolarSubCamera.hxx>
#include <SMS/Manager/FlagManager.hxx>
#include <SMS/MSound/MSound.hxx>
#include <SMS/MSound/MSoundSESystem.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/MarioCap.hxx>
#include <SMS/Player/MarioGamePad.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/raw_fn.hxx>

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;
extern MSound *gpMSound;
extern TApplication gpApplication;
extern CPolarSubCamera *gpCamera;

namespace smso {

namespace {

using StartAppearTimerFn = void (*)(TGCConsole2 *, int, s32);
using StartInsertTimerFn = void (*)(TGCConsole2 *);
using StartDisappearTimerFn = void (*)(TGCConsole2 *);
using StartMoveTimerFn = void (*)(TGCConsole2 *, int);
using StopMoveTimerFn = void (*)(TGCConsole2 *);
using SetTimerFn = void (*)(TGCConsole2 *, s32);
using CountShineFn = void (*)(TGCConsole2 *);
using StartBgmFn = void (*)(u32);
// Retail level-start "GO" banner (TConsoleStr::startAppearGo).
using StartAppearGoFn = void (*)(TConsoleStr *);

// gc-forever asset list / OST track 42: demo BGM index 0x26 = Race Fanfare (Il Piantissimo).
constexpr u32 kRaceFanfareBgm = 0x80010026u;

// Base durations are authored for 30 Hz. Scale with BetterSMS::getFrameRate() so
// 60/120 fps do not finish death recovery (setMario) while Mario is still dying.
// Cap wall-clock death → reload (~3s@30Hz). 420f (~14s) left players stuck in
// STATE_DEATH long after the drop finished (dolphin.log mid-tag death feel).
constexpr u16 kTaggedDeathTimeoutFrames30 = 90u;
// Retail death drop is ~1–1.5s wall-clock; keep a short floor before reload.
constexpr u16 kMinTaggedDeathFrames30 = 40u;
constexpr u8 kSeekerPromotionVfxDelayFrames = 12u;
constexpr u8 kShirtShapeIndex = 10u;
constexpr u32 kModelDataShapeNumOffset = 0x2Cu;
constexpr u32 kModelDataShapeTableOffset = 0x30u;
constexpr u32 kShapeDrawFlagsOffset = 0x8u;
constexpr u16 kCapGlassesFlag = 0x4u;
// doldecomp TMarDirector: race timer baseline + event stopwatch used by GCConsole2::setTimer(-1).
constexpr u32 kDirectorTimerStartOffset = 0xC8u;
constexpr u32 kDirectorEventStopwatchOffset = 0xE8u;

static bool s_hideSeekModeActive = false;
static bool s_tagActive = false;
static bool s_timerPanelVisible = false;
static bool s_retailTimerMoving = false;
static bool s_localWasHider = false;
static bool s_localWasSeeker = false;
static bool s_localSeekerLook = false;
static bool s_roundFanfareWas = false;
static bool s_roundCompleteWas = false;
static bool s_timerResetWas = false;
static bool s_roundEndFanfarePlayed = false;
static bool s_taggedDeathActive = false;
static bool s_hiderTimerRunning = false;
static bool s_localHasRunHiderTimer = false;
static u8 s_handledTagEventId = 0;
static u8 s_lastTagSoundEventId = 0;
static u8 s_deathAreaId = 0;
static u8 s_deathEpisodeId = 0;
// Sirena hotel: mounted load scenario vs mission-overridden director episode.
// Death reload must remount load + re-arm mission override or remotes vanish.
static u8 s_deathMissionEpisode = 0xFF;
static bool s_deathStageCaptured = false;
static u16 s_taggedDeathElapsed = 0;
static u16 s_taggedDeathTimeout = 0;
static u16 s_minTaggedDeathFrames = kMinTaggedDeathFrames30;
static s32 s_frozenTimerCentiseconds = 0;
static bool s_resumeDeathRecoveryAfterReload = false;
static bool s_envDeathRecovery = false;
static bool s_envDeathPromotionPending = false;
static u8 s_seekerLookRetryFrames = 0;
static u8 s_pendingSeekerPromotionVfxFrames = 0;
// Defer lethal state changes off stageUpdate (pre-direct) onto the next local
// Mario playerUpdate — changePlayerDropping mid-tree / before playerControl is
// the frame-safe window. Coalesce duplicate tag/death edges into one apply.
static bool s_pendingForceDeathAnim = false;
static bool s_pendingDeathStageReload = false;
static bool s_deathFinishBusy = false;
static bool s_hideSeekHooksInstalled = false;
static bool s_allowDeathStageTransition = false;
static bool s_allowLauncherStageTransition = false;
static bool s_authorizedStageExitPending = false;
static bool s_tagPlayStageValid = false;
static u8 s_tagPlayStageArea = 0;
static u8 s_tagPlayStageEpisode = 0;
// Hotel mission override (director / 0x40003) when load ≠ mission — survives death remount.
static u8 s_tagPlayStageMission = 0xFF;
static u16 s_tagDeathGraceFrames = 0;
static u32 s_lastNetworkRoundStartMs = 0;
static bool s_graceWasActive = false;
static bool s_tagWasActiveForGo = false;
static bool s_seekerGraceInputLocked = false;
static bool s_seekerGracePosPinned = false;
static u8 s_seekerGraceGapHoldFramesLeft = 0;
static f32 s_seekerGracePinX = 0.0f;
static f32 s_seekerGracePinY = 0.0f;
static f32 s_seekerGracePinZ = 0.0f;
static u8 s_graceEndFlashFrames = 0;
// After mid-round warp (not death), dismiss stage-entry demos so Tag stays playable.
static u8 s_hideSeekIntroSkipFrames = 0;
// True once death recovery has queued a same-stage reload (prevents soft setMario into a
// black fade before moveStage completes).
static bool s_deathReloadQueued = false;
// Post-reload settle: wait for retail STATE_NORMAL / setMario, then one-shot restore.
// Replaces builds 67–68 sticky mIsDisableInput / VerifyingMove stick gates.
static bool s_deathReloadSettleActive = false;
static u16 s_deathReloadSettleFrames = 0;
// After mid-tag death reload: keep forcing TIME HUD appear until retail mIsTimerCard
// is live. Build 70 one-shot remount was gated on GMF_TAG_ACTIVE (often false that
// frame) and seeker promotion could set a stale s_timerPanelVisible first.
static bool s_deathReloadTimerRemountPending = false;
static u16 s_deathReloadTimerRemountFrames = 0;

constexpr u16 kTagDeathGraceFrames = 360u;
constexpr u8 kGraceEndFlashFrames = 18u;
constexpr u8 kHideSeekIntroSkipFrames = 120u;
// After STATE_NORMAL, allow retail entry a moment; then skip cinematic if still active.
constexpr u16 kDeathReloadSettleBudgetFrames = 180u;
// Retry appear across post-reload HUD settle / tag-flag flicker (~5s at 60fps).
constexpr u16 kDeathReloadTimerRemountBudgetFrames = 300u;
// Match retail SMS HUD digit size (same nominal as nametag / coin counters).
constexpr int kGraceHudFontSize = 22;
constexpr int kJ2DPrintDefaultLeading = static_cast<int>(0x80000000);

static bool isLocalMarioInDeathState(const TMario *mario);
static void applyForcedTaggedDeathAnim(TMario *mario);
static bool isLocalSlotSeeker(const CommBuffer *buf);

static u8 resolveHotelMissionOverride(u8 loadEpisode, u8 directorEpisode, u8 flagMission,
                                      u8 pinnedMission) {
    // Prefer the highest non-load candidate so King Boo (mission 4, load 2) survives
    // when director/flag/pin briefly disagree during death remount.
    u8 mission = 0;
    bool have = false;
    const u8 candidates[] = {directorEpisode, flagMission, pinnedMission};
    for (u8 v : candidates) {
        if (v == 0xFF || v == loadEpisode)
            continue;
        if (!have || v > mission) {
            mission = v;
            have = true;
        }
    }
    return have ? mission : 0xFF;
}

static void pinTagPlayStage(TMarDirector *director) {
    if (!director)
        return;

    s_tagPlayStageArea = director->mAreaID;
    // Pin the mounted load scenario (CurrentScene), not mission-overridden director episode.
    // Bianco/Ricco match; Sirena hotel would otherwise pin mission 3/4 as a load id.
    s_tagPlayStageEpisode = gpApplication.mCurrentScene.mEpisodeID;
    s_tagPlayStageMission = 0xFF;
    if (director->mAreaID == 7) {
        const u8 flag = TFlagManager::smInstance
                            ? static_cast<u8>(TFlagManager::smInstance->getFlag(0x40003))
                            : 0xFF;
        s_tagPlayStageMission = resolveHotelMissionOverride(
            s_tagPlayStageEpisode, director->mEpisodeID, flag, 0xFF);
    }
    s_tagPlayStageValid = true;
}

/// Write next-scene for tag/death leave without wiping hotel mission (0x40003=load alone
/// caused stageInit director=load after warp-in had mission — remotes failed isSameStage).
static void writeDeathOrTagNextScene(u8 destArea, u8 destLoad) {
    gpApplication.mNextScene.mAreaID = destArea;
    gpApplication.mNextScene.mEpisodeID = destLoad;
    TFlagManager::smInstance->setFlag(0x40002, 0);

    u8 mission = destLoad;
    if (s_deathStageCaptured && s_deathMissionEpisode != 0xFF)
        mission = s_deathMissionEpisode;
    else if (destArea == 7 && s_tagPlayStageValid && s_tagPlayStageMission != 0xFF)
        mission = s_tagPlayStageMission;

    TFlagManager::smInstance->setFlag(0x40003, mission);
    if (destArea == 7 && mission != destLoad)
        armHotelMissionEpisodeSync(mission);
    BetterSMS::Loading::setLoading(false);
}

static bool isHideSeekTagRoundActive(const CommBuffer *buf) {
    if (!buf)
        return false;

    const GameModeState &gm = buf->gameModeState;
    return gm.mode == GM_HIDE_SEEK && (gm.flags & GMF_TAG_ACTIVE) != 0;
}

static StartAppearTimerFn startAppearTimerFn() {
    return reinterpret_cast<StartAppearTimerFn>(SMS_PORT_REGION(0x8014AFCC, 0x8013FC5C, 0, 0));
}

static StartInsertTimerFn startInsertTimerFn() {
    return reinterpret_cast<StartInsertTimerFn>(SMS_PORT_REGION(0x8014AE30, 0x8013FAC0, 0, 0));
}

static StartDisappearTimerFn startDisappearTimerFn() {
    return reinterpret_cast<StartDisappearTimerFn>(SMS_PORT_REGION(0x8014B1D8, 0x8013FE68, 0, 0));
}

static StartMoveTimerFn startMoveTimerFn() {
    return reinterpret_cast<StartMoveTimerFn>(SMS_PORT_REGION(0x80147568, 0x8013c1ec, 0, 0));
}

static StopMoveTimerFn stopMoveTimerFn() {
    return reinterpret_cast<StopMoveTimerFn>(SMS_PORT_REGION(0x80147550, 0x8013C1D4, 0, 0));
}

static SetTimerFn setTimerFn() {
    return reinterpret_cast<SetTimerFn>(SMS_PORT_REGION(0x8014836C, 0x8013CFF0, 0, 0));
}

static CountShineFn countShineFn() {
    return reinterpret_cast<CountShineFn>(SMS_PORT_REGION(0x80147A0C, 0x8013C690, 0, 0));
}

static StartBgmFn startBgmFn() {
    return reinterpret_cast<StartBgmFn>(SMS_PORT_REGION(0x80016978, 0x800169D4, 0, 0));
}

static StartAppearGoFn startAppearGoFn() {
    // NTSC-U / PAL from BSE maps (us.map / eu.map).
    return reinterpret_cast<StartAppearGoFn>(SMS_PORT_REGION(0x80171BB8, 0x801679d0, 0, 0));
}

static TGCConsole2 *getConsole(TMarDirector *director) {
    return director && director->mGCConsole ? director->mGCConsole : nullptr;
}

// One-shot retail "GO" HUD — same path as stage-load start banner (processGo).
static void pulseRetailGoHud() {
    TGCConsole2 *console = getConsole(gpMarDirector);
    if (!console || !console->mConsoleStr)
        return;

    StartAppearGoFn fn = startAppearGoFn();
    if (!fn)
        return;
    fn(console->mConsoleStr);
}

static u16 scaleFramesFrom30(u16 frames30) {
    f32 rate = BetterSMS::getFrameRate();
    if (rate < 1.0f)
        rate = 30.0f;
    const u32 scaled = static_cast<u32>(static_cast<f32>(frames30) * (rate / 30.0f) + 0.5f);
    if (scaled == 0)
        return 1;
    if (scaled > 0xFFFFu)
        return 0xFFFFu;
    return static_cast<u16>(scaled);
}

static void armTaggedDeathTimers() {
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = scaleFramesFrom30(kTaggedDeathTimeoutFrames30);
    s_minTaggedDeathFrames = scaleFramesFrom30(kMinTaggedDeathFrames30);
}

static void applyForcedTaggedDeathAnim(TMario *mario) {
    if (!mario)
        return;

    // Avoid floorDamageExec here: it assumes collision/playerControl context and can
    // start overlapping death/game-over side-effects. Force the death drop once.
    mario->mInvincibilityFrames = 0;
    mario->mAttributes.mIsGameOver = false;
    if (mario->mHealth > 0)
        mario->mHealth = 0;
    if (mario->mState != TMario::STATE_DEATH)
        mario->changePlayerDropping(TMario::STATE_DEATH, 0);
}

static OSStopwatch *getDirectorEventStopwatch(TMarDirector *director) {
    if (!director)
        return nullptr;
    return reinterpret_cast<OSStopwatch *>(reinterpret_cast<u8 *>(director) +
                                           kDirectorEventStopwatchOffset);
}

static OSTime *getDirectorTimerStartMark(TMarDirector *director) {
    if (!director)
        return nullptr;
    return reinterpret_cast<OSTime *>(reinterpret_cast<u8 *>(director) + kDirectorTimerStartOffset);
}

static J3DShape *getRetailShapePointer(J3DModelData *modelData, u16 index) {
    if (!modelData)
        return nullptr;

    auto *raw = reinterpret_cast<u8 *>(modelData);
    const u16 shapeNum = *reinterpret_cast<const u16 *>(raw + kModelDataShapeNumOffset);
    if (index >= shapeNum)
        return nullptr;

    J3DShape **table = *reinterpret_cast<J3DShape ***>(raw + kModelDataShapeTableOffset);
    return table ? table[index] : nullptr;
}

static void setRetailShapeDrawFlag(J3DShape *shape, u32 flag, bool enabled) {
    if (!shape)
        return;

    u32 &flags = *reinterpret_cast<u32 *>(reinterpret_cast<u8 *>(shape) + kShapeDrawFlagsOffset);
    if (enabled)
        flags |= flag;
    else
        flags &= ~flag;
}

static void setShineShirtShapeVisible(TMario *mario, bool visible) {
    if (!mario || !mario->mModelData || !mario->mModelData->mModel ||
        !mario->mModelData->mModel->mModelData)
        return;

    J3DShape *shirtShape =
        getRetailShapePointer(mario->mModelData->mModel->mModelData, kShirtShapeIndex);
    if (!shirtShape)
        return;

    setRetailShapeDrawFlag(shirtShape, 0x1, !visible);
}

static void setSeekerGlassesFlag(TMario *mario, bool enabled) {
    if (!mario || !mario->mCap)
        return;

    u16 *capFlags = reinterpret_cast<u16 *>(reinterpret_cast<u8 *>(mario->mCap) + 0x4);
    if (enabled)
        *capFlags = static_cast<u16>(*capFlags | kCapGlassesFlag);
    else
        *capFlags = static_cast<u16>(*capFlags & ~kCapGlassesFlag);
}

static void playTagStartSound(TMario *mario) {
    if (!gpMSound || !mario)
        return;

    const u32 soundId = MSD_SE_SY_RACE_START;
    if (!gpMSound->gateCheck(soundId))
        return;

    const Vec pos = {mario->mTranslation.x, mario->mTranslation.y, mario->mTranslation.z};
    MSoundSESystem::MSoundSE::startSoundActor(soundId, &pos, 0, nullptr, 0, 4);
}

static void playGraceEndSound(TMario *mario) {
    if (!gpMSound || !mario)
        return;

    // Soft "go" cue when seekers are released.
    const u32 soundId = MSD_SE_SY_RACE_FIRE;
    if (!gpMSound->gateCheck(soundId))
        return;

    const Vec pos = {mario->mTranslation.x, mario->mTranslation.y, mario->mTranslation.z};
    MSoundSESystem::MSoundSE::startSoundActor(soundId, &pos, 0, nullptr, 0, 4);
}

static bool isHideSeekGraceActive(const GameModeState &gm) {
    return (gm.flags & GMF_GRACE_ACTIVE) != 0;
}

static constexpr u32 kMarioGamePadFlagsOffset = 0xE2u;
static constexpr u16 kMarioGamePadControlEnabledFlag = 0x2u;

static void setLocalPadControlEnabled(TMario *mario, bool enabled) {
    if (!mario || !mario->mController)
        return;

    u8 *padRaw = reinterpret_cast<u8 *>(mario->mController);
    u16 &flags = *reinterpret_cast<u16 *>(padRaw + kMarioGamePadFlagsOffset);
    if (enabled)
        flags = static_cast<u16>(flags | kMarioGamePadControlEnabledFlag);
    else
        flags = static_cast<u16>(flags & ~kMarioGamePadControlEnabledFlag);
}

static void releaseSeekerGraceInputLock(TMario *mario) {
    if (!s_seekerGraceInputLocked)
        return;

    // Full pad/BSE restore — see restoreLocalMarioControl (BSE mirrors mIsDisableInput).
    restoreLocalMarioControl(gpMarDirector, mario);
    s_seekerGraceInputLocked = false;
    s_seekerGracePosPinned = false;
    s_seekerGraceGapHoldFramesLeft = 0;
}

static void clearDeathReloadSettle() {
    s_deathReloadSettleActive = false;
    s_deathReloadSettleFrames = 0;
}

static void armDeathReloadSettle(TMarDirector *director) {
    s_deathReloadSettleActive = true;
    s_deathReloadSettleFrames = 0;
    if (director) {
        OSReport("[SMSO] HideSeek death-reload settle armed area=%u ep=%u\n", director->mAreaID,
                 director->mEpisodeID);
    }
}

// One-shot pad/BSE restore after retail stage entry — not a per-frame sticky loop.
static void oneShotRestoreControlAfterDeath(TMarDirector *director, TMario *mario) {
    restoreLocalMarioControl(director, mario);
    setLocalPadControlEnabled(mario, true);
    s_seekerGraceInputLocked = false;
    s_seekerGracePosPinned = false;
    s_seekerGraceGapHoldFramesLeft = 0;
}

static void logDeathControlDiag(const char *tag, TMarDirector *director, TMario *mario) {
    if (!director || !mario)
        return;
    u8 disableInput = 0;
    u8 warpActive = 0;
    u8 warpState = 0xFF;
    if (auto *playerData = BetterSMS::Player::getData(mario)) {
        disableInput = playerData->mCollisionFlags.mIsDisableInput ? 1u : 0u;
        warpActive = playerData->mIsWarpActive ? 1u : 0u;
        warpState = playerData->mWarpState;
    }
    const u8 demoCam = (gpCamera && gpCamera->isSimpleDemoCamera()) ? 1u : 0u;
    u16 padFlags = 0;
    if (mario->mController) {
        u8 *padRaw = reinterpret_cast<u8 *>(mario->mController);
        padFlags = *reinterpret_cast<u16 *>(padRaw + kMarioGamePadFlagsOffset);
    }
    OSReport("[SMSO] HideSeek %s area=%u ep=%u state=%u mario=0x%08x "
             "read=%u disable=%u bseDis=%u warp=%u/%u demoCam=%u pad=0x%04x\n",
             tag, director->mAreaID, director->mEpisodeID, director->mCurState,
             mario->mState, mario->mController && mario->mController->mState.mReadInput ? 1u : 0u,
             mario->mController && mario->mController->mState.mDisable ? 1u : 0u, disableInput,
             warpActive, warpState, demoCam, padFlags);
}

static void applySeekerGraceFreeze(TMario *mario) {
    if (!mario || !mario->mController)
        return;

    // BSE updateCollisionContext overwrites mReadInput from mIsDisableInput every
    // Mario perform. Stage-update pad locks alone are cleared before playerControl,
    // so seekers could walk during Start Tag grace. Keep both in sync.
    if (auto *playerData = BetterSMS::Player::getData(mario))
        playerData->mCollisionFlags.mIsDisableInput = true;

    // Clear the retail "control enabled" pad flag — mDisable alone is not enough
    // once BSE / intro-skip paths keep re-arming read input.
    setLocalPadControlEnabled(mario, false);

    mario->mController->mState.mReadInput = false;
    mario->mController->mState.mDisable = true;
    mario->mController->mStickX = 0.0f;
    mario->mController->mStickY = 0.0f;
    mario->mController->mMeaning = 0;
    mario->mController->mFrameMeaning = 0;
    // JUTGamePad stick the moveset / BSE airborn path actually reads.
    mario->mController->mControlStick.mStickX = 0.0f;
    mario->mController->mControlStick.mStickY = 0.0f;
    mario->mController->mControlStick.mLengthFromNeutral = 0.0f;
    mario->mController->mButtons.mFrameInput = 0;
    mario->mController->mButtons.mInput = 0;
    mario->mController->mButtons.mRapidInput = 0;

    // Hard pin: zero all velocity and restore the grace-start position every frame so
    // residual physics / slope slide cannot creep the seeker.
    if (!s_seekerGracePosPinned) {
        s_seekerGracePinX = mario->mTranslation.x;
        s_seekerGracePinY = mario->mTranslation.y;
        s_seekerGracePinZ = mario->mTranslation.z;
        s_seekerGracePosPinned = true;
    } else {
        mario->mTranslation.x = s_seekerGracePinX;
        mario->mTranslation.y = s_seekerGracePinY;
        mario->mTranslation.z = s_seekerGracePinZ;
    }
    mario->mSpeed.x = 0.0f;
    mario->mSpeed.y = 0.0f;
    mario->mSpeed.z = 0.0f;
    mario->mForwardSpeed = 0.0f;
    s_seekerGraceInputLocked = true;
}

static void updateHideSeekGrace(TMario *mario, TMarDirector *director, const GameModeState &gm,
                                bool localIsSeeker, bool tagActive) {
    // Flag is authoritative; graceRemainingMs covers one-frame mailbox gaps when a
    // failed remote-sync write briefly drops GMF_GRACE_ACTIVE while the timer is live.
    const bool graceFlag = isHideSeekGraceActive(gm);
    const bool graceByTimer = gm.graceRemainingMs > 0;
    const bool graceActive = tagActive && (graceFlag || graceByTimer);
    const bool notDying =
        !s_taggedDeathActive && !isLocalMarioInDeathState(mario);
    // Want freeze for the whole seeker grace window. Do NOT gate on
    // isStageEntryDemoActive — that flag flaps (simple demo camera) and used to
    // release the lock / clear the position pin so seekers walked freely.
    const bool wantFreeze = localIsSeeker && graceActive && notDying;
    // First arm only once the stage is playable so we do not pin mid fly-in.
    const bool stagePlayable =
        director && director->mCurState >= TMarDirector::STATE_NORMAL;
    const bool canArmFreeze = wantFreeze && stagePlayable;

    constexpr u8 kSeekerGraceGapHoldFrames = 8;
    bool freezeSeeker = false;
    if (canArmFreeze || (wantFreeze && s_seekerGraceInputLocked)) {
        freezeSeeker = true;
        s_seekerGraceGapHoldFramesLeft = kSeekerGraceGapHoldFrames;
    } else if (localIsSeeker && tagActive && s_seekerGraceInputLocked &&
               s_seekerGraceGapHoldFramesLeft > 0) {
        // Brief grace-flag gap while still seeker/tag — keep locked.
        --s_seekerGraceGapHoldFramesLeft;
        freezeSeeker = true;
    }

    if (freezeSeeker)
        applySeekerGraceFreeze(mario);
    else {
        s_seekerGraceGapHoldFramesLeft = 0;
        releaseSeekerGraceInputLock(mario);
    }

    // Hiders: retail GO as soon as Start Tag begins (they can move during grace).
    // Seekers: wait until grace ends (freeze lifts) — same as before. If Tag starts
    // with no grace window, seekers also get GO on the tag rising edge.
    if (tagActive && !s_tagWasActiveForGo) {
        if (!localIsSeeker)
            pulseRetailGoHud();
        else if (!graceActive)
            pulseRetailGoHud();
    }

    // Rising edge: grace → hunt. Seekers pulse GO; everyone still gets the end cue.
    if (s_graceWasActive && !graceActive && tagActive) {
        playGraceEndSound(mario);
        if (localIsSeeker)
            pulseRetailGoHud();
        s_graceEndFlashFrames = kGraceEndFlashFrames;
    }

    if (!graceActive && !tagActive)
        s_graceEndFlashFrames = 0;
    else if (s_graceEndFlashFrames > 0)
        --s_graceEndFlashFrames;

    s_graceWasActive = graceActive;
    s_tagWasActiveForGo = tagActive;
}

static void armHideSeekIntroSkip(const char *reason, TMarDirector *director) {
    s_hideSeekIntroSkipFrames = kHideSeekIntroSkipFrames;
    if (director) {
        OSReport("[SMSO] HideSeek intro skip armed (%s) area=%u ep=%u frames=%u\n", reason,
                 director->mAreaID, director->mEpisodeID, s_hideSeekIntroSkipFrames);
    } else {
        OSReport("[SMSO] HideSeek intro skip armed (%s) frames=%u\n", reason,
                 s_hideSeekIntroSkipFrames);
    }
}

static void updateHideSeekIntroSkip(TMarDirector *director, TMario *mario) {
    if (!director || !mario)
        return;

    // Death reload settles itself after retail STATE_NORMAL — do not fight setMario here.
    if (s_deathReloadSettleActive || s_resumeDeathRecoveryAfterReload ||
        (s_taggedDeathActive && s_deathStageCaptured))
        return;

    if (s_hideSeekIntroSkipFrames == 0)
        return;

    if (!isStageEntryDemoActive(director, mario)) {
        s_hideSeekIntroSkipFrames = 0;
        return;
    }

    const bool playable = forceSkipStageEntryDemo(director, mario);
    if (playable || !isStageEntryDemoActive(director, mario)) {
        s_hideSeekIntroSkipFrames = 0;
        OSReport("[SMSO] Skipped stage entry demo -> area=%u episode=%u (HideSeek)\n",
                 director->mAreaID, director->mEpisodeID);
        return;
    }

    if (s_hideSeekIntroSkipFrames > 0)
        --s_hideSeekIntroSkipFrames;
}

static void drawOutlinedGraceHudLine(int centerX, int drawY, const char *text,
                                     const JUtility::TColor &top, const JUtility::TColor &bottom,
                                     const JUtility::TColor &outline, int fontSize, int outlinePx) {
    if (!gpSystemFont || !text || text[0] == '\0')
        return;

    J2DPrint printer(gpSystemFont, 1);
    printer.private_initiate(gpSystemFont, 1, kJ2DPrintDefaultLeading, outline, outline);
    printer.initiate();
    printer.setFontSize(fontSize, fontSize);
    printer.syncCharMetrics();

    for (int dy = -outlinePx; dy <= outlinePx; ++dy) {
        for (int dx = -outlinePx; dx <= outlinePx; ++dx) {
            if (dx == 0 && dy == 0)
                continue;
            const int adx = dx < 0 ? -dx : dx;
            const int ady = dy < 0 ? -dy : dy;
            const int cheb = adx > ady ? adx : ady;
            if (cheb < 1 || cheb > outlinePx)
                continue;
            printer.print(centerX + dx, drawY + dy, "%s", text);
        }
    }

    printer.private_initiate(gpSystemFont, 1, kJ2DPrintDefaultLeading, top, bottom);
    printer.initiate();
    printer.setFontSize(fontSize, fontSize);
    printer.syncCharMetrics();
    printer.print(centerX, drawY, "%s", text);
}

static void drawGraceCountdownText(int secondsLeft, bool localIsSeeker) {
    if (!gpSystemFont)
        return;

    char line[32];
    if (localIsSeeker)
        snprintf(line, sizeof(line), "HIDE %d", secondsLeft);
    else
        snprintf(line, sizeof(line), "HIDE TIME %d", secondsLeft);

    const int centerX = 200;
    // Bottom of the 480p ortho, above the FLUDD meter / TIME HUD cluster.
    const int y = 400;
    // Seeker-only lock cue — sits just above the shared countdown, clear of stock HUD.
    const int lockY = 372;

    // Match retail HUD: white→yellow fill with thick black outline.
    f32 outlineOffsetF = static_cast<f32>(kGraceHudFontSize) * 0.11f + 0.35f;
    if (outlineOffsetF < 1.0f)
        outlineOffsetF = 1.0f;
    if (outlineOffsetF > 3.0f)
        outlineOffsetF = 3.0f;
    const int outlinePx = static_cast<int>(outlineOffsetF + 0.5f);

    const JUtility::TColor fillTop(255, 255, 220, 255);
    const JUtility::TColor fillBottom(255, 200, 0, 255);
    const JUtility::TColor outline(0, 0, 0, 255);
    // Warm amber for the lock line — readable on the seeker blue wash.
    const JUtility::TColor lockTop(255, 230, 140, 255);
    const JUtility::TColor lockBottom(255, 170, 40, 255);

    if (localIsSeeker)
        drawOutlinedGraceHudLine(centerX, lockY, "CAN'T MOVE YET", lockTop, lockBottom, outline,
                                 kGraceHudFontSize, outlinePx);

    drawOutlinedGraceHudLine(centerX, y, line, fillTop, fillBottom, outline, kGraceHudFontSize,
                             outlinePx);
}

static bool tryGetHideSeekSlotWorldPos(const CommBuffer *buf, u8 slot, Vec &out) {
    if (!buf || slot >= MAX_PLAYERS)
        return false;

    if (slot == buf->localSlot) {
        if (!gpMarioAddress)
            return false;
        out.x = gpMarioAddress->mTranslation.x;
        out.y = gpMarioAddress->mTranslation.y;
        out.z = gpMarioAddress->mTranslation.z;
        return true;
    }

    const PlayerSnapshot &snap = buf->remoteSnapshots[slot];
    if (snap.connected == 0)
        return false;

    out.x = snap.position.x;
    out.y = snap.position.y;
    out.z = snap.position.z;
    return true;
}

// doldecomp MapObjHide.cpp TFruitBasket::countFruit — matching-fruit deposit cue.
static void playHideSeekTagSound(const CommBuffer *buf, const GameModeState &gm) {
    if (!gpMSound)
        return;

    Vec pos = {0.0f, 0.0f, 0.0f};
    if (!tryGetHideSeekSlotWorldPos(buf, gm.lastTaggedSlot, pos)) {
        if (!gpMarioAddress)
            return;
        pos.x = gpMarioAddress->mTranslation.x;
        pos.y = gpMarioAddress->mTranslation.y;
        pos.z = gpMarioAddress->mTranslation.z;
    }

    if (gpMSound->gateCheck(MSD_SE_IT_SOCCER_GOAL))
        MSoundSESystem::MSoundSE::startSoundActor(MSD_SE_IT_SOCCER_GOAL, &pos, 0, nullptr, 0, 4);
    if (gpMSound->gateCheck(MSD_SE_FGM_SOCCER_GOAL))
        gpMSound->startSoundSystemSE(MSD_SE_FGM_SOCCER_GOAL, 0, nullptr, 0);
}

static void playRoundCompleteFanfare(TMarDirector *director, TMario *mario) {
    OSReport("[SMSO] HideSeek round-end fanfare play (area=%u ep=%u)\n",
             director ? director->mAreaID : 0,
             director ? director->mEpisodeID : 0);

    TGCConsole2 *console = getConsole(director);
    if (console)
        countShineFn()(console);

    // Prefer system SE — actor SE is often gated out mid death-fade / warp.
    if (gpMSound) {
        if (gpMSound->gateCheck(MSD_SE_FGM_SOCCER_GOAL))
            gpMSound->startSoundSystemSE(MSD_SE_FGM_SOCCER_GOAL, 0, nullptr, 0);
        if (gpMSound->gateCheck(MSD_SE_SY_RACE_FIRE)) {
            MSoundSESystem::MSoundSE::startSoundSystemSE(MSD_SE_SY_RACE_FIRE, 0, nullptr, 0);
            if (mario) {
                const Vec pos = {mario->mTranslation.x, mario->mTranslation.y,
                                 mario->mTranslation.z};
                MSoundSESystem::MSoundSE::startSoundActor(MSD_SE_SY_RACE_FIRE, &pos, 0,
                                                         nullptr, 0, 4);
            }
        }
    }

    // Race Fanfare demo BGM (Il Piantissimo / OST 42). Safe even if mario is null.
    StartBgmFn bgm = startBgmFn();
    if (bgm)
        bgm(kRaceFanfareBgm);
}

static void applyLocalSeekerLookEdge(TMario *mario, bool wantSeekerLook) {
    if (!mario || wantSeekerLook == s_localSeekerLook)
        return;

    applyHideSeekPlayerCosmetics(mario, wantSeekerLook, false);
    if (wantSeekerLook)
        mario->wearGlass();
    else
        mario->takeOffGlass();
    s_localSeekerLook = wantSeekerLook;
}

static void stopRetailTimerMovement(TGCConsole2 *console) {
    if (!console)
        return;

    if (s_retailTimerMoving) {
        stopMoveTimerFn()(console);
        s_retailTimerMoving = false;
    }
}

// doldecomp GCConsole2::setTimer — explicit values are centiseconds (1/100 sec).
static void setTimerCentiseconds(TGCConsole2 *console, s32 centiseconds) {
    if (!console)
        return;

    if (centiseconds < 0)
        centiseconds = 0;

    setTimerFn()(console, centiseconds);
}

static s32 readDirectorRaceCentiseconds(TMarDirector *director) {
    if (!director)
        return 0;

    OSStopwatch *watch = getDirectorEventStopwatch(director);
    OSTime *startMark = getDirectorTimerStartMark(director);
    if (!watch || !startMark)
        return 0;

    const s64 startMs = OSTicksToMilliseconds(*startMark);
    const s64 nowMs = OSTicksToMilliseconds(OSCheckStopwatch(watch));
    if (nowMs <= startMs)
        return 0;

    return static_cast<s32>((nowMs - startMs) / 10);
}

// doldecomp MarDirector + GCConsole2::setTimer(-1): sync count-up display from director stopwatch.
static void syncTimerFromDirectorStopwatch(TGCConsole2 *console) {
    if (!console)
        return;

    setTimerFn()(console, -1);
}

static void resetDirectorRaceStopwatch(TMarDirector *director) {
    if (!director)
        return;

    OSStopwatch *watch = getDirectorEventStopwatch(director);
    OSTime *startMark = getDirectorTimerStartMark(director);
    if (!watch || !startMark)
        return;

    OSStartStopwatch(watch);
    *startMark = OSCheckStopwatch(watch);
}

static void backdateDirectorRaceStopwatch(TMarDirector *director, s32 elapsedCentiseconds) {
    if (!director || elapsedCentiseconds <= 0)
        return;

    OSStopwatch *watch = getDirectorEventStopwatch(director);
    OSTime *startMark = getDirectorTimerStartMark(director);
    if (!watch || !startMark)
        return;

    OSStartStopwatch(watch);
    const OSTime now = OSCheckStopwatch(watch);
    const s64 backMs = static_cast<s64>(elapsedCentiseconds) * 10;
    *startMark = now - OSMillisecondsToTicks(backMs);
}

// Never decrease a captured hider run — post-reload director stopwatches restart near 0
// and must not clobber the pre-death freeze (build 71: 6146 → 3 on control-ready).
static void adoptFrozenTimerCentiseconds(s32 centiseconds, const char *reason) {
    if (centiseconds <= 0)
        return;

    const s32 before = s_frozenTimerCentiseconds;
    if (centiseconds > s_frozenTimerCentiseconds) {
        s_frozenTimerCentiseconds = centiseconds;
        OSReport("[SMSO] HideSeek timer freeze adopt frozenCs=%d (was %d) reason=%s\n",
                 s_frozenTimerCentiseconds, before, reason ? reason : "?");
    }
    s_localHasRunHiderTimer = true;
}

static void freezeHiderTimer(TMarDirector *director, TGCConsole2 *console) {
    if (!s_hiderTimerRunning)
        return;

    // Read before stopMoveTimer — retail stopMoveTimer syncs the director baseline to now,
    // which would zero out elapsed time on resumed rounds (doldecomp GCConsole2::stopMoveTimer).
    const s32 centiseconds = readDirectorRaceCentiseconds(director);
    stopRetailTimerMovement(console);
    s_hiderTimerRunning = false;
    adoptFrozenTimerCentiseconds(centiseconds, "freezeHider");
    if (console)
        setTimerCentiseconds(console, s_frozenTimerCentiseconds);
}

static void captureHiderTimerSnapshot(TMarDirector *director, TGCConsole2 *console) {
    if (!director)
        return;

    // Lifetime seekers (started the round as seeker, never ran a hider count-up) must
    // stay at 0:00. Sampling the retail director stopwatch after death reload adopts
    // unrelated race-timer noise and wrongly sets s_localHasRunHiderTimer.
    if (!s_hiderTimerRunning && !s_localHasRunHiderTimer && !s_localWasHider)
        return;

    if (s_hiderTimerRunning) {
        freezeHiderTimer(director, console);
    } else if (s_localHasRunHiderTimer) {
        // Already frozen — do not re-sample a post-reload director clock.
        return;
    } else {
        // Was a hider this round but count-up never started (or panel already torn down).
        const s32 centiseconds = readDirectorRaceCentiseconds(director);
        if (centiseconds > 0)
            adoptFrozenTimerCentiseconds(centiseconds, "captureSnapshot");
    }
}

// Capture before network role flip stops the hider stopwatch (later tags in 3+ player rounds).
static void tryCaptureLocalTagTimerOnNewEvent(TMarDirector *director, TGCConsole2 *console,
                                              const CommBuffer *buf, const GameModeState &gm) {
    if (!director || !console || !buf)
        return;
    if (gm.tagEventId == 0 || gm.tagEventId == s_handledTagEventId)
        return;
    if (gm.lastTaggedSlot != buf->localSlot)
        return;

    captureHiderTimerSnapshot(director, console);
}

static void ensureFrozenTimerPanelVisible(TGCConsole2 *console) {
    if (!console || s_timerPanelVisible)
        return;

    startAppearTimerFn()(console, 0, 0);
    startInsertTimerFn()(console);
    s_timerPanelVisible = true;
    setTimerCentiseconds(console, s_frozenTimerCentiseconds);
}

static s32 seekerDisplayCentiseconds() {
    // Prefer frozen value even if hasRun was cleared mid-reload; seekers who never hid
    // keep frozen at 0.
    if (s_frozenTimerCentiseconds > 0)
        return s_frozenTimerCentiseconds;
    return s_localHasRunHiderTimer ? s_frozenTimerCentiseconds : 0;
}

static void ensureSeekerTimerPanelVisible(TGCConsole2 *console) {
    if (!console)
        return;

    stopRetailTimerMovement(console);
    const s32 display = seekerDisplayCentiseconds();
    if (!s_timerPanelVisible) {
        startAppearTimerFn()(console, 0, 0);
        startInsertTimerFn()(console);
        s_timerPanelVisible = true;
    }

    setTimerCentiseconds(console, display);
}

static void hideTimerPanel(TGCConsole2 *console) {
    if (!console || !s_timerPanelVisible)
        return;

    stopRetailTimerMovement(console);
    startDisappearTimerFn()(console);
    s_timerPanelVisible = false;
}

// Stage reload rebuilds GCConsole2; module statics survive. Drop the stale
// "panel visible" binding so ensure*/show* remount on the new console without
// wiping frozen/hider elapsed time (needed after mid-tag death reload).
static void invalidateTimerPanelBinding() {
    s_timerPanelVisible = false;
    s_retailTimerMoving = false;
    s_hiderTimerRunning = false;
}

static void captureTimerThenInvalidatePanel(TMarDirector *director) {
    TGCConsole2 *console = getConsole(director);
    // Always snapshot before console teardown — do not require s_hiderTimerRunning
    // (tag/stop may have already cleared the running flag while director still holds time).
    captureHiderTimerSnapshot(director, console);
    stopRetailTimerMovement(console);
    // Binding only — preserve s_frozenTimerCentiseconds / s_localHasRunHiderTimer.
    invalidateTimerPanelBinding();
    OSReport("[SMSO] HideSeek timer capture-then-invalidate frozenCs=%d hasRun=%u\n",
             s_frozenTimerCentiseconds, s_localHasRunHiderTimer ? 1u : 0u);
}

// Retail startInsertTimer sets mIsTimerCard (unk34[10] @ 0x3E). Use that — not our
// module sticky — to know appear actually stuck on the live GCConsole2.
static bool isRetailTimerPanelLive(const TGCConsole2 *console) {
    return console && console->mIsTimerCard;
}

static void armDeathReloadTimerRemount(const char *reason) {
    s_deathReloadTimerRemountPending = true;
    s_deathReloadTimerRemountFrames = 0;
    invalidateTimerPanelBinding();
    OSReport("[SMSO] HideSeek death-reload timer remount armed (%s)\n",
             reason ? reason : "?");
}

static void tickDeathReloadTimerRemount(TMarDirector *director, TGCConsole2 *console,
                                        const CommBuffer *buf, bool localIsSeeker,
                                        bool tagActive);

// doldecomp GCConsole2::startAppearTimer(0, 0) — count-up panel (unk510 = true).
static void showTimerPanelAtZero(TMarDirector *director, TGCConsole2 *console) {
    if (!console)
        return;

    stopRetailTimerMovement(console);
    if (s_timerPanelVisible)
        hideTimerPanel(console);

    startAppearTimerFn()(console, 0, 0);
    startInsertTimerFn()(console);
    s_timerPanelVisible = true;
    resetDirectorRaceStopwatch(director);
    setTimerCentiseconds(console, 0);
}

static void beginHiderCountUp(TMarDirector *director, TGCConsole2 *console) {
    if (!console)
        return;

    if (!s_timerPanelVisible)
        showTimerPanelAtZero(director, console);
    else if (s_frozenTimerCentiseconds <= 0)
        resetDirectorRaceStopwatch(director);

    startMoveTimerFn()(console, 0);
    s_retailTimerMoving = true;
    s_hiderTimerRunning = true;
    s_localHasRunHiderTimer = true;
    syncTimerFromDirectorStopwatch(console);
}

static void resumeHiderTimerFromElapsedMs(TMarDirector *director, TGCConsole2 *console, u32 elapsedMs) {
    if (!console)
        return;

    const s32 centiseconds = static_cast<s32>(elapsedMs / 10u);
    // Network elapsed must not shrink a larger local freeze (death remount race).
    adoptFrozenTimerCentiseconds(centiseconds, "resumeNetwork");
    s_hiderTimerRunning = false;
    stopRetailTimerMovement(console);

    if (!s_timerPanelVisible)
        ensureFrozenTimerPanelVisible(console);
    else
        setTimerCentiseconds(console, s_frozenTimerCentiseconds);

    backdateDirectorRaceStopwatch(director, s_frozenTimerCentiseconds);
}

/// Force TIME HUD remount after death reload until retail reports the timer card
/// live. Does not rely on rising-edge tagActive (s_tagActive often stays true).
static void tickDeathReloadTimerRemount(TMarDirector *director, TGCConsole2 *console,
                                        const CommBuffer *buf, bool localIsSeeker,
                                        bool tagActive) {
    if (!s_deathReloadTimerRemountPending)
        return;

    ++s_deathReloadTimerRemountFrames;

    if (!buf || buf->gameModeState.mode != GM_HIDE_SEEK) {
        s_deathReloadTimerRemountPending = false;
        OSReport("[SMSO] HideSeek death-reload timer remount abort (not HnS) frame=%u\n",
                 s_deathReloadTimerRemountFrames);
        return;
    }

    // Network GMF_TAG_ACTIVE can flicker false across reload; keep sticky s_tagActive.
    const bool wantTimer = tagActive || s_tagActive;
    if (!wantTimer) {
        if (s_deathReloadTimerRemountFrames >= kDeathReloadTimerRemountBudgetFrames) {
            s_deathReloadTimerRemountPending = false;
            OSReport("[SMSO] HideSeek death-reload timer remount timeout (no tag) "
                     "frame=%u\n",
                     s_deathReloadTimerRemountFrames);
        }
        return;
    }

    if (!console) {
        if ((s_deathReloadTimerRemountFrames % 30u) == 1u)
            OSReport("[SMSO] HideSeek death-reload timer remount wait console=null "
                     "frame=%u\n",
                     s_deathReloadTimerRemountFrames);
        if (s_deathReloadTimerRemountFrames >= kDeathReloadTimerRemountBudgetFrames) {
            s_deathReloadTimerRemountPending = false;
            OSReport("[SMSO] HideSeek death-reload timer remount fail console=null\n");
        }
        return;
    }

    // Always re-invalidate so ensure*/show* call startAppearTimer on this console.
    invalidateTimerPanelBinding();

    if (localIsSeeker) {
        ensureSeekerTimerPanelVisible(console);
    } else if (s_frozenTimerCentiseconds > 0 || buf->gameModeState.roundStartMs > 0) {
        ensureFrozenTimerPanelVisible(console);
        if (tagActive) {
            // Prefer the larger of local freeze vs network authority elapsed.
            u32 elapsedMs = static_cast<u32>(s_frozenTimerCentiseconds) * 10u;
            if (buf->gameModeState.roundStartMs > elapsedMs)
                elapsedMs = buf->gameModeState.roundStartMs;
            resumeHiderTimerFromElapsedMs(director, console, elapsedMs);
            // resume* only paints frozen; count-up resumes via updateTimerDisplay.
        }
    } else {
        showTimerPanelAtZero(director, console);
    }

    const bool live = isRetailTimerPanelLive(console);
    if (s_deathReloadTimerRemountFrames == 1u ||
        (s_deathReloadTimerRemountFrames % 30u) == 0u || live) {
        OSReport("[SMSO] HideSeek death-reload timer remount try seeker=%u frozenCs=%d "
                 "live=%u visible=%u tag=%u sticky=%u frame=%u\n",
                 localIsSeeker ? 1u : 0u, s_frozenTimerCentiseconds, live ? 1u : 0u,
                 s_timerPanelVisible ? 1u : 0u, tagActive ? 1u : 0u, s_tagActive ? 1u : 0u,
                 s_deathReloadTimerRemountFrames);
    }

    if (live) {
        s_deathReloadTimerRemountPending = false;
        OSReport("[SMSO] HideSeek death-reload timer remount ok seeker=%u frozenCs=%d "
                 "frame=%u\n",
                 localIsSeeker ? 1u : 0u, s_frozenTimerCentiseconds,
                 s_deathReloadTimerRemountFrames);
        return;
    }

    if (s_deathReloadTimerRemountFrames >= kDeathReloadTimerRemountBudgetFrames) {
        s_deathReloadTimerRemountPending = false;
        OSReport("[SMSO] HideSeek death-reload timer remount fail live=0 seeker=%u "
                 "frozenCs=%d frame=%u\n",
                 localIsSeeker ? 1u : 0u, s_frozenTimerCentiseconds,
                 s_deathReloadTimerRemountFrames);
    }
}

static void resetHiderTimerAtZero(TMarDirector *director, TGCConsole2 *console) {
    s_frozenTimerCentiseconds = 0;
    s_hiderTimerRunning = false;
    s_localHasRunHiderTimer = false;
    stopRetailTimerMovement(console);
    resetDirectorRaceStopwatch(director);
    if (console && s_timerPanelVisible)
        setTimerCentiseconds(console, 0);
}

static void clampGameOverToDeathStage() {
    if (!s_taggedDeathActive || !s_deathStageCaptured)
        return;

    gpApplication.mNextScene.mAreaID = s_deathAreaId;
    gpApplication.mNextScene.mEpisodeID = s_deathEpisodeId;
}

static bool isOnCapturedDeathStage(const TMarDirector *director) {
    if (!director || !s_deathStageCaptured)
        return false;
    if (director->mAreaID != s_deathAreaId)
        return false;
    // Director episode may be a mission override (hotel); compare mounted load scenario.
    return gpApplication.mCurrentScene.mEpisodeID == s_deathEpisodeId;
}

static void captureHideSeekDeathStage(TMarDirector *director) {
    if (s_tagPlayStageValid) {
        s_deathAreaId = s_tagPlayStageArea;
        s_deathEpisodeId = s_tagPlayStageEpisode;
    } else if (director) {
        s_deathAreaId = director->mAreaID;
        // Prefer mounted load scenario so same-stage reload remounts the correct archive
        // (hotel/casino mission ids on director are not load indices).
        s_deathEpisodeId = gpApplication.mCurrentScene.mEpisodeID;
    } else {
        return;
    }

    // Preserve hotel mission (director / flag / pin) when it differs from the delfino load row.
    s_deathMissionEpisode = 0xFF;
    if (s_deathAreaId == 7) {
        const u8 dir = director ? director->mEpisodeID : 0xFF;
        const u8 flag = TFlagManager::smInstance
                            ? static_cast<u8>(TFlagManager::smInstance->getFlag(0x40003))
                            : 0xFF;
        const u8 pinned = s_tagPlayStageValid ? s_tagPlayStageMission : 0xFF;
        s_deathMissionEpisode =
            resolveHotelMissionOverride(s_deathEpisodeId, dir, flag, pinned);
    }

    s_deathStageCaptured = true;
}

static void reloadCapturedDeathStage(TMarDirector *director) {
    if (!director || !s_deathStageCaptured)
        return;
    const u8 mission = (s_deathMissionEpisode != 0xFF) ? s_deathMissionEpisode : s_deathEpisodeId;
    reloadLocalStage(director, s_deathAreaId, s_deathEpisodeId, mission);
}

static void ensureHideSeekDeathStage(TMarDirector *director) {
    if (!director || !s_deathStageCaptured)
        return;

    if (isOnCapturedDeathStage(director)) {
        clampGameOverToDeathStage();
        return;
    }

    if (!s_deathReloadQueued) {
        s_deathReloadQueued = true;
        reloadCapturedDeathStage(director);
    }
}

static bool isLocalMarioInDeathState(const TMario *mario) {
    if (!mario)
        return false;

    return mario->mState == TMario::STATE_DEATH || mario->mAttributes.mIsGameOver ||
           mario->mHealth <= 0;
}

static void beginHideSeekDeathRecovery(TMarDirector *director, TMario *mario, bool forceDeathAnim) {
    if (!mario || s_taggedDeathActive || s_deathFinishBusy)
        return;

    // Freeze hider elapsed before death fade / stage exit tears down GCConsole2.
    captureHiderTimerSnapshot(director, getConsole(director));

    captureHideSeekDeathStage(director);
    s_envDeathRecovery = !forceDeathAnim;
    if (!forceDeathAnim)
        s_envDeathPromotionPending = true;

    // Queue the lethal drop for the local Mario playerUpdate callback (runs inside
    // perform, before playerControl). Applying floorDamageExec/changePlayerDropping
    // from stageUpdate (pre-direct) races draw/half-frames at 60fps.
    if (forceDeathAnim)
        s_pendingForceDeathAnim = true;

    s_taggedDeathActive = true;
    armTaggedDeathTimers();
    clampGameOverToDeathStage();
}

static void noteTagRoundDeathGrace(TMario *mario) {
    if (!mario || !isLocalMarioInDeathState(mario))
        return;

    if (!isHideSeekTagRoundActive(getCommBuffer()) && !s_tagActive)
        return;

    s_tagDeathGraceFrames = kTagDeathGraceFrames;
}

static void tryBeginHideSeekDeathRecovery(TMarDirector *director, TMario *mario) {
    if (!director || !mario || s_taggedDeathActive)
        return;

    if (!isHideSeekTagRoundActive(getCommBuffer()) && !s_tagActive)
        return;

    if (!isLocalMarioInDeathState(mario))
        return;

    beginHideSeekDeathRecovery(director, mario, false);
    noteTagRoundDeathGrace(mario);
}

static bool isLeavingTagPlayStage() {
    if (!s_tagPlayStageValid)
        return false;

    return gpApplication.mNextScene.mAreaID != s_tagPlayStageArea ||
           gpApplication.mNextScene.mEpisodeID != s_tagPlayStageEpisode;
}

static void forceTagRoundDeathStageRecovery(TMarDirector *director) {
    if (!director || s_taggedDeathActive)
        return;

    if (!isHideSeekTagRoundActive(getCommBuffer()) && !s_tagActive)
        return;

    captureHiderTimerSnapshot(director, getConsole(director));
    captureHideSeekDeathStage(director);
    if (!s_deathStageCaptured)
        return;

    s_envDeathRecovery = true;
    s_envDeathPromotionPending = true;
    s_taggedDeathActive = true;
    armTaggedDeathTimers();
    clampGameOverToDeathStage();

    if (gpMarioAddress)
        gpMarioAddress->mAttributes.mIsGameOver = false;

    OSReport("[SMSO] HideSeek death stage locked area=%u ep=%u frozenCs=%d\n", s_deathAreaId,
             s_deathEpisodeId, s_frozenTimerCentiseconds);
}

static void beginTaggedDeath(TMarDirector *director, TMario *mario) {
    beginHideSeekDeathRecovery(director, mario, true);
}

static bool isLocalSlotSeeker(const CommBuffer *buf) {
    if (!buf)
        return false;

    const GameModeState &gm = buf->gameModeState;
    if (gm.localRole == HSR_SEEKER)
        return true;

    return buf->localSlot < MAX_PLAYERS && gm.roleBySlot[buf->localSlot] == HSR_SEEKER;
}

static void bindModelToHeadBone(J3DModel *model, Mtx *jointMtx) {
    if (!model || !jointMtx)
        return;

    MTXCopy(*jointMtx, model->mBaseMtx);
    model->calc();
}

static void bindLocalSeekerCapAttachments(TMario *mario, JDrama::TGraphics *graphics) {
    J3DModel *body = mario->mModelData ? mario->mModelData->mModel : nullptr;
    if (!body || !body->mJointArray || !mario->mCap)
        return;

    const u8 mhead = mario->mBindBoneIDArray[11];
    Mtx *mheadMtx = &body->mJointArray[mhead];

    CommBuffer *buf = getCommBuffer();
    const bool hideHats = buf && buf->magic == COMM_MAGIC &&
                          smso::marioModelIdWantsHiddenCaps(buf->localMarioModelId);

    if (hideHats) {
        smso::squashHiddenCapDrawInstance(mario);
    } else {
        if (mario->mCap->mCap1)
            bindModelToHeadBone(mario->mCap->mCap1, mheadMtx);
        if (mario->mCap->mCap3)
            bindModelToHeadBone(mario->mCap->mCap3, mheadMtx);
    }
    if (mario->mCap->maGlass1)
        bindModelToHeadBone(mario->mCap->maGlass1, mheadMtx);

    mario->mCap->perform(2, graphics);
    if (hideHats)
        smso::squashHiddenCapDrawInstance(mario);
}

static void queueLocalSeekerPromotionVfx() {
    s_pendingSeekerPromotionVfxFrames = kSeekerPromotionVfxDelayFrames;
}

static void tickLocalSeekerPromotionVfx(TMarDirector *director, TMario *mario) {
    if (s_pendingSeekerPromotionVfxFrames == 0 || !director || !mario)
        return;

    if (director->mCurState < TMarDirector::STATE_NORMAL)
        return;

    if (s_taggedDeathActive || isLocalMarioInDeathState(mario))
        return;

    --s_pendingSeekerPromotionVfxFrames;
    if (s_pendingSeekerPromotionVfxFrames != 0)
        return;

    // doldecomp MarioDraw.cpp wearGlass: setPositions() first so emitGetEffect binds
    // PARTICLE_MS_ITEMGET1_A to unk160, then activates E_CAP_MODEL_SUNGLASSES.
    mario->setPositions();
    mario->wearGlass();
}

static void applyLocalSeekerPromotionLook(TMario *mario) {
    if (!mario)
        return;

    // setMario() during respawn resets draw state; force a fresh cosmetic pass.
    s_localSeekerLook = false;
    applyHideSeekPlayerCosmetics(mario, true, false);
    s_localSeekerLook = true;
    queueLocalSeekerPromotionVfx();
    s_seekerLookRetryFrames = 30;
}

static void handleLocalSeekerPromotion(TMarDirector *director, TGCConsole2 *console, TMario *mario,
                                       const GameModeState &gm) {
    if (gm.tagEventId != 0)
        s_handledTagEventId = gm.tagEventId;

    // Only freeze when this client actually hid. Lifetime seekers keep 0:00.
    if (s_hiderTimerRunning || s_localHasRunHiderTimer || s_localWasHider)
        captureHiderTimerSnapshot(director, console);
    ensureSeekerTimerPanelVisible(console);
    OSReport("[SMSO] HideSeek seeker promotion timer frozenCs=%d hasRun=%u wasHider=%u\n",
             s_frozenTimerCentiseconds, s_localHasRunHiderTimer ? 1u : 0u,
             s_localWasHider ? 1u : 0u);
    s_localWasSeeker = true;
    s_localWasHider = false;
    s_envDeathRecovery = false;
    s_envDeathPromotionPending = false;

    if (mario && director && director->mCurState >= TMarDirector::STATE_NORMAL &&
        !isLocalMarioInDeathState(mario) && !s_taggedDeathActive)
        applyLocalSeekerPromotionLook(mario);
}

static void clearDeathFadeIfStuck() {
    TSMSFader *fader = gpApplication.mFader;
    if (!fader)
        return;
    // Soft setMario after a death wipe leaves FADE_ON with no load cycle to fade back in.
    if (fader->mFadeStatus == TSMSFader::FADE_ON)
        fader->startFadeinT(0.35f);
}

/// Complete death recovery after retail same-stage moveStage has reached STATE_NORMAL.
/// Skips cinematic only if still active; one-shot control restore — no sticky pad wars.
static void completeHideSeekDeathReload(TMarDirector *director, TMario *mario) {
    if (!director || !mario || !s_taggedDeathActive || s_deathFinishBusy)
        return;

    CommBuffer *buf = getCommBuffer();
    TGCConsole2 *console = getConsole(director);

    s_deathFinishBusy = true;
    s_pendingForceDeathAnim = false;
    s_pendingDeathStageReload = false;
    s_deathReloadQueued = false;
    s_resumeDeathRecoveryAfterReload = false;
    clearDeathReloadSettle();
    s_hideSeekIntroSkipFrames = 0;

    // Retail load already placed Mario at the episode spawn. Do NOT call setMario /
    // finishMarioEntryWithoutSetMario here — that softlocks (Mare/Noki / builds 67–68).
    if (isStageEntryDemoActive(director, mario))
        forceSkipStageEntryDemo(director, mario);
    else
        oneShotRestoreControlAfterDeath(director, mario);

    while (mario->mHealth < 8)
        mario->incHP(1);

    mario->mAttributes.mIsGameOver = false;
    clampGameOverToDeathStage();
    clearDeathFadeIfStuck();

    const bool localIsSeeker = isLocalSlotSeeker(buf);
    s_localWasSeeker = localIsSeeker;
    s_localWasHider = !localIsSeeker;
    s_envDeathRecovery = false;

    s_taggedDeathActive = false;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = 0;
    s_deathStageCaptured = false;

    // Seekers still in Start Tag grace re-lock via updateHideSeekGrace next frame.
    s_seekerGraceInputLocked = false;
    s_seekerGracePosPinned = false;
    s_seekerGraceGapHoldFramesLeft = 0;

    if (localIsSeeker)
        handleLocalSeekerPromotion(director, console, mario, buf->gameModeState);

    // Always arm post-death TIME remount while in Hide & Seek. Build 70 gated a
    // one-shot remount on isHideSeekTagRoundActive — that flag was often false at
    // control-ready (log: control ready without "timer remount"), while seeker
    // promotion had already set a stale s_timerPanelVisible and blocked ensure*.
    if (buf && buf->gameModeState.mode == GM_HIDE_SEEK) {
        armDeathReloadTimerRemount("control-ready");
        const bool tagActive =
            isHideSeekTagRoundActive(buf) || s_tagActive;
        tickDeathReloadTimerRemount(director, console, buf, localIsSeeker, tagActive);
    }

    logDeathControlDiag("death-reload control ready", director, mario);
    OSReport("[SMSO] HideSeek death-reload control ready area=%u ep=%u seeker=%u\n",
             director->mAreaID, director->mEpisodeID, localIsSeeker ? 1u : 0u);

    s_deathFinishBusy = false;
}

static void finishHideSeekDeathRecovery(TMarDirector *director, TMario *mario) {
    if (!director || !mario || !s_taggedDeathActive || s_deathFinishBusy)
        return;

    ensureHideSeekDeathStage(director);
    if (!s_deathStageCaptured)
        return;

    // Soft setMario mid-stage races the death fade (Bianco void/water). Always force
    // one authorized same-course/same-episode moveStage first; stageInit resumes settle.
    if (!s_resumeDeathRecoveryAfterReload) {
        if (isOnCapturedDeathStage(director) && !s_deathReloadQueued) {
            s_deathReloadQueued = true;
            OSReport("[SMSO] HideSeek death-reload begin moveStage area=%u load=%u mission=%u\n",
                     s_deathAreaId, s_deathEpisodeId,
                     (s_deathMissionEpisode != 0xFF) ? s_deathMissionEpisode : s_deathEpisodeId);
            reloadCapturedDeathStage(director);
        }
        return;
    }

    if (!isOnCapturedDeathStage(director))
        return;

    if (!s_deathReloadSettleActive)
        armDeathReloadSettle(director);

    ++s_deathReloadSettleFrames;

    // Skip intro from settle frame 1 on every stage — do not wait for frame ≥2 or residual
    // demo budget (Ricco ~13s begin→ready). Complete as soon as STATE_NORMAL.
    forceSkipStageEntryDemo(director, mario);
    if (s_deathReloadSettleFrames == 1 || (s_deathReloadSettleFrames % 30) == 0) {
        OSReport("[SMSO] HideSeek death-reload skip cinematic area=%u ep=%u "
                 "settle=%u state=%u\n",
                 director->mAreaID, director->mEpisodeID, s_deathReloadSettleFrames,
                 director->mCurState);
    }

    if (director->mCurState < TMarDirector::STATE_NORMAL) {
        if (s_deathReloadSettleFrames < kDeathReloadSettleBudgetFrames)
            return;
    }

    completeHideSeekDeathReload(director, mario);
}

static void finishTaggedDeath(TMarDirector *director, TMario *mario) {
    finishHideSeekDeathRecovery(director, mario);
}

static void installHideSeekDeathHooks() {
    if (s_hideSeekHooksInstalled)
        return;

    s_hideSeekHooksInstalled = true;
    BetterSMS::Player::addUpdateCallback(
        [](TMario *player, bool isLocal) {
            if (!isLocal || !gpMarDirector || !player)
                return;

            // Apply deferred tag-kill inside Mario perform, before playerControl.
            // Gated on s_taggedDeathActive so a Stop Tag / clear cannot fire a stale kill.
            if (s_pendingForceDeathAnim) {
                s_pendingForceDeathAnim = false;
                if (s_taggedDeathActive)
                    applyForcedTaggedDeathAnim(player);
            }

            // Re-assert seeker grace freeze after BSE collisionContext (registered
            // earlier) and before playerControl — stageUpdate alone is too early.
            if (s_seekerGraceInputLocked)
                applySeekerGraceFreeze(player);

            CommBuffer *buf = getCommBuffer();
            // Keep mid-death remount alive after RoundComplete/Stop Tag — tag-off used
            // to early-return here and leave Mario softlocked in the death cutscene.
            if (!isHideSeekTagRoundActive(buf)) {
                if (s_taggedDeathActive) {
                    player->mAttributes.mIsGameOver = false;
                    clampGameOverToDeathStage();
                    if (s_deathStageCaptured && !isOnCapturedDeathStage(gpMarDirector))
                        s_pendingDeathStageReload = true;
                }
                return;
            }

            if (s_taggedDeathActive) {
                player->mAttributes.mIsGameOver = false;
                clampGameOverToDeathStage();
                noteTagRoundDeathGrace(player);
                // Never setNextStage mid-perform — queue for stageUpdate.
                if (s_deathStageCaptured && !isOnCapturedDeathStage(gpMarDirector))
                    s_pendingDeathStageReload = true;
                return;
            }

            tryBeginHideSeekDeathRecovery(gpMarDirector, player);
        });
    BetterSMS::Stage::setNextStageHandler([](TMarDirector *director) {
        if (!director)
            return;

        CommBuffer *buf = getCommBuffer();
        const bool launcherWarp = buf && (buf->bridgeFlags & BF_WARP_PENDING) != 0;

        if (s_allowDeathStageTransition) {
            s_allowDeathStageTransition = false;
            // Vanilla can overwrite next to plaza/0xFF after we queued a same-stage
            // reload — always re-assert the death/tag stage before moveStage.
            redirectHideSeekDeathStageLeave(director);
            smso::performSmsoMoveStage(director);
            smso::clearAuthorizedStageMovePending();
            return;
        }

        if (s_allowLauncherStageTransition || launcherWarp) {
            // Keep authorize flags through performSmsoMoveStage AND any immediate
            // second vanilla setNextStage during the same transition. Clearing here
            // let sticky death rewrite next=plaza → death stage after RoundComplete
            // (dolphin.log: tag=0 death=1). stageInit clears the latches.
            smso::performSmsoMoveStage(director);
            return;
        }

        // Cover every non-launcher leave (plaza hub return is the common death path).
        redirectHideSeekDeathStageLeave(director);

        if (s_taggedDeathActive && s_deathStageCaptured) {
            if (gpMarioAddress)
                gpMarioAddress->mAttributes.mIsGameOver = false;

            writeDeathOrTagNextScene(s_deathAreaId, s_deathEpisodeId);
            s_allowDeathStageTransition = false;
            smso::performSmsoMoveStage(director);
            smso::clearAuthorizedStageMovePending();
            return;
        }

        smso::performSmsoMoveStage(director);
        smso::clearAuthorizedStageMovePending();
    });
}

static void resetHideSeekRoundUiState() {
    // Do not clear frozen/hasRun here — mid-round stageInit during an active tag used to
    // wipe the hider run. Death remount relies on those statics surviving GCConsole2 rebuild.
    s_timerPanelVisible = false;
    s_retailTimerMoving = false;
    s_hiderTimerRunning = false;
}

static void resetHideSeekDeathState() {
    s_taggedDeathActive = false;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = 0;
    s_minTaggedDeathFrames = kMinTaggedDeathFrames30;
    s_deathAreaId = 0;
    s_deathEpisodeId = 0;
    s_deathMissionEpisode = 0xFF;
    s_deathStageCaptured = false;
    s_pendingForceDeathAnim = false;
    s_pendingDeathStageReload = false;
    s_deathFinishBusy = false;
    s_resumeDeathRecoveryAfterReload = false;
    s_deathReloadQueued = false;
    clearDeathReloadSettle();
    s_deathReloadTimerRemountPending = false;
    s_deathReloadTimerRemountFrames = 0;
    s_hideSeekIntroSkipFrames = 0;
}

static void resetHideSeekTransientState() {
    resetHideSeekRoundUiState();
    s_frozenTimerCentiseconds = 0;
    s_localHasRunHiderTimer = false;
    resetHideSeekDeathState();
}

static void recoverTaggedSeekerAfterStageReload(TMarDirector *director) {
    if (!director || !gpMarioAddress)
        return;

    CommBuffer *buf = getCommBuffer();
    const GameModeState &gm = buf->gameModeState;
    if (gm.mode != GM_HIDE_SEEK || gm.tagEventId == 0)
        return;
    if (gm.lastTaggedSlot != buf->localSlot || gm.localRole != HSR_SEEKER)
        return;

    s_handledTagEventId = gm.tagEventId;
    s_localWasSeeker = true;
    s_localWasHider = false;

    TMario *mario = gpMarioAddress;
    if (mario->mState == TMario::STATE_DEATH || mario->mHealth <= 0) {
        finishTaggedDeath(director, mario);
        return;
    }

    applyLocalSeekerPromotionLook(mario);
}

static void syncLocalSeekerLookAfterTag(TMario *mario) {
    if (!mario || s_localSeekerLook)
        return;

    applyLocalSeekerPromotionLook(mario);
}

static bool isNewLocalTagEvent(const GameModeState &gm, u8 localSlot, bool localIsSeeker) {
    if (gm.tagEventId == 0 || gm.tagEventId == s_handledTagEventId)
        return false;

    if (gm.lastTaggedSlot != localSlot)
        return false;

    // Already converted to seeker before this tag event was handled.
    if (localIsSeeker && !s_localWasHider)
        return false;

    return true;
}

static void syncPausedTimerFromNetwork(const GameModeState &gm, TGCConsole2 *console) {
    if (gm.mode != GM_HIDE_SEEK || (gm.flags & GMF_TAG_ACTIVE) != 0 || gm.roundStartMs == 0)
        return;
    if ((gm.flags & GMF_TIMER_RESET) != 0)
        return;

    const s32 pausedCentiseconds = static_cast<s32>(gm.roundStartMs / 10u);
    if (pausedCentiseconds <= 0)
        return;

    adoptFrozenTimerCentiseconds(pausedCentiseconds, "networkPaused");
    if (console && s_timerPanelVisible)
        setTimerCentiseconds(console, s_frozenTimerCentiseconds);
}

static void updateTimerDisplay(TMarDirector *director, TGCConsole2 *console, bool timerShouldRun,
                               bool localIsSeeker) {
    if (!console || !s_timerPanelVisible)
        return;

    if (localIsSeeker) {
        stopRetailTimerMovement(console);
        setTimerCentiseconds(console, seekerDisplayCentiseconds());
        return;
    }

    if (timerShouldRun) {
        if (!s_hiderTimerRunning)
            beginHiderCountUp(director, console);
        else
            syncTimerFromDirectorStopwatch(console);
        return;
    }

    if (s_hiderTimerRunning)
        freezeHiderTimer(director, console);
    else
        setTimerCentiseconds(console, s_frozenTimerCentiseconds);
}

static void setAllowDeathStageTransition(bool allow) {
    s_allowDeathStageTransition = allow;
}

static void setAllowLauncherStageTransition(bool allow) {
    s_allowLauncherStageTransition = allow;
}

static bool hideSeekStageTransitionAuthorized() {
    return s_allowLauncherStageTransition || s_allowDeathStageTransition || s_taggedDeathActive;
}

} // namespace

void playHideSeekSeekerCosmeticVfx(TMario *mario) {
    if (!mario)
        return;

    // doldecomp MarioDraw.cpp emitGetEffect — ITEMGET swirl at unk160 (chest bind point).
    mario->setPositions();
    mario->emitGetEffect();
}

void maintainLocalHideSeekSeekerDraw(TMario *mario, JDrama::TGraphics *graphics) {
    if (!mario || !graphics || mario != gpMarioAddress)
        return;

    const CommBuffer *buf = getCommBuffer();
    if (!isHideSeekActive() || !isLocalSlotSeeker(buf))
        return;

    // Cap/glass bind + mCap->perform during STATE_DEATH races joint teardown at 60fps.
    if (s_taggedDeathActive || isLocalMarioInDeathState(mario) || s_deathFinishBusy)
        return;

    // Remote puppets reapply in remoteCalcAnim every frame; retail thinkAloha/calcAnim
    // overwrites local shirt + cap state if we only set it once from updateHideSeek.
    applyHideSeekPlayerCosmetics(mario, true, false);
    s_localSeekerLook = true;
    bindLocalSeekerCapAttachments(mario, graphics);
}

void applyHideSeekPlayerCosmetics(TMario *mario, bool isSeeker, bool isRemote) {
    (void)isRemote;
    if (!mario)
        return;

    if (isSeeker) {
        mario->mAttributes.mIsShineShirt = true;
        mario->mPrevAttributes.mIsShineShirt = true;
        setShineShirtShapeVisible(mario, true);
        setSeekerGlassesFlag(mario, true);
    } else {
        mario->mAttributes.mIsShineShirt = false;
        mario->mPrevAttributes.mIsShineShirt = false;
        setShineShirtShapeVisible(mario, false);
        setSeekerGlassesFlag(mario, false);
    }
}

void bootHideSeek() {
    installHideSeekDeathHooks();
}

void initHideSeek() {
    installHideSeekDeathHooks();

    if (s_authorizedStageExitPending) {
        s_authorizedStageExitPending = false;
        if (isHideSeekTagRoundActive(getCommBuffer()) && gpMarDirector) {
            pinTagPlayStage(gpMarDirector);
            // Mid-round launcher warps must not freeze on gate/title intros.
            armHideSeekIntroSkip("mid-round-warp", gpMarDirector);
        }
    }

    if (s_taggedDeathActive && s_deathStageCaptured) {
        s_resumeDeathRecoveryAfterReload = true;
        s_deathReloadQueued = false;
        armDeathReloadSettle(gpMarDirector);
        // Defensive: sticky leave paths used to set 0x40003=load without hotel arm —
        // re-arm so applyHotelWarpMissionOverride (after this) restores director mission.
        if (s_deathAreaId == 7 && s_deathMissionEpisode != 0xFF &&
            s_deathMissionEpisode != s_deathEpisodeId)
            armHotelMissionEpisodeSync(s_deathMissionEpisode);
        OSReport("[SMSO] HideSeek death-reload stageInit area=%u load=%u mission=%u\n",
                 gpMarDirector ? gpMarDirector->mAreaID : 0, s_deathEpisodeId,
                 (s_deathMissionEpisode != 0xFF) ? s_deathMissionEpisode : s_deathEpisodeId);
        // New GCConsole2 — drop stale panel binding and arm remount for control-ready.
        armDeathReloadTimerRemount("stageInit");
        if (gpMarDirector && !isOnCapturedDeathStage(gpMarDirector)) {
            OSReport("[SMSO] HideSeek reloading death stage area=%u load=%u mission=%u "
                     "(was area=%u ep=%u)\n",
                     s_deathAreaId, s_deathEpisodeId,
                     (s_deathMissionEpisode != 0xFF) ? s_deathMissionEpisode : s_deathEpisodeId,
                     gpMarDirector->mAreaID, gpMarDirector->mEpisodeID);
            s_deathReloadQueued = true;
            reloadCapturedDeathStage(gpMarDirector);
        }
        // Still drop launcher-warp latch after the death stage actually loaded.
        s_allowLauncherStageTransition = false;
        smso::clearAuthorizedStageMovePending();
        return;
    }

    // Launcher/host warp arrived — drop authorize latch after the stage actually loaded.
    // Clearing it in setNextStageHandler let a second vanilla moveStage redirect
    // post-round warps (tag=0 death=1) while remotes left.
    s_allowLauncherStageTransition = false;
    smso::clearAuthorizedStageMovePending();

    if (isHideSeekTagRoundActive(getCommBuffer()))
        resetHideSeekRoundUiState();
    else
        resetHideSeekTransientState();
}

void onHideSeekStageExit() {
    const CommBuffer *buf = getCommBuffer();
    const bool tagActive = isHideSeekTagRoundActive(buf) || s_tagActive;

    if (s_authorizedStageExitPending) {
        if (s_taggedDeathActive && s_deathStageCaptured) {
            captureTimerThenInvalidatePanel(gpMarDirector);
        } else if (!tagActive) {
            resetHideSeekTransientState();
        }
        return;
    }

    if (tagActive && gpMarDirector) {
        if (gpMarioAddress)
            tryBeginHideSeekDeathRecovery(gpMarDirector, gpMarioAddress);

        if (!s_taggedDeathActive &&
            (s_tagDeathGraceFrames > 0 || isLeavingTagPlayStage()))
            forceTagRoundDeathStageRecovery(gpMarDirector);

        if (s_taggedDeathActive && s_deathStageCaptured) {
            captureTimerThenInvalidatePanel(gpMarDirector);
            return;
        }
    }

    if (!s_taggedDeathActive)
        resetHideSeekTransientState();
}

void guardHideSeekDeathBeforeWarp(TMarDirector *director) {
    if (!director || !gpMarioAddress)
        return;

    // RoundComplete / Stop Tag land in the mailbox before updateHideSeek runs.
    // Drop pin/grace so post-round warp-all is never rewritten to the death stage.
    // Keep an in-flight same-stage remount — aborting mid-death softlocks in the
    // death cutscene (dolphin.log: scrub sticky death tagOff death=1). Intentional
    // launcher warps still call clearHideSeekDeathForLauncherWarp from consumeWarp.
    const CommBuffer *buf = getCommBuffer();
    if (!isHideSeekTagRoundActive(buf)) {
        const bool remountInFlight =
            s_taggedDeathActive || s_deathReloadQueued ||
            s_resumeDeathRecoveryAfterReload || s_deathReloadSettleActive;
        if (s_tagPlayStageValid || s_tagDeathGraceFrames > 0 || remountInFlight ||
            s_deathStageCaptured) {
            OSReport("[SMSO] HideSeek clear pin before warp "
                     "(tagOff remount=%u death=%u pin=%u)\n",
                     remountInFlight ? 1u : 0u,
                     (s_taggedDeathActive || s_deathStageCaptured) ? 1u : 0u,
                     s_tagPlayStageValid ? 1u : 0u);
        }
        s_tagPlayStageValid = false;
        s_tagPlayStageMission = 0xFF;
        s_tagDeathGraceFrames = 0;
        if (!remountInFlight)
            resetHideSeekDeathState();
        s_tagActive = false;
        return;
    }

    noteTagRoundDeathGrace(gpMarioAddress);
    tryBeginHideSeekDeathRecovery(director, gpMarioAddress);
}

void clearHideSeek() {
    s_hideSeekModeActive = false;
    s_tagActive = false;
    s_timerPanelVisible = false;
    s_retailTimerMoving = false;
    s_localWasHider = false;
    s_localWasSeeker = false;
    s_localSeekerLook = false;
    s_roundFanfareWas = false;
    s_roundCompleteWas = false;
    s_timerResetWas = false;
    s_roundEndFanfarePlayed = false;
    s_taggedDeathActive = false;
    s_hiderTimerRunning = false;
    s_localHasRunHiderTimer = false;
    s_handledTagEventId = 0;
    s_lastTagSoundEventId = 0;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = 0;
    s_minTaggedDeathFrames = kMinTaggedDeathFrames30;
    s_frozenTimerCentiseconds = 0;
    s_lastNetworkRoundStartMs = 0;
    s_deathAreaId = 0;
    s_deathEpisodeId = 0;
    s_deathMissionEpisode = 0xFF;
    s_deathStageCaptured = false;
    s_envDeathRecovery = false;
    s_envDeathPromotionPending = false;
    s_seekerLookRetryFrames = 0;
    s_pendingSeekerPromotionVfxFrames = 0;
    s_pendingForceDeathAnim = false;
    s_pendingDeathStageReload = false;
    s_deathFinishBusy = false;
    s_resumeDeathRecoveryAfterReload = false;
    s_deathReloadQueued = false;
    clearDeathReloadSettle();
    s_deathReloadTimerRemountPending = false;
    s_deathReloadTimerRemountFrames = 0;
    s_tagPlayStageValid = false;
    s_tagPlayStageMission = 0xFF;
    s_authorizedStageExitPending = false;
    s_tagDeathGraceFrames = 0;
    releaseSeekerGraceInputLock(gpMarioAddress);
    s_graceWasActive = false;
    s_tagWasActiveForGo = false;
    s_graceEndFlashFrames = 0;
    s_hideSeekIntroSkipFrames = 0;
}

void setHideSeekAllowStageTransition(bool allow) {
    if (allow)
        s_authorizedStageExitPending = true;
    setAllowLauncherStageTransition(allow);
}

void setHideSeekAllowDeathStageReload(bool allow) {
    setAllowDeathStageTransition(allow);
}

void clearHideSeekDeathForLauncherWarp() {
    resetHideSeekDeathState();
    s_tagDeathGraceFrames = 0;
}

bool isHideSeekAuthorizedStageTransition() {
    return hideSeekStageTransitionAuthorized();
}

bool isHideSeekActive() {
    return s_hideSeekModeActive;
}

bool isHideSeekTaggedDeathActive() {
    return s_taggedDeathActive;
}

bool redirectHideSeekDeathStageLeave(TMarDirector *director) {
    if (!director)
        return false;

    // Host/launcher warps are intentional (including warping TO plaza).
    // BF_WARP_PENDING is cleared in consumeWarpIntent before setNextStage runs;
    // s_allowLauncherStageTransition / authorizeLauncherStageMove cover that window.
    const CommBuffer *buf = getCommBuffer();
    if (s_allowLauncherStageTransition || isLauncherAuthorizedStageMove())
        return false;

    const bool tagRound = isHideSeekTagRoundActive(buf) || s_tagActive;
    // After Stop Tag / RoundComplete, never hijack warps — sticky death-recovery
    // flags used to rewrite next=1/2 → death stage (dolphin.log: tag=0 death=1)
    // while remotes left for plaza, leaving local remotes invisible forever.
    if (!tagRound)
        return false;
    if (buf && (buf->gameModeState.flags & GMF_ROUND_COMPLETE) != 0)
        return false;

    const bool deathRecovery = s_taggedDeathActive || s_deathStageCaptured ||
                               s_tagDeathGraceFrames > 0 || s_deathReloadQueued ||
                               s_resumeDeathRecoveryAfterReload || s_deathReloadSettleActive;
    // Do NOT gate on a leftover pin alone — after Stop Tag, players must still be
    // able to return to plaza. Only redirect while tag/death recovery is live.
    if (!deathRecovery && !s_tagPlayStageValid)
        return false;

    constexpr u8 kDelfinoPlazaAreaId = 1;
    const u8 nextArea = gpApplication.mNextScene.mAreaID;
    const u8 nextEp = gpApplication.mNextScene.mEpisodeID;
    const bool plazaHubReturn =
        nextArea == kDelfinoPlazaAreaId && nextEp == 0xFF;
    const bool leavingPin = s_tagPlayStageValid &&
                            (nextArea != s_tagPlayStageArea || nextEp != s_tagPlayStageEpisode);
    const bool leavingDeath = s_deathStageCaptured &&
                              (nextArea != s_deathAreaId || nextEp != s_deathEpisodeId);

    // Only rewrite when we'd leave the tag play / death stage (plaza hub is the
    // common vanilla death path). Same-stage reloads keep next==pin and pass through.
    if (!plazaHubReturn && !leavingPin && !leavingDeath)
        return false;

    if (tagRound && !s_taggedDeathActive)
        forceTagRoundDeathStageRecovery(director);
    else if (!s_deathStageCaptured && director)
        captureHideSeekDeathStage(director);

    u8 destArea = 0;
    u8 destEp = 0;
    bool haveDest = false;
    if (s_deathStageCaptured) {
        destArea = s_deathAreaId;
        destEp = s_deathEpisodeId;
        haveDest = true;
    } else if (s_tagPlayStageValid) {
        destArea = s_tagPlayStageArea;
        destEp = s_tagPlayStageEpisode;
        haveDest = true;
    } else if (gpApplication.mCurrentScene.mAreaID != 0 &&
               gpApplication.mCurrentScene.mAreaID != kDelfinoPlazaAreaId) {
        destArea = gpApplication.mCurrentScene.mAreaID;
        destEp = gpApplication.mCurrentScene.mEpisodeID;
        haveDest = true;
    } else if (director->mAreaID != 0 && director->mAreaID != kDelfinoPlazaAreaId) {
        destArea = director->mAreaID;
        destEp = gpApplication.mCurrentScene.mEpisodeID;
        haveDest = true;
    }

    if (!haveDest)
        return false;

    if (nextArea == destArea && nextEp == destEp)
        return false;

    OSReport("[SMSO] HideSeek redirect leave next=%u/%u → area=%u ep=%u "
             "(tag=%u death=%u)\n",
             nextArea, nextEp, destArea, destEp, tagRound ? 1u : 0u,
             deathRecovery ? 1u : 0u);

    writeDeathOrTagNextScene(destArea, destEp);
    if (gpMarioAddress)
        gpMarioAddress->mAttributes.mIsGameOver = false;
    // Do not arm intro-skip here — that would fight retail setMario.
    // stageInit arms death-reload settle after the real stage load.
    return true;
}

bool shouldForceHideSeekDeadSnapshot() {
    const CommBuffer *buf = getCommBuffer();
    if (!buf || !isHideSeekTagRoundActive(buf))
        return false;

    if (s_taggedDeathActive)
        return true;

    return s_envDeathPromotionPending && !isLocalSlotSeeker(buf);
}

bool isHideSeekNameTagMode() {
    return s_hideSeekModeActive;
}

bool isLocalHideSeekSeeker() {
    return isLocalSlotSeeker(getCommBuffer());
}

bool shouldDrawHideSeekNameTag(u8 remoteSlot) {
    if (!s_hideSeekModeActive)
        return true;

    const CommBuffer *buf = getCommBuffer();
    if (buf->gameModeState.mode != GM_HIDE_SEEK)
        return true;

    if (remoteSlot >= MAX_PLAYERS)
        return true;

    // Hide-and-seek: only seekers keep nametags. Hider tags stay fully hidden for everyone.
    return buf->gameModeState.roleBySlot[remoteSlot] == HSR_SEEKER;
}

bool isHideSeekSeekerSlot(u8 slot) {
    const CommBuffer *buf = getCommBuffer();
    if (buf->gameModeState.mode != GM_HIDE_SEEK || slot >= MAX_PLAYERS)
        return false;
    return buf->gameModeState.roleBySlot[slot] == HSR_SEEKER;
}

bool isHideSeekGraceActive() {
    const CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return false;
    const GameModeState &gm = buf->gameModeState;
    // Include graceRemainingMs so a brief GMF_GRACE_ACTIVE gap (failed remote-sync
    // write) does not flash remote hiders / release seeker-side grace visuals.
    return gm.mode == GM_HIDE_SEEK && (gm.flags & GMF_TAG_ACTIVE) != 0 &&
           ((gm.flags & GMF_GRACE_ACTIVE) != 0 || gm.graceRemainingMs > 0);
}

bool shouldSuppressRemoteHiderFromSeekerGrace(u8 remoteSlot) {
    if (!isHideSeekGraceActive())
        return false;

    const CommBuffer *buf = getCommBuffer();
    if (!buf || remoteSlot >= MAX_PLAYERS)
        return false;

    const GameModeState &gm = buf->gameModeState;
    // Prefer localRole (same as blue wash); fall back to roleBySlot[localSlot].
    if (!isLocalSlotSeeker(buf))
        return false;
    if (gm.roleBySlot[remoteSlot] == HSR_SEEKER)
        return false;
    return true;
}

void updateHideSeek(TMarDirector *director) {
    if (!director || !gpMarioAddress)
        return;

    CommBuffer *buf = getCommBuffer();
    GameModeState &gm = buf->gameModeState;
    TGCConsole2 *console = getConsole(director);

    const bool hideSeekMode = gm.mode == GM_HIDE_SEEK;
    const bool tagActive = hideSeekMode && (gm.flags & GMF_TAG_ACTIVE) != 0;
    const bool roundComplete = hideSeekMode && (gm.flags & GMF_ROUND_COMPLETE) != 0;
    const bool roundFanfare = hideSeekMode && (gm.flags & GMF_ROUND_FANFARE) != 0;
    const bool timerReset = hideSeekMode && (gm.flags & GMF_TIMER_RESET) != 0;
    const bool localIsSeeker = hideSeekMode && isLocalSlotSeeker(buf);
    const bool localIsHider = hideSeekMode && !localIsSeeker;
    const bool timerShouldRun = tagActive && localIsHider && !roundComplete;

    // Stop Tag / RoundComplete: drop pin/grace so warps are not rewritten, but keep an
    // in-flight death remount running (same retail path as Pianta). Aborting mid-death
    // left hotel/other stages softlocked in the death cutscene.
    if ((s_tagActive && !tagActive) || (roundComplete && !s_roundCompleteWas)) {
        const bool remountInFlight =
            s_taggedDeathActive || s_deathReloadQueued ||
            s_resumeDeathRecoveryAfterReload || s_deathReloadSettleActive;
        s_tagDeathGraceFrames = 0;
        s_tagPlayStageValid = false;
        s_tagPlayStageMission = 0xFF;
        if (!remountInFlight)
            resetHideSeekDeathState();
    }

    if (!hideSeekMode) {
        if (s_localSeekerLook)
            applyLocalSeekerLookEdge(gpMarioAddress, false);
        stopRetailTimerMovement(console);
        hideTimerPanel(console);
        clearHideSeek();
        return;
    }

    s_hideSeekModeActive = true;

    // Skip stage-entry demos after mid-round HnS warps. Death reload settles after
    // retail STATE_NORMAL (see finishHideSeekDeathRecovery) — no sticky pad restore.
    updateHideSeekIntroSkip(director, gpMarioAddress);

    updateHideSeekGrace(gpMarioAddress, director, gm, localIsSeeker, tagActive);

    tryCaptureLocalTagTimerOnNewEvent(director, console, buf, gm);

    if (tagActive && gm.tagEventId != 0 && gm.tagEventId != s_lastTagSoundEventId) {
        playHideSeekTagSound(buf, gm);
        s_lastTagSoundEventId = gm.tagEventId;
    } else if (!tagActive || gm.tagEventId == 0) {
        s_lastTagSoundEventId = 0;
    }

    const bool timerResetEdge = timerReset && !s_timerResetWas;
    const bool roundStartCleared =
        hideSeekMode && !tagActive && gm.roundStartMs == 0 && s_lastNetworkRoundStartMs > 0;
    if (timerResetEdge || roundStartCleared)
        resetHiderTimerAtZero(director, console);

    const bool hasPausedDisplay = !timerReset && gm.roundStartMs > 0;

    // RoundFanfare is set only by ResetTag(playRoundFanfare) and cleared for late joiners
    // on the server. Prefer that rising edge; also accept RoundComplete (brief pre-ResetTag
    // broadcast). Do NOT require s_tagActive — MarkRoundCompleteFlags clears TagActive in
    // the same packet, and requiring the sticky local flag skipped fanfare when the
    // RoundComplete frame was coalesced away (no OSReport fanfare in dolphin.log).
    const bool roundEndSignal =
        (roundFanfare && !s_roundFanfareWas) || (roundComplete && !s_roundCompleteWas);
    const bool witnessedTagThisSession =
        s_tagActive || s_tagWasActiveForGo || s_localWasHider || s_localWasSeeker ||
        s_taggedDeathActive;
    if (hideSeekMode && roundEndSignal && !s_roundEndFanfarePlayed) {
        if (witnessedTagThisSession) {
            playRoundCompleteFanfare(director, gpMarioAddress);
        } else {
            OSReport("[SMSO] HideSeek round-end fanfare skip (late-join sticky)\n");
        }
        s_roundEndFanfarePlayed = true;
    }

    if (tagActive && !s_tagActive)
        s_roundEndFanfarePlayed = false;

    if (localIsSeeker && gm.tagEventId != 0 && gm.lastTaggedSlot == buf->localSlot &&
        s_handledTagEventId != gm.tagEventId && !s_taggedDeathActive) {
        recoverTaggedSeekerAfterStageReload(director);
    }

    if (s_taggedDeathActive) {
        gpMarioAddress->mAttributes.mIsGameOver = false;
        clampGameOverToDeathStage();
        s_pendingDeathStageReload = false;
        ensureHideSeekDeathStage(director);
        noteTagRoundDeathGrace(gpMarioAddress);

        if (gm.lastTaggedSlot == buf->localSlot && isLocalSlotSeeker(buf) &&
            gm.tagEventId != 0 && gm.tagEventId != s_handledTagEventId) {
            captureHiderTimerSnapshot(director, console);
            s_handledTagEventId = gm.tagEventId;
            s_envDeathPromotionPending = false;
        }

        bool shouldFinish = false;
        const bool postReload = s_resumeDeathRecoveryAfterReload;
        const bool stageReady = director->mCurState >= TMarDirector::STATE_NORMAL;

        ++s_taggedDeathElapsed;
        if (s_taggedDeathTimeout > 0)
            --s_taggedDeathTimeout;

        const bool leftDeathState = gpMarioAddress->mState != TMario::STATE_DEATH;
        const bool timersDone = s_taggedDeathTimeout == 0 ||
                                (s_taggedDeathElapsed >= s_minTaggedDeathFrames && leftDeathState);

        if (postReload) {
            // Keep s_resumeDeathRecoveryAfterReload until finish succeeds — clearing it
            // during STATE_INTRO used to re-queue reloadLocalStage and black-screen.
            if (stageReady && timersDone)
                shouldFinish = true;
        } else if (timersDone) {
            // Pre-reload: finishHideSeekDeathRecovery queues same-stage reload only.
            shouldFinish = true;
        }

        if (shouldFinish)
            finishTaggedDeath(director, gpMarioAddress);

        // completeHideSeekDeathReload arms remount; keep trying even on this early-return path.
        if (s_deathReloadTimerRemountPending)
            tickDeathReloadTimerRemount(director, console, buf, localIsSeeker, tagActive);

        s_tagActive = tagActive;
        s_localWasSeeker = localIsSeeker;
        s_localWasHider = localIsHider;
        s_roundFanfareWas = roundFanfare;
        s_roundCompleteWas = roundComplete;
        s_timerResetWas = timerReset;
        return;
    }

    if (tagActive && !s_taggedDeathActive && gpMarioAddress) {
        TMario *mario = gpMarioAddress;
        noteTagRoundDeathGrace(mario);
        if (isLocalMarioInDeathState(mario)) {
            if (localIsHider)
                captureHiderTimerSnapshot(director, console);
            tryBeginHideSeekDeathRecovery(director, mario);
            s_tagActive = tagActive;
            s_localWasSeeker = localIsSeeker;
            s_localWasHider = localIsHider;
            s_roundFanfareWas = roundFanfare;
            s_roundCompleteWas = roundComplete;
            s_timerResetWas = timerReset;
            return;
        }
    }

    if (isNewLocalTagEvent(gm, buf->localSlot, localIsSeeker) && !s_taggedDeathActive) {
        if (localIsSeeker && s_envDeathPromotionPending) {
            handleLocalSeekerPromotion(director, console, gpMarioAddress, gm);
        } else {
            s_handledTagEventId = gm.tagEventId;
            beginTaggedDeath(director, gpMarioAddress);
            captureHiderTimerSnapshot(director, console);
            s_localWasSeeker = true;
            s_localWasHider = false;
        }

        s_tagActive = tagActive;
        s_roundFanfareWas = roundFanfare;
        s_roundCompleteWas = roundComplete;
        s_timerResetWas = timerReset;
        return;
    }

    if (localIsSeeker && s_envDeathPromotionPending && gm.tagEventId != 0 &&
        gm.lastTaggedSlot == buf->localSlot && s_handledTagEventId == gm.tagEventId &&
        !s_taggedDeathActive)
        handleLocalSeekerPromotion(director, console, gpMarioAddress, gm);

    const bool wantSeekerLook = localIsSeeker;
    applyLocalSeekerLookEdge(gpMarioAddress, wantSeekerLook);

    if (tagActive && !s_tagActive)
        playTagStartSound(gpMarioAddress);

    if ((!tagActive || roundComplete) && s_tagActive && s_localWasHider && !timerReset)
        freezeHiderTimer(director, console);

    if (tagActive && !s_tagActive) {
        s_handledTagEventId = 0;
        if (localIsHider) {
            if (gm.roundStartMs == 0)
                resetHiderTimerAtZero(director, console);
            else
                resumeHiderTimerFromElapsedMs(director, console, gm.roundStartMs);
        } else if (localIsSeeker) {
            s_hiderTimerRunning = false;
            stopRetailTimerMovement(console);
            ensureSeekerTimerPanelVisible(console);
        }
        pinTagPlayStage(director);
    }

    // Keep the pin fresh every tick while tag runs — rising-edge-only left a stale
    // or missing pin when death raced Start Tag / resume.
    if (tagActive && director)
        pinTagPlayStage(director);

    if (localIsSeeker && gm.tagEventId != 0 && gm.lastTaggedSlot == buf->localSlot &&
        s_handledTagEventId == gm.tagEventId && !s_localSeekerLook)
        syncLocalSeekerLookAfterTag(gpMarioAddress);

    if (s_seekerLookRetryFrames > 0 && localIsSeeker && gpMarioAddress) {
        --s_seekerLookRetryFrames;
        if (!s_localSeekerLook)
            applyLocalSeekerPromotionLook(gpMarioAddress);
    }

    if (localIsHider && !s_timerPanelVisible) {
        if (hasPausedDisplay || s_frozenTimerCentiseconds > 0)
            ensureFrozenTimerPanelVisible(console);
        else
            showTimerPanelAtZero(director, console);
    }

    if (tagActive && !roundComplete) {
        if (localIsSeeker)
            ensureSeekerTimerPanelVisible(console);
    } else if (localIsHider && !tagActive && (hasPausedDisplay || s_frozenTimerCentiseconds > 0))
        ensureFrozenTimerPanelVisible(console);
    else if (localIsSeeker && !tagActive)
        ensureSeekerTimerPanelVisible(console);

    // Post death-reload: force appear until retail mIsTimerCard is live (not rising-edge).
    if (s_deathReloadTimerRemountPending)
        tickDeathReloadTimerRemount(director, console, buf, localIsSeeker, tagActive);

    if (!tagActive && localIsHider && !timerReset)
        syncPausedTimerFromNetwork(gm, console);

    updateTimerDisplay(director, console, timerShouldRun, localIsSeeker);

    tickLocalSeekerPromotionVfx(director, gpMarioAddress);

    if (s_tagDeathGraceFrames > 0)
        --s_tagDeathGraceFrames;

    s_tagActive = tagActive;
    s_localWasHider = localIsHider;
    s_localWasSeeker = localIsSeeker;
    s_roundFanfareWas = roundFanfare;
    s_roundCompleteWas = roundComplete;
    s_timerResetWas = timerReset;
    if (hideSeekMode)
        s_lastNetworkRoundStartMs = gm.roundStartMs;
}

void drawHideSeekGrace(const J2DOrthoGraph *graph) {
    if (!graph)
        return;

    const CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return;

    const GameModeState &gm = buf->gameModeState;
    const bool hideSeekMode = gm.mode == GM_HIDE_SEEK;
    const bool tagActive = hideSeekMode && (gm.flags & GMF_TAG_ACTIVE) != 0;
    const bool graceActive = tagActive && isHideSeekGraceActive(gm);
    const bool flashActive = s_graceEndFlashFrames > 0;

    if (!graceActive && !flashActive)
        return;

    auto *ctx = const_cast<J2DOrthoGraph *>(graph);
    gx_hud_fence::beginOverlay(ctx);

    // Cover the full J2D ortho (widescreen-safe). Stacked strips avoid rare large-fill clipping.
    const f32 adjust = BetterSMS::getScreenRatioAdjustX();
    const int left = static_cast<int>(-adjust);
    const int width = BetterSMS::getScreenRenderWidth() > 0
                          ? BetterSMS::getScreenRenderWidth()
                          : static_cast<int>(600.0f + adjust * 2.0f);
    const int screenH = 480;
    const int stripH = 48;

    if (graceActive) {
        const bool localIsSeeker = isLocalSlotSeeker(buf);
        // Blue wash is seeker-only so hiders keep a clear view while hiding.
        if (localIsSeeker) {
            const JUtility::TColor wash(24, 72, 190, 110);
            for (int y = 0; y < screenH; y += stripH) {
                const int h = (y + stripH > screenH) ? (screenH - y) : stripH;
                J2DFillBox(left, y, width, h, wash);
            }
        }

        const int secondsLeft =
            static_cast<int>((gm.graceRemainingMs + 999u) / 1000u);
        const int clamped = secondsLeft < 1 ? 1 : secondsLeft;
        drawGraceCountdownText(clamped, localIsSeeker);
    }

    if (flashActive) {
        const f32 t = static_cast<f32>(s_graceEndFlashFrames) /
                      static_cast<f32>(kGraceEndFlashFrames);
        const u8 flashAlpha = static_cast<u8>(t * 140.0f);
        const JUtility::TColor flash(180, 220, 255, flashAlpha);
        for (int y = 0; y < screenH; y += stripH) {
            const int h = (y + stripH > screenH) ? (screenH - y) : stripH;
            J2DFillBox(left, y, width, h, flash);
        }
    }

    gx_hud_fence::endOverlay(ctx);
}

} // namespace smso
