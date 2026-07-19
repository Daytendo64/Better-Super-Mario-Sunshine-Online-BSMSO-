#include "red_coin_sync.hpp"

#include "coin_collect_fx.hpp"
#include "collectible_scan.hpp"
#include "comm_buffer.hpp"
#include "world_sync.hpp"

#include <Dolphin/OS.h>
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
constexpr f32 kRebindPosEpsilon = 4.0f;
constexpr u32 kCoinTakenFlagOffset = 0x152;
constexpr u8 kMaxStageRedCoins = 8;
constexpr u8 kInvalidHudSlot = 0xFF;
// Rebind only when a collected index lacks a usable actor — not every frame.
constexpr u16 kReconcileRebindPeriod = 30;

static bool sApplyingRemoteEvent = false;

static u32 sLastRedCoinCount = 0;
static u8 sLastCourseId = 0xFF;
static u8 sLastEpisodeId = 0xFF;
static u16 sStageSettleFrames = 0;
static bool sStageSnapshotReady = false;
/// True once identity is locked to a full sorted cohort (8 coins/empties) or will not
/// grow via switch-spawn trickle (pre-placed / enemy-drop stages still expand by pos).
static bool sSnapshotFinal = false;
static u8 sCollectedMask = 0;
// Set by stageInit so same course/episode reloads still reset trackers.
static bool sForceStageTrackerReset = false;
// Remote collections recorded while the local switch mission was idle; flush HUD
// once the local player presses the switch (or live coins appear on pre-placed maps).
static bool sPendingHudCatchUp = false;
static u8 sHudAppliedMask = 0;
static u16 sFramesSinceRebind = 0;
static bool sWasSwitchPressed = false;
// Solo death/reload: vanilla clears red coins. Durable server history must not
// resurrect them unless another peer shares this course+episode (co-op persist).
static bool sSoloMissionAttempt = false;
static bool sPublishedSoloMissionReset = false;
/// Sticky: once a same-stage peer was seen this visit, never solo-reset on death reload
/// just because remote snapshots briefly drop during load.
static bool sStageHadSameStagePeer = false;
/// Delayed solo reset so brief peer gaps during death load cannot wipe co-op authority.
static u16 sPendingSoloResetFrames = 0;
constexpr u16 kSoloResetConfirmFrames = 180; // ~3s at 60fps
/// Keep BF_REQUEST_PROGRESS asserted for a few frames so the launcher can observe it.
static u8 sRequestProgressHoldFrames = 0;

static smso::CommWorldEvent sDeferredRedCoinEvents[16] = {};
static u8 sDeferredRedCoinCount = 0;

/// Sentinel reserved for WE_RED_COIN_COLLECTED: clear server authority for this stage.
constexpr u8 kRedCoinMissionResetReserved = 0xFF;

/// Canonical identity after settle: stableIndex binds to actor pointer (+ mapObjId/pos for rebind).
struct StageRedCoinEntry {
    TCoin *actor;
    u16 mapObjId;
    u8 hudSlot;
    u8 stableIndex;
    bool active;
    TVec3f initialPos;
};

static StageRedCoinEntry sStageRedCoins[kMaxStageRedCoins] = {};
static u8 sStageRedCoinCount = 0;

using ProcessDownCoinFn = void (*)(TGCConsole2 *, int);

static ProcessDownCoinFn gProcessDownCoin = nullptr;
static u32 gCoinRedVtable = 0;
static u32 gCoinEmptyVtable = 0;

struct SortedCoinCtx;
static u8 popCountMask(u8 mask);
static void reconcileCollectedRedCoinActors();
static void gatherSortedLiveRedCoins(SortedCoinCtx *sorted);
static void rebindStageRedCoinActors();
static void hideRedCoinByStableIndex(u8 stableIndex);
static void expandSnapshotWithNewRedCoins();
static void adoptDeadUntrackedRedCoins(u8 maxAdopt);
static void bindPendingCollectedByPosition();
static void maybeFinalizeSwitchCohort(const TFlagManager *fm);
static void gatherSortedIdentityCoins(SortedCoinCtx *sorted);
static bool hideRedCoinByInitialPos(const TVec3f &pos);
static bool entryHasPos(const StageRedCoinEntry &entry);
static void buildSnapshotFromSorted(const SortedCoinCtx &sorted);
static void hideRedCoinActor(TMapObjBase *obj);

static u8 currentCourseId() {
    return gpMarDirector ? gpMarDirector->mAreaID : 0;
}

static u8 currentEpisodeId() {
    return gpMarDirector ? gpMarDirector->mEpisodeID : 0;
}

static bool isHubArea(u8 areaId) {
    return areaId == 15;
}

// Sirena casino (14): director/mission uses beach ids 3/4; archive/catalog uses 0/1.
static bool sameCasinoEpisode(u8 a, u8 b) {
    if (a == b)
        return true;
    const bool aEp4 = (a == 0 || a == 3);
    const bool bEp4 = (b == 0 || b == 3);
    if (aEp4 && bEp4)
        return true;
    const bool aEp5 = (a == 1 || a == 4);
    const bool bEp5 = (b == 1 || b == 4);
    return aEp5 && bEp5;
}

static bool redCoinPublishEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0 &&
           (buf->bridgeFlags & (smso::BF_SYNC_EVENT | smso::BF_SYNC_MISSION)) != 0;
}

static bool redCoinScanReady(const smso::CommBuffer *buf) {
    return redCoinPublishEnabled(buf) && sStageSettleFrames >= kStageSettleFrames;
}

static bool sameStage(u8 courseId, u8 episodeId) {
    if (courseId != currentCourseId())
        return false;
    if (episodeId == currentEpisodeId())
        return true;
    if (courseId == 14)
        return sameCasinoEpisode(episodeId, currentEpisodeId());
    return false;
}

static bool peerMatchesStage(const smso::PlayerSnapshot &snap, u8 courseId, u8 episodeId) {
    if (snap.connected == 0)
        return false;
    if (snap.stageId != courseId)
        return false;
    if (snap.episodeId == episodeId)
        return true;
    if (courseId == 14)
        return sameCasinoEpisode(snap.episodeId, episodeId);
    return false;
}

/// True when at least one remote snapshot is on the same course+episode.
static bool hasSameStagePeer(const smso::CommBuffer *buf) {
    if (!buf)
        return false;
    const u8 courseId = currentCourseId();
    const u8 episodeId = currentEpisodeId();
    for (u32 i = 0; i < smso::MAX_REMOTE_SLOTS; ++i) {
        if (peerMatchesStage(buf->remoteSnapshots[i], courseId, episodeId))
            return true;
    }
    return false;
}

static void clearDeferredRedCoinEventsForCurrentStage() {
    u8 writeIndex = 0;
    for (u8 readIndex = 0; readIndex < sDeferredRedCoinCount; ++readIndex) {
        const smso::CommWorldEvent &event = sDeferredRedCoinEvents[readIndex];
        if (sameStage(event.courseId, event.episodeId))
            continue;
        sDeferredRedCoinEvents[writeIndex++] = event;
    }
    sDeferredRedCoinCount = writeIndex;
}

