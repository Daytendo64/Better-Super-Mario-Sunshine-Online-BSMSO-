#include "fruit_sync.hpp"

#include "collectible_scan.hpp"
#include "comm_buffer.hpp"
#include "remote_actor.hpp"
#include "world_sync.hpp"

#include <SMS/Map/Map.hxx>
#include <SMS/MapObj/MapObjBase.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/NozzleTrigger.hxx>
#include <SMS/Player/Watergun.hxx>
#include <SMS/raw_fn.hxx>
#include <SMS/Strategic/HitActor.hxx>
#include <SMS/Strategic/LiveActor.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>
#include <BetterSMS/memory.hxx>
#include <Dolphin/OS.h>

extern TMario *gpMarioAddress;
extern TMarDirector *gpMarDirector;
extern TMap *gpMap;

namespace {

constexpr u32 kHitMessageTake = 4u;
constexpr u32 kHitMessageThrow = 6u;
constexpr u32 kHitMessageSprayedByWater = 0xFu;
constexpr u32 kDurianActorType = 0x400000D0u;
constexpr f32 kFruitMatchRadius = 450.0f;
constexpr f32 kFruitMatchRadiusSq = kFruitMatchRadius * kFruitMatchRadius;
constexpr f32 kFruitMatchRadiusExpanded = 3000.0f;
constexpr f32 kFruitMatchRadiusExpandedSq = kFruitMatchRadiusExpanded * kFruitMatchRadiusExpanded;
constexpr f32 kKickDetectMinSpeedSq = 8.0f * 8.0f;
constexpr f32 kKickDetectDeltaSq = 10.0f * 10.0f;
constexpr f32 kDurianKickDetectMinSpeedSq = 1.5f * 1.5f;
constexpr f32 kDurianKickDetectDeltaSq = 1.5f * 1.5f;
constexpr f32 kSprayKickDetectMinSpeedSq = 2.0f * 2.0f;
constexpr f32 kSprayKickDetectDeltaSq = 2.0f * 2.0f;
constexpr f32 kSprayInteractPadding = 520.0f;
constexpr f32 kKickInteractPadding = 280.0f;
constexpr f32 kThrowSpeedThresholdSq = 40.0f * 40.0f;
constexpr f32 kThrowHeightAboveMario = 100.0f;
constexpr f32 kFruitGroundProbeLift = 80.0f;
constexpr u8 kPendingReleaseFrames = 5;
constexpr u8 kFruitAirborneSyncInterval = 4;
constexpr f32 kFruitAirborneMinSpeedSq = 2.0f * 2.0f;
constexpr bool kFruitHotPathOsReport = false;

static bool sRetryingPendingFruitEvent = false;

struct PendingFruitRelease {
    TMapObjBase *fruit;
    u8 framesLeft;
};

static PendingFruitRelease sPendingRelease = {};

using ReceiveMessageFn = bool (*)(THitActor *, THitActor *, u32);
using FruitKickedFn = void (*)(TMapObjBase *);
using FruitHoldFn = void (*)(TMapObjBase *, TTakeActor *);
using FruitThrownFn = void (*)(TMapObjBase *);
using BoundByActorFn = void (*)(TMapObjBase *, THitActor *);
using GetRadiusAtYFn = f32 (*)(const TMapObjBase *, f32);

static FruitKickedFn sResetFruitKickedReplay = nullptr;
static FruitHoldFn sResetFruitHoldReplay = nullptr;
static FruitKickedFn sMapObjBallKickedReplay = nullptr;
static FruitHoldFn sMapObjBallHoldReplay = nullptr;
static FruitHoldFn sMapObjGeneralHoldReplay = nullptr;
static BoundByActorFn sMapObjBallBoundByActorReplay = nullptr;
static GetRadiusAtYFn sMapObjGetRadiusAtY = nullptr;

constexpr u32 kLiveFlagUnk10 = 0x10u;
constexpr u32 kLiveFlagAirborne = 0x80u;
constexpr u16 kFruitThrownState = 11u;
constexpr u16 kFruitFreeState = 1u;
constexpr f32 kDefaultThrowPower = 1.0f;
constexpr f32 kFruitThrowVelPackScale = 0.25f;
constexpr s32 kFruitThrowVelPackBias = 512;
constexpr u32 kFruitThrowVelPayloadTag = 0x80000000u;

struct FruitMapObjPhysicalData {
    f32 unk0;
    f32 unk4;
    f32 unk8;
    f32 unkC;
    f32 unk10;
    f32 unk14;
    f32 unk18;
    f32 unk1C;
    f32 unk20;
    f32 unk24;
    f32 unk28;
    f32 unk2C;
    f32 unk30;
};

struct FruitMapObjPhysicalInfo {
    u32 unk0;
    FruitMapObjPhysicalData *unk4;
    u32 mWallCheckFlags;
};

struct FruitMapObjData {
    const char *unk0;
    u32 unk4;
    const char *unk8;
    const char *unkC;
    const void *mAnim;
    const void *mHit;
    const void *mCollision;
    const void *mSound;
    const FruitMapObjPhysicalInfo *mPhysical;
};

using SinCosFn = f32 (*)(s16);

static SinCosFn sJmasSin = nullptr;
static SinCosFn sJmasCos = nullptr;

static u32 gTResetFruitVt = 0;
static u32 gTMapObjBallVt = 0;

constexpr u32 kMaxVtReceiveHooks = 8;
constexpr u32 kVtReceiveMessageOffset = 0x38;

struct VtReceiveHook {
    u32 vtable;
    ReceiveMessageFn orig;
};

static VtReceiveHook sVtReceiveHooks[kMaxVtReceiveHooks] = {};
static u32 sVtReceiveHookCount = 0;
static bool sFruitHooksReady = false;

static bool sApplyingRemoteFruitEvent = false;
static THitActor *sLastLocalHeldFruit = nullptr;
static u32 sLastLocalFruitEventId = 0;
static u32 sDropSuppressFrames = 0;

static TMapObjBase *sRemoteCarriedFruit[smso::MAX_REMOTE_SLOTS] = {};

struct FruitKickWatch {
    TMapObjBase *fruit;
    f32 lastSpeedSq;
    f32 lastPosX;
    f32 lastPosY;
    f32 lastPosZ;
    u8 cooldown;
};

constexpr u32 kMaxFruitKickWatch = 24;
static FruitKickWatch sFruitKickWatch[kMaxFruitKickWatch] = {};

struct FlyingFruitTrack {
    TMapObjBase *fruit;
    u8 fruitEnc;
    u8 moverSlot;
    u8 framesUntilSync;
    u32 lastPackedPos;
    u32 lastPackedVel;
};

constexpr u32 kMaxFlyingFruitTrack = 24;
static FlyingFruitTrack sFlyingFruitTrack[kMaxFlyingFruitTrack] = {};

static void ensureFruitAwakeForSync(TMapObjBase *fruit) {
    if (!fruit)
        return;

    auto *live = reinterpret_cast<TLiveActor *>(fruit);
    live->mStateFlags.asFlags.mClipFromScene = false;
    live->mStateFlags.asFlags.mIsObjDead = false;
    fruit->awake();
}

static bool isDurian(const TMapObjBase *obj) {
    if (!obj)
        return false;
    return reinterpret_cast<const THitActor *>(obj)->mObjectID == kDurianActorType;
}

static bool isMarioSyncFruit(const THitActor *hit) {
    if (!hit)
        return false;
    return TMapObjBase::isFruit(const_cast<THitActor *>(hit)) || hit->mObjectID == kDurianActorType;
}

static bool isFruitCandidateForApply(const TMapObjBase *obj, u32 actorType) {
    if (!obj || !smso::isValidMapObjPtr(obj))
        return false;
    auto *hit = reinterpret_cast<THitActor *>(const_cast<TMapObjBase *>(obj));
    if (!isMarioSyncFruit(hit))
        return false;
    if (actorType != 0 && hit->mObjectID != actorType)
        return false;
    const auto *live = reinterpret_cast<const TLiveActor *>(obj);
    return !live->mStateFlags.asFlags.mIsObjDead;
}

static bool isLiveFruit(const TMapObjBase *obj) {
    return isFruitCandidateForApply(obj, 0);
}

static TVec3f mapObjWorldPos(const TMapObjBase *obj) {
    TVec3f pos = obj->mInitialPosition;
    if (obj)
        const_cast<TMapObjBase *>(obj)->JSGGetTranslation(reinterpret_cast<Vec *>(&pos));
    return pos;
}

static void tryCaptureFruitKicked(TMapObjBase *obj);
static void replayFruitDurianBump(TMapObjBase *fruit, TMario *kicker, const TVec3f &kickPos, f32 vx,
                                  f32 vy, f32 vz, bool hasPackedVelocity);

static f32 marioDamageRadiusForFruitCapture() {
    constexpr f32 kDefaultRadius = 90.0f;
    if (!gpMarioAddress || !smso::objectSyncGameplayReady())
        return kDefaultRadius;

    using GetMarioDamageRadiusFn = f32 (*)();
    static GetMarioDamageRadiusFn getRadius = reinterpret_cast<GetMarioDamageRadiusFn>(
        SMS_PORT_REGION(0x802738E4, 0x8026B670, 0, 0));
    const f32 radius = getRadius();
    return radius >= 30.0f ? radius : kDefaultRadius;
}

static f32 fruitSpeedSq(const TMapObjBase *obj) {
    if (!obj)
        return 0.0f;
    const auto *live = reinterpret_cast<const TLiveActor *>(obj);
    const TVec3f &speed = live->mSpeed;
    return speed.x * speed.x + speed.y * speed.y + speed.z * speed.z;
}

static FruitKickWatch *fruitKickWatchFor(TMapObjBase *fruit) {
    for (u32 i = 0; i < kMaxFruitKickWatch; ++i) {
        if (sFruitKickWatch[i].fruit == fruit)
            return &sFruitKickWatch[i];
    }
    for (u32 i = 0; i < kMaxFruitKickWatch; ++i) {
        if (sFruitKickWatch[i].fruit == nullptr) {
            const TVec3f pos = mapObjWorldPos(fruit);
            sFruitKickWatch[i].fruit = fruit;
            sFruitKickWatch[i].lastSpeedSq = fruitSpeedSq(fruit);
            sFruitKickWatch[i].lastPosX = pos.x;
            sFruitKickWatch[i].lastPosY = pos.y;
            sFruitKickWatch[i].lastPosZ = pos.z;
            sFruitKickWatch[i].cooldown = 0;
            return &sFruitKickWatch[i];
        }
    }
    return nullptr;
}

static FlyingFruitTrack *flyingFruitTrackFor(TMapObjBase *fruit) {
    for (u32 i = 0; i < kMaxFlyingFruitTrack; ++i) {
        if (sFlyingFruitTrack[i].fruit == fruit)
            return &sFlyingFruitTrack[i];
    }
    for (u32 i = 0; i < kMaxFlyingFruitTrack; ++i) {
        if (sFlyingFruitTrack[i].fruit == nullptr)
            return &sFlyingFruitTrack[i];
    }
    return nullptr;
}

static void untrackFlyingFruit(TMapObjBase *fruit) {
    for (u32 i = 0; i < kMaxFlyingFruitTrack; ++i) {
        if (sFlyingFruitTrack[i].fruit == fruit)
            sFlyingFruitTrack[i] = {};
    }
}

static void trackFlyingFruit(TMapObjBase *fruit, u8 fruitEnc, u8 moverSlot) {
    FlyingFruitTrack *track = flyingFruitTrackFor(fruit);
    if (!track)
        return;
    const TVec3f pos = mapObjWorldPos(fruit);
    const u32 packedPos = smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
    *track = {fruit, fruitEnc, moverSlot, kFruitAirborneSyncInterval, packedPos, 0};
}

static void computeDurianKickVelocityFromMario(const TMario *mario, const TVec3f &fruitPos, f32 &vx,
                                               f32 &vy, f32 &vz) {
    vx = 0.0f;
    vy = 0.0f;
    vz = 0.0f;
    if (!mario)
        return;

    f32 dirX = fruitPos.x - mario->mTranslation.x;
    f32 dirZ = fruitPos.z - mario->mTranslation.z;
    const f32 horizLenSq = dirX * dirX + dirZ * dirZ;
    if (horizLenSq > 1.0f) {
        const f32 invLen = 1.0f / sqrtf(horizLenSq);
        dirX *= invLen;
        dirZ *= invLen;
    } else {
        const s16 angleY = mario->mModelAngleY;
        dirX = sJmasSin ? sJmasSin(angleY) : sinf(static_cast<f32>(angleY) / 182.04445f);
        dirZ = sJmasCos ? sJmasCos(angleY) : cosf(static_cast<f32>(angleY) / 182.04445f);
    }

    const f32 marioHorizSq =
        mario->mSpeed.x * mario->mSpeed.x + mario->mSpeed.z * mario->mSpeed.z;
    const f32 marioHoriz = marioHorizSq > 0.0f ? sqrtf(marioHorizSq) : 0.0f;
    const f32 kickPower = marioHoriz > 5.0f ? marioHoriz * 1.15f + 10.0f : 14.0f;

    vx = dirX * kickPower + mario->mSpeed.x * 0.35f;
    vz = dirZ * kickPower + mario->mSpeed.z * 0.35f;
    vy = mario->mSpeed.y > 1.0f ? mario->mSpeed.y * 0.35f + 6.0f : 8.0f;
}

struct PendingRemoteFruitEvent {
    u8 eventType;
    u8 fruitEnc;
    u8 actorSlot;
    u32 packedPos;
    u32 packedVel;
    u8 retriesLeft;
};

constexpr u32 kMaxPendingRemoteFruit = 8;
constexpr u8 kPendingRemoteFruitRetries = 90;
static PendingRemoteFruitEvent sPendingRemoteFruit[kMaxPendingRemoteFruit] = {};

static bool localMarioWaterGunActive(const TMario *mario) {
    if (!mario || !mario->mFludd)
        return false;

    const TWaterGun *gun = mario->mFludd;
    if (gun->mCurrentNozzle != TWaterGun::Spray && gun->mCurrentNozzle != TWaterGun::Yoshi)
        return false;

    const TNozzleBase *nozzle = gun->mNozzleList[gun->mCurrentNozzle];
    if (!nozzle)
        return false;

    const auto *trigger = static_cast<const TNozzleTrigger *>(nozzle);
    return trigger->mSprayState == TNozzleTrigger::ACTIVE;
}

static void enqueuePendingRemoteFruitEvent(u8 eventType, u8 fruitEnc, u8 actorSlot, u32 packedPos,
                                           u32 packedVel) {
    for (u32 i = 0; i < kMaxPendingRemoteFruit; ++i) {
        PendingRemoteFruitEvent &slot = sPendingRemoteFruit[i];
        if (slot.retriesLeft == 0)
            continue;
        if (slot.eventType == eventType && slot.fruitEnc == fruitEnc &&
            slot.actorSlot == actorSlot && slot.packedPos == packedPos &&
            slot.packedVel == packedVel) {
            slot.retriesLeft = kPendingRemoteFruitRetries;
            return;
        }
    }

    for (u32 i = 0; i < kMaxPendingRemoteFruit; ++i) {
        PendingRemoteFruitEvent &slot = sPendingRemoteFruit[i];
        if (slot.retriesLeft != 0)
            continue;
        slot = {eventType, fruitEnc, actorSlot, packedPos, packedVel, kPendingRemoteFruitRetries};
        return;
    }
}

static void pollFruitKickCapture(TMario *mario, TMapObjBase *obj) {
    if (!mario || !obj || obj->mHolder || sApplyingRemoteFruitEvent)
        return;
    if (!isMarioSyncFruit(reinterpret_cast<THitActor *>(obj)))
        return;

    FruitKickWatch *watch = fruitKickWatchFor(obj);
    if (!watch)
        return;

    const bool durian = isDurian(obj);
    const TVec3f pos = mapObjWorldPos(obj);
    const f32 dx = pos.x - mario->mTranslation.x;
    const f32 dy = pos.y - mario->mTranslation.y;
    const f32 dz = pos.z - mario->mTranslation.z;
    const f32 interactR = marioDamageRadiusForFruitCapture() + kKickInteractPadding;
    const f32 distSq = dx * dx + dy * dy + dz * dz;
    const f32 speedSq = fruitSpeedSq(obj);
    const f32 minSpeedSq = durian ? kDurianKickDetectMinSpeedSq : kKickDetectMinSpeedSq;
    const f32 deltaSq = durian ? kDurianKickDetectDeltaSq : kKickDetectDeltaSq;

    if (watch->cooldown > 0)
        --watch->cooldown;

    if (distSq <= interactR * interactR) {
        const f32 marioSpeedSq =
            mario->mSpeed.x * mario->mSpeed.x + mario->mSpeed.y * mario->mSpeed.y +
            mario->mSpeed.z * mario->mSpeed.z;
        const f32 marioHorizSq =
            mario->mSpeed.x * mario->mSpeed.x + mario->mSpeed.z * mario->mSpeed.z;
        const f32 posDx = pos.x - watch->lastPosX;
        const f32 posDy = pos.y - watch->lastPosY;
        const f32 posDz = pos.z - watch->lastPosZ;
        const f32 posDeltaSq = posDx * posDx + posDy * posDy + posDz * posDz;

        if (durian && watch->cooldown == 0) {
            const f32 bodyRadius = marioDamageRadiusForFruitCapture() + 50.0f;
            const f32 bodyRadiusSq = bodyRadius * bodyRadius;
            if (distSq <= bodyRadiusSq && marioHorizSq >= 2.0f * 2.0f) {
                tryCaptureFruitKicked(obj);
                watch->cooldown = 4u;
                watch->lastSpeedSq = fruitSpeedSq(obj);
                watch->lastPosX = pos.x;
                watch->lastPosY = pos.y;
                watch->lastPosZ = pos.z;
                return;
            }
        }

        const bool marioMovingIntoFruit = durian && marioSpeedSq >= 10.0f * 10.0f;
        const bool durianDisplaced =
            durian && posDeltaSq >= 2.0f * 2.0f && marioSpeedSq >= 5.0f * 5.0f;
        const bool durianBump =
            durian && distSq <= interactR * interactR && marioSpeedSq >= 8.0f * 8.0f &&
            (speedSq >= minSpeedSq || posDeltaSq >= 1.0f * 1.0f);
        if (watch->cooldown == 0 &&
            (speedSq >= minSpeedSq &&
                 (speedSq >= watch->lastSpeedSq + deltaSq || marioMovingIntoFruit ||
                  durianDisplaced) ||
             durianBump)) {
            tryCaptureFruitKicked(obj);
            watch->cooldown = durian ? 4u : 8u;
        }
    }

    watch->lastSpeedSq = speedSq;
    watch->lastPosX = pos.x;
    watch->lastPosY = pos.y;
    watch->lastPosZ = pos.z;
}

static void pollFruitSprayCapture(TMario *mario, TMapObjBase *obj) {
    if (!mario || !obj || obj->mHolder || sApplyingRemoteFruitEvent)
        return;
    if (!isMarioSyncFruit(reinterpret_cast<THitActor *>(obj)))
        return;
    if (!localMarioWaterGunActive(mario))
        return;

    FruitKickWatch *watch = fruitKickWatchFor(obj);
    if (!watch)
        return;

    const TVec3f pos = mapObjWorldPos(obj);
    const f32 dx = pos.x - mario->mTranslation.x;
    const f32 dy = pos.y - mario->mTranslation.y;
    const f32 dz = pos.z - mario->mTranslation.z;
    const f32 interactR = marioDamageRadiusForFruitCapture() + kSprayInteractPadding;
    const f32 distSq = dx * dx + dy * dy + dz * dz;
    const f32 speedSq = fruitSpeedSq(obj);

    if (watch->cooldown > 0)
        --watch->cooldown;

    if (distSq <= interactR * interactR && watch->cooldown == 0 &&
        speedSq >= kSprayKickDetectMinSpeedSq &&
        speedSq >= watch->lastSpeedSq + kSprayKickDetectDeltaSq) {
        tryCaptureFruitKicked(obj);
        watch->cooldown = 2;
    }

    watch->lastSpeedSq = speedSq;
}

static TVec3f *gpMarioPosPtr() {
    return reinterpret_cast<TVec3f *>(SMS_PORT_REGION(0x8040E10C, 0x804057D4, 0, 0));
}

static f32 *gpMarioSpeedYPtr() {
    return reinterpret_cast<f32 *>(SMS_PORT_REGION(0x8040E120, 0x804057E8, 0, 0));
}

static f32 *gpMarioSpeedXPtr() {
    return reinterpret_cast<f32 *>(SMS_PORT_REGION(0x8040E11C, 0x804057E4, 0, 0));
}

static f32 *gpMarioSpeedZPtr() {
    return reinterpret_cast<f32 *>(SMS_PORT_REGION(0x8040E124, 0x804057EC, 0, 0));
}

static f32 *gpMarioThrowPowerPtr() {
    return reinterpret_cast<f32 *>(SMS_PORT_REGION(0x8040E130, 0x804057F8, 0, 0));
}

static f32 *mNormalThrowSpeedRatePtr() {
    return reinterpret_cast<f32 *>(SMS_PORT_REGION(0x8040C78C, 0x80403EEC, 0, 0));
}

static s16 holderThrowAngleY(const TMario *holder) {
    if (!holder)
        return 0;
    return holder->mModelAngleY;
}

static const FruitMapObjData *fruitMapObjData(const TMapObjBase *fruit) {
    if (!fruit)
        return nullptr;
    return reinterpret_cast<const FruitMapObjData *>(
        reinterpret_cast<const char *>(fruit) + 0x130);
}

static u32 packFruitThrowVelocity(f32 vx, f32 vy, f32 vz) {
    auto enc = [](f32 v) -> u32 {
        s32 e = static_cast<s32>(v / kFruitThrowVelPackScale + static_cast<f32>(kFruitThrowVelPackBias));
        if (e < 0)
            e = 0;
        if (e > 1023)
            e = 1023;
        return static_cast<u32>(e);
    };
    return kFruitThrowVelPayloadTag | enc(vx) | (enc(vy) << 10) | (enc(vz) << 20);
}

static bool unpackFruitThrowVelocity(u32 packed, f32 &vx, f32 &vy, f32 &vz) {
    if ((packed & kFruitThrowVelPayloadTag) == 0)
        return false;

    auto dec = [](u32 bits) -> f32 {
        return (static_cast<f32>(bits & 0x3FFu) - static_cast<f32>(kFruitThrowVelPackBias)) *
               kFruitThrowVelPackScale;
    };
    vx = dec(packed);
    vy = dec(packed >> 10);
    vz = dec(packed >> 20);
    return true;
}

static bool isFruitThrowVelocityPayload(u32 packed) {
    return (packed & kFruitThrowVelPayloadTag) != 0;
}

static u32 *mapObjLiveFlags(TLiveActor *live) {
    return reinterpret_cast<u32 *>(reinterpret_cast<char *>(live) + 0xF0);
}

static void armFruitFlight(TLiveActor *live) {
    u32 *flags = mapObjLiveFlags(live);
    *flags &= ~kLiveFlagUnk10;
    *flags |= kLiveFlagAirborne;
}

static void restFruitOnGround(TLiveActor *live) {
    u32 *flags = mapObjLiveFlags(live);
    *flags &= ~kLiveFlagAirborne;
    *flags |= kLiveFlagUnk10;
}

static void applyAuthoritativeFruitState(TMapObjBase *fruit, const TVec3f &pos, f32 vx, f32 vy, f32 vz,
                                         bool airborneHint) {
    if (!fruit)
        return;

    auto *live = reinterpret_cast<TLiveActor *>(fruit);
    fruit->changeObjSRT(pos, fruit->mInitialRotation, TVec3f{1.0f, 1.0f, 1.0f});
    live->mSpeed.set(vx, vy, vz);

    const f32 speedSq = vx * vx + vy * vy + vz * vz;
    const bool airborne = airborneHint || speedSq >= kFruitAirborneMinSpeedSq;
    if (airborne) {
        fruit->mState = kFruitThrownState;
        armFruitFlight(live);
    } else {
        fruit->mState = kFruitFreeState;
        restFruitOnGround(live);
    }
    fruit->awake();
}

static void setFruitThrowVelocityFallback(TMapObjBase *fruit, TLiveActor *live, const TMario *holder,
                                          f32 throwPower) {
    const s16 angleY = holderThrowAngleY(holder);
    f32 horizMul = 40.0f;
    f32 vertSpeed = 15.0f;

    const FruitMapObjData *data = fruitMapObjData(fruit);
    if (data && data->mPhysical && data->mPhysical->unk4) {
        horizMul = data->mPhysical->unk4->unk2C;
        vertSpeed = data->mPhysical->unk4->unk30;
    }

    const f32 sinY = sJmasSin ? sJmasSin(angleY) : sinf(static_cast<f32>(angleY) / 182.04445f);
    const f32 cosY = sJmasCos ? sJmasCos(angleY) : cosf(static_cast<f32>(angleY) / 182.04445f);
    const f32 marioSpeedX = holder ? holder->mSpeed.x : 0.0f;
    const f32 marioSpeedZ = holder ? holder->mSpeed.z : 0.0f;
    const f32 throwRate = *mNormalThrowSpeedRatePtr();

    live->mSpeed.x = sinY * throwPower * horizMul + throwRate * marioSpeedX;
    live->mSpeed.y = vertSpeed;
    live->mSpeed.z = cosY * throwPower * horizMul + throwRate * marioSpeedZ;
}

static void clearPendingFruitRelease() {
    sPendingRelease.fruit = nullptr;
    sPendingRelease.framesLeft = 0;
}

static void schedulePendingFruitRelease(TMapObjBase *obj) {
    sPendingRelease.fruit = obj;
    sPendingRelease.framesLeft = kPendingReleaseFrames;
}

static u8 encodeFruitActorType(u32 actorType) {
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
    case kDurianActorType:
        return 8;
    default:
        return 0;
    }
}

