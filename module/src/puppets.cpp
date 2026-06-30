#include "puppets.hpp"
#include "comm_buffer.hpp"
#include "hide_seek.hpp"
#include "stage_guard.hpp"
#include "yoshi_sync.hpp"

#include <BetterSMS/loading.hxx>
#include <Dolphin/mem.h>
#include <Dolphin/string.h>
#include <Dolphin/types.h>
#include <SMS/Camera/PolarSubCamera.hxx>
#include <SMS/Manager/FlagManager.hxx>
#include <SMS/Map/BGCheck.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/Watergun.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>
#include <SMS/raw_fn.hxx>

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;
extern TApplication gpApplication;
extern CPolarSubCamera *gpCamera;

namespace smso {

// Retail FLUDD pack visibility on Mario's back (Super Mario Wiki / GameFAQs):
// - Hidden when riding Yoshi (pack is on Yoshi, not Mario).
// - Hidden when Shadow Mario steals FLUDD (mHasFludd == false):
//     * First entry into a secret course before its Shine is collected.
//     * Pianta Village Ep. 3 (The Goopy Inferno) until FLUDD is rescued in-stage.
// - Visible on secret revisits (red-coin rematch), sky/special stages (Slide Stage,
//   Gelato Ep. 4, Noki Ep. 3, etc.), and all normal gameplay once FLUDD is obtained.
bool shouldShowFluddPackOnMario(const TMario *mario) {
    if (!mario || !mario->mFludd)
        return false;
    if (mario->onYoshi())
        return false;
    return mario->mAttributes.mHasFludd;
}

// Delfino Plaza hub: catalog episode IDs map to in-game scenario indices.
// dolpic archive numbers != scenario indices (dolpic10 loads at scenario 2).
struct DelfinoPlazaEpisode {
    u8 catalogId;
    u8 scenarioId;
};

static constexpr u8 kDelfinoPlazaAreaId = 1;
static constexpr DelfinoPlazaEpisode kDelfinoPlazaEpisodes[] = {
    {0, 8}, // dolpic8 — open hub
    {1, 0}, // dolpic0 — arrival
    {2, 1}, // dolpic1
    {3, 5}, // dolpic5
    {4, 6}, // dolpic6
    {5, 7}, // dolpic7
    {6, 9}, // dolpic9 — flooded
    {7, 2}, // dolpic10 — post-flood (scenario 2, not 10)
};

// Sirena hotel interior (area 7): only delfino0/2/4 stage files load; 6 and 7 crash to title.
// timenoe/RAScripts: Sirena ep 8 red coins use logical episode 5 → scenario 4.
struct SirenaHotelEpisode {
    u8 catalogId;
    u8 loadScenario;
    u8 missionEpisode;
};

struct WarpTarget {
    u8 loadScenario;
    u8 missionEpisode;
};

static constexpr u8 kSirenaBeachAreaId = 6;
static constexpr u8 kSirenaHotelAreaId = 7;
static constexpr u8 kInvalidHotelMission = 0xFF;
// Survives stageExit/clearPuppets — set in consumeWarpIntent, consumed in applyHotelWarpMissionOverride.
// doldecomp/BSE Stage::getStageName indexes mStageArchiveAry by episode; delfino6 is missing on disc.
static u8 s_hotelMissionEpisodeSync = kInvalidHotelMission;
static u8 s_hotelWarpDemoSkipFrames = 0;

struct ResolvedWarp {
    u8 areaId;
    WarpTarget target;
};

static constexpr SirenaHotelEpisode kSirenaHotelEpisodes[] = {
    {0, 0, 0}, // delfino0 lobby
    {2, 2, 2}, // Sirena ep 3 — Mysterious Hotel Delfino
    {6, 0, 6}, // Sirena ep 7 — Shadow Mario (delfino0 map, ep7 mission scripts)
    {7, 4, 4}, // Sirena ep 8 — Red Coins (logical ep 5 → scenario 4)
};

static WarpTarget resolveHotelWarpTarget(u8 catalogEpisodeId) {
    for (const auto &ep : kSirenaHotelEpisodes) {
        if (ep.catalogId == catalogEpisodeId)
            return {ep.loadScenario, ep.missionEpisode};
    }
    return {catalogEpisodeId, catalogEpisodeId};
}

static WarpTarget resolveWarpTarget(u8 areaId, u8 catalogEpisodeId) {
    if (areaId == kSirenaHotelAreaId)
        return resolveHotelWarpTarget(catalogEpisodeId);

    if (areaId == kDelfinoPlazaAreaId) {
        for (const auto &ep : kDelfinoPlazaEpisodes) {
            if (ep.catalogId == catalogEpisodeId)
                return {ep.scenarioId, ep.scenarioId};
        }
    }

    return {catalogEpisodeId, catalogEpisodeId};
}

// Shadow Mario / hotel red coins play inside area 7 even when launched from Sirena Beach (area 6).
static ResolvedWarp resolveFullWarp(u8 areaId, u8 catalogEpisodeId) {
    if (areaId == kSirenaBeachAreaId && (catalogEpisodeId == 6 || catalogEpisodeId == 7))
        return {kSirenaHotelAreaId, resolveHotelWarpTarget(catalogEpisodeId)};

    return {areaId, resolveWarpTarget(areaId, catalogEpisodeId)};
}

static u8 resolveWarpScenario(u8 areaId, u8 catalogEpisodeId) {
    return resolveWarpTarget(areaId, catalogEpisodeId).missionEpisode;
}

// doldecomp CPolarSubCamera::mCurrentTarget.mPitch @ 0xA4 (BSE names the field _02).
// doldecomp TWaterGun tail fields (BSE Watergun.hxx lumps these as mGeometry[]).
constexpr u32 kFluddSwitchProgressOffset = 0x1CFC;
constexpr u32 kFluddSwitchSpeedOffset = 0x1D00;
constexpr u32 kFluddDeployOffset = 0x1CEC;

static f32 readFluddSwitchProgress(TWaterGun *fludd) {
    return *reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(fludd) + kFluddSwitchProgressOffset);
}