static bool isRedCoinLikeVtable(u32 vtable) {
    return vtable == gCoinRedVtable || vtable == gCoinEmptyVtable;
}

static bool isBoundActorValid(const TCoin *coin) {
    if (!coin || !smso::isValidMapObjPtr(coin))
        return false;
    return isRedCoinLikeVtable(*reinterpret_cast<const u32 *>(coin));
}

static bool isActorDeadOrTaken(const TCoin *coin) {
    if (!isBoundActorValid(coin))
        return true;
    const auto *live = reinterpret_cast<const TLiveActor *>(coin);
    const auto *bytes = reinterpret_cast<const u8 *>(coin);
    return live->mStateFlags.asFlags.mIsObjDead || bytes[kCoinTakenFlagOffset] != 0;
}

static bool isCollectibleRedCoin(const TMapObjBase *obj) {
    if (!obj || !smso::isValidMapObjPtr(obj))
        return false;
    if (!isRedCoinLikeVtable(*reinterpret_cast<const u32 *>(obj)))
        return false;
    // Snapshot / gather live coins only — exclude empty / already taken.
    if (*reinterpret_cast<const u32 *>(obj) != gCoinRedVtable)
        return false;
    const auto *bytes = reinterpret_cast<const u8 *>(obj);
    if (bytes[kCoinTakenFlagOffset] != 0)
        return false;
    const auto *live = reinterpret_cast<const TLiveActor *>(obj);
    return !live->mStateFlags.asFlags.mIsObjDead;
}

/// Dead/taken red coin only — never adopt TCoinEmpty placeholders (they fill the
/// 8-slot snapshot and block Pianta/Pokey/bird drops from appending).
static bool isDeadOrTakenRedCoin(const TMapObjBase *obj) {
    if (!obj || !smso::isValidMapObjPtr(obj))
        return false;
    if (*reinterpret_cast<const u32 *>(obj) != gCoinRedVtable)
        return false;
    return isActorDeadOrTaken(reinterpret_cast<const TCoin *>(obj));
}

static bool snapshotOwnsActor(const TCoin *actor) {
    if (!actor)
        return false;
    for (u8 i = 0; i < sStageRedCoinCount; ++i) {
        if (sStageRedCoins[i].actor == actor)
            return true;
    }
    return false;
}

static void writeSnapshotEntry(u8 index, TCoin *coin, bool active) {
    auto *base = reinterpret_cast<TMapObjBase *>(coin);
    sStageRedCoins[index].actor = coin;
    sStageRedCoins[index].mapObjId = static_cast<u16>(base->mMapObjID);
    sStageRedCoins[index].hudSlot = static_cast<u8>(coin->_154);
    sStageRedCoins[index].initialPos = base->mInitialPosition;
    sStageRedCoins[index].active = active;
    sStageRedCoins[index].stableIndex = index;
    if (index + 1 > sStageRedCoinCount)
        sStageRedCoinCount = static_cast<u8>(index + 1);
}

/// Append a newly discovered coin without reshuffling existing stable indices.
static bool appendSnapshotCoin(TCoin *coin, bool active) {
    if (!coin || sStageRedCoinCount >= kMaxStageRedCoins)
        return false;
    if (snapshotOwnsActor(coin))
        return false;

    writeSnapshotEntry(sStageRedCoinCount, coin, active);
    sStageSnapshotReady = true;
    return true;
}

