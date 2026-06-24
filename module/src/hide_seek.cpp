#include "hide_seek.hpp"

#include "comm_buffer.hpp"
#include "puppets.hpp"

#include <BetterSMS/area.hxx>
#include <BetterSMS/module.hxx>
#include <BetterSMS/player.hxx>
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
static u8 s_handledTagEventId = 0;
static u8 s_deathAreaId = 0;
static u8 s_deathEpisodeId = 0;
static bool s_deathStageCaptured = false;
static u16 s_taggedDeathElapsed = 0;
static u16 s_taggedDeathTimeout = 0;
static s32 s_frozenTimerCentiseconds = 0;
static bool s_resumeDeathRecoveryAfterReload = false;
static bool s_hideSeekHooksInstalled = false;
static bool s_tagRoundPinActive = false;
static bool s_allowTagRoundStageTransition = false;
static bool s_updatePinOnNextStageInit = false;
static u8 s_tagRoundPinArea = 0;
static u8 s_tagRoundPinEpisode = 0;

static void clampTagRoundStage(TMarDirector *director) {
    if (!s_tagRoundPinActive || !director)
        return;

    gpApplication.mNextScene.mAreaID = s_tagRoundPinArea;
    gpApplication.mNextScene.mEpisodeID = s_tagRoundPinEpisode;
}

static bool isHideSeekTagRoundActive(const CommBuffer *buf) {
    if (!buf)
        return false;

    const GameModeState &gm = buf->gameModeState;
    return gm.mode == GM_HIDE_SEEK && (gm.flags & GMF_TAG_ACTIVE) != 0;
}

static void pinHideSeekTagRoundStage(TMarDirector *director) {
    if (!director)
        return;

    s_tagRoundPinArea = director->mAreaID;
    s_tagRoundPinEpisode = director->mEpisodeID;
    s_tagRoundPinActive = true;
}

static bool enforceHideSeekTagRoundStage(TMarDirector *director) {
    if (!director || !isHideSeekTagRoundActive(getCommBuffer()) || !s_tagRoundPinActive)
        return false;

    if (director->mAreaID == s_tagRoundPinArea && director->mEpisodeID == s_tagRoundPinEpisode) {
        clampTagRoundStage(director);
        return false;
    }

    reloadLocalStage(director, s_tagRoundPinArea, s_tagRoundPinEpisode);
    return true;
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

static void freezeHiderTimer(TMarDirector *director, TGCConsole2 *console) {
    if (!s_hiderTimerRunning)
        return;

    stopRetailTimerMovement(console);
    s_frozenTimerCentiseconds = readDirectorRaceCentiseconds(director);
    s_hiderTimerRunning = false;
    setTimerCentiseconds(console, s_frozenTimerCentiseconds);
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
    else
        resetDirectorRaceStopwatch(director);

    startMoveTimerFn()(console, 0);
    s_retailTimerMoving = true;
    s_hiderTimerRunning = true;
    syncTimerFromDirectorStopwatch(console);
}

static void resetHiderTimerAtZero(TMarDirector *director, TGCConsole2 *console) {
    s_frozenTimerCentiseconds = 0;
    s_hiderTimerRunning = false;
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
    if (!director)
        return;

    s_deathAreaId = director->mAreaID;
    s_deathEpisodeId = director->mEpisodeID;
    s_deathStageCaptured = true;
    s_tagRoundPinArea = s_deathAreaId;
    s_tagRoundPinEpisode = s_deathEpisodeId;
    s_tagRoundPinActive = true;
}

static void ensureHideSeekDeathStage(TMarDirector *director) {
    if (!director || !s_deathStageCaptured)
        return;

    if (director->mAreaID == s_deathAreaId && director->mEpisodeID == s_deathEpisodeId)
        return;

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

static void beginTaggedDeath(TMarDirector *director, TMario *mario) {
    beginHideSeekDeathRecovery(director, mario, true);
}

static void finishHideSeekDeathRecovery(TMarDirector *director, TMario *mario) {
    if (!director || !mario)
        return;

    CommBuffer *buf = getCommBuffer();
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

    const bool localIsSeeker = buf->gameModeState.localRole == HSR_SEEKER;
    s_localWasSeeker = localIsSeeker;
    s_localWasHider = !localIsSeeker;

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

            player->mAttributes.mIsGameOver = false;
            clampTagRoundStage(gpMarDirector);

            if (s_taggedDeathActive) {
                clampGameOverToDeathStage();
                if (s_deathStageCaptured &&
                    (gpMarDirector->mAreaID != s_deathAreaId ||
                     gpMarDirector->mEpisodeID != s_deathEpisodeId))
                    ensureHideSeekDeathStage(gpMarDirector);
                return;
            }

            if (isLocalMarioInDeathState(player))
                beginHideSeekDeathRecovery(gpMarDirector, player, false);
        });
    BetterSMS::Stage::setNextStageHandler([](TMarDirector *director) {
        if (!director)
            return;

        if (enforceHideSeekTagRoundStage(director))
            return;

        CommBuffer *buf = getCommBuffer();
        if (isHideSeekTagRoundActive(buf) && s_tagRoundPinActive) {
            if (s_allowTagRoundStageTransition) {
                s_allowTagRoundStageTransition = false;
                director->moveStage();
                return;
            }

            const u8 keepArea = s_tagRoundPinArea;
            const u8 keepEp = s_tagRoundPinEpisode;
            const u8 nextArea = gpApplication.mNextScene.mAreaID;
            const u8 nextEp = gpApplication.mNextScene.mEpisodeID;
            if (nextArea != keepArea || nextEp != keepEp) {
                gpApplication.mNextScene.mAreaID = keepArea;
                gpApplication.mNextScene.mEpisodeID = keepEp;
                if (gpMarioAddress) {
                    gpMarioAddress->mAttributes.mIsGameOver = false;
                    if (isLocalMarioInDeathState(gpMarioAddress))
                        beginHideSeekDeathRecovery(director, gpMarioAddress, false);
                }
                return;
            }
        }

        director->moveStage();
    });
}