static f32 readFluddSwitchSpeed(TWaterGun *fludd) {
    return *reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(fludd) + kFluddSwitchSpeedOffset);
}

static f32 readFluddDeploy(TWaterGun *fludd) {
    return *reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(fludd) + kFluddDeployOffset);
}

static bool isLocalBlooperSurf(const TMario *mario) {
    if (!mario)
        return false;
    const u32 state = mario->mState;
    if ((state & 0x10000u) == 0)
        return false;
    const u32 id = state & 0x1FFu;
    return id == 0x046u || id == 0x09Au;
}

static s16 readHostGunAngle(TMario *mario) {
    if (!mario->mFludd || !mario->mAttributes.mHasFludd)
        return 0;
    TNozzleBase *nozzle = mario->mFludd->mNozzleList[mario->mFludd->mCurrentNozzle];
    return nozzle ? nozzle->mGunAngle : 0;
}

static u8 exportHandIndex(TMario *mario) {
    if ((mario->mState & 0x1C0) == 0x040 &&
        (mario->mAnimationID == 0x48 || mario->mAnimationID == 0x72)) {
        const f32 mix = 1.0f - mario->_41C;
        if (mix < 0.3f)
            return 2;
        if (mix <= 0.7f)
            return 1;
        return 0;
    }
    return 0;
}

static s16 getLButtonCameraPitch() {
    if (!gpCamera)
        return 0;
    return *reinterpret_cast<s16 *>(reinterpret_cast<u8 *>(gpCamera) + 0xA4);
}

static bool hostUsesWetSlideParticles(const TMario *mario) {
    // Retail frontSlipEffect() wet branch: belly CATCH in/on water only.
    if ((mario->mState & 0x1FFu) != 0x056u)
        return false;

    if (mario->mSubState == 1)
        return true;
    if (mario->mAttributes.mIsWater || mario->mAttributes.mIsShallowWater)
        return true;

    const TBGCheckData *ground = mario->mFloorTriangle;
    return ground && ground->isWaterSlip();
}

