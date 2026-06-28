#include "remote_water_sync.hpp"

#include "comm_buffer.hpp"
#include "yoshi_sync.hpp"

#include <BetterSMS/module.hxx>
#include <SMS/Manager/ModelWaterManager.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/raw_fn.hxx>
#include <math.h>

extern TModelWaterManager *gpModelWaterManager;
extern TMario *gpMarioAddress;

namespace smso {

namespace {

// doldecomp TModelWaterManager::unk5E08 — horizontal cull radius from local Mario.
constexpr u32 kModelWaterCullRadiusOffset = 0x5E08;
constexpr f32 kVanillaWaterCullRadius = 5000.0f;
constexpr f32 kRemoteSprayReachMargin = 2000.0f;
constexpr s32 kRemoteWaterDropletMaxPerRequest = 6;
constexpr u8 kRemoteYoshiJuiceTintUnset = 0xFF;
// doldecomp TModelWaterManager::perform — 0x8 drawWaterVolume (drawTouching tint),
// 0x80 drawRefracAndSpec (TEVREG0 tint). Not Mario perform flag 0x200.
constexpr u32 kModelWaterPerformJuiceTintFlags = 0x88u;

using ModelWaterPerformFn = void (*)(TModelWaterManager *, u32, JDrama::TGraphics *);

static ModelWaterPerformFn sOrigModelWaterPerform = nullptr;
static u8 gRemoteYoshiJuiceDrawTint = kRemoteYoshiJuiceTintUnset;

static f32 *getWaterCullRadius() {
    if (!gpModelWaterManager)
        return nullptr;
    return reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(gpModelWaterManager) +
                                   kModelWaterCullRadiusOffset);
}

static f32 horizontalDistance(f32 ax, f32 az, f32 bx, f32 bz) {
    const f32 dx = ax - bx;
    const f32 dz = az - bz;
    return sqrtf(dx * dx + dz * dz);
}

static f32 computeNeededWaterCullRadius() {
    f32 radius = kVanillaWaterCullRadius;
    if (!gpMarioAddress)
        return radius;

    const f32 localX = gpMarioAddress->mTranslation.x;
    const f32 localZ = gpMarioAddress->mTranslation.z;

    CommBuffer *buf = getCommBuffer();
    if (!buf || (buf->bridgeFlags & BF_CONNECTED) == 0)
        return radius;

    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (slot == buf->localSlot)
            continue;

        const PlayerSnapshot &snap = buf->remoteSnapshots[slot];
        if (snap.connected == 0)
            continue;

        const f32 needed =
            horizontalDistance(localX, localZ, snap.position.x, snap.position.z) +
            kRemoteSprayReachMargin;
        if (needed > radius)
            radius = needed;
    }

    return radius;
}

static void applyWaterCullRadius() {
    f32 *cullRadius = getWaterCullRadius();
    if (!cullRadius)
        return;
    *cullRadius = computeNeededWaterCullRadius();
}

static u8 clampWaterCardType(u8 type) {
    return static_cast<u8>(type & 0x03);
}

static u8 findRemoteYoshiSprayTintFromSnapshots() {
    CommBuffer *buf = getCommBuffer();
    if (!buf || (buf->bridgeFlags & BF_CONNECTED) == 0)
        return kRemoteYoshiJuiceTintUnset;

    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (slot == buf->localSlot)
            continue;

        const PlayerSnapshot &snap = buf->remoteSnapshots[slot];
        if (snap.connected == 0)
            continue;
        if (!snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags))
            continue;
        if ((snap.vfxFlags & VFX_WATER_SPRAY) == 0 || (snap.vfxFlags & VFX_FLUDD_EMPTY) != 0)
            continue;

        return clampWaterCardType(unpackSecondNozzle(snap.nozzleId));
    }

    return kRemoteYoshiJuiceTintUnset;
}

static u8 activeRemoteYoshiJuiceDrawTint() {
    const u8 fromSnapshots = findRemoteYoshiSprayTintFromSnapshots();
    if (fromSnapshots != kRemoteYoshiJuiceTintUnset)
        return fromSnapshots;
    return gRemoteYoshiJuiceDrawTint;
}

static void smso_ModelWaterManager_perform(TModelWaterManager *mgr, u32 flags,
                                           JDrama::TGraphics *graphics) {
    u8 savedCard = 0;
    bool overrideTint = false;

    // Draw passes read mWaterCardType globally (doldecomp ModelWaterManager.cpp).
    const u8 remoteTint = activeRemoteYoshiJuiceDrawTint();
    if (mgr && sOrigModelWaterPerform && remoteTint != kRemoteYoshiJuiceTintUnset &&
        (flags & kModelWaterPerformJuiceTintFlags) != 0) {
        bool applyTint = true;
        if (gpMarioAddress && gpMarioAddress->onYoshi() && gpMarioAddress->mYoshi) {
            // Local Yoshi already owns unk5D5F during movement(); only override when colors differ.
            const u8 localTint =
                clampWaterCardType(static_cast<u8>(gpMarioAddress->mYoshi->mType));
            applyTint = remoteTint != localTint;
        }

        if (applyTint) {
            savedCard = mgr->mWaterCardType;
            mgr->mWaterCardType = remoteTint;
            overrideTint = true;
        }
    }

    sOrigModelWaterPerform(mgr, flags, graphics);

    if (overrideTint && mgr)
        mgr->mWaterCardType = savedCard;
}

static void hookModelWaterPerform() {
    if (sOrigModelWaterPerform)
        return;

    // doldecomp __vt__18TModelWaterManager + 0x4 → perform (after dtor slot).
    u32 *performSlot = reinterpret_cast<u32 *>(SMS_PORT_REGION(0x803DE9F0, 0x803D61D0, 0x803DE9F0, 0));
    sOrigModelWaterPerform = reinterpret_cast<ModelWaterPerformFn>(*performSlot);
    *performSlot = reinterpret_cast<u32>(&smso_ModelWaterManager_perform);
}

} // namespace

void initRemoteWaterSync() {
    applyWaterCullRadius();
    hookModelWaterPerform();
}

void updateRemoteWaterSync() {
    applyWaterCullRadius();
}

void emitRemoteWaterRequest(TWaterEmitInfo *emitInfo) {
    if (!emitInfo || !gpModelWaterManager)
        return;

    const s32 requested = emitInfo->mNum.get();
    if (requested > kRemoteWaterDropletMaxPerRequest)
        emitInfo->mNum.set(kRemoteWaterDropletMaxPerRequest);
    else if (requested < 0)
        emitInfo->mNum.set(0);

    applyWaterCullRadius();
    gpModelWaterManager->emitRequest(*emitInfo);
}

void emitRemoteWaterRequestWithCardTint(TWaterEmitInfo *emitInfo, u8 waterCardType) {
    if (!emitInfo || !gpModelWaterManager)
        return;

    const u8 card = clampWaterCardType(waterCardType);
    const u8 saved = gpModelWaterManager->mWaterCardType;
    gpModelWaterManager->mWaterCardType = card;
    emitRemoteWaterRequest(emitInfo);
    gpModelWaterManager->mWaterCardType = saved;
}

void resetRemoteYoshiJuiceDrawTint() {
    gRemoteYoshiJuiceDrawTint = kRemoteYoshiJuiceTintUnset;
}

void notifyRemoteYoshiJuiceDrawTint(u8 yoshiType) {
    gRemoteYoshiJuiceDrawTint = clampWaterCardType(yoshiType);
}

} // namespace smso
