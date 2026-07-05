#include "world_sync.hpp"

#include "coin_collect_fx.hpp"
#include "collectible_scan.hpp"
#include "comm_buffer.hpp"
#include "red_coin_sync.hpp"
#include "remote_actor.hpp"
#include "yoshi_sync.hpp"

#include <SMS/GC2D/GCConsole2.hxx>
#include <SMS/Manager/FlagManager.hxx>
#include <SMS/Manager/ItemManager.hxx>
#include <SMS/Manager/ObjManager.hxx>
#include <SMS/Map/BGCheck.hxx>
#include <SMS/MapObj/MapObjBase.hxx>
#include <SMS/MoveBG/Coin.hxx>
#include <SMS/MoveBG/Shine.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Strategic/HitActor.hxx>
#include <SMS/Strategic/LiveActor.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>
#include <BetterSMS/memory.hxx>
#include <sdk.h>
#include <Dolphin/OS.h>

extern TMarDirector *gpMarDirector;
extern TItemManager *gpItemManager;
extern TMario *gpMarioAddress;

struct TMapObjManager;
extern TMapObjManager *gpMapObjManager;

namespace {

constexpr u32 kStageSettleFrames = 180;
constexpr u32 kCoinTakenFlagOffset = 0x152;
constexpr u8 kMaxStageBlueCoins = 30;
constexpr u8 kMaxStageShines = 48;
constexpr f32 kPosMatchEpsilon = 4.0f;
constexpr f32 kShinePosMatchEpsilon = 64.0f;
// 10-bit axis @ scale 16 → roughly ±4096..+12288 world units (SMS stage extents).
constexpr f32 kWorldPosPackScale = 16.0f;
constexpr f32 kWorldPosPackBias = 256.0f;
constexpr u16 kRemoteShineCollectMaxFrames = 300;

struct RemoteShineCollectState {
    u8 collectorSlot;
    TShine *shine;
    u16 frames;
    bool shrinking;
};

static RemoteShineCollectState sRemoteShineCollect = {};

struct PendingShineCapture {
    u8 shineId;
    u8 hasPos;
    u8 hasId;
    TVec3f pos;
};

static PendingShineCapture sPendingShineCapture = {};

struct StageShineEntry {
    u8 shineId;
    TVec3f initialPos;
    TVec3f livePos;
};

static StageShineEntry sStageShines[kMaxStageShines] = {};
static u8 sStageShineCount = 0;
static bool sStageShineSnapshotReady = false;
static u8 sKnownShinePosValid[128] = {};
static TVec3f sKnownShinePos[128] = {};

static bool sApplyingRemoteEvent = false;
static u16 sLocalWorldEventSequence = 0;

// Unified outbound world-event queue. Both world_sync and red_coin_sync enqueue here
// through smso::enqueueLocalWorldEvent. A single sequence counter + single consumer
// (the bridge reads one localPending slot) prevents the two-publisher sequence
// collisions and same-frame slot overwrites that previously dropped red-coin events.
constexpr u32 kLocalWorldEventQueueCap = 32;
static smso::CommWorldEvent sLocalWorldEventQueue[kLocalWorldEventQueueCap] = {};
static u8 sLocalWorldEventQueueHead = 0;
static u8 sLocalWorldEventQueueCount = 0;

static u32 sLastGoldCoinCount = 0;
static u8 sLastCourseId = 0xFF;
static u8 sLastEpisodeId = 0xFF;
static u8 sShineBits[16] = {};
static u32 sBlueCoinBits = 0;
static u16 sStageSettleFrames = 0;

static u32 gShineVtable = 0;
static u32 gCoinBlueVtable = 0;
static u32 gCoinEmptyVtable = 0;

// TMapObjChangeStageHipDrop vtable — manhole / sub-area transition pads. Excluded
// from hip-drop replay because stage transitions are owned by the stage-sync system;
// replaying the pad's receiveMessage would double-trigger the transition.
static u32 gChangeStageHipDropVtable = 0;

// doldecomp THitMessageType: HIT_MESSAGE_HIP_DROP = 1, HIT_MESSAGE_SUPER_HIP_DROP = 3.
// hipAttacking() delivers message 1 on a normal pound and both 3 + 1 on a super pound.
constexpr u32 kHitMessageHipDrop = 1;
constexpr u32 kHitMessageSuperHipDrop = 3;
constexpr u32 kHipDropSuperPayloadFlag = 0x80000000u;
constexpr f32 kHipDropMatchRadius = 96.0f;
constexpr f32 kHipDropMatchRadiusSq = kHipDropMatchRadius * kHipDropMatchRadius;
constexpr u32 kVtReceiveMessageOffset = 0x24; // __vt__6TMario receiveMessage slot

static bool sLocalHipDropFired = false;
static u32 gHipDropHideObjVtable = 0;
static u32 gBreakableBlockVtable = 0;

using ReceiveMessageFn = bool (*)(THitActor *, THitActor *, u32);
using TouchPlayerFn = void (*)(TMapObjBase *, THitActor *);

struct VtReceiveHookEntry {
    u32 vtable;
    ReceiveMessageFn orig;
};

static constexpr u32 kMaxVtReceiveHooks = 16;
static VtReceiveHookEntry sVtReceiveHooks[kMaxVtReceiveHooks] = {};
static u32 sVtReceiveHookCount = 0;
static TouchPlayerFn sOrigHipDropHideTouch = nullptr;
static TouchPlayerFn sOrigBreakableBlockTouch = nullptr;
static bool sHipDropHooksInstalled = false;

static const u32 kVtMapObjBase = SMS_PORT_REGION(0x803C2AB8, 0x803BA2A8, 0, 0);
static const u32 kFnMapObjBaseReceive =
    SMS_PORT_REGION(0x801AF944, 0x801A77FC, 0, 0);
static const u32 kVtMapObjGeneral = SMS_PORT_REGION(0x803C8B20, 0x803C0310, 0, 0);
static const u32 kFnMapObjGeneralReceive =
    SMS_PORT_REGION(0x801B305C, 0x801AAF14, 0, 0);
static const u32 kVtSuperHipDropBlock = SMS_PORT_REGION(0x803CB520, 0x803C2D10, 0, 0);
static const u32 kFnSuperHipDropReceive =
    SMS_PORT_REGION(0x801C2FF0, 0x801BAEA8, 0, 0);
static const u32 kVtBrickBlock = SMS_PORT_REGION(0x803CB958, 0x803C3148, 0, 0);
static const u32 kFnBrickBlockReceive =
    SMS_PORT_REGION(0x801C34B4, 0x801BB36C, 0, 0);
static const u32 kVtRedCoinSwitch = SMS_PORT_REGION(0x803CA6CC, 0x803C1EBC, 0, 0);
static const u32 kFnRedCoinSwitchReceive =
    SMS_PORT_REGION(0x801C0A9C, 0x801B8954, 0, 0);
static const u32 kVtBreakHideObj = SMS_PORT_REGION(0x803D7050, 0x803CE840, 0, 0);
static const u32 kFnBreakHideReceive =
    SMS_PORT_REGION(0x801FED74, 0x801F6C58, 0, 0);
static const u32 kVtHipDropHideObj = SMS_PORT_REGION(0x803D74B8, 0x803CECA8, 0, 0x803D74B8);
static const u32 kFnHipDropHideTouch =
    SMS_PORT_REGION(0x801FFE74, 0x801F7D58, 0, 0x801FFE74);
// Delfino / Ricco crates — TWoodBarrel overrides receiveMessage (delegates hip-drop to
// TMapObjGeneral::kill). Hooking TMapObjGeneral alone never sees the virtual dispatch.
static const u32 kVtWoodBarrel = SMS_PORT_REGION(0x803C28D8, 0x803BA0C8, 0, 0);
static const u32 kFnWoodBarrelReceive =
    SMS_PORT_REGION(0x801AEE24, 0x801A6CDC, 0, 0);
// Hip-breakable blocks use touchPlayer + marioHipAttack(), not receiveMessage.
static const u32 kVtBreakableBlock = SMS_PORT_REGION(0x803CBEF8, 0x803C36E8, 0, 0);
static const u32 kFnBreakableBlockTouch =
    SMS_PORT_REGION(0x801C42E4, 0x801BC19C, 0, 0);

using GetShineIdFn = s32 (*)(u32 shineStage, u32 index, bool);
using GetShineStageFn = u32 (*)(u8 areaId);

static GetShineIdFn gGetShineId = nullptr;
static GetShineStageFn gGetShineStage = nullptr;

using IncGoldCoinFlagFn = void (*)(TFlagManager *, u8, s32);
using CountShineFn = void (*)(TGCConsole2 *);
using CountBlueCoinFn = void (*)(TGCConsole2 *);

static IncGoldCoinFlagFn gIncGoldCoinFlag = nullptr;
static CountShineFn gCountShine = nullptr;
static CountBlueCoinFn gCountBlueCoin = nullptr;

static void publishLocalWorldEvent(smso::WorldEventType type, u8 courseId, u8 episodeId, u8 payload0,
                                   u8 reserved, u32 payload1);

static bool worldSyncEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0 &&
           (buf->bridgeFlags & (smso::BF_SYNC_SHINE | smso::BF_SYNC_BLUE_COIN | smso::BF_SYNC_EVENT |
                                smso::BF_SYNC_STORY | smso::BF_SYNC_MISSION | smso::BF_SYNC_SECRET |
                                smso::BF_SYNC_PROGRESS)) != 0;
}

