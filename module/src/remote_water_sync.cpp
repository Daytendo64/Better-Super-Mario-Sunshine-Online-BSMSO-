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
// Cap so one distant remote cannot explode ModelWaterManager cost for everyone.
// Raised from 7500 so plaza-dock remote spray still runs hit checks (splashWall →
// clean) when the viewer stands across the plaza — live graffiti clear depends on
// remote droplets reaching ModelWaterManager::move, not only durable stamps.
constexpr f32 kMaxWaterCullRadius = 12000.0f;
constexpr f32 kRemoteSprayReachMargin = 3000.0f;
constexpr u8 kRemoteYoshiJuiceTintUnset = 0xFF;
// doldecomp TModelWaterManager::perform — 0x8 drawWaterVolume (drawTouching tint),
// 0x80 drawRefracAndSpec (TEVREG0 tint). Not Mario perform flag 0x200.
constexpr u32 kModelWaterPerformJuiceTintFlags = 0x88u;
// Soft per-request ceiling. Retail accumulate emits ~1–few droplets/frame; an uncapped
// _37C spike filled the SOA with huge overlapping cards that merge into a ribbon.
constexpr s32 kRemoteWaterDropletSoftCap = 12;

using ModelWaterPerformFn = void (*)(TModelWaterManager *, u32, JDrama::TGraphics *);

static ModelWaterPerformFn sOrigModelWaterPerform = nullptr;
static u8 gRemoteYoshiJuiceDrawTint = kRemoteYoshiJuiceTintUnset;
static u8 sWaterCullUpdateCounter = 0;

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

    if (radius > kMaxWaterCullRadius)
        radius = kMaxWaterCullRadius;
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

static bool snapshotSprayingWater(const PlayerSnapshot &snap) {
    return (snap.vfxFlags & VFX_WATER_SPRAY) != 0 && (snap.vfxFlags & VFX_FLUDD_EMPTY) == 0;
}

// Orange/pink/purple Yoshi juice indexes waterColor[1..3] (high alpha → opaque ribbon).
// Green (0) is the same GXColor as FLUDD water — not a juice override.
static u8 findRemoteYoshiJuiceTintFromSnapshots() {
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
        if (!snapshotSprayingWater(snap))
            continue;

        const u8 tint = clampWaterCardType(unpackSecondNozzle(snap.nozzleId));
        if (tint == 0)
            continue;
        return tint;
    }

    return kRemoteYoshiJuiceTintUnset;
}

static bool anyRemoteFluddSpraying() {
    CommBuffer *buf = getCommBuffer();
    if (!buf || (buf->bridgeFlags & BF_CONNECTED) == 0)
        return false;

    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (slot == buf->localSlot)
            continue;

        const PlayerSnapshot &snap = buf->remoteSnapshots[slot];
        if (snap.connected == 0)
            continue;
        if (snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags))
            continue;
        if (!snapshotSprayingWater(snap))
            continue;
        return true;
    }
    return false;
}

static bool localFluddSpraying() {
    if (!gpMarioAddress || gpMarioAddress->onYoshi())
        return false;
    if (gpMarioAddress->mAttributes.mIsFluddEmitting)
        return true;
    if (!gpMarioAddress->mFludd)
        return false;
    return gpMarioAddress->mFludd->mIsEmitWater;
}

static u8 activeRemoteYoshiJuiceDrawTint() {
    const u8 fromSnapshots = findRemoteYoshiJuiceTintFromSnapshots();
    if (fromSnapshots != kRemoteYoshiJuiceTintUnset)
        return fromSnapshots;
    // gRemoteYoshiJuiceDrawTint may be green(0) from notify — treat as unset.
    if (gRemoteYoshiJuiceDrawTint == 0 || gRemoteYoshiJuiceDrawTint == kRemoteYoshiJuiceTintUnset)
        return kRemoteYoshiJuiceTintUnset;
    return gRemoteYoshiJuiceDrawTint;
}

// Resolve the single global ModelWater tint for this draw.
// Retail SMS has one Mario → one tint. Multiplayer must not let Yoshi juice
// (waterColor[1]=opaque orange) leak onto FLUDD droplets / the local water HUD.
static u8 resolveModelWaterDrawTint(u8 currentCard) {
    const bool localOnYoshi =
        gpMarioAddress && gpMarioAddress->onYoshi() && gpMarioAddress->mYoshi != nullptr;

    if (localOnYoshi) {
        // Retail TYoshi::movement owns unk5D5F while mounted (juice HUD + juice spray).
        return clampWaterCardType(static_cast<u8>(gpMarioAddress->mYoshi->mType));
    }

    // FLUDD (local or remote) always wins: force clear water. Juice tint has high
    // alpha (0x6E vs water 0x14) and turns streams into muddy opaque ribbons.
    if (localFluddSpraying() || anyRemoteFluddSpraying())
        return 0;

    const u8 remoteJuice = activeRemoteYoshiJuiceDrawTint();
    if (remoteJuice != kRemoteYoshiJuiceTintUnset)
        return remoteJuice;

    // Heal stuck juice tint left after a prior Yoshi ride / bad notify.
    (void)currentCard;
    return 0;
}

