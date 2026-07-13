#include "red_coin_sync.hpp"

#include "coin_collect_fx.hpp"
#include "collectible_scan.hpp"
#include "comm_buffer.hpp"
#include "world_sync.hpp"

#include <SMS/GC2D/GCConsole2.hxx>
#include <SMS/Manager/FlagManager.hxx>
#include <SMS/Manager/ItemManager.hxx>
#include <SMS/Manager/ObjManager.hxx>
#include <SMS/MapObj/MapObjBase.hxx>
#include <SMS/MoveBG/Coin.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Strategic/HitActor.hxx>
#include <SMS/Strategic/LiveActor.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>

extern TMarDirector *gpMarDirector;
extern TItemManager *gpItemManager;
extern TMario *gpMarioAddress;

struct TMapObjManager;
extern TMapObjManager *gpMapObjManager;

namespace {

constexpr u32 kStageSettleFrames = 90;
constexpr f32 kPosMatchEpsilon = 4.0f;
// Network positions use scale-16 packing; decoded coords can be up to one quanta off.
constexpr f32 kNetworkRedCoinPosEpsilon = 18.0f;
constexpr u32 kCoinTakenFlagOffset = 0x152;
constexpr u8 kMaxStageRedCoins = 8;
constexpr u8 kInvalidHudSlot = 0xFF;

static bool sApplyingRemoteEvent = false;

static u32 sLastRedCoinCount = 0;
static u8 sLastCourseId = 0xFF;
static u8 sLastEpisodeId = 0xFF;
static u16 sStageSettleFrames = 0;
static bool sStageSnapshotReady = false;
static u8 sCollectedMask = 0;
// Set by stageInit so same course/episode reloads still reset trackers.
static bool sForceStageTrackerReset = false;
// Remote collections recorded while the local switch mission was idle; flush HUD
// once the local player presses the switch (or live coins appear on pre-placed maps).
static bool sPendingHudCatchUp = false;
static u8 sHudAppliedMask = 0;

static TCoin *sPrevLiveCoins[kMaxStageRedCoins] = {};
static u8 sPrevLiveCoinCount = 0;

static smso::CommWorldEvent sDeferredRedCoinEvents[16] = {};
static u8 sDeferredRedCoinCount = 0;

struct StageRedCoinEntry {
    u16 mapObjId;
    u8 hudSlot;
    u8 stableIndex;
    bool active;
    TVec3f initialPos;
};

static StageRedCoinEntry sStageRedCoins[kMaxStageRedCoins] = {};
static u8 sStageRedCoinCount = 0;

static TVec3f sCollectedPositions[kMaxStageRedCoins] = {};
static u8 sCollectedPositionCount = 0;

using ProcessDownCoinFn = void (*)(TGCConsole2 *, int);

static ProcessDownCoinFn gProcessDownCoin = nullptr;
static u32 gCoinRedVtable = 0;
static u32 gCoinEmptyVtable = 0;

struct SortedCoinCtx;
static u8 popCountMask(u8 mask);
static void reconcileCollectedRedCoinActors();
static void gatherSortedLiveRedCoins(SortedCoinCtx *sorted);

static u8 currentCourseId() {
    return gpMarDirector ? gpMarDirector->mAreaID : 0;
}

static u8 currentEpisodeId() {
    return gpMarDirector ? gpMarDirector->mEpisodeID : 0;
}

static bool isHubArea(u8 areaId) {
    return areaId == 15;
}

static bool redCoinPublishEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0 &&
           (buf->bridgeFlags & (smso::BF_SYNC_EVENT | smso::BF_SYNC_MISSION)) != 0;
}

static bool redCoinScanReady(const smso::CommBuffer *buf) {
    return redCoinPublishEnabled(buf) && sStageSettleFrames >= kStageSettleFrames;
}

static bool sameStage(u8 courseId, u8 episodeId) {
    return courseId == currentCourseId() && episodeId == currentEpisodeId();
}