static u32 decodeFruitActorType(u8 enc) {
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
    case 8:
        return kDurianActorType;
    default:
        return 0;
    }
}

struct FindFruitCtx {
    u32 actorType;
    TVec3f target;
    f32 maxDistSq;
    TMapObjBase *best;
    f32 bestDistSq;
};

static bool visitFindFruit(TMapObjBase *obj, void *rawCtx) {
    auto *ctx = reinterpret_cast<FindFruitCtx *>(rawCtx);
    if (!isFruitCandidateForApply(obj, ctx->actorType))
        return false;

    const TVec3f pos = mapObjWorldPos(obj);
    const f32 dx = pos.x - ctx->target.x;
    const f32 dy = pos.y - ctx->target.y;
    const f32 dz = pos.z - ctx->target.z;
    const f32 distSq = dx * dx + dy * dy + dz * dz;
    if (distSq > ctx->maxDistSq)
        return false;

    if (!ctx->best || distSq < ctx->bestDistSq) {
        ctx->best = obj;
        ctx->bestDistSq = distSq;
    }
    return false;
}

static TMapObjBase *findFruitNear(const TVec3f &pos, u32 actorType, f32 maxDistSq) {
    FindFruitCtx ctx = {actorType, pos, maxDistSq, nullptr, maxDistSq};
    smso::forEachManagedMapObj(visitFindFruit, &ctx);
    return ctx.best;
}