static void smso_ModelWaterManager_perform(TModelWaterManager *mgr, u32 flags,
                                           JDrama::TGraphics *graphics) {
    if (mgr && sOrigModelWaterPerform && (flags & kModelWaterPerformJuiceTintFlags) != 0) {
        const u8 desired = resolveModelWaterDrawTint(static_cast<u8>(mgr->mWaterCardType));
        // Do NOT restore the previous card after draw — that re-armed juice tint and
        // turned the next FLUDD frame into an opaque muddy ribbon / red WATER HUD.
        mgr->mWaterCardType = static_cast<s8>(desired);
    }

    sOrigModelWaterPerform(mgr, flags, graphics);
}

static void healWaterCardTypeIfNeeded() {
    if (!gpModelWaterManager || !gpMarioAddress)
        return;
    // While local rides Yoshi, retail keeps juice card type for the HUD gauge.
    if (gpMarioAddress->onYoshi())
        return;
    // Remote-only Yoshi juice may tint draw via the perform hook; keep the HUD on water
    // unless we are actively showing remote juice with no FLUDD competing.
    if (localFluddSpraying() || anyRemoteFluddSpraying()) {
        gpModelWaterManager->mWaterCardType = 0;
        return;
    }
    if (activeRemoteYoshiJuiceDrawTint() != kRemoteYoshiJuiceTintUnset)
        return;
    gpModelWaterManager->mWaterCardType = 0;
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
    // Heal juice-tint leak into the local WATER HUD every tick (cheap).
    healWaterCardTypeIfNeeded();

    // One shared cull scan at 15 Hz is enough with the reach margin.
    if ((++sWaterCullUpdateCounter & 3u) != 0)
        return;
    applyWaterCullRadius();
}

void emitRemoteWaterRequest(TWaterEmitInfo *emitInfo) {
    if (!emitInfo || !gpModelWaterManager)
        return;

    // Ensure cull radius covers this emitter before retail makeEmit distance checks.
    applyWaterCullRadius();

    // Soft-cap pathological mNum spikes. Local TNozzle*::emit does not clamp, but
    // remotes can accumulate a huge _37C after pressure/resync glitches; uncapped
    // mNum fills the particle SOA with overlapping cards that merge into a ribbon.
    s32 n = emitInfo->mNum.get();
    if (n < 0)
        n = 0;
    if (n > kRemoteWaterDropletSoftCap)
        n = kRemoteWaterDropletSoftCap;
    emitInfo->mNum.set(n);

    // Force clear-water card and leave it — restoring a prior juice index after emit
    // recreated the muddy ribbon on the next ModelWater draw/move.
    gpModelWaterManager->mWaterCardType = 0;

    // Retail WaterGun also sets mFlag=0x40 before emitRequest — keep it. Live graffiti
    // clear on viewers comes from ModelWaterManager::move → splashWall/Ground → clean().
    gpModelWaterManager->emitRequest(*emitInfo);
}

void emitRemoteWaterRequestWithCardTint(TWaterEmitInfo *emitInfo, u8 waterCardType) {
    if (!emitInfo || !gpModelWaterManager)
        return;

    const u8 card = clampWaterCardType(waterCardType);
    // Green Yoshi skips juice droplets in retail; don't pretend-tint emits.
    if (card == 0) {
        emitRemoteWaterRequest(emitInfo);
        return;
    }

    const s8 saved = gpModelWaterManager->mWaterCardType;
    gpModelWaterManager->mWaterCardType = static_cast<s8>(card);

    // Juice emits use the same soft mNum cap / cull path, but must keep juice card
    // type during emitRequest (bypass the FLUDD force-0 wrapper).
    applyWaterCullRadius();
    s32 n = emitInfo->mNum.get();
    if (n < 0)
        n = 0;
    if (n > kRemoteWaterDropletSoftCap)
        n = kRemoteWaterDropletSoftCap;
    emitInfo->mNum.set(n);
    gpModelWaterManager->emitRequest(*emitInfo);

    gpModelWaterManager->mWaterCardType = saved;
}

void resetRemoteYoshiJuiceDrawTint() {
    gRemoteYoshiJuiceDrawTint = kRemoteYoshiJuiceTintUnset;
}

void notifyRemoteYoshiJuiceDrawTint(u8 yoshiType) {
    const u8 card = clampWaterCardType(yoshiType);
    // Green Yoshi juice is water-colored — do not arm the global juice override.
    if (card == 0) {
        gRemoteYoshiJuiceDrawTint = kRemoteYoshiJuiceTintUnset;
        return;
    }
    gRemoteYoshiJuiceDrawTint = card;
}

} // namespace smso