static bool isCollectibleRedCoin(const TMapObjBase *obj) {
    if (!obj || !smso::isValidMapObjPtr(obj))
        return false;
    const u32 vtable = *reinterpret_cast<const u32 *>(obj);
    if (vtable != gCoinRedVtable)
        return false;
    const auto *bytes = reinterpret_cast<const u8 *>(obj);
    if (bytes[kCoinTakenFlagOffset] != 0)
        return false;
    const auto *live = reinterpret_cast<const TLiveActor *>(obj);
    return !live->mStateFlags.asFlags.mIsObjDead;
}

static bool positionsMatch(const TVec3f &a, const TVec3f &b) {
    const f32 dx = a.x - b.x;
    const f32 dy = a.y - b.y;
    const f32 dz = a.z - b.z;
    return dx * dx + dy * dy + dz * dz <= kPosMatchEpsilon * kPosMatchEpsilon;
}

static int compareCoinPos(const TMapObjBase *a, const TMapObjBase *b) {
    if (a->mInitialPosition.x != b->mInitialPosition.x)
        return a->mInitialPosition.x < b->mInitialPosition.x ? -1 : 1;
    if (a->mInitialPosition.y != b->mInitialPosition.y)
        return a->mInitialPosition.y < b->mInitialPosition.y ? -1 : 1;
    if (a->mInitialPosition.z != b->mInitialPosition.z)
        return a->mInitialPosition.z < b->mInitialPosition.z ? -1 : 1;
    return 0;
}

static void forEachManagedMapObj(bool (*visitor)(TMapObjBase *, void *), void *ctx) {
    smso::forEachManagedMapObj(
        reinterpret_cast<smso::MapObjVisitorFn>(visitor), ctx);
}

struct SortedCoinCtx {
    TCoin *coins[kMaxStageRedCoins];
    u8 count;
};

static bool visitGatherLiveRedCoin(TMapObjBase *obj, void *ctx) {
    auto *sorted = reinterpret_cast<SortedCoinCtx *>(ctx);
    if (sorted->count >= kMaxStageRedCoins || !isCollectibleRedCoin(obj))
        return false;
    sorted->coins[sorted->count++] = reinterpret_cast<TCoin *>(obj);
    return false;
}

static void sortGatheredCoins(SortedCoinCtx *sorted) {
    for (u8 i = 0; i + 1 < sorted->count; ++i) {
        for (u8 j = i + 1; j < sorted->count; ++j) {
            auto *a = reinterpret_cast<TMapObjBase *>(sorted->coins[i]);
            auto *b = reinterpret_cast<TMapObjBase *>(sorted->coins[j]);
            if (compareCoinPos(a, b) > 0) {
                TCoin *tmp = sorted->coins[i];
                sorted->coins[i] = sorted->coins[j];
                sorted->coins[j] = tmp;
            }
        }
    }
}

static void gatherSortedLiveRedCoins(SortedCoinCtx *sorted) {
    sorted->count = 0;
    forEachManagedMapObj(visitGatherLiveRedCoin, sorted);
    sortGatheredCoins(sorted);
}

static void sortStageRedCoinSnapshot() {
    for (u8 i = 0; i + 1 < sStageRedCoinCount; ++i) {
        for (u8 j = i + 1; j < sStageRedCoinCount; ++j) {
            const StageRedCoinEntry &a = sStageRedCoins[i];
            const StageRedCoinEntry &b = sStageRedCoins[j];
            const bool swap = a.initialPos.x > b.initialPos.x ||
                              (a.initialPos.x == b.initialPos.x && a.initialPos.y > b.initialPos.y) ||
                              (a.initialPos.x == b.initialPos.x && a.initialPos.y == b.initialPos.y &&
                               a.initialPos.z > b.initialPos.z);
            if (swap) {
                const StageRedCoinEntry tmp = sStageRedCoins[i];
                sStageRedCoins[i] = sStageRedCoins[j];
                sStageRedCoins[j] = tmp;
            }
        }
    }
    for (u8 i = 0; i < sStageRedCoinCount; ++i)
        sStageRedCoins[i].stableIndex = i;
}