static u16 buildVfxFlags(TMario *mario) {
    u16 vfx = 0;

    const bool yCamActive =
        gpCamera && gpCamera->isLButtonCameraSpecifyMode(static_cast<int>(gpCamera->mMode));

    auto readNozzlePressure = [](TWaterGun *fludd) -> f32 {
        if (!fludd)
            return 0.0f;
        TNozzleBase *nozzle = fludd->mNozzleList[fludd->mCurrentNozzle];
        if (!nozzle)
            return 0.0f;
        const u8 nozzleType = fludd->mCurrentNozzle;
        if (nozzleType == TWaterGun::Hover || nozzleType == TWaterGun::Rocket ||
            nozzleType == TWaterGun::Turbo) {
            const auto *trigger = static_cast<const TNozzleTrigger *>(nozzle);
            const f32 maxPressure = trigger->mEmitParams.mInsidePressureMax.get();
            if (maxPressure <= 0.0f)
                return 0.0f;
            return trigger->mTriggerFill / maxPressure;
        }
        return nozzle->_378;
    };

    const bool sprayAttempt = [&]() -> bool {
        if (!mario->mFludd || !mario->mAttributes.mHasFludd)
            return false;
        if (mario->mFludd->mIsEmitWater)
            return true;
        if (!yCamActive && mario->mAttributes.mIsFluddEmitting && mario->mFluddUsageState <= 1)
            return true;
        if (mario->mFluddUsageState <= 1 && readNozzlePressure(mario->mFludd) > 0.01f)
            return true;
        return false;
    }();

    const bool hasTankWater = mario->mFludd && mario->mFludd->mCurrentWater > 0;
    if (sprayAttempt && hasTankWater)
        vfx |= VFX_WATER_SPRAY;
    else if (sprayAttempt && !hasTankWater)
        vfx |= VFX_FLUDD_EMPTY;

    if (mario->mFludd) {
        if (readFluddSwitchSpeed(mario->mFludd) != 0.0f)
            vfx |= VFX_NOZZLE_SWITCHING;

        switch (mario->mFludd->mCurrentNozzle) {
        case TWaterGun::Hover:
            vfx |= VFX_HOVER;
            break;
        case TWaterGun::Rocket:
            vfx |= VFX_ROCKET;
            break;
        case TWaterGun::Turbo:
            if (sprayAttempt)
                vfx |= VFX_TURBO;
            break;
        default:
            break;
        }
    }

    if (mario->mState == TMario::STATE_DEATH || mario->mAttributes.mIsGameOver)
        vfx |= VFX_DEAD;

    // Hide & seek: death recovery can clamp game-over before UDP snapshots reach the server.
    if ((vfx & VFX_DEAD) == 0 && shouldForceHideSeekDeadSnapshot())
        vfx |= VFX_DEAD;

    if (hostUsesWetSlideParticles(mario))
        vfx |= VFX_WET_SLIDE;

    if (!shouldShowFluddPackOnMario(mario))
        vfx |= VFX_NO_FLUDD;

    if (yCamActive) {
        vfx |= VFX_Y_CAM;
        s16 pitch = getLButtonCameraPitch();
        if (pitch < kSnapshotAngleMin)
            pitch = kSnapshotAngleMin;
        if (pitch > kSnapshotAngleMax)
            pitch = kSnapshotAngleMax;
        vfx = packVfxAuxAngle(vfx, encodeSnapshotAngle6(pitch));
    }

    return vfx;
}

static u8 mapGameStateToDolphin(u8 gameState) {
    // During normal stage play, publish Active even if a blocked loading zone left
    // mGameState stuck in WARPING — keeps bridge remote sync alive (SMSO visibility).
    if (gpMarDirector && gpMarDirector->mCurState == TMarDirector::STATE_NORMAL)
        return DS_ACTIVE;

    switch (gameState) {
    case 0x00:
        return DS_LOADING;
    case 0x09:
        return DS_WARPING;
    case 0x04:
    case 0x03:
        return DS_ACTIVE;
    default:
        return DS_BOOTING;
    }
}