static bool positionsMatch(const TVec3f &a, const TVec3f &b, f32 eps) {
    const f32 dx = a.x - b.x;
    const f32 dy = a.y - b.y;
    const f32 dz = a.z - b.z;
    return dx * dx + dy * dy + dz * dz <= eps * eps;
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
    smso::forEachManagedMapObj(reinterpret_cast<smso::MapObjVisitorFn>(visitor), ctx);
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

/// Live TCoinRed plus TCoinEmpty placeholders (switch-mission spawn seats).
static bool isIdentityCandidate(const TMapObjBase *obj) {
    if (!obj || !smso::isValidMapObjPtr(obj))
        return false;
    const u32 vtable = *reinterpret_cast<const u32 *>(obj);
    if (!isRedCoinLikeVtable(vtable))
        return false;
    const auto *bytes = reinterpret_cast<const u8 *>(obj);
    if (bytes[kCoinTakenFlagOffset] != 0)
        return false;
    const auto *live = reinterpret_cast<const TLiveActor *>(obj);
    if (live->mStateFlags.asFlags.mIsObjDead)
        return false;
    return true;
}

static bool visitGatherIdentityCoin(TMapObjBase *obj, void *ctx) {
    auto *sorted = reinterpret_cast<SortedCoinCtx *>(ctx);
    if (sorted->count >= kMaxStageRedCoins || !isIdentityCandidate(obj))
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

static void gatherSortedIdentityCoins(SortedCoinCtx *sorted) {
    sorted->count = 0;
    forEachManagedMapObj(visitGatherIdentityCoin, sorted);
    sortGatheredCoins(sorted);
}

static bool entryHasPos(const StageRedCoinEntry &entry) {
    return entry.initialPos.x != 0.0f || entry.initialPos.y != 0.0f || entry.initialPos.z != 0.0f;
}

static bool visitGatherDeadRedCoin(TMapObjBase *obj, void *ctx) {
    auto *sorted = reinterpret_cast<SortedCoinCtx *>(ctx);
    if (sorted->count >= kMaxStageRedCoins || !isDeadOrTakenRedCoin(obj))
        return false;
    auto *coin = reinterpret_cast<TCoin *>(obj);
    if (snapshotOwnsActor(coin))
        return false;
    sorted->coins[sorted->count++] = coin;
    return false;
}

static void gatherSortedDeadUntrackedRedCoins(SortedCoinCtx *sorted) {
    sorted->count = 0;
    forEachManagedMapObj(visitGatherDeadRedCoin, sorted);
    sortGatheredCoins(sorted);
}

static bool snapshotOwnsInitialPos(const TVec3f &pos) {
    for (u8 i = 0; i < sStageRedCoinCount; ++i) {
        if (!entryHasPos(sStageRedCoins[i]))
            continue;
        if (positionsMatch(sStageRedCoins[i].initialPos, pos, kRebindPosEpsilon))
            return true;
    }
    return false;
}

/// After switch arm, wait for the full 8-coin cohort then lock ONE sorted snapshot.
/// Never append-only while coins trickle in — that desynced indices vs remotes.
static void maybeFinalizeSwitchCohort(const TFlagManager *fm) {
    if (!fm || !fm->Type5Flag.mRedCoinSwitchPressed || sSnapshotFinal)
        return;

    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    if (sorted.count < kMaxStageRedCoins)
        return;

    // Rebuild identity from the full cohort when nothing has been published yet.
    if (sCollectedMask == 0) {
        buildSnapshotFromSorted(sorted);
        sSnapshotFinal = true;
        OSReport("[SMSOBB] red-coin snapshot switch-cohort count=%u mask=0x%02X\n",
                 sStageRedCoinCount, sCollectedMask);
        return;
    }

    // Mask already set: keep indices, rebind actors by position/mapObjId.
    rebindStageRedCoinActors();
    sSnapshotFinal = true;
    OSReport("[SMSOBB] red-coin snapshot switch-cohort rebind count=%u mask=0x%02X\n",
             sStageRedCoinCount, sCollectedMask);
}

/// Steal an unbound / empty / dead-uncollected slot for a late live drop when the
/// 8-slot snapshot was filled with placeholders (Field Pianta/Pokey/bird path).
static bool tryClaimSnapshotSlotForLiveRed(TCoin *liveRed, bool active) {
    if (!liveRed || snapshotOwnsActor(liveRed))
        return false;

    for (u8 i = 0; i < sStageRedCoinCount; ++i) {
        if ((sCollectedMask & static_cast<u8>(1u << i)) != 0)
            continue;
        TCoin *actor = sStageRedCoins[i].actor;
        if (!isBoundActorValid(actor)) {
            writeSnapshotEntry(i, liveRed, active);
            OSReport("[SMSOBB] red-coin snapshot claim-slot i=%u (late drop)\n", i);
            return true;
        }
        const u32 vt = *reinterpret_cast<const u32 *>(actor);
        if (vt == gCoinEmptyVtable || isActorDeadOrTaken(actor)) {
            writeSnapshotEntry(i, liveRed, active);
            OSReport("[SMSOBB] red-coin snapshot claim-slot i=%u (late drop)\n", i);
            return true;
        }
    }
    return false;
}

/// Enemy-drop / late red coins (Field Pianta/Pokey/pod/bird):
/// 1) Rebind into an existing *uncollected* initialPos fingerprint slot.
/// 2) Else append a NEW unique drop (one slot per unique spawn pos).
/// 3) Else claim an uncollected dead/empty placeholder slot.
/// NEVER rebind a live drop into a collected seat — that makeObjDead'd the Pianta
/// reward when its spawn pos matched (or claimed) a collected fingerprint.
static void expandSnapshotWithNewRedCoins() {
    TFlagManager *fm = TFlagManager::smInstance;
    if (fm && fm->Type5Flag.mRedCoinSwitchPressed && !sSnapshotFinal) {
        maybeFinalizeSwitchCohort(fm);
        return;
    }
    if (sStageRedCoinCount == 0)
        return;

    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    if (sorted.count == 0)
        return;

    u8 rebound = 0;
    u8 expanded = 0;
    for (u8 i = 0; i < sorted.count; ++i) {
        if (snapshotOwnsActor(sorted.coins[i]))
            continue;
        auto *base = reinterpret_cast<TMapObjBase *>(sorted.coins[i]);

        bool reboundExisting = false;
        bool matchedCollectedSeat = false;
        for (u8 s = 0; s < sStageRedCoinCount; ++s) {
            if (!entryHasPos(sStageRedCoins[s]))
                continue;
            if (!positionsMatch(sStageRedCoins[s].initialPos, base->mInitialPosition,
                                kRebindPosEpsilon))
                continue;
            // Collected seats are hide targets — do not attach a new live reward there.
            if ((sCollectedMask & static_cast<u8>(1u << s)) != 0) {
                matchedCollectedSeat = true;
                continue;
            }
            if (isBoundActorValid(sStageRedCoins[s].actor) &&
                !isActorDeadOrTaken(sStageRedCoins[s].actor) &&
                *reinterpret_cast<const u32 *>(sStageRedCoins[s].actor) == gCoinRedVtable &&
                sStageRedCoins[s].actor != sorted.coins[i])
                continue;
            writeSnapshotEntry(s, sorted.coins[i], true);
            ++rebound;
            reboundExisting = true;
            break;
        }
        if (reboundExisting)
            continue;

        // Uncollected seat already owns this spawn fingerprint — wait for rebind.
        // If only a *collected* seat matched, fall through to append/claim so the
        // Pianta/NPC reward stays a visible world coin.
        if (snapshotOwnsInitialPos(base->mInitialPosition) && !matchedCollectedSeat)
            continue;

        if (appendSnapshotCoin(sorted.coins[i], true)) {
            ++expanded;
            continue;
        }
        if (tryClaimSnapshotSlotForLiveRed(sorted.coins[i], true))
            ++expanded;
    }
    if (rebound > 0) {
        OSReport("[SMSOBB] red-coin snapshot rebind-live count=%u (+%u) mask=0x%02X\n",
                 sStageRedCoinCount, rebound, sCollectedMask);
    }
    if (expanded > 0) {
        OSReport("[SMSOBB] red-coin snapshot expand count=%u (+%u) mask=0x%02X\n",
                 sStageRedCoinCount, expanded, sCollectedMask);
    }
}

/// After a local collect, adopt at most one taken TCoinRed (never empties / mass-fill).
/// Prefer rebinding into an existing fingerprint — never append a second seat for the
/// same spawn pos (that + expand filled all 8 slots before Pianta could drop).
static void adoptDeadUntrackedRedCoins(u8 maxAdopt) {
    if (maxAdopt == 0)
        return;

    SortedCoinCtx sorted = {};
    gatherSortedDeadUntrackedRedCoins(&sorted);
    if (sorted.count == 0)
        return;

    u8 added = 0;
    for (u8 i = 0; i < sorted.count && added < maxAdopt; ++i) {
        auto *base = reinterpret_cast<TMapObjBase *>(sorted.coins[i]);
        bool boundExisting = false;
        for (u8 s = 0; s < sStageRedCoinCount; ++s) {
            if (!entryHasPos(sStageRedCoins[s]))
                continue;
            if (!positionsMatch(sStageRedCoins[s].initialPos, base->mInitialPosition,
                                kRebindPosEpsilon))
                continue;
            writeSnapshotEntry(s, sorted.coins[i], false);
            boundExisting = true;
            break;
        }
        if (boundExisting)
            continue;
        // Fingerprint already reserved (live expand tracked it) — do not grow the snapshot.
        if (snapshotOwnsInitialPos(base->mInitialPosition))
            continue;
        if (appendSnapshotCoin(sorted.coins[i], false)) {
            ++added;
            continue;
        }
        if (tryClaimSnapshotSlotForLiveRed(sorted.coins[i], false)) {
            for (u8 s = 0; s < sStageRedCoinCount; ++s) {
                if (sStageRedCoins[s].actor == sorted.coins[i]) {
                    sStageRedCoins[s].active = false;
                    break;
                }
            }
            ++added;
        }
    }
    if (added > 0) {
        OSReport("[SMSOBB] red-coin snapshot adopt-dead count=%u (+%u) mask=0x%02X\n",
                 sStageRedCoinCount, added, sCollectedMask);
    }
}

struct HidePosCtx {
    TVec3f pos;
    TMapObjBase *match;
};

static bool visitHideByInitialPos(TMapObjBase *obj, void *raw) {
    auto *c = reinterpret_cast<HidePosCtx *>(raw);
    // Only live TCoinRed — never hide empties or near-miss neighbors of deferred drops.
    if (!obj || *reinterpret_cast<const u32 *>(obj) != gCoinRedVtable)
        return false;
    if (!positionsMatch(obj->mInitialPosition, c->pos, kRebindPosEpsilon))
        return false;
    auto *live = reinterpret_cast<TLiveActor *>(obj);
    if (live->mStateFlags.asFlags.mIsObjDead)
        return false;
    c->match = obj;
    return true;
}

static bool hideRedCoinByInitialPos(const TVec3f &pos) {
    if (pos.x == 0.0f && pos.y == 0.0f && pos.z == 0.0f)
        return false;

    HidePosCtx ctx = {pos, nullptr};
    forEachManagedMapObj(visitHideByInitialPos, &ctx);
    if (!ctx.match)
        return false;
    hideRedCoinActor(ctx.match);
    OSReport("[SMSOBB] red-coin hide-by-pos mask=0x%02X\n", sCollectedMask);
    return true;
}

/// Remote collections of not-yet-spawned drops: bind ONLY by initialPos fingerprint.
/// FIFO nearest-unclaimed is forbidden — it hid wrong coins on switch trickle / Field drops.
static void bindPendingCollectedByPosition() {
    if (sCollectedMask == 0)
        return;

    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    if (sorted.count == 0)
        return;

    bool claimed[kMaxStageRedCoins] = {};
    for (u8 i = 0; i < sStageRedCoinCount; ++i) {
        if (!isBoundActorValid(sStageRedCoins[i].actor))
            continue;
        for (u8 j = 0; j < sorted.count; ++j) {
            if (sorted.coins[j] == sStageRedCoins[i].actor) {
                claimed[j] = true;
                break;
            }
        }
    }

    for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
        if ((sCollectedMask & static_cast<u8>(1u << i)) == 0)
            continue;
        if (i < sStageRedCoinCount && isBoundActorValid(sStageRedCoins[i].actor) &&
            isActorDeadOrTaken(sStageRedCoins[i].actor))
            continue;
        if (i < sStageRedCoinCount && isBoundActorValid(sStageRedCoins[i].actor) &&
            !isActorDeadOrTaken(sStageRedCoins[i].actor)) {
            // Live actor already bound to a collected bit — hide it.
            hideRedCoinByStableIndex(i);
            continue;
        }
        if (i >= sStageRedCoinCount || !entryHasPos(sStageRedCoins[i]))
            continue;

        TCoin *match = nullptr;
        u8 matchJ = kInvalidHudSlot;
        for (u8 j = 0; j < sorted.count; ++j) {
            if (claimed[j])
                continue;
            auto *base = reinterpret_cast<TMapObjBase *>(sorted.coins[j]);
            if (!positionsMatch(base->mInitialPosition, sStageRedCoins[i].initialPos,
                                kRebindPosEpsilon))
                continue;
            match = sorted.coins[j];
            matchJ = j;
            break;
        }
        if (!match || matchJ >= kMaxStageRedCoins)
            continue;

        claimed[matchJ] = true;
        writeSnapshotEntry(i, match, false);
        sStageSnapshotReady = true;
        OSReport("[SMSOBB] red-coin pending-bind-pos i=%u mask=0x%02X\n", i, sCollectedMask);
        hideRedCoinByStableIndex(i);
    }
}

static void assignStableIndicesInPlace() {
    for (u8 i = 0; i < sStageRedCoinCount; ++i)
        sStageRedCoins[i].stableIndex = i;
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
    assignStableIndicesInPlace();
}

static void buildSnapshotFromSorted(const SortedCoinCtx &sorted) {
    sStageRedCoinCount = sorted.count;
    for (u8 i = 0; i < sorted.count; ++i) {
        auto *base = reinterpret_cast<TMapObjBase *>(sorted.coins[i]);
        sStageRedCoins[i].actor = sorted.coins[i];
        sStageRedCoins[i].mapObjId = static_cast<u16>(base->mMapObjID);
        sStageRedCoins[i].hudSlot = static_cast<u8>(sorted.coins[i]->_154);
        sStageRedCoins[i].initialPos = base->mInitialPosition;
        sStageRedCoins[i].active = true;
        sStageRedCoins[i].stableIndex = i;
    }
    sortStageRedCoinSnapshot();
    sStageSnapshotReady = sorted.count > 0;
    // Only switch missions lock final at 8 — Field must keep expanding for late NPC drops.
    TFlagManager *fmLock = TFlagManager::smInstance;
    if (sorted.count >= kMaxStageRedCoins && fmLock && fmLock->Type5Flag.mRedCoinSwitchPressed)
        sSnapshotFinal = true;
    sFramesSinceRebind = 0;
    OSReport("[SMSOBB] red-coin snapshot ready count=%u mask=0x%02X final=%u\n", sStageRedCoinCount,
             sCollectedMask, sSnapshotFinal ? 1u : 0u);
}

static void captureStageRedCoinSnapshot() {
    // Switch missions: prefer TCoinEmpty seats before/while arming.
    // Deferred-drop stages (Red Coin Field): live TCoinRed only — empties fill the
    // 8-slot snapshot and block Pianta/Pokey/bird drops from claiming slots.
    SortedCoinCtx sorted = {};
    TFlagManager *fm = TFlagManager::smInstance;
    if (fm && fm->Type5Flag.mRedCoinSwitchPressed)
        gatherSortedIdentityCoins(&sorted);
    if (sorted.count == 0)
        gatherSortedLiveRedCoins(&sorted);
    if (sorted.count == 0)
        return;
    buildSnapshotFromSorted(sorted);
}

/// Refresh actor pointers without changing stable indices. Match by mapObjId, then exact pos.
static void rebindStageRedCoinActors() {
    if (sStageRedCoinCount == 0) {
        captureStageRedCoinSnapshot();
        return;
    }

    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    if (sorted.count == 0)
        return;

    bool claimed[kMaxStageRedCoins] = {};
    for (u8 i = 0; i < sStageRedCoinCount; ++i) {
        StageRedCoinEntry &entry = sStageRedCoins[i];
        // Keep any still-valid binding (live or already dead/taken). Never rematch a
        // collected index onto a live neighbor — that was the cascade failure mode.
        if (isBoundActorValid(entry.actor)) {
            entry.active = !isActorDeadOrTaken(entry.actor);
            if (entry.active) {
                for (u8 j = 0; j < sorted.count; ++j) {
                    if (sorted.coins[j] == entry.actor) {
                        claimed[j] = true;
                        break;
                    }
                }
            }
            continue;
        }

        TCoin *match = nullptr;
        u8 matchJ = kInvalidHudSlot;
        for (u8 j = 0; j < sorted.count; ++j) {
            if (claimed[j])
                continue;
            auto *base = reinterpret_cast<TMapObjBase *>(sorted.coins[j]);
            if (entry.mapObjId != 0 && static_cast<u16>(base->mMapObjID) == entry.mapObjId) {
                match = sorted.coins[j];
                matchJ = j;
                break;
            }
        }
        if (!match) {
            for (u8 j = 0; j < sorted.count; ++j) {
                if (claimed[j])
                    continue;
                auto *base = reinterpret_cast<TMapObjBase *>(sorted.coins[j]);
                if (positionsMatch(base->mInitialPosition, entry.initialPos, kRebindPosEpsilon)) {
                    match = sorted.coins[j];
                    matchJ = j;
                    break;
                }
            }
        }
        if (match && matchJ < kMaxStageRedCoins) {
            claimed[matchJ] = true;
            auto *base = reinterpret_cast<TMapObjBase *>(match);
            entry.actor = match;
            entry.mapObjId = static_cast<u16>(base->mMapObjID);
            entry.hudSlot = static_cast<u8>(match->_154);
            entry.initialPos = base->mInitialPosition;
            entry.active = !isActorDeadOrTaken(match);
        }
    }

    sFramesSinceRebind = 0;
}

static void resetRedCoinTrackersForStage(u8 courseId, u8 episodeId, bool preserveCoopMask) {
    const u8 savedMask = preserveCoopMask ? sCollectedMask : static_cast<u8>(0);
    TVec3f savedPos[kMaxStageRedCoins] = {};
    u8 savedHud[kMaxStageRedCoins] = {};
    if (preserveCoopMask && savedMask != 0) {
        for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
            if ((savedMask & static_cast<u8>(1u << i)) == 0)
                continue;
            if (i < sStageRedCoinCount && entryHasPos(sStageRedCoins[i]))
                savedPos[i] = sStageRedCoins[i].initialPos;
            if (i < sStageRedCoinCount)
                savedHud[i] = sStageRedCoins[i].hudSlot;
        }
    }

    sLastCourseId = courseId;
    sLastEpisodeId = episodeId;
    sLastRedCoinCount = 0;
    sStageSettleFrames = 0;
    sStageSnapshotReady = false;
    sSnapshotFinal = false;
    sStageRedCoinCount = 0;
    sCollectedMask = 0;
    sPendingHudCatchUp = false;
    sHudAppliedMask = 0;
    sFramesSinceRebind = 0;
    sWasSwitchPressed = false;
    sPublishedSoloMissionReset = false;
    sPendingSoloResetFrames = 0;
    for (u32 i = 0; i < kMaxStageRedCoins; ++i)
        sStageRedCoins[i] = {};

    // Solo death: drop deferred same-stage collections so durable events cannot
    // resurrect vanilla-cleared coins. Co-op death: keep them for immediate flush
    // and restore the authority mask so HUD/hides do not wait on the 45s resync.
    if (!preserveCoopMask)
        clearDeferredRedCoinEventsForCurrentStage();

    if (preserveCoopMask && savedMask != 0) {
        sCollectedMask = savedMask;
        sPendingHudCatchUp = true;
        for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
            if ((savedMask & static_cast<u8>(1u << i)) == 0)
                continue;
            sStageRedCoins[i].stableIndex = i;
            sStageRedCoins[i].hudSlot = savedHud[i] < kMaxStageRedCoins ? savedHud[i] : i;
            sStageRedCoins[i].active = false;
            if (savedPos[i].x != 0.0f || savedPos[i].y != 0.0f || savedPos[i].z != 0.0f)
                sStageRedCoins[i].initialPos = savedPos[i];
            if (i + 1 > sStageRedCoinCount)
                sStageRedCoinCount = static_cast<u8>(i + 1);
        }
        OSReport("[SMSOBB] red-coin co-op mask preserved 0x%02X (same-stage reload)\n", savedMask);
    }

    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return;

    // After a fresh stage enter vanilla clears the red-coin session; prefer 0 over a
    // stale Type6 value if anything raced before resetStage finished.
    // Co-op preserve: Type6 stays 0 until HUD catch-up / durable apply restores count.
    sLastRedCoinCount = static_cast<u32>(fm->Type6Flag.mRedCoinCount);
    if (preserveCoopMask && savedMask != 0)
        sLastRedCoinCount = 0;
}