static void buildSnapshotFromSorted(const SortedCoinCtx &sorted) {
    sStageRedCoinCount = sorted.count;
    for (u8 i = 0; i < sorted.count; ++i) {
        auto *base = reinterpret_cast<TMapObjBase *>(sorted.coins[i]);
        sStageRedCoins[i].mapObjId = static_cast<u16>(base->mMapObjID);
        sStageRedCoins[i].hudSlot = static_cast<u8>(sorted.coins[i]->_154);
        sStageRedCoins[i].initialPos = base->mInitialPosition;
        sStageRedCoins[i].active = true;
        sStageRedCoins[i].stableIndex = i;
    }
    sortStageRedCoinSnapshot();
    sStageSnapshotReady = sorted.count > 0;
}

static void captureStageRedCoinSnapshot() {
    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    if (sorted.count == 0)
        return;
    buildSnapshotFromSorted(sorted);
}

static u8 stableIndexForPosition(const TVec3f &pos) {
    for (u8 i = 0; i < sStageRedCoinCount; ++i) {
        if (positionsMatch(sStageRedCoins[i].initialPos, pos))
            return i;
    }
    return kInvalidHudSlot;
}

static void resetRedCoinTrackersForStage(u8 courseId, u8 episodeId) {
    sLastCourseId = courseId;
    sLastEpisodeId = episodeId;
    sLastRedCoinCount = 0;
    sStageSettleFrames = 0;
    sStageSnapshotReady = false;
    sStageRedCoinCount = 0;
    sCollectedMask = 0;
    sCollectedPositionCount = 0;
    sPrevLiveCoinCount = 0;
    sPendingHudCatchUp = false;
    sHudAppliedMask = 0;
    for (u32 i = 0; i < kMaxStageRedCoins; ++i) {
        sStageRedCoins[i] = {};
        sPrevLiveCoins[i] = nullptr;
        sCollectedPositions[i] = {};
    }

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return;

    // After a fresh stage enter vanilla clears the red-coin session; prefer 0 over a
    // stale Type6 value if anything raced before resetStage finished.
    sLastRedCoinCount = static_cast<u32>(fm->Type6Flag.mRedCoinCount);
}

/// True when this client has already started the red-coin mission locally.
/// Switch missions: hip-drop sets mRedCoinSwitchPressed and spawns TCoinRed.
/// Pre-placed missions: live TCoinRed exist at settle without the switch bit.
/// Never treat durable collection replay alone as "armed" — that reopens the HUD.
static bool isLocalRedCoinMissionLive(const TFlagManager *fm) {
    if (fm && fm->Type5Flag.mRedCoinSwitchPressed)
        return true;

    TGCConsole2 *console = gpMarDirector ? gpMarDirector->mGCConsole : nullptr;
    if (console && console->mIsRedCoinCard)
        return true;

    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    return sorted.count > 0;
}

static void applyHudSlotForCollection(TGCConsole2 *console, u8 hudSlot, u8 stableIndex) {
    if (!console || !gProcessDownCoin)
        return;

    u8 slot = hudSlot;
    if (slot >= kMaxStageRedCoins)
        slot = stableIndex;
    if (slot >= kMaxStageRedCoins)
        slot = 0;

    if ((sHudAppliedMask & static_cast<u8>(1u << slot)) != 0)
        return;

    gProcessDownCoin(console, static_cast<int>(slot));
    sHudAppliedMask |= static_cast<u8>(1u << slot);
}

static void catchUpRedCoinHudIfArmed(TFlagManager *fm) {
    if (!fm || !sPendingHudCatchUp)
        return;
    if (!isLocalRedCoinMissionLive(fm))
        return;

    TGCConsole2 *console = gpMarDirector ? gpMarDirector->mGCConsole : nullptr;
    const u32 resolvedCount = static_cast<u32>(popCountMask(sCollectedMask));
    if (resolvedCount == 0) {
        sPendingHudCatchUp = false;
        return;
    }

    fm->Type6Flag.mRedCoinCount = static_cast<s32>(resolvedCount);
    sLastRedCoinCount = resolvedCount;

    if (console && gProcessDownCoin) {
        for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
            if ((sCollectedMask & static_cast<u8>(1u << i)) == 0)
                continue;
            const u8 hudSlot =
                (i < sStageRedCoinCount) ? sStageRedCoins[i].hudSlot : i;
            applyHudSlotForCollection(console, hudSlot, i);
        }
    }

    sPendingHudCatchUp = false;
    reconcileCollectedRedCoinActors();
}

