#include "yoshi_sync.hpp"

#include "collectible_scan.hpp"
#include "remote_water_sync.hpp"
#include "world_sync.hpp"
#include <SMS/MapObj/MapObjBase.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/NozzleBase.hxx>
#include <SMS/Player/Yoshi.hxx>
#include <SMS/Strategic/HitActor.hxx>
#include <SMS/Strategic/LiveActor.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>
#include <SMS/raw_fn.hxx>

#include <JSystem/J3D/J3DModel.hxx>
#include <JSystem/JDrama/JDRGraphics.hxx>
#include <Dolphin/MTX.h>
#include <Dolphin/types.h>
#include <JSystem/JUtility/JUTColor.hxx>

extern JUtility::TColor bodyColor[4];
extern TMarDirector *gpMarDirector;

namespace {

constexpr u8 kYoshiNozzleId = static_cast<u8>(TWaterGun::Yoshi);
constexpr u8 kYoshiJuiceScale = 31;
constexpr int kYoshiRideAnimId = 0x16;
constexpr int kYoshiMirrorJointA = 37;
constexpr int kYoshiMirrorJointB = 32;
// doldecomp TYoshi @ 0x3C — tongue joint index for getTongueMtx().
constexpr u32 kYoshiJointIdxTongueOffset = 0x3C;
constexpr u32 kYoshiTonguePtrOffset = 0x38;
constexpr u32 kTongueStateOffset = 0x7C;
constexpr u32 kTongueProgressOffset = 0x7E;
constexpr u32 kTongueActorTypeInMouthOffset = 0xD0;
constexpr u32 kTongueHeadPosOffset = 0xA0;
constexpr u32 kTongueHeadDirOffset = 0xAC;
constexpr u32 kTongueTipPosOffset = 0xB8;
constexpr f32 kYoshiFruitMatchRadiusSq = 450.0f * 450.0f;

using YoshiTongueCalcAnimFn = void (*)(void *, Mtx *);
using YoshiTongueViewCalcFn = void (*)(void *);
using YoshiTongueEntryFn = void (*)(void *);
using YoshiThinkUpperFn = void (*)(TYoshi *);
using YoshiDoEatFn = void (*)(TYoshi *, u32);

static YoshiTongueCalcAnimFn sYoshiTongueCalcAnim =
    reinterpret_cast<YoshiTongueCalcAnimFn>(SMS_PORT_REGION(0x8026764C, 0x8025F3D8, 0, 0));
static YoshiTongueViewCalcFn sYoshiTongueViewCalc =
    reinterpret_cast<YoshiTongueViewCalcFn>(SMS_PORT_REGION(0x802675F0, 0x8025F37C, 0, 0));
static YoshiTongueEntryFn sYoshiTongueEntry =
    reinterpret_cast<YoshiTongueEntryFn>(SMS_PORT_REGION(0x80267594, 0x8025F320, 0, 0));
static YoshiThinkUpperFn sYoshiThinkUpper =
    reinterpret_cast<YoshiThinkUpperFn>(SMS_PORT_REGION(0x8026FC90, 0x80267A1C, 0, 0));
static YoshiDoEatFn sYoshiDoEat =
    reinterpret_cast<YoshiDoEatFn>(SMS_PORT_REGION(0x8026F60C, 0x80267398, 0, 0));

static void *remoteYoshiTongue(TYoshi *yoshi) {
    if (!yoshi)
        return nullptr;
    return *reinterpret_cast<void **>(reinterpret_cast<u8 *>(yoshi) + kYoshiTonguePtrOffset);
}

static u16 remoteYoshiTongueJointIndex(const TYoshi *yoshi) {
    return *reinterpret_cast<const u16 *>(reinterpret_cast<const u8 *>(yoshi) +
                                          kYoshiJointIdxTongueOffset);
}

static u16 *remoteYoshiTongueStatePtr(void *tongue) {
    return reinterpret_cast<u16 *>(reinterpret_cast<u8 *>(tongue) + kTongueStateOffset);
}

static u16 *remoteYoshiTongueProgressPtr(void *tongue) {
    return reinterpret_cast<u16 *>(reinterpret_cast<u8 *>(tongue) + kTongueProgressOffset);
}

static u32 *remoteYoshiTongueActorTypeInMouthPtr(void *tongue) {
    return reinterpret_cast<u32 *>(reinterpret_cast<u8 *>(tongue) + kTongueActorTypeInMouthOffset);
}

static Vec *remoteYoshiTongueTipPos(void *tongue) {
    return reinterpret_cast<Vec *>(reinterpret_cast<u8 *>(tongue) + kTongueTipPosOffset);
}

static Vec *remoteYoshiTongueHeadPos(void *tongue) {
    return reinterpret_cast<Vec *>(reinterpret_cast<u8 *>(tongue) + kTongueHeadPosOffset);
}

static bool isYoshiFruitActorType(u32 actorType) {
    return actorType >= 0x40000390u && actorType <= 0x40000396u;
}

static u8 encodeYoshiFruitActorType(u32 actorType) {
    switch (actorType) {
    case 0x40000390:
        return 1;
    case 0x40000391:
        return 2;
    case 0x40000392:
        return 3;
    case 0x40000393:
        return 4;
    case 0x40000394:
        return 5;
    case 0x40000395:
        return 6;
    case 0x40000396:
        return 7;
    default:
        return 0;
    }
}

static u32 decodeYoshiFruitActorType(u8 enc) {
    switch (enc) {
    case 1:
        return 0x40000390;
    case 2:
        return 0x40000391;
    case 3:
        return 0x40000392;
    case 4:
        return 0x40000393;
    case 5:
        return 0x40000394;
    case 6:
        return 0x40000395;
    case 7:
        return 0x40000396;
    default:
        return 0;
    }
}

static u32 localFruitEatEventId = 0;
static u8 localPublishedMouthEnc = 0;

static bool isYoshiEatBck(u8 bckId) {
    return bckId == 10 || bckId == 12 || bckId == 15;
}

static u8 snapshotMouthFruitEnc(const smso::PlayerSnapshot &snap) {
    if ((snap.vfxFlags & smso::VFX_YOSHI_FRUIT_MOUTH) == 0)
        return 0;
    const u8 enc = snap.episodeId;
    return enc >= 1 && enc <= 7 ? enc : 0;
}

static void publishLocalYoshiFruitTaken(u8 actorTypeEnc, const Vec &tipPos, u8 slot) {
    if (actorTypeEnc == 0)
        return;

    const u32 packedPos = smso::packCollectibleWorldPos(tipPos.x, tipPos.y, tipPos.z);
    const u32 eventId = (static_cast<u32>(actorTypeEnc) << 24) | (packedPos & 0x00FFFFFFu);
    if (eventId == localFruitEatEventId)
        return;
    localFruitEatEventId = eventId;

    smso::CommBuffer *buf = smso::getCommBuffer();
    const u8 courseId = buf && gpMarDirector ? gpMarDirector->mAreaID : 0;
    const u8 episodeId = buf && gpMarDirector ? gpMarDirector->mEpisodeID : 0;
    smso::enqueueLocalWorldEvent(static_cast<u8>(smso::WE_YOSHI_FRUIT_TAKEN), courseId, episodeId,
                                 actorTypeEnc, slot, packedPos);
}

static void performRemoteYoshiTongueDraw(void *tongue) {
    if (!tongue || !sYoshiTongueViewCalc || !sYoshiTongueEntry)
        return;

    const u16 state = *remoteYoshiTongueStatePtr(tongue);
    if (state == 0)
        return;

    sYoshiTongueViewCalc(tongue);
    sYoshiTongueEntry(tongue);
}

static void applyRemoteYoshiTongueTipFromOffset(TMario *body, void *tongue, const smso::Vec3 &offset) {
    if (!body || !tongue)
        return;

    Vec *tip = remoteYoshiTongueTipPos(tongue);
    Vec *head = remoteYoshiTongueHeadPos(tongue);
    tip->x = body->mTranslation.x + offset.x;
    tip->y = body->mTranslation.y + offset.y;
    tip->z = body->mTranslation.z + offset.z;
    head->x = body->mTranslation.x;
    head->y = body->mTranslation.y;
    head->z = body->mTranslation.z;
}

static void calcRemoteYoshiTongueAnim(TYoshi *yoshi, J3DModel *model) {
    void *tongue = remoteYoshiTongue(yoshi);
    if (!tongue || !model || !model->mJointArray || !sYoshiTongueCalcAnim)
        return;

    const u16 joint = remoteYoshiTongueJointIndex(yoshi);
    Mtx tongueBase;
    MTXCopy(model->mJointArray[joint], tongueBase);
    sYoshiTongueCalcAnim(tongue, &tongueBase);
}

// doldecomp J3DModelData / J3DShape layout (BSE headers omit shape-node helpers).
struct YoshiJ3DShape {
    u32 _0;
    u16 _4;
    u16 _6;
    u32 mFlags;