/// True when this client has already started the red-coin mission locally.
/// Switch missions: hip-drop sets mRedCoinSwitchPressed and spawns TCoinRed.
/// Pre-placed missions: live TCoinRed exist at settle without the switch bit.
/// Never arm from empty-only settle snapshots — that reopens HUD / FX without coins.
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
            const u8 hudSlot = (i < sStageRedCoinCount) ? sStageRedCoins[i].hudSlot : i;
            applyHudSlotForCollection(console, hudSlot, i);
        }
    }

    sPendingHudCatchUp = false;
    reconcileCollectedRedCoinActors();
}

static void publishLocalRedCoinEvent(smso::WorldEventType type, u8 courseId, u8 episodeId, u8 payload0,
                                     u8 stableIndex, u32 payload1, u32 payload2) {
    if (sApplyingRemoteEvent)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!redCoinPublishEnabled(buf))
        return;

    smso::enqueueLocalWorldEvent(static_cast<u8>(type), courseId, episodeId, payload0, stableIndex,
                                 payload1, payload2);
}

static u8 popCountMask(u8 mask) {
    u8 count = 0;
    for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
        if ((mask & static_cast<u8>(1u << i)) != 0)
            ++count;
    }
    return count;
}

static u8 redCoinStableIndex(u8 reserved) {
    if (reserved < kMaxStageRedCoins)
        return reserved;
    return 0;
}