static void publishLocalRedCoinEvent(smso::WorldEventType type, u8 courseId, u8 episodeId, u8 payload0,
                                     u8 stableIndex, u32 payload1) {
    if (sApplyingRemoteEvent)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!redCoinPublishEnabled(buf))
        return;

    smso::enqueueLocalWorldEvent(static_cast<u8>(type), courseId, episodeId, payload0, stableIndex,
                                 payload1);
}

static bool isPositionAlreadyCollected(const TVec3f &pos) {
    for (u8 i = 0; i < sCollectedPositionCount; ++i) {
        if (positionsMatch(sCollectedPositions[i], pos))
            return true;
    }
    return false;
}

static void rememberCollectedPosition(const TVec3f &pos) {
    if (isPositionAlreadyCollected(pos))
        return;
    if (sCollectedPositionCount >= kMaxStageRedCoins)
        return;
    sCollectedPositions[sCollectedPositionCount++] = pos;
}

static u32 packRedCoinCollectionPayload(const TVec3f &pos) {
    return smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
}

static u8 popCountMask(u8 mask) {
    u8 count = 0;
    for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
        if ((mask & static_cast<u8>(1u << i)) != 0)
            ++count;
    }
    return count;
}

static u8 redCoinStableIndex(u8 reserved, u32 payload1) {
    (void)payload1;
    if (reserved < kMaxStageRedCoins)
        return reserved;
    return 0;
}

static void hideRedCoinActor(TMapObjBase *obj) {
    if (!obj)
        return;

    const u32 vtable = *reinterpret_cast<const u32 *>(obj);
    if (vtable != gCoinRedVtable && vtable != gCoinEmptyVtable)
        return;

    auto *live = reinterpret_cast<TLiveActor *>(obj);
    if (live->mStateFlags.asFlags.mIsObjDead)
        return;

    obj->makeObjDead();

    auto *mutableBytes = reinterpret_cast<u8 *>(obj);
    mutableBytes[kCoinTakenFlagOffset] = 1;

    live->mStateFlags.asFlags.mClipFromScene = true;
    live->mStateFlags.asFlags.mIsObjDead = true;
}

static bool isRedCoinLikeVtable(u32 vtable) {
    return vtable == gCoinRedVtable || vtable == gCoinEmptyVtable;
}

struct PosCoinCtx {
    TVec3f pos;
    TCoin *found;
    f32 matchEpsilon;
};

static bool visitCoinAtPosition(TMapObjBase *obj, void *ctx) {
    auto *posCtx = reinterpret_cast<PosCoinCtx *>(ctx);
    if (!isRedCoinLikeVtable(*reinterpret_cast<const u32 *>(obj)))
        return false;

    const auto *live = reinterpret_cast<const TLiveActor *>(obj);
    if (live->mStateFlags.asFlags.mIsObjDead)
        return false;

    const TVec3f &ref = obj->mInitialPosition;
    const f32 dx = ref.x - posCtx->pos.x;
    const f32 dy = ref.y - posCtx->pos.y;
    const f32 dz = ref.z - posCtx->pos.z;
    const f32 eps = posCtx->matchEpsilon;
    if (dx * dx + dy * dy + dz * dz > eps * eps)
        return false;
    posCtx->found = reinterpret_cast<TCoin *>(obj);
    return true;
}

struct NearestPosCoinCtx {
    TVec3f pos;
    TCoin *found;
    f32 bestDistSq;
    f32 maxDistSq;
};