static TMapObjBase *findFruitForRemoteApply(const TVec3f &target, u32 actorType, u8 actorSlot,
                                              smso::WorldEventType type) {
    const bool carryEvent =
        type == smso::WE_MARIO_FRUIT_THROWN || type == smso::WE_MARIO_FRUIT_DROPPED;
    if (carryEvent) {
        TMapObjBase *carried = sRemoteCarriedFruit[actorSlot];
        if (carried && isFruitCandidateForApply(carried, actorType))
            return carried;
    }

    TMapObjBase *fruit = findFruitNear(target, actorType, kFruitMatchRadiusSq);
    if (fruit)
        return fruit;

    if (type == smso::WE_MARIO_FRUIT_KICKED || type == smso::WE_MARIO_FRUIT_PICKED)
        return findFruitNear(target, actorType, kFruitMatchRadiusExpandedSq);

    return nullptr;
}

static bool fruitObjectSyncEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0 &&
           (buf->bridgeFlags & smso::BF_SYNC_OBJECTS) != 0;
}

static const char *fruitEventName(smso::WorldEventType type) {
    switch (type) {
    case smso::WE_MARIO_FRUIT_KICKED:
        return "kicked";
    case smso::WE_MARIO_FRUIT_PICKED:
        return "picked";
    case smso::WE_MARIO_FRUIT_THROWN:
        return "thrown";
    case smso::WE_MARIO_FRUIT_DROPPED:
        return "dropped";
    case smso::WE_MARIO_FRUIT_SYNC:
        return "sync";
    default:
        return "?";
    }
}

