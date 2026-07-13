#include "npc_sync.hpp"

#include "comm_buffer.hpp"
#include "remote_actor.hpp"
#include "world_sync.hpp"

#include <BetterSMS/memory.hxx>
#include <Dolphin/MTX.h>
#include <Dolphin/OS.h>
#include <SMS/NPC/NpcBase.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/NozzleTrigger.hxx>
#include <SMS/Player/Watergun.hxx>
#include <SMS/Strategic/HitActor.hxx>
#include <SMS/Strategic/LiveActor.hxx>
#include <SMS/Strategic/Strategy.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>
#include <SMS/raw_fn.hxx>
#include <math.h>
#include <sdk.h>

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;
extern TStrategy *gpStrategy;

namespace {

constexpr u8 kNpcReactWet = 1;
constexpr u8 kNpcReactTrample = 2;
constexpr u8 kNpcReactMad = 3;

constexpr u32 kHitMessageTrample = 0x0u;
constexpr u32 kHitMessageSprayedByWater = 0xFu;

constexpr u32 kVtReceiveMessageOffset = 0x24;
constexpr u32 kMaxVtReceiveHooks = 4;
constexpr u32 kMaxTrackedNpcs = 96;
constexpr u16 kNpcReactCooldownFrames = 48;
// Emit-geometry spray is precise enough that a short cooldown still avoids spam.
constexpr u16 kRemoteSprayPollCooldown = 8;
constexpr u16 kRemoteTramplePollCooldown = 36;
constexpr u16 kLocalSprayPollCooldown = 8;
constexpr bool kNpcReactHotPathOsReport = false;

constexpr f32 kNpcMatchRadius = 128.0f;
constexpr f32 kNpcMatchRadiusSq = kNpcMatchRadius * kNpcMatchRadius;
// Retail ModelWaterManager static hit actor is ~50–80uu; ray length covers typical spray reach.
constexpr f32 kSprayHitRadius = 70.0f;
constexpr f32 kSprayHitRadiusSq = kSprayHitRadius * kSprayHitRadius;
constexpr f32 kSprayRayLength = 900.0f;
constexpr f32 kSprayNearPad = 40.0f;
constexpr f32 kTrampleHeightPad = 8.0f;
constexpr f32 kRemoteTrampleMaxHeight = 220.0f;
constexpr f32 kDefaultMarioAttackRadius = 50.0f;
constexpr f32 kDefaultNpcReceiveRadius = 60.0f;
constexpr f32 kTrampleRadiusPad = 12.0f;

constexpr u32 kMaxPendingRemoteNpc = 8;
constexpr u8 kPendingRemoteNpcRetries = 90;

using ReceiveMessageFn = bool (*)(THitActor *, THitActor *, u32);
using IsNpcPredFn = bool (*)(const TBaseNPC *);

struct VtReceiveHook {
    u32 vtable;
    ReceiveMessageFn orig;
};

static VtReceiveHook sVtReceiveHooks[kMaxVtReceiveHooks] = {};
static u32 sVtReceiveHookCount = 0;
static bool sNpcHooksInstalled = false;
static bool sApplyingRemoteNpcEvent = false;
static bool sRetryingPendingNpcEvent = false;

struct TrackedNpc {
    TBaseNPC *npc;
    u32 initialPackedPos;
    u16 cooldownFrames;
    u16 remoteSprayCooldown;
    u16 remoteTrampleCooldown;
    u16 localSprayCooldown;
};

struct PendingRemoteNpcEvent {
    u8 kind;
    u8 actorSlot;
    u32 packedPos;
    u32 payload2;
    u8 retriesLeft;
};

static TrackedNpc sTrackedNpcs[kMaxTrackedNpcs] = {};
static u32 sTrackedNpcCount = 0;
static bool sNpcSnapshotReady = false;
static PendingRemoteNpcEvent sPendingRemoteNpc[kMaxPendingRemoteNpc] = {};

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

static TVec3f npcWorldPos(const TBaseNPC *npc) {
    return npc ? npc->mTranslation : TVec3f{0.0f, 0.0f, 0.0f};
}

static f32 npcPosDistSq(const TBaseNPC *npc, const TVec3f &target) {
    const TVec3f live = npcWorldPos(npc);
    const f32 dx = live.x - target.x;
    const f32 dy = live.y - target.y;
    const f32 dz = live.z - target.z;
    return dx * dx + dy * dy + dz * dz;
}

static bool npcObjectSyncEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0 &&
           (buf->bridgeFlags & smso::BF_SYNC_OBJECTS) != 0;
}

