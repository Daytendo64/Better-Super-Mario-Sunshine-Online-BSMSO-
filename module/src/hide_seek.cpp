#include "hide_seek.hpp"
#include "stage_guard.hpp"

#include "comm_buffer.hpp"
#include "puppets.hpp"

#include <BetterSMS/area.hxx>
#include <BetterSMS/module.hxx>
#include <BetterSMS/player.hxx>
#include <Dolphin/MTX.h>
#include <JSystem/J3D/J3DModel.hxx>
#include <SMS/Player/MarioCap.hxx>
#include <Dolphin/OS.h>
#include <JSystem/J3D/J3DShape.hxx>
#include <SMS/GC2D/GCConsole2.hxx>
#include <SMS/MSound/MSound.hxx>
#include <SMS/MSound/MSoundSESystem.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/raw_fn.hxx>

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;
extern MSound *gpMSound;
extern TApplication gpApplication;

namespace smso {

namespace {

using StartAppearTimerFn = void (*)(TGCConsole2 *, int, s32);
using StartInsertTimerFn = void (*)(TGCConsole2 *);
using StartDisappearTimerFn = void (*)(TGCConsole2 *);
using StartMoveTimerFn = void (*)(TGCConsole2 *, int);
using StopMoveTimerFn = void (*)(TGCConsole2 *);
using SetTimerFn = void (*)(TGCConsole2 *, s32);
using FloorDamageFn = void (*)(TMario *, int, int, int, int);
using CountShineFn = void (*)(TGCConsole2 *);
using StartBgmFn = void (*)(u32);

// gc-forever asset list / OST track 42: demo BGM index 0x26 = Race Fanfare (Il Piantissimo).
constexpr u32 kRaceFanfareBgm = 0x80010026u;

constexpr u16 kTaggedDeathTimeoutFrames = 420u;
constexpr u16 kMinTaggedDeathFrames = 90u;
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
static bool s_deathStageCaptured = false;
static u16 s_taggedDeathElapsed = 0;
static u16 s_taggedDeathTimeout = 0;
static s32 s_frozenTimerCentiseconds = 0;
static bool s_resumeDeathRecoveryAfterReload = false;
static bool s_envDeathRecovery = false;
static bool s_envDeathPromotionPending = false;
static u8 s_seekerLookRetryFrames = 0;
static u8 s_pendingSeekerPromotionVfxFrames = 0;
static bool s_hideSeekHooksInstalled = false;
static bool s_allowDeathStageTransition = false;
static bool s_allowLauncherStageTransition = false;
static bool s_authorizedStageExitPending = false;
static bool s_tagPlayStageValid = false;
static u8 s_tagPlayStageArea = 0;
static u8 s_tagPlayStageEpisode = 0;
static u16 s_tagDeathGraceFrames = 0;
static u32 s_lastNetworkRoundStartMs = 0;

constexpr u16 kTagDeathGraceFrames = 360u;

static void pinTagPlayStage(TMarDirector *director) {
    if (!director)
        return;

    s_tagPlayStageArea = director->mAreaID;
    s_tagPlayStageEpisode = director->mEpisodeID;
    s_tagPlayStageValid = true;
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

static FloorDamageFn floorDamageExecFn() {
    return reinterpret_cast<FloorDamageFn>(SMS_PORT_REGION(0x8024303C, 0x8023ADC8, 0, 0));
}

static CountShineFn countShineFn() {
    return reinterpret_cast<CountShineFn>(SMS_PORT_REGION(0x80147A0C, 0x8013C690, 0, 0));
}

static StartBgmFn startBgmFn() {
    return reinterpret_cast<StartBgmFn>(SMS_PORT_REGION(0x80016978, 0x800169D4, 0, 0));
}

static TGCConsole2 *getConsole(TMarDirector *director) {
    return director && director->mGCConsole ? director->mGCConsole : nullptr;
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
    if (!mario)
        return;

    TGCConsole2 *console = getConsole(director);
    if (console)
        countShineFn()(console);

    if (gpMSound && gpMSound->gateCheck(MSD_SE_SY_RACE_FIRE)) {
        const Vec pos = {mario->mTranslation.x, mario->mTranslation.y, mario->mTranslation.z};
        MSoundSESystem::MSoundSE::startSoundActor(MSD_SE_SY_RACE_FIRE, &pos, 0, nullptr, 0, 4);
    }

    startBgmFn()(kRaceFanfareBgm);
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

static void freezeHiderTimer(TMarDirector *director, TGCConsole2 *console) {
    if (!s_hiderTimerRunning)
        return;

    // Read before stopMoveTimer — retail stopMoveTimer syncs the director baseline to now,
    // which would zero out elapsed time on resumed rounds (doldecomp GCConsole2::stopMoveTimer).
    const s32 centiseconds = readDirectorRaceCentiseconds(director);
    stopRetailTimerMovement(console);
    s_frozenTimerCentiseconds = centiseconds;
    s_hiderTimerRunning = false;
    setTimerCentiseconds(console, s_frozenTimerCentiseconds);
}

static void captureHiderTimerSnapshot(TMarDirector *director, TGCConsole2 *console) {
    if (!director || !console)
        return;

    if (s_hiderTimerRunning) {
        freezeHiderTimer(director, console);
    } else if (s_timerPanelVisible) {
        const s32 centiseconds = readDirectorRaceCentiseconds(director);
        if (centiseconds > 0)
            s_frozenTimerCentiseconds = centiseconds;
    }

    if (s_frozenTimerCentiseconds > 0 || s_hiderTimerRunning)
        s_localHasRunHiderTimer = true;
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
    s_frozenTimerCentiseconds = centiseconds > 0 ? centiseconds : 0;
    s_localHasRunHiderTimer = s_frozenTimerCentiseconds > 0;
    s_hiderTimerRunning = false;
    stopRetailTimerMovement(console);

    if (!s_timerPanelVisible)
        ensureFrozenTimerPanelVisible(console);
    else
        setTimerCentiseconds(console, s_frozenTimerCentiseconds);

    backdateDirectorRaceStopwatch(director, s_frozenTimerCentiseconds);
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

static void captureHideSeekDeathStage(TMarDirector *director) {
    if (s_tagPlayStageValid) {
        s_deathAreaId = s_tagPlayStageArea;
        s_deathEpisodeId = s_tagPlayStageEpisode;
    } else if (director) {
        s_deathAreaId = director->mAreaID;
        s_deathEpisodeId = director->mEpisodeID;
    } else {
        return;
    }

    s_deathStageCaptured = true;
}

static void ensureHideSeekDeathStage(TMarDirector *director) {
    if (!director || !s_deathStageCaptured)
        return;

    if (director->mAreaID == s_deathAreaId && director->mEpisodeID == s_deathEpisodeId) {
        clampGameOverToDeathStage();
        return;
    }

    reloadLocalStage(director, s_deathAreaId, s_deathEpisodeId);
}

static bool isLocalMarioInDeathState(const TMario *mario) {
    if (!mario)
        return false;

    return mario->mState == TMario::STATE_DEATH || mario->mAttributes.mIsGameOver ||
           mario->mHealth <= 0;
}

static void beginHideSeekDeathRecovery(TMarDirector *director, TMario *mario, bool forceDeathAnim) {
    if (!mario || s_taggedDeathActive)
        return;

    captureHideSeekDeathStage(director);
    s_envDeathRecovery = !forceDeathAnim;
    if (!forceDeathAnim)
        s_envDeathPromotionPending = true;

    if (forceDeathAnim) {
        mario->mInvincibilityFrames = 0;
        floorDamageExecFn()(mario, 8, 0, 0, 0);
        if (mario->mState != TMario::STATE_DEATH)
            mario->changePlayerDropping(TMario::STATE_DEATH, 0);
    }

    s_taggedDeathActive = true;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = kTaggedDeathTimeoutFrames;
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

    captureHideSeekDeathStage(director);
    if (!s_deathStageCaptured)
        return;

    s_envDeathRecovery = true;
    s_envDeathPromotionPending = true;
    s_taggedDeathActive = true;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = kTaggedDeathTimeoutFrames;
    clampGameOverToDeathStage();

    if (gpMarioAddress)
        gpMarioAddress->mAttributes.mIsGameOver = false;

    OSReport("[SMSO] HideSeek death stage locked area=%u ep=%u\n", s_deathAreaId, s_deathEpisodeId);
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

    if (mario->mCap->mCap1)
        bindModelToHeadBone(mario->mCap->mCap1, mheadMtx);
    if (mario->mCap->mCap3)
        bindModelToHeadBone(mario->mCap->mCap3, mheadMtx);
    if (mario->mCap->maGlass1)
        bindModelToHeadBone(mario->mCap->maGlass1, mheadMtx);

    mario->mCap->perform(2, graphics);
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

    captureHiderTimerSnapshot(director, console);
    ensureSeekerTimerPanelVisible(console);
    s_localWasSeeker = true;
    s_localWasHider = false;
    s_envDeathRecovery = false;
    s_envDeathPromotionPending = false;

    if (mario && director && director->mCurState >= TMarDirector::STATE_NORMAL &&
        !isLocalMarioInDeathState(mario) && !s_taggedDeathActive)
        applyLocalSeekerPromotionLook(mario);
}

static void finishHideSeekDeathRecovery(TMarDirector *director, TMario *mario) {
    if (!director || !mario)
        return;

    CommBuffer *buf = getCommBuffer();
    TGCConsole2 *console = getConsole(director);
    ensureHideSeekDeathStage(director);
    if (!s_deathStageCaptured || director->mAreaID != s_deathAreaId ||
        director->mEpisodeID != s_deathEpisodeId) {
        return;
    }
    if (director->mCurState < TMarDirector::STATE_NORMAL) {
        return;
    }

    respawnLocalMarioAtStageSpawn(director, mario);
    while (mario->mHealth < 8)
        mario->incHP(1);

    mario->mAttributes.mIsGameOver = false;
    clampGameOverToDeathStage();

    const bool localIsSeeker = isLocalSlotSeeker(buf);
    s_localWasSeeker = localIsSeeker;
    s_localWasHider = !localIsSeeker;
    s_envDeathRecovery = false;

    if (localIsSeeker)
        handleLocalSeekerPromotion(director, console, mario, buf->gameModeState);

    s_taggedDeathActive = false;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = 0;
    s_deathStageCaptured = false;
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

            CommBuffer *buf = getCommBuffer();
            if (!isHideSeekTagRoundActive(buf))
                return;

            if (s_taggedDeathActive) {
                player->mAttributes.mIsGameOver = false;
                clampGameOverToDeathStage();
                noteTagRoundDeathGrace(player);
                if (s_deathStageCaptured &&
                    (gpMarDirector->mAreaID != s_deathAreaId ||
                     gpMarDirector->mEpisodeID != s_deathEpisodeId))
                    ensureHideSeekDeathStage(gpMarDirector);
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
            smso::performSmsoMoveStage(director);
            smso::clearAuthorizedStageMovePending();
            return;
        }

        if (s_allowLauncherStageTransition || launcherWarp) {
            s_allowLauncherStageTransition = false;
            smso::performSmsoMoveStage(director);
            smso::clearAuthorizedStageMovePending();
            return;
        }

        if (s_taggedDeathActive && s_deathStageCaptured) {
            const u8 nextArea = gpApplication.mNextScene.mAreaID;
            const u8 nextEp = gpApplication.mNextScene.mEpisodeID;
            if (nextArea != s_deathAreaId || nextEp != s_deathEpisodeId) {
                if (gpMarioAddress)
                    gpMarioAddress->mAttributes.mIsGameOver = false;
                reloadLocalStage(director, s_deathAreaId, s_deathEpisodeId);
                return;
            }
        }

        if (!shouldAllowMoveStage(director)) {
            static u32 s_blockedMoveStageLogCooldown = 0;
            if (s_blockedMoveStageLogCooldown == 0) {
                OSReport("[SMSO] Blocked moveStage — only local Mario may leave via loading zones\n");
                s_blockedMoveStageLogCooldown = 120;
            } else {
                --s_blockedMoveStageLogCooldown;
            }
            gpApplication.mNextScene = gpApplication.mCurrentScene;
            smso::clearBlockedLoadingZoneTransition(director);
            smso::clearAuthorizedStageMovePending();
            return;
        }
        smso::performSmsoMoveStage(director);
        smso::clearAuthorizedStageMovePending();
    });
}

static void resetHideSeekRoundUiState() {
    s_timerPanelVisible = false;
    s_retailTimerMoving = false;
    s_hiderTimerRunning = false;
    s_frozenTimerCentiseconds = 0;
}

static void resetHideSeekDeathState() {
    s_taggedDeathActive = false;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = 0;
    s_deathAreaId = 0;
    s_deathEpisodeId = 0;
    s_deathStageCaptured = false;
}

static void resetHideSeekTransientState() {
    resetHideSeekRoundUiState();
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

    s_frozenTimerCentiseconds = pausedCentiseconds;
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
        if (isHideSeekTagRoundActive(getCommBuffer()) && gpMarDirector)
            pinTagPlayStage(gpMarDirector);
    }

    if (s_taggedDeathActive && s_deathStageCaptured) {
        s_resumeDeathRecoveryAfterReload = true;
        stopRetailTimerMovement(getConsole(gpMarDirector));
        s_hiderTimerRunning = false;
        if (gpMarDirector &&
            (gpMarDirector->mAreaID != s_deathAreaId ||
             gpMarDirector->mEpisodeID != s_deathEpisodeId)) {
            OSReport("[SMSO] HideSeek reloading death stage area=%u ep=%u (was area=%u ep=%u)\n",
                     s_deathAreaId, s_deathEpisodeId, gpMarDirector->mAreaID,
                     gpMarDirector->mEpisodeID);
            reloadLocalStage(gpMarDirector, s_deathAreaId, s_deathEpisodeId);
        }
        return;
    }

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
            stopRetailTimerMovement(getConsole(gpMarDirector));
            s_hiderTimerRunning = false;
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
            stopRetailTimerMovement(getConsole(gpMarDirector));
            s_hiderTimerRunning = false;
            return;
        }
    }