    void onFlag(u32 flag) { mFlags |= flag; }
    void offFlag(u32 flag) { mFlags &= ~flag; }
};

struct YoshiJ3DModelData {
    u8 _pad[0x24];
    u16 mMaterialNum;
    u16 _padMat;
    void **mMaterials;
    u16 mShapeNum;
    u16 _padShape;
    YoshiJ3DShape **mShapeNodePointer;
};

static u8 clampYoshiType(u8 type) {
    if (type > TYoshi::PINK)
        return TYoshi::GREEN;
    return type;
}

static u8 packYoshiJuiceMovement(u8 upperState, s32 curJuice, s32 maxJuice) {
    f32 ratio = 1.0f;
    if (maxJuice > 0)
        ratio = static_cast<f32>(curJuice) / static_cast<f32>(maxJuice);
    if (ratio < 0.0f)
        ratio = 0.0f;
    if (ratio > 1.0f)
        ratio = 1.0f;

    u8 juiceEnc = static_cast<u8>(ratio * static_cast<f32>(kYoshiJuiceScale));
    if (juiceEnc > kYoshiJuiceScale)
        juiceEnc = kYoshiJuiceScale;
    return static_cast<u8>((juiceEnc << 3) | (upperState & 0x07));
}

static f32 unpackYoshiJuiceRatio(u8 packedMovement) {
    const u8 juiceEnc = static_cast<u8>((packedMovement >> 3) & 0x1F);
    return static_cast<f32>(juiceEnc) / static_cast<f32>(kYoshiJuiceScale);
}

static J3DModel **yoshiMirrorModels(TYoshi *yoshi) {
    return reinterpret_cast<J3DModel **>(reinterpret_cast<u8 *>(yoshi) + 0x44);
}

static YoshiJ3DModelData *yoshiModelShapeData(J3DModelData *data) {
    return reinterpret_cast<YoshiJ3DModelData *>(data);
}

static void onFlag1OnAllShapes(J3DModelData *data) {
    YoshiJ3DModelData *shapeData = yoshiModelShapeData(data);
    if (!shapeData || !shapeData->mShapeNodePointer)
        return;

    for (u16 i = 0; i < shapeData->mShapeNum; ++i) {
        if (shapeData->mShapeNodePointer[i])
            shapeData->mShapeNodePointer[i]->onFlag(1);
    }
}

static void offFlag1OnAllShapes(J3DModelData *data) {
    YoshiJ3DModelData *shapeData = yoshiModelShapeData(data);
    if (!shapeData || !shapeData->mShapeNodePointer)
        return;

    for (u16 i = 0; i < shapeData->mShapeNum; ++i) {
        if (shapeData->mShapeNodePointer[i])
            shapeData->mShapeNodePointer[i]->offFlag(1);
    }
}

static void applyYoshiColor(TYoshi *yoshi, u8 type) {
    const u8 clamped = clampYoshiType(type);
    yoshi->mType = static_cast<s8>(clamped);

    const JUtility::TColor &color = bodyColor[clamped];
    // doldecomp TYoshi::init stores 0..255 in unk84; entry() casts to s16 for tev color.
    yoshi->mRedComponent = static_cast<f32>(color.r);
    yoshi->mGreenComponent = static_cast<f32>(color.g);
    yoshi->mBlueComponent = static_cast<f32>(color.b);
    yoshi->thinkBtp(4);
}

static void ensureRemoteYoshiDrawable(TYoshi *yoshi) {
    if (!yoshi)
        return;

    // doldecomp TYoshi::entry skips draw when juice timer fails low-bit checks.
    if (yoshi->mCurJuice < 600)
        yoshi->mCurJuice = yoshi->mMaxJuice > 0 ? yoshi->mMaxJuice : 21300;
    if ((yoshi->mCurJuice & 0x8) == 0 && yoshi->mCurJuice < 360)
        yoshi->mCurJuice |= 0x8;
}

static void resetRemoteYoshiToEgg(TYoshi *yoshi, RemoteYoshiSlot &slot) {
    if (!yoshi)
        return;

    yoshi->mState = TYoshi::EGG;
    yoshi->mMario = nullptr;
    slot.mounted = false;
    slot.hatched = false;
    slot.type = 0xFF;
    slot.hostSpraying = false;
    slot.sprayPressureEnc = 0;
    slot.lastTongueState = 0;
    slot.lastMouthActorEnc = 0;
    slot.lastYoshiBck = 0;
    slot.lastFruitEatEventId = 0;
}

static void stagePuppetFluddForYoshiThinkUpper(TMario *body, const RemoteYoshiSlot *slot) {
    if (!body || !body->mFludd || !slot)
        return;

    TWaterGun *fludd = body->mFludd;
    fludd->mCurrentNozzle = TWaterGun::Yoshi;

    TNozzleBase *nozzle = fludd->mNozzleList[TWaterGun::Yoshi];
    if (!nozzle)
        return;

    fludd->mCurrentWater = nozzle->mEmitParams.mAmountMax.get();
    if (slot->hostSpraying) {
        const f32 pressure =
            slot->sprayPressureEnc > 0 ? static_cast<f32>(slot->sprayPressureEnc) / 255.0f : 0.0f;
        nozzle->_378 = pressure;
    } else {
        // doldecomp TYoshi::thinkUpper — unk378 <= 0 exits spray mouth BCK back to idle.
        nozzle->_378 = 0.0f;
    }
}

struct PuppetFluddThinkUpperState {
    u8 nozzle;
    s32 water;
    f32 pressure;
    bool valid;
};

static PuppetFluddThinkUpperState stashPuppetFluddForThinkUpper(TMario *body) {
    PuppetFluddThinkUpperState saved = {};
    if (!body || !body->mFludd)
        return saved;

    TWaterGun *fludd = body->mFludd;
    saved.valid = true;
    saved.nozzle = fludd->mCurrentNozzle;
    saved.water = fludd->mCurrentWater;
    TNozzleBase *nozzle = fludd->mNozzleList[TWaterGun::Yoshi];
    saved.pressure = nozzle ? nozzle->_378 : 0.0f;
    return saved;
}

static void restorePuppetFluddAfterThinkUpper(TMario *body, const PuppetFluddThinkUpperState &saved) {
    if (!saved.valid || !body || !body->mFludd)
        return;

    TWaterGun *fludd = body->mFludd;
    fludd->mCurrentNozzle = saved.nozzle;
    fludd->mCurrentWater = saved.water;
    TNozzleBase *nozzle = fludd->mNozzleList[TWaterGun::Yoshi];
    if (nozzle)
        nozzle->_378 = saved.pressure;
}

static void applyRemoteYoshiJuice(TYoshi *yoshi, u8 packedMovement) {
    if (yoshi->mMaxJuice <= 0)
        yoshi->mMaxJuice = 21300;

    const f32 ratio = unpackYoshiJuiceRatio(packedMovement);
    yoshi->mCurJuice = static_cast<s32>(ratio * static_cast<f32>(yoshi->mMaxJuice));
    if (yoshi->mCurJuice < 0)
        yoshi->mCurJuice = 0;
    if (yoshi->mCurJuice == 0)
        yoshi->mCurJuice = yoshi->mMaxJuice;
}

static bool ensureRemoteYoshiStageReady(TYoshi *yoshi, RemoteYoshiSlot &slot) {
    if (!yoshi || !yoshi->mActor)
        return false;
    if (!slot.stageInitDone) {
        // doldecomp TYoshi::initInLoadAfter — tongue mirror + riding rig after stage load.
        yoshi->initInLoadAfter();
        slot.stageInitDone = true;
    }
    return true;
}

static void syncMountedYoshiTransform(TMario *body, TYoshi *yoshi) {
    yoshi->mTranslation.x = body->mTranslation.x;
    yoshi->mTranslation.y = body->mTranslation.y;
    yoshi->mTranslation.z = body->mTranslation.z;
}

static void applyRemoteYoshiAnim(TYoshi *yoshi, u8 bckId) {
    if (!yoshi || !yoshi->mActor)
        return;

    MActor *actor = yoshi->mActor;
    const int target = bckId > 0 ? static_cast<int>(bckId) : kYoshiRideAnimId;
    if (actor->getCurAnmIdx(MActor::BCK) == target)
        return;

    actor->setBckFromIndex(target);
    yoshi->thinkBtp(target);
}

static void applyMountedYoshiShapeDrawFlags(MActor *actor, J3DModel *mirror0, J3DModel *mirror1) {
    if (!actor || !actor->mModel || !actor->mModel->mModelData)
        return;

    YoshiJ3DModelData *mainShapes = yoshiModelShapeData(actor->mModel->mModelData);
    if (!mainShapes || mainShapes->mShapeNum < 2 || !mainShapes->mShapeNodePointer)
        return;

    const s32 bckId = actor->getCurAnmIdx(MActor::BCK);
    const bool useMainBodyShapes = bckId == 10 || bckId == 12 || bckId == 15;

    if (useMainBodyShapes) {
        mainShapes->mShapeNodePointer[0]->onFlag(1);
        mainShapes->mShapeNodePointer[1]->onFlag(1);
    } else {
        mainShapes->mShapeNodePointer[0]->offFlag(1);
        mainShapes->mShapeNodePointer[1]->offFlag(1);
        if (mirror0 && mirror0->mModelData)
            onFlag1OnAllShapes(mirror0->mModelData);
        if (mirror1 && mirror1->mModelData)
            onFlag1OnAllShapes(mirror1->mModelData);
    }
}

static bool remoteMountedYoshiDrawAllowed(const TYoshi *yoshi) {
    if (!yoshi)
        return false;

    // doldecomp TYoshi::entry draw gate uses unkC @ 0xC (BSE mCurJuice).
    const s32 juiceTimer = yoshi->mCurJuice;
    if (yoshi->mState == TYoshi::UNMOUNTED || yoshi->mState == TYoshi::MOUNTED) {
        if (juiceTimer >= 360 && juiceTimer < 600 && (juiceTimer & 0x10) == 0)
            return false;
        if (juiceTimer < 360 && (juiceTimer & 0x8) == 0)
            return false;
    }
    return true;
}

static void viewCalcRemoteMountedYoshi(TYoshi *yoshi) {
    if (!yoshi || !yoshi->mActor)
        return;

    yoshi->mActor->viewCalc();
    J3DModel **mirrors = yoshiMirrorModels(yoshi);
    if (mirrors[0])
        mirrors[0]->viewCalc();
    if (mirrors[1])
        mirrors[1]->viewCalc();

    performRemoteYoshiTongueDraw(remoteYoshiTongue(yoshi));
}

// MOUNTED subset of doldecomp TYoshi::calcAnim — skips thinkAnimation (gamepad) and
// mBodyAnmSound->animeLoop(). thinkUpper runs only while host sprays (mouth BCK).
static void calcRemoteYoshiMountedAnim(TMario *body, TYoshi *yoshi, const RemoteYoshiSlot *slot) {
    if (!body || !yoshi || !yoshi->mActor || !yoshi->mActor->mModel)
        return;

    MActor *actor = yoshi->mActor;
    J3DModel *model = actor->mModel;
    J3DModel *mirrors[2] = {yoshiMirrorModels(yoshi)[0], yoshiMirrorModels(yoshi)[1]};
    if (!mirrors[0] || !mirrors[1])
        return;

    if (slot) {
        const PuppetFluddThinkUpperState savedFludd = stashPuppetFluddForThinkUpper(body);
        stagePuppetFluddForYoshiThinkUpper(body, slot);
        if (sYoshiThinkUpper)
            sYoshiThinkUpper(yoshi);
        restorePuppetFluddAfterThinkUpper(body, savedFludd);
    }

    Mtx *taken = body->getTakenMtx();
    if (taken)
        MTXCopy(*taken, model->mBaseMtx);

    // doldecomp TYoshi::movement hatched path clears all shape draw flags first.
    if (model->mModelData)
        offFlag1OnAllShapes(model->mModelData);
    offFlag1OnAllShapes(mirrors[0]->mModelData);
    offFlag1OnAllShapes(mirrors[1]->mModelData);

    applyMountedYoshiShapeDrawFlags(actor, mirrors[0], mirrors[1]);
    actor->calcAnm();

    if (model->mJointArray) {
        MTXCopy(model->mJointArray[kYoshiMirrorJointA], mirrors[0]->mBaseMtx);
        MTXCopy(model->mJointArray[kYoshiMirrorJointB], mirrors[1]->mBaseMtx);
    }

    mirrors[0]->calc();
    mirrors[1]->calc();
    calcRemoteYoshiTongueAnim(yoshi, model);
}

// Mount without TYoshi::ride() side effects (fireRideYoshi, BGM percussion, voice).
static void mountRemoteYoshiPuppet(TMario *body, TYoshi *yoshi, RemoteYoshiSlot &slot, u8 type,
                                   u8 bckId) {
    if (!ensureRemoteYoshiStageReady(yoshi, slot))
        return;

    yoshi->mMario = body;
    applyYoshiColor(yoshi, type);

    if (yoshi->mState != TYoshi::MOUNTED) {
        yoshi->mState = TYoshi::MOUNTED;
        slot.hatched = true;
    }

    applyRemoteYoshiAnim(yoshi, bckId);
    slot.mounted = true;
    slot.type = type;
    syncMountedYoshiTransform(body, yoshi);
}

static void syncRemoteYoshiAnimFrame(TYoshi *yoshi, const smso::PlayerSnapshot &snap) {
    if (!yoshi || !yoshi->mActor)
        return;

    J3DFrameCtrl *frameCtrl = yoshi->mActor->getFrameCtrl(MActor::BCK);
    if (!frameCtrl)
        return;

    const f32 hostFrame = static_cast<f32>(snap.animFrame) / 256.0f;
    frameCtrl->mCurFrame = hostFrame;
}

} // namespace