static u8 currentCourseId() {
    return gpMarDirector ? static_cast<u8>(gpMarDirector->mAreaID) : 0;
}

static u8 currentEpisodeId() {
    return gpMarDirector ? static_cast<u8>(gpMarDirector->mEpisodeID) : 0;
}

static bool npcBehavesToWater(const TBaseNPC *npc) {
    if (!npc)
        return false;
    return isBehaveToWaterNpc__8TBaseNPCCFv(npc) != 0;
}

static bool npcCanBeTrampled(const TBaseNPC *npc) {
    if (!npc)
        return false;
    return isBeTrampledNpc__8TBaseNPCCFv(npc) != 0;
}

static bool npcCanGoMad(const TBaseNPC *npc) {
    if (!npc)
        return false;
    return isMadNpc__8TBaseNPCCFv(npc) != 0;
}

static bool reactionKindValidForNpc(u8 kind, const TBaseNPC *npc) {
    switch (kind) {
    case kNpcReactWet:
        return npcBehavesToWater(npc);
    case kNpcReactTrample:
        return npcCanBeTrampled(npc);
    case kNpcReactMad:
        return npcCanGoMad(npc) || npcCanBeTrampled(npc) || npcBehavesToWater(npc);
    default:
        return false;
    }
}

static u32 retailMessageForKind(u8 kind, u32 payload2) {
    if (payload2 != 0)
        return payload2;
    switch (kind) {
    case kNpcReactWet:
        return kHitMessageSprayedByWater;
    case kNpcReactTrample:
    case kNpcReactMad:
        return kHitMessageTrample;
    default:
        return kHitMessageSprayedByWater;
    }
}

static TrackedNpc *findTrackedNpc(TBaseNPC *npc) {
    for (u32 i = 0; i < sTrackedNpcCount; ++i) {
        if (sTrackedNpcs[i].npc == npc)
            return &sTrackedNpcs[i];
    }
    return nullptr;
}

static void clearNpcTrackers() {
    sTrackedNpcCount = 0;
    sNpcSnapshotReady = false;
    for (u32 i = 0; i < kMaxTrackedNpcs; ++i)
        sTrackedNpcs[i] = {};
}

static void clearPendingNpcEvents() {
    for (u32 i = 0; i < kMaxPendingRemoteNpc; ++i)
        sPendingRemoteNpc[i] = {};
}

static void enqueuePendingRemoteNpcEvent(u8 kind, u8 actorSlot, u32 packedPos, u32 payload2) {
    for (u32 i = 0; i < kMaxPendingRemoteNpc; ++i) {
        PendingRemoteNpcEvent &slot = sPendingRemoteNpc[i];
        if (slot.retriesLeft == 0)
            continue;
        if (slot.kind == kind && slot.actorSlot == actorSlot && slot.packedPos == packedPos &&
            slot.payload2 == payload2) {
            slot.retriesLeft = kPendingRemoteNpcRetries;
            return;
        }
    }

    for (u32 i = 0; i < kMaxPendingRemoteNpc; ++i) {
        PendingRemoteNpcEvent &slot = sPendingRemoteNpc[i];
        if (slot.retriesLeft != 0)
            continue;
        slot = {kind, actorSlot, packedPos, payload2, kPendingRemoteNpcRetries};
        return;
    }
}

static void snapshotStageNpcs() {
    clearNpcTrackers();
    if (!gpStrategy || !gpStrategy->mNPCGroup)
        return;

    for (auto &entry : gpStrategy->mNPCGroup->mViewObjList) {
        if (sTrackedNpcCount >= kMaxTrackedNpcs)
            break;
        auto *npc = reinterpret_cast<TBaseNPC *>(entry);
        if (!isLiveNpc(npc))
            continue;
        // Skip talk-only / non-interactive stubs (Peach stand-in, etc.).
        if (npc->mObjectID == 0x400001Cu || npc->mObjectID == 0x400001Du)
            continue;
        if (!npcBehavesToWater(npc) && !npcCanBeTrampled(npc) && !npcCanGoMad(npc))
            continue;

        const TVec3f pos = npcWorldPos(npc);
        const u32 packed = smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
        if (!smso::isValidPackedWorldPos(packed))
            continue;

        TrackedNpc &slot = sTrackedNpcs[sTrackedNpcCount++];
        slot.npc = npc;
        slot.initialPackedPos = packed;
        slot.cooldownFrames = 0;
        slot.remoteSprayCooldown = 0;
        slot.remoteTrampleCooldown = 0;
        slot.localSprayCooldown = 0;
    }

    sNpcSnapshotReady = true;
    OSReport("[SMSOBB] npc-sync snapshot count=%u\n", sTrackedNpcCount);
}

