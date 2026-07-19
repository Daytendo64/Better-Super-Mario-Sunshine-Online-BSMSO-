#include "monte_clean_sync.hpp"

#include "world_sync.hpp"

#include <Dolphin/OS.h>
#include <SMS/NPC/NpcBase.hxx>
#include <SMS/Strategic/LiveActor.hxx>
#include <SMS/Strategic/Strategy.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/raw_fn.hxx>
#include <math.h>
#include <sdk.h>

extern TMarDirector *gpMarDirector;
extern TStrategy *gpStrategy;

// doldecomp gpPollution @ NTSC-U 0x8040DED0
#define smso_gpPollution (*reinterpret_cast<void **>(SMS_PORT_REGION(0x8040DED0, 0, 0, 0x8040DED0)))

namespace {

// doldecomp TLiveFlagBits — sink / buried state used by evCheckMonteClear.
constexpr u32 kLiveFlagUnk10 = 0x10u;
constexpr u32 kLiveFlagUnk400000 = 0x400000u;   // sunk in goop
constexpr u32 kLiveFlagSinkBottom = 0x800000u; // NPC fully buried
constexpr u32 kHitFlagNoCollision = 0x1u;

// doldecomp TBaseNPC::mPollutionAmount @ 0x178; initial position unk194 @ 0x194.
constexpr u32 kNpcPollutionAmountOffset = 0x178u;
constexpr u32 kNpcInitialPosOffset = 0x194u;

constexpr u8 kMaxStageMonteCleans = 16;
constexpr u16 kStageSettleFrames = 90;
constexpr f32 kPosMatchEpsilon = 128.0f;
constexpr f32 kPollutionCleanRadius = 220.0f;
constexpr bool kMonteCleanHotPathOsReport = false;

struct StageMonteEntry {
    TBaseNPC *npc;
    TVec3f initialPos;
    u8 stableIndex;
    bool wasClear;
    bool active;
};

static StageMonteEntry sStageMontes[kMaxStageMonteCleans] = {};
static u8 sStageMonteCount = 0;
static u16 sCleanedMask = 0;
static u16 sStageSettleFrames = 0;
static u8 sLastCourseId = 0xFF;
static u8 sLastEpisodeId = 0xFF;
static bool sApplyingRemote = false;
static bool sSnapshotReady = false;

static u8 currentCourseId() {
    return gpMarDirector ? static_cast<u8>(gpMarDirector->mAreaID) : 0;
}

static u8 currentEpisodeId() {
    return gpMarDirector ? static_cast<u8>(gpMarDirector->mEpisodeID) : 0;
}

static bool monteCleanPublishEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0 &&
           (buf->bridgeFlags & (smso::BF_SYNC_EVENT | smso::BF_SYNC_MISSION)) != 0;
}

static bool monteCleanScanReady(const smso::CommBuffer *buf) {
    return monteCleanPublishEnabled(buf) && sStageSettleFrames >= kStageSettleFrames;
}

static bool isValidNpcPtr(const void *ptr) {
    const u32 addr = reinterpret_cast<u32>(ptr);
    return addr >= 0x80000000u && addr < 0x81800000u;
}

static bool isLiveNpc(const TBaseNPC *npc) {
    if (!npc || !isValidNpcPtr(npc))
        return false;
    const auto *live = reinterpret_cast<const TLiveActor *>(npc);
    if (live->mStateFlags.asFlags.mIsObjDead)
        return false;
    return true;
}

static bool isPollutionMonte(const TBaseNPC *npc) {
    if (!npc)
        return false;
    // Pianta Village Ep. 6 rescue targets are pollution-capable Montes.
    if (isPollutionNpc__8TBaseNPCCFv(npc) == 0)
        return false;
    return isNormalMonteM__8TBaseNPCCFv(npc) != 0 || isNormalMonteW__8TBaseNPCCFv(npc) != 0;
}

static f32 *npcPollutionAmount(TBaseNPC *npc) {
    return reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(npc) + kNpcPollutionAmountOffset);
}

static const f32 *npcPollutionAmountConst(const TBaseNPC *npc) {
    return reinterpret_cast<const f32 *>(reinterpret_cast<const u8 *>(npc) +
                                        kNpcPollutionAmountOffset);
}

static TVec3f *npcInitialPos(TBaseNPC *npc) {
    return reinterpret_cast<TVec3f *>(reinterpret_cast<u8 *>(npc) + kNpcInitialPosOffset);
}

static const TVec3f *npcInitialPosConst(const TBaseNPC *npc) {
    return reinterpret_cast<const TVec3f *>(reinterpret_cast<const u8 *>(npc) +
                                           kNpcInitialPosOffset);
}