namespace smso {

bool snapshotHostOnYoshi(u8 packedNozzle, u16 vfxFlags) {
    return (vfxFlags & VFX_NO_FLUDD) != 0 && unpackCurrentNozzle(packedNozzle) == kYoshiNozzleId;
}

bool remoteBodyRidingYoshi(const RemoteYoshiSlot &slot) {
    return slot.mounted;
}

bool remoteBodyRidingYoshi(const TMario *body) {
    return body && body->mYoshi && body->mYoshi->mState == TYoshi::MOUNTED;
}

void exportYoshiSnapshotFields(TMario *mario, PlayerSnapshot &snap) {
    if (!mario || !mario->onYoshi() || !mario->mYoshi)
        return;

    TYoshi *yoshi = mario->mYoshi;
    const u8 upper = static_cast<u8>(mario->mFluddUsageState) & 0x07;
    snap.movementState = packYoshiJuiceMovement(upper, yoshi->mCurJuice, yoshi->mMaxJuice);
    snap.nozzleId = packNozzleIds(kYoshiNozzleId, static_cast<u8>(yoshi->mType & 0x0F));

    if (yoshi->mActor)
        snap.water = static_cast<u8>(yoshi->mActor->getCurAnmIdx(MActor::BCK) & 0xFF);

    void *tongue = remoteYoshiTongue(yoshi);
    u16 tongueState = 0;
    u16 tongueProgress = 0;
    u32 mouthActorType = 0;
    if (tongue) {
        tongueState = *remoteYoshiTongueStatePtr(tongue);
        tongueProgress = *remoteYoshiTongueProgressPtr(tongue);
        mouthActorType = *remoteYoshiTongueActorTypeInMouthPtr(tongue);
    }

  const u8 hand = unpackAnimAuxHand(snap.health);
    snap.health = packYoshiTongueHealth(hand, tongueState, tongueProgress);

    if (yoshiTongueIsActive(static_cast<u8>(tongueState))) {
        snap.stageId = static_cast<u8>(tongueProgress > 255 ? 255 : tongueProgress);
        const Vec *tip = remoteYoshiTongueTipPos(tongue);
        snap.velocity.x = tip->x - mario->mTranslation.x;
        snap.velocity.y = tip->y - mario->mTranslation.y;
        snap.velocity.z = tip->z - mario->mTranslation.z;
    }

    const u8 mouthEnc = encodeYoshiFruitActorType(mouthActorType);
    if (mouthEnc != 0) {
        snap.episodeId = mouthEnc;
        snap.vfxFlags |= smso::VFX_YOSHI_FRUIT_MOUTH;
        const Vec *tip = remoteYoshiTongueTipPos(tongue);
        if (mouthEnc != localPublishedMouthEnc) {
            localPublishedMouthEnc = mouthEnc;
            smso::CommBuffer *buf = smso::getCommBuffer();
            publishLocalYoshiFruitTaken(mouthEnc, *tip, buf ? buf->localSlot : 0);
        }
    } else {
        snap.vfxFlags &= ~static_cast<u16>(smso::VFX_YOSHI_FRUIT_MOUTH);
        localPublishedMouthEnc = 0;
    }
}

static void applyRemoteYoshiTongueFromSnapshot(TMario *body, void *tongue, const PlayerSnapshot &snap) {
    if (!tongue)
        return;

    const u8 tongueState = unpackYoshiTongueState(snap.health);
    u16 *state = remoteYoshiTongueStatePtr(tongue);
    u16 *progress = remoteYoshiTongueProgressPtr(tongue);
    u32 *mouthType = remoteYoshiTongueActorTypeInMouthPtr(tongue);

    if (!yoshiTongueIsActive(tongueState)) {
        *state = 0;
        *progress = 0;
        *mouthType = 0;
        return;
    }

    *state = tongueState;
    *progress = snap.stageId != 0 ? snap.stageId : unpackYoshiTongueProgressCoarse(snap.health);
    if (*progress == 0 && tongueState != 0)
        *progress = 1;

    const u8 mouthEnc = snapshotMouthFruitEnc(snap);
    *mouthType = decodeYoshiFruitActorType(mouthEnc);
    applyRemoteYoshiTongueTipFromOffset(body, tongue, snap.velocity);
}

static void applyRemoteYoshiEatFromSnapshot(TYoshi *yoshi, RemoteYoshiSlot &slot,
                                            const PlayerSnapshot &snap) {
    const u8 mouthEnc = snapshotMouthFruitEnc(snap);
    const u8 bck = snap.water;

    if (isYoshiEatBck(bck) && !isYoshiEatBck(slot.lastYoshiBck)) {
        const u8 eatEnc = mouthEnc != 0 ? mouthEnc : slot.lastMouthActorEnc;
        const u32 actorType = decodeYoshiFruitActorType(eatEnc);
        if (actorType != 0 && sYoshiDoEat)
            sYoshiDoEat(yoshi, actorType);
    }

    slot.lastYoshiBck = bck;
    if (mouthEnc != 0)
        slot.lastMouthActorEnc = mouthEnc;
    else if (!isYoshiEatBck(bck) && unpackYoshiTongueState(snap.health) == 0)
        slot.lastMouthActorEnc = 0;
}

void syncRemoteYoshiFromSnapshot(TMario *body, RemoteYoshiSlot &slot, const PlayerSnapshot &snap) {
    TYoshi *yoshi = body ? body->mYoshi : nullptr;
    if (!yoshi)
        return;

    if (!snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags)) {
        if (slot.mounted || slot.hatched || yoshi->mState != TYoshi::EGG)
            resetRemoteYoshiToEgg(yoshi, slot);
        return;
    }