static void tickNpcCooldowns() {
    for (u32 i = 0; i < sTrackedNpcCount; ++i) {
        TrackedNpc &slot = sTrackedNpcs[i];
        if (slot.cooldownFrames > 0)
            --slot.cooldownFrames;
        if (slot.remoteSprayCooldown > 0)
            --slot.remoteSprayCooldown;
        if (slot.remoteTrampleCooldown > 0)
            --slot.remoteTrampleCooldown;
        if (slot.localSprayCooldown > 0)
            --slot.localSprayCooldown;
    }
}

static TBaseNPC *findNpcNear(const TVec3f &pos, f32 maxDistSq, IsNpcPredFn pred) {
    TBaseNPC *best = nullptr;
    f32 bestDistSq = maxDistSq;
    for (u32 i = 0; i < sTrackedNpcCount; ++i) {
        TBaseNPC *npc = sTrackedNpcs[i].npc;
        if (!isLiveNpc(npc))
            continue;
        if (pred && !pred(npc))
            continue;
        const f32 distSq = npcPosDistSq(npc, pos);
        if (distSq > bestDistSq)
            continue;
        bestDistSq = distSq;
        best = npc;
    }

    if (best || !gpStrategy || !gpStrategy->mNPCGroup)
        return best;

    // Fallback scan if snapshot missed a late-spawned NPC.
    for (auto &entry : gpStrategy->mNPCGroup->mViewObjList) {
        auto *npc = reinterpret_cast<TBaseNPC *>(entry);
        if (!isLiveNpc(npc))
            continue;
        if (pred && !pred(npc))
            continue;
        const f32 distSq = npcPosDistSq(npc, pos);
        if (distSq > bestDistSq)
            continue;
        bestDistSq = distSq;
        best = npc;
    }
    return best;
}

static bool waterNpcPred(const TBaseNPC *npc) {
    return npcBehavesToWater(npc);
}

static bool trampleNpcPred(const TBaseNPC *npc) {
    return npcCanBeTrampled(npc);
}

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

static bool emitMtxTranslationValid(const Mtx &mtx) {
    const f32 x = mtx[0][3];
    const f32 y = mtx[1][3];
    const f32 z = mtx[2][3];
    return x == x && y == y && z == z;
}

static bool tryGetFluddEmit(TMario *mario, TVec3f *outOrigin, TVec3f *outDir) {
    if (!mario || !mario->mFludd || !outOrigin || !outDir)
        return false;

    TWaterGun *fludd = mario->mFludd;
    Mtx *emitMtx = fludd->getEmitMtx(0);
    if (emitMtx && emitMtxTranslationValid(*emitMtx)) {
        outOrigin->x = (*emitMtx)[0][3];
        outOrigin->y = (*emitMtx)[1][3];
        outOrigin->z = (*emitMtx)[2][3];
        // SMS emit matrices aim along +Z (column 2).
        outDir->x = (*emitMtx)[0][2];
        outDir->y = (*emitMtx)[1][2];
        outDir->z = (*emitMtx)[2][2];
    } else {
        TVec3f speed{};
        fludd->getEmitPosDirSpeed(0, outOrigin, outDir, &speed);
    }

    const f32 lenSq = outDir->x * outDir->x + outDir->y * outDir->y + outDir->z * outDir->z;
    if (lenSq < 1.0e-4f)
        return false;

    const f32 inv = 1.0f / sqrtf(lenSq);
    outDir->x *= inv;
    outDir->y *= inv;
    outDir->z *= inv;
    return true;
}

static f32 pointToSegmentDistSq(const TVec3f &p, const TVec3f &a, const TVec3f &b) {
    const f32 abx = b.x - a.x;
    const f32 aby = b.y - a.y;
    const f32 abz = b.z - a.z;
    const f32 apx = p.x - a.x;
    const f32 apy = p.y - a.y;
    const f32 apz = p.z - a.z;
    const f32 abLenSq = abx * abx + aby * aby + abz * abz;
    f32 t = 0.0f;
    if (abLenSq > 1.0e-4f) {
        t = (apx * abx + apy * aby + apz * abz) / abLenSq;
        if (t < 0.0f)
            t = 0.0f;
        else if (t > 1.0f)
            t = 1.0f;
    }
    const f32 cx = a.x + abx * t - p.x;
    const f32 cy = a.y + aby * t - p.y;
    const f32 cz = a.z + abz * t - p.z;
    return cx * cx + cy * cy + cz * cz;
}