static void publishFruitAuthoritativeSync(u8 fruitEnc, u8 slot, const TMapObjBase *fruit) {
    if (!fruit || sApplyingRemoteFruitEvent)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!fruitObjectSyncEnabled(buf))
        return;

    const TVec3f pos = mapObjWorldPos(fruit);
    const u32 packedPos = smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
    if (!smso::isValidPackedWorldPos(packedPos))
        return;

    const auto *live = reinterpret_cast<const TLiveActor *>(fruit);
    const u32 packedVel =
        packFruitThrowVelocity(live->mSpeed.x, live->mSpeed.y, live->mSpeed.z);

    FlyingFruitTrack *track = flyingFruitTrackFor(const_cast<TMapObjBase *>(fruit));
    if (track && track->fruit == fruit && track->lastPackedPos == packedPos &&
        track->lastPackedVel == packedVel)
        return;

    if (track && track->fruit == fruit) {
        track->lastPackedPos = packedPos;
        track->lastPackedVel = packedVel;
    }

    const u8 courseId = gpMarDirector ? gpMarDirector->mAreaID : 0;
    const u8 episodeId = gpMarDirector ? gpMarDirector->mEpisodeID : 0;
    smso::enqueueLocalWorldEvent(static_cast<u8>(smso::WE_MARIO_FRUIT_SYNC), courseId, episodeId,
                                 fruitEnc, slot, packedPos, packedVel);
}

static void reconcileFlyingFruitSync() {
    for (u32 i = 0; i < kMaxFlyingFruitTrack; ++i) {
        FlyingFruitTrack &track = sFlyingFruitTrack[i];
        TMapObjBase *fruit = track.fruit;
        if (!fruit) {
            track = {};
            continue;
        }

        if (!isLiveFruit(fruit)) {
            track = {};
            continue;
        }

        if (fruit->mHolder) {
            track = {};
            continue;
        }

        const f32 speedSq = fruitSpeedSq(fruit);
        const auto *live = reinterpret_cast<const TLiveActor *>(fruit);
        const u32 liveFlags = *mapObjLiveFlags(const_cast<TLiveActor *>(live));
        const bool airborne = (liveFlags & kLiveFlagAirborne) != 0;
        if (!airborne && speedSq < kFruitAirborneMinSpeedSq) {
            track = {};
            continue;
        }

        if (track.framesUntilSync > 0) {
            --track.framesUntilSync;
            continue;
        }
        track.framesUntilSync = kFruitAirborneSyncInterval;

        const TVec3f pos = mapObjWorldPos(fruit);
        const u32 packedPos = smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
        const u32 packedVel =
            packFruitThrowVelocity(live->mSpeed.x, live->mSpeed.y, live->mSpeed.z);

        if (packedPos == track.lastPackedPos && packedVel == track.lastPackedVel)
            continue;

        track.lastPackedPos = packedPos;
        track.lastPackedVel = packedVel;
        publishFruitAuthoritativeSync(track.fruitEnc, track.moverSlot, fruit);
    }
}

