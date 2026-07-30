#include "world_sync.hpp"

#include "coin_collect_fx.hpp"
#include "collectible_scan.hpp"
#include "comm_buffer.hpp"
#include "episode_equiv.hpp"
#include "fruit_sync.hpp"
#include "graffiti_clean_sync.hpp"
#include "monte_clean_sync.hpp"
#include "npc_sync.hpp"
#include "red_coin_sync.hpp"
#include "remote_actor.hpp"
#include "story_flag_sync.hpp"
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
#include <SMS/System/Application.hxx>
#include <SMS/macros.h>
#include <BetterSMS/memory.hxx>
#include <BetterSMS/module.hxx>
#include <sdk.h>
#include <Dolphin/OS.h>

extern TMarDirector *gpMarDirector;
extern TItemManager *gpItemManager;
extern TMario *gpMarioAddress;
extern TApplication gpApplication;

struct TMapObjManager;
extern TMapObjManager *gpMapObjManager;

namespace smso {
bool objectSyncGameplayReady();
}

namespace {

constexpr u32 kStageSettleFrames = 180;
constexpr u32 kCoinTakenFlagOffset = 0x152;
// Vanilla TFlagManager::get/setBlueCoinFlag accepts indices 0..49 per shine stage.
// Shine ownership sync covers ids 0..255 (BSE EXTRA_SHINES + live payload0 byte).
// Must stay in lockstep with ProtocolConstants.ShineBitCapacity / WorldProgressSnapshot v2.
constexpr u16 kShineBitCapacity = 256;
constexpr u16 kShineBitsByteCount = kShineBitCapacity / 8;
// Vanilla shine ids are 0..119 (correctFlag counts 0x10000..0x10077). Movie 14
// (epilogue.thp) latches the Bowser clear reward as id 0x77 = 119 (the 120th).
constexpr u8 kBowserEpilogueShineId = 0x77u;
constexpr u8 kMaxStageShines = 48;
constexpr u8 kMaxStageBlueCoins = 50;
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
static u8 sKnownShinePosValid[kShineBitCapacity] = {};
static TVec3f sKnownShinePos[kShineBitCapacity] = {};

static bool sApplyingRemoteEvent = false;
static u16 sLocalWorldEventSequence = 0;

// Dual outbound world-event queues (Comm v14). Ownership and mission/ephemeral each have
// their own localPending mailbox slot — red/gold/fruit volume can never wedge shine/blue.
// Shared sequence counter keeps bridge seq space unique across both lanes.
constexpr u32 kLocalWorldEventQueueCap = 64;
constexpr u16 kLocalMissionQueueSoftCap = 24;
static smso::CommWorldEvent sOwnershipWorldEventQueue[kLocalWorldEventQueueCap] = {};
static u16 sOwnershipWorldEventQueueHead = 0;
static u16 sOwnershipWorldEventQueueCount = 0;
static smso::CommWorldEvent sMissionWorldEventQueue[kLocalWorldEventQueueCap] = {};
static u16 sMissionWorldEventQueueHead = 0;
static u16 sMissionWorldEventQueueCount = 0;
static u32 sWorldEventQueueDropCount = 0;
// Per-lane stuck tracking. Mission may be abandoned; ownership only bumps seq.
static u16 sOwnershipPendingStuckFrames = 0;
static u16 sOwnershipPendingStuckSeq = 0;
static u16 sOwnershipPendingStuckBumpCount = 0;
static u16 sMissionPendingStuckFrames = 0;
static u16 sMissionPendingStuckSeq = 0;
static u16 sMissionPendingStuckBumpCount = 0;
constexpr u16 kLocalPendingStuckBumpFrames = 90;
constexpr u16 kLocalPendingAbandonMissionBumps = 3; // ~4.5s — authorities heal red/NPC

static u32 sLastGoldCoinCount = 0;
static u8 sLastCourseId = 0xFF;
static u8 sLastEpisodeId = 0xFF;
static u8 sShineBits[kShineBitsByteCount] = {};
/// Session authority admission for shines — survives stageInit tracker reseed.
/// Without this, movie/load-time latches (Bowser 0x77) are absorbed into sShineBits
/// on the next stage enter and never emit ShineCollected to the server.
static u8 sAuthorityShineBits[kShineBitsByteCount] = {};
/// Published to the bridge but not yet echoed back by the server (authority snapshot /
/// ownership apply). Stage enter re-publishes these a bounded number of times so a lost
/// TCP publish cannot silently strand a shine that only this client knows about.
static u8 sPendingConfirmShineBits[kShineBitsByteCount] = {};
static u8 sShineConfirmRetryPasses = 0;
constexpr u8 kMaxShineConfirmRetryPasses = 3;
static u64 sBlueCoinBits = 0;
static u16 sStageSettleFrames = 0;
static u16 sCollectibleFallbackCountdown = 0;
constexpr u16 kCollectibleFallbackIntervalFrames = 120;
/// After SessionProgressReset, ignore durable ownership events with eventId at
/// or below this watermark so in-flight pre-reset packets cannot re-apply.
static u32 sIgnoreDurableAtOrBelowEventId = 0;

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
// After stage enter, refuse remote THipDropHideObj replays briefly so a deferred /
// late packet cannot hide virgin Ep5 purple casino panels on spawn.
static u16 sHipDropHideGraceFrames = 0;
constexpr u16 kHipDropHideStageEnterGrace = 90; // ~1.5s at 60Hz
static constexpr u8 kSirenaCasinoAreaId = 14;
static bool sCasinoHipDropDiagPending = false;
static u16 sCasinoHipDropDiagDelay = 0;
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
using StartAppearStarFn = void (*)(TGCConsole2 *);

static IncGoldCoinFlagFn gIncGoldCoinFlag = nullptr;
static CountShineFn gCountShine = nullptr;
static CountBlueCoinFn gCountBlueCoin = nullptr;
static StartAppearStarFn gStartAppearStar = nullptr;

static bool publishLocalWorldEvent(smso::WorldEventType type, u8 courseId, u8 episodeId, u8 payload0,
                                   u8 reserved, u32 payload1, u32 payload2 = 0);

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

// Sirena casino (14): director/mission uses beach ids 3/4; archive/catalog uses 0/1.
static bool sameStage(u8 courseId, u8 episodeId) {
    return smso::episode_equiv::sameStage(courseId, episodeId, currentCourseId(),
                                          currentEpisodeId());
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

static bool authorityShineWasSet(u8 shineId) {
    return (sAuthorityShineBits[shineId >> 3] & (1u << (shineId & 7))) != 0;
}

static void markAuthorityShine(u8 shineId) {
    sAuthorityShineBits[shineId >> 3] |= static_cast<u8>(1u << (shineId & 7));
}

static bool pendingConfirmShine(u8 shineId) {
    return (sPendingConfirmShineBits[shineId >> 3] & (1u << (shineId & 7))) != 0;
}

/// Local publish handed to the bridge — NOT yet proof the server accepted it. Only a
/// server-sourced apply / progress snapshot promotes a shine into the authority cache.
static void markPendingConfirmShine(u8 shineId) {
    sPendingConfirmShineBits[shineId >> 3] |= static_cast<u8>(1u << (shineId & 7));
}

/// Server confirmed ownership: stop retrying this shine on stage enter.
static void confirmAuthorityShine(u8 shineId) {
    markAuthorityShine(shineId);
    sPendingConfirmShineBits[shineId >> 3] &= static_cast<u8>(~(1u << (shineId & 7)));
}

/// Published locally and either already confirmed or out of retry budget.
static bool shinePublishSettled(u8 shineId) {
    if (authorityShineWasSet(shineId))
        return true;
    return pendingConfirmShine(shineId) &&
           sShineConfirmRetryPasses >= kMaxShineConfirmRetryPasses;
}

static void clearAuthorityShineBits() {
    for (u32 i = 0; i < sizeof(sAuthorityShineBits); ++i)
        sAuthorityShineBits[i] = 0;
    for (u32 i = 0; i < sizeof(sPendingConfirmShineBits); ++i)
        sPendingConfirmShineBits[i] = 0;
    sShineConfirmRetryPasses = 0;
}

static bool blueCoinWasSet(u8 coinIndex) {
    if (coinIndex >= kMaxStageBlueCoins)
        return false;
    return (sBlueCoinBits & (1ull << coinIndex)) != 0;
}

static void markBlueCoinSet(u8 coinIndex) {
    if (coinIndex >= kMaxStageBlueCoins)
        return;
    sBlueCoinBits |= 1ull << coinIndex;
}

/// Drop pending local durable progress publishes so a host Reset Progress cannot
/// be immediately undone by events already sitting in the outbound queues.
static bool isPurgableProgressWorldEventType(u8 type) {
    switch (type) {
    case smso::WE_SHINE_COLLECTED:
    case smso::WE_BLUE_COIN_COLLECTED:
    case smso::WE_RED_COIN_COLLECTED:
    case smso::WE_NPC_CLEANED:
    case smso::WE_GRAFFITI_CLEANED:
    case smso::WE_STORY_FLAG:
    case smso::WE_TRIGGER_FLAG:
    case smso::WE_SECRET_COMPLETE:
        return true;
    default:
        return false;
    }
}

static void purgeQueueProgressEvents(smso::CommWorldEvent *queue, u16 &head, u16 &count) {
    if (count == 0)
        return;

    u16 read = 0;
    u16 write = 0;
    const u16 total = count;
    while (read < total) {
        const u16 idx = static_cast<u16>((head + read) % kLocalWorldEventQueueCap);
        const smso::CommWorldEvent ev = queue[idx];
        ++read;
        if (isPurgableProgressWorldEventType(ev.type))
            continue;
        const u16 dst = static_cast<u16>((head + write) % kLocalWorldEventQueueCap);
        queue[dst] = ev;
        ++write;
    }
    count = write;
}

static void purgeLocalProgressWorldEvents() {
    purgeQueueProgressEvents(sOwnershipWorldEventQueue, sOwnershipWorldEventQueueHead,
                             sOwnershipWorldEventQueueCount);
    purgeQueueProgressEvents(sMissionWorldEventQueue, sMissionWorldEventQueueHead,
                             sMissionWorldEventQueueCount);
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
    // TRedCoinSwitch is intentionally not hooked — switch arming stays local-only.
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

    // Vanilla blue-coin identity is TMapObjBase::mMapObjID (0x134): TCoinBlue::loadBeforeInit
    // writes the stream ID there, and fireGetBlueCoin / makeObjAppeared / graffiti
    // TWaterHitPictureHideObj all gate on getBlueCoinFlag(area, mMapObjID).
    // Never match TCoin::_154 — on TCoinBlue that field stays 0 (or becomes a particle
    // pointer). Matching it treated every graffiti-spawned coin as index 0, so after any
    // real index-0 collect, reconcileCollectibleActors immediately killed newly appeared
    // graffiti coins ("spray cleans graffiti but no blue coin spawns").
    if (static_cast<u8>(obj->mMapObjID) != hideCtx->flagIndex)
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

    if (static_cast<u8>(obj->mMapObjID) != findCtx->flagIndex)
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

struct ReconcileCollectiblesCtx {
    TFlagManager *fm;
    u8 courseId;
};

static bool visitReconcileCollectible(TMapObjBase *obj, void *rawCtx) {
    auto *ctx = reinterpret_cast<ReconcileCollectiblesCtx *>(rawCtx);
    const u32 vtable = *reinterpret_cast<const u32 *>(obj);

    if (vtable == gCoinBlueVtable) {
        const u8 coinIndex = static_cast<u8>(obj->mMapObjID);
        if (coinIndex < kMaxStageBlueCoins && isCollectibleBlueCoin(obj) &&
            ctx->fm->getBlueCoinFlag(ctx->courseId, coinIndex))
            hideBlueCoinActor(obj);
        return false;
    }

    if (vtable != gShineVtable)
        return false;

    auto *shine = reinterpret_cast<TShine *>(obj);
    if (!isLiveCollectible(obj) || shine->mIsAlreadyObtained)
        return false;

    const s32 globalId = shineGlobalIdForActor(shine);
    if (globalId >= 0 && ctx->fm->getShineFlag(static_cast<u8>(globalId)) &&
        shouldHideShineActor(shine))
        hideShineActor(shine);
    return false;
}

static void reconcileVisibleCollectibles(TFlagManager *fm, u8 courseId) {
    if (!fm || gShineVtable == 0 || gCoinBlueVtable == 0)
        return;

    ReconcileCollectiblesCtx ctx = {fm, courseId};
    smso::forEachManagedMapObj(visitReconcileCollectible, &ctx);
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
    if (!outPos || sKnownShinePosValid[shineId] == 0)
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
    if (globalId >= 0 && globalId < static_cast<s32>(kShineBitCapacity)) {
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

/// Prefer immediate publish for Bowser epilogue shine 0x77 so peers receive it
/// without waiting on stage-update edge detect (movie context has no stageUpdate).
static bool tryPublishBowserEpilogueShineInternal(TFlagManager *fm, smso::CommBuffer *buf) {
    if (!fm || !buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0)
        return false;
    if ((buf->bridgeFlags & smso::BF_SYNC_SHINE) == 0)
        return false;
    if (!fm->getShineFlag(kBowserEpilogueShineId))
        return false;
    if (authorityShineWasSet(kBowserEpilogueShineId) ||
        pendingConfirmShine(kBowserEpilogueShineId))
        return true;

    const u8 courseId = currentCourseId();
    const u8 episodeId = currentEpisodeId();
    const u32 packedPos = packShinePublishPayload(kBowserEpilogueShineId);
    if (!publishLocalWorldEvent(smso::WE_SHINE_COLLECTED, courseId, episodeId,
                                kBowserEpilogueShineId, buf->localSlot, packedPos))
        return false;

    markShineSet(kBowserEpilogueShineId);
    markPendingConfirmShine(kBowserEpilogueShineId);
    OSReport("[SMSOBB] shine publish id=%u bowserEpilogue=1 course=%u/%u slot=%u\n",
             kBowserEpilogueShineId, courseId, episodeId, buf->localSlot);
    return true;
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
        OSReport("[SMSOBB] shine defer-visual id=%u (no actor on course=%u)\n", shineId,
                 currentCourseId());
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

    // The steady-state reconciliation is one manager traversal regardless of how many
    // collectibles are set. Keep the expensive ID/position fallback at a low cadence for
    // unusual or late-spawned shines whose global ID cannot be resolved from their actor.
    reconcileVisibleCollectibles(fm, courseId);
    if (sCollectibleFallbackCountdown > 0) {
        --sCollectibleFallbackCountdown;
        return;
    }
    sCollectibleFallbackCountdown = kCollectibleFallbackIntervalFrames;

    for (u16 shineId = 0; shineId < kShineBitCapacity; ++shineId) {
        if (!fm->getShineFlag(static_cast<u8>(shineId)))
            continue;

        u32 packed = 0;
        TVec3f pos{};
        if (lookupKnownShinePosition(static_cast<u8>(shineId), &pos))
            packed = packWorldPos(pos);
        hideCollectedShineActor(static_cast<u8>(shineId), packed);
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
    sCollectibleFallbackCountdown = 0;
    sStageShineSnapshotReady = false;
    sStageShineCount = 0;
    sPendingShineCapture = {};
    clearRemoteShineCollect();
    clearKnownShinePositions();
    smso::resetLocalYoshiFruitSync();
    smso::resetNpcSyncForStage();
    smso::resetStoryFlagTrackers();
    sLocalHipDropFired = false;
    sHipDropHideGraceFrames = kHipDropHideStageEnterGrace;
    if (courseId == kSirenaCasinoAreaId) {
        sCasinoHipDropDiagPending = true;
        sCasinoHipDropDiagDelay = 2; // after setupObjects + a couple of frames
    } else {
        sCasinoHipDropDiagPending = false;
        sCasinoHipDropDiagDelay = 0;
    }
    for (u32 i = 0; i < sizeof(sShineBits); ++i)
        sShineBits[i] = 0;

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return;

    sLastGoldCoinCount = static_cast<u32>(fm->getFlag(0x40002u));
    // Only seed trackers from shines already admitted to session authority.
    // Movie/load latches (Bowser epilogue 0x77) set FlagManager while stage
    // callbacks are inactive — absorbing them here would silence the 0→1 edge.
    // Shines published but never confirmed by the server are deliberately left unseeded
    // so this stage enter re-publishes them (server accepts are idempotent); the retry
    // budget stops that from repeating forever when progress sync is off.
    bool retriedUnconfirmedShine = false;
    for (u16 shineId = 0; shineId < kShineBitCapacity; ++shineId) {
        const u8 id = static_cast<u8>(shineId);
        if (!fm->getShineFlag(id))
            continue;
        if (shinePublishSettled(id))
            markShineSet(id);
        else if (pendingConfirmShine(id))
            retriedUnconfirmedShine = true;
    }
    if (retriedUnconfirmedShine && sShineConfirmRetryPasses < kMaxShineConfirmRetryPasses) {
        ++sShineConfirmRetryPasses;
        OSReport("[SMSOBB] shine publish unconfirmed — retry pass %u/%u\n",
                 static_cast<u32>(sShineConfirmRetryPasses),
                 static_cast<u32>(kMaxShineConfirmRetryPasses));
    }
    for (u8 coinIndex = 0; coinIndex < kMaxStageBlueCoins; ++coinIndex) {
        if (fm->getBlueCoinFlag(courseId, coinIndex))
            markBlueCoinSet(coinIndex);
    }
}

// Ephemeral live-only traffic — safe to evict when the outbound queue is full.
static bool isEphemeralWorldEventType(u8 type) {
    switch (type) {
    case smso::WE_NPC_REACT:
    case smso::WE_HIP_DROP_OBJECT:
    case smso::WE_YOSHI_FRUIT_TAKEN:
    case smso::WE_MARIO_FRUIT_KICKED:
    case smso::WE_MARIO_FRUIT_PICKED:
    case smso::WE_MARIO_FRUIT_THROWN:
    case smso::WE_MARIO_FRUIT_DROPPED:
    case smso::WE_MARIO_FRUIT_SYNC:
    case smso::WE_GRAFFITI_CLEANED: // legacy; never published after goop sync removal
    case smso::WE_GOLD_COIN_COLLECTED:
        return true;
    default:
        return false;
    }
}

// Card ownership — dedicated outbound localPendingOwnership lane.
static bool isCardOwnershipWorldEventType(u8 type) {
    switch (type) {
    case smso::WE_SHINE_COLLECTED:
    case smso::WE_BLUE_COIN_COLLECTED:
    case smso::WE_STORY_FLAG:
    case smso::WE_TRIGGER_FLAG:
    case smso::WE_SECRET_COMPLETE:
    case smso::WE_EPISODE_COMPLETE:
    case smso::WE_SESSION_PROGRESS_RESET:
        return true;
    default:
        return false;
    }
}

// Episode mission durables — heal via authority; may be abandoned if wedged.
static bool isMissionWorldEventType(u8 type) {
    switch (type) {
    case smso::WE_RED_COIN_COLLECTED:
    case smso::WE_NPC_CLEANED:
        return true;
    default:
        return false;
    }
}

static u16 countMissionQueued() {
    u16 count = 0;
    for (u16 i = 0; i < sMissionWorldEventQueueCount; ++i) {
        const u16 idx = static_cast<u16>(
            (sMissionWorldEventQueueHead + i) % kLocalWorldEventQueueCap);
        if (isMissionWorldEventType(sMissionWorldEventQueue[idx].type))
            ++count;
    }
    return count;
}

static bool dropOldestMissionMatching(bool (*pred)(u8 type)) {
    for (u16 i = 0; i < sMissionWorldEventQueueCount; ++i) {
        const u16 idx = static_cast<u16>(
            (sMissionWorldEventQueueHead + i) % kLocalWorldEventQueueCap);
        if (!pred(sMissionWorldEventQueue[idx].type))
            continue;

        for (u16 j = i; j + 1 < sMissionWorldEventQueueCount; ++j) {
            const u16 from = static_cast<u16>(
                (sMissionWorldEventQueueHead + j + 1) % kLocalWorldEventQueueCap);
            const u16 to = static_cast<u16>(
                (sMissionWorldEventQueueHead + j) % kLocalWorldEventQueueCap);
            sMissionWorldEventQueue[to] = sMissionWorldEventQueue[from];
        }
        --sMissionWorldEventQueueCount;
        return true;
    }
    return false;
}

// Ownership identity: same shine id / blue index / flag id on the same course+episode.
// payload2 (packed position) and reserved (collector slot) are cosmetic for the server,
// whose accepts are grow-only and idempotent.
static bool ownershipKeyMatches(const smso::CommWorldEvent &event, u8 type, u8 courseId,
                                u8 episodeId, u8 payload0, u32 payload1) {
    return event.type == type && event.courseId == courseId && event.episodeId == episodeId &&
           event.payload0 == payload0 && event.payload1 == payload1;
}

static bool ownershipKeyQueued(u8 type, u8 courseId, u8 episodeId, u8 payload0, u32 payload1) {
    for (u16 i = 0; i < sOwnershipWorldEventQueueCount; ++i) {
        const u16 idx = static_cast<u16>(
            (sOwnershipWorldEventQueueHead + i) % kLocalWorldEventQueueCap);
        if (ownershipKeyMatches(sOwnershipWorldEventQueue[idx], type, courseId, episodeId,
                                payload0, payload1))
            return true;
    }
    return false;
}

/// Collapse duplicate ownership keys, keeping the oldest of each. Mirrors the bridge's
/// incoming coalesce (BridgeWorker.TryCoalesceOldestOwnershipDuplicateUnlocked) so a burst
/// of repeats never forces a distinct card to be dropped.
static u16 coalesceOwnershipQueueDuplicates() {
    u16 removed = 0;
    u16 kept = 0;
    for (u16 i = 0; i < sOwnershipWorldEventQueueCount; ++i) {
        const u16 srcIdx = static_cast<u16>(
            (sOwnershipWorldEventQueueHead + i) % kLocalWorldEventQueueCap);
        const smso::CommWorldEvent src = sOwnershipWorldEventQueue[srcIdx];

        bool duplicate = false;
        for (u16 j = 0; j < kept; ++j) {
            const u16 keptIdx = static_cast<u16>(
                (sOwnershipWorldEventQueueHead + j) % kLocalWorldEventQueueCap);
            if (ownershipKeyMatches(sOwnershipWorldEventQueue[keptIdx], src.type, src.courseId,
                                    src.episodeId, src.payload0, src.payload1)) {
                duplicate = true;
                break;
            }
        }

        if (duplicate) {
            ++removed;
            continue;
        }

        // kept <= i, so the destination never overwrites an unvisited entry.
        const u16 dstIdx = static_cast<u16>(
            (sOwnershipWorldEventQueueHead + kept) % kLocalWorldEventQueueCap);
        if (dstIdx != srcIdx)
            sOwnershipWorldEventQueue[dstIdx] = src;
        ++kept;
    }

    sOwnershipWorldEventQueueCount = kept;
    return removed;
}

static bool makeRoomInOwnershipQueue() {
    if (sOwnershipWorldEventQueueCount < kLocalWorldEventQueueCap)
        return true;

    // Coalesce before ever dropping: ownership bits are durable and a dropped card is only
    // recoverable if the server already knows about it.
    const u16 coalesced = coalesceOwnershipQueueDuplicates();
    if (coalesced != 0) {
        OSReport("[SMSOBB] ownership queue full — coalesced %u duplicate(s), depth=%u\n",
                 static_cast<u32>(coalesced), static_cast<u32>(sOwnershipWorldEventQueueCount));
        if (sOwnershipWorldEventQueueCount < kLocalWorldEventQueueCap)
            return true;
    }

    ++sWorldEventQueueDropCount;
    OSReport("[SMSOBB] ownership queue full — dropping (drops=%u)\n",
             sWorldEventQueueDropCount);
    return false;
}

static bool makeRoomInMissionQueue(smso::WorldEventType incomingType) {
    const u8 incoming = static_cast<u8>(incomingType);

    if (isMissionWorldEventType(incoming)) {
        while (countMissionQueued() >= kLocalMissionQueueSoftCap) {
            if (!dropOldestMissionMatching(isMissionWorldEventType))
                break;
            ++sWorldEventQueueDropCount;
            OSReport("[SMSOBB] world-event mission cap — evicted red/NPC (drops=%u)\n",
                     sWorldEventQueueDropCount);
        }
    }

    if (sMissionWorldEventQueueCount < kLocalWorldEventQueueCap)
        return true;

    // Evict ephemeral first so fruit / NPC react never starve red/NPC mission bits.
    if (dropOldestMissionMatching(isEphemeralWorldEventType)) {
        ++sWorldEventQueueDropCount;
        OSReport("[SMSOBB] mission queue full — evicted ephemeral (drops=%u) for type=%u\n",
                 sWorldEventQueueDropCount, static_cast<u32>(incomingType));
        return true;
    }

    if (isMissionWorldEventType(incoming) &&
        dropOldestMissionMatching(isMissionWorldEventType)) {
        ++sWorldEventQueueDropCount;
        OSReport("[SMSOBB] mission queue full — evicted older mission for type=%u (drops=%u)\n",
                 static_cast<u32>(incomingType), sWorldEventQueueDropCount);
        return true;
    }

    ++sWorldEventQueueDropCount;
    OSReport("[SMSOBB] mission queue full — dropping type=%u (drops=%u)\n",
             static_cast<u32>(incomingType), sWorldEventQueueDropCount);
    return false;
}

static bool localPendingSlotIsFree(const smso::CommWorldEvent &slot) {
    return slot.sequence == 0 || slot.type == 0;
}

static u16 nextLocalWorldEventSequence() {
    u16 seq = ++sLocalWorldEventSequence;
    if (seq == 0)
        seq = ++sLocalWorldEventSequence;
    return seq;
}

static bool flushOneOwnershipWorldEvent() {
    if (sOwnershipWorldEventQueueCount == 0)
        return false;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0)
        return false;
    if (!localPendingSlotIsFree(buf->worldSync.localPendingOwnership))
        return false;

    smso::CommWorldEvent &slot = buf->worldSync.localPendingOwnership;
    slot = sOwnershipWorldEventQueue[sOwnershipWorldEventQueueHead];
    slot.sequence = nextLocalWorldEventSequence();
    sOwnershipWorldEventQueueHead =
        static_cast<u16>((sOwnershipWorldEventQueueHead + 1) % kLocalWorldEventQueueCap);
    --sOwnershipWorldEventQueueCount;
    sOwnershipPendingStuckFrames = 0;
    sOwnershipPendingStuckSeq = slot.sequence;
    sOwnershipPendingStuckBumpCount = 0;
    return true;
}

static bool flushOneMissionWorldEvent() {
    if (sMissionWorldEventQueueCount == 0)
        return false;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0)
        return false;
    if (!localPendingSlotIsFree(buf->worldSync.localPendingMission))
        return false;

    smso::CommWorldEvent &slot = buf->worldSync.localPendingMission;
    slot = sMissionWorldEventQueue[sMissionWorldEventQueueHead];
    slot.sequence = nextLocalWorldEventSequence();
    sMissionWorldEventQueueHead =
        static_cast<u16>((sMissionWorldEventQueueHead + 1) % kLocalWorldEventQueueCap);
    --sMissionWorldEventQueueCount;
    sMissionPendingStuckFrames = 0;
    sMissionPendingStuckSeq = slot.sequence;
    sMissionPendingStuckBumpCount = 0;
    return true;
}

static void flushLocalWorldEventQueue() {
    while (flushOneOwnershipWorldEvent()) {
    }
    while (flushOneMissionWorldEvent()) {
    }
}

static void abandonMissionPendingSlot(smso::CommBuffer *buf, const char *reason) {
    if (!buf)
        return;
    smso::CommWorldEvent &slot = buf->worldSync.localPendingMission;
    OSReport("[SMSOBB] localPendingAbandon lane=mission type=%u seq=%u (%s) queue=%u\n",
             static_cast<u32>(slot.type), static_cast<u32>(slot.sequence), reason,
             static_cast<u32>(sMissionWorldEventQueueCount));
    slot = {};
    sMissionPendingStuckFrames = 0;
    sMissionPendingStuckSeq = 0;
    sMissionPendingStuckBumpCount = 0;
}

static void bumpStuckOwnershipPendingIfNeeded(smso::CommBuffer *buf) {
    if (!buf)
        return;

    smso::CommWorldEvent &slot = buf->worldSync.localPendingOwnership;
    if (slot.sequence == 0 || slot.type == 0) {
        sOwnershipPendingStuckFrames = 0;
        sOwnershipPendingStuckSeq = 0;
        sOwnershipPendingStuckBumpCount = 0;
        return;
    }

    if (slot.sequence != sOwnershipPendingStuckSeq) {
        sOwnershipPendingStuckSeq = slot.sequence;
        sOwnershipPendingStuckFrames = 0;
        return;
    }

    if (++sOwnershipPendingStuckFrames < kLocalPendingStuckBumpFrames)
        return;

    sOwnershipPendingStuckFrames = 0;
    ++sOwnershipPendingStuckBumpCount;
    slot.sequence = nextLocalWorldEventSequence();
    sOwnershipPendingStuckSeq = slot.sequence;
    OSReport("[SMSOBB] localPendingOwnership stuck — bumped seq=%u type=%u bumps=%u\n",
             static_cast<u32>(slot.sequence), static_cast<u32>(slot.type),
             static_cast<u32>(sOwnershipPendingStuckBumpCount));
}

static void bumpStuckMissionPendingIfNeeded(smso::CommBuffer *buf) {
    if (!buf)
        return;

    smso::CommWorldEvent &slot = buf->worldSync.localPendingMission;
    if (slot.sequence == 0 || slot.type == 0) {
        sMissionPendingStuckFrames = 0;
        sMissionPendingStuckSeq = 0;
        sMissionPendingStuckBumpCount = 0;
        return;
    }

    if (slot.sequence != sMissionPendingStuckSeq) {
        sMissionPendingStuckSeq = slot.sequence;
        sMissionPendingStuckFrames = 0;
        return;
    }

    if (++sMissionPendingStuckFrames < kLocalPendingStuckBumpFrames)
        return;

    sMissionPendingStuckFrames = 0;
    ++sMissionPendingStuckBumpCount;

    // Mission/ephemeral may be abandoned — authorities heal red/NPC; fruit is ephemeral.
    if (sMissionPendingStuckBumpCount >= kLocalPendingAbandonMissionBumps) {
        abandonMissionPendingSlot(buf, "mission-timeout");
        flushOneMissionWorldEvent();
        return;
    }

    slot.sequence = nextLocalWorldEventSequence();
    sMissionPendingStuckSeq = slot.sequence;
    OSReport("[SMSOBB] localPendingMission stuck — bumped seq=%u type=%u bumps=%u\n",
             static_cast<u32>(slot.sequence), static_cast<u32>(slot.type),
             static_cast<u32>(sMissionPendingStuckBumpCount));
}

static void bumpStuckLocalPendingIfNeeded(smso::CommBuffer *buf) {
    bumpStuckOwnershipPendingIfNeeded(buf);
    bumpStuckMissionPendingIfNeeded(buf);
}

static bool publishLocalWorldEvent(smso::WorldEventType type, u8 courseId, u8 episodeId, u8 payload0,
                                   u8 reserved, u32 payload1, u32 payload2) {
    if (sApplyingRemoteEvent)
        return false;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!buf || (buf->bridgeFlags & smso::BF_CONNECTED) == 0)
        return false;

    switch (type) {
    case smso::WE_SHINE_COLLECTED:
        if (!worldSyncEnabled(buf) || (buf->bridgeFlags & smso::BF_SYNC_SHINE) == 0)
            return false;
        break;
    case smso::WE_BLUE_COIN_COLLECTED:
        if (!worldSyncEnabled(buf) || (buf->bridgeFlags & smso::BF_SYNC_BLUE_COIN) == 0)
            return false;
        break;
    case smso::WE_GOLD_COIN_COLLECTED:
    case smso::WE_HIP_DROP_OBJECT:
    case smso::WE_NPC_REACT:
    case smso::WE_YOSHI_FRUIT_TAKEN:
    case smso::WE_MARIO_FRUIT_KICKED:
    case smso::WE_MARIO_FRUIT_PICKED:
    case smso::WE_MARIO_FRUIT_THROWN:
    case smso::WE_MARIO_FRUIT_DROPPED:
    case smso::WE_MARIO_FRUIT_SYNC:
    case smso::WE_GRAFFITI_CLEANED:
        // Phase A (ModBuildId 28): never enqueue ephemeral / gold onto localPendingMission.
        // TCP is durable-only; these flooded mission lane + TCP under 10p.
        return false;
    case smso::WE_RED_COIN_COLLECTED:
        if ((buf->bridgeFlags & (smso::BF_SYNC_EVENT | smso::BF_SYNC_MISSION)) == 0)
            return false;
        break;
    case smso::WE_NPC_CLEANED:
        if ((buf->bridgeFlags & (smso::BF_SYNC_EVENT | smso::BF_SYNC_MISSION)) == 0)
            return false;
        break;
    case smso::WE_STORY_FLAG:
        if (!worldSyncEnabled(buf) || (buf->bridgeFlags & smso::BF_SYNC_STORY) == 0)
            return false;
        break;
    case smso::WE_TRIGGER_FLAG:
        if (!worldSyncEnabled(buf) ||
            (buf->bridgeFlags & (smso::BF_SYNC_STORY | smso::BF_SYNC_MISSION)) == 0)
            return false;
        break;
    case smso::WE_SECRET_COMPLETE:
        if (!worldSyncEnabled(buf) ||
            (buf->bridgeFlags & (smso::BF_SYNC_STORY | smso::BF_SYNC_SECRET)) == 0)
            return false;
        break;
    default:
        if (!worldSyncEnabled(buf))
            return false;
        break;
    }

    if (isCardOwnershipWorldEventType(static_cast<u8>(type))) {
        // Identical card already pending — treat as published (it will be sent) instead of
        // growing the queue toward a drop.
        if (ownershipKeyQueued(static_cast<u8>(type), courseId, episodeId, payload0, payload1))
            return true;
        if (!makeRoomInOwnershipQueue())
            return false;
        const u16 writeIndex = static_cast<u16>(
            (sOwnershipWorldEventQueueHead + sOwnershipWorldEventQueueCount) %
            kLocalWorldEventQueueCap);
        smso::CommWorldEvent &event = sOwnershipWorldEventQueue[writeIndex];
        event.eventId = 0;
        event.sequence = 0;
        event.type = static_cast<u8>(type);
        event.courseId = courseId;
        event.episodeId = episodeId;
        event.payload0 = payload0;
        event.reserved = reserved;
        event.payload1 = payload1;
        event.payload2 = payload2;
        ++sOwnershipWorldEventQueueCount;
        flushOneOwnershipWorldEvent();
        return true;
    }

    if (!makeRoomInMissionQueue(type))
        return false;

    const u16 writeIndex = static_cast<u16>(
        (sMissionWorldEventQueueHead + sMissionWorldEventQueueCount) % kLocalWorldEventQueueCap);
    smso::CommWorldEvent &event = sMissionWorldEventQueue[writeIndex];
    event.eventId = 0;
    event.sequence = 0;
    event.type = static_cast<u8>(type);
    event.courseId = courseId;
    event.episodeId = episodeId;
    event.payload0 = payload0;
    event.reserved = reserved;
    event.payload1 = payload1;
    event.payload2 = payload2;
    ++sMissionWorldEventQueueCount;
    flushOneMissionWorldEvent();
    return true;
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

// TGCConsole2 HUD caches (NTSC-U disasm):
//   shine displayed +0x64, shine count timer +0x8A, appear armed +0x34 / frame +0x5C
//   blue displayed +0x168
// countShine only increments displayed when FlagManager 0x40000 > displayed.
// countBlueCoin increments displayed whenever it disagrees with 0x40001 — even when
// the FlagManager total is *lower* — so a reset that leaves +0x168 stale makes the
// blue counter walk UP on every refreshHudCounters call.
constexpr u32 kHudOffAppearArmed = 0x34u;
constexpr u32 kHudOffAppearFrame = 0x5Cu;
constexpr u32 kHudOffShineDisplayed = 0x64u;
constexpr u32 kHudOffShineCountTimer = 0x8Au;
constexpr u32 kHudOffBlueDisplayed = 0x168u;
// countShine pane-refresh path when timer == 252 (before the +1 commit at 262).
constexpr u16 kHudShinePaneRefreshTimer = 252u;

// Retail shine HUD for *increases* only:
//   startAppearStar arms a one-shot card process. While armed, perform() drives
//   countShine across ~250 frames; countShine commits the shown total to +0x64.
// Force displayed behind the FlagManager total then spin so every newly set shine
// bumps digits immediately (second shine on same stage used to stall).
static void refreshShineHudLive(bool shineFlagChanged) {
    if (!shineFlagChanged)
        return;
    if (!gpMarDirector || !gpMarDirector->mGCConsole)
        return;
    if (!TFlagManager::smInstance)
        return;

    TGCConsole2 *console = gpMarDirector->mGCConsole;
    auto *base = reinterpret_cast<u8 *>(console);

    s32 *displayed = reinterpret_cast<s32 *>(base + kHudOffShineDisplayed);
    u16 *countTimer = reinterpret_cast<u16 *>(base + kHudOffShineCountTimer);
    u32 *appearFrame = reinterpret_cast<u32 *>(base + kHudOffAppearFrame);

    const s32 count = TFlagManager::smInstance->getFlag(0x40000u);
    if (count < 0)
        return;

    // Collection path only — never use this to move the counter downward.
    if (count == 0) {
        *displayed = 0;
        *countTimer = kHudShinePaneRefreshTimer;
        base[kHudOffAppearArmed] = 0;
        *appearFrame = 0;
        if (gCountShine)
            gCountShine(console);
        OSReport("[SMSOBB] shine hud-snap count=0 displayed=%d timer=%u\n", *displayed,
                 static_cast<u32>(*countTimer));
        return;
    }

    base[kHudOffAppearArmed] = 0;
    *appearFrame = 0;
    if (gStartAppearStar)
        gStartAppearStar(console);

    if (*displayed >= count)
        *displayed = count - 1;
    *countTimer = 0;

    if (gCountShine) {
        for (int i = 0; i < 320; ++i) {
            gCountShine(console);
            if (*displayed >= count)
                break;
        }
    }

    OSReport("[SMSOBB] shine hud-refresh count=%d displayed=%d timer=%u armed=%u\n", count,
             *displayed, static_cast<u32>(*countTimer), static_cast<u32>(base[kHudOffAppearArmed]));
}

enum class HudSnapMode : u8 {
    /// Session reset / forced clear — always rewrite both digit caches.
    ForceBoth = 0,
    /// Snapshot heals: never interrupt an in-progress shine star-card appear
    /// (local collect or refreshShineHudLive). Blue still snaps.
    PreserveShineAppear = 1,
};

/// Snap shine + blue HUD digit caches to FlagManager totals without the collect
/// count-up animation. Safe when totals decreased (session reset).
/// PreserveShineAppear avoids killing retail/startAppearStar mid-flight when a
/// coalesced WorldProgressSnapshot lands ~125 ms after a local/remote collect.
static void snapHudCountersToFlagManager(HudSnapMode mode = HudSnapMode::ForceBoth) {
    if (!gpMarDirector || !gpMarDirector->mGCConsole || !TFlagManager::smInstance)
        return;

    TGCConsole2 *console = gpMarDirector->mGCConsole;
    auto *base = reinterpret_cast<u8 *>(console);
    s32 *shineDisplayed = reinterpret_cast<s32 *>(base + kHudOffShineDisplayed);
    s32 *blueDisplayed = reinterpret_cast<s32 *>(base + kHudOffBlueDisplayed);
    u16 *shineTimer = reinterpret_cast<u16 *>(base + kHudOffShineCountTimer);
    u32 *appearFrame = reinterpret_cast<u32 *>(base + kHudOffAppearFrame);

    const s32 shineCount = TFlagManager::smInstance->getFlag(0x40000u);
    const s32 blueCount = TFlagManager::smInstance->getFlag(0x40001u);
    const bool shineAppearActive = base[kHudOffAppearArmed] != 0;

    if (mode == HudSnapMode::PreserveShineAppear) {
        if (shineAppearActive || *shineDisplayed == shineCount) {
            // Leave the star-card appear / already-matched digits alone.
            OSReport("[SMSOBB] hud-snap shine-skipped appear=%u displayed=%d count=%d\n",
                     shineAppearActive ? 1u : 0u, *shineDisplayed, shineCount);
        } else if (*shineDisplayed < shineCount) {
            // Digits stuck behind with no appear armed — bump via live path.
            refreshShineHudLive(true);
        } else {
            // displayed > count — hard snap down (reset / desync).
            base[kHudOffAppearArmed] = 0;
            *appearFrame = 0;
            *shineDisplayed = shineCount > 0 ? shineCount : 0;
            *shineTimer = kHudShinePaneRefreshTimer;
            if (gCountShine)
                gCountShine(console);
        }
    } else {
        // Disarm appear so perform cannot keep pumping a collect animation.
        base[kHudOffAppearArmed] = 0;
        *appearFrame = 0;

        // Shine: write cache then force the pane-refresh timer frame.
        *shineDisplayed = shineCount > 0 ? shineCount : 0;
        *shineTimer = kHudShinePaneRefreshTimer;
        if (gCountShine)
            gCountShine(console);
    }

    // Blue: countBlueCoin only runs its pane path after bumping displayed when
    // caches disagree. Seed displayed one below the target (or -1 when target is
    // 0) so a single call lands on the FlagManager total and redraws digits.
    *blueDisplayed = blueCount > 0 ? blueCount - 1 : -1;
    if (gCountBlueCoin)
        gCountBlueCoin(console);
    if (*blueDisplayed != blueCount)
        *blueDisplayed = blueCount > 0 ? blueCount : 0;

    OSReport("[SMSOBB] hud-snap shine=%d/%d blue=%d/%d mode=%u\n", *shineDisplayed, shineCount,
             *blueDisplayed, blueCount, static_cast<u32>(mode));
}

/// Mid-session "new file" progress clear: card ownership (shines/blues/nozzles/
/// story/secrets), plaza Type5 allowlist, HUD counters. Does NOT call
/// firstStart()/resetCard() — those wipe Type3 cutscene watched bits and saved
/// card backups and can soft-lock / re-fire FMVs mid-session. correctFlag()
/// restores always-set bits 0x1039A/0x1039D, min lives, and FLUDD water defaults.
static bool applySessionProgressReset(TFlagManager *fm) {
    if (!fm)
        return false;

    // Entire card bool bank (shines, blues, nozzles, story, secrets).
    for (u32 flag = 0x10000u; flag < 0x103B4u; ++flag)
        fm->setBool(false, flag);

    // Card ints (save count, lives, records, water). correctFlag restores safe mins.
    for (u32 flag = 0x20000u; flag < 0x20015u; ++flag)
        fm->setFlag(flag, 0);

    // Game ints: shine/blue/gold counts. Leave Type3 cutscene bools intact.
    fm->setFlag(0x40000u, 0);
    fm->setFlag(0x40001u, 0);
    fm->setFlag(0x40002u, 0);

#if BETTER_SMS_EXTRA_SHINES
    // BSE EXTRA_SHINES maps ownership for shine ids > 0x77 into Type6 bit
    // storage (flag 0x60040 + (id - 0x78)), not the card bool bank wiped above.
    // Without this, Host Reset Progress leaves extras set and the HUD/count can
    // rematch grow-only into session authority after the publish grace.
    // Clear the full sync capacity (0..255), not only getMaxShines(), so a
    // prior co-op heal that wrote ids past the local max still wipes clean.
    for (u32 shineId = 0x78u; shineId < kShineBitCapacity; ++shineId) {
        const u32 flagId = 0x60040u + (shineId - 0x78u);
        fm->setFlag(flagId, 0);
    }
#endif

    // Plaza hub Type5 allowlist (Ricco gate / lighthouse / MareGate).
    fm->setBool(false, 0x50001u);
    fm->setBool(false, 0x50002u);
    fm->setBool(false, 0x50004u);

    // Never leave spawn directors latched across a session wipe.
    fm->setBool(false, 0x30001u);
    fm->setBool(false, 0x30004u);

    fm->correctFlag();

    for (u32 i = 0; i < sizeof(sShineBits); ++i)
        sShineBits[i] = 0;
    clearAuthorityShineBits();
    sBlueCoinBits = 0;
    sPendingShineCapture = {};
    sLastGoldCoinCount = 0;
    clearKnownShinePositions();
    clearRemoteShineCollect();
    purgeLocalProgressWorldEvents();

    smso::clearStoryFlagSessionProgress();
    smso::notifyGraffitiCleanStageEnter();
    smso::notifyMonteCleanStageEnter();
    smso::notifyRedCoinStageEnter();

    snapHudCountersToFlagManager();
    OSReport("[SMSOBB] session progress reset applied (new-file scope, type6 extras cleared)\n");
    return true;
}

// Live-first ownership: FlagManager write + HUD always succeed. Stage visuals
// (actor hide / FX / get-star anim) are best-effort and must never block the
// durable mailbox — otherwise a stuck visual retry freezes shine/blue behind it.
static bool applyShineOwnershipFlag(TFlagManager *fm, const smso::CommWorldEvent &event,
                                    bool *changedOut) {
    if (changedOut)
        *changedOut = false;
    if (!fm)
        return false;

    const u8 shineId = event.payload0;
    const bool alreadySet = fm->getShineFlag(shineId);
    if (!alreadySet) {
        fm->setShineFlag(shineId);
        if (changedOut)
            *changedOut = true;
    }
    markShineSet(shineId);
    // Server-sourced apply — this is the ack that stops stage-enter re-publish.
    confirmAuthorityShine(shineId);
    if (event.payload1 != 0)
        rememberShinePosition(shineId, unpackWorldPos(event.payload1));

    // Noop applies dominate ownership-push storms — only log real mutations.
    if (!alreadySet) {
        OSReport("[SMSOBB] shine apply-flag id=%u changed=1 course=%u/%u collector=%u\n", shineId,
                 event.courseId, event.episodeId, event.reserved);
    }
    return true;
}

static void applyShineVisualReconcile(const smso::CommWorldEvent &event) {
    // Get-star anim / actor hide only when the shine actor exists on this stage.
    beginRemoteShineCollect(event.reserved, event.payload0, event.payload1);
}

static bool applyBlueCoinOwnershipFlag(TFlagManager *fm, const smso::CommWorldEvent &event,
                                       bool *changedOut, bool *alreadySetOut,
                                       bool *locallyTrackedOut) {
    if (changedOut)
        *changedOut = false;
    if (alreadySetOut)
        *alreadySetOut = false;
    if (locallyTrackedOut)
        *locallyTrackedOut = false;
    if (!fm)
        return false;

    const u8 flagIndex = event.payload0;
    if (flagIndex >= kMaxStageBlueCoins)
        return true;

    const bool alreadySet = fm->getBlueCoinFlag(event.courseId, flagIndex);
    // Read tracker before marking so host-echo hide/FX gating stays correct.
    const bool locallyTracked =
        event.courseId == currentCourseId() && blueCoinWasSet(flagIndex);

    if (!alreadySet) {
        fm->setBlueCoinFlag(event.courseId, flagIndex);
        if (changedOut)
            *changedOut = true;
    }

    // Only update the per-stage publish tracker for the course we are on —
    // marking another course's index would suppress a later local publish.
    if (event.courseId == currentCourseId())
        markBlueCoinSet(flagIndex);

    if (alreadySetOut)
        *alreadySetOut = alreadySet;
    if (locallyTrackedOut)
        *locallyTrackedOut = locallyTracked;

    if (!alreadySet) {
        OSReport("[SMSOBB] blue apply-flag course=%u idx=%u changed=1 localCourse=%u\n",
                 event.courseId, flagIndex, currentCourseId());
    }
    return true;
}

static void applyBlueCoinVisualReconcile(const smso::CommWorldEvent &event, bool alreadySet,
                                         bool locallyTracked) {
    const u8 flagIndex = event.payload0;
    if (flagIndex >= kMaxStageBlueCoins)
        return;

    const bool onCourse = event.courseId == currentCourseId();

    if (!onCourse) {
        OSReport("[SMSOBB] blue defer-visual course=%u idx=%u (local course=%u)\n", event.courseId,
                 flagIndex, currentCourseId());
        return;
    }

    // Resolve BEFORE hide — isCollectibleBlueCoin requires live+untaken, so a post-hide
    // lookup always fails and used to log blue defer-fx with no particles/SFX.
    // Ownership-push snapshots leave reserved=0 (ambiguous with host slot 0); gate FX on
    // first-time apply that we did not locally track. Local collectors already heard
    // vanilla taken(); snapshot echoes skip entirely once the flag is set.
    TVec3f coinPos{};
    const bool haveCoinPos = tryResolveBlueCoinWorldPos(flagIndex, &coinPos);

    // Skip hide on host echo after a local pickup — vanilla is still driving the coin actor.
    if (!alreadySet || !locallyTracked)
        hideBlueCoinAtIndex(flagIndex);

    if (!alreadySet && !locallyTracked && haveCoinPos)
        smso::playRemoteCoinCollectParticles(coinPos, true);
    else if (!alreadySet && !locallyTracked && !haveCoinPos) {
        OSReport("[SMSOBB] blue defer-fx course=%u idx=%u (actor not found)\n", event.courseId,
                 flagIndex);
    }
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
    case smso::WE_SHINE_COLLECTED: {
        bool shineChanged = false;
        if (!applyShineOwnershipFlag(fm, event, &shineChanged)) {
            applied = false;
            break;
        }
        // FlagManager 0x40000 already incremented by setShineFlag. Force the
        // on-screen star card + digit refresh on ANY stage (not plaza-only).
        refreshShineHudLive(shineChanged);
        applyShineVisualReconcile(event);
        break;
    }

    case smso::WE_BLUE_COIN_COLLECTED: {
        bool alreadySet = false;
        bool locallyTracked = false;
        if (!applyBlueCoinOwnershipFlag(fm, event, nullptr, &alreadySet, &locallyTracked)) {
            applied = false;
            break;
        }
        applyBlueCoinVisualReconcile(event, alreadySet, locallyTracked);
        break;
    }

    case smso::WE_SESSION_PROGRESS_RESET:
        // Watermark before clear so any durable packet still in the mailbox with
        // an older eventId is dropped instead of re-setting ownership.
        if (event.eventId != 0)
            sIgnoreDurableAtOrBelowEventId = event.eventId;
        applied = applySessionProgressReset(fm);
        break;

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

    case smso::WE_NPC_CLEANED:
        applied = smso::applyMonteCleanWorldEvent(event);
        break;

    case smso::WE_GRAFFITI_CLEANED:
        // Goop sync permanently disabled — consume legacy wire events so the
        // single incoming mailbox is not held forever (apply=false + durable
        // early-return would starve shine/story/red for the rest of the run).
        applied = true;
        break;

    case smso::WE_STORY_FLAG:
        applied = smso::applyStoryFlagWorldEvent(event);
        break;

    case smso::WE_TRIGGER_FLAG:
        applied = smso::applyTriggerFlagWorldEvent(event);
        break;

    case smso::WE_SECRET_COMPLETE:
        applied = smso::applySecretCompleteWorldEvent(event);
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

        const u32 objVt = *reinterpret_cast<const u32 *>(obj);
        OSReport("[SMSOBB] hip-drop apply hit vt=0x%08X id=%u from slot=%u\n", objVt,
                 event.payload0, event.reserved);

        // Never remotely arm red-coin switches — each player presses their own.
        if (objVt == kVtRedCoinSwitch) {
            OSReport("[SMSOBB] hip-drop skip red-coin switch (local-only)\n");
            break;
        }

        // THipDropHideObj::touchPlayer hides/activates the pad. On Sirena casino (14)
        // that includes the Ep5 purple roulette panel — never replay during stage-enter
        // grace, and always log so missing-panel bugs are diagnosable.
        if (gHipDropHideObjVtable != 0 && objVt == gHipDropHideObjVtable) {
            if (currentCourseId() == kSirenaCasinoAreaId) {
                OSReport("[SMSOBB] casino HipDropHideObj apply id=%u slot=%u grace=%u "
                         "course=%u/%u\n",
                         event.payload0, event.reserved, sHipDropHideGraceFrames,
                         event.courseId, event.episodeId);
                if (sHipDropHideGraceFrames > 0) {
                    OSReport("[SMSOBB] casino HipDropHideObj skipped (stage-enter grace)\n");
                    break;
                }
            } else if (sHipDropHideGraceFrames > 0) {
                OSReport("[SMSOBB] hip-drop skip HipDropHideObj during stage-enter grace\n");
                break;
            }
        }

        replayRemoteHipDropHit(obj, hipDropPayloadIsSuper(event.payload1));
        break;
    }

    case smso::WE_NPC_REACT: {
        if (!sameStage(event.courseId, event.episodeId)) {
            smso::deferRemoteNpcReact(event.payload0, event.reserved, event.payload1,
                                       event.payload2);
            // Never hold the durable mailbox for ephemeral NPC VFX — defer is enough.
            break;
        }
        smso::CommBuffer *buf = smso::getCommBuffer();
        const u8 localSlot = buf ? buf->localSlot : 0;
        if (event.reserved == localSlot)
            break;
        if (!buf || !objectSyncEnabled(buf))
            break;
        if (!smso::objectSyncGameplayReady()) {
            smso::deferRemoteNpcReact(event.payload0, event.reserved, event.payload1,
                                       event.payload2);
            break;
        }

        // Misses are deferred/retryable; always free the incoming slot so shine/story
        // ownership is never blocked behind plaza NPC spam under 10-player load.
        if (!smso::applyRemoteNpcReact(event.payload0, event.reserved, event.payload1,
                                       event.payload2)) {
            smso::deferRemoteNpcReact(event.payload0, event.reserved, event.payload1,
                                       event.payload2);
        }
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

    case smso::WE_MARIO_FRUIT_KICKED:
    case smso::WE_MARIO_FRUIT_PICKED:
    case smso::WE_MARIO_FRUIT_THROWN:
    case smso::WE_MARIO_FRUIT_DROPPED: {
        if (!sameStage(event.courseId, event.episodeId)) {
            smso::deferRemoteMarioFruitWorldEvent(event.type, event.payload0, event.reserved,
                                                    event.payload1, event.payload2);
            break;
        }
        if (!smso::objectSyncGameplayReady()) {
            smso::deferRemoteMarioFruitWorldEvent(event.type, event.payload0, event.reserved,
                                                  event.payload1, event.payload2);
            break;
        }
        smso::CommBuffer *buf = smso::getCommBuffer();
        const u8 localSlot = buf ? buf->localSlot : 0;
        if (event.reserved == localSlot)
            break;
        if (!buf || !objectSyncEnabled(buf))
            break;

        if (!smso::applyRemoteMarioFruitWorldEvent(event.type, event.payload0, event.reserved,
                                                   event.payload1, event.payload2)) {
            OSReport("[SMSOBB] mario-fruit apply miss type=%u enc=%u slot=%u packed=0x%08X\n",
                     event.type, event.payload0, event.reserved, event.payload1);
            applied = false;
        }
        break;
    }

    case smso::WE_MARIO_FRUIT_SYNC: {
        if (!sameStage(event.courseId, event.episodeId)) {
            smso::deferRemoteMarioFruitWorldEvent(event.type, event.payload0, event.reserved,
                                                  event.payload1, event.payload2);
            break;
        }
        if (!smso::objectSyncGameplayReady()) {
            smso::deferRemoteMarioFruitWorldEvent(event.type, event.payload0, event.reserved,
                                                  event.payload1, event.payload2);
            break;
        }
        smso::CommBuffer *buf = smso::getCommBuffer();
        const u8 localSlot = buf ? buf->localSlot : 0;
        if (event.reserved == localSlot)
            break;
        if (!buf || !objectSyncEnabled(buf))
            break;

        if (!smso::applyRemoteMarioFruitSync(event.payload0, event.reserved, event.payload1,
                                           event.payload2))
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

    if (sHipDropHideGraceFrames > 0)
        --sHipDropHideGraceFrames;

    if (sStageSettleFrames >= kStageSettleFrames && !sStageShineSnapshotReady)
        captureStageShineSnapshot();

    trackLocalShineCollection();
    resetLocalHipDropCaptureIfIdle();

    const u32 goldCoins = static_cast<u32>(fm->getFlag(0x40002u));
    if (goldCoins > sLastGoldCoinCount) {
        // Build 26: do NOT publish yellow/gold coins. Under 10p they flooded TCP +
        // localPendingMission (hundreds of events/min) and were implicated in mid-run
        // stage-enter soft-death. Gold is not required for 120-shine clear; keep local
        // count only. Remote apply path retained for mixed-build peers.
        sLastGoldCoinCount = goldCoins;
    }

    if ((buf->bridgeFlags & smso::BF_SYNC_SHINE) != 0) {
        // Prefer Bowser epilogue (0x77) — may already be set from movie context
        // before this stage's tracker seed, with no live 0→1 edge.
        tryPublishBowserEpilogueShineInternal(fm, buf);

        for (u16 shineId = 0; shineId < kShineBitCapacity; ++shineId) {
            const u8 id = static_cast<u8>(shineId);
            if (!fm->getShineFlag(id) || shineWasSet(id))
                continue;

            const u32 packedPos = packShinePublishPayload(id);
            if (!publishLocalWorldEvent(smso::WE_SHINE_COLLECTED, courseId, episodeId, id,
                                        buf->localSlot, packedPos))
                continue;

            // Mark only after enqueue so a queue-full drop can retry next frame. This is a
            // local publish, not an ack: the authority cache is only set once the server
            // echoes the shine back (ownership apply / progress snapshot).
            markShineSet(id);
            markPendingConfirmShine(id);
            OSReport("[SMSOBB] shine publish id=%u course=%u/%u slot=%u pos=0x%08X\n", id,
                     courseId, episodeId, buf->localSlot, packedPos);

            if (sPendingShineCapture.hasId && sPendingShineCapture.shineId == id)
                sPendingShineCapture = {};
            else if (sPendingShineCapture.hasPos && !sPendingShineCapture.hasId)
                sPendingShineCapture = {};
        }
    }

    if ((buf->bridgeFlags & smso::BF_SYNC_BLUE_COIN) != 0) {
        for (u8 coinIndex = 0; coinIndex < kMaxStageBlueCoins; ++coinIndex) {
            if (!fm->getBlueCoinFlag(courseId, coinIndex) || blueCoinWasSet(coinIndex))
                continue;
            if (!publishLocalWorldEvent(smso::WE_BLUE_COIN_COLLECTED, courseId, episodeId, coinIndex,
                                        buf->localSlot, 0))
                continue;
            markBlueCoinSet(coinIndex);
            OSReport("[SMSOBB] blue publish course=%u/%u idx=%u slot=%u\n", courseId, episodeId,
                     coinIndex, buf->localSlot);
        }
    }

    tickRemoteShineCollect();

    smso::captureLocalRedCoinProgress();
    smso::captureLocalStoryFlagProgress();
    smso::updateMonteCleanSync();
    smso::updateGraffitiCleanSync();
    reconcileCollectibleActors(fm, courseId);
}

void ensureHipDropObjectHooksImpl() {
    if (sHipDropHooksInstalled)
        return;
    initHipDropObjectHooks();
    sHipDropHooksInstalled = true;
    OSReport("[SMSOBB] hip-drop object hooks installed (%u receive vtables)\n", sVtReceiveHookCount);
}

static u16 readU16Le(const u8 *p) {
    return static_cast<u16>(p[0] | (static_cast<u16>(p[1]) << 8));
}

static u32 readU32Le(const u8 *p) {
    return static_cast<u32>(p[0]) | (static_cast<u32>(p[1]) << 8) | (static_cast<u32>(p[2]) << 16) |
           (static_cast<u32>(p[3]) << 24);
}

static u64 readU64Le(const u8 *p) {
    return static_cast<u64>(readU32Le(p)) | (static_cast<u64>(readU32Le(p + 4)) << 32);
}

static bool testShineBit(const u8 *bits, u16 bitsLen, u16 shineId) {
    if (shineId >= kShineBitCapacity || bitsLen < kShineBitsByteCount)
        return false;
    return (bits[shineId >> 3] & (1u << (shineId & 7))) != 0;
}

/// Bulk-apply a LE WorldProgressSnapshot TCP payload into FlagManager + mission sync.
static bool applyProgressSnapshotPayload(const u8 *payload, u16 payloadLen) {
    if (!payload || payloadLen < 6)
        return false;

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return false;

    u32 offset = 0;
    const u8 version = payload[offset++];
    // FormatVersion 2 = 256-bit shine ownership. v1 (128-bit) is a hard cut with ModBuildId.
    if (version != 2)
        return false;
    const u8 flags = payload[offset++];
    const u32 progressSeq = readU32Le(payload + offset);
    offset += 4;
    (void)progressSeq;

    if ((flags & 1) != 0)
        return true;

    if (payloadLen < offset + kShineBitsByteCount)
        return false;

    sApplyingRemoteEvent = true;

    const u8 *shineBits = payload + offset;
    offset += kShineBitsByteCount;
    u32 shineChanged = 0;
    for (u16 shineId = 0; shineId < kShineBitCapacity; ++shineId) {
        if (!testShineBit(shineBits, kShineBitsByteCount, shineId))
            continue;
        // Already owned locally — skip FlagManager/OSReport walk. Ownership-push storms
        // re-applied tens of thousands of changed=0 flags per session (CPU soft-death).
        // Still record the confirmation: the snapshot proves the server holds this shine.
        if (shineWasSet(static_cast<u8>(shineId)) && fm->getShineFlag(static_cast<u8>(shineId))) {
            confirmAuthorityShine(static_cast<u8>(shineId));
            continue;
        }
        smso::CommWorldEvent ev{};
        ev.eventId = 0x70000000u + shineId;
        ev.type = static_cast<u8>(smso::WE_SHINE_COLLECTED);
        ev.payload0 = static_cast<u8>(shineId);
        bool changed = false;
        if (applyShineOwnershipFlag(fm, ev, &changed) && changed)
            ++shineChanged;
    }
    if (shineChanged != 0)
        refreshShineHudLive(true);

    // Snapshot heals must not ForceBoth-snap the shine HUD: coalesced ownership
    // pushes land ~125 ms after local/remote collect and would disarm startAppearStar
    // mid-flight (partial star-card / counter glitch).
    constexpr auto kSnapMode = HudSnapMode::PreserveShineAppear;

    if (payloadLen < offset + 1) {
        sApplyingRemoteEvent = false;
        snapHudCountersToFlagManager(kSnapMode);
        return true;
    }

    const u8 blueCount = payload[offset++];
    for (u8 i = 0; i < blueCount; ++i) {
        if (payloadLen < offset + 9)
            break;
        const u8 courseId = payload[offset++];
        const u64 mask = readU64Le(payload + offset);
        offset += 8;
        for (u8 index = 0; index < 50; ++index) {
            if ((mask & (1ull << index)) == 0)
                continue;
            // Already owned — skip apply/OSReport/defer-visual on ownership-push echoes.
            if (fm->getBlueCoinFlag(courseId, index)) {
                if (courseId == currentCourseId())
                    markBlueCoinSet(index);
                continue;
            }
            smso::CommWorldEvent ev{};
            ev.eventId = 0x70010000u + (static_cast<u32>(courseId) << 8) + index;
            ev.type = static_cast<u8>(smso::WE_BLUE_COIN_COLLECTED);
            ev.courseId = courseId;
            ev.payload0 = index;
            bool alreadySet = false;
            bool locallyTracked = false;
            if (!applyBlueCoinOwnershipFlag(fm, ev, nullptr, &alreadySet, &locallyTracked))
                continue;
            applyBlueCoinVisualReconcile(ev, alreadySet, locallyTracked);
        }
    }

    auto readU16Count = [&](u16 *out) -> bool {
        if (payloadLen < offset + 2)
            return false;
        *out = readU16Le(payload + offset);
        offset += 2;
        return true;
    };

    u16 storyCount = 0;
    if (!readU16Count(&storyCount)) {
        sApplyingRemoteEvent = false;
        snapHudCountersToFlagManager(kSnapMode);
        return true;
    }
    for (u16 i = 0; i < storyCount; ++i) {
        if (payloadLen < offset + 5)
            break;
        const u32 flagId = readU32Le(payload + offset);
        offset += 4;
        const u8 value = payload[offset++];
        smso::CommWorldEvent ev{};
        ev.eventId = 0x70020000u + i;
        ev.type = static_cast<u8>(smso::WE_STORY_FLAG);
        ev.payload0 = value;
        ev.payload1 = flagId;
        smso::applyStoryFlagWorldEvent(ev);
    }

    u16 triggerCount = 0;
    if (!readU16Count(&triggerCount)) {
        sApplyingRemoteEvent = false;
        snapHudCountersToFlagManager(kSnapMode);
        return true;
    }
    for (u16 i = 0; i < triggerCount; ++i) {
        if (payloadLen < offset + 7)
            break;
        const u8 courseId = payload[offset++];
        const u8 episodeId = payload[offset++];
        const u32 flagId = readU32Le(payload + offset);
        offset += 4;
        const u8 value = payload[offset++];
        smso::CommWorldEvent ev{};
        ev.eventId = 0x70030000u + i;
        ev.type = static_cast<u8>(smso::WE_TRIGGER_FLAG);
        ev.courseId = courseId;
        ev.episodeId = episodeId;
        ev.payload0 = value;
        ev.payload1 = flagId;
        smso::applyTriggerFlagWorldEvent(ev);
    }

    u16 secretCount = 0;
    if (!readU16Count(&secretCount)) {
        sApplyingRemoteEvent = false;
        snapHudCountersToFlagManager(kSnapMode);
        return true;
    }
    for (u16 i = 0; i < secretCount; ++i) {
        if (payloadLen < offset + 5)
            break;
        const u32 flagId = readU32Le(payload + offset);
        offset += 4;
        const u8 value = payload[offset++];
        smso::CommWorldEvent ev{};
        ev.eventId = 0x70040000u + i;
        ev.type = static_cast<u8>(smso::WE_SECRET_COMPLETE);
        ev.payload0 = value;
        ev.payload1 = flagId;
        smso::applySecretCompleteWorldEvent(ev);
    }

    u16 redCount = 0;
    if (!readU16Count(&redCount)) {
        sApplyingRemoteEvent = false;
        snapHudCountersToFlagManager(kSnapMode);
        return true;
    }
    for (u16 i = 0; i < redCount; ++i) {
        if (payloadLen < offset + 3 + 32)
            break;
        const u8 courseId = payload[offset++];
        const u8 episodeId = payload[offset++];
        const u8 mask = payload[offset++];
        u32 packedPos[8];
        for (u8 p = 0; p < 8; ++p) {
            packedPos[p] = readU32Le(payload + offset);
            offset += 4;
        }
        u32 pop = 0;
        for (u8 index = 0; index < 8; ++index) {
            if ((mask & (1u << index)) != 0)
                ++pop;
        }
        for (u8 index = 0; index < 8; ++index) {
            if ((mask & (1u << index)) == 0)
                continue;
            smso::CommWorldEvent ev{};
            ev.eventId = 0x70050000u + (static_cast<u32>(i) << 4) + index;
            ev.type = static_cast<u8>(smso::WE_RED_COIN_COLLECTED);
            ev.courseId = courseId;
            ev.episodeId = episodeId;
            ev.payload0 = static_cast<u8>((pop << 4) | index);
            ev.reserved = index;
            ev.payload1 = mask;
            ev.payload2 = packedPos[index];
            applyRedCoinWorldEvent(ev);
        }
    }

    u16 npcCount = 0;
    if (!readU16Count(&npcCount)) {
        sApplyingRemoteEvent = false;
        snapHudCountersToFlagManager(kSnapMode);
        return true;
    }
    for (u16 i = 0; i < npcCount; ++i) {
        if (payloadLen < offset + 4)
            break;
        const u8 courseId = payload[offset++];
        const u8 episodeId = payload[offset++];
        const u16 mask = readU16Le(payload + offset);
        offset += 2;
        u32 pop = 0;
        for (u8 index = 0; index < 16; ++index) {
            if ((mask & (1u << index)) != 0)
                ++pop;
        }
        for (u8 index = 0; index < 16; ++index) {
            if ((mask & (1u << index)) == 0)
                continue;
            smso::CommWorldEvent ev{};
            ev.eventId = 0x70060000u + (static_cast<u32>(i) << 4) + index;
            ev.type = static_cast<u8>(smso::WE_NPC_CLEANED);
            ev.courseId = courseId;
            ev.episodeId = episodeId;
            ev.payload0 = static_cast<u8>((pop << 4) | (index & 0xF));
            ev.reserved = index;
            smso::applyMonteCleanWorldEvent(ev);
        }
    }

    sApplyingRemoteEvent = false;
    snapHudCountersToFlagManager(kSnapMode);
    OSReport("[SMSOBB] progress snapshot bulk-applied bytes=%u shinesChanged=%u\n", payloadLen,
             shineChanged);
    return true;
}

static void applyIncomingWorldEventSlot(smso::CommBuffer *buf, smso::CommWorldEvent &incoming) {
    if (incoming.eventId == 0 || incoming.type == 0)
        return;

    const bool durableCollectible =
        incoming.type == static_cast<u8>(smso::WE_SHINE_COLLECTED) ||
        incoming.type == static_cast<u8>(smso::WE_BLUE_COIN_COLLECTED) ||
        incoming.type == static_cast<u8>(smso::WE_RED_COIN_COLLECTED) ||
        incoming.type == static_cast<u8>(smso::WE_NPC_CLEANED) ||
        incoming.type == static_cast<u8>(smso::WE_STORY_FLAG) ||
        incoming.type == static_cast<u8>(smso::WE_TRIGGER_FLAG) ||
        incoming.type == static_cast<u8>(smso::WE_SECRET_COMPLETE);

    const bool isProgressReset =
        incoming.type == static_cast<u8>(smso::WE_SESSION_PROGRESS_RESET);
    const bool staleAfterProgressReset =
        durableCollectible && sIgnoreDurableAtOrBelowEventId != 0 &&
        incoming.eventId <= sIgnoreDurableAtOrBelowEventId;
    const bool shouldAttempt =
        isProgressReset ||
        (!staleAfterProgressReset &&
         (durableCollectible || incoming.eventId > buf->worldSync.lastAppliedEventId));

    if (shouldAttempt) {
        if (applyWorldEvent(incoming)) {
            if (incoming.eventId > buf->worldSync.lastAppliedEventId)
                buf->worldSync.lastAppliedEventId = incoming.eventId;
        } else if (durableCollectible) {
            // Only FlagManager-missing should fail now — never hold the slot for settle.
            return;
        }
    }

    incoming = {};
}

static void processProgressSnapshotMailbox(smso::CommBuffer *buf) {
    if (!buf)
        return;

    auto &slot = buf->progressSnapshot;
    if (slot.hostSeq == 0 || slot.hostSeq <= slot.moduleAppliedSeq)
        return;
    if (slot.payloadLen == 0 || slot.payloadLen > smso::COMM_PROGRESS_SNAPSHOT_MAX_PAYLOAD) {
        slot.moduleAppliedSeq = slot.hostSeq;
        return;
    }

    if (!applyProgressSnapshotPayload(slot.payload, slot.payloadLen))
        return;

    slot.moduleAppliedSeq = slot.hostSeq;
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
    initMonteCleanSync();
    initGraffitiCleanSync();
    initStoryFlagSync();
    initFruitSync();
    initNpcSync();
    clearRemoteShineCollect();
    sPendingShineCapture = {};
    clearKnownShinePositions();
    clearAuthorityShineBits();
    gIncGoldCoinFlag =
        reinterpret_cast<IncGoldCoinFlagFn>(SMS_PORT_REGION(0x80294610, 0x8028C428, 0, 0));
    gCountShine = reinterpret_cast<CountShineFn>(SMS_PORT_REGION(0x80147A0C, 0x8013C690, 0, 0));
    gCountBlueCoin =
        reinterpret_cast<CountBlueCoinFn>(SMS_PORT_REGION(0x8014757C, 0x8013C200, 0, 0));
    gStartAppearStar =
        reinterpret_cast<StartAppearStarFn>(SMS_PORT_REGION(0x80149B00, 0x8013E790, 0, 0));
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

void tryPublishBowserEpilogueShine() {
    smso::CommBuffer *buf = smso::getCommBuffer();
    TFlagManager *fm = TFlagManager::smInstance;
    tryPublishBowserEpilogueShineInternal(fm, buf);
}

void processWorldEvents() {
    CommBuffer *buf = getCommBuffer();
    const bool connected =
        buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0;
    static bool sWasConnected = false;
    if (!connected) {
        // Server eventIds restart low on a new session. A stale reset watermark
        // or high lastApplied from a prior lobby would drop durable heals / resets.
        if (sWasConnected) {
            sIgnoreDurableAtOrBelowEventId = 0;
            clearAuthorityShineBits();
            if (buf) {
                buf->worldSync.lastAppliedEventId = 0;
                buf->progressSnapshot.hostSeq = 0;
                buf->progressSnapshot.moduleAppliedSeq = 0;
                buf->progressSnapshot.payloadLen = 0;
                buf->progressSnapshot.flags = 0;
            }
            OSReport("[SMSOBB] progress-reset watermark + lastApplied + snapshot lane cleared (disconnect)\n");
        }
        sWasConnected = false;
    } else {
        sWasConnected = true;
    }
    const bool storySync =
        connected &&
        (buf->bridgeFlags &
         (smso::BF_SYNC_STORY | smso::BF_SYNC_MISSION | smso::BF_SYNC_SECRET)) != 0;
    updateStoryFlagSyncConnectionState(connected, storySync);
    if (!connected)
        return;

    const bool progressSync = worldSyncEnabled(buf);
    const bool objectsSync = objectSyncEnabled(buf);
    if (!progressSync && !objectsSync)
        return;

    if (progressSync)
        captureLocalWorldProgress();
    else {
        resetLocalHipDropCaptureIfIdle();
        if (objectsSync) {
            const u8 courseId = currentCourseId();
            const u8 episodeId = currentEpisodeId();
            if (courseId != sLastCourseId || episodeId != sLastEpisodeId)
                resetLocalTrackersForStage(courseId, episodeId);
            if (sStageSettleFrames < kStageSettleFrames)
                ++sStageSettleFrames;
            if (sHipDropHideGraceFrames > 0)
                --sHipDropHideGraceFrames;
        }
    }

    const bool fruitGameplayReady = objectSyncGameplayReady();
    if (objectsSync && fruitGameplayReady)
        updateLocalMarioFruitCapture(gpMarioAddress);

    if (objectsSync && fruitGameplayReady)
        retryPendingRemoteFruitEvents();

    if (objectsSync && fruitGameplayReady)
        retryPendingRemoteNpcEvents();

    if (objectsSync && fruitGameplayReady)
        updateNpcReactSync();

    flushLocalWorldEventQueue();
    bumpStuckLocalPendingIfNeeded(buf);

    processProgressSnapshotMailbox(buf);

    // Dedicated ownership lane first — never blocked by mission/ephemeral in `incoming`.
    applyIncomingWorldEventSlot(buf, buf->worldSync.incomingOwnership);
    applyIncomingWorldEventSlot(buf, buf->worldSync.incoming);
}

bool enqueueLocalWorldEvent(u8 type, u8 courseId, u8 episodeId, u8 payload0, u8 reserved,
                            u32 payload1, u32 payload2) {
    return publishLocalWorldEvent(static_cast<smso::WorldEventType>(type), courseId, episodeId,
                                  payload0, reserved, payload1, payload2);
}

bool objectSyncGameplayReady() {
    if (!gpMarDirector || gpMarDirector->mCurState != TMarDirector::STATE_NORMAL)
        return false;
    return sStageSettleFrames >= kStageSettleFrames;
}

struct CasinoHipDropDiagCtx {
    u32 hipDropLive;
    u32 hipDropDead;
    u32 roulette;
};

static bool visitCasinoHipDropDiag(TMapObjBase *obj, void *rawCtx) {
    auto *ctx = reinterpret_cast<CasinoHipDropDiagCtx *>(rawCtx);
    if (!obj)
        return false;
    const u32 vt = *reinterpret_cast<const u32 *>(obj);
    if (gHipDropHideObjVtable != 0 && vt == gHipDropHideObjVtable) {
        if (isLiveCollectible(obj)) {
            ++ctx->hipDropLive;
            const TVec3f pos = hipDropObjWorldPos(obj);
            OSReport("[SMSOBB] casino HipDropHideObj[%u] live pos=(%.0f,%.0f,%.0f)\n",
                     ctx->hipDropLive - 1, pos.x, pos.y, pos.z);
        } else {
            ++ctx->hipDropDead;
        }
        return false;
    }
    // casinorulet MapObjData actor type 0x4000019A
    if (reinterpret_cast<const THitActor *>(obj)->mObjectID == 0x4000019Au)
        ++ctx->roulette;
    return false;
}

void reportCasinoHipDropSpawnDiag(u8 areaId) {
    if (areaId != kSirenaCasinoAreaId) {
        sCasinoHipDropDiagPending = false;
        sCasinoHipDropDiagDelay = 0;
        return;
    }

    if (!sCasinoHipDropDiagPending) {
        sCasinoHipDropDiagPending = true;
        sCasinoHipDropDiagDelay = 2;
        return;
    }

    if (sCasinoHipDropDiagDelay > 0) {
        --sCasinoHipDropDiagDelay;
        return;
    }

    sCasinoHipDropDiagPending = false;
    ensureHipDropObjectHooks();

    CasinoHipDropDiagCtx ctx = {};
    smso::forEachManagedMapObj(visitCasinoHipDropDiag, &ctx);

    const u8 dirEp = gpMarDirector ? gpMarDirector->mEpisodeID : 0xFF;
    const u8 sceneEp = gpApplication.mCurrentScene.mEpisodeID;
    const u8 flagEp =
        TFlagManager::smInstance
            ? static_cast<u8>(TFlagManager::smInstance->getFlag(0x40003))
            : static_cast<u8>(0xFF);
    OSReport("[SMSOBB] casino HipDrop diag area=%u director=%u sceneLoad=%u flagMission=%u "
             "HipDropHideObj live=%u dead=%u casinorulet=%u\n",
             areaId, dirEp, sceneEp, flagEp, ctx.hipDropLive, ctx.hipDropDead, ctx.roulette);
}

} // namespace smso