static bool npcHitBySprayRay(const TBaseNPC *npc, const TVec3f &origin, const TVec3f &dir) {
    if (!npc)
        return false;
    const TVec3f npcPos = npcWorldPos(npc);
    // Prefer chest/focal height over feet.
    const TVec3f focal{npcPos.x, npcPos.y + 60.0f, npcPos.z};
    const TVec3f end{origin.x + dir.x * kSprayRayLength, origin.y + dir.y * kSprayRayLength,
                     origin.z + dir.z * kSprayRayLength};
    if (pointToSegmentDistSq(focal, origin, end) <= kSprayHitRadiusSq)
        return true;
    // Also accept near-nozzle body hits (short range puddle).
    const f32 dx = npcPos.x - origin.x;
    const f32 dy = npcPos.y - origin.y;
    const f32 dz = npcPos.z - origin.z;
    const f32 nearR = kSprayHitRadius + kSprayNearPad;
    return (dx * dx + dy * dy + dz * dz) <= nearR * nearR;
}

static TBaseNPC *findNpcAlongSpray(const TVec3f &origin, const TVec3f &dir, IsNpcPredFn pred) {
    TBaseNPC *best = nullptr;
    f32 bestDistSq = kSprayRayLength * kSprayRayLength;
    for (u32 i = 0; i < sTrackedNpcCount; ++i) {
        TBaseNPC *npc = sTrackedNpcs[i].npc;
        if (!isLiveNpc(npc))
            continue;
        if (pred && !pred(npc))
            continue;
        if (!npcHitBySprayRay(npc, origin, dir))
            continue;
        const f32 distSq = npcPosDistSq(npc, origin);
        if (distSq > bestDistSq)
            continue;
        bestDistSq = distSq;
        best = npc;
    }
    return best;
}

static f32 marioAttackRadiusForTrample(const TMario *mario) {
    if (!mario)
        return kDefaultMarioAttackRadius;
    // Remotes keep attack radius at 0 (must not enable collision); use a retail-like default.
    const f32 r = mario->mAttackRadius;
    if (r >= 20.0f)
        return r;
    return kDefaultMarioAttackRadius;
}

static f32 npcReceiveRadiusForTrample(const TBaseNPC *npc) {
    if (!npc)
        return kDefaultNpcReceiveRadius;
    const auto *hit = reinterpret_cast<const THitActor *>(npc);
    const f32 r = hit->mReceiveRadius;
    if (r >= 20.0f)
        return r;
    return kDefaultNpcReceiveRadius;
}

static bool remoteStateLooksDive(u32 state) {
    return state == TMario::STATE_DIVE || state == TMario::STATE_DIVEJUMP ||
           state == TMario::STATE_DIVESLIDE;
}

static bool remoteStateLooksJumpTrample(u32 state) {
    if (remoteStateLooksDive(state))
        return false;
    if (state == TMario::STATE_JUMP || state == TMario::STATE_FALL ||
        state == TMario::STATE_JUMPSIDE || state == TMario::STATE_JUMPSPIN ||
        state == TMario::STATE_JUMPSPINR || state == TMario::STATE_JUMPSPINL ||
        state == TMario::STATE_SLAMSTART || state == TMario::STATE_G_POUND ||
        state == TMario::STATE_SLAM)
        return true;
    // Common jump/fall bit used by several airborne statuses.
    return (state & 0x800u) != 0 && (state & 0x20000000u) != 0;
}

static bool remoteStateLooksLanding(u32 state) {
    if (remoteStateLooksDive(state))
        return false;
    return state == TMario::STATE_LAND_RECOVER || (state & 0xFFu) == 0x30;
}

