#include "yoshi_sync.hpp"

#include "collectible_scan.hpp"
#include "remote_water_sync.hpp"
#include "world_sync.hpp"
#include <SMS/MapObj/MapObjBase.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/NozzleBase.hxx>
#include <SMS/Player/NozzleTrigger.hxx>
#include <SMS/Player/Watergun.hxx>
#include <SMS/Player/Yoshi.hxx>
#include <SMS/Strategic/HitActor.hxx>
#include <SMS/Strategic/LiveActor.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>

#include <JSystem/J3D/J3DModel.hxx>
#include <JSystem/JDrama/JDRGraphics.hxx>
#include <JSystem/JDrama/JDRNameRefGen.hxx>
#include <JSystem/JDrama/JDRViewObjPtrListT.hxx>
#include <Dolphin/OS.h>
#include <math.h>
#include <Dolphin/MTX.h>
#include <Dolphin/types.h>
#include <JSystem/JUtility/JUTColor.hxx>

extern JUtility::TColor bodyColor[4];
extern TMarDirector *gpMarDirector;

namespace {

constexpr u8 kYoshiNozzleId = static_cast<u8>(TWaterGun::Yoshi);
constexpr u8 kYoshiJuiceScale = 31;
constexpr f32 kRemoteYoshiAnimResyncFrames = 2.0f;
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
using YoshiTongueVoidFn = void (*)(void *);
using YoshiEntryFn = void (*)(TYoshi *);
using YoshiThinkUpperFn = void (*)(TYoshi *);

static YoshiTongueCalcAnimFn sYoshiTongueCalcAnim =
    reinterpret_cast<YoshiTongueCalcAnimFn>(SMS_PORT_REGION(0x8026764C, 0x8025F3D8, 0, 0));
static YoshiTongueVoidFn sYoshiTongueViewCalc =
    reinterpret_cast<YoshiTongueVoidFn>(SMS_PORT_REGION(0x802675F0, 0x8025F37C, 0, 0));
static YoshiEntryFn sYoshiEntry =
    reinterpret_cast<YoshiEntryFn>(SMS_PORT_REGION(0x8026DF9C, 0x80265D28, 0, 0));
static YoshiThinkUpperFn sYoshiThinkUpper =
    reinterpret_cast<YoshiThinkUpperFn>(SMS_PORT_REGION(0x8026FC90, 0x80267A1C, 0, 0));

struct StagedYoshiThinkFludd {
    u8 nozzle;
    s32 water;
    f32 deformPressure;
    f32 triggerFill;
    u8 sprayState;
};

static void stageRemoteYoshiThinkUpperFludd(TMario *body, const RemoteYoshiSlot *slot,
                                            StagedYoshiThinkFludd &saved) {
    if (!body || !body->mFludd || !slot)
        return;

    TWaterGun *fludd = body->mFludd;
    saved.nozzle = fludd->mCurrentNozzle;
    saved.water = fludd->mCurrentWater;
    saved.deformPressure = 0.0f;
    saved.triggerFill = 0.0f;
    saved.sprayState = TNozzleTrigger::INACTIVE;

    TNozzleBase *yoshiNozzle = fludd->mNozzleList[TWaterGun::Yoshi];
    if (yoshiNozzle)
        saved.deformPressure = yoshiNozzle->_378;

    const bool tongueActive = smso::yoshiTongueIsActive(slot->lastTongueState);
    f32 mouthPressure = 0.0f;
    if (slot->hostSpraying)
        mouthPressure = smso::decodeSprayPressure(slot->sprayPressureEnc);
    else if (tongueActive)
        mouthPressure = 1.0f;

    fludd->mCurrentNozzle = TWaterGun::Yoshi;
    if (!yoshiNozzle)
        return;

    fludd->mCurrentWater = yoshiNozzle->mEmitParams.mAmountMax.get();
    if (mouthPressure <= 0.01f) {
        yoshiNozzle->_378 = 0.0f;
        return;
    }

    yoshiNozzle->_378 = mouthPressure;

    auto *trigger = reinterpret_cast<TNozzleTrigger *>(yoshiNozzle);
    saved.triggerFill = trigger->mTriggerFill;
    saved.sprayState = trigger->mSprayState;
    const f32 maxPressure = trigger->mEmitParams.mInsidePressureMax.get();
    if (maxPressure > 0.0f)
        trigger->mTriggerFill = mouthPressure * maxPressure;
    trigger->mSprayState = TNozzleTrigger::ACTIVE;
}

static void restoreRemoteYoshiThinkUpperFludd(TMario *body, const StagedYoshiThinkFludd &saved) {
    if (!body || !body->mFludd)
        return;

    TWaterGun *fludd = body->mFludd;
    fludd->mCurrentNozzle = saved.nozzle;
    fludd->mCurrentWater = saved.water;

    TNozzleBase *yoshiNozzle = fludd->mNozzleList[TWaterGun::Yoshi];
    if (!yoshiNozzle)
        return;

    yoshiNozzle->_378 = saved.deformPressure;
    auto *trigger = reinterpret_cast<TNozzleTrigger *>(yoshiNozzle);
    trigger->mTriggerFill = saved.triggerFill;
    trigger->mSprayState = saved.sprayState;
}

static void calcRemoteYoshiThinkUpper(TMario *body, TYoshi *yoshi, RemoteYoshiSlot *slot) {
    if (!body || !yoshi || !slot || !sYoshiThinkUpper || !body->mFludd)
        return;
    // Mouth-open mtx only — idle remotes skip joint mtxCalc churn when several are mounted.
    // Still run one closing frame after spray/tongue ends so eat mtxCalc is cleared.
    const bool needMouth =
        slot->hostSpraying || smso::yoshiTongueIsActive(slot->lastTongueState);
    if (!needMouth && !slot->thinkUpperMouthWasOpen)
        return;

    StagedYoshiThinkFludd staged = {};
    stageRemoteYoshiThinkUpperFludd(body, slot, staged);
    sYoshiThinkUpper(yoshi);
    restoreRemoteYoshiThinkUpperFludd(body, staged);
    slot->thinkUpperMouthWasOpen = needMouth;
}

// "敵グループ" in Shift-JIS — must match retail TNameRef bytes (see kPlayerGroupName).
// A UTF-8 source literal embeds the wrong code units, so tongue removal silently fails
// and puppet tongues stay on the enemy perform list (movement/findTarget → multi-Yoshi crash).
static const char kEnemyGroupName[] = "\x93\x47\x83\x4F\x83\x8B\x81\x5B\x83\x76";

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

static Vec *remoteYoshiTongueHeadDir(void *tongue) {
    return reinterpret_cast<Vec *>(reinterpret_cast<u8 *>(tongue) + kTongueHeadDirOffset);
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
    return smso::unpackYoshiFruitEnc(snap.vfxFlags);
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

// doldecomp TYoshiTongue::viewCalc only calls mModel/mTipModel->viewCalc() — safe on remotes.
// movement()/findTarget() are the stage-scanning paths and must never run on puppets.
static void performRemoteYoshiTongueViewCalc(void *tongue) {
    if (!tongue || !sYoshiTongueViewCalc)
        return;
    sYoshiTongueViewCalc(tongue);
}

static void applyRemoteYoshiTongueTipFromOffset(TMario *body, void *tongue, const smso::Vec3 &offset) {
    if (!body || !tongue)
        return;

    Vec *tip = remoteYoshiTongueTipPos(tongue);
    Vec *head = remoteYoshiTongueHeadPos(tongue);
    Vec *headDir = remoteYoshiTongueHeadDir(tongue);
    tip->x = body->mTranslation.x + offset.x;
    tip->y = body->mTranslation.y + offset.y;
    tip->z = body->mTranslation.z + offset.z;
    head->x = body->mTranslation.x;
    head->y = body->mTranslation.y;
    head->z = body->mTranslation.z;

    const f32 lenSq = offset.x * offset.x + offset.y * offset.y + offset.z * offset.z;
    if (lenSq > 1.0f) {
        const f32 invLen = 1.0f / sqrtf(lenSq);
        headDir->x = offset.x * invLen;
        headDir->y = offset.y * invLen;
        headDir->z = offset.z * invLen;
    } else {
        const f32 yaw = body->mRotation.y;
        headDir->x = sinf(yaw);
        headDir->y = 0.0f;
        headDir->z = cosf(yaw);
    }
}

static void calcRemoteYoshiTongueAnim(TYoshi *yoshi, J3DModel *model) {
    void *tongue = remoteYoshiTongue(yoshi);
    if (!tongue || !model || !model->mJointArray || !sYoshiTongueCalcAnim)
        return;

    const u16 joint = remoteYoshiTongueJointIndex(yoshi);
    Mtx tongueBase;
    MTXCopy(model->mJointArray[joint], tongueBase);
    // calcAnim only — updates tongue joint matrices from synced state/progress (no viewCalc).
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

static void applyYoshiColor(TYoshi *yoshi, u8 type, u8 bckId) {
    const u8 clamped = clampYoshiType(type);
    yoshi->mType = static_cast<s8>(clamped);

    const JUtility::TColor &color = bodyColor[clamped];
    // doldecomp TYoshi::init stores 0..255 in unk84; entry() casts to s16 for tev color.
    yoshi->mRedComponent = static_cast<f32>(color.r);
    yoshi->mGreenComponent = static_cast<f32>(color.g);
    yoshi->mBlueComponent = static_cast<f32>(color.b);
    yoshi->thinkBtp(bckId > 0 ? static_cast<int>(bckId) : kYoshiRideAnimId);
}

// Returns true when the enemy group was located (tongue erased if present).
// False means the stage name-ref is missing — do not treat initInLoadAfter as settled.
static bool removeRemoteYoshiTongueFromEnemyGroup(void *tongue) {
    if (!tongue)
        return false;

    JDrama::TNameRefGen *gen = JDrama::TNameRefGen::getInstance();
    if (!gen)
        return false;

    JDrama::TNameRef *ref = gen->getNameRef(kEnemyGroupName);
    if (!ref) {
        JDrama::TNameRef *root = gen->getRootNameRef();
        if (root)
            ref = root->search(kEnemyGroupName);
    }
    if (!ref)
        return false;

    auto *group = reinterpret_cast<JDrama::TViewObjPtrListT<JDrama::TViewObj> *>(ref);
    // erase every match — a failed prior remove + re-initInLoadAfter can duplicate entries.
    // JDrama erase returns void; use post-increment like clearRemotePerformGroupMembers.
    for (auto it = group->mViewObjList.begin(); it != group->mViewObjList.end();) {
        if (static_cast<void *>(*it) == tongue)
            group->mViewObjList.erase(it++);
        else
            ++it;
    }
    return true;
}

static void ensureRemoteYoshiDrawable(TYoshi *yoshi) {
    if (!yoshi)
        return;

    // doldecomp TYoshi::entry skips draw when juice timer fails low-bit checks,
    // and also forces mType=GREEN when juice < 600. Keep remotes well clear of
    // both the <360 and the 360..599 blink windows.
    if (yoshi->mMaxJuice <= 0)
        yoshi->mMaxJuice = 21300;
    if (yoshi->mCurJuice < 600)
        yoshi->mCurJuice = yoshi->mMaxJuice;
    if (yoshi->mCurJuice < 600)
        yoshi->mCurJuice = 21300;
    if ((yoshi->mCurJuice & 0x8) == 0 && yoshi->mCurJuice < 360)
        yoshi->mCurJuice |= 0x8;
    if (yoshi->mCurJuice >= 360 && yoshi->mCurJuice < 600 &&
        (yoshi->mCurJuice & 0x10) == 0)
        yoshi->mCurJuice |= 0x10;
}

static void resetRemoteYoshiToEgg(TYoshi *yoshi, RemoteYoshiSlot &slot) {
    if (!yoshi)
        return;

    // Belt: ensure a dismounting puppet tongue cannot remain on 敵グループ.
    removeRemoteYoshiTongueFromEnemyGroup(remoteYoshiTongue(yoshi));

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
    slot.thinkUpperMouthWasOpen = false;
    slot.stageInitAttempted = false;
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

static bool remoteYoshiRigReady(TYoshi *yoshi) {
    if (!yoshi || !yoshi->mActor || !yoshi->mActor->mModel)
        return false;
    if (!remoteYoshiTongue(yoshi))
        return false;
    J3DModel **mirrors = yoshiMirrorModels(yoshi);
    return mirrors[0] != nullptr && mirrors[1] != nullptr;
}

static bool ensureRemoteYoshiStageReady(TYoshi *yoshi, RemoteYoshiSlot &slot) {
    if (!remoteYoshiRigReady(yoshi))
        return false;
    if (!slot.stageInitDone) {
        // NEVER call TYoshi::initInLoadAfter() on network puppets.
        //
        // Retail initInLoadAfter creates five TMirrorActors per Yoshi (body + 2
        // hands via TYoshi, tongue + tip via TYoshiTongue::initInLoadAfter) and
        // push_back's the tongue onto 敵グループ. Each TMirrorActor also
        // allocates a second J3DModel sharing the live ModelData and inserts
        // into 鏡シーン. Five concurrent remote mounts ≈ 25 mirror models —
        // that exhausts stage/mirror heap and the shared J3D draw path then
        // drops Mario+Yoshi packets (host sees it first: local + remotes).
        //
        // Riding meshes (mActor / mMirrorModels / tongue models) already exist
        // from TYoshi::init at body spawn. Puppet tongues must never join
        // 敵グループ (movement/findTarget). Mirror reflections for remotes are
        // intentionally skipped.
        //
        // stageInitAttempted latches the one-shot settle; never re-enter a path
        // that could call initInLoadAfter (duplicate TMirrorActor crash guard).
        void *tongue = remoteYoshiTongue(yoshi);
        if (!slot.stageInitAttempted) {
            removeRemoteYoshiTongueFromEnemyGroup(tongue);
            slot.stageInitAttempted = true;
        }
        // Belt: erase any tongue that retail/stage paths may have pushed.
        // Missing 敵グループ name-ref is fine — we never pushed it ourselves.
        removeRemoteYoshiTongueFromEnemyGroup(remoteYoshiTongue(yoshi));
        slot.stageInitDone = true;
    }
    return remoteYoshiRigReady(yoshi);
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
    if (!remoteYoshiRigReady(yoshi))
        return;

    yoshi->mActor->viewCalc();
    J3DModel **mirrors = yoshiMirrorModels(yoshi);
    mirrors[0]->viewCalc();
    mirrors[1]->viewCalc();

    performRemoteYoshiTongueViewCalc(remoteYoshiTongue(yoshi));
}

static void drawRemoteMountedYoshi(TYoshi *yoshi) {
    // Prefer slim entry when possible: retail TYoshi::entry always
    // gpBindShadowManager->request + gpQuestionManager->request, and always
    // entries hand mirrors even when shape-hidden. With many remotes that floods
    // shared queues. Keep retail entry for tev body tint (orange/purple/pink).
    if (!remoteYoshiRigReady(yoshi) || !remoteMountedYoshiDrawAllowed(yoshi) || !sYoshiEntry)
        return;

    const u8 savedType = static_cast<u8>(yoshi->mType & 0x0F);
    const u8 bckId =
        static_cast<u8>(yoshi->mActor->getCurAnmIdx(MActor::BCK) & 0xFF);
    applyYoshiColor(yoshi, savedType, bckId);
    ensureRemoteYoshiDrawable(yoshi);
    sYoshiEntry(yoshi);
    if (static_cast<u8>(yoshi->mType & 0x0F) != savedType)
        applyYoshiColor(yoshi, savedType, bckId);
}

// MOUNTED subset of doldecomp TYoshi::calcAnim — omits thinkAnimation side effects;
// thinkUpper only when spray/tongue needs mouth open (plus one closing frame).
static void calcRemoteYoshiMountedAnim(TMario *body, TYoshi *yoshi, RemoteYoshiSlot *slot) {
    if (!body || !yoshi || !yoshi->mActor || !yoshi->mActor->mModel)
        return;

    MActor *actor = yoshi->mActor;
    J3DModel *model = actor->mModel;
    J3DModel *mirrors[2] = {yoshiMirrorModels(yoshi)[0], yoshiMirrorModels(yoshi)[1]};
    if (!mirrors[0] || !mirrors[1])
        return;

    Mtx *taken = body->getTakenMtx();
    if (taken)
        MTXCopy(*taken, model->mBaseMtx);

    // doldecomp TYoshi::movement hatched path clears all shape draw flags first.
    if (model->mModelData)
        offFlag1OnAllShapes(model->mModelData);
    offFlag1OnAllShapes(mirrors[0]->mModelData);
    offFlag1OnAllShapes(mirrors[1]->mModelData);

    applyMountedYoshiShapeDrawFlags(actor, mirrors[0], mirrors[1]);
    calcRemoteYoshiThinkUpper(body, yoshi, slot);
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
    applyYoshiColor(yoshi, type, bckId);

    if (yoshi->mState != TYoshi::MOUNTED) {
        yoshi->mState = TYoshi::MOUNTED;
        slot.hatched = true;
    }

    applyRemoteYoshiAnim(yoshi, bckId);
    slot.mounted = true;
    slot.type = type;
    applyYoshiColor(yoshi, type, bckId);
    syncMountedYoshiTransform(body, yoshi);
}

static void syncRemoteYoshiAnimFrame(TYoshi *yoshi, RemoteYoshiSlot &slot,
                                     const smso::PlayerSnapshot &snap) {
    if (!yoshi || !yoshi->mActor)
        return;

    J3DFrameCtrl *frameCtrl = yoshi->mActor->getFrameCtrl(MActor::BCK);
    if (!frameCtrl)
        return;

    const bool hostSpraying =
        (snap.vfxFlags & smso::VFX_WATER_SPRAY) != 0 &&
        (snap.vfxFlags & smso::VFX_FLUDD_EMPTY) == 0;
    const u8 bckId = snap.water;
    const bool bckChanged = slot.lastYoshiBck != bckId;

    // While spraying, pingMs high carries juice pressure — advance body BCK locally.
    if (!hostSpraying) {
        const f32 hostFrame = static_cast<f32>(snap.pingMs >> 8) / 8.0f;
        const f32 drift = frameCtrl->mCurFrame - hostFrame;
        const f32 absDrift = drift < 0.0f ? -drift : drift;
        if (bckChanged || absDrift > kRemoteYoshiAnimResyncFrames)
            frameCtrl->mCurFrame = hostFrame;
    }

    frameCtrl->mFrameRate = 1.0f;
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
        const Vec *tip = remoteYoshiTongueTipPos(tongue);
        snap.velocity.x = tip->x - mario->mTranslation.x;
        snap.velocity.y = tip->y - mario->mTranslation.y;
        snap.velocity.z = tip->z - mario->mTranslation.z;
    }

    const u8 mouthEnc = encodeYoshiFruitActorType(mouthActorType);
    if (mouthEnc != 0) {
        snap.vfxFlags = smso::packYoshiFruitMouthVfx(snap.vfxFlags, mouthEnc);
        const Vec *tip = remoteYoshiTongueTipPos(tongue);
        if (mouthEnc != localPublishedMouthEnc) {
            localPublishedMouthEnc = mouthEnc;
            smso::CommBuffer *buf = smso::getCommBuffer();
            publishLocalYoshiFruitTaken(mouthEnc, *tip, buf ? buf->localSlot : 0);
        }
    } else {
        smso::clearYoshiFruitMouthVfx(snap.vfxFlags);
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
    *progress = snapshotYoshiTongueProgressByte(snap);
    if (*progress == 0)
        *progress = unpackYoshiTongueProgressCoarse(snap.health);
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
        // Visual eat BCK only — retail doEat() mutates stage fruit/actors on every client.
        if (yoshi->mActor)
            applyRemoteYoshiAnim(yoshi, bck);
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
    syncRemoteYoshiAnimFrame(yoshi, slot, snap);

    if (static_cast<u8>(yoshi->mType & 0x0F) != yoshiType)
        applyYoshiColor(yoshi, yoshiType, snap.water);

    applyRemoteYoshiTongueFromSnapshot(body, remoteYoshiTongue(yoshi), snap);
    if (yoshiTongueIsActive(unpackYoshiTongueState(snap.health)) &&
        slot.lastTongueState != unpackYoshiTongueState(snap.health)) {
        OSReport("[SMSO] remote tongue state=%u progress=%u\n",
                 unpackYoshiTongueState(snap.health), snapshotYoshiTongueProgressByte(snap));
    }
    slot.lastTongueState = unpackYoshiTongueState(snap.health);
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

void calcRemoteYoshiAnim(TMario *body, RemoteYoshiSlot *slot) {
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
    if (flags & 0x200)
        drawRemoteMountedYoshi(yoshi);
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

void exportYoshiTongueProgressPingLow(TMario *mario, PlayerSnapshot &snap) {
    if (!mario || !mario->onYoshi() || !mario->mYoshi)
        return;

    void *tongue = remoteYoshiTongue(mario->mYoshi);
    if (!tongue)
        return;

    const u8 tongueState = unpackYoshiTongueState(snap.health);
    if (!yoshiTongueIsActive(tongueState))
        return;

    const u16 progress = *remoteYoshiTongueProgressPtr(tongue);
    snap.pingMs = static_cast<u16>(progress > 255 ? 255 : progress) | (snap.pingMs & 0xFF00u);
}

void exportYoshiBckFramePingHigh(TMario *mario, PlayerSnapshot &snap) {
    if (!mario || !mario->onYoshi() || !mario->mYoshi || !mario->mYoshi->mActor)
        return;
    if ((snap.vfxFlags & smso::VFX_WATER_SPRAY) != 0 &&
        (snap.vfxFlags & smso::VFX_FLUDD_EMPTY) == 0)
        return;

    J3DFrameCtrl *frameCtrl = mario->mYoshi->mActor->getFrameCtrl(MActor::BCK);
    if (!frameCtrl)
        return;

    f32 frame = frameCtrl->mCurFrame;
    if (frame < 0.0f)
        frame = 0.0f;
    const u8 enc = static_cast<u8>(frame * 8.0f > 255.0f ? 255.0f : frame * 8.0f);
    snap.pingMs = static_cast<u16>((snap.pingMs & 0xFFu) | (static_cast<u16>(enc) << 8));
}

} // namespace smso