void initPuppets() {
    publishMailboxAnchor();
}

// doldecomp: ANIM_TURN keeps mModelFaceAngle at pre-turn root; ANIM_TRNED / turnEnd()
// updates mFaceAngle first, then adds 0x8000 to mModelFaceAngle. Export face yaw during
// trned and whenever face leads model so remotes don't snap back after standing 180s.
//
// Side flip chain (doldecomp MarioRun.cpp / MarioWait.cpp):
//   uTurnJumping      (0x887, ANIM_TJMP1)  -> mModelFaceAngle += 0x8000
//   uTurnJumpSlip     (0x04000473, ANIM_TJMP2) -> same
//   uTurnJumpEnd      (0x0C000233, ANIM_TJMP2) -> same
// turnEnd() during ANIM_TRNED also adds 0x8000 to the model root.
constexpr u32 kStateSideFlipAir = 0x00000887u;
constexpr u32 kStateSideFlipSlip = 0x04000473u;
constexpr u32 kStateSideFlipEnd = 0x0C000233u;
constexpr u16 kAnimSideFlipAir = 0xBF;  // ANIM_TJMP1
constexpr u16 kAnimSideFlipLand = 0xBE; // ANIM_TJMP2
constexpr s16 kModelFaceHalfTurn = 0x8000;

static bool isSideFlipSequenceAnim(u16 animId) {
    return animId == kAnimSideFlipAir || animId == kAnimSideFlipLand;
}

static bool hasHalfTurnModelFaceOffset(s16 faceY, s16 modelY) {
    const s16 diff = static_cast<s16>(faceY - modelY);
    return diff == static_cast<s16>(-kModelFaceHalfTurn) ||
           diff == static_cast<s16>(kModelFaceHalfTurn);
}

static bool usesSideFlipModelOffset(u32 state, u16 animId) {
    if (state == kStateSideFlipAir || state == kStateSideFlipSlip || state == kStateSideFlipEnd)
        return true;
    if (!isSideFlipSequenceAnim(animId))
        return false;
    if (state & TMario::STATE_AIRBORN)
        return true;
    return state == kStateSideFlipSlip || state == kStateSideFlipEnd;
}

static s16 exportSnapshotYaw(TMario *mario) {
    constexpr u16 kAnimTurn = 0xBC;
    constexpr u16 kAnimTurnEnd = 0xBD;

    const u16 anim = mario->mAnimationID;
    if (usesSideFlipModelOffset(mario->mState, anim))
        return mario->mModelAngleY;
    if (anim == kAnimTurn)
        return mario->mModelAngleY;
    if (anim == kAnimTurnEnd)
        return mario->mAngle.y;
    if (anim == 0xF4) {
        const u32 id = mario->mState & 0x1FFu;
        if (id == 0x041u || id == 0x042u || id == 0x095u || id == 0x096u)
            return mario->mModelAngleY;
    }
    if (hasHalfTurnModelFaceOffset(mario->mAngle.y, mario->mModelAngleY))
        return mario->mModelAngleY;

    s16 yaw = mario->mModelAngleY;
    const s16 diff = static_cast<s16>(mario->mAngle.y - yaw);
    if (diff > 0x4000 || diff < -0x4000)
        yaw = mario->mAngle.y;
    return yaw;
}

static void detachLocalSurfGessoIfDying(TMario *mario) {
    if (!mario)
        return;

    if (mario->mState == TMario::STATE_DEATH || mario->mAttributes.mIsGameOver ||
        mario->mHealth <= 0) {
        mario->mSurfGesso = nullptr;
        mario->mSurfGessoID = 0;
    }
}