static void publishNpcReact(u8 kind, u8 actorSlot, TBaseNPC *npc, u32 retailMsg) {
    if (sApplyingRemoteNpcEvent || !npc)
        return;
    if (!reactionKindValidForNpc(kind, npc) && kind != kNpcReactMad)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!npcObjectSyncEnabled(buf))
        return;
    if (!smso::objectSyncGameplayReady())
        return;

    TrackedNpc *tracked = findTrackedNpc(npc);
    if (tracked && tracked->cooldownFrames > 0)
        return;

    const TVec3f pos = npcWorldPos(npc);
    const u32 packedPos = smso::packCollectibleWorldPos(pos.x, pos.y, pos.z);
    if (!smso::isValidPackedWorldPos(packedPos))
        return;

    smso::enqueueLocalWorldEvent(static_cast<u8>(smso::WE_NPC_REACT), currentCourseId(),
                                 currentEpisodeId(), kind, actorSlot, packedPos, retailMsg);

    if (tracked)
        tracked->cooldownFrames = kNpcReactCooldownFrames;
    else if (sTrackedNpcCount < kMaxTrackedNpcs) {
        TrackedNpc &slot = sTrackedNpcs[sTrackedNpcCount++];
        slot.npc = npc;
        slot.initialPackedPos = packedPos;
        slot.cooldownFrames = kNpcReactCooldownFrames;
        slot.remoteSprayCooldown = 0;
        slot.remoteTrampleCooldown = 0;
        slot.localSprayCooldown = 0;
    }

    if (kNpcReactHotPathOsReport) {
        OSReport("[SMSOBB] npc-react publish kind=%u slot=%u pos=(%.0f,%.0f,%.0f) msg=0x%X\n",
                 kind, actorSlot, pos.x, pos.y, pos.z, retailMsg);
    }
}

static void tryCaptureLocalNpcMessage(THitActor *receiver, THitActor *sender, u32 msg) {
    if (sApplyingRemoteNpcEvent || !receiver || !sender)
        return;
    if (msg != kHitMessageTrample && msg != kHitMessageSprayedByWater)
        return;

    auto *npc = reinterpret_cast<TBaseNPC *>(receiver);
    if (!isLiveNpc(npc))
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    const u8 localSlot = buf ? buf->localSlot : 0;

    if (msg == kHitMessageSprayedByWater) {
        // Retail FLUDD water uses TModelWaterManager::mStaticHitActor as sender (not gpMario).
        // Mirror fruit_sync: accept 0xF whenever local spray nozzle is actively emitting.
        if (!localMarioWaterGunActive(gpMarioAddress))
            return;
        if (!npcBehavesToWater(npc))
            return;
        publishNpcReact(kNpcReactWet, localSlot, npc, msg);
        return;
    }

    // Trample / mad still require Mario himself as the message sender.
    if (sender != reinterpret_cast<THitActor *>(gpMarioAddress))
        return;
    if (!npcCanBeTrampled(npc) && !npcCanGoMad(npc))
        return;
    const u8 kind = npcCanGoMad(npc) ? kNpcReactMad : kNpcReactTrample;
    publishNpcReact(kind, localSlot, npc, msg);
}

static ReceiveMessageFn lookupReceiveMessageOrig(u32 vtable) {
    for (u32 i = 0; i < sVtReceiveHookCount; ++i) {
        if (sVtReceiveHooks[i].vtable == vtable)
            return sVtReceiveHooks[i].orig;
    }
    return nullptr;
}

static bool smso_npc_receiveMessage_captureHook(THitActor *self, THitActor *sender, u32 msg) {
    const ReceiveMessageFn orig = lookupReceiveMessageOrig(*reinterpret_cast<const u32 *>(self));
    const bool result = orig ? orig(self, sender, msg) : false;
    if (result)
        tryCaptureLocalNpcMessage(self, sender, msg);
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

static void registerNpcReceiveMessageHook(u32 vtable, u32 origFn) {
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
    sVtReceiveHooks[sVtReceiveHookCount++] = {vtable, reinterpret_cast<ReceiveMessageFn>(*slot)};
    BetterSMS::PowerPC::writeU32(slot, reinterpret_cast<u32>(&smso_npc_receiveMessage_captureHook));
}

static void initNpcReactHooks() {
    const u32 vtBaseNpc = SMS_PORT_REGION(0x803D8448, 0x803CFC38, 0, 0x803D8448);
    const u32 fnReceive =
        SMS_PORT_REGION(0x802073E8, 0x801FF2CC, 0, 0x802073E8);
    registerNpcReceiveMessageHook(vtBaseNpc, fnReceive);
}

static void pollLocalSprayCapture() {
    if (sApplyingRemoteNpcEvent || !gpMarioAddress)
        return;
    if (!localMarioWaterGunActive(gpMarioAddress))
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!npcObjectSyncEnabled(buf) || !smso::objectSyncGameplayReady())
        return;

    TVec3f origin{}, dir{};
    if (!tryGetFluddEmit(gpMarioAddress, &origin, &dir))
        return;

    TBaseNPC *npc = findNpcAlongSpray(origin, dir, waterNpcPred);
    if (!npc)
        return;

    TrackedNpc *tracked = findTrackedNpc(npc);
    if (tracked && (tracked->localSprayCooldown > 0 || tracked->cooldownFrames > 0))
        return;

    publishNpcReact(kNpcReactWet, buf->localSlot, npc, kHitMessageSprayedByWater);
    if (tracked)
        tracked->localSprayCooldown = kLocalSprayPollCooldown;
}