static void publishMarioFruitEvent(smso::WorldEventType type, u8 fruitEnc, const TVec3f &pos,
                                     u8 slot, const TMapObjBase *velocitySource = nullptr) {
    if (sApplyingRemoteFruitEvent)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!fruitObjectSyncEnabled(buf))
        return;

    const u32 packedPos = smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
    if (!smso::isValidPackedWorldPos(packedPos))
        return;

    u32 packedVel = 0;
    if ((type == smso::WE_MARIO_FRUIT_THROWN || type == smso::WE_MARIO_FRUIT_KICKED) &&
        velocitySource) {
        const auto *live = reinterpret_cast<const TLiveActor *>(velocitySource);
        f32 vx = live->mSpeed.x;
        f32 vy = live->mSpeed.y;
        f32 vz = live->mSpeed.z;
        const f32 speedSq = vx * vx + vy * vy + vz * vz;

        if (type == smso::WE_MARIO_FRUIT_KICKED && isDurian(velocitySource) &&
            speedSq < kDurianKickDetectMinSpeedSq)
            computeDurianKickVelocityFromMario(gpMarioAddress, pos, vx, vy, vz);

        packedVel = packFruitThrowVelocity(vx, vy, vz);
    }

    const u32 eventId = (static_cast<u32>(type) << 24) | (static_cast<u32>(fruitEnc) << 16) |
                        ((packedPos ^ packedVel) & 0xFFFFu);
    if (eventId == sLastLocalFruitEventId)
        return;
    sLastLocalFruitEventId = eventId;

    const u8 courseId = gpMarDirector ? gpMarDirector->mAreaID : 0;
    const u8 episodeId = gpMarDirector ? gpMarDirector->mEpisodeID : 0;
    smso::enqueueLocalWorldEvent(static_cast<u8>(type), courseId, episodeId, fruitEnc, slot,
                                 packedPos, packedVel);

    if (kFruitHotPathOsReport &&
        (type == smso::WE_MARIO_FRUIT_THROWN || type == smso::WE_MARIO_FRUIT_KICKED) &&
        isFruitThrowVelocityPayload(packedVel)) {
        f32 vx = 0.0f;
        f32 vy = 0.0f;
        f32 vz = 0.0f;
        unpackFruitThrowVelocity(packedVel, vx, vy, vz);
        OSReport("[SMSOBB] mario-fruit publish %s enc=%u slot=%u pos=(%.0f,%.0f,%.0f) vel=(%.1f,%.1f,%.1f)\n",
                 fruitEventName(type), fruitEnc, slot, pos.x, pos.y, pos.z, vx, vy, vz);
    } else if (kFruitHotPathOsReport) {
        OSReport("[SMSOBB] mario-fruit publish %s enc=%u slot=%u pos=(%.0f,%.0f,%.0f)\n",
                 fruitEventName(type), fruitEnc, slot, pos.x, pos.y, pos.z);
    }

    if (type == smso::WE_MARIO_FRUIT_THROWN)
        sDropSuppressFrames = 4;

    if (type == smso::WE_MARIO_FRUIT_KICKED || type == smso::WE_MARIO_FRUIT_THROWN) {
        if (velocitySource)
            trackFlyingFruit(const_cast<TMapObjBase *>(velocitySource), fruitEnc, slot);
    } else if (type == smso::WE_MARIO_FRUIT_PICKED || type == smso::WE_MARIO_FRUIT_DROPPED) {
        if (velocitySource)
            untrackFlyingFruit(const_cast<TMapObjBase *>(velocitySource));
    }
}

static u8 localSlotOrZero() {
    smso::CommBuffer *buf = smso::getCommBuffer();
    return buf ? buf->localSlot : 0;
}

static u8 fruitEncForObj(const TMapObjBase *obj) {
    return encodeFruitActorType(reinterpret_cast<const THitActor *>(obj)->mObjectID);
}

static void tryCaptureFruitPicked(TMapObjBase *obj) {
    if (!obj || sApplyingRemoteFruitEvent)
        return;
    if (!TMapObjBase::isFruit(reinterpret_cast<THitActor *>(obj)))
        return;

    const TVec3f pos = mapObjWorldPos(obj);
    publishMarioFruitEvent(smso::WE_MARIO_FRUIT_PICKED, fruitEncForObj(obj), pos,
                           localSlotOrZero(), obj);
    sLastLocalHeldFruit = reinterpret_cast<THitActor *>(obj);
}

static void tryCaptureFruitThrown(TMapObjBase *obj) {
    if (!obj || sApplyingRemoteFruitEvent)
        return;
    if (!TMapObjBase::isFruit(reinterpret_cast<THitActor *>(obj)))
        return;

    clearPendingFruitRelease();

    const TVec3f pos = mapObjWorldPos(obj);
    publishMarioFruitEvent(smso::WE_MARIO_FRUIT_THROWN, fruitEncForObj(obj), pos,
                           localSlotOrZero(), obj);
    sLastLocalHeldFruit = nullptr;
}

static void resolvePendingFruitRelease() {
    if (!sPendingRelease.fruit || sPendingRelease.framesLeft == 0)
        return;

    --sPendingRelease.framesLeft;
    if (sPendingRelease.framesLeft != 0)
        return;

    TMapObjBase *obj = sPendingRelease.fruit;
    sPendingRelease.fruit = nullptr;
    if (!obj || !isLiveFruit(obj) || sApplyingRemoteFruitEvent)
        return;

    const TVec3f pos = mapObjWorldPos(obj);
    const f32 speedSq = fruitSpeedSq(obj);
    const TMario *mario = gpMarioAddress;
    const bool highSpeed = speedSq >= kThrowSpeedThresholdSq;
    const bool airborne =
        mario != nullptr && pos.y > mario->mTranslation.y + kThrowHeightAboveMario;

    if (highSpeed || airborne)
        tryCaptureFruitThrown(obj);
    else
        publishMarioFruitEvent(smso::WE_MARIO_FRUIT_DROPPED, fruitEncForObj(obj), pos,
                               localSlotOrZero(), obj);
}

static void tryCaptureFruitKicked(TMapObjBase *obj) {
    if (!obj || sApplyingRemoteFruitEvent)
        return;
    if (!isMarioSyncFruit(reinterpret_cast<THitActor *>(obj)))
        return;

    const TVec3f pos = mapObjWorldPos(obj);
    publishMarioFruitEvent(smso::WE_MARIO_FRUIT_KICKED, fruitEncForObj(obj), pos,
                           localSlotOrZero(), obj);
}

static void tryCaptureLocalMarioFruitMessageAfter(TMapObjBase *obj, THitActor *sender, u32 msg) {
    if (!obj || sApplyingRemoteFruitEvent)
        return;
    if (!isMarioSyncFruit(reinterpret_cast<THitActor *>(obj)))
        return;

    const bool fromLocalMario =
        sender && sender == reinterpret_cast<THitActor *>(gpMarioAddress);

    if (msg == kHitMessageTake && fromLocalMario) {
        if (TMapObjBase::isFruit(reinterpret_cast<THitActor *>(obj)))
            tryCaptureFruitPicked(obj);
        return;
    }

    if (msg == kHitMessageThrow && fromLocalMario) {
        if (TMapObjBase::isFruit(reinterpret_cast<THitActor *>(obj)))
            tryCaptureFruitThrown(obj);
        return;
    }

    if (msg == kHitMessageSprayedByWater) {
        if (localMarioWaterGunActive(gpMarioAddress))
            tryCaptureFruitKicked(obj);
        return;
    }
}

static ReceiveMessageFn lookupReceiveMessageOrig(u32 vtable) {
    for (u32 i = 0; i < sVtReceiveHookCount; ++i) {
        if (sVtReceiveHooks[i].vtable == vtable)
            return sVtReceiveHooks[i].orig;
    }
    return nullptr;
}

static bool smso_fruit_receiveMessage_captureHook(THitActor *self, THitActor *sender, u32 msg) {
    const ReceiveMessageFn orig = lookupReceiveMessageOrig(*reinterpret_cast<const u32 *>(self));
    const bool result = orig ? orig(self, sender, msg) : false;
    tryCaptureLocalMarioFruitMessageAfter(reinterpret_cast<TMapObjBase *>(self), sender, msg);
    return result;
}