static bool npcIsMonteClear(const TBaseNPC *npc) {
    if (!npc)
        return false;
    const auto *live = reinterpret_cast<const TLiveActor *>(npc);
    // Matches doldecomp evCheckMonteClear: !LIVE_FLAG_UNK400000 && isClean().
    if ((live->mStateFlags.asU32 & kLiveFlagUnk400000) != 0)
        return false;
    return *npcPollutionAmountConst(npc) <= 0.0f;
}

static void cleanPollutionAt(f32 x, f32 y, f32 z) {
    void *pollution = smso_gpPollution;
    if (!pollution)
        return;
    clean__17TPollutionManagerFffff(pollution, x, y, z, kPollutionCleanRadius);
}

static void forceMonteNpcCleared(TBaseNPC *npc) {
    if (!npc)
        return;

    auto *live = reinterpret_cast<TLiveActor *>(npc);
    TVec3f &pos = npc->mTranslation;
    const TVec3f *initial = npcInitialPos(npc);

    // Remove ground goop so changeNerveProc_ cannot re-sink the NPC.
    cleanPollutionAt(pos.x, pos.y, pos.z);
    if (initial)
        cleanPollutionAt(initial->x, initial->y, initial->z);

    *npcPollutionAmount(npc) = 0.0f;

    live->mStateFlags.asU32 &= ~(kLiveFlagUnk400000 | kLiveFlagSinkBottom | kLiveFlagUnk10);
    // BSE HitActor::mObjectType is doldecomp mHitFlags.
    npc->mObjectType &= ~kHitFlagNoCollision;

    // Raise buried NPCs back toward their load position so they are interactive.
    if (initial) {
        const f32 dy = initial->y - pos.y;
        if (dy > 8.0f) {
            pos.x = initial->x;
            pos.y = initial->y;
            pos.z = initial->z;
        }
    }
}

static bool positionsMatch(const TVec3f &a, const TVec3f &b, f32 eps) {
    const f32 dx = a.x - b.x;
    const f32 dy = a.y - b.y;
    const f32 dz = a.z - b.z;
    return dx * dx + dy * dy + dz * dz <= eps * eps;
}

static int comparePos(const TVec3f &a, const TVec3f &b) {
    if (a.x != b.x)
        return a.x < b.x ? -1 : 1;
    if (a.y != b.y)
        return a.y < b.y ? -1 : 1;
    if (a.z != b.z)
        return a.z < b.z ? -1 : 1;
    return 0;
}

static void sortStageMonteSnapshot() {
    for (u8 i = 0; i + 1 < sStageMonteCount; ++i) {
        for (u8 j = i + 1; j < sStageMonteCount; ++j) {
            if (comparePos(sStageMontes[i].initialPos, sStageMontes[j].initialPos) > 0) {
                const StageMonteEntry tmp = sStageMontes[i];
                sStageMontes[i] = sStageMontes[j];
                sStageMontes[j] = tmp;
            }
        }
    }
    for (u8 i = 0; i < sStageMonteCount; ++i)
        sStageMontes[i].stableIndex = i;
}

static void clearMonteTrackers() {
    sStageMonteCount = 0;
    sCleanedMask = 0;
    sSnapshotReady = false;
    for (u32 i = 0; i < kMaxStageMonteCleans; ++i)
        sStageMontes[i] = {};
}

static void captureStageMonteSnapshot() {
    clearMonteTrackers();
    if (!gpStrategy || !gpStrategy->mNPCGroup)
        return;

    for (auto &entry : gpStrategy->mNPCGroup->mViewObjList) {
        if (sStageMonteCount >= kMaxStageMonteCleans)
            break;
        auto *npc = reinterpret_cast<TBaseNPC *>(entry);
        if (!isLiveNpc(npc) || !isPollutionMonte(npc))
            continue;

        const TVec3f *initial = npcInitialPosConst(npc);
        StageMonteEntry &slot = sStageMontes[sStageMonteCount++];
        slot.npc = npc;
        slot.initialPos = initial ? *initial : npc->mTranslation;
        slot.stableIndex = 0;
        slot.wasClear = npcIsMonteClear(npc);
        slot.active = true;
    }

    sortStageMonteSnapshot();

    // Do NOT seed sCleanedMask from already-clear locals. wasClear alone tracks
    // clear→dirty→clear transitions for publish. Seeding the ownership mask made
    // reconcilePendingMonteCleans treat a later walk-into-goop sink as "must
    // force-unsink": it wiped pollution under the Pianta (radius 220) and freed
    // them without any player spray (dolphin.log: alreadyClear=0xFFFF then goop
    // vanishes under stuck Piantas). Mid-mission soft-reload keeps correct local
    // actor state; remote WE_NPC_CLEANED still sets mask bits for true rescues.

    sSnapshotReady = true;
    u16 alreadyClearBits = 0;
    for (u8 i = 0; i < sStageMonteCount; ++i) {
        if (sStageMontes[i].wasClear)
            alreadyClearBits |= static_cast<u16>(1u << i);
    }
    OSReport("[SMSOBB] monte-clean snapshot count=%u alreadyClear=0x%X mask=0x%X\n",
             sStageMonteCount, alreadyClearBits, sCleanedMask);
}