static void pollRemoteSprayForBody(u8 slot, TMario *body) {
    if (!body || !body->mFludd)
        return;

    TVec3f origin{}, dir{};
    if (!tryGetFluddEmit(body, &origin, &dir))
        return;

    TBaseNPC *npc = findNpcAlongSpray(origin, dir, waterNpcPred);
    if (!npc)
        return;

    TrackedNpc *tracked = findTrackedNpc(npc);
    if (tracked && (tracked->remoteSprayCooldown > 0 || tracked->cooldownFrames > 0))
        return;

    publishNpcReact(kNpcReactWet, slot, npc, kHitMessageSprayedByWater);
    if (tracked)
        tracked->remoteSprayCooldown = kRemoteSprayPollCooldown;
}

static void pollRemoteTrampleForBody(u8 slot, TMario *body, u32 remoteState) {
    if (!body)
        return;

    const bool jumpOnCandidate =
        remoteStateLooksJumpTrample(remoteState) || remoteStateLooksLanding(remoteState);
    if (!jumpOnCandidate)
        return;

    const TVec3f bodyPos = body->mTranslation;
    const f32 attackR = marioAttackRadiusForTrample(body);

    TBaseNPC *bestNpc = nullptr;
    TrackedNpc *bestTracked = nullptr;
    f32 bestDistSq = 1.0e12f;

    for (u32 n = 0; n < sTrackedNpcCount; ++n) {
        TrackedNpc &tracked = sTrackedNpcs[n];
        TBaseNPC *npc = tracked.npc;
        if (!isLiveNpc(npc) || !npcCanBeTrampled(npc))
            continue;
        if (tracked.remoteTrampleCooldown > 0 || tracked.cooldownFrames > 0)
            continue;

        const TVec3f npcPos = npcWorldPos(npc);
        if (bodyPos.y < npcPos.y - kTrampleHeightPad ||
            bodyPos.y > npcPos.y + kRemoteTrampleMaxHeight)
            continue;

        const f32 receiveR = npcReceiveRadiusForTrample(npc);
        const f32 interactR = attackR + receiveR + kTrampleRadiusPad;
        const f32 dx = bodyPos.x - npcPos.x;
        const f32 dz = bodyPos.z - npcPos.z;
        const f32 distSqXZ = dx * dx + dz * dz;
        if (distSqXZ > interactR * interactR)
            continue;

        if (distSqXZ >= bestDistSq)
            continue;
        bestDistSq = distSqXZ;
        bestNpc = npc;
        bestTracked = &tracked;
    }

    if (!bestNpc || !bestTracked)
        return;

    const u8 kind = npcCanGoMad(bestNpc) ? kNpcReactMad : kNpcReactTrample;
    publishNpcReact(kind, slot, bestNpc, kHitMessageTrample);
    bestTracked->remoteTrampleCooldown = kRemoteTramplePollCooldown;
}

static void pollRemoteNpcInteractions() {
    if (sApplyingRemoteNpcEvent)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!npcObjectSyncEnabled(buf) || !smso::objectSyncGameplayReady())
        return;

    // Local spray belt-and-suspenders: spraying client publishes even if receiveMessage
    // capture missed (and covers the lowest-slot authority self-skip case).
    pollLocalSprayCapture();

    // Only the lowest-slot connected client publishes remote-provoked reactions so
    // every peer does not spam the same WE_NPC_REACT (server also dedups briefly).
    u8 authoritySlot = buf->localSlot;
    for (u32 j = 0; j < smso::MAX_REMOTE_SLOTS; ++j) {
        const smso::PlayerSnapshot &other = buf->remoteSnapshots[j];
        if (other.connected == 0)
            continue;
        if (other.slot < authoritySlot)
            authoritySlot = other.slot;
    }
    if (authoritySlot != buf->localSlot)
        return;

    for (u32 i = 0; i < smso::MAX_REMOTE_SLOTS; ++i) {
        const smso::PlayerSnapshot &snap = buf->remoteSnapshots[i];
        if (snap.connected == 0)
            continue;

        const u8 slot = snap.slot;
        if (slot == buf->localSlot)
            continue;

        TMario *body = smso::getRemoteBodyForSlot(slot);
        if (!body)
            body = smso::getRemoteBodyForSlotLoose(slot);
        if (!body)
            continue;

        const u32 remoteState =
            static_cast<u32>(snap.actionId) | (static_cast<u32>(snap.actionIdHi) << 16);

        const bool spraying = (snap.vfxFlags & smso::VFX_WATER_SPRAY) != 0 &&
                              (snap.vfxFlags & smso::VFX_FLUDD_EMPTY) == 0;
        if (spraying)
            pollRemoteSprayForBody(slot, body);

        pollRemoteTrampleForBody(slot, body, remoteState);
    }
}