static u32 findVtSlotForFn(u32 vtable, u32 fn) {
    auto resolveBranchTarget = [](u32 entry) -> u32 {
        if (entry < 0x80000000 || entry >= 0x81800000)
            return 0;
        const u32 branch = *reinterpret_cast<const u32 *>(entry);
        if ((branch >> 26) != 18)
            return 0;
        s32 imm = static_cast<s32>(branch & 0x03FFFFFC);
        if (imm & 0x02000000)
            imm -= 0x04000000;
        return static_cast<u32>(entry + imm);
    };

    for (u32 off = 0x1C; off <= 0xC0; off += 4) {
        const u32 entry = *reinterpret_cast<const u32 *>(vtable + off);
        if (entry == fn)
            return off;

        const u32 direct = resolveBranchTarget(entry);
        if (direct == fn)
            return off;

        if (entry < 0x80000000 || entry >= 0x81800000)
            continue;

        const u32 skip = *reinterpret_cast<const u32 *>(entry);
        if ((skip >> 26) == 18 && (skip & 0x03FFFFFC) == 8) {
            const u32 inner = resolveBranchTarget(entry + 8);
            if (inner == fn)
                return off;
        }
    }
    return 0;
}

static void registerFruitReceiveMessageHook(u32 vtable, u32 origFn) {
    if (vtable == 0 || origFn == 0 || sVtReceiveHookCount >= kMaxVtReceiveHooks)
        return;

    for (u32 i = 0; i < sVtReceiveHookCount; ++i) {
        if (sVtReceiveHooks[i].vtable == vtable)
            return;
    }

    u32 off = findVtSlotForFn(vtable, origFn);
    if (off == 0)
        off = kVtReceiveMessageOffset;

    u32 *slot = reinterpret_cast<u32 *>(vtable + off);
    sVtReceiveHooks[sVtReceiveHookCount++] = {vtable,
                                              reinterpret_cast<ReceiveMessageFn>(*slot)};
    BetterSMS::PowerPC::writeU32(slot, reinterpret_cast<u32>(&smso_fruit_receiveMessage_captureHook));
}

struct PollFruitCtx {
    TMario *mario;
};

static bool visitPollLocalFruit(TMapObjBase *obj, void *rawCtx) {
    auto *ctx = reinterpret_cast<PollFruitCtx *>(rawCtx);
    if (!ctx->mario || sApplyingRemoteFruitEvent)
        return false;
    if (!isLiveFruit(obj))
        return false;

    pollFruitKickCapture(ctx->mario, obj);
    pollFruitSprayCapture(ctx->mario, obj);

    auto *hit = reinterpret_cast<THitActor *>(obj);
    if (!TMapObjBase::isFruit(hit))
        return false;

    THitActor *holder = obj->mHolder;
    const bool heldByLocal = holder == reinterpret_cast<THitActor *>(ctx->mario);

    if (heldByLocal && sLastLocalHeldFruit != hit) {
        tryCaptureFruitPicked(obj);
        return false;
    }

    if (!heldByLocal && sLastLocalHeldFruit == hit && sDropSuppressFrames == 0) {
        schedulePendingFruitRelease(obj);
        sLastLocalHeldFruit = nullptr;
    }

    return false;
}

static TVec3f snapFruitDropToGround(TMapObjBase *fruit, const TVec3f &dropPos) {
    TVec3f pos = dropPos;
    if (!gpMap)
        return pos;

    f32 restLift = 45.0f;
    if (sMapObjGetRadiusAtY && fruit) {
        const f32 radius = sMapObjGetRadiusAtY(fruit, 0.0f);
        if (radius > 1.0f && radius < 500.0f)
            restLift = radius + 2.0f;
    } else if (fruit && isDurian(fruit)) {
        restLift = 80.0f;
    }

    const TBGCheckData *plane = nullptr;
    f32 bestGround = -1.0e9f;
    const f32 probes[] = {kFruitGroundProbeLift, 200.0f, 400.0f, 0.0f};
    for (f32 probe : probes) {
        const f32 groundY = gpMap->checkGround(pos.x, pos.y + probe, pos.z, &plane);
        if (groundY > -1.0e8f && groundY > bestGround)
            bestGround = groundY;
    }

    if (bestGround > -1.0e8f)
        pos.y = bestGround + restLift;
    return pos;
}

static void spoofGpMarioForFruitReplay(const TMario *mario, TVec3f *savedPos, f32 *savedSpeedX,
                                       f32 *savedSpeedY, f32 *savedSpeedZ) {
    TVec3f *gpPos = gpMarioPosPtr();
    f32 *gpSpeedX = gpMarioSpeedXPtr();
    f32 *gpSpeedY = gpMarioSpeedYPtr();
    f32 *gpSpeedZ = gpMarioSpeedZPtr();

    *savedPos = *gpPos;
    *savedSpeedX = *gpSpeedX;
    *savedSpeedY = *gpSpeedY;
    *savedSpeedZ = *gpSpeedZ;

    *gpSpeedY = mario->mSpeed.y > 1.0f ? mario->mSpeed.y : 200.0f;
    *gpSpeedX = mario->mSpeed.x;
    *gpSpeedZ = mario->mSpeed.z;
    *gpPos = mario->mTranslation;
}

static void restoreGpMarioAfterFruitReplay(const TVec3f &savedPos, f32 savedSpeedX,
                                           f32 savedSpeedY, f32 savedSpeedZ) {
    *gpMarioSpeedYPtr() = savedSpeedY;
    *gpMarioSpeedXPtr() = savedSpeedX;
    *gpMarioSpeedZPtr() = savedSpeedZ;
    *gpMarioPosPtr() = savedPos;
}

static void replayFruitDurianBump(TMapObjBase *fruit, TMario *kicker, const TVec3f &kickPos, f32 vx,
                                  f32 vy, f32 vz, bool hasPackedVelocity) {
    if (!fruit)
        return;

    auto *live = reinterpret_cast<TLiveActor *>(fruit);
    if (hasPackedVelocity) {
        applyAuthoritativeFruitState(fruit, kickPos, vx, vy, vz, true);
        return;
    }

    fruit->changeObjSRT(kickPos, fruit->mInitialRotation, TVec3f{1.0f, 1.0f, 1.0f});

    TMario *mario = kicker;
    if (!mario || !sMapObjBallBoundByActorReplay) {
        computeDurianKickVelocityFromMario(kicker, kickPos, vx, vy, vz);
        applyAuthoritativeFruitState(fruit, kickPos, vx, vy, vz, true);
        return;
    }

    TVec3f savedPos{};
    f32 savedSpeedX = 0.0f;
    f32 savedSpeedY = 0.0f;
    f32 savedSpeedZ = 0.0f;
    spoofGpMarioForFruitReplay(mario, &savedPos, &savedSpeedX, &savedSpeedY, &savedSpeedZ);
    sMapObjBallBoundByActorReplay(fruit, reinterpret_cast<THitActor *>(gpMarioAddress));
    restoreGpMarioAfterFruitReplay(savedPos, savedSpeedX, savedSpeedY, savedSpeedZ);

    const f32 replaySpeedSq = fruitSpeedSq(fruit);
    if (replaySpeedSq >= kDurianKickDetectMinSpeedSq) {
        applyAuthoritativeFruitState(fruit, kickPos, live->mSpeed.x, live->mSpeed.y, live->mSpeed.z,
                                     true);
        return;
    }

    computeDurianKickVelocityFromMario(kicker, kickPos, vx, vy, vz);
    applyAuthoritativeFruitState(fruit, kickPos, vx, vy, vz, true);
}

static void replayFruitKick(TMapObjBase *fruit, TMario *kicker, const TVec3f &kickPos, f32 vx,
                            f32 vy, f32 vz, bool hasPackedVelocity) {
    if (!fruit)
        return;

    if (isDurian(fruit)) {
        replayFruitDurianBump(fruit, kicker, kickPos, vx, vy, vz, hasPackedVelocity);
        return;
    }

    auto *live = reinterpret_cast<TLiveActor *>(fruit);
    if (hasPackedVelocity) {
        applyAuthoritativeFruitState(fruit, kickPos, vx, vy, vz, true);
        return;
    }

    fruit->changeObjSRT(kickPos, fruit->mInitialRotation, TVec3f{1.0f, 1.0f, 1.0f});

    TMario *mario = kicker;
    if (!mario)
        return;

    TVec3f savedPos{};
    f32 savedSpeedX = 0.0f;
    f32 savedSpeedY = 0.0f;
    f32 savedSpeedZ = 0.0f;
    spoofGpMarioForFruitReplay(mario, &savedPos, &savedSpeedX, &savedSpeedY, &savedSpeedZ);

    const u32 vt = *reinterpret_cast<const u32 *>(fruit);
    if (vt == gTResetFruitVt && sResetFruitKickedReplay)
        sResetFruitKickedReplay(fruit);
    else if (vt == gTMapObjBallVt && sMapObjBallKickedReplay)
        sMapObjBallKickedReplay(fruit);

    restoreGpMarioAfterFruitReplay(savedPos, savedSpeedX, savedSpeedY, savedSpeedZ);
    applyAuthoritativeFruitState(fruit, kickPos, live->mSpeed.x, live->mSpeed.y, live->mSpeed.z, true);
}