static u8 popCountMask(u16 mask) {
    u8 count = 0;
    for (u8 i = 0; i < kMaxStageMonteCleans; ++i) {
        if ((mask & static_cast<u16>(1u << i)) != 0)
            ++count;
    }
    return count;
}

static void publishLocalMonteClean(u8 stableIndex, const TVec3f &pos) {
    if (sApplyingRemote)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!monteCleanPublishEnabled(buf))
        return;

    const u32 packed = smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
    if (!smso::isValidPackedWorldPos(packed))
        return;

    const u8 localCount = popCountMask(sCleanedMask);
    const u8 payload0 = static_cast<u8>((localCount << 4) | (stableIndex & 0xFu));
    smso::enqueueLocalWorldEvent(static_cast<u8>(smso::WE_NPC_CLEANED), currentCourseId(),
                                 currentEpisodeId(), payload0, stableIndex, packed);
    if (kMonteCleanHotPathOsReport) {
        OSReport("[SMSOBB] monte-clean publish idx=%u count=%u pos=(%.0f,%.0f,%.0f)\n",
                 stableIndex, localCount, pos.x, pos.y, pos.z);
    }
}

static TBaseNPC *findMonteNear(const TVec3f &pos, f32 eps) {
    TBaseNPC *best = nullptr;
    f32 bestDistSq = eps * eps;

    for (u8 i = 0; i < sStageMonteCount; ++i) {
        TBaseNPC *npc = sStageMontes[i].npc;
        if (!isLiveNpc(npc))
            continue;
        const TVec3f &ref = sStageMontes[i].initialPos;
        const f32 dx = ref.x - pos.x;
        const f32 dy = ref.y - pos.y;
        const f32 dz = ref.z - pos.z;
        const f32 distSq = dx * dx + dy * dy + dz * dz;
        if (distSq > bestDistSq)
            continue;
        bestDistSq = distSq;
        best = npc;
    }

    if (best || !gpStrategy || !gpStrategy->mNPCGroup)
        return best;

    for (auto &entry : gpStrategy->mNPCGroup->mViewObjList) {
        auto *npc = reinterpret_cast<TBaseNPC *>(entry);
        if (!isLiveNpc(npc) || !isPollutionMonte(npc))
            continue;
        const TVec3f *initial = npcInitialPosConst(npc);
        const TVec3f &ref = initial ? *initial : npc->mTranslation;
        const f32 dx = ref.x - pos.x;
        const f32 dy = ref.y - pos.y;
        const f32 dz = ref.z - pos.z;
        const f32 distSq = dx * dx + dy * dy + dz * dz;
        if (distSq > bestDistSq)
            continue;
        bestDistSq = distSq;
        best = npc;
    }
    return best;
}

static void scanLocalMonteCleans() {
    if (sApplyingRemote || !sSnapshotReady)
        return;

    for (u8 i = 0; i < sStageMonteCount; ++i) {
        StageMonteEntry &entry = sStageMontes[i];
        if (!entry.active || !isLiveNpc(entry.npc))
            continue;

        const bool clear = npcIsMonteClear(entry.npc);
        const u16 bit = static_cast<u16>(1u << entry.stableIndex);
        if (!clear) {
            // Pianta (re)sank or got dirty — drop ownership so reconcile cannot
            // force-clean pollution / unsink them. Legitimate rescues re-publish
            // on the next clear transition.
            if ((sCleanedMask & bit) != 0) {
                sCleanedMask &= static_cast<u16>(~bit);
                if (kMonteCleanHotPathOsReport) {
                    OSReport("[SMSOBB] monte-clean resink drop idx=%u mask=0x%X\n",
                             entry.stableIndex, sCleanedMask);
                }
            }
            entry.wasClear = false;
            continue;
        }

        if (entry.wasClear)
            continue;
        entry.wasClear = true;

        if ((sCleanedMask & bit) != 0)
            continue;

        sCleanedMask |= bit;
        publishLocalMonteClean(entry.stableIndex, entry.initialPos);
    }
}