    const u8 yoshiType = clampYoshiType(unpackSecondNozzle(snap.nozzleId));
    applyRemoteYoshiJuice(yoshi, snap.movementState);
    mountRemoteYoshiPuppet(body, yoshi, slot, yoshiType, snap.water);
    syncRemoteYoshiAnimFrame(yoshi, snap);

    if (static_cast<u8>(yoshi->mType & 0x0F) != yoshiType)
        applyYoshiColor(yoshi, yoshiType);

    applyRemoteYoshiTongueFromSnapshot(body, remoteYoshiTongue(yoshi), snap);
    applyRemoteYoshiEatFromSnapshot(yoshi, slot, snap);

    slot.hostSpraying = (snap.vfxFlags & VFX_WATER_SPRAY) != 0 &&
                        (snap.vfxFlags & VFX_FLUDD_EMPTY) == 0;
    if (slot.hostSpraying) {
        slot.sprayPressureEnc = static_cast<u8>(snap.pingMs >> 8);
        if (slot.sprayPressureEnc == 0)
            slot.sprayPressureEnc = encodeSprayPressure(1.0f);
        notifyRemoteYoshiJuiceDrawTint(yoshiType);
    } else {
        slot.sprayPressureEnc = 0;
    }
}

void calcRemoteYoshiAnim(TMario *body, const RemoteYoshiSlot *slot) {
    if (!body || !body->mYoshi)
        return;

    TYoshi *yoshi = body->mYoshi;
    if (yoshi->mState != TYoshi::MOUNTED)
        return;

    syncMountedYoshiTransform(body, yoshi);
    ensureRemoteYoshiDrawable(yoshi);
    calcRemoteYoshiMountedAnim(body, yoshi, slot);
}