static void hideRedCoinActor(TMapObjBase *obj) {
    if (!obj || !smso::isValidMapObjPtr(obj))
        return;

    // Never kill TCoinEmpty seats — only live/taken reds.
    const u32 vtable = *reinterpret_cast<const u32 *>(obj);
    if (vtable != gCoinRedVtable)
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

/// Index-authoritative hide — never nearest-search. Missing actor → wait for rebind.
static void hideRedCoinByStableIndex(u8 stableIndex) {
    if (stableIndex >= kMaxStageRedCoins)
        return;

    if (stableIndex >= sStageRedCoinCount || !isBoundActorValid(sStageRedCoins[stableIndex].actor))
        return;

    TCoin *coin = sStageRedCoins[stableIndex].actor;
    if (isActorDeadOrTaken(coin)) {
        sStageRedCoins[stableIndex].active = false;
        return;
    }

    hideRedCoinActor(reinterpret_cast<TMapObjBase *>(coin));
    sStageRedCoins[stableIndex].active = false;
    OSReport("[SMSOBB] red-coin hide-by-index i=%u mask=0x%02X count=%u\n", stableIndex,
             sCollectedMask, popCountMask(sCollectedMask));
}

static bool collectedIndexNeedsActor(u8 stableIndex) {
    if (stableIndex >= sStageRedCoinCount)
        return true;
    return !isBoundActorValid(sStageRedCoins[stableIndex].actor);
}

static void reconcileCollectedRedCoinActors() {
    if (sCollectedMask == 0)
        return;

    TFlagManager *fm = TFlagManager::smInstance;
    maybeFinalizeSwitchCohort(fm);
    // Position-fingerprint bind for deferred drops — never FIFO unclaimed.
    bindPendingCollectedByPosition();
    expandSnapshotWithNewRedCoins();

    bool needRebind = false;
    for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
        if ((sCollectedMask & static_cast<u8>(1u << i)) == 0)
            continue;
        if (collectedIndexNeedsActor(i)) {
            needRebind = true;
            break;
        }
    }

    if (needRebind || sFramesSinceRebind >= kReconcileRebindPeriod) {
        if (!sStageSnapshotReady)
            captureStageRedCoinSnapshot();
        else
            rebindStageRedCoinActors();
        sFramesSinceRebind = 0;
    } else {
        ++sFramesSinceRebind;
    }

    for (u8 i = 0; i < kMaxStageRedCoins; ++i) {
        if ((sCollectedMask & static_cast<u8>(1u << i)) == 0)
            continue;
        hideRedCoinByStableIndex(i);
        // Pos hide only when the collected slot already has a fingerprint AND no live
        // bound actor — never spray-hide nearby untracked Pianta/Pokey drops.
        if (i < sStageRedCoinCount && entryHasPos(sStageRedCoins[i]) &&
            collectedIndexNeedsActor(i))
            hideRedCoinByInitialPos(sStageRedCoins[i].initialPos);
    }
}