void exportLocalPlayer(TMario *mario, TMarDirector *director) {
    if (!mario || !director)
        return;

    detachLocalSurfGessoIfDying(mario);

    publishMailboxAnchor();
    CommBuffer *buf = getCommBuffer();

    PlayerSnapshot &snap = buf->localSnapshot;
    snap.position.x = mario->mTranslation.x;
    snap.position.y = mario->mTranslation.y;
    snap.position.z = mario->mTranslation.z;
    snap.velocity.x = mario->mSpeed.x;
    snap.velocity.y = mario->mSpeed.y;
    snap.velocity.z = mario->mSpeed.z;
    snap.rotationY = static_cast<f32>(exportSnapshotYaw(mario));
    snap.animId = mario->mAnimationID;
    f32 frame = mario->getCurrentFrame(0) * 256.0f;
    if (frame < 0.0f)
        frame = 0.0f;
    if (frame > 65535.0f)
        frame = 65535.0f;
    snap.animFrame = static_cast<u16>(frame + 0.5f);
    u8 rateEnc = 0;
    u8 upperEnc = 0;
    u8 tankEnc = 0;
    if (mario->mModelData && mario->mModelData->mFrameCtrl) {
        const f32 rate = mario->mModelData->mFrameCtrl[0].mFrameRate;
        f32 upperFrame = mario->getCurrentFrame(1);
        if (upperFrame < 0.0f)
            upperFrame = 0.0f;
        rateEnc = static_cast<u8>(rate * 64.0f > 255.0f ? 255 : rate * 64.0f);
        upperEnc = static_cast<u8>(upperFrame * 8.0f > 255.0f ? 255 : upperFrame * 8.0f);
    }
    if (mario->mFludd && mario->mAttributes.mHasFludd) {
        snap.nozzleId = packNozzleIds(mario->mFludd->mCurrentNozzle, mario->mFludd->mSecondNozzle);
        const s32 tank = mario->mFludd->mCurrentWater;
        tankEnc = static_cast<u8>(tank < 0 ? 0 : (tank > 255 ? 255 : tank));
    } else {
        snap.nozzleId = 0;
    }
    snap.health = packAnimAux(exportHandIndex(mario),
                              mario->mFludd ? readFluddDeploy(mario->mFludd) : 0.0f);
    snap.stageId = director->mAreaID;
    // Raw in-game scenario index; launcher server normalizes Delfino Plaza for roster display.
    snap.episodeId = director->mEpisodeID;
    // Offset 0x380 is named mFluddUsageState in the BSE header, but doldecomp
    // identifies it as Mario's upper-body state. Sync it so remote FLUDD
    // holding/pumping posture matches the local player.
    // Low 3 bits: upper-body state; high 5 bits: FLUDD X-switch progress (0..1).
    const f32 switchProg =
        mario->mFludd ? readFluddSwitchProgress(mario->mFludd) : 0.0f;
    const f32 switchSpeed =
        mario->mFludd ? readFluddSwitchSpeed(mario->mFludd) : 0.0f;
    snap.movementState = packMovementState(static_cast<u8>(mario->mFluddUsageState),
                                           switchProg, switchSpeed);
    const u32 marioState = mario->mState;
    snap.actionId = static_cast<u16>(marioState);
    snap.actionIdHi = static_cast<u16>(marioState >> 16);
    snap.vfxFlags = buildVfxFlags(mario);

    exportYoshiSnapshotFields(mario, snap);

    const bool yCam = (snap.vfxFlags & VFX_Y_CAM) != 0;
    const bool pumpHold = mario->mFluddUsageState <= 1;
    const bool sprayingWater = (snap.vfxFlags & VFX_WATER_SPRAY) != 0;
    const bool drySpray = (snap.vfxFlags & VFX_FLUDD_EMPTY) != 0;
    f32 sprayPressure = 0.0f;
    if (sprayingWater && mario->mFludd) {
        TNozzleBase *nozzle = mario->mFludd->mNozzleList[mario->mFludd->mCurrentNozzle];
        if (nozzle) {
            const u8 nozzleType = mario->mFludd->mCurrentNozzle;
            if (nozzleType == TWaterGun::Hover || nozzleType == TWaterGun::Rocket ||
                nozzleType == TWaterGun::Turbo) {
                const auto *trigger = static_cast<const TNozzleTrigger *>(nozzle);
                const f32 maxPressure = trigger->mEmitParams.mInsidePressureMax.get();
                if (maxPressure > 0.0f)
                    sprayPressure = trigger->mTriggerFill / maxPressure;
            } else {
                sprayPressure = nozzle->_378;
            }
        }
    }
    const bool blooperSurf = isLocalBlooperSurf(mario);
    const bool waistPack = !yCam && (mario->mAnimationID == 0x48 || mario->mAnimationID == 0x72 ||
                                     mario->mAnimationID == 0x6D || blooperSurf);
    const bool running = waistPack && (mario->mAnimationID == 0x48 || mario->mAnimationID == 0x72);
    u8 highEnc = upperEnc;
    if (yCam) {
        highEnc = encodeSnapshotAngle(mario->_100);
    } else if (waistPack) {
        highEnc = encodeSnapshotAngle(static_cast<s16>(mario->_3DC));
    }
    snap.pingMs = static_cast<u16>(rateEnc) | (static_cast<u16>(highEnc) << 8);

    if (snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags) && sprayingWater)
        snap.pingMs = static_cast<u16>(rateEnc) |
                      (static_cast<u16>(encodeSprayPressure(sprayPressure)) << 8);

    // water: tank level by default; Y-cam and hold-pump reuse it for upper BCK frame;
    // while spraying water it carries synced nozzle pressure (0..1 -> 0..255);
    // dry spray reuses the byte as an explicit empty-tank marker (0);
    // blooper surf reuses it for mSurfGessoID (purple/yellow/green);
    // host on Yoshi reuses it for TYoshi BCK index (set in exportYoshiSnapshotFields).
    if (snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags)) {
        // snap.water already carries host Yoshi BCK.
    } else if (yCam || (pumpHold && !sprayingWater && !drySpray))
        snap.water = upperEnc;
    else if (sprayingWater)
        snap.water = encodeSprayPressure(sprayPressure);
    else if (drySpray)
        snap.water = 0;
    else if (isLocalBlooperSurf(mario))
        snap.water = mario->mSurfGessoID & 0x03u;
    else
        snap.water = tankEnc;

    if (waistPack) {
        const u8 rollEnc = encodeSnapshotAngle6(static_cast<s16>(mario->_3D8));
        snap.vfxFlags = packVfxAuxAngle(snap.vfxFlags, rollEnc);
    }

    // Aux-bit priority: Y-cam pitch (set in buildVfxFlags) > active-spray gun angle > waist roll.
    // During spray the retail waist callback's FLUDD branch overrides the run branch, so the gun
    // angle is what matters; mGunAngle is negative while hovering (head aims down) and positive
    // while aiming up. Pack it raw — the sign convention matches retail (no inversion).
    if (!yCam && (sprayingWater || drySpray)) {
        snap.vfxFlags =
            packVfxAuxAngle(snap.vfxFlags, encodeSnapshotAngle6(readHostGunAngle(mario)));
    }

    snap.connected = 1;
    snap.slot = buf->localSlot;

    if (buf->localPlayerName[0] != '\0') {
        memset(snap.name, 0, MAX_PLAYER_NAME);
        int copyLen = 0;
        while (copyLen < static_cast<int>(MAX_PLAYER_NAME) && buf->localPlayerName[copyLen] != '\0')
            ++copyLen;
        memcpy(snap.name, buf->localPlayerName, static_cast<size_t>(copyLen));
    }

    buf->dolphinState = mapGameStateToDolphin(static_cast<u8>(director->mGameState));
}