Mtx *getRemoteYoshiSprayEmitMtx(TMario *body) {
    if (!body || !body->mYoshi || body->mYoshi->mState != TYoshi::MOUNTED)
        return nullptr;

    TYoshi *yoshi = body->mYoshi;
    if (!yoshi->mActor || !yoshi->mActor->mModel || !yoshi->mActor->mModel->mJointArray)
        return nullptr;

    const u16 joint = remoteYoshiTongueJointIndex(yoshi);
    return &yoshi->mActor->mModel->mJointArray[joint];
}

void performRemoteYoshiDraw(TMario *body, u32 flags, JDrama::TGraphics *graphics, bool drawBody) {
    (void)graphics;
    if (!drawBody || !body || !body->mYoshi)
        return;

    TYoshi *yoshi = body->mYoshi;
    if (yoshi->mState != TYoshi::MOUNTED)
        return;

    ensureRemoteYoshiDrawable(yoshi);

    if (flags & 0x4)
        viewCalcRemoteMountedYoshi(yoshi);
    if (flags & 0x200 && remoteMountedYoshiDrawAllowed(yoshi)) {
        entry__6TYoshiFv(yoshi);
        performRemoteYoshiTongueDraw(remoteYoshiTongue(yoshi));
    }
}

struct FindYoshiFruitCtx {
    u32 actorType;
    TVec3f target;
    TMapObjBase *best;
    f32 bestDistSq;
};