static void markCollectedIndex(u8 stableIndex, u8 hudSlot) {
    if (stableIndex >= kMaxStageRedCoins)
        return;

    sCollectedMask |= static_cast<u8>(1u << stableIndex);

    StageRedCoinEntry &entry = sStageRedCoins[stableIndex];
    entry.stableIndex = stableIndex;
    if (hudSlot < kMaxStageRedCoins)
        entry.hudSlot = hudSlot;
    entry.active = false;
    if (stableIndex + 1 > sStageRedCoinCount)
        sStageRedCoinCount = stableIndex + 1;
}

static void rememberCollectedPos(u8 stableIndex, const TVec3f &pos) {
    if (stableIndex >= kMaxStageRedCoins)
        return;
    if (pos.x == 0.0f && pos.y == 0.0f && pos.z == 0.0f)
        return;
    sStageRedCoins[stableIndex].initialPos = pos;
    if (stableIndex + 1 > sStageRedCoinCount)
        sStageRedCoinCount = static_cast<u8>(stableIndex + 1);
}

/// payload1 low byte = authoritative collected mask when not a packed XYZ (legacy).
static u8 maskFromPayload1(u32 payload1) {
    if (payload1 == 0)
        return 0;
    if (smso::looksLikePackedCollectibleWorldPos(payload1))
        return 0;
    if (payload1 > 0xFFu)
        return 0;
    return static_cast<u8>(payload1 & 0xFFu);
}

static void applySingleRedCoinCollected(TFlagManager *fm, TGCConsole2 *console, u8 stableIndex,
                                        u8 payload0, u32 payload1, u32 payload2) {
    if (!fm || stableIndex >= kMaxStageRedCoins)
        return;

    // Do NOT blanket-skip applies while "solo". That blocked co-op replay for joiners and
    // even local collect echoes. Solo death reset is handled by same-stage-reload mission
    // reset (server clears authority + durable history) + excluding solo stages from resync.

    // Red-coin switch arming is local-only. Collection sync may remember/hide coins, but must
    // not write mRedCoinCount or call processDownCoin until this client has armed the mission
    // (local switch press or live pre-placed coins). Otherwise durable replay / periodic
    // resync after a stage reload reopens the red-coin HUD with no switch press.
    const u8 publishHudSlot = payload0 & 0xF;
    const u32 targetCount = (payload0 >> 4) & 0xF;
    const bool missionLive = isLocalRedCoinMissionLive(fm);
    const u8 remoteMask = maskFromPayload1(payload1);

    TVec3f fingerprintPos = {};
    bool haveFingerprint = false;
    if (smso::isValidPackedWorldPos(payload2) && smso::looksLikePackedCollectibleWorldPos(payload2)) {
        smso::unpackCollectibleWorldPos(payload2, fingerprintPos.x, fingerprintPos.y,
                                        fingerprintPos.z);
        haveFingerprint = true;
    }

    if (!sStageSnapshotReady && missionLive) {
        SortedCoinCtx sorted = {};
        gatherSortedIdentityCoins(&sorted);
        if (sorted.count == 0)
            gatherSortedLiveRedCoins(&sorted);
        if (sorted.count > 0)
            buildSnapshotFromSorted(sorted);
    }
    maybeFinalizeSwitchCohort(fm);

    const bool already = (sCollectedMask & static_cast<u8>(1u << stableIndex)) != 0;
    markCollectedIndex(stableIndex, publishHudSlot);
    if (haveFingerprint)
        rememberCollectedPos(stableIndex, fingerprintPos);
    if (remoteMask != 0)
        sCollectedMask |= remoteMask;

    OSReport("[SMSOBB] red-coin apply i=%u mask=0x%02X count=%u live=%u already=%u pos=%u\n",
             stableIndex, sCollectedMask, targetCount > 0 ? targetCount : popCountMask(sCollectedMask),
             missionLive ? 1u : 0u, already ? 1u : 0u, haveFingerprint ? 1u : 0u);

    bool hidLiveCoin = false;
    if (missionLive && !already) {
        bindPendingCollectedByPosition();
        expandSnapshotWithNewRedCoins();
        if (stableIndex < sStageRedCoinCount &&
            isBoundActorValid(sStageRedCoins[stableIndex].actor) &&
            !isActorDeadOrTaken(sStageRedCoins[stableIndex].actor))
            hidLiveCoin = true;
        hideRedCoinByStableIndex(stableIndex);
        if (!hidLiveCoin && haveFingerprint)
            hidLiveCoin = hideRedCoinByInitialPos(fingerprintPos);
    } else if (missionLive && already) {
        // Replay / durable apply — hide bound dead/taken only; never pos-hunt new drops.
        hideRedCoinByStableIndex(stableIndex);
    }

    const u32 resolvedCount =
        targetCount > 0 ? targetCount : static_cast<u32>(popCountMask(sCollectedMask));

    if (!missionLive) {
        sPendingHudCatchUp = true;
        return;
    }

    if (static_cast<u32>(fm->Type6Flag.mRedCoinCount) != resolvedCount) {
        fm->Type6Flag.mRedCoinCount = static_cast<s32>(resolvedCount);
        sLastRedCoinCount = resolvedCount;
    }

    if (!already && resolvedCount > 0)
        applyHudSlotForCollection(console, publishHudSlot, stableIndex);

    // Sound/particles only when a live world coin was actually removed — avoids
    // Pianta/NPC reward sound-only when the drop was never hidden (or not spawned).
    if (!already && hidLiveCoin) {
        TVec3f fxPos = fingerprintPos;
        if (!haveFingerprint && stableIndex < sStageRedCoinCount)
            fxPos = sStageRedCoins[stableIndex].initialPos;
        smso::playRemoteCoinCollectParticles(fxPos, false);
    }
}