static void spoofMarioToward(TMario *mario, const TVec3f &pos, u32 desiredState, f32 speedY,
                             u32 *savedState, TVec3f *savedPos, f32 *savedSpeedY) {
    if (!mario || !savedState || !savedPos || !savedSpeedY)
        return;
    *savedState = mario->mState;
    *savedPos = mario->mTranslation;
    *savedSpeedY = mario->mSpeed.y;
    mario->mTranslation = pos;
    if (desiredState != 0)
        mario->mState = desiredState;
    mario->mSpeed.y = speedY;
}

static void restoreMario(TMario *mario, u32 savedState, const TVec3f &savedPos, f32 savedSpeedY) {
    if (!mario)
        return;
    mario->mState = savedState;
    mario->mTranslation = savedPos;
    mario->mSpeed.y = savedSpeedY;
}

static TVec3f wetSpoofPosForNpc(TMario *remoteBody, const TBaseNPC *npc) {
    TVec3f actorPos = npcWorldPos(npc);
    actorPos.y += 40.0f;

    if (!remoteBody)
        return actorPos;

    TVec3f origin{}, dir{};
    if (tryGetFluddEmit(remoteBody, &origin, &dir)) {
        // Spoof near the emit origin so wet nerves see a droplet-like sender, not body center.
        const TVec3f npcPos = npcWorldPos(npc);
        const f32 toNpcX = npcPos.x - origin.x;
        const f32 toNpcY = (npcPos.y + 60.0f) - origin.y;
        const f32 toNpcZ = npcPos.z - origin.z;
        const f32 lenSq = toNpcX * toNpcX + toNpcY * toNpcY + toNpcZ * toNpcZ;
        if (lenSq > 1.0f) {
            const f32 inv = 1.0f / sqrtf(lenSq);
            // Place spoof slightly in front of the nozzle toward the NPC.
            actorPos.x = origin.x + toNpcX * inv * 40.0f;
            actorPos.y = origin.y + toNpcY * inv * 40.0f;
            actorPos.z = origin.z + toNpcZ * inv * 40.0f;
            return actorPos;
        }
        return origin;
    }

    return remoteBody->mTranslation;
}

} // namespace

namespace smso {

void initNpcSync() {
    clearNpcTrackers();
    clearPendingNpcEvents();
}

void ensureNpcReactHooks() {
    if (sNpcHooksInstalled)
        return;
    initNpcReactHooks();
    sNpcHooksInstalled = true;
    OSReport("[SMSOBB] npc-react hooks installed (%u receive vtables)\n", sVtReceiveHookCount);
}

void resetNpcSyncForStage() {
    clearNpcTrackers();
    clearPendingNpcEvents();
}

void updateNpcReactSync() {
    if (!gpMarDirector || !gpMarioAddress)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!npcObjectSyncEnabled(buf))
        return;
    if (!objectSyncGameplayReady())
        return;

    if (!sNpcSnapshotReady)
        snapshotStageNpcs();

    tickNpcCooldowns();
    pollRemoteNpcInteractions();
}

void deferRemoteNpcReact(u8 reactionKind, u8 actorSlot, u32 packedPos, u32 payload2) {
    enqueuePendingRemoteNpcEvent(reactionKind, actorSlot, packedPos, payload2);
}

void retryPendingRemoteNpcEvents() {
    sRetryingPendingNpcEvent = true;
    for (u32 i = 0; i < kMaxPendingRemoteNpc; ++i) {
        PendingRemoteNpcEvent &pending = sPendingRemoteNpc[i];
        if (pending.retriesLeft == 0)
            continue;

        if (applyRemoteNpcReact(pending.kind, pending.actorSlot, pending.packedPos,
                                  pending.payload2)) {
            pending = {};
            continue;
        }

        if (--pending.retriesLeft == 0)
            pending = {};
    }
    sRetryingPendingNpcEvent = false;
}