static TVec3f mapObjWorldPos(const TMapObjBase *obj) {
    TVec3f pos = obj->mInitialPosition;
    if (obj)
        const_cast<TMapObjBase *>(obj)->JSGGetTranslation(reinterpret_cast<Vec *>(&pos));
    return pos;
}

static bool visitFindYoshiFruit(TMapObjBase *obj, void *rawCtx) {
    auto *ctx = reinterpret_cast<FindYoshiFruitCtx *>(rawCtx);
    if (!obj || !smso::isValidMapObjPtr(obj))
        return false;

    auto *hit = reinterpret_cast<THitActor *>(obj);
    if (!TMapObjBase::isFruit(hit))
        return false;

    const u32 objectId = hit->mObjectID;
    if (!isYoshiFruitActorType(objectId))
        return false;

    const TVec3f pos = mapObjWorldPos(obj);
    const f32 dx = pos.x - ctx->target.x;
    const f32 dy = pos.y - ctx->target.y;
    const f32 dz = pos.z - ctx->target.z;
    const f32 distSq = dx * dx + dy * dy + dz * dz;
    if (distSq > kYoshiFruitMatchRadiusSq)
        return false;

    if (ctx->actorType != 0 && objectId != ctx->actorType)
        return false;

    if (!ctx->best || distSq < ctx->bestDistSq) {
        ctx->best = obj;
        ctx->bestDistSq = distSq;
    }
    return false;
}