/// Detect which snapshot entry was collected locally via bound pointer state — not position.
static u8 findLocallyCollectedStableIndex() {
    if (!sStageSnapshotReady || sStageRedCoinCount == 0)
        return kInvalidHudSlot;

    for (u8 i = 0; i < sStageRedCoinCount; ++i) {
        if ((sCollectedMask & static_cast<u8>(1u << i)) != 0)
            continue;

        TCoin *actor = sStageRedCoins[i].actor;
        if (!isBoundActorValid(actor))
            continue; // wait for rebind / adopt — never invent from a null slot
        if (isActorDeadOrTaken(actor))
            return i;
    }

    // Fallback: any unset index whose bound actor is no longer among live collectibles.
    SortedCoinCtx sorted = {};
    gatherSortedLiveRedCoins(&sorted);
    for (u8 i = 0; i < sStageRedCoinCount; ++i) {
        if ((sCollectedMask & static_cast<u8>(1u << i)) != 0)
            continue;
        TCoin *actor = sStageRedCoins[i].actor;
        if (!isBoundActorValid(actor))
            continue;
        bool stillLive = false;
        for (u8 j = 0; j < sorted.count; ++j) {
            if (sorted.coins[j] == actor) {
                stillLive = true;
                break;
            }
        }
        if (!stillLive)
            return i;
    }

    return kInvalidHudSlot;
}

static void detectLocalRedCoinProgress(TFlagManager *fm, u8 courseId, u8 episodeId) {
    const u32 count = static_cast<u32>(fm->Type6Flag.mRedCoinCount);
    while (count > sLastRedCoinCount) {
        maybeFinalizeSwitchCohort(fm);
        // Register live deferred drops first so findLocallyCollected can see them.
        // Only adopt an untracked dead coin when the collected actor was never expanded
        // into the snapshot (avoids expand+adopt double-seat growth).
        expandSnapshotWithNewRedCoins();

        if (!sStageSnapshotReady) {
            SortedCoinCtx sorted = {};
            gatherSortedIdentityCoins(&sorted);
            if (sorted.count == 0)
                gatherSortedLiveRedCoins(&sorted);
            if (sorted.count > 0)
                buildSnapshotFromSorted(sorted);
            expandSnapshotWithNewRedCoins();
        }

        // Switch cohort not locked yet — defer so we do not publish append-order indices.
        if (fm->Type5Flag.mRedCoinSwitchPressed && !sSnapshotFinal) {
            OSReport("[SMSOBB] red-coin collect-defer switch-cohort count=%u snap=%u\n", count,
                     sStageRedCoinCount);
            break;
        }

        u8 stableIndex = findLocallyCollectedStableIndex();
        if (stableIndex == kInvalidHudSlot || stableIndex >= kMaxStageRedCoins) {
            adoptDeadUntrackedRedCoins(1);
            stableIndex = findLocallyCollectedStableIndex();
        }
        if (stableIndex == kInvalidHudSlot || stableIndex >= kMaxStageRedCoins) {
            // Do NOT invent "first unset bit" — that hid unrelated settle coins when a
            // drop was collected before it was adopted into the snapshot.
            OSReport("[SMSOBB] red-coin collect-defer count=%u snap=%u mask=0x%02X\n", count,
                     sStageRedCoinCount, sCollectedMask);
            break;
        }

        u8 publishHudSlot = kInvalidHudSlot;
        if (stableIndex < sStageRedCoinCount)
            publishHudSlot = sStageRedCoins[stableIndex].hudSlot;
        if (publishHudSlot == kInvalidHudSlot || publishHudSlot >= kMaxStageRedCoins)
            publishHudSlot = stableIndex;

        markCollectedIndex(stableIndex, publishHudSlot);
        sStageRedCoins[stableIndex].active = false;
        if (isBoundActorValid(sStageRedCoins[stableIndex].actor)) {
            auto *base = reinterpret_cast<TMapObjBase *>(sStageRedCoins[stableIndex].actor);
            rememberCollectedPos(stableIndex, base->mInitialPosition);
        }

        const u32 newCount = sLastRedCoinCount + 1;
        const u32 payload1 = static_cast<u32>(sCollectedMask);
        u32 payload2 = 0;
        if (entryHasPos(sStageRedCoins[stableIndex])) {
            const TVec3f &p = sStageRedCoins[stableIndex].initialPos;
            payload2 = smso::packCollectibleWorldPos(p.x, p.y, p.z);
        }
        publishLocalRedCoinEvent(smso::WE_RED_COIN_COLLECTED, courseId, episodeId, publishHudSlot,
                                 stableIndex, payload1, payload2);
        sLastRedCoinCount = newCount;

        OSReport("[SMSOBB] red-coin collect i=%u mask=0x%02X count=%u\n", stableIndex,
                 sCollectedMask, newCount);
    }
}