static bool objectSyncEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0 &&
           (buf->bridgeFlags & smso::BF_SYNC_OBJECTS) != 0;
}

static bool episodeCollectibleSyncEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & (smso::BF_SYNC_EVENT | smso::BF_SYNC_SHINE)) != 0;
}

static u8 currentCourseId() {
    if (!gpMarDirector)
        return 0;
    return gpMarDirector->mAreaID;
}

static u8 currentEpisodeId() {
    if (!gpMarDirector)
        return 0;
    return gpMarDirector->mEpisodeID;
}

static bool sameStage(u8 courseId, u8 episodeId) {
    return courseId == currentCourseId() && episodeId == currentEpisodeId();
}

static bool positionsMatch(const TVec3f &a, const TVec3f &b) {
    return (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z) <=
           kPosMatchEpsilon * kPosMatchEpsilon;
}

static bool shinePositionsMatch(const TVec3f &a, const TVec3f &b) {
    return (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z) <=
           kShinePosMatchEpsilon * kShinePosMatchEpsilon;
}

static void rememberShinePosition(u8 shineId, const TVec3f &pos) {
    if (shineId >= 128)
        return;
    sKnownShinePos[shineId] = pos;
    sKnownShinePosValid[shineId] = 1;
}

static void clearKnownShinePositions() {
    for (u32 i = 0; i < sizeof(sKnownShinePosValid); ++i)
        sKnownShinePosValid[i] = 0;
}

static u32 packWorldPos(const TVec3f &pos) {
    return smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
}

static TVec3f unpackWorldPos(u32 packed) {
    f32 x = 0.0f;
    f32 y = 0.0f;
    f32 z = 0.0f;
    smso::unpackCollectibleWorldPos(packed, x, y, z);
    return TVec3f(x, y, z);
}

static void clearRemoteShineCollect() {
    sRemoteShineCollect.collectorSlot = 0xFF;
    sRemoteShineCollect.shine = nullptr;
    sRemoteShineCollect.frames = 0;
    sRemoteShineCollect.shrinking = false;
}

static bool isPendingRemoteCollectShine(const TShine *shine) {
    return shine != nullptr && sRemoteShineCollect.shine == shine;
}

static bool shouldHideShineActor(TShine *shine) {
    return shine && !isPendingRemoteCollectShine(shine);
}

static bool shineWasSet(u8 shineId) {
    return (sShineBits[shineId >> 3] & (1u << (shineId & 7))) != 0;
}

static void markShineSet(u8 shineId) {
    sShineBits[shineId >> 3] |= static_cast<u8>(1u << (shineId & 7));
}

static bool blueCoinWasSet(u8 coinIndex) {
    return (sBlueCoinBits & (1u << coinIndex)) != 0;
}

static void markBlueCoinSet(u8 coinIndex) {
    sBlueCoinBits |= 1u << coinIndex;
}

static bool isLiveCollectible(const TMapObjBase *obj) {
    if (!obj || !smso::isValidMapObjPtr(obj))
        return false;
    const auto *live = reinterpret_cast<const TLiveActor *>(obj);
    return !live->mStateFlags.asFlags.mIsObjDead;
}

static TVec3f hipDropObjWorldPos(const TMapObjBase *obj) {
    TVec3f pos = obj->mInitialPosition;
    if (obj)
        const_cast<TMapObjBase *>(obj)->JSGGetTranslation(reinterpret_cast<Vec *>(&pos));
    return pos;
}

static f32 hipDropPosDistSq(const TMapObjBase *obj, const TVec3f &target) {
    const TVec3f live = hipDropObjWorldPos(obj);
    const f32 ldx = live.x - target.x;
    const f32 ldy = live.y - target.y;
    const f32 ldz = live.z - target.z;
    const f32 liveDistSq = ldx * ldx + ldy * ldy + ldz * ldz;

    const TVec3f &initial = obj->mInitialPosition;
    const f32 idx = initial.x - target.x;
    const f32 idy = initial.y - target.y;
    const f32 idz = initial.z - target.z;
    const f32 initialDistSq = idx * idx + idy * idy + idz * idz;

    return liveDistSq < initialDistSq ? liveDistSq : initialDistSq;
}

static bool isChangeStageHipDropObj(const TMapObjBase *obj) {
    return obj != nullptr && gChangeStageHipDropVtable != 0 &&
           *reinterpret_cast<const u32 *>(obj) == gChangeStageHipDropVtable;
}

struct FindHipDropObjCtx {
    TVec3f target;
    u8 mapObjId;
    TMapObjBase *best;
    f32 bestDistSq;
    bool bestIdMatch;
};

static bool visitFindHipDropObj(TMapObjBase *obj, void *rawCtx) {
    auto *ctx = reinterpret_cast<FindHipDropObjCtx *>(rawCtx);
    if (!isLiveCollectible(obj) || isChangeStageHipDropObj(obj))
        return false;

    const f32 distSq = hipDropPosDistSq(obj, ctx->target);
    if (distSq > kHipDropMatchRadiusSq)
        return false;

    const bool idMatch =
        ctx->mapObjId != 0 && static_cast<u8>(obj->mMapObjID) == ctx->mapObjId;
    if (ctx->best == nullptr) {
        ctx->best = obj;
        ctx->bestDistSq = distSq;
        ctx->bestIdMatch = idMatch;
    } else if (idMatch && !ctx->bestIdMatch) {
        ctx->best = obj;
        ctx->bestDistSq = distSq;
        ctx->bestIdMatch = true;
    } else if (idMatch == ctx->bestIdMatch && distSq < ctx->bestDistSq) {
        ctx->best = obj;
        ctx->bestDistSq = distSq;
    }
    return false;
}