static TMapObjBase *findYoshiFruitNear(const TVec3f &pos, u32 actorType) {
    FindYoshiFruitCtx ctx = {actorType, pos, nullptr, kYoshiFruitMatchRadiusSq};
    smso::forEachManagedMapObj(visitFindYoshiFruit, &ctx);
    return ctx.best;
}

static void hideYoshiFruitActor(TMapObjBase *obj) {
    if (!obj)
        return;

    obj->makeObjDead();

    auto *live = reinterpret_cast<TLiveActor *>(obj);
    live->mStateFlags.asFlags.mClipFromScene = true;
    live->mStateFlags.asFlags.mIsObjDead = true;
}

bool applyRemoteYoshiFruitWorldEvent(u8 actorTypeEnc, u32 packedPos) {
    if (actorTypeEnc == 0 || !smso::isValidPackedWorldPos(packedPos))
        return false;

    f32 x = 0.0f;
    f32 y = 0.0f;
    f32 z = 0.0f;
    smso::unpackCollectibleWorldPos(packedPos, x, y, z);
    const TVec3f target = {x, y, z};
    TMapObjBase *fruit =
        findYoshiFruitNear(target, decodeYoshiFruitActorType(actorTypeEnc));
    if (!fruit)
        return false;

    hideYoshiFruitActor(fruit);
    return true;
}

void resetLocalYoshiFruitSync() {
    localFruitEatEventId = 0;
    localPublishedMouthEnc = 0;
}

} // namespace smso