static bool applyRedCoinWorldEventOnStage(const smso::CommWorldEvent &event) {
    TFlagManager *fm = TFlagManager::smInstance;
    if (!fm)
        return false;

    TGCConsole2 *console = gpMarDirector ? gpMarDirector->mGCConsole : nullptr;

    sApplyingRemoteEvent = true;

    switch (static_cast<smso::WorldEventType>(event.type)) {
    case smso::WE_RED_COIN_COLLECTED: {
        // Mission-reset sentinel is server-only; never treat as a collected index.
        if (event.reserved == kRedCoinMissionResetReserved) {
            sApplyingRemoteEvent = false;
            return true;
        }
        const u8 stableIndex = redCoinStableIndex(event.reserved);
        applySingleRedCoinCollected(fm, console, stableIndex, event.payload0, event.payload1,
                                    event.payload2);
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
    sSoloMissionAttempt = false;
    sPublishedSoloMissionReset = false;
    sStageHadSameStagePeer = false;
    sPendingSoloResetFrames = 0;
    sRequestProgressHoldFrames = 0;
}

void notifyRedCoinStageEnter() {
    sForceStageTrackerReset = true;
}

void captureLocalRedCoinProgress() {
    if (sApplyingRemoteEvent)
        return;

    CommBuffer *buf = getCommBuffer();
    const bool publishEnabled = redCoinPublishEnabled(buf);

    // One-shot progress request for the launcher (co-op same-stage death reload).
    if (buf && sRequestProgressHoldFrames > 0) {
        buf->bridgeFlags |= smso::BF_REQUEST_PROGRESS;
        --sRequestProgressHoldFrames;
        if (sRequestProgressHoldFrames == 0)
            buf->bridgeFlags &= ~static_cast<u32>(smso::BF_REQUEST_PROGRESS);
    }

    const u8 courseId = currentCourseId();
    const u8 episodeId = currentEpisodeId();
    if (isHubArea(courseId))
        return;

    if (sForceStageTrackerReset || courseId != sLastCourseId || episodeId != sLastEpisodeId) {
        // Capture before resetRedCoinTrackers overwrites sLast*.
        const u8 prevCourse = sLastCourseId;
        const u8 prevEpisode = sLastEpisodeId;
        // Death / soft reload stays on the same course+episode. Joiners and first visits
        // change course/episode — they must NOT publish mission-reset (that wiped co-op
        // authority while occupancy was still 1).
        const bool sameStageReload =
            prevCourse == courseId && prevEpisode == episodeId && prevCourse != 0xFF;

        sForceStageTrackerReset = false;
        const bool peerNow = hasSameStagePeer(buf);
        if (!sameStageReload) {
            // New stage visit — sticky peer starts from who is here now.
            sStageHadSameStagePeer = peerNow;
            sPendingSoloResetFrames = 0;
        } else if (peerNow) {
            sStageHadSameStagePeer = true;
            sPendingSoloResetFrames = 0;
        }

        // Sticky: once a same-stage peer was seen this visit, never solo-reset on death
        // reload just because remote snapshots briefly drop during load.
        const bool treatAsSolo =
            publishEnabled && !peerNow && !sStageHadSameStagePeer;
        sSoloMissionAttempt = treatAsSolo;
        const bool preserveCoop =
            sameStageReload && publishEnabled && (peerNow || sStageHadSameStagePeer);
        resetRedCoinTrackersForStage(courseId, episodeId, preserveCoop);

        // Only solo death/reload clears server authority. First enter + joiners skip this.
        // Brief peer gaps: delay the sentinel until solo is confirmed for ~3s.
        if (sameStageReload && treatAsSolo && publishEnabled && !sPublishedSoloMissionReset) {
            sPendingSoloResetFrames = 1;
        } else if (sameStageReload && !treatAsSolo) {
            sPendingSoloResetFrames = 0;
            OSReport("[SMSOBB] red-coin stage-enter co-op course=%u/%u (no mission-reset)\n",
                     courseId, episodeId);
            // Soft death keeps course/episode — launcher will not auto stage-enter resync.
            // Ask for an immediate authority snapshot so hides/HUD do not wait ~45s.
            sRequestProgressHoldFrames = 8;
        }

        flushDeferredRedCoinEvents();
    }

    // Confirm delayed solo mission-reset after peer snapshots stay empty.
    if (sPendingSoloResetFrames > 0 && publishEnabled && !sPublishedSoloMissionReset) {
        if (hasSameStagePeer(buf)) {
            sStageHadSameStagePeer = true;
            sSoloMissionAttempt = false;
            sPendingSoloResetFrames = 0;
            OSReport("[SMSOBB] red-coin co-op peer joined — resume persist course=%u/%u\n",
                     courseId, episodeId);
        } else if (sPendingSoloResetFrames >= kSoloResetConfirmFrames) {
            sPendingSoloResetFrames = 0;
            sPublishedSoloMissionReset = true;
            publishLocalRedCoinEvent(smso::WE_RED_COIN_COLLECTED, courseId, episodeId, 0,
                                     kRedCoinMissionResetReserved, 0, 0);
            OSReport("[SMSOBB] red-coin solo-mission-reset course=%u/%u (same-stage reload)\n",
                     courseId, episodeId);
        } else {
            ++sPendingSoloResetFrames;
        }
    }

    // Peer joined mid-attempt: leave solo mode so applies/resync can catch them up.
    if (hasSameStagePeer(buf)) {
        sStageHadSameStagePeer = true;
        if (sSoloMissionAttempt) {
            sSoloMissionAttempt = false;
            sPendingSoloResetFrames = 0;
            OSReport("[SMSOBB] red-coin co-op peer joined — resume persist course=%u/%u\n",
                     courseId, episodeId);
        }
    }

    if (sStageSettleFrames < kStageSettleFrames)
        ++sStageSettleFrames;

    if (redCoinScanReady(buf) && !sStageSnapshotReady)
        captureStageRedCoinSnapshot();

    TFlagManager *fmEarly = TFlagManager::smInstance;
    if (publishEnabled && fmEarly) {
        const bool switchNow = fmEarly->Type5Flag.mRedCoinSwitchPressed;
        if (switchNow && !sWasSwitchPressed) {
            // Fresh switch arm: drop any partial append snapshot so we wait for the
            // full 8-coin cohort sorted by initialPos (shared with remotes).
            if (!sSnapshotFinal && sCollectedMask == 0 && sStageRedCoinCount < kMaxStageRedCoins) {
                sStageSnapshotReady = false;
                sStageRedCoinCount = 0;
                for (u32 i = 0; i < kMaxStageRedCoins; ++i)
                    sStageRedCoins[i] = {};
                OSReport("[SMSOBB] red-coin switch-arm reset partial snap\n");
            }
        }
        sWasSwitchPressed = switchNow;
        maybeFinalizeSwitchCohort(fmEarly);
    }

    // Enemy-drop stages: always expand/claim late live reds (Pianta etc.).
    // Switch missions: expand early-returns until switch-cohort finalizes.
    if (publishEnabled && sStageSnapshotReady) {
        if (sCollectedMask != 0)
            bindPendingCollectedByPosition();
        expandSnapshotWithNewRedCoins();
    }

    // Switch missions: coins appear after local arm — snapshot as soon as the cohort is full.
    if (publishEnabled && !sStageSnapshotReady) {
        TFlagManager *fmSwitch = TFlagManager::smInstance;
        if (fmSwitch && fmSwitch->Type5Flag.mRedCoinSwitchPressed) {
            maybeFinalizeSwitchCohort(fmSwitch);
            if (!sStageSnapshotReady) {
                SortedCoinCtx sorted = {};
                gatherSortedLiveRedCoins(&sorted);
                if (sorted.count > 0 && sorted.count < kMaxStageRedCoins) {
                    // Wait for full cohort — do not lock a partial append snapshot.
                } else if (sorted.count >= kMaxStageRedCoins) {
                    buildSnapshotFromSorted(sorted);
                }
            }
        }
    }

    TFlagManager *fm = TFlagManager::smInstance;
    if (fm)
        catchUpRedCoinHudIfArmed(fm);

    if (publishEnabled && fm)
        detectLocalRedCoinProgress(fm, courseId, episodeId);

    // Event-driven hides happen on apply; validate mask→actor bindings lightly each frame.
    if (fm && redCoinPublishEnabled(buf) && sCollectedMask != 0)
        reconcileCollectedRedCoinActors();

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