static bool visitNearestRedCoin(TMapObjBase *obj, void *ctx) {
    auto *search = reinterpret_cast<NearestPosCoinCtx *>(ctx);
    if (!isRedCoinLikeVtable(*reinterpret_cast<const u32 *>(obj)))
        return false;

    const auto *live = reinterpret_cast<const TLiveActor *>(obj);
    if (live->mStateFlags.asFlags.mIsObjDead)
        return false;

    const TVec3f &ref = obj->mInitialPosition;
    const f32 dx = ref.x - search->pos.x;
    const f32 dy = ref.y - search->pos.y;
    const f32 dz = ref.z - search->pos.z;
    const f32 distSq = dx * dx + dy * dy + dz * dz;
    if (distSq > search->maxDistSq)
        return false;
    if (!search->found || distSq < search->bestDistSq) {
        search->found = reinterpret_cast<TCoin *>(obj);
        search->bestDistSq = distSq;
    }
    return false;
}

static TCoin *coinAtPosition(const TVec3f &pos, f32 matchEpsilon) {
    PosCoinCtx ctx = {pos, nullptr, matchEpsilon};
    forEachManagedMapObj(visitCoinAtPosition, &ctx);
    return ctx.found;
}

static TCoin *coinNearestNetworkPosition(const TVec3f &pos) {
    NearestPosCoinCtx ctx = {pos, nullptr, 1.0e30f, kNetworkRedCoinPosEpsilon * kNetworkRedCoinPosEpsilon};
    forEachManagedMapObj(visitNearestRedCoin, &ctx);
    return ctx.found;
}

static void rememberCollectedRedCoinPosition(u8 stableIndex, u8 hudSlot, TCoin *coin) {
    if (stableIndex >= kMaxStageRedCoins)
        return;

    StageRedCoinEntry &entry = sStageRedCoins[stableIndex];
    entry.stableIndex = stableIndex;
    entry.hudSlot = hudSlot;
    entry.active = false;

    if (coin) {
        auto *base = reinterpret_cast<TMapObjBase *>(coin);
        entry.initialPos = base->mInitialPosition;
        entry.mapObjId = static_cast<u16>(base->mMapObjID);
    }

    if (stableIndex + 1 > sStageRedCoinCount)
        sStageRedCoinCount = stableIndex + 1;
    sStageSnapshotReady = true;
}

static TCoin *coinAtStableIndex(u8 stableIndex) {
    if (stableIndex >= kMaxStageRedCoins || stableIndex >= sStageRedCoinCount)
        return nullptr;

    return coinAtPosition(sStageRedCoins[stableIndex].initialPos, kPosMatchEpsilon);
}

static void recordAuthoritativeRedCoinPosition(u8 stableIndex, u8 hudSlot, const TVec3f &pos) {
    if (stableIndex >= kMaxStageRedCoins)
        return;

    StageRedCoinEntry &entry = sStageRedCoins[stableIndex];
    entry.stableIndex = stableIndex;
    entry.hudSlot = hudSlot;
    entry.initialPos = pos;
    entry.active = false;
    if (stableIndex + 1 > sStageRedCoinCount)
        sStageRedCoinCount = stableIndex + 1;
    sStageSnapshotReady = true;
}

static void hideRedCoinAtNetworkPosition(const TVec3f &pos) {
    TCoin *coin = coinNearestNetworkPosition(pos);
    if (!coin)
        coin = coinAtPosition(pos, kNetworkRedCoinPosEpsilon);
    if (coin)
        hideRedCoinActor(reinterpret_cast<TMapObjBase *>(coin));
}

static void hideRedCoinByStableIndex(u8 stableIndex) {
    if (stableIndex >= kMaxStageRedCoins)
        return;

    if (stableIndex < sStageRedCoinCount)
        hideRedCoinAtNetworkPosition(sStageRedCoins[stableIndex].initialPos);
}

static void reconcileCollectedRedCoinActors() {
    for (u8 i = 0; i < sCollectedPositionCount; ++i)
        hideRedCoinAtNetworkPosition(sCollectedPositions[i]);

    for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
        if ((sCollectedMask & static_cast<u8>(1u << i)) != 0)
            hideRedCoinByStableIndex(i);
    }
}