// doldecomp TMarDirector::setMario unkD1: 1=rollingStart, 2=returnStart, 3=waitingStart, 4=torocco.
constexpr u32 kDirectorMarioEntryKindOffset = 0xD1u;
constexpr u32 kDirectorEntryFlagsOffset = 0x50u;
constexpr u8 kMarioEntryWaitingStart = 3u;
constexpr u16 kDirectorMarioReadyFlag = 1u;
constexpr u32 kMarioStatusTypeMask = 0x1C0u;
constexpr u32 kMarioStatusTypeDemo = 0x100u;
constexpr u32 kMarioStatusWait = 0xC400201u;

static void overrideDirectorEntryKind(TMarDirector *director) {
    if (!director)
        return;
    reinterpret_cast<u8 *>(director)[kDirectorMarioEntryKindOffset] = kMarioEntryWaitingStart;
}

static bool isMarioInDemoState(const TMario *mario) {
    return mario && (mario->mState & kMarioStatusTypeMask) == kMarioStatusTypeDemo;
}

static void forceInstantMarioSpawn(TMarDirector *director, TMario *mario) {
    if (!director || !mario)
        return;

    overrideDirectorEntryKind(director);

    u8 *raw = reinterpret_cast<u8 *>(director);
    u16 &entryFlags = *reinterpret_cast<u16 *>(raw + kDirectorEntryFlagsOffset);
    if (!(entryFlags & kDirectorMarioReadyFlag))
        startStageBGM__10MSMainProcFUcUc(director->mAreaID, director->mEpisodeID);

    director->setMario();

    if (isMarioInDemoState(mario))
        mario->waitingStart(nullptr, 0.0f);

    if (isMarioInDemoState(mario))
        mario->changePlayerStatus(kMarioStatusWait, 0, true);

    entryFlags |= kDirectorMarioReadyFlag;
}

