#include "remote_water_sync.hpp"

#include "comm_buffer.hpp"

#include <SMS/Manager/ModelWaterManager.hxx>
#include <SMS/Player/Mario.hxx>
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

} // namespace

void initRemoteWaterSync() {
    applyWaterCullRadius();
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

} // namespace smso