// Finds the live managed MapObj nearest to the packed pound position, preferring a
// matching mMapObjID to disambiguate same-archetype objects (e.g. a row of crates).
static TMapObjBase *findHipDropTarget(const TVec3f &pos, u8 mapObjId) {
    FindHipDropObjCtx ctx = {pos, mapObjId, nullptr, kHipDropMatchRadiusSq, false};
    smso::forEachManagedMapObj(visitFindHipDropObj, &ctx);
    return ctx.best;
}

struct MatchPtrCtx {
    const void *needle;
    bool found;
};

static bool visitMatchManagedPtr(TMapObjBase *obj, void *rawCtx) {
    auto *ctx = reinterpret_cast<MatchPtrCtx *>(rawCtx);
    if (static_cast<const void *>(obj) == ctx->needle) {
        ctx->found = true;
        return true;
    }
    return false;
}

// Confirms a floor-actor pointer is genuinely a managed MapObj before we treat it as
// TMapObjBase. Only invoked on the single pound frame, so the manager scan is cheap.
static bool isManagedMapObj(const void *ptr) {
    if (!smso::isValidMapObjPtr(ptr))
        return false;
    MatchPtrCtx ctx = {ptr, false};
    smso::forEachManagedMapObj(visitMatchManagedPtr, &ctx);
    return ctx.found;
}

static u32 packHipDropPublishPayload(const TVec3f &pos, bool superPound) {
    u32 packed = packWorldPos(pos);
    if (packed == 0)
        return 0;
    if (superPound)
        packed |= kHipDropSuperPayloadFlag;
    return packed;
}

static bool hipDropPayloadIsSuper(u32 packed) {
    return (packed & kHipDropSuperPayloadFlag) != 0;
}

static u32 hipDropPayloadPosBits(u32 packed) {
    return packed & ~kHipDropSuperPayloadFlag;
}

static void resetLocalHipDropCaptureIfIdle() {
    const TMario *mario = gpMarioAddress;
    if (!mario || mario->mState != TMario::STATE_G_POUND)
        sLocalHipDropFired = false;
}

static void tryPublishLocalHipDropHit(TMapObjBase *obj, bool superPound) {
    if (sApplyingRemoteEvent || sLocalHipDropFired || !obj)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0 ||
        (buf->bridgeFlags & smso::BF_SYNC_OBJECTS) == 0)
        return;

    if (isChangeStageHipDropObj(obj) || !isLiveCollectible(obj))
        return;

    const u32 packed = packHipDropPublishPayload(hipDropObjWorldPos(obj), superPound);
    if (packed == 0)
        return;

    publishLocalWorldEvent(smso::WE_HIP_DROP_OBJECT, currentCourseId(), currentEpisodeId(),
                           static_cast<u8>(obj->mMapObjID), buf->localSlot, packed);
    sLocalHipDropFired = true;
    const TVec3f pos = hipDropObjWorldPos(obj);
    OSReport("[SMSOBB] hip-drop publish id=%u slot=%u pos=(%.0f,%.0f,%.0f)\n",
             static_cast<u32>(obj->mMapObjID), static_cast<u32>(buf->localSlot), pos.x, pos.y,
             pos.z);
}

static void tryCaptureLocalHipDropFromMessage(THitActor *receiver, THitActor *sender, u32 msg) {
    if (!receiver || sender != reinterpret_cast<THitActor *>(gpMarioAddress))
        return;
    if (msg != kHitMessageHipDrop && msg != kHitMessageSuperHipDrop)
        return;
    if (!isManagedMapObj(receiver))
        return;

    tryPublishLocalHipDropHit(reinterpret_cast<TMapObjBase *>(receiver),
                              msg == kHitMessageSuperHipDrop);
}

static void tryCaptureLocalHipDropFromTouch(TMapObjBase *obj, THitActor *player) {
    if (!obj || player != reinterpret_cast<THitActor *>(gpMarioAddress))
        return;
    if (!gpMarioAddress || gpMarioAddress->mState != TMario::STATE_G_POUND)
        return;
    tryPublishLocalHipDropHit(obj, false);
}

static ReceiveMessageFn lookupReceiveMessageOrig(u32 vtable) {
    for (u32 i = 0; i < sVtReceiveHookCount; ++i) {
        if (sVtReceiveHooks[i].vtable == vtable)
            return sVtReceiveHooks[i].orig;
    }
    return nullptr;
}

static bool smso_receiveMessage_captureHook(THitActor *self, THitActor *sender, u32 msg) {
    tryCaptureLocalHipDropFromMessage(self, sender, msg);
    const ReceiveMessageFn orig = lookupReceiveMessageOrig(*reinterpret_cast<const u32 *>(self));
    if (orig)
        return orig(self, sender, msg);
    return false;
}

static void smso_hipDropHideTouch_captureHook(TMapObjBase *self, THitActor *player) {
    tryCaptureLocalHipDropFromTouch(self, player);
    if (sOrigHipDropHideTouch)
        sOrigHipDropHideTouch(self, player);
}

static void smso_breakableBlockTouch_captureHook(TMapObjBase *self, THitActor *player) {
    tryCaptureLocalHipDropFromTouch(self, player);
    if (sOrigBreakableBlockTouch)
        sOrigBreakableBlockTouch(self, player);
}

static u32 findVtSlotForFn(u32 vtable, u32 fn) {
    for (u32 off = 0x1C; off <= 0x48; off += 4) {
        const u32 entry = *reinterpret_cast<const u32 *>(vtable + off);
        if (entry == fn)
            return off;
    }
    return 0;
}

static void registerTouchPlayerHook(u32 vtable, u32 origFn, TouchPlayerFn *origOut,
                                    TouchPlayerFn hookFn) {
    if (vtable == 0 || origFn == 0 || *origOut != nullptr)
        return;

    u32 touchOff = findVtSlotForFn(vtable, origFn);
    if (touchOff == 0)
        touchOff = 0x30;
    u32 *touchSlot = reinterpret_cast<u32 *>(vtable + touchOff);
    *origOut = reinterpret_cast<TouchPlayerFn>(*touchSlot);
    BetterSMS::PowerPC::writeU32(touchSlot, reinterpret_cast<u32>(hookFn));
}

static void registerReceiveMessageHook(u32 vtable, u32 origFn) {
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
    BetterSMS::PowerPC::writeU32(slot, reinterpret_cast<u32>(&smso_receiveMessage_captureHook));
}

static void replayRemoteHipDropHit(TMapObjBase *obj, bool superPound) {
    TMario *mario = gpMarioAddress;
    if (!mario || !obj)
        return;

    // Object handlers call marioHipAttack() / SMS_IsMarioStatusHipDrop(), which read
    // gpMarioAddress — not the remote puppet. Spoof hip-drop status on the local body
    // for the duration of the replayed hit.
    const u32 savedState = mario->mState;
    mario->mState = TMario::STATE_G_POUND;
    THitActor *sender = static_cast<THitActor *>(mario);

    const u32 vt = *reinterpret_cast<const u32 *>(obj);
    if (gHipDropHideObjVtable != 0 && vt == gHipDropHideObjVtable) {
        obj->touchPlayer(sender);
    } else if (gBreakableBlockVtable != 0 && vt == gBreakableBlockVtable) {
        obj->touchPlayer(sender);
    } else {
        if (superPound)
            obj->receiveMessage(sender, kHitMessageSuperHipDrop);
        obj->receiveMessage(sender, kHitMessageHipDrop);
    }

    mario->mState = savedState;
}