static bool resolveCollectedCoinWorldPos(u8 stableIndex, u32 payload1, TVec3f *outPos) {
    if (smso::looksLikePackedCollectibleWorldPos(payload1)) {
        smso::unpackCollectibleWorldPos(payload1, outPos->x, outPos->y, outPos->z);
        return true;
    }

    if (stableIndex < sStageRedCoinCount) {
        *outPos = sStageRedCoins[stableIndex].initialPos;
        return true;
    }

    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    if (sorted.count > 0) {
        buildSnapshotFromSorted(sorted);
        if (stableIndex < sStageRedCoinCount) {
            *outPos = sStageRedCoins[stableIndex].initialPos;
            return true;
        }
    }

    return false;
}

static void applySingleRedCoinCollected(TFlagManager *fm, TGCConsole2 *console, u8 stableIndex, u8 payload0,
                                        u32 payload1) {
    if (!fm || stableIndex >= kMaxStageRedCoins)
        return;

    // Red-coin switch arming is local-only. Collection sync may remember/hide coins, but must
    // not write mRedCoinCount or call processDownCoin until this client has armed the mission
    // (local switch press or live pre-placed coins). Otherwise durable replay / periodic
    // resync after a stage reload reopens the red-coin HUD with no switch press.
    const u8 publishHudSlot = payload0 & 0xF;
    const u32 targetCount = (payload0 >> 4) & 0xF;
    const bool missionLive = isLocalRedCoinMissionLive(fm);
    TVec3f collectedPos{};
    const bool havePos = resolveCollectedCoinWorldPos(stableIndex, payload1, &collectedPos);

    if ((sCollectedMask & (1u << stableIndex)) != 0) {
        if (missionLive && targetCount > 0 &&
            static_cast<u32>(fm->Type6Flag.mRedCoinCount) != targetCount) {
            fm->Type6Flag.mRedCoinCount = static_cast<s32>(targetCount);
            sLastRedCoinCount = targetCount;
        }
        if (havePos) {
            recordAuthoritativeRedCoinPosition(stableIndex, publishHudSlot, collectedPos);
            rememberCollectedPosition(collectedPos);
            if (missionLive) {
                hideRedCoinAtNetworkPosition(collectedPos);
                hideRedCoinByStableIndex(stableIndex);
            }
        }
        if (!missionLive)
            sPendingHudCatchUp = true;
        return;
    }

    if (!sStageSnapshotReady) {
        SortedCoinCtx sorted = {};
        gatherSortedLiveRedCoins(&sorted);
        if (sorted.count > 0)
            buildSnapshotFromSorted(sorted);
    }

    if (havePos) {
        recordAuthoritativeRedCoinPosition(stableIndex, publishHudSlot, collectedPos);
        rememberCollectedPosition(collectedPos);
        if (missionLive) {
            hideRedCoinAtNetworkPosition(collectedPos);
            hideRedCoinByStableIndex(stableIndex);
            rememberCollectedRedCoinPosition(stableIndex, publishHudSlot,
                                             coinNearestNetworkPosition(collectedPos));
        } else {
            rememberCollectedRedCoinPosition(stableIndex, publishHudSlot, nullptr);
        }
    } else if (stableIndex < sStageRedCoinCount) {
        rememberCollectedPosition(sStageRedCoins[stableIndex].initialPos);
        if (missionLive) {
            hideRedCoinAtNetworkPosition(sStageRedCoins[stableIndex].initialPos);
            hideRedCoinByStableIndex(stableIndex);
            rememberCollectedRedCoinPosition(stableIndex, publishHudSlot,
                                             coinAtStableIndex(stableIndex));
        } else {
            rememberCollectedRedCoinPosition(stableIndex, publishHudSlot, nullptr);
        }
    }

    sCollectedMask |= static_cast<u8>(1u << stableIndex);
    const u32 resolvedCount =
        targetCount > 0 ? targetCount : static_cast<u32>(popCountMask(sCollectedMask));

    if (!missionLive) {
        // Bookkeeping only — wait for local arm before touching Type6 count / HUD.
        sPendingHudCatchUp = true;
        return;
    }

    fm->Type6Flag.mRedCoinCount = static_cast<s32>(resolvedCount);
    sLastRedCoinCount = resolvedCount;

    if (resolvedCount > 0)
        applyHudSlotForCollection(console, publishHudSlot, stableIndex);

    if (havePos)
        smso::playRemoteCoinCollectParticles(collectedPos, false);
    else if (stableIndex < sStageRedCoinCount)
        smso::playRemoteCoinCollectParticles(sStageRedCoins[stableIndex].initialPos, false);
}