static void linkFruitCarryPointers(TMapObjBase *fruit, TMario *holder) {
    if (!fruit || !holder)
        return;
    holder->mHeldObject = reinterpret_cast<TTakeActor *>(fruit);
    fruit->mHolder = holder;
}

static void replayFruitHold(TMapObjBase *fruit, TMario *holder) {
    if (!fruit || !holder)
        return;

    const u32 vt = *reinterpret_cast<const u32 *>(fruit);
    if (vt == gTResetFruitVt && sResetFruitHoldReplay)
        sResetFruitHoldReplay(fruit, holder);
    else if (vt == gTMapObjBallVt && sMapObjBallHoldReplay)
        sMapObjBallHoldReplay(fruit, holder);
    else if (sMapObjGeneralHoldReplay)
        sMapObjGeneralHoldReplay(fruit, holder);

    linkFruitCarryPointers(fruit, holder);
}

static void unlinkFruitCarry(TMapObjBase *fruit, TMario *holder) {
    if (!fruit)
        return;

    if (holder) {
        if (holder->mHeldObject == reinterpret_cast<TTakeActor *>(fruit))
            holder->mHeldObject = nullptr;
        if (fruit->mHolder == holder)
            fruit->mHolder = nullptr;
        return;
    }

    // Remote puppet missing — break carry on the fruit without touching Mario memory.
    fruit->mHolder = nullptr;
}

static void syncFruitCarryPosition(TMapObjBase *fruit, TMario *holder) {
    if (!fruit || !holder || fruit->mHolder != holder)
        return;

    const f32 *mtx = reinterpret_cast<const f32 *>(fruit->getTakingMtx());
    if (!mtx)
        return;

    const TVec3f pos = {mtx[3], mtx[7], mtx[11]};
    fruit->changeObjSRT(pos, fruit->mInitialRotation, TVec3f{1.0f, 1.0f, 1.0f});
}

static void replayFruitThrow(TMapObjBase *fruit, TMario *holder, const TVec3f &releasePos, f32 vx,
                             f32 vy, f32 vz, bool hasPackedVelocity) {
    if (!fruit)
        return;

    unlinkFruitCarry(fruit, holder);

    auto *live = reinterpret_cast<TLiveActor *>(fruit);
    TVec3f throwPos = releasePos;
    if (hasPackedVelocity) {
        applyAuthoritativeFruitState(fruit, throwPos, vx, vy, vz, true);
        return;
    }

    fruit->changeObjSRT(throwPos, fruit->mInitialRotation, TVec3f{1.0f, 1.0f, 1.0f});
    {
        f32 throwPower = kDefaultThrowPower;
        if (holder == gpMarioAddress)
            throwPower = *gpMarioThrowPowerPtr();
        setFruitThrowVelocityFallback(fruit, live, holder, throwPower);
        throwPos.x += live->mSpeed.x;
        throwPos.y += live->mSpeed.y;
        throwPos.z += live->mSpeed.z;
    }

    applyAuthoritativeFruitState(fruit, throwPos, live->mSpeed.x, live->mSpeed.y, live->mSpeed.z,
                                 true);
}

static void replayFruitDrop(TMapObjBase *fruit, TMario *holder, const TVec3f &dropPos) {
    if (!fruit)
        return;

    unlinkFruitCarry(fruit, holder);

    auto *live = reinterpret_cast<TLiveActor *>(fruit);
    const TVec3f groundedPos = snapFruitDropToGround(fruit, dropPos);
    fruit->changeObjSRT(groundedPos, fruit->mInitialRotation, TVec3f{1.0f, 1.0f, 1.0f});
    live->mSpeed.set(0.0f, 0.0f, 0.0f);
    restFruitOnGround(live);
    fruit->mState = kFruitFreeState;
    fruit->awake();
}

} // namespace