void respawnLocalMarioAtStageSpawn(TMarDirector *director, TMario *mario) {
    forceInstantMarioSpawn(director, mario);
}

void reloadLocalStage(TMarDirector *director, u8 areaId, u8 episodeId) {
    if (!director)
        return;

    const u16 stageId = static_cast<u16>(((static_cast<u32>(areaId) + 1) << 8) | episodeId);

    gpApplication.mNextScene.mAreaID = areaId;
    gpApplication.mNextScene.mEpisodeID = episodeId;

    TFlagManager::smInstance->setFlag(0x40002, 0);
    TFlagManager::smInstance->setFlag(0x40003, episodeId);

    BetterSMS::Loading::setLoading(false);
    setHideSeekAllowDeathStageReload(true);
    director->setNextStage(stageId, nullptr);
}

void consumeWarpIntent() {
    CommBuffer *buf = getCommBuffer();
    if (!(buf->bridgeFlags & BF_WARP_PENDING))
        return;
    if (!gpMarDirector)
        return;

    const u8 target = buf->warpTargetSlot;
    const bool warpAll = (buf->bridgeFlags & BF_WARP_ALL) != 0;

    if (warpAll) {
        if (target != WARP_ALL_SLOTS)
            return;
    } else if (target == WARP_NO_TARGET || target != buf->localSlot) {
        return;
    }

    const ResolvedWarp resolved = resolveFullWarp(buf->warpCourseId, buf->warpEpisodeId);
    const u8 areaId = resolved.areaId;
    const WarpTarget warpTarget = resolved.target;
    const u16 stageId =
        static_cast<u16>(((static_cast<u32>(areaId) + 1) << 8) | warpTarget.loadScenario);

    OSReport("[SMSO] Warp -> area=%u load=%u mission=%u stage=0x%04X slot=%u\n", areaId,
             warpTarget.loadScenario, warpTarget.missionEpisode, stageId, buf->localSlot);

    gpApplication.mNextScene.mAreaID = areaId;
    gpApplication.mNextScene.mEpisodeID = warpTarget.loadScenario;

    TFlagManager::smInstance->setFlag(0x40002, 0);
    TFlagManager::smInstance->setFlag(0x40003, warpTarget.missionEpisode);

    if (areaId == kSirenaHotelAreaId && warpTarget.loadScenario != warpTarget.missionEpisode)
        s_hotelMissionEpisodeSync = warpTarget.missionEpisode;
    else
        s_hotelMissionEpisodeSync = kInvalidHotelMission;

    BetterSMS::Loading::setLoading(false);

    setHideSeekAllowStageTransition(true);
    authorizeLauncherStageMove();
    gpMarDirector->setNextStage(stageId, nullptr);

    buf->bridgeFlags &= ~static_cast<u32>(BF_WARP_PENDING | BF_WARP_ALL);
    buf->warpTargetSlot = WARP_NO_TARGET;
}