    if (!s_taggedDeathActive)
        resetHideSeekTransientState();
}

void guardHideSeekDeathBeforeWarp(TMarDirector *director) {
    if (!director || !gpMarioAddress)
        return;

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
    s_frozenTimerCentiseconds = 0;
    s_lastNetworkRoundStartMs = 0;
    s_deathAreaId = 0;
    s_deathEpisodeId = 0;
    s_deathStageCaptured = false;
    s_envDeathRecovery = false;
    s_envDeathPromotionPending = false;
    s_seekerLookRetryFrames = 0;
    s_pendingSeekerPromotionVfxFrames = 0;
    s_tagPlayStageValid = false;
    s_authorizedStageExitPending = false;
    s_tagDeathGraceFrames = 0;
}

void setHideSeekAllowStageTransition(bool allow) {
    if (allow)
        s_authorizedStageExitPending = true;
    setAllowLauncherStageTransition(allow);
}

void setHideSeekAllowDeathStageReload(bool allow) {
    setAllowDeathStageTransition(allow);
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

bool shouldDrawHideSeekNameTag(u8 remoteSlot) {
    if (!s_hideSeekModeActive)
        return true;

    const CommBuffer *buf = getCommBuffer();
    if (buf->gameModeState.mode != GM_HIDE_SEEK)
        return true;

    if (remoteSlot >= MAX_PLAYERS)
        return true;

    if (buf->gameModeState.localRole == HSR_HIDER)
        return true;

    return buf->gameModeState.roleBySlot[remoteSlot] == HSR_SEEKER;
}

bool isHideSeekSeekerSlot(u8 slot) {
    const CommBuffer *buf = getCommBuffer();
    if (buf->gameModeState.mode != GM_HIDE_SEEK || slot >= MAX_PLAYERS)
        return false;
    return buf->gameModeState.roleBySlot[slot] == HSR_SEEKER;
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

    if (!hideSeekMode) {
        if (s_localSeekerLook)
            applyLocalSeekerLookEdge(gpMarioAddress, false);
        stopRetailTimerMovement(console);
        hideTimerPanel(console);
        clearHideSeek();
        return;
    }

    s_hideSeekModeActive = true;

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

    const bool roundEndSignal =
        (roundFanfare && !s_roundFanfareWas) || (roundComplete && !s_roundCompleteWas);
    if (hideSeekMode && roundEndSignal && !s_roundEndFanfarePlayed) {
        playRoundCompleteFanfare(director, gpMarioAddress);
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
        ensureHideSeekDeathStage(director);
        noteTagRoundDeathGrace(gpMarioAddress);

        if (gm.lastTaggedSlot == buf->localSlot && isLocalSlotSeeker(buf) &&
            gm.tagEventId != 0 && gm.tagEventId != s_handledTagEventId) {
            captureHiderTimerSnapshot(director, console);
            s_handledTagEventId = gm.tagEventId;
            s_envDeathPromotionPending = false;
        }

        if (s_resumeDeathRecoveryAfterReload) {
            s_resumeDeathRecoveryAfterReload = false;
            if (director->mCurState >= TMarDirector::STATE_NORMAL)
                finishTaggedDeath(director, gpMarioAddress);
        }

        ++s_taggedDeathElapsed;
        if (s_taggedDeathTimeout > 0)
            --s_taggedDeathTimeout;

        const bool leftDeathState = gpMarioAddress->mState != TMario::STATE_DEATH;
        const bool deathFinished = s_taggedDeathTimeout == 0 ||
                                   (s_taggedDeathElapsed >= kMinTaggedDeathFrames && leftDeathState);
        if (deathFinished)
            finishTaggedDeath(director, gpMarioAddress);

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

} // namespace smso