namespace smso {

void initFruitSync() {
    sResetFruitKickedReplay = reinterpret_cast<FruitKickedFn>(
        SMS_PORT_REGION(0x801E2C00, 0x801DAAD8, 0, 0));
    sResetFruitHoldReplay = reinterpret_cast<FruitHoldFn>(
        SMS_PORT_REGION(0x801E3500, 0x801DB3D8, 0, 0));
    sMapObjBallKickedReplay = reinterpret_cast<FruitKickedFn>(
        SMS_PORT_REGION(0x801E4FC8, 0x801DCEA0, 0, 0));
    sMapObjBallHoldReplay = reinterpret_cast<FruitHoldFn>(
        SMS_PORT_REGION(0x801E51E4, 0x801DD0BC, 0, 0));
    sMapObjBallBoundByActorReplay = reinterpret_cast<BoundByActorFn>(
        SMS_PORT_REGION(0x801E4974, 0x801DC84C, 0, 0));
    sMapObjGetRadiusAtY = reinterpret_cast<GetRadiusAtYFn>(
        SMS_PORT_REGION(0x800C6E94, 0x800C0534, 0, 0));
    sMapObjGeneralHoldReplay = reinterpret_cast<FruitHoldFn>(
        SMS_PORT_REGION(0x801B4200, 0x801ABF8C, 0, 0));
    sJmasSin = reinterpret_cast<SinCosFn>(SMS_PORT_REGION(0x8002D758, 0x8002D810, 0, 0));
    sJmasCos = reinterpret_cast<SinCosFn>(SMS_PORT_REGION(0x8002D73C, 0x8002D7F4, 0, 0));

    gTResetFruitVt = SMS_PORT_REGION(0x803D2BA4, 0x803CA394, 0, 0);
    gTMapObjBallVt = SMS_PORT_REGION(0x803D2D94, 0x803CA584, 0, 0);
}

void ensureMarioFruitHooks() {
    if (sFruitHooksReady)
        return;

    registerFruitReceiveMessageHook(gTResetFruitVt,
                                    SMS_PORT_REGION(0x801E1CA0, 0x801D9B78, 0, 0));
    registerFruitReceiveMessageHook(gTMapObjBallVt,
                                    SMS_PORT_REGION(0x801E3FBC, 0x801DBE94, 0, 0));

    // Do not patch touchActor/kicked/hold/thrown vtable slots: SMS uses indirect thunks and
    // guessed fallback offsets corrupt retail dispatch (title hang / pickup crash).
    // Local durian capture uses polling; remote viewers use shadow hitbox proxy.

    sFruitHooksReady = true;
    OSReport("[SMSOBB] mario-fruit hooks installed (receive=%u poll=1)\n", sVtReceiveHookCount);
}

void updateLocalMarioFruitCapture(TMario *mario) {
    if (!mario || sApplyingRemoteFruitEvent)
        return;

    if (sDropSuppressFrames > 0)
        --sDropSuppressFrames;

    resolvePendingFruitRelease();

    PollFruitCtx ctx = {mario};
    smso::forEachManagedMapObj(visitPollLocalFruit, &ctx);

    reconcileFlyingFruitSync();

    THitActor *held = mario->mHeldObject;
    if (held && TMapObjBase::isFruit(held))
        sLastLocalHeldFruit = held;
    else if (!sLastLocalHeldFruit)
        sLastLocalHeldFruit = nullptr;
}

TTakeActor *getRemoteCarriedFruitActor(u8 slot) {
    if (slot >= smso::MAX_REMOTE_SLOTS)
        return nullptr;
    TMapObjBase *fruit = sRemoteCarriedFruit[slot];
    if (!fruit || !isLiveFruit(fruit))
        return nullptr;
    return reinterpret_cast<TTakeActor *>(fruit);
}

void clearRemoteCarriedFruit(u8 slot) {
    if (slot >= smso::MAX_REMOTE_SLOTS)
        return;
    TMapObjBase *fruit = sRemoteCarriedFruit[slot];
    if (fruit) {
        unlinkFruitCarry(fruit, nullptr);
        sRemoteCarriedFruit[slot] = nullptr;
    }
}

void updateRemoteCarriedFruit() {
    for (u32 slot = 0; slot < smso::MAX_REMOTE_SLOTS; ++slot) {
        TMapObjBase *fruit = sRemoteCarriedFruit[slot];
        if (!fruit || !isLiveFruit(fruit)) {
            sRemoteCarriedFruit[slot] = nullptr;
            continue;
        }

        TMario *body = getRemoteBodyForSlot(static_cast<u8>(slot));
        if (!body) {
            clearRemoteCarriedFruit(static_cast<u8>(slot));
            continue;
        }

        if (fruit->mHolder != body || body->mHeldObject != reinterpret_cast<TTakeActor *>(fruit))
            linkFruitCarryPointers(fruit, body);

        syncFruitCarryPosition(fruit, body);
    }
}

void resetFruitSyncForStage() {
    sLastLocalHeldFruit = nullptr;
    sLastLocalFruitEventId = 0;
    sDropSuppressFrames = 0;
    clearPendingFruitRelease();
    for (u32 i = 0; i < smso::MAX_REMOTE_SLOTS; ++i)
        sRemoteCarriedFruit[i] = nullptr;
    for (u32 i = 0; i < kMaxFruitKickWatch; ++i)
        sFruitKickWatch[i] = {};
    for (u32 i = 0; i < kMaxFlyingFruitTrack; ++i)
        sFlyingFruitTrack[i] = {};
    for (u32 i = 0; i < kMaxPendingRemoteFruit; ++i)
        sPendingRemoteFruit[i] = {};
}

void retryPendingRemoteFruitEvents() {
    sRetryingPendingFruitEvent = true;
    for (u32 i = 0; i < kMaxPendingRemoteFruit; ++i) {
        PendingRemoteFruitEvent &pending = sPendingRemoteFruit[i];
        if (pending.retriesLeft == 0)
            continue;

        if (applyRemoteMarioFruitWorldEvent(pending.eventType, pending.fruitEnc, pending.actorSlot,
                                            pending.packedPos, pending.packedVel)) {
            pending = {};
            continue;
        }

        if (--pending.retriesLeft == 0)
            pending = {};
    }
    sRetryingPendingFruitEvent = false;
}

bool applyRemoteMarioFruitWorldEvent(u8 eventType, u8 fruitEnc, u8 actorSlot, u32 packedPos,
                                       u32 packedVel) {
    if (actorSlot >= smso::MAX_REMOTE_SLOTS)
        return false;

    const WorldEventType type = static_cast<WorldEventType>(eventType);
    if (!smso::isValidPackedWorldPos(packedPos))
        return false;

    f32 x = 0.0f;
    f32 y = 0.0f;
    f32 z = 0.0f;
    smso::unpackCollectibleWorldPos(packedPos, x, y, z);
    const TVec3f target = {x, y, z};
    const u32 decodedType = decodeFruitActorType(fruitEnc);
    TMapObjBase *fruit = findFruitForRemoteApply(target, decodedType, actorSlot, type);
    if (!fruit) {
        OSReport("[SMSOBB] mario-fruit apply miss type=%u enc=%u slot=%u packed=0x%08X\n",
                 eventType, fruitEnc, actorSlot, packedPos);
        if (!sRetryingPendingFruitEvent)
            enqueuePendingRemoteFruitEvent(eventType, fruitEnc, actorSlot, packedPos, packedVel);
        return false;
    }

    ensureFruitAwakeForSync(fruit);

    TMario *remoteBody = getRemoteBodyForSlot(actorSlot);
    if (!remoteBody)
        remoteBody = getRemoteBodyForSlotLoose(actorSlot);

    const bool hasPackedVelocity = isFruitThrowVelocityPayload(packedVel) ||
                                   (type == WE_MARIO_FRUIT_THROWN &&
                                    isFruitThrowVelocityPayload(packedPos));
    if ((type == WE_MARIO_FRUIT_KICKED || type == WE_MARIO_FRUIT_THROWN) && !hasPackedVelocity &&
        !remoteBody) {
        if (!sRetryingPendingFruitEvent)
            enqueuePendingRemoteFruitEvent(eventType, fruitEnc, actorSlot, packedPos, packedVel);
        return false;
    }

    sApplyingRemoteFruitEvent = true;

    switch (static_cast<WorldEventType>(eventType)) {
    case WE_MARIO_FRUIT_KICKED: {
        f32 vx = 0.0f;
        f32 vy = 0.0f;
        f32 vz = 0.0f;
        bool hasPackedVelocity = false;
        if (isFruitThrowVelocityPayload(packedVel))
            hasPackedVelocity = unpackFruitThrowVelocity(packedVel, vx, vy, vz);
        replayFruitKick(fruit, remoteBody, target, vx, vy, vz, hasPackedVelocity);
        break;
    }
    case WE_MARIO_FRUIT_PICKED:
        fruit->changeObjSRT(target, fruit->mInitialRotation, TVec3f{1.0f, 1.0f, 1.0f});
        if (remoteBody)
            replayFruitHold(fruit, remoteBody);
        sRemoteCarriedFruit[actorSlot] = fruit;
        break;
    case WE_MARIO_FRUIT_THROWN: {
        f32 vx = 0.0f;
        f32 vy = 0.0f;
        f32 vz = 0.0f;
        bool hasPackedVelocity = false;
        if (isFruitThrowVelocityPayload(packedVel))
            hasPackedVelocity = unpackFruitThrowVelocity(packedVel, vx, vy, vz);
        else if (isFruitThrowVelocityPayload(packedPos))
            hasPackedVelocity = unpackFruitThrowVelocity(packedPos, vx, vy, vz);
        replayFruitThrow(fruit, remoteBody, target, vx, vy, vz, hasPackedVelocity);
        sRemoteCarriedFruit[actorSlot] = nullptr;
        break;
    }
    case WE_MARIO_FRUIT_DROPPED:
        replayFruitDrop(fruit, remoteBody, target);
        sRemoteCarriedFruit[actorSlot] = nullptr;
        break;
    default:
        sApplyingRemoteFruitEvent = false;
        return false;
    }

    OSReport("[SMSOBB] mario-fruit apply %s enc=%u slot=%u\n", fruitEventName(
                 static_cast<WorldEventType>(eventType)),
             fruitEnc, actorSlot);

    sApplyingRemoteFruitEvent = false;
    return true;
}

void deferRemoteMarioFruitWorldEvent(u8 eventType, u8 fruitEnc, u8 actorSlot, u32 packedPos,
                                     u32 packedVel) {
    enqueuePendingRemoteFruitEvent(eventType, fruitEnc, actorSlot, packedPos, packedVel);
}

bool applyRemoteMarioFruitSync(u8 fruitEnc, u8 actorSlot, u32 packedPos, u32 packedVel) {
    (void)actorSlot;
    if (!smso::isValidPackedWorldPos(packedPos))
        return false;

    f32 x = 0.0f;
    f32 y = 0.0f;
    f32 z = 0.0f;
    smso::unpackCollectibleWorldPos(packedPos, x, y, z);
    const TVec3f target = {x, y, z};
    const u32 decodedType = decodeFruitActorType(fruitEnc);
    TMapObjBase *fruit = findFruitNear(target, decodedType, kFruitMatchRadiusExpandedSq);
    if (!fruit)
        return false;

    ensureFruitAwakeForSync(fruit);

    f32 vx = 0.0f;
    f32 vy = 0.0f;
    f32 vz = 0.0f;
    const bool hasVel = unpackFruitThrowVelocity(packedVel, vx, vy, vz);
    sApplyingRemoteFruitEvent = true;
    applyAuthoritativeFruitState(fruit, target, hasVel ? vx : 0.0f, hasVel ? vy : 0.0f,
                                 hasVel ? vz : 0.0f, hasVel);
    sApplyingRemoteFruitEvent = false;
    return true;
}

} // namespace smso