bool applyRemoteNpcReact(u8 reactionKind, u8 actorSlot, u32 packedPos, u32 payload2) {
    if (!isValidPackedWorldPos(packedPos))
        return false;
    if (reactionKind != kNpcReactWet && reactionKind != kNpcReactTrample &&
        reactionKind != kNpcReactMad)
        return false;

    f32 x = 0.0f, y = 0.0f, z = 0.0f;
    unpackCollectibleWorldPos(packedPos, x, y, z);
    const TVec3f target{x, y, z};

    IsNpcPredFn pred = nullptr;
    if (reactionKind == kNpcReactWet)
        pred = waterNpcPred;
    else if (reactionKind == kNpcReactTrample)
        pred = trampleNpcPred;

    TBaseNPC *npc = findNpcNear(target, kNpcMatchRadiusSq, pred);
    if (!npc && reactionKind == kNpcReactMad)
        npc = findNpcNear(target, kNpcMatchRadiusSq, nullptr);
    if (!npc || !isLiveNpc(npc)) {
        OSReport("[SMSOBB] npc-react apply miss kind=%u packed=0x%08X\n", reactionKind, packedPos);
        if (!sRetryingPendingNpcEvent)
            enqueuePendingRemoteNpcEvent(reactionKind, actorSlot, packedPos, payload2);
        return false;
    }

    TMario *mario = gpMarioAddress;
    if (!mario)
        return false;

    TMario *remoteBody = getRemoteBodyForSlot(actorSlot);
    if (!remoteBody)
        remoteBody = getRemoteBodyForSlotLoose(actorSlot);

    TVec3f actorPos{};
    u32 desiredState = 0;
    f32 spoofSpeedY = mario->mSpeed.y;
    if (reactionKind == kNpcReactWet) {
        actorPos = wetSpoofPosForNpc(remoteBody, npc);
    } else {
        // Jumping-compatible state so behaveToBeTrampled_ accepts the spoofed Mario.
        desiredState = TMario::STATE_JUMP;
        spoofSpeedY = -200.0f;
        if (remoteBody) {
            actorPos = remoteBody->mTranslation;
            if (remoteBody->mSpeed.y < spoofSpeedY)
                spoofSpeedY = remoteBody->mSpeed.y;
        } else {
            actorPos = npcWorldPos(npc);
            actorPos.y += 40.0f;
        }
    }

    u32 savedState = 0;
    TVec3f savedPos{};
    f32 savedSpeedY = 0.0f;
    sApplyingRemoteNpcEvent = true;
    spoofMarioToward(mario, actorPos, desiredState, spoofSpeedY, &savedState, &savedPos,
                     &savedSpeedY);

    const u32 msg = retailMessageForKind(reactionKind, payload2);
    THitActor *sender = static_cast<THitActor *>(mario);
    bool ok = false;

    if (reactionKind == kNpcReactMad) {
        // Prefer the retail trample path (builds mad via trample ctrl); fall back to nerve.
        ok = npc->receiveMessage(sender, kHitMessageTrample);
        if (!ok && npcCanGoMad(npc)) {
            changeNerveToMad___8TBaseNPCFv(npc);
            ok = true;
        }
    } else {
        ok = npc->receiveMessage(sender, msg);
    }

    restoreMario(mario, savedState, savedPos, savedSpeedY);
    sApplyingRemoteNpcEvent = false;

    if (!ok) {
        OSReport("[SMSOBB] npc-react apply fail kind=%u slot=%u packed=0x%08X\n", reactionKind,
                 actorSlot, packedPos);
        if (!sRetryingPendingNpcEvent)
            enqueuePendingRemoteNpcEvent(reactionKind, actorSlot, packedPos, payload2);
        return false;
    }

    TrackedNpc *tracked = findTrackedNpc(npc);
    if (tracked)
        tracked->cooldownFrames = kNpcReactCooldownFrames;

    if (kNpcReactHotPathOsReport) {
        OSReport("[SMSOBB] npc-react apply kind=%u slot=%u ok=%u pos=(%.0f,%.0f,%.0f)\n",
                 reactionKind, actorSlot, ok ? 1u : 0u, x, y, z);
    }
    return true;
}

} // namespace smso