static TCoin *findRemovedLiveCoin() {
    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);

    for (u8 i = 0; i < sPrevLiveCoinCount; ++i) {
        TCoin *prev = sPrevLiveCoins[i];
        if (!prev)
            continue;

        bool stillLive = false;
        for (u8 j = 0; j < sorted.count; ++j) {
            if (sorted.coins[j] == prev) {
                stillLive = true;
                break;
            }
        }
        if (!stillLive)
            return prev;
    }
    return nullptr;
}

static void rememberLiveCoinsForNextFrame() {
    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    sPrevLiveCoinCount = sorted.count;
    for (u8 i = 0; i < kMaxStageRedCoins; ++i)
        sPrevLiveCoins[i] = i < sorted.count ? sorted.coins[i] : nullptr;
}

static void detectLocalRedCoinProgress(TFlagManager *fm, u8 courseId, u8 episodeId) {
    const u32 count = static_cast<u32>(fm->Type6Flag.mRedCoinCount);
    while (count > sLastRedCoinCount) {
        u8 stableIndex = kInvalidHudSlot;
        u8 hudSlot = kInvalidHudSlot;

        TCoin *removed = findRemovedLiveCoin();
        if (removed) {
            auto *base = reinterpret_cast<TMapObjBase *>(removed);
            stableIndex = stableIndexForPosition(base->mInitialPosition);
            hudSlot = static_cast<u8>(removed->_154);
        }

        if (stableIndex == kInvalidHudSlot) {
            for (u8 i = 0; i < sStageRedCoinCount; ++i) {
                if ((sCollectedMask & (1u << i)) != 0)
                    continue;
                if (coinAtStableIndex(i))
                    continue;

                stableIndex = i;
                hudSlot = sStageRedCoins[i].hudSlot;
                break;
            }
        }

        if (stableIndex == kInvalidHudSlot) {
            for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
                if ((sCollectedMask & (1u << i)) == 0) {
                    stableIndex = i;
                    break;
                }
            }
        }

        if (stableIndex >= kMaxStageRedCoins)
            stableIndex = 0;

        u8 publishHudSlot = hudSlot;
        if (publishHudSlot == kInvalidHudSlot)
            publishHudSlot = stableIndex;

        TVec3f publishPos{};
        bool havePos = false;
        if (removed) {
            publishPos = reinterpret_cast<TMapObjBase *>(removed)->mInitialPosition;
            havePos = true;
        } else if (stableIndex < sStageRedCoinCount) {
            publishPos = sStageRedCoins[stableIndex].initialPos;
            havePos = true;
        }

        if (havePos)
            rememberCollectedPosition(publishPos);

        const u32 packedPos = havePos ? packRedCoinCollectionPayload(publishPos) : 0;

        const u32 newCount = sLastRedCoinCount + 1;
        sCollectedMask |= static_cast<u8>(1u << stableIndex);
        rememberCollectedRedCoinPosition(stableIndex, publishHudSlot, removed);
        publishLocalRedCoinEvent(
            smso::WE_RED_COIN_COLLECTED, courseId, episodeId, publishHudSlot, stableIndex, packedPos);
        sLastRedCoinCount = newCount;
        if (stableIndex < sStageRedCoinCount)
            sStageRedCoins[stableIndex].active = false;

        rememberLiveCoinsForNextFrame();
    }

    if (count <= sLastRedCoinCount)
        rememberLiveCoinsForNextFrame();
}

static bool applyRedCoinWorldEventOnStage(const smso::CommWorldEvent &event) {
    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return false;

    TGCConsole2 *console = gpMarDirector ? gpMarDirector->mGCConsole : nullptr;

    sApplyingRemoteEvent = true;

    switch (static_cast<smso::WorldEventType>(event.type)) {
    case smso::WE_RED_COIN_COLLECTED: {
        const u8 stableIndex = redCoinStableIndex(event.reserved, event.payload1);
        applySingleRedCoinCollected(fm, console, stableIndex, event.payload0, event.payload1);
        break;
    }

    default:
        break;
    }

    sApplyingRemoteEvent = false;
    return true;
}

} // namespace