static void resetHideSeekTransientState() {
    s_timerPanelVisible = false;
    s_retailTimerMoving = false;
    s_hiderTimerRunning = false;
    s_taggedDeathActive = false;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = 0;
    s_frozenTimerCentiseconds = 0;
    s_deathAreaId = 0;
    s_deathEpisodeId = 0;
    s_deathStageCaptured = false;
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

    applyLocalSeekerLookEdge(mario, true);
}

static void syncLocalSeekerLookAfterTag(TMario *mario) {
    if (!mario || s_localSeekerLook)
        return;

    applyLocalSeekerLookEdge(mario, true);
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

static void updateTimerDisplay(TMarDirector *director, TGCConsole2 *console, bool timerShouldRun) {
    if (!console || !s_timerPanelVisible)
        return;

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

static void setAllowTagRoundStageTransition(bool allow) {
    s_allowTagRoundStageTransition = allow;
    if (allow)
        s_updatePinOnNextStageInit = true;
}

} // namespace

void playHideSeekSeekerCosmeticVfx(TMario *mario) {
    if (!mario)
        return;

    // doldecomp MarioDraw.cpp wearGlass -> emitGetEffect (one-shot burst, no lingering aura).
    mario->emitGetEffect();
}

void applyHideSeekPlayerCosmetics(TMario *mario, bool isSeeker, bool isRemote) {
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

void initHideSeek() {
    installHideSeekDeathHooks();

    if (s_taggedDeathActive && s_deathStageCaptured) {
        s_resumeDeathRecoveryAfterReload = true;
        s_timerPanelVisible = false;
        s_retailTimerMoving = false;
        s_hiderTimerRunning = false;
        return;
    }

    if (isHideSeekTagRoundActive(getCommBuffer()) && gpMarDirector) {
        if (s_updatePinOnNextStageInit) {
            s_updatePinOnNextStageInit = false;
            pinHideSeekTagRoundStage(gpMarDirector);
        } else if (s_tagRoundPinActive) {
            if (enforceHideSeekTagRoundStage(gpMarDirector)) {
                s_timerPanelVisible = false;
                s_retailTimerMoving = false;
                s_hiderTimerRunning = false;
                return;
            }
        } else {
            pinHideSeekTagRoundStage(gpMarDirector);
        }
    }

    resetHideSeekTransientState();
}

void onHideSeekStageExit() {
    if (s_taggedDeathActive && s_deathStageCaptured) {
        s_timerPanelVisible = false;
        s_retailTimerMoving = false;
        s_hiderTimerRunning = false;
        return;
    }

    resetHideSeekTransientState();
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
    s_handledTagEventId = 0;
    s_taggedDeathElapsed = 0;
    s_taggedDeathTimeout = 0;
    s_frozenTimerCentiseconds = 0;
    s_deathAreaId = 0;
    s_deathEpisodeId = 0;
    s_deathStageCaptured = false;
    s_tagRoundPinActive = false;
}

void setHideSeekAllowStageTransition(bool allow) {
    setAllowTagRoundStageTransition(allow);
}

bool isHideSeekActive() {
    return s_hideSeekModeActive;
}

bool isHideSeekTaggedDeathActive() {
    return s_taggedDeathActive;
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

    if (remoteSlot >= 4)
        return true;

    if (buf->gameModeState.localRole == HSR_HIDER)
        return true;

    return buf->gameModeState.roleBySlot[remoteSlot] == HSR_SEEKER;
}

bool isHideSeekSeekerSlot(u8 slot) {
    const CommBuffer *buf = getCommBuffer();
    if (buf->gameModeState.mode != GM_HIDE_SEEK || slot >= 4)
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
    const bool localIsHider = hideSeekMode && gm.localRole == HSR_HIDER;
    const bool localIsSeeker = hideSeekMode && gm.localRole == HSR_SEEKER;
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

    if (tagActive && s_tagRoundPinActive) {
        if (enforceHideSeekTagRoundStage(director))
            return;
        clampTagRoundStage(director);
        gpMarioAddress->mAttributes.mIsGameOver = false;
    }

    if (timerReset && !s_timerResetWas)
        resetHiderTimerAtZero(director, console);

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
        if (isLocalMarioInDeathState(mario)) {
            beginHideSeekDeathRecovery(director, mario, false);
            s_tagActive = tagActive;
            s_localWasSeeker = localIsSeeker;
            s_localWasHider = localIsHider;
            s_roundFanfareWas = roundFanfare;
            s_roundCompleteWas = roundComplete;
            s_timerResetWas = timerReset;
            return;
        }
    }

    if (isNewLocalTagEvent(gm, buf->localSlot, localIsSeeker)) {
        s_handledTagEventId = gm.tagEventId;
        beginTaggedDeath(director, gpMarioAddress);
        freezeHiderTimer(director, console);
        s_localWasSeeker = true;
        s_localWasHider = false;
        s_tagActive = tagActive;
        s_roundFanfareWas = roundFanfare;
        s_roundCompleteWas = roundComplete;
        s_timerResetWas = timerReset;
        return;
    }

    const bool wantSeekerLook = localIsSeeker;
    applyLocalSeekerLookEdge(gpMarioAddress, wantSeekerLook);

    if (tagActive && !s_tagActive)
        playTagStartSound(gpMarioAddress);

    if ((!tagActive || roundComplete) && s_tagActive && s_localWasHider)
        freezeHiderTimer(director, console);

    if (tagActive && !s_tagActive) {
        s_handledTagEventId = 0;
        resetHiderTimerAtZero(director, console);
        pinHideSeekTagRoundStage(director);
    }

    if (!tagActive)
        s_tagRoundPinActive = false;

    if (localIsSeeker && gm.tagEventId != 0 && gm.lastTaggedSlot == buf->localSlot &&
        s_handledTagEventId == gm.tagEventId && !s_localSeekerLook)
        syncLocalSeekerLookAfterTag(gpMarioAddress);

    if (localIsHider && !s_timerPanelVisible)
        showTimerPanelAtZero(director, console);

    updateTimerDisplay(director, console, timerShouldRun);

    s_tagActive = tagActive;
    s_localWasHider = localIsHider;
    s_localWasSeeker = localIsSeeker;
    s_roundFanfareWas = roundFanfare;
    s_roundCompleteWas = roundComplete;
    s_timerResetWas = timerReset;
}

} // namespace smso