void applyHotelWarpMissionOverride(TMarDirector *director) {
    if (!director || s_hotelMissionEpisodeSync == kInvalidHotelMission)
        return;
    if (director->mAreaID != kSirenaHotelAreaId)
        return;

    const u8 missionEpisode = s_hotelMissionEpisodeSync;

    // After TFlagManager::resetStage(). Mission scripts read mEpisodeID during setupObjects;
    // mCurrentScene.mEpisodeID stays on the load scenario so getStageName loads delfino0/2/4.
    director->mEpisodeID = missionEpisode;
    TFlagManager::smInstance->setFlag(0x40003, missionEpisode);
    s_hotelWarpDemoSkipFrames = 120;
    s_hotelMissionEpisodeSync = kInvalidHotelMission;

    OSReport("[SMSO] Hotel mission override load=%u mission=%u\n",
             gpApplication.mCurrentScene.mEpisodeID, missionEpisode);
}

void syncHotelWarpMissionEpisode(TMarDirector *director) {
    if (!director || s_hotelWarpDemoSkipFrames == 0)
        return;
    if (director->mAreaID != kSirenaHotelAreaId)
        return;
    if (!gpMarioAddress)
        return;

    if (isMarioInDemoState(gpMarioAddress))
        forceInstantMarioSpawn(director, gpMarioAddress);

    if (!isMarioInDemoState(gpMarioAddress) || s_hotelWarpDemoSkipFrames == 1)
        s_hotelWarpDemoSkipFrames = 0;
    else
        --s_hotelWarpDemoSkipFrames;
}

void applyPendingWarpPoint(TMarDirector *director) {
    CommBuffer *buf = getCommBuffer();
    if (!(buf->bridgeFlags & BF_WARP_TO_POINT))
        return;
    if (!gpMarioAddress || !director)
        return;

    if (director->mCurState < TMarDirector::STATE_NORMAL)
        return;

    const ResolvedWarp resolved = resolveFullWarp(buf->warpCourseId, buf->warpEpisodeId);
    const u8 expectArea = resolved.areaId;
    const u8 expectMission = resolved.target.missionEpisode;
    u8 actualMission = director->mEpisodeID;
    if (expectArea == kSirenaHotelAreaId)
        actualMission = static_cast<u8>(TFlagManager::smInstance->getFlag(0x40003));
    if (director->mAreaID != expectArea || actualMission != expectMission)
        return;

    if (isMarioInDemoState(gpMarioAddress))
        return;

    TVec3f pos;
    pos.x = buf->warpPosX;
    pos.y = buf->warpPosY;
    pos.z = buf->warpPosZ;

    gpMarioAddress->warpRequest(pos, buf->warpFacingY);
    buf->bridgeFlags &= ~static_cast<u32>(BF_WARP_TO_POINT);

    OSReport("[SMSO] Teleport -> (%.1f, %.1f, %.1f) facing=%.1f area=%u episode=%u\n", pos.x,
             pos.y, pos.z, buf->warpFacingY, director->mAreaID, director->mEpisodeID);
}

void updatePuppets(TMarDirector *director) {
    (void)director;
    CommBuffer *buf = getCommBuffer();
    u8 count = 0;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (buf->remoteSnapshots[i].connected)
            ++count;
    }
    buf->playerCount = count;
}

void clearPuppets() {
    CommBuffer *buf = getCommBuffer();
    memset(buf->remoteSnapshots, 0, sizeof(buf->remoteSnapshots));
    buf->playerCount = 0;
}

} // namespace smso