namespace smso {

void flushDeferredRedCoinEvents();

void initRedCoinSync() {
    gProcessDownCoin =
        reinterpret_cast<ProcessDownCoinFn>(SMS_PORT_REGION(0x801466F0, 0x8013B32C, 0, 0));
    gCoinRedVtable = SMS_PORT_REGION(0x803C9BB4, 0x803C13A4, 0, 0);
    gCoinEmptyVtable = SMS_PORT_REGION(0x803C9D98, 0x803C1588, 0, 0);
    sLastCourseId = 0xFF;
    sLastEpisodeId = 0xFF;
    sForceStageTrackerReset = false;
}

void notifyRedCoinStageEnter() {
    sForceStageTrackerReset = true;
}

void captureLocalRedCoinProgress() {
    if (sApplyingRemoteEvent)
        return;

    CommBuffer *buf = getCommBuffer();
    const bool publishEnabled = redCoinPublishEnabled(buf);

    const u8 courseId = currentCourseId();
    const u8 episodeId = currentEpisodeId();
    if (isHubArea(courseId))
        return;

    if (sForceStageTrackerReset || courseId != sLastCourseId || episodeId != sLastEpisodeId) {
        sForceStageTrackerReset = false;
        resetRedCoinTrackersForStage(courseId, episodeId);
        // Apply deferred replay events only after the per-stage reset so we do not flush
        // collections and then immediately wipe sCollectedMask on the same frame (late join).
        flushDeferredRedCoinEvents();
    }

    if (sStageSettleFrames < kStageSettleFrames)
        ++sStageSettleFrames;

    if (redCoinScanReady(buf) && !sStageSnapshotReady)
        captureStageRedCoinSnapshot();

    TFlagManager *fm = TFlagManager::smInstance;
    if (fm)
        catchUpRedCoinHudIfArmed(fm);

    if (publishEnabled && fm)
        detectLocalRedCoinProgress(fm, courseId, episodeId);

    if (fm && redCoinPublishEnabled(buf))
        reconcileCollectedRedCoinActors();

    if (fm && redCoinScanReady(buf) && !sStageSnapshotReady) {
        SortedCoinCtx sorted = {};
        gatherSortedLiveRedCoins(&sorted);
        if (sorted.count > 0)
            buildSnapshotFromSorted(sorted);
    }

    flushDeferredRedCoinEvents();
}

bool applyRedCoinWorldEvent(const CommWorldEvent &event) {
    if (!sameStage(event.courseId, event.episodeId)) {
        if (sDeferredRedCoinCount >= sizeof(sDeferredRedCoinEvents) / sizeof(sDeferredRedCoinEvents[0]))
            return true;

        sDeferredRedCoinEvents[sDeferredRedCoinCount++] = event;
        return true;
    }

    if (applyRedCoinWorldEventOnStage(event))
        return true;

    if (sDeferredRedCoinCount >= sizeof(sDeferredRedCoinEvents) / sizeof(sDeferredRedCoinEvents[0]))
        return false;

    sDeferredRedCoinEvents[sDeferredRedCoinCount++] = event;
    return true;
}

void flushDeferredRedCoinEvents() {
    u8 writeIndex = 0;
    for (u8 readIndex = 0; readIndex < sDeferredRedCoinCount; ++readIndex) {
        const smso::CommWorldEvent &event = sDeferredRedCoinEvents[readIndex];
        if (!sameStage(event.courseId, event.episodeId)) {
            sDeferredRedCoinEvents[writeIndex++] = event;
            continue;
        }

        if (!applyRedCoinWorldEventOnStage(event)) {
            sDeferredRedCoinEvents[writeIndex++] = event;
            continue;
        }
    }
    sDeferredRedCoinCount = writeIndex;
}

} // namespace smso