static void initHipDropObjectHooks() {
    registerReceiveMessageHook(kVtMapObjBase, kFnMapObjBaseReceive);
    registerReceiveMessageHook(kVtMapObjGeneral, kFnMapObjGeneralReceive);
    registerReceiveMessageHook(kVtWoodBarrel, kFnWoodBarrelReceive);
    registerReceiveMessageHook(kVtSuperHipDropBlock, kFnSuperHipDropReceive);
    registerReceiveMessageHook(kVtBrickBlock, kFnBrickBlockReceive);
    registerReceiveMessageHook(kVtRedCoinSwitch, kFnRedCoinSwitchReceive);
    registerReceiveMessageHook(kVtBreakHideObj, kFnBreakHideReceive);

    gHipDropHideObjVtable = kVtHipDropHideObj;
    registerTouchPlayerHook(kVtHipDropHideObj, kFnHipDropHideTouch, &sOrigHipDropHideTouch,
                            &smso_hipDropHideTouch_captureHook);

    gBreakableBlockVtable = kVtBreakableBlock;
    registerTouchPlayerHook(kVtBreakableBlock, kFnBreakableBlockTouch, &sOrigBreakableBlockTouch,
                            &smso_breakableBlockTouch_captureHook);
}

static bool isCollectibleBlueCoin(const TMapObjBase *obj) {
    if (!isLiveCollectible(obj))
        return false;
    if (*reinterpret_cast<const u32 *>(obj) != gCoinBlueVtable)
        return false;
    const auto *bytes = reinterpret_cast<const u8 *>(obj);
    return bytes[kCoinTakenFlagOffset] == 0;
}

static void hideBlueCoinActor(TMapObjBase *obj) {
    if (!obj || !isCollectibleBlueCoin(obj))
        return;

    obj->makeObjDead();

    auto *mutableBytes = reinterpret_cast<u8 *>(obj);
    mutableBytes[kCoinTakenFlagOffset] = 1;

    auto *live = reinterpret_cast<TLiveActor *>(obj);
    live->mStateFlags.asFlags.mClipFromScene = true;
    live->mStateFlags.asFlags.mIsObjDead = true;
}

struct HideBlueCtx {
    u8 flagIndex;
    bool hidden;
};

static bool visitHideBlueCoinByFlagIndex(TMapObjBase *obj, void *rawCtx) {
    auto *hideCtx = reinterpret_cast<HideBlueCtx *>(rawCtx);
    if (!isCollectibleBlueCoin(obj))
        return false;

    auto *coin = reinterpret_cast<TCoin *>(obj);
    const u8 coinIndex = static_cast<u8>(coin->_154);
    const u8 mapObjId = static_cast<u8>(obj->mMapObjID);
    if (coinIndex != hideCtx->flagIndex && mapObjId != hideCtx->flagIndex)
        return false;

    hideBlueCoinActor(obj);
    hideCtx->hidden = true;
    return true;
}

static void hideBlueCoinAtIndex(u8 flagIndex) {
    if (flagIndex >= kMaxStageBlueCoins)
        return;

    HideBlueCtx ctx = {flagIndex, false};
    smso::forEachManagedMapObj(visitHideBlueCoinByFlagIndex, &ctx);
}

struct FindBlueCoinCtx {
    u8 flagIndex;
    TMapObjBase *coin;
};

static bool visitFindBlueCoinByFlagIndex(TMapObjBase *obj, void *rawCtx) {
    auto *findCtx = reinterpret_cast<FindBlueCoinCtx *>(rawCtx);
    if (!isCollectibleBlueCoin(obj))
        return false;

    auto *coin = reinterpret_cast<TCoin *>(obj);
    const u8 coinIndex = static_cast<u8>(coin->_154);
    const u8 mapObjId = static_cast<u8>(obj->mMapObjID);
    if (coinIndex != findCtx->flagIndex && mapObjId != findCtx->flagIndex)
        return false;

    findCtx->coin = obj;
    return true;
}

static bool tryResolveBlueCoinWorldPos(u8 flagIndex, TVec3f *outPos) {
    if (flagIndex >= kMaxStageBlueCoins || !outPos)
        return false;

    FindBlueCoinCtx ctx = {flagIndex, nullptr};
    smso::forEachManagedMapObj(visitFindBlueCoinByFlagIndex, &ctx);
    if (!ctx.coin)
        return false;

    *outPos = ctx.coin->mInitialPosition;
    return true;
}

static s32 shineGlobalIdForActor(const TShine *shine) {
    if (!gGetShineId || !gGetShineStage || !shine)
        return -1;

    const u32 shineStage = gGetShineStage(currentCourseId());
    const bool isEx = (shine->mType & 0x10) != 0;
    const u32 scenario = shine->mType & 0xFu;
    return gGetShineId(shineStage, scenario, isEx);
}

static void hideShineActor(TShine *shine) {
    if (!shine || shine->mIsAlreadyObtained || !shouldHideShineActor(shine))
        return;

    auto *live = reinterpret_cast<TLiveActor *>(shine);
    if (live->mStateFlags.asFlags.mIsObjDead)
        return;

    shine->mIsAlreadyObtained = true;
    reinterpret_cast<TMapObjBase *>(shine)->makeObjDead();
    live->mStateFlags.asFlags.mClipFromScene = true;
    live->mStateFlags.asFlags.mIsObjDead = true;
}

struct LiveShineCtx {
    u8 count;
    TShine *only;
};