// Apply ownership mask to live actors when they exist. Never blocks the durable
 // mailbox — mask is recorded immediately on apply; visuals catch up here.
static void reconcilePendingMonteCleans() {
    if (!sSnapshotReady || sCleanedMask == 0 || sApplyingRemote)
        return;

    for (u8 i = 0; i < sStageMonteCount; ++i) {
        StageMonteEntry &entry = sStageMontes[i];
        if (!entry.active || !isLiveNpc(entry.npc))
            continue;
        const u16 bit = static_cast<u16>(1u << entry.stableIndex);
        if ((sCleanedMask & bit) == 0)
            continue;
        if (entry.wasClear && npcIsMonteClear(entry.npc))
            continue;

        sApplyingRemote = true;
        forceMonteNpcCleared(entry.npc);
        sApplyingRemote = false;
        entry.wasClear = true;
        if (kMonteCleanHotPathOsReport) {
            OSReport("[SMSOBB] monte-clean reconcile idx=%u mask=0x%X\n", entry.stableIndex,
                     sCleanedMask);
        }
    }
}

} // namespace

namespace smso {

void initMonteCleanSync() {
    clearMonteTrackers();
    sStageSettleFrames = 0;
    sLastCourseId = 0xFF;
    sLastEpisodeId = 0xFF;
}

void notifyMonteCleanStageEnter() {
    clearMonteTrackers();
    sStageSettleFrames = 0;
    sLastCourseId = currentCourseId();
    sLastEpisodeId = currentEpisodeId();
}

void updateMonteCleanSync() {
    if (!gpMarDirector)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!monteCleanPublishEnabled(buf))
        return;
    if (gpMarDirector->mCurState != TMarDirector::STATE_NORMAL)
        return;

    const u8 course = currentCourseId();
    const u8 episode = currentEpisodeId();
    if (course != sLastCourseId || episode != sLastEpisodeId) {
        notifyMonteCleanStageEnter();
        sLastCourseId = course;
        sLastEpisodeId = episode;
    }

    if (sStageSettleFrames < kStageSettleFrames)
        ++sStageSettleFrames;

    if (!monteCleanScanReady(buf))
        return;

    if (!sSnapshotReady)
        captureStageMonteSnapshot();

    scanLocalMonteCleans();
    reconcilePendingMonteCleans();
}

bool applyMonteCleanWorldEvent(const CommWorldEvent &event) {
    if (event.type != static_cast<u8>(WE_NPC_CLEANED))
        return false;

    // Episode-scoped: launcher defers off-stage; consume here so the durable mailbox advances.
    if (event.courseId != currentCourseId() || event.episodeId != currentEpisodeId())
        return true;

    const u8 stableIndex = event.reserved < kMaxStageMonteCleans
                               ? event.reserved
                               : static_cast<u8>(event.payload0 & 0xFu);
    if (stableIndex >= kMaxStageMonteCleans)
        return true; // drop malformed; do not block durable queue forever

    // Ownership first: always record the cleaned bit and free the mailbox. Actor
    // hide/unsink is reconciled when NPCs exist (never hold shine/blue behind settle).
    const u16 bit = static_cast<u16>(1u << stableIndex);
    sCleanedMask |= bit;

    if (!sSnapshotReady && gpStrategy && gpStrategy->mNPCGroup)
        captureStageMonteSnapshot();

    f32 x = 0.0f, y = 0.0f, z = 0.0f;
    TBaseNPC *npc = nullptr;
    if (isValidPackedWorldPos(event.payload1)) {
        unpackCollectibleWorldPos(event.payload1, x, y, z);
        npc = findMonteNear(TVec3f{x, y, z}, kPosMatchEpsilon);
    }

    if (!npc && stableIndex < sStageMonteCount)
        npc = sStageMontes[stableIndex].npc;

    if (!npc || !isLiveNpc(npc)) {
        OSReport("[SMSOBB] monte-clean apply-mask idx=%u defer-visual packed=0x%08X\n",
                 stableIndex, event.payload1);
        return true;
    }

    sApplyingRemote = true;
    forceMonteNpcCleared(npc);
    sApplyingRemote = false;

    for (u8 i = 0; i < sStageMonteCount; ++i) {
        if (sStageMontes[i].npc == npc) {
            sStageMontes[i].wasClear = true;
            break;
        }
    }

    const u8 authCount = static_cast<u8>((event.payload0 >> 4) & 0xFu);
    OSReport("[SMSOBB] monte-clean apply idx=%u authCount=%u mask=0x%X\n", stableIndex, authCount,
             sCleanedMask);
    return true;
}

} // namespace smso