static bool visitCountLiveShine(TMapObjBase *obj, void *ctx) {
    auto *liveCtx = reinterpret_cast<LiveShineCtx *>(ctx);
    if (*reinterpret_cast<const u32 *>(obj) != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    if (liveCtx->count == 0)
        liveCtx->only = shine;
    else
        liveCtx->only = nullptr;
    ++liveCtx->count;
    return false;
}

static bool visitHideShineIfFlagged(TMapObjBase *obj, void *ctx) {
    auto *fm = reinterpret_cast<TFlagManager *>(ctx);
    if (*reinterpret_cast<const u32 *>(obj) != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    const s32 globalId = shineGlobalIdForActor(shine);
    if (globalId >= 0 && fm->getShineFlag(static_cast<u8>(globalId)) && shouldHideShineActor(shine))
        hideShineActor(shine);
    return false;
}

static void hideAllShinesWithSetFlags(TFlagManager *fm) {
    if (!fm || gShineVtable == 0)
        return;

    smso::forEachManagedMapObj(visitHideShineIfFlagged, fm);
}

struct HideShineCtx {
    u8 targetShineId;
    bool found;
};

static bool visitHideShineById(TMapObjBase *obj, void *ctx) {
    auto *hideCtx = reinterpret_cast<HideShineCtx *>(ctx);
    if (*reinterpret_cast<const u32 *>(obj) != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    const s32 globalId = shineGlobalIdForActor(shine);
    if (globalId < 0 || static_cast<u8>(globalId) != hideCtx->targetShineId)
        return false;

    hideShineActor(shine);
    hideCtx->found = true;
    return false;
}

struct BruteShineCtx {
    u8 targetShineId;
    u32 scenario;
    bool isEx;
    bool found;
};

static bool visitHideShineByScenario(TMapObjBase *obj, void *rawCtx) {
    auto *match = reinterpret_cast<BruteShineCtx *>(rawCtx);
    if (*reinterpret_cast<const u32 *>(obj) != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    const bool isEx = (shine->mType & 0x10) != 0;
    const u32 scenario = shine->mType & 0xFu;
    if (scenario != match->scenario || isEx != match->isEx)
        return false;

    hideShineActor(shine);
    match->found = true;
    return true;
}

static void hideShineByGlobalId(u8 shineId) {
    HideShineCtx ctx = {shineId, false};
    smso::forEachManagedMapObj(visitHideShineById, &ctx);
    if (ctx.found)
        return;

    if (!gGetShineId || !gGetShineStage)
        return;

    const u32 shineStage = gGetShineStage(currentCourseId());
    BruteShineCtx brute = {shineId, 0, false, false};

    for (u32 scenario = 0; scenario < 12; ++scenario) {
        for (u32 ex = 0; ex < 2; ++ex) {
            const s32 candidate = gGetShineId(shineStage, scenario, ex != 0);
            if (candidate < 0 || static_cast<u8>(candidate) != shineId)
                continue;

            brute.scenario = scenario;
            brute.isEx = ex != 0;
            brute.found = false;
            smso::forEachManagedMapObj(visitHideShineByScenario, &brute);
            if (brute.found)
                return;
        }
    }

    LiveShineCtx live = {0, nullptr};
    smso::forEachManagedMapObj(visitCountLiveShine, &live);
    (void)live;
}

struct FindShinePosCtx {
    TVec3f pos;
    TShine *found;
};

static bool visitFindShineAtPos(TMapObjBase *obj, void *ctx) {
    auto *find = reinterpret_cast<FindShinePosCtx *>(ctx);
    if (*reinterpret_cast<const u32 *>(obj) != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    TVec3f actorPos;
    shine->JSGGetTranslation(reinterpret_cast<Vec *>(&actorPos));
    if (!shinePositionsMatch(actorPos, find->pos) &&
        !shinePositionsMatch(obj->mInitialPosition, find->pos))
        return false;

    find->found = shine;
    return true;
}

struct FindShineIdCtx {
    u8 targetShineId;
    TShine *found;
};

static bool visitFindShineById(TMapObjBase *obj, void *ctx) {
    auto *find = reinterpret_cast<FindShineIdCtx *>(ctx);
    if (*reinterpret_cast<const u32 *>(obj) != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    const s32 globalId = shineGlobalIdForActor(shine);
    if (globalId < 0 || static_cast<u8>(globalId) != find->targetShineId)
        return false;

    find->found = shine;
    return true;
}

static TShine *findShineAtPosition(const TVec3f &pos) {
    FindShinePosCtx ctx = {pos, nullptr};
    smso::forEachManagedMapObj(visitFindShineAtPos, &ctx);
    return ctx.found;
}

struct FindShineScenarioCtx {
    u32 scenario;
    bool isEx;
    TShine *found;
};

static bool visitFindShineByScenario(TMapObjBase *obj, void *rawCtx) {
    auto *match = reinterpret_cast<FindShineScenarioCtx *>(rawCtx);
    if (*reinterpret_cast<const u32 *>(obj) != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    const bool isEx = (shine->mType & 0x10) != 0;
    const u32 scenario = shine->mType & 0xFu;
    if (scenario != match->scenario || isEx != match->isEx)
        return false;

    match->found = shine;
    return true;
}

static TShine *findShineByGlobalId(u8 shineId) {
    FindShineIdCtx ctx = {shineId, nullptr};
    smso::forEachManagedMapObj(visitFindShineById, &ctx);
    if (ctx.found)
        return ctx.found;

    if (!gGetShineId || !gGetShineStage)
        return nullptr;

    const u32 shineStage = gGetShineStage(currentCourseId());
    for (u32 scenario = 0; scenario < 12; ++scenario) {
        for (u32 ex = 0; ex < 2; ++ex) {
            const s32 candidate = gGetShineId(shineStage, scenario, ex != 0);
            if (candidate < 0 || static_cast<u8>(candidate) != shineId)
                continue;

            FindShineScenarioCtx scenarioCtx = {scenario, ex != 0, nullptr};
            smso::forEachManagedMapObj(visitFindShineByScenario, &scenarioCtx);
            if (scenarioCtx.found)
                return scenarioCtx.found;
        }
    }

    return nullptr;
}

struct GatherStageShineCtx {
    StageShineEntry entries[kMaxStageShines];
    u8 count;
};

static bool visitGatherStageShine(TMapObjBase *obj, void *ctx) {
    auto *gather = reinterpret_cast<GatherStageShineCtx *>(ctx);
    if (gather->count >= kMaxStageShines)
        return false;
    if (*reinterpret_cast<const u32 *>(obj) != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    StageShineEntry &entry = gather->entries[gather->count++];
    entry.initialPos = obj->mInitialPosition;
    shine->JSGGetTranslation(reinterpret_cast<Vec *>(&entry.livePos));

    const s32 globalId = shineGlobalIdForActor(shine);
    entry.shineId = globalId >= 0 ? static_cast<u8>(globalId) : 0xFF;
    if (globalId >= 0)
        rememberShinePosition(static_cast<u8>(globalId), entry.livePos);
    return false;
}

static void captureStageShineSnapshot() {
    GatherStageShineCtx gather = {};
    smso::forEachManagedMapObj(visitGatherStageShine, &gather);

    sStageShineCount = gather.count;
    for (u8 i = 0; i < gather.count; ++i)
        sStageShines[i] = gather.entries[i];
    sStageShineSnapshotReady = true;
}

static bool lookupStageShinePosition(u8 shineId, TVec3f *outPos) {
    if (!outPos)
        return false;

    for (u8 i = 0; i < sStageShineCount; ++i) {
        if (sStageShines[i].shineId != shineId)
            continue;
        *outPos = sStageShines[i].livePos;
        return true;
    }
    return false;
}

static bool lookupKnownShinePosition(u8 shineId, TVec3f *outPos) {
    if (!outPos || shineId >= 128 || sKnownShinePosValid[shineId] == 0)
        return false;
    *outPos = sKnownShinePos[shineId];
    return true;
}

static void trackLocalShineCollection() {
    if (sApplyingRemoteEvent || !gpMarDirector || !gpMarDirector->mCollectedShine)
        return;

    TShine *shine = gpMarDirector->mCollectedShine;
    shine->JSGGetTranslation(reinterpret_cast<Vec *>(&sPendingShineCapture.pos));
    sPendingShineCapture.hasPos = 1;

    const s32 globalId = shineGlobalIdForActor(shine);
    if (globalId >= 0 && globalId < 128) {
        sPendingShineCapture.shineId = static_cast<u8>(globalId);
        sPendingShineCapture.hasId = 1;
        rememberShinePosition(sPendingShineCapture.shineId, sPendingShineCapture.pos);
    }
}

static TShine *resolveShineForCollect(u8 shineId, u32 packedPos) {
    if (packedPos != 0) {
        TShine *shine = findShineAtPosition(unpackWorldPos(packedPos));
        if (shine)
            return shine;
    }

    TShine *shine = findShineByGlobalId(shineId);
    if (shine)
        return shine;

    TVec3f pos{};
    if (lookupKnownShinePosition(shineId, &pos)) {
        shine = findShineAtPosition(pos);
        if (shine)
            return shine;
    }

    for (u8 i = 0; i < sStageShineCount; ++i) {
        if (sStageShines[i].shineId != shineId)
            continue;
        shine = findShineAtPosition(sStageShines[i].livePos);
        if (shine)
            return shine;
        shine = findShineAtPosition(sStageShines[i].initialPos);
        if (shine)
            return shine;
    }

    LiveShineCtx live = {0, nullptr};
    smso::forEachManagedMapObj(visitCountLiveShine, &live);
    (void)live;
    return nullptr;
}

static void hideCollectedShineActor(u8 shineId, u32 packedPos) {
    TShine *shine = resolveShineForCollect(shineId, packedPos);
    if (shine) {
        hideShineActor(shine);
        return;
    }
    hideShineByGlobalId(shineId);
}

static bool captureShinePublishPosition(u8 shineId, TVec3f *outPos) {
    if (!outPos)
        return false;

    if (sPendingShineCapture.hasPos &&
        (!sPendingShineCapture.hasId || sPendingShineCapture.shineId == shineId)) {
        *outPos = sPendingShineCapture.pos;
        return true;
    }

    if (gpMarDirector && gpMarDirector->mCollectedShine) {
        gpMarDirector->mCollectedShine->JSGGetTranslation(reinterpret_cast<Vec *>(outPos));
        return true;
    }

    if (lookupKnownShinePosition(shineId, outPos))
        return true;

    if (lookupStageShinePosition(shineId, outPos))
        return true;

    TShine *shine = findShineByGlobalId(shineId);
    if (shine) {
        shine->JSGGetTranslation(reinterpret_cast<Vec *>(outPos));
        return true;
    }

    return false;
}

static u32 packShinePublishPayload(u8 shineId) {
    TVec3f shinePos{};
    if (!captureShinePublishPosition(shineId, &shinePos))
        return 0;
    rememberShinePosition(shineId, shinePos);
    return packWorldPos(shinePos);
}

static void tickRemoteShineCollect() {
    if (!sRemoteShineCollect.shine)
        return;

    TShine *shine = sRemoteShineCollect.shine;
    if (shine->mIsAlreadyObtained) {
        clearRemoteShineCollect();
        return;
    }

    TMario *remote = smso::getRemoteBodyForSlot(sRemoteShineCollect.collectorSlot);
    if (!remote || !smso::hasRemoteBodyForSlot(sRemoteShineCollect.collectorSlot)) {
        hideShineActor(shine);
        clearRemoteShineCollect();
        return;
    }

    f32 hx = remote->mTranslation.x;
    f32 hy = remote->mTranslation.y + 200.0f;
    f32 hz = remote->mTranslation.z;
    smso::getRemoteHeadAnchorPosition(sRemoteShineCollect.collectorSlot, hx, hy, hz);

    const TVec3f target(hx, hy, hz);
    TVec3f position;
    TVec3f size;
    TVec3f rotation;
    shine->JSGGetTranslation(reinterpret_cast<Vec *>(&position));
    shine->JSGGetScaling(reinterpret_cast<Vec *>(&size));
    shine->JSGGetRotation(reinterpret_cast<Vec *>(&rotation));

    const TVec3f step(0.007f, 0.007f, 0.007f);

    if (sRemoteShineCollect.shrinking) {
        if (size.x - 0.011f <= 0.0f) {
            hideShineActor(shine);
            remote->mGrabTarget = nullptr;
            remote->mState = 0x337u; // STATE_WARPOUT
            clearRemoteShineCollect();
            return;
        }

        rotation.y += 3.0f;
        position.y += 4.0f;
        size.sub(step);
        shine->JSGSetScaling(reinterpret_cast<Vec &>(size));
        shine->JSGSetRotation(reinterpret_cast<Vec &>(rotation));
        shine->JSGSetTranslation(reinterpret_cast<Vec &>(position));
        shine->mGlowSize.sub(step);
    } else {
        position.x += (target.x - position.x) * 0.15f;
        position.y += (target.y - position.y) * 0.15f + 2.0f;
        position.z += (target.z - position.z) * 0.15f;
        shine->JSGSetTranslation(reinterpret_cast<Vec &>(position));
        shine->mGlowSize.set(1.0f, 1.0f, 1.0f);

        const f32 dx = target.x - position.x;
        const f32 dy = target.y - position.y;
        const f32 dz = target.z - position.z;
        if (dx * dx + dy * dy + dz * dz < 400.0f)
            sRemoteShineCollect.shrinking = true;
    }

    ++sRemoteShineCollect.frames;
    if (sRemoteShineCollect.frames >= kRemoteShineCollectMaxFrames) {
        hideShineActor(shine);
        remote->mGrabTarget = nullptr;
        remote->mState = 0x337u; // STATE_WARPOUT
        clearRemoteShineCollect();
    }
}

static void beginRemoteShineCollect(u8 collectorSlot, u8 shineId, u32 packedPos) {
    smso::CommBuffer *buf = smso::getCommBuffer();
    const u8 localSlot = buf ? buf->localSlot : 0;

    if (packedPos != 0)
        rememberShinePosition(shineId, unpackWorldPos(packedPos));

    TShine *shine = resolveShineForCollect(shineId, packedPos);
    if (!shine) {
        hideShineByGlobalId(shineId);
        return;
    }

    if (collectorSlot == localSlot) {
        hideShineActor(shine);
        return;
    }

    TMario *remote = smso::getRemoteBodyForSlot(collectorSlot);
    if (!remote || !smso::hasRemoteBodyForSlot(collectorSlot)) {
        hideShineActor(shine);
        return;
    }

    clearRemoteShineCollect();
    sRemoteShineCollect.collectorSlot = collectorSlot;
    sRemoteShineCollect.shine = shine;
    sRemoteShineCollect.frames = 0;
    sRemoteShineCollect.shrinking = false;

    remote->mGrabTarget = reinterpret_cast<TTakeActor *>(shine);
    remote->mState = static_cast<u32>(TMario::STATE_SHINE_C);
    remote->setAnimation(TMario::ANIMATION_SHINEGET, 1.0f);
    shine->mType = (shine->mType & 0x10) | 1;
}

static void reconcileCollectibleActors(TFlagManager *fm, u8 courseId) {
    if (!fm || gShineVtable == 0 || gCoinBlueVtable == 0)
        return;

    if (sStageSettleFrames < kStageSettleFrames)
        return;

    hideAllShinesWithSetFlags(fm);

    for (u8 shineId = 0; shineId < 128; ++shineId) {
        if (!fm->getShineFlag(shineId))
            continue;

        u32 packed = 0;
        TVec3f pos{};
        if (lookupKnownShinePosition(shineId, &pos))
            packed = packWorldPos(pos);
        hideCollectedShineActor(shineId, packed);
    }

    for (u8 coinIndex = 0; coinIndex < kMaxStageBlueCoins; ++coinIndex) {
        if (fm->getBlueCoinFlag(courseId, coinIndex))
            hideBlueCoinAtIndex(coinIndex);
    }
}

static void resetLocalTrackersForStage(u8 courseId, u8 episodeId) {
    sLastCourseId = courseId;
    sLastEpisodeId = episodeId;
    sLastGoldCoinCount = 0;
    sBlueCoinBits = 0;
    sStageSettleFrames = 0;
    sStageShineSnapshotReady = false;
    sStageShineCount = 0;
    sPendingShineCapture = {};
    clearRemoteShineCollect();
    clearKnownShinePositions();
    smso::resetLocalYoshiFruitSync();
    sLocalHipDropFired = false;
    for (u32 i = 0; i < sizeof(sShineBits); ++i)
        sShineBits[i] = 0;

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return;

    sLastGoldCoinCount = static_cast<u32>(fm->getFlag(0x40002u));
    for (u8 shineId = 0; shineId < 128; ++shineId) {
        if (fm->getShineFlag(shineId))
            markShineSet(shineId);
    }
    for (u8 coinIndex = 0; coinIndex < 30; ++coinIndex) {
        if (fm->getBlueCoinFlag(courseId, coinIndex))
            markBlueCoinSet(coinIndex);
    }
}

static void publishLocalWorldEvent(smso::WorldEventType type, u8 courseId, u8 episodeId, u8 payload0,
                                   u8 reserved, u32 payload1) {
    if (sApplyingRemoteEvent)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0)
        return;

    switch (type) {
    case smso::WE_SHINE_COLLECTED:
        if (!worldSyncEnabled(buf) || (buf->bridgeFlags & smso::BF_SYNC_SHINE) == 0)
            return;
        break;
    case smso::WE_BLUE_COIN_COLLECTED:
        if (!worldSyncEnabled(buf) || (buf->bridgeFlags & smso::BF_SYNC_BLUE_COIN) == 0)
            return;
        break;
    case smso::WE_GOLD_COIN_COLLECTED:
        if (!episodeCollectibleSyncEnabled(buf))
            return;
        break;
    case smso::WE_RED_COIN_COLLECTED:
        if ((buf->bridgeFlags & (smso::BF_SYNC_EVENT | smso::BF_SYNC_MISSION)) == 0)
            return;
        break;
    case smso::WE_HIP_DROP_OBJECT:
        if (!objectSyncEnabled(buf))
            return;
        break;
    case smso::WE_YOSHI_FRUIT_TAKEN:
        if (!worldSyncEnabled(buf))
            return;
        break;
    default:
        if (!worldSyncEnabled(buf))
            return;
        break;
    }

    if (sLocalWorldEventQueueCount >= kLocalWorldEventQueueCap)
        return;

    const u8 writeIndex = static_cast<u8>(
        (sLocalWorldEventQueueHead + sLocalWorldEventQueueCount) % kLocalWorldEventQueueCap);
    smso::CommWorldEvent &event = sLocalWorldEventQueue[writeIndex];
    event.eventId = 0;
    event.sequence = 0;
    event.type = static_cast<u8>(type);
    event.courseId = courseId;
    event.episodeId = episodeId;
    event.payload0 = payload0;
    event.reserved = reserved;
    event.payload1 = payload1;
    ++sLocalWorldEventQueueCount;
}

static bool localPendingSlotIsFree(const smso::CommBuffer *buf) {
    if (!buf)
        return false;
    const smso::CommWorldEvent &slot = buf->worldSync.localPending;
    // The bridge zeroes the slot (sequence + type) once it has published the event, so an
    // empty slot means the previous event was consumed and we can hand over the next one.
    return slot.sequence == 0 || slot.type == 0;
}

// Flush at most one queued event into the localPending mailbox slot. The bridge polls the
// comm buffer and publishes localPending on a sequence change, then clears the slot. By
// only writing when the slot is free we guarantee no outbound event is overwritten before
// the bridge relays it to the server.
static void flushLocalWorldEventQueue() {
    if (sLocalWorldEventQueueCount == 0)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0)
        return;
    if (!localPendingSlotIsFree(buf))
        return;

    smso::CommWorldEvent &slot = buf->worldSync.localPending;
    slot = sLocalWorldEventQueue[sLocalWorldEventQueueHead];
    slot.sequence = ++sLocalWorldEventSequence;
    sLocalWorldEventQueueHead = static_cast<u8>(
        (sLocalWorldEventQueueHead + 1) % kLocalWorldEventQueueCap);
    --sLocalWorldEventQueueCount;
}

static void refreshHudCounters() {
    if (!gpMarDirector || !gpMarDirector->mGCConsole)
        return;

    TGCConsole2 *console = gpMarDirector->mGCConsole;
    if (gCountBlueCoin)
        gCountBlueCoin(console);
    if (gCountShine)
        gCountShine(console);
}

static void applyGoldCoinCount(TFlagManager *fm, u8 courseId, u32 targetCount) {
    if (!gIncGoldCoinFlag || !fm)
        return;

    u32 current = static_cast<u32>(fm->getFlag(0x40002u));
    if (targetCount <= current)
        return;

    const u32 delta = targetCount - current;
    for (u32 i = 0; i < delta && current < 100u; ++i) {
        gIncGoldCoinFlag(fm, courseId, 1);
        current = static_cast<u32>(fm->getFlag(0x40002u));
    }
}

static bool applyWorldEvent(const smso::CommWorldEvent &event) {
    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm || event.eventId == 0 || event.type == 0)
        return false;

    sApplyingRemoteEvent = true;

    bool applied = true;
    switch (static_cast<smso::WorldEventType>(event.type)) {
    case smso::WE_SHINE_COLLECTED:
        if (!fm->getShineFlag(event.payload0)) {
            fm->setShineFlag(event.payload0);
            markShineSet(event.payload0);
        }
        if (event.payload1 != 0)
            rememberShinePosition(event.payload0, unpackWorldPos(event.payload1));
        beginRemoteShineCollect(event.reserved, event.payload0, event.payload1);
        break;

    case smso::WE_BLUE_COIN_COLLECTED: {
        const u8 flagIndex = event.payload0;
        const bool alreadySet = fm->getBlueCoinFlag(event.courseId, flagIndex);
        const bool locallyTracked = blueCoinWasSet(flagIndex);
        const smso::CommBuffer *buf = smso::getCommBuffer();
        const u8 localSlot = buf ? buf->localSlot : 0;
        const bool remoteCollector = event.reserved != localSlot;

        TVec3f coinPos{};
        const bool haveCoinPos = tryResolveBlueCoinWorldPos(flagIndex, &coinPos);

        if (!alreadySet) {
            fm->setBlueCoinFlag(event.courseId, flagIndex);
            markBlueCoinSet(flagIndex);
        } else if (!locallyTracked && event.courseId == sLastCourseId) {
            markBlueCoinSet(flagIndex);
        }

        // Skip hide on host echo after a local pickup — vanilla is still driving the coin actor.
        if (event.courseId == currentCourseId() && (!alreadySet || !locallyTracked))
            hideBlueCoinAtIndex(flagIndex);

        if (remoteCollector && !locallyTracked && haveCoinPos)
            smso::playRemoteCoinCollectParticles(coinPos, true);
        break;
    }

    case smso::WE_GOLD_COIN_COLLECTED:
        if (!sameStage(event.courseId, event.episodeId)) {
            applied = false;
            break;
        }
        applyGoldCoinCount(fm, event.courseId, event.payload1);
        sLastGoldCoinCount = static_cast<u32>(fm->getFlag(0x40002u));
        break;

    case smso::WE_RED_COIN_COLLECTED:
        applied = applyRedCoinWorldEvent(event);
        break;

    case smso::WE_HIP_DROP_OBJECT: {
        if (!sameStage(event.courseId, event.episodeId)) {
            applied = false;
            break;
        }
        smso::CommBuffer *buf = smso::getCommBuffer();
        const u8 localSlot = buf ? buf->localSlot : 0;
        if (event.reserved == localSlot)
            break;
        if (!buf || (buf->bridgeFlags & smso::BF_SYNC_OBJECTS) == 0)
            break;

        const u32 packedPos = hipDropPayloadPosBits(event.payload1);
        TMapObjBase *obj = findHipDropTarget(unpackWorldPos(packedPos), event.payload0);
        if (!obj || isChangeStageHipDropObj(obj) || !isLiveCollectible(obj)) {
            OSReport("[SMSOBB] hip-drop apply miss course=%u/%u id=%u packed=0x%08X\n",
                     event.courseId, event.episodeId, event.payload0, packedPos);
            applied = false;
            break;
        }

        OSReport("[SMSOBB] hip-drop apply hit vt=0x%08X id=%u from slot=%u\n",
                 *reinterpret_cast<const u32 *>(obj), event.payload0, event.reserved);

        const u32 objVt = *reinterpret_cast<const u32 *>(obj);
        if (objVt == smso::redCoinSwitchVtable())
            smso::applyRemoteRedCoinSwitchHit(obj);
        else
            replayRemoteHipDropHit(obj, hipDropPayloadIsSuper(event.payload1));
        break;
    }

    case smso::WE_YOSHI_FRUIT_TAKEN: {
        if (!sameStage(event.courseId, event.episodeId)) {
            applied = false;
            break;
        }
        smso::CommBuffer *buf = smso::getCommBuffer();
        const u8 localSlot = buf ? buf->localSlot : 0;
        if (event.reserved == localSlot)
            break;
        if (!buf || !worldSyncEnabled(buf))
            break;

        if (!smso::applyRemoteYoshiFruitWorldEvent(event.payload0, event.payload1))
            applied = false;
        break;
    }

    default:
        applied = false;
        break;
    }

    refreshHudCounters();
    sApplyingRemoteEvent = false;
    return applied;
}

static void captureLocalWorldProgress() {
    if (sApplyingRemoteEvent)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!worldSyncEnabled(buf))
        return;

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return;

    const u8 courseId = currentCourseId();
    const u8 episodeId = currentEpisodeId();
    if (courseId != sLastCourseId || episodeId != sLastEpisodeId)
        resetLocalTrackersForStage(courseId, episodeId);

    if (sStageSettleFrames < kStageSettleFrames)
        ++sStageSettleFrames;

    if (sStageSettleFrames >= kStageSettleFrames && !sStageShineSnapshotReady)
        captureStageShineSnapshot();

    trackLocalShineCollection();
    smso::flushDeferredRedCoinEvents();
    resetLocalHipDropCaptureIfIdle();

    const u32 goldCoins = static_cast<u32>(fm->getFlag(0x40002u));
    if (goldCoins > sLastGoldCoinCount) {
        publishLocalWorldEvent(smso::WE_GOLD_COIN_COLLECTED, courseId, episodeId, 0, 0, goldCoins);
        sLastGoldCoinCount = goldCoins;
    }

    if ((buf->bridgeFlags & smso::BF_SYNC_SHINE) != 0) {
        for (u8 shineId = 0; shineId < 128; ++shineId) {
            if (!fm->getShineFlag(shineId) || shineWasSet(shineId))
                continue;
            markShineSet(shineId);

            const u32 packedPos = packShinePublishPayload(shineId);
            publishLocalWorldEvent(smso::WE_SHINE_COLLECTED, courseId, episodeId, shineId, buf->localSlot,
                                   packedPos);

            if (sPendingShineCapture.hasId && sPendingShineCapture.shineId == shineId)
                sPendingShineCapture = {};
            else if (sPendingShineCapture.hasPos && !sPendingShineCapture.hasId)
                sPendingShineCapture = {};
        }
    }

    if ((buf->bridgeFlags & smso::BF_SYNC_BLUE_COIN) != 0) {
        for (u8 coinIndex = 0; coinIndex < 30; ++coinIndex) {
            if (!fm->getBlueCoinFlag(courseId, coinIndex) || blueCoinWasSet(coinIndex))
                continue;
            markBlueCoinSet(coinIndex);
            publishLocalWorldEvent(smso::WE_BLUE_COIN_COLLECTED, courseId, episodeId, coinIndex,
                                   buf->localSlot, 0);
        }
    }

    tickRemoteShineCollect();

    smso::captureLocalRedCoinProgress();
    reconcileCollectibleActors(fm, courseId);
}

void ensureHipDropObjectHooksImpl() {
    if (sHipDropHooksInstalled)
        return;
    initHipDropObjectHooks();
    sHipDropHooksInstalled = true;
    OSReport("[SMSOBB] hip-drop object hooks installed (%u receive vtables)\n", sVtReceiveHookCount);
}

} // namespace

namespace smso {

void ensureHipDropObjectHooks() {
    ensureHipDropObjectHooksImpl();
}

bool isRemoteShineCollectActive(u8 slot) {
    return sRemoteShineCollect.shine != nullptr && sRemoteShineCollect.collectorSlot == slot;
}

u32 packCollectibleWorldPos(f32 x, f32 y, f32 z) {
    auto enc = [](f32 v) -> s32 {
        return static_cast<s32>(v / kWorldPosPackScale + kWorldPosPackBias);
    };
    const s32 ex = enc(x);
    const s32 ey = enc(y);
    const s32 ez = enc(z);
    if (ex < 0 || ex > 1023 || ey < 0 || ey > 1023 || ez < 0 || ez > 1023)
        return 0;

    return static_cast<u32>(ex) | (static_cast<u32>(ey) << 10) | (static_cast<u32>(ez) << 20);
}

void unpackCollectibleWorldPos(u32 packed, f32 &x, f32 &y, f32 &z) {
    auto dec = [](u32 bits) -> f32 {
        return (static_cast<f32>(bits & 0x3FFu) - kWorldPosPackBias) * kWorldPosPackScale;
    };
    x = dec(packed);
    y = dec(packed >> 10);
    z = dec(packed >> 20);
}

bool isValidPackedWorldPos(u32 packed) {
    return packed != 0 && packed != 0x3FFFFFFFu;
}

bool looksLikePackedCollectibleWorldPos(u32 packed) {
    if (!isValidPackedWorldPos(packed))
        return false;
    // Pre-position wire format: (stableIndex << 8) | count, always fits in 16 bits with Z term zero.
    if (packed <= 0xFFFFu && (packed >> 20) == 0)
        return false;
    return true;
}

void initWorldSync() {
    initRedCoinSync();
    clearRemoteShineCollect();
    sPendingShineCapture = {};
    clearKnownShinePositions();
    gIncGoldCoinFlag =
        reinterpret_cast<IncGoldCoinFlagFn>(SMS_PORT_REGION(0x80294610, 0x8028C428, 0, 0));
    gCountShine = reinterpret_cast<CountShineFn>(SMS_PORT_REGION(0x80147A0C, 0x8013C690, 0, 0));
    gCountBlueCoin =
        reinterpret_cast<CountBlueCoinFn>(SMS_PORT_REGION(0x8014757C, 0x8013C200, 0, 0));
    gGetShineId = reinterpret_cast<GetShineIdFn>(SMS_PORT_REGION(0x8016FAC0, 0x80165834, 0, 0));
    gGetShineStage =
        reinterpret_cast<GetShineStageFn>(SMS_PORT_REGION(0x802A8AC8, 0x802A0B70, 0, 0));
    gShineVtable = SMS_PORT_REGION(0x803C97EC, 0x803C0FDC, 0, 0);
    gCoinBlueVtable = SMS_PORT_REGION(0x803C99D0, 0x803C11C0, 0, 0);
    gCoinEmptyVtable = SMS_PORT_REGION(0x803C9D98, 0x803C1588, 0, 0);
    gChangeStageHipDropVtable = SMS_PORT_REGION(0x803CADA4, 0x803C2594, 0, 0x803CADA4);
    sLastCourseId = 0xFF;
    sLastEpisodeId = 0xFF;
}

void processWorldEvents() {
    CommBuffer *buf = getCommBuffer();
    if (!buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0)
        return;

    const bool progressSync = worldSyncEnabled(buf);
    const bool objectsSync = objectSyncEnabled(buf);
    if (!progressSync && !objectsSync)
        return;

    if (progressSync)
        captureLocalWorldProgress();
    else
        resetLocalHipDropCaptureIfIdle();

    flushLocalWorldEventQueue();

    CommWorldEvent &incoming = buf->worldSync.incoming;
    if (incoming.eventId != 0 && incoming.type != 0) {
        if (incoming.eventId > buf->worldSync.lastAppliedEventId) {
            if (applyWorldEvent(incoming))
                buf->worldSync.lastAppliedEventId = incoming.eventId;
        }
        // Always free the incoming slot once we have observed it so the bridge can deliver
        // the next queued remote event. Without this, a duplicate or stale eventId would
        // pin the slot and stall the incoming queue.
        incoming = {};
    }
}

void enqueueLocalWorldEvent(u8 type, u8 courseId, u8 episodeId, u8 payload0, u8 reserved,
                            u32 payload1) {
    publishLocalWorldEvent(static_cast<smso::WorldEventType>(type), courseId, episodeId, payload0,
                           reserved, payload1);
}

} // namespace smso
