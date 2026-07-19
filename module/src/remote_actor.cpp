#include "remote_actor.hpp"
#include "blooper_surf_sync.hpp"
#include "fruit_sync.hpp"
#include "graffiti_clean_sync.hpp"
#include "remote_mario_audio.hpp"
#include "world_sync.hpp"
#include "comm_buffer.hpp"
#include "hide_seek.hpp"
#include "particle_ids.hpp"
#include "remote_water_sync.hpp"
#include "voice_sync.hpp"
#include "yoshi_sync.hpp"

class JKRHeap;

#ifdef SMSO_REMOTE_ENEMY_MARIO

#include <BetterSMS/libs/constmath.hxx>
#include <BetterSMS/module.hxx>
#include <BetterSMS/player.hxx>
#include <BetterSMS/settings.hxx>
#include <Dolphin/MTX.h>
#include <Dolphin/OS.h>
#include <Dolphin/string.h>
#include <math.h>
#include <SMS/Camera/PolarSubCamera.hxx>
#include <SMS/Manager/MarioParticleManager.hxx>
#include <SMS/Manager/ModelWaterManager.hxx>
#include <SMS/MSound/MSound.hxx>
#include <SMS/MSound/MSoundSESystem.hxx>
#include <SMS/Map/Map.hxx>
#include <SMS/MarioUtil/ShadowUtil.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/MarioCap.hxx>
#include <SMS/Player/NozzleBase.hxx>
#include <SMS/Player/NozzleTrigger.hxx>
#include <SMS/Player/Watergun.hxx>
#include <SMS/Player/Yoshi.hxx>
#include <SMS/Strategic/HitActor.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/macros.h>
#include <SMS/raw_fn.hxx>
#include <sdk.h>

#include <JSystem/J3D/J3DModel.hxx>
#include <JSystem/J3D/J3DShape.hxx>
#include <JSystem/J3D/J3DJoint.hxx>
#include <JSystem/JDrama/JDRNameRefGen.hxx>
#include <JSystem/JDrama/JDRViewObjPtrListT.hxx>
#include <JSystem/JSupport/JSUMemoryStream.hxx>
#include <JSystem/JKernel/JKRHeap.hxx>
#include "mario_model_system.hpp"
#include "mario_tex_anim.hpp"

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;
extern TMap *gpMap;
extern CPolarSubCamera *gpCamera;
extern MSound *gpMSound;
extern TModelWaterManager *gpModelWaterManager;

// doldecomp MoveBG/MapObjManager.hpp — shared surf-blooper MActor templates (Ricco / global patch).
struct TMapObjManager;
extern TMapObjManager *gpMapObjManager;

using namespace smso;

namespace {

// "プレーヤーグループ" (Player Group) in Shift-JIS — used only as a stage-ready signal.
// Remote puppets register on a separate view list (gRemotePerformGroup) and receive
// calc/draw via mirror dispatch from local Mario's perform() — not Player Group.
static const char kPlayerGroupName[] =
    "\x83\x76\x83\x8C\x81\x5B\x83\x84\x81\x5B\x83\x4F\x83\x8B\x81\x5B\x83\x76";

// Full TMario puppets are heavy (model + ~199 anims + cap + Yoshi), ~612 KiB each
// in practice. We pre-spawn a three-body spare pool on hub/loading and grow it
// on demand (at most 1 body per frame), avoiding both first-join work and a
// nine-body startup burst.
// Bodies + pack buffers live on the expanded-MEM1 remote heap and are kept across
// stage exits while BF_CONNECTED — only perform-group membership is cleared.
// A full MAX_PLAYERS session has MAX_PLAYERS-1 remotes (one body per remote
// network slot); 9 * ~612 KiB ≈ 5.4 MiB. Custom mario packs (~1.6–1.9 MiB each)
// also live in this arena via SMSLoadArchive. A 10-player lobby with unique
// models needs up to 10 packs (~19 MiB) + 9 bodies (~5.5 MiB) ≈ 25 MiB —
// soft-fail extras to retail when the mapped arena is short.
//
// CRITICAL: pool arrays are indexed by network slot id (0..MAX_PLAYERS-1), NOT by
// a dense 0..MAX_PLAYERS-2 range. Slot 9 must be addressable when localSlot != 9.
// kSessionMaxRemotes is the expected BODY COUNT (local left null), not the array size.
constexpr u32 kSessionMaxRemotes = MAX_PLAYERS - 1;
// Observed TMario graphs consume ~612 KiB. Reserve 704 KiB so initValues plus
// the custom BTK/MActor tail cannot exhaust the body heap or spill elsewhere.
constexpr size_t kRemoteBodySpawnMinFree = 0x000B0000u;
// Keep a small retail spare pool for immediate joins instead of allocating all
// nine heavy TMarios before anyone needs them. Additional bodies are prepared
// on demand, one at a time.
constexpr u32 kBaselinePrewarmBodies = 3;
// Ready-cache parking for prepared + demoted arena-backed graphs. Sized above
// the honest body-heap soft limit (~7 MiB / 768 KiB ≈ 9 arenas) so mid-stage
// never needs freeAll to admit a new identity — soft-defer when RAM is full.
constexpr u32 kReadyCustomBodyCapacity = 16;
// Soft A↔B retention hint (per-slot variant table). Never triggers mid-stage
// teardown; excess graphs stay parked until stage-boundary heap recycle.
constexpr u32 kRecentBodyVariantCapacity = 2;
// Child ExpHeap arenas hold one replacement TMario graph. Mid-stage never
// freeAll / ~TMario / destroy arenas — only stage-boundary recycle may. Live
// pool arenas are never freeAll'd while still referenced by gBodyPool / an actor.
constexpr size_t kRemoteBodyArenaBytes = 0x000C0000u; // 768 KiB
// Two preferred staging arenas created at stage start. Once a graph is born
// into one, that arena stays occupied until stage recycle; further identities
// allocate additional child ExpHeaps while the body heap has room.
constexpr u32 kBodyPingPongArenaCount = 2;
// Main-heap prewarm / first-residency bodies have no child arena. They are never
// scrub-and-forgotten mid-stage; demotion parks them here until stage recycle.
constexpr u32 kMainHeapParkedSpareCapacity = MAX_REMOTE_SLOTS;
constexpr u8 kReadyBodyActivationDelayTicks = 2;
// Legacy demotion delay (unused for mid-stage freeAll — reclaim is stage-only).
// Kept so stamp/log paths remain stable if a future stage-pass needs pacing.
constexpr u32 kBodyGraphReclaimDelayTicks = 6;
// Separate expensive archive/body phases so joins cannot create a burst of
// consecutive heavy frames even when several identities arrive together.
constexpr u8 kHeavyPreparationSpacingFrames = 6;
constexpr f32 kPreparationIdleSpeedSq = 4.0f * 4.0f;
constexpr u8 kSafePreparationIdleFrames = 8;
// Dolphin expanded MEM1 (48 MiB) puppet+pack arena. Retail SMS only configures
// CPU BATs for the stock 24 MiB, so this region faults until we map it
// (see ensureExtendedMem1Mapping).
//
// CRITICAL: the arena MUST start above retail arenaHi (~0x817fe4c0) and the
// 0x817FC000 mailbox. A 30 MiB heap at 0x81000000 dual-BAT-mapped and probed
// OK, then froze stage load — it overlapped the live root heap.
//
// Primary layout (dual-BAT, ~23.9 MiB):
//   DBAT0 widened 16→32 MiB covers 0x80000000..0x82000000
//   DBAT2 installed 16 MiB covers 0x82000000..0x83000000
//   Arena 0x81810000..0x82FF0000 sits across both BATs, above arenaHi.
// Fallback (proven): DBAT2 8→16 MiB only, 7.5 MiB @ 0x81810000..0x81F90000.
//
// NOTE: A single 64 MiB DBAT0 is impossible (would end at 0x84000000 past MEM1).
// A 32 MiB BAT at 0x81000000 is also invalid (not naturally 32 MiB-aligned).
constexpr u32 kRemoteActorExpandedHeapAddress = 0x81810000u;
constexpr size_t kRemoteActorExpandedHeapSize = 0x017E0000u; // 23.875 MiB → 0x82FF0000
constexpr size_t kRemoteActorExpandedHeapSizeFallback = 0x00780000u; // 7.5 MiB proven
// Full arena: 16.5 MiB immutable packs + 7.375 MiB bodies/J3D. This admits ten
// typical ~1.6 MiB roster archives while reserving >11 conservative 640 KiB
// body graphs. Worst-case 2 MiB packs soft-fail after eight entries.
constexpr size_t kRemotePackHeapSize = 0x01080000u;
// Fallback arena: 2.25 MiB packs + 5.25 MiB bodies. It intentionally admits
// one worst-case custom archive and about seven conservative body graphs.
constexpr size_t kRemotePackHeapSizeFallback = 0x00240000u;
// Only attempt the extended arena when Dolphin actually backs >24 MiB of MEM1.
constexpr u32 kMinMem1ForExpandedHeap = 0x02800000u; // 40 MiB
constexpr u32 kMem1CachedEnd = 0x83000000u;          // 48 MiB ceiling
constexpr u32 kMem1DualBatSplit = 0x82000000u;       // DBAT0 end / DBAT2 start

// u16 @0x114 holds the draw attribute bits (decomp unk114); 0x2 = visible.
constexpr u32 kAttr114VisibleBit = 0x2;
// doldecomp TMario::unk390 — bind-shadow body created in initValues().
constexpr u32 kMarioBindShadowBodyOffset = 0x390u;
// doldecomp mSinkTimer; graffiti sink suppresses shadow in retail perform().
constexpr u32 kMarioSinkTimerOffset = 0x368u;
constexpr f32 kShadowGroundProbeLift = 100.0f;
constexpr u8 kUpperStatePumping = 0;
constexpr u8 kUpperStateHoldingPump = 1;
constexpr u8 kUpperStateIdle = 5;

// Future cosmetic toggles (disabled for now).
constexpr bool kEnableRemoteShineShirt = false;
constexpr bool kEnableRemoteYCamHelmet = false;

constexpr u16 kAnimTurn = 0xBC;
constexpr u16 kAnimTurnEnd = 0xBD;
constexpr u32 kStateSideFlipAir = 0x00000887u;
constexpr u32 kStateSideFlipSlip = 0x04000473u;
constexpr u32 kStateSideFlipEnd = 0x0C000233u;
constexpr u16 kAnimSideFlipAir = 0xBF;  // ANIM_TJMP1
constexpr u16 kAnimSideFlipLand = 0xBE; // ANIM_TJMP2
constexpr s16 kModelFaceHalfTurn = 0x8000;
// ModelWater droplet emit is LOD-exempt and always runs at full game rate (60 Hz).
// Sound / mist JPA may still use a lighter cadence below.
constexpr u8 kFluddSprayEmitHz = 60;
constexpr u8 kFluddSprayEmitInterval = 60 / kFluddSprayEmitHz;
constexpr u8 kFluddSpraySoundHz = 30;
constexpr u8 kFluddSpraySoundInterval = 60 / kFluddSpraySoundHz;
// Remote swim VFX cadence. Retail swimMain() fires bubbles + a surface ripple each
// frame for one local Mario; rate-limiting keeps 9 simultaneous swimmers affordable.
constexpr u8 kSwimBubbleEmitHz = 30;
constexpr u8 kSwimBubbleEmitInterval = 60 / kSwimBubbleEmitHz; // every 2 frames
constexpr u8 kSwimRippleEmitHz = 10;
constexpr u8 kSwimRippleEmitInterval = 60 / kSwimRippleEmitHz; // every 6 frames
// Phase A remote movement SE cadence. Retail re-triggers slip / swim / surf every
// frame via soundMovement(); rate-limit so 9 remotes stay affordable.
constexpr u8 kMoveSeSlipHz = 8;
constexpr u8 kMoveSeSlipInterval = 60 / kMoveSeSlipHz;
constexpr u8 kMoveSeSwimHz = 5;
constexpr u8 kMoveSeSwimInterval = 60 / kMoveSeSwimHz;
constexpr u8 kMoveSeSurfHz = 8;
constexpr u8 kMoveSeSurfInterval = 60 / kMoveSeSurfHz;
constexpr u8 kMoveSeFootstepHz = 6;
constexpr u8 kMoveSeFootstepInterval = 60 / kMoveSeFootstepHz;
constexpr u8 kMoveSeRollHz = 4;
constexpr u8 kMoveSeRollInterval = 60 / kMoveSeRollHz;
constexpr f32 kMoveSeFootstepSpeedMin = 8.0f;
constexpr f32 kMoveSeRollSpeedMin = 12.0f;
constexpr u8 kRemoteDismissInvalidStreak = 3;

// doldecomp MarioStatus.hpp status type+id mask (low 9 bits of mState).
constexpr u32 kStatusTypeAndIdMask = 0x1FFu;

static u32 remoteStatusId(u32 state) {
    return state & kStatusTypeAndIdMask;
}

struct RemoteActorSlot;

static void syncRemoteNozzleGunAngle(TMario *mario, s16 pitch);

static bool ensureRemoteActorHeap();
static void dismissRemoteBody(u8 slotIndex, RemoteActorSlot &slot);
static void removeBodyFromViewList(TMario *body);
static void parkRemoteBody(TMario *body);
static void detachBodyBeforeReclaim(TMario *body);

static u32 stripSurfDrawFlag(u32 state) {
    return state & ~smso::kBlooperSurfDrawFlag;
}

static bool remoteMarioInWater(const TMario *mario) {
    if (!mario)
        return false;
    if (mario->mState == TMario::STATE_SWIM)
        return true;
    if ((mario->mState & TMario::STATE_WATERBORN) != 0)
        return true;
    if (mario->mAttributes.mIsWater || mario->mAttributes.mIsShallowWater)
        return true;
    return smso::isBlooperSurfState(mario->mState);
}

// Deep-water swimming (surface paddle + dive). STATE_WATERBORN (0x2000) is the
// reliable in-water flag set by the host's water collision and carried on the
// synced mState; it is distinct from Blooper surf (status id 0x046, no WATERBORN),
// which the surf VFX branch already handles.
static bool isRemoteSwimming(const TMario *mario) {
    if (!mario)
        return false;
    if ((mario->mState & TMario::STATE_WATERBORN) == 0)
        return false;
    if (smso::isBlooperSurfState(mario->mState))
        return false;
    return true;
}

static bool isSideFlipSequenceAnim(u16 animId) {
    return animId == kAnimSideFlipAir || animId == kAnimSideFlipLand;
}

static bool usesSideFlipModelOffset(u32 state, u16 animId) {
    if (state == kStateSideFlipAir || state == kStateSideFlipSlip || state == kStateSideFlipEnd)
        return true;
    if (!isSideFlipSequenceAnim(animId))
        return false;
    if (state & TMario::STATE_AIRBORN)
        return true;
    return state == kStateSideFlipSlip || state == kStateSideFlipEnd;
}

static bool sideFlipSequenceEnded(u32 state, u16 animId) {
    if (usesSideFlipModelOffset(state, animId))
        return false;
    if (state == TMario::STATE_RUNNING)
        return true;
    return !isSideFlipSequenceAnim(animId);
}
constexpr u16 kAnimSpinJump = 0xF4;
constexpr u16 kAnimSlideCatch = 0x88;  // ANIM_SLDCT — belly flop air + belly slide ground
constexpr u16 kAnimSlip = 0x91;        // ANIM_SLIP
constexpr u16 kAnimDiveWait = 0x137;   // ANIM_DIVE_WAIT — deep underwater dive pose
constexpr u16 kAnimDiveLand = 0x138;   // ANIM_DIVE_LAND
constexpr f32 kCatchSlideMaxFrame = 50.0f; // doldecomp catching() clamps grounded slide here
constexpr u32 kStatusJumpCatch = 0x08Au;   // MARIO_STATUS_JUMP_CATCH / STATE_DIVE
constexpr u32 kStatusIdCatchSlide = 0x056u; // MARIO_STATUS_CATCH / belly slide
// doldecomp demo warp / load states (full mState values).
constexpr u32 kStateWarpIn = 0x00001336u;
constexpr u32 kStateWarpOut = 0x00001337u;
constexpr u32 kStateNomotion = 0x0000133Eu;
constexpr u32 kStateDisappear = 0x0000133Fu;
constexpr u32 kStateWait = 0x0C400201u;
constexpr u16 kAnimWarpOutGate = 0x12Eu;
constexpr u16 kAnimWarpOutAlt = 0x13Bu;
// doldecomp TMario::warpIn() after warpInEffect: mWarpInBallsTime (70) + mWarpInCapturedTime (120).
constexpr u16 kWarpInAppearHideFrames = 190u;
constexpr u8 kShirtShapeIndex = 10u;
constexpr s16 kSpinYawStepPerFrame = 4096; // retail rotating()/rotateJumping() use mStatusTimer * 0x1000
// SMS world coordinates move by tens of units per frame. A four-unit threshold
// treated ordinary running as a teleport and repeatedly bypassed smoothing.
constexpr f32 kRemoteMotionSnapDistance = 700.0f;
constexpr f32 kRemotePositionBlendRate = 24.0f;
constexpr f32 kRemoteVelocityBlendRate = 20.0f;
constexpr f32 kRemoteRotationBlendRate = 30.0f;
// Visual scheduling is deliberately separate from snapshot/state application.
// Visible remotes never drop below a 30 Hz sampled pose, while their cached pose
// is re-rooted at render rate below. Off-screen remotes can use the cheaper 15 Hz
// cadence. The enter/exit gap prevents a remote near the quality boundary from
// alternating tiers as either player moves.
constexpr f32 kRemoteFullRateEnterDistanceSq = 2600.0f * 2600.0f;
constexpr f32 kRemoteFullRateExitDistanceSq = 3400.0f * 3400.0f;
constexpr f32 kRemoteShadowDistanceSq = 4500.0f * 4500.0f;
constexpr u8 kRemoteOffscreenGraceFrames = 6;
constexpr u8 kRemoteFullAnimInterval = 1;
constexpr u8 kRemoteVisibleAnimInterval = 2;
constexpr u8 kRemoteOffscreenAnimInterval = 4;
// Attachment-space contract:
// - J3D joint matrices are world-space matrices for the last sampled pose, then
//   re-rooted every rendered frame so translation/rotation stay smooth.
// - Nametags read the live head joint (same matrix the mesh uses) so the tag
//   cannot drift above a visible crown from a stale root-local conversion.
// - contact VFX positions are world-space and never bind to sampled joints.
constexpr f32 kHeadCrownWorldOffset = 18.0f;
constexpr f32 kHeadFallbackWorldOffset = 160.0f;
constexpr f32 kHeadAnchorMaxAboveBody = 220.0f;

constexpr u8 selectRemoteVisualInterval(u8 previousInterval, bool potentiallyOnScreen,
                                        f32 distanceSq) {
    if (!potentiallyOnScreen)
        return kRemoteOffscreenAnimInterval;

    const bool wasFullRate = previousInterval == kRemoteFullAnimInterval;
    const f32 threshold =
        wasFullRate ? kRemoteFullRateExitDistanceSq : kRemoteFullRateEnterDistanceSq;
    return distanceSq <= threshold ? kRemoteFullAnimInterval : kRemoteVisibleAnimInterval;
}

// Compile-time scheduler coverage: visible far actors stay at 30 Hz, tier
// hysteresis is stable in both directions, and only hidden actors reach 15 Hz.
static_assert(selectRemoteVisualInterval(2, true, 2500.0f * 2500.0f) == 1);
static_assert(selectRemoteVisualInterval(1, true, 3000.0f * 3000.0f) == 1);
static_assert(selectRemoteVisualInterval(2, true, 3000.0f * 3000.0f) == 2);
static_assert(selectRemoteVisualInterval(1, true, 3600.0f * 3600.0f) == 2);
static_assert(selectRemoteVisualInterval(1, false, 1000.0f * 1000.0f) == 4);

constexpr bool kRemoteHotPathOsReport = false; // gate periodic / spammy OSReport in hot paths
constexpr u32 kVisibilityDiagInterval = 1800;  // 30s @ 60 Hz when hot-path reports enabled
constexpr f32 kRemoteAnimResyncBehindFrames = 1.25f;
constexpr f32 kRemoteAnimResyncAheadFrames = 4.0f;
constexpr f32 kRemoteAnimLoopSpanFrames = 256.0f;
constexpr f32 kRemoteAnimLoopSnapFrames = 8.0f;
constexpr u16 kAnimHipAttack = 0x3D;
// Retail MarioStatus.hpp (doldecomp) — use full mState values, never STATUS_* bit tests on mState.
constexpr u32 kStatusRotateL = 0x00000441u;
constexpr u32 kStatusRotateR = 0x00000442u;
constexpr u32 kInvalidTrackState = 0xFFFFFFFFu;
constexpr u16 kAnimRun1 = 0x48;
constexpr u16 kAnimRun2 = 0x72;
constexpr u16 kCapModelHat = 1;
// doldecomp calcBaseMtx dereferences mHolder when this flag is set (tree / bar climb).
constexpr u32 kHolderDependentStateFlag = 0x100000;

static TMBindShadowBody *getMarioBindShadowBody(TMario *mario) {
    if (!mario)
        return nullptr;
    return reinterpret_cast<TMBindShadowBody *>(
        *reinterpret_cast<u32 *>(reinterpret_cast<u8 *>(mario) + kMarioBindShadowBodyOffset));
}

static bool isRemoteMarioSinking(const TMario *mario) {
    if (!mario)
        return false;
    const f32 sinkTimer =
        *reinterpret_cast<const f32 *>(reinterpret_cast<const u8 *>(mario) + kMarioSinkTimerOffset);
    return sinkTimer > 0.0f;
}

static bool shouldDrawRemoteShadow(const TMario *mario, u16 vfxFlags) {
    if (!mario || mario->mAttributes.mIsInvisible)
        return false;
    if (vfxFlags & VFX_DEAD)
        return false;

    const u32 attr114 = *reinterpret_cast<const u32 *>(reinterpret_cast<const u8 *>(mario) + 0x114);
    if ((attr114 & kAttr114VisibleBit) == 0)
        return false;
    if (isRemoteMarioSinking(mario))
        return false;
    return true;
}

// Remote puppets skip thinkHeight(); probe ground so bind-shadow placement matches retail.
static void syncRemoteShadowGround(TMario *mario) {
    if (!mario || !gpMap)
        return;

    // doldecomp TWaterGun::emit skips model-water when emit mtx is below the surface;
    // probing dry ground while swimming leaves stale triangles that crash FLUDD emit.
    if (remoteMarioInWater(mario))
        return;

    const TBGCheckData *plane = nullptr;
    const f32 x = mario->mTranslation.x;
    const f32 y = mario->mTranslation.y;
    const f32 z = mario->mTranslation.z;
    const f32 groundY = gpMap->checkGround(x, y + kShadowGroundProbeLift, z, &plane);

    mario->mFloorTriangle = plane;
    mario->mFloorBelow = groundY;
}

static void drawRemoteMarioShadow(TMario *mario, u16 vfxFlags) {
    if (!shouldDrawRemoteShadow(mario, vfxFlags))
        return;

    TMBindShadowBody *shadowBody = getMarioBindShadowBody(mario);
    if (!shadowBody)
        return;

    syncRemoteShadowGround(mario);
    shadowBody->entryDrawShadow();
}

// doldecomp TWaterGun tail fields (BSE Watergun.hxx lumps these as mGeometry[]).
constexpr u32 kFluddDeployOffset = 0x1CEC;
constexpr u32 kFluddSwitchProgressOffset = 0x1CFC;
constexpr u32 kFluddSwitchSpeedOffset = 0x1D00;
// WaterGun.prm ChangeSpeed; used when replaying retail switch motion on remotes.
constexpr f32 kFluddNozzleChangeSpeed = 0.1f;

// doldecomp J3DModelData / J3DShape field offsets (thinkAloha toggles shape 10 unk8 bit 1).
constexpr u32 kModelDataShapeNumOffset = 0x2Cu;
constexpr u32 kModelDataShapeTableOffset = 0x30u;
constexpr u32 kShapeDrawFlagsOffset = 0x8u;

using TMarioWarpOutEffectFn = void (*)(TMario *, int, f32);
using TMarioWarpInEffectFn = void (*)(TMario *);
using TMarioWarpInLightFn = void (*)(TMario *);

static TMarioWarpOutEffectFn sWarpOutEffect =
    reinterpret_cast<TMarioWarpOutEffectFn>(SMS_PORT_REGION(0x802634E0, 0x8025B26C, 0, 0));
static TMarioWarpInEffectFn sWarpInEffect =
    reinterpret_cast<TMarioWarpInEffectFn>(SMS_PORT_REGION(0x802637A0, 0x8025B52C, 0, 0));
static TMarioWarpInLightFn sWarpInLight =
    reinterpret_cast<TMarioWarpInLightFn>(SMS_PORT_REGION(0x8026376C, 0x8025B4F8, 0, 0));

struct RemoteActorSlot {
    bool spawned;
    bool inViewList;
    bool visible;
    u8 rosterSlot;
    bool hideSeekSeekerLook;
    bool hideSeekSeekerLookWas;
    bool wasYCam;
    bool turnRootLatched;
    bool sideFlipOffsetLatched;
    bool fluddSwitchLatched;
    bool fluddTowardSpray;
    TMario *body;
    s16 yaw;
    s16 turnRootYaw;
    s16 syncHeadLook;
    s16 syncGunAngle;
    f32 syncWaistPitch;
    f32 syncWaistRoll;
    u8 lastWaterTank;
    u8 nozzleId;
    u8 lastNozzleId;
    u8 lastMovementState;
    u8 fluddSecondNozzle;
    u16 vfxFlags;
    BlooperSurfSlot surf;
    u8 lastHealth;
    u16 lastVfxFlags;
    u32 lastState;
    u16 lastAnimId;
    bool wasAirborne;
    bool syncPumpUpper;
    f32 syncUpperFrame;
    f32 syncAnimFrame;
    f32 syncAnimRate;
    bool spinYawLatched;
    s16 spinYaw;
    bool pendingWarpInVfx;
    bool pendingWarpOutVfx;
    u8 pendingWarpOutKind;
    u16 appearRevealFrames;
    u16 lastSoundVfx;
    u8 fluddSprayTick;
    u8 swimVfxTick;
    bool wasInWater;
    // Phase A movement SE bookkeeping (edge + rate-limit; never setPlayerInfo).
    u8 moveSeTick;
    u8 footstepPhase; // L/R heel/tip cycle for generic stone walk SE
    bool wasSurfRide;
    bool wasSurfJump;
    u8 syncedSprayPressure;
    u8 invalidSnapshotStreak;
    f32 remoteSprayPressure;
    bool inWarpTransition;
    bool renderVisible;
    bool visualStateDirty;
    bool visualUpdateThisFrame;
    bool cosmeticUpdateThisFrame;
    bool drawShadowThisFrame;
    u8 visualUpdateInterval;
    u8 offscreenFrames;
    u32 lastVisualWorkFrame;
    Mtx cachedPoseRoot;
    bool cachedPoseRootValid;
    u8 pendingContactVfx;
    Vec pendingContactPos;
    Vec3 targetPos;
    Vec3 targetVel;
    Vec3 displayPos;
    Vec3 displayVel;
    f32 targetRotY;
    f32 displayRotY;
    bool displayMotionInit;
    // Dirty tracking for cheap per-frame path (skip Yoshi/blooper/cosmetics when quiet).
    u32 lastAppliedState;
    u16 lastAppliedAnimId;
    u16 lastAppliedVfx;
    u8 lastAppliedNozzle;
    u8 lastAppliedHealth;
    u8 lastAppliedWater;
    u8 lastAppliedMovement;
    RemoteYoshiSlot yoshi;
};

static RemoteActorSlot gActors[MAX_REMOTE_SLOTS];
static u32 gRemoteVisualFrame = 0;
static u32 gRemotePerformBodyCount = 0;

// Pre-spawned remote puppet pool. Adapted from the SMSO 2 design principle in
// createMarios.c (makeMarios), but bounded to a small spare baseline rather than
// allocating all nine before the roster is known. Spawns are staggered and
// prefer an already-cached custom pack so first-residency need not rebuild.
// Indexed by network slot (0..MAX_REMOTE_SLOTS-1); localSlot entry stays nullptr.
static TMario *gBodyPool[MAX_REMOTE_SLOTS] = {};
// Model id that was mounted when each pool body was initValues()'d (empty = retail).
static char gBodyPoolModelIds[MAX_REMOTE_SLOTS][8] = {};
// True when the pool body was constructed under a bound custom pack (not soft-fail retail).
static bool gBodyPoolIsCustom[MAX_REMOTE_SLOTS] = {};
// Capless packs (Yoshi/Birdo): retail hat BMDs stay for initValues; hide meshes.
static bool gBodyPoolHideCaps[MAX_REMOTE_SLOTS] = {};
// Superseded / staged bodies are retained for fast A↔B reuse until stage
// recycle. Mid-stage never teardownRemoteBodyGraph / freeAll / destroy arenas —
// research proved SMS engine subsystems UAF when TMario arenas are recycled
// before a stage boundary even after detach + TexAnim + ~TMario.
// Main-heap prewarm graphs (arena == nullptr) park in gMainHeapParkedSpares.
struct RemoteBodyVariant {
    TMario *body;
    JKRExpHeap *arena;
    char modelId[MARIO_MODEL_ID_SIZE];
    bool isCustom;
    bool hideCaps;
    u8 readyDelay;
    u8 ownerSlot;
    u32 generation;
    // Stamp-only: mid-stage freeAll is disabled. Stage recycle ignores this.
    u32 reclaimAfterTick;
};
static RemoteBodyVariant gBodyVariants[MAX_REMOTE_SLOTS] = {};
static RemoteBodyVariant gReadyCustomBodies[kReadyCustomBodyCapacity] = {};
// Child arenas for live gBodyPool graphs that were built as staged
// replacements (ready-commit). nullptr means a main-heap prewarm/legacy body.
static JKRExpHeap *gBodyPoolArenas[MAX_REMOTE_SLOTS] = {};
// Preferred staging ExpHeaps. Occupied for the rest of the stage once a graph
// is born into them; additional identities create new child ExpHeaps.
static JKRExpHeap *gBodyPingPongArenas[kBodyPingPongArenaCount] = {};
// Non-destroyed main-heap TMario graphs demoted off the live/ready/variant
// tables. Still module-owned (perform must no-op). Freed only at stage recycle.
static TMario *gMainHeapParkedSpares[kMainHeapParkedSpareCapacity] = {};
static u32 gReadyCustomPrewarmCursor = 0;
static bool gRemoteHeapRecycleOnStageExit = false;
// Monotonic preload tick (diagnostics / demotion stamps only).
static u32 gBodyReclaimTick = 1;
// Heavy-work budget shared by loading prewarm and live first-residency upgrades.
// These flags accumulate until updateRemoteActors consumes a normal gameplay
// update, preventing preload + live assignment from both constructing bodies in
// one video frame. Archive I/O and TMario::initValues are never combined.
static bool gArchiveLoadAttemptedSinceActorUpdate = false;
static bool gBodyConstructedSinceActorUpdate = false;
static bool gFirstVisibleBodyPendingThisUpdate = false;
static bool gBodyConstructionWindowOpen = false;
static u8 gHeavyPreparationCooldown = 0;
static u16 gPreparationIdleWaitFrames = 0;
constexpr bool bodyConstructionBudgetAvailable(bool bodyConstructed) {
    return !bodyConstructed;
}
static_assert(bodyConstructionBudgetAvailable(false));
static_assert(!bodyConstructionBudgetAvailable(true));

static bool canConstructRemoteBodyThisUpdate() {
    return gBodyConstructionWindowOpen &&
           bodyConstructionBudgetAvailable(gBodyConstructedSinceActorUpdate);
}
// Once true for a slot, mid-stage CommBuffer id changes must not rebuild the body
// until requestRemoteMarioModelReapply (or dismiss / next stage). Cleared when the
// body is dismissed back to the pool so a later first-residency can re-apply.
static bool gBodyModelApplied[MAX_REMOTE_SLOTS] = {};
// Monotonic per-slot request generations cancel stale preparation/activation.
// Desired ids are mirrored here so a mailbox write is noticed even if the
// higher-level model-system update and actor update straddle that write.
static u32 gBodyModelRequestGeneration[MAX_REMOTE_SLOTS] = {};
static char gBodyRequestedModelIds[MAX_REMOTE_SLOTS][MARIO_MODEL_ID_SIZE] = {};
static u32 gBodyPreparingGeneration[MAX_REMOTE_SLOTS] = {};
static char gBodyPreparingModelIds[MAX_REMOTE_SLOTS][MARIO_MODEL_ID_SIZE] = {};
static u32 gBodyReadyGeneration[MAX_REMOTE_SLOTS] = {};
static char gBodyReadyModelIds[MAX_REMOTE_SLOTS][MARIO_MODEL_ID_SIZE] = {};
static u32 gBodyAppliedGeneration[MAX_REMOTE_SLOTS] = {};
// Frames spent assigned with an empty (retail) id before freezing. Lets a late
// CommBuffer model-id write win over retail-prewarm without allowing mid-stage
// hot-swaps after the grace window (reapply still unlocks on a later non-empty id).
static u8 gBodyRetailGraceFrames[MAX_REMOTE_SLOTS] = {};
constexpr u8 kRemoteModelRetailGraceFrames = 180; // 3s @ 60 Hz (roster/pack install race)
// Cooldown between soft-fail retries (pack missing / heap low / spawn fail) so we
// do not SMSLoadArchive every frame while still recovering when the pack appears.
static u8 gBodyModelRetryCooldown[MAX_REMOTE_SLOTS] = {};
constexpr u8 kRemoteModelRetryCooldownFrames = 45; // 0.75s @ 60 Hz
constexpr u8 kRemoteModelPendingRetryFrames = 1; // cache readiness polling only
static u32 gBodyPoolCount = 0;
// Staggered prewarm: walk slots one spawn per frame until the pool is full.
static u32 gBodyPoolPrewarmIndex = 0;
static bool gBodyPoolPrewarmComplete = false;
static u32 gVisibilityDiagFrame = 0;
static bool gRemoteHeapReserved = false;
static JDrama::TViewObjPtrListT<JDrama::TViewObj> *gPlayerGroup = nullptr;
static JDrama::TViewObjPtrListT<JDrama::TViewObj> *gRemotePerformGroup = nullptr;
static bool gRemotePerformGroupRegistered = false;
static bool gReportedMissingPlayerGroup = false;
static bool gReportedBodyCap = false;
static JKRHeap *gRemoteActorHeap = nullptr;
static JKRHeap *gRemoteActorPackHeap = nullptr;
static bool gRemoteActorHeapOwned = false;
static u32 gRemoteActorHeapCapacity = 0;
static u32 gRemoteActorPackHeapCapacity = 0;
static bool gReportedHeapShortage = false;
static bool gExpandedHeapFailed = false;
// Set once an extended-MEM1 data BAT is installed and verified. Persists for the
// module's lifetime (the CPU mapping survives stage teardown); only a full console
// reset clears it, after which we re-install on the next session.
static bool gExtendedMappingReady = false;
static u32 gExtendedMappedEnd = 0; // exclusive end of the verified mapped window
static RemoteActorSlot *gRemoteWaistSlot = nullptr;

static u32 gRemotePerformDrawDiag = 0;
static u32 gModelBuildCount = 0;
static u32 gModelBuildDeferredCount = 0;
static u32 gModelPointerCommitCount = 0;
static u32 gModelBuildMilliseconds = 0;
static u32 gModelDiagnosticsFrame = 0;

static bool gReportedPerformGroupAllocFail = false;

static JDrama::TViewObjPtrListT<JDrama::TViewObj> *ensureRemotePerformGroup() {
    if (gRemotePerformGroup)
        return gRemotePerformGroup;

    // Prefer the root heap (persists across stage teardown), but fall back to the
    // remote actor heap and the current heap so a null/exhausted root heap on a
    // given boot path does not silently disable every remote body for the stage.
    const size_t size = sizeof(JDrama::TViewObjPtrListT<JDrama::TViewObj>);
    JKRHeap *candidates[3] = {
        JKRHeap::sRootHeap,
        gRemoteActorHeap,
        JKRHeap::sCurrentHeap,
    };

    void *mem = nullptr;
    for (JKRHeap *heap : candidates) {
        if (!heap)
            continue;
        mem = heap->alloc(size, 0x20);
        if (mem)
            break;
    }

    if (!mem) {
        if (!gReportedPerformGroupAllocFail) {
            gReportedPerformGroupAllocFail = true;
            OSReport("[SMSO] Remote perform group alloc FAILED (root=%p remote=%p current=%p)\n",
                     JKRHeap::sRootHeap, gRemoteActorHeap, JKRHeap::sCurrentHeap);
        }
        return nullptr;
    }

    gRemotePerformGroup =
        new (mem) JDrama::TViewObjPtrListT<JDrama::TViewObj>("SMSO_RemotePuppets");
    gReportedPerformGroupAllocFail = false;
    return gRemotePerformGroup;
}

static void clearRemotePerformGroupMembers() {
    gRemotePerformBodyCount = 0;
    if (!gRemotePerformGroup)
        return;

    for (auto it = gRemotePerformGroup->mViewObjList.begin();
         it != gRemotePerformGroup->mViewObjList.end();) {
        gRemotePerformGroup->mViewObjList.erase(it++);
    }
}

static bool remotePerformGroupHasActiveBodies() {
    return gRemotePerformGroup && gRemotePerformBodyCount != 0;
}

// Retail only walks perform lists built during preEntry for the Player Group.
// Remotes live on gRemotePerformGroup (not Player Group), so piggyback local
// Mario's perform timing — same flags/graphics the engine already uses each frame.
static void mirrorRemotePerformGroup(u32 flags, JDrama::TGraphics *graphics) {
    if (!graphics || !gRemotePerformGroup || !remotePerformGroupHasActiveBodies())
        return;

    const u32 mirrorFlags = flags & (0x204u | 0x4u | 0x200u | 0x205u);
    if (mirrorFlags == 0)
        return;

    // Reset juice draw tint only on the calc bit (0x1). The old `mirrorFlags & 0x205`
    // also matched draw (0x200) / viewCalc (0x4) and cleared the tint before
    // ModelWaterManager drew droplets (doldecomp uses global unk5D5F / mWaterCardType).
    if ((flags & 0x1u) != 0)
        smso::resetRemoteYoshiJuiceDrawTint();

    gRemotePerformGroup->perform(mirrorFlags, graphics);
}

static void registerRemotePerformGroup(TMarDirector *director) {
    if (!director || !gPlayerGroup)
        return;

    // Recover from a stale "registered" flag left over from a previous stage whose
    // perform group was never (re)created. Without this, a null pointer paired with
    // a true flag would keep updateRemoteActors bailing out for the whole stage.
    if (gRemotePerformGroupRegistered && gRemotePerformGroup)
        return;

    if (gRemotePerformGroupRegistered && !gRemotePerformGroup) {
        OSReport("[SMSO] Remote perform group flag set but pointer null, recreating\n");
        gRemotePerformGroupRegistered = false;
    }

    if (!ensureRemotePerformGroup()) {
        // ensureRemotePerformGroup already emits a one-shot alloc-fail report.
        return;
    }

    gRemotePerformGroupRegistered = true;
    OSReport("[SMSO] Remote perform group ready (mirror dispatch via local Mario perform)\n");
}

using J3DNodeCallback = int (*)(J3DNode *, int);
static J3DNodeCallback MarioHeadCtrlFn = reinterpret_cast<J3DNodeCallback>(
    SMS_PORT_REGION(0x80248B18, 0x802408a4, 0, 0));

static TMario *&gpMarioForCallBackRef() {
    return *reinterpret_cast<TMario **>(SMS_PORT_REGION(0x8040E0E0, 0x804057a8, 0, 0));
}

static Mtx &j3dCurrentMtx() {
    return *reinterpret_cast<Mtx *>(SMS_PORT_REGION(0x80404788, 0x803fbf28, 0, 0));
}

static bool isRunningAnim(const TMario *mario) {
    if (!mario)
        return false;
    if ((mario->mState & 0x1C0) != 0x040)
        return false;
    const u16 anim = mario->mAnimationID;
    return anim == kAnimRun1 || anim == kAnimRun2;
}

static bool snapshotPacksHeadLook(u16 vfxFlags) {
    return (vfxFlags & VFX_Y_CAM) != 0;
}

// During active spray (water or dry-pump), the vfx aux bits carry the host's FLUDD gun angle
// (mGunAngle) instead of waist roll. Retail MarioHeadCtrl/MarioWaistCtrl read getGunAngle()
// to aim the head and chest; mGunAngle is negative while hovering (looks down) and positive
// while aiming up. Y-cam already packs the L-button pitch as the gun angle elsewhere, so this
// only covers non-Y-cam spray. Run+spray intentionally prefers gun angle over waist roll here
// because the retail waist callback's FLUDD branch overrides the run branch while spraying.
static bool snapshotPacksGunAngle(u16 vfxFlags) {
    if (vfxFlags & VFX_Y_CAM)
        return false;
    return (vfxFlags & (VFX_WATER_SPRAY | VFX_FLUDD_EMPTY)) != 0;
}

static bool snapshotPacksWaistPitch(u16 vfxFlags, u16 animId) {
    if (vfxFlags & VFX_Y_CAM)
        return false;
    return animId == kAnimRun1 || animId == kAnimRun2 || animId == smso::kBlooperSurfRideShellAnim;
}

static bool snapshotPacksWaistPitchForState(u16 vfxFlags, u16 animId, u32 state) {
    if (snapshotPacksWaistPitch(vfxFlags, animId))
        return true;
    return smso::isBlooperSurfState(state);
}

static bool pointerInHeap(const void *ptr, const JKRHeap *heap) {
    if (!ptr || !heap || !heap->mStart || !heap->mEnd)
        return false;
    const u32 p = reinterpret_cast<u32>(ptr);
    return p >= reinterpret_cast<u32>(heap->mStart) && p < reinterpret_cast<u32>(heap->mEnd);
}

// ANY module-owned puppet: live pool, demoted variants, ready cache, active
// actor binding, or resident of a tracked child / ping-pong arena. Demoted
// graphs must never fall through to retail TMario::perform (BetterSMS player
// data / gamepad paths) — that is the intermittent mid-stage swap crash.
static bool isRemoteBody(const TMario *mario) {
    if (!mario)
        return false;
    // Pool is indexed by network slot (local slot left null). Scan every slot
    // index — do NOT use gBodyPoolCount as a contiguous length, or high-index
    // puppets (e.g. slot 9 when local is not 9) are misclassified as local.
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (gBodyPool[i] == mario)
            return true;
        if (gBodyVariants[i].body == mario)
            return true;
        if (gActors[i].body == mario)
            return true;
        if (gBodyPoolArenas[i] && pointerInHeap(mario, gBodyPoolArenas[i]))
            return true;
        if (gBodyVariants[i].arena && pointerInHeap(mario, gBodyVariants[i].arena))
            return true;
    }
    for (const auto &ready : gReadyCustomBodies) {
        if (ready.body == mario)
            return true;
        if (ready.arena && pointerInHeap(mario, ready.arena))
            return true;
    }
    for (u32 i = 0; i < kMainHeapParkedSpareCapacity; ++i) {
        if (gMainHeapParkedSpares[i] == mario)
            return true;
    }
    for (u32 i = 0; i < kBodyPingPongArenaCount; ++i) {
        if (gBodyPingPongArenas[i] && pointerInHeap(mario, gBodyPingPongArenas[i]))
            return true;
    }
    return false;
}

static f32 vecSquareDistance(const TVec3f &a, const TVec3f &b) {
    const f32 dx = a.x - b.x;
    const f32 dy = a.y - b.y;
    const f32 dz = a.z - b.z;
    return dx * dx + dy * dy + dz * dz;
}

// Enemies and hazards resolve against gpMarioAddress even when a remote puppet
// in the player group is the one actually in range. Block the bleed when a
// visible remote body is materially closer to the damage source.
static bool isRemoteProxiedLocalDamage(TMario *mario, THitActor *hit) {
    if (!mario || !hit || mario != gpMarioAddress)
        return false;

    const TVec3f &localPos = mario->mTranslation;
    const TVec3f &hitPos = hit->mTranslation;
    const f32 localDistSq = vecSquareDistance(localPos, hitPos);

    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        const RemoteActorSlot &slot = gActors[i];
        if (!slot.spawned || !slot.visible || !slot.body)
            continue;

        const f32 remoteDistSq = vecSquareDistance(slot.body->mTranslation, hitPos);
        constexpr f32 kMarginSq = 100.0f * 100.0f;
        if (remoteDistSq + kMarginSq < localDistSq)
            return true;
    }
    return false;
}

static void configureRemoteMarioCollision(TMario *body, u8 rosterSlot) {
    if (!body)
        return;

    auto *hitBody = reinterpret_cast<THitActor *>(body);

    // Never mirror attack/receive radii onto puppets — that breaks stage load and routes
    // map-object hits to the wrong body.
    (void)rosterSlot;
    body->mReceiveRadius = 0.0f;
    body->mReceiveHeight = 0.0f;
    hitBody->mEntryRadius = 0.0f;
    body->mAttackRadius = 0.0f;
    body->mAttackHeight = 0.0f;

    // Preserve synced fruit carry — cleared again when the remote drops/throws.
    body->mHeldObject = smso::getRemoteCarriedFruitActor(rosterSlot);
    body->mHolder = nullptr;

    if (!smso::isRemoteShineCollectActive(rosterSlot))
        body->mGrabTarget = nullptr;
}

static RemoteActorSlot *findRemoteSlot(TMario *mario) {
    if (!mario)
        return nullptr;
    for (auto &slot : gActors) {
        if (slot.spawned && slot.body == mario)
            return &slot;
    }
    return nullptr;
}

static bool isSpinJumpState(u32 state) {
    const u32 id = state & 0x1FFu;
    return id == 0x041u || id == 0x042u || id == 0x095u || id == 0x096u;
}

static bool isSpinJumpPlayback(u16 animId, u32 state) {
    if (animId == kAnimSpinJump)
        return true;
    return isSpinJumpState(state);
}

static bool isSpinJumpPositiveYaw(u32 state) {
    const u32 id = state & 0x1FFu;
    // doldecomp: ROTATE_L and RIGHT_ROTATE_JUMP add +timer*4096; R/LEFT subtract.
    return id == 0x041u || id == 0x096u;
}

static void applySpinJumpFacing(TMario *body, s16 modelYaw, RemoteActorSlot *slot) {
    if (slot) {
        if (!slot->spinYawLatched) {
            slot->spinYaw = modelYaw;
            slot->spinYawLatched = true;
        }
        modelYaw = slot->spinYaw;
    }

    body->mModelAngleY = modelYaw;
    body->_9E = modelYaw;
    body->mAngle.x = 0;
    body->mAngle.y = modelYaw;
    body->mAngle.z = 0;
    body->mRotation.x = 0.0f;
    body->mRotation.y = convertAngleS16ToFloat(modelYaw);
    body->mRotation.z = 0.0f;
}

static void advanceRemoteSpinYaw(TMario *body, RemoteActorSlot *slot, u32 state) {
    if (!body || !slot)
        return;

    if (!isSpinJumpPlayback(body->mAnimationID, state)) {
        slot->spinYawLatched = false;
        return;
    }

    if (!slot->spinYawLatched) {
        slot->spinYaw = body->mModelAngleY;
        slot->spinYawLatched = true;
    }

    const s16 step = isSpinJumpPositiveYaw(state) ? kSpinYawStepPerFrame : -kSpinYawStepPerFrame;
    slot->spinYaw = static_cast<s16>(slot->spinYaw + step);

    body->mModelAngleY = slot->spinYaw;
    body->_9E = slot->spinYaw;
    body->mAngle.y = slot->spinYaw;
    body->mRotation.y = convertAngleS16ToFloat(slot->spinYaw);
}

// ANIM_TURN / ANIM_TRNED: keep root mModelAngleY at pre-turn yaw while the BCK rotates.
// turnEnd() (STATE_TURNING 0x444 only) adds 0x8000 to mModelFaceAngle; turnning() does not.
static void applyRemoteFacing(TMario *body, s16 modelYaw, u16 animId, u32 state,
                              RemoteActorSlot *slot) {
    const s16 prevFaceY = body->mAngle.y;
    const bool inTurnAnim = animId == kAnimTurn || animId == kAnimTurnEnd;

    if (isSpinJumpPlayback(animId, state)) {
        applySpinJumpFacing(body, modelYaw, slot);
        body->_9C = prevFaceY;
        return;
    }
    if (slot)
        slot->spinYawLatched = false;

    bool sideFlipOffset = usesSideFlipModelOffset(state, animId);
    if (slot) {
        if (sideFlipOffset)
            slot->sideFlipOffsetLatched = true;
        else if (slot->sideFlipOffsetLatched && sideFlipSequenceEnded(state, animId))
            slot->sideFlipOffsetLatched = false;
        if (slot->sideFlipOffsetLatched)
            sideFlipOffset = true;
    }

    if (inTurnAnim) {
        if (slot && !slot->turnRootLatched && animId == kAnimTurn)
            slot->turnRootYaw = modelYaw;
        if (slot && animId == kAnimTurn)
            slot->turnRootLatched = true;
        else if (slot && slot->turnRootLatched && animId != kAnimTurnEnd)
            slot->turnRootLatched = false;

        if (slot && slot->turnRootLatched)
            modelYaw = slot->turnRootYaw;
        // doldecomp turnEnd() only; turnning()'s in-state TRNED does not add 0x8000.
        if (animId == kAnimTurnEnd && state == TMario::STATE_TURNING)
            modelYaw = static_cast<s16>(modelYaw + kModelFaceHalfTurn);
    } else if (slot && slot->turnRootLatched) {
        slot->turnRootLatched = false;
    }

    body->mModelAngleY = modelYaw;
    body->_9E = modelYaw;
    body->mRotation.y = convertAngleS16ToFloat(modelYaw);

    if (inTurnAnim) {
        // TURN BCK rotates relative to pre-turn root; TRNED carries intended face yaw.
        // Early-return used to leave stale mAngle.y (~180° off) from before the turn.
        body->mAngle.x = 0;
        if (animId == kAnimTurn) {
            body->mAngle.y = modelYaw;
        } else {
            const s16 faceYaw = slot ? slot->yaw : modelYaw;
            body->mAngle.y =
                sideFlipOffset ? static_cast<s16>(faceYaw + kModelFaceHalfTurn) : faceYaw;
        }
        body->mAngle.z = 0;
        body->mRotation.x = 0.0f;
        body->mRotation.z = 0.0f;
        body->_9C = prevFaceY;
        return;
    }

    body->mAngle.x = 0;
    body->mAngle.y =
        sideFlipOffset ? static_cast<s16>(modelYaw + kModelFaceHalfTurn) : modelYaw;
    body->mAngle.z = 0;
    body->mRotation.x = 0.0f;
    body->mRotation.z = 0.0f;
    // considerWaist() uses mAngle.y - _9C for run roll; keep one frame of lag.
    body->_9C = prevFaceY;
}

static f32 shortestAnimFrameDrift(f32 from, f32 to) {
    f32 drift = to - from;
    if (drift < -kRemoteAnimLoopSnapFrames)
        drift += kRemoteAnimLoopSpanFrames;
    else if (drift > kRemoteAnimLoopSnapFrames)
        drift -= kRemoteAnimLoopSpanFrames;
    return drift;
}

static bool isDiveSwimAnim(u16 animId) {
    return animId == kAnimDiveWait || animId == kAnimDiveLand;
}

static bool isDrySlideState(u32 state, u16 vfxFlags);

// doldecomp jumpCatch() / catching(): belly flop and belly slide share ANIM_SLDCT; catching()
// clamps frame on the grounded slide so Mario leaves the stretched dive pose.
static void applyRemoteSlideAnimOverrides(TMario *body, RemoteActorSlot *slot, u32 state) {
    if (!body->mModelData || !body->mModelData->mFrameCtrl)
        return;

    const u16 vfxFlags = slot ? slot->vfxFlags : static_cast<u16>(0);
    if (isDrySlideState(state, vfxFlags) && isDiveSwimAnim(body->mAnimationID) &&
        body->mAnimationID != kAnimSlideCatch) {
        body->setAnimation(kAnimSlip, 1.0f);
        return;
    }

    const u32 statusId = state & kStatusTypeAndIdMask;
    const bool bellyFlopAir = statusId == kStatusJumpCatch;
    const bool bellySlide = statusId == kStatusIdCatchSlide;
    if (!bellyFlopAir && !bellySlide)
        return;

    if (body->mAnimationID != kAnimSlideCatch || isDiveSwimAnim(body->mAnimationID))
        body->setAnimation(kAnimSlideCatch, 1.0f);

    J3DFrameCtrl &bodyCtrl = body->mModelData->mFrameCtrl[0];
    if (bellySlide && bodyCtrl.mCurFrame > kCatchSlideMaxFrame)
        bodyCtrl.mCurFrame = kCatchSlideMaxFrame;

    if (bellySlide && slot && slot->lastState != kInvalidTrackState) {
        const u32 prevId = slot->lastState & kStatusTypeAndIdMask;
        if (prevId == kStatusJumpCatch && bodyCtrl.mCurFrame > kCatchSlideMaxFrame)
            bodyCtrl.mCurFrame = kCatchSlideMaxFrame;
    }
}

static void syncRemoteAnimation(TMario *body, RemoteActorSlot *slot, const PlayerSnapshot &snap,
                                u8 netUpperState) {
    const f32 snapFrame = static_cast<f32>(snap.animFrame) / 256.0f;
    const u8 rateEnc = static_cast<u8>(snap.pingMs & 0xFF);
    const u8 upperEnc = static_cast<u8>(snap.pingMs >> 8);
    const bool hostYoshiTongue =
        snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags) &&
        yoshiTongueIsActive(unpackYoshiTongueState(snap.health));
    const f32 rate = hostYoshiTongue ? 1.0f
                                   : (rateEnc != 0 ? static_cast<f32>(rateEnc) / 64.0f : 1.0f);
    const f32 upperFrame = static_cast<f32>(upperEnc) / 8.0f;
    const bool yCam = (snap.vfxFlags & VFX_Y_CAM) != 0;
    const bool pumpUpper = netUpperState <= kUpperStateHoldingPump;
    const bool packedAux = snapshotPacksHeadLook(snap.vfxFlags) ||
                           snapshotPacksWaistPitchForState(snap.vfxFlags, snap.animId, body->mState);
    const bool spinJump = isSpinJumpPlayback(snap.animId, body->mState);
    const bool animChanged =
        slot && (slot->lastAnimId != snap.animId || snap.animId != body->mAnimationID);

    u16 animId = snap.animId;
    if (snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags) && !remoteBodyRidingYoshi(body))
        animId = TMario::ANIMATION_IDLE;
    else if (smso::snapshotIsBlooperSurfing(snap) &&
             smso::isBlooperSurfRideState(smso::snapshotMarioState(snap)) &&
             animId != smso::kBlooperSurfRideShellAnim)
        animId = smso::kBlooperSurfRideShellAnim;

    if (animChanged || animId != body->mAnimationID)
        body->setAnimation(animId, rate);

    if (!body->mModelData || !body->mModelData->mFrameCtrl)
        return;

    J3DFrameCtrl &bodyCtrl = body->mModelData->mFrameCtrl[0];
    bodyCtrl.mFrameRate = rate;
    body->mModelData->mFrameCtrl[2].mFrameRate = rate;

    if (slot) {
        const f32 drift = shortestAnimFrameDrift(bodyCtrl.mCurFrame, snapFrame);
        if (animChanged) {
            slot->syncAnimFrame = snapFrame;
            bodyCtrl.mCurFrame = snapFrame;
        } else if (drift > kRemoteAnimResyncBehindFrames) {
            // Local BCK is behind the authoritative network frame — snap forward.
            bodyCtrl.mCurFrame = snapFrame;
            slot->syncAnimFrame = snapFrame;
        } else if (!spinJump && drift < -kRemoteAnimResyncAheadFrames) {
            // Rate overshoot on non-spin anims only; spin-jump must stay ahead-free.
            bodyCtrl.mCurFrame = snapFrame;
            slot->syncAnimFrame = snapFrame;
        }
        slot->syncAnimRate = rate;
        slot->lastAnimId = snap.animId;
    } else if (animChanged) {
        bodyCtrl.mCurFrame = snapFrame;
    }

    if (slot) {
        slot->syncPumpUpper = pumpUpper;
        slot->syncUpperFrame = 0.0f;
    }

    if ((yCam || pumpUpper) && !smso::isBlooperSurfState(body->mState) &&
        !snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags)) {
        // Y-cam and pump/hold drive the upper (FLUDD pump) BCK authoritatively.
        // While spraying, snap.water carries spray pressure (including under Y-cam /
        // C-up) so remotes keep nozzle->_378 non-zero for droplet emit. Y-cam pitch
        // still rides pingMs high — do not reinterpret pressure or pitch as a frame.
        const bool hostSpraying = (snap.vfxFlags & (VFX_WATER_SPRAY | VFX_FLUDD_EMPTY)) != 0;
        f32 syncedUpper;
        if (!hostSpraying && (yCam || pumpUpper)) {
            syncedUpper = static_cast<f32>(snap.water) / 8.0f;
        } else if (hostSpraying && yCam && slot) {
            syncedUpper = slot->syncUpperFrame;
        } else {
            syncedUpper = upperFrame;
        }
        body->mModelData->mFrameCtrl[1].mCurFrame = syncedUpper;
        body->mModelData->mFrameCtrl[1].mFrameRate = 0.0f;
        if (slot)
            slot->syncUpperFrame = syncedUpper;
    } else if (!spinJump && !packedAux && upperEnc != 0 &&
               !snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags)) {
        // pingMs high byte carries Yoshi BCK frame while the host rides Yoshi.
        body->mModelData->mFrameCtrl[1].mCurFrame = upperFrame;
        body->mModelData->mFrameCtrl[1].mFrameRate = rate;
        if (slot)
            slot->syncUpperFrame = upperFrame;
    }

    applyRemoteSlideAnimOverrides(body, slot, body->mState);
}

static void reapplySyncedUpperFrame(TMario *mario, const RemoteActorSlot *slot) {
    if (!slot || !mario->mModelData || !mario->mModelData->mFrameCtrl)
        return;
    if (!slot->syncPumpUpper && !(slot->vfxFlags & VFX_Y_CAM))
        return;

    mario->mModelData->mFrameCtrl[1].mCurFrame = slot->syncUpperFrame;
    mario->mModelData->mFrameCtrl[1].mFrameRate = 0.0f;
}

static void syncRemoteHeadWaist(TMario *body, RemoteActorSlot &slot, const PlayerSnapshot &snap) {
    const u8 highEnc = static_cast<u8>(snap.pingMs >> 8);

    if (snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags)) {
        slot.syncGunAngle = 0;
        syncRemoteNozzleGunAngle(body, 0);
        return;
    }

    if (snapshotPacksHeadLook(snap.vfxFlags)) {
        slot.syncHeadLook = decodeSnapshotAngle(highEnc);
        body->_100 = slot.syncHeadLook;
        slot.syncGunAngle = 0;
        return;
    }

    if (snapshotPacksGunAngle(snap.vfxFlags)) {
        // FLUDD vertical aim (mGunAngle) packed in vfx aux bits during spray/hover. Applied to
        // the nozzle here (before joint callbacks run) so retail MarioHeadCtrl's gunAngle<0
        // branch tilts the head down while hovering and the chest/spray follow the host's aim.
        slot.syncGunAngle = decodeSnapshotAngle6(unpackVfxAuxAngle(snap.vfxFlags));
        syncRemoteNozzleGunAngle(body, slot.syncGunAngle);
        slot.syncHeadLook = 0;
        return;
    }

    slot.syncGunAngle = 0;
    // Clear any stale FLUDD aim so the head/chest don't keep tilting after spray ends.
    syncRemoteNozzleGunAngle(body, 0);

    if (snapshotPacksWaistPitchForState(snap.vfxFlags, snap.animId, body->mState)) {
        slot.syncWaistPitch = static_cast<f32>(decodeSnapshotAngle(highEnc));
        body->_3DC = slot.syncWaistPitch;

        const u8 rollEnc = unpackVfxAuxAngle(snap.vfxFlags);
        slot.syncWaistRoll = static_cast<f32>(decodeSnapshotAngle6(rollEnc));
        body->_3D8 = slot.syncWaistRoll;
        slot.syncHeadLook = 0;
        return;
    }

    slot.syncHeadLook = 0;
}

static bool isRemoteWarpTransitionState(u32 state) {
    switch (state & 0x1FFu) {
    case 0x336u: // STATE_WARPIN
    case 0x337u: // STATE_WARPOUT
    case 0x33Eu: // STATE_NOMOTION
    case 0x33Fu: // STATE_DISAPPEAR
        return true;
    default:
        return false;
    }
}

static u32 sanitizeRemoteState(u32 state) {
    // Remote puppets have no mHolder; tree/bar climb states crash in calcBaseMtx otherwise.
    state &= ~kHolderDependentStateFlag;

    // Remote warp transitions are local-only; never replay pipe/cannon warp states.
    if (isRemoteWarpTransitionState(state))
        state = (state & ~0x1FFu) | (kStateWait & 0x1FFu);

    // Gameplay damage states still run hazard/enemy side-effects through shared
    // engine paths even though remotes skip playerControl. Keep knockback/death
    // visuals on the synced BCK (animId) instead of the state machine.
    const u32 id = state & 0x1FFu;
    switch (id) {
    case 0x467u: // STATE_DEATH
    case 0x462u: // STATE_KNCK_LND
    case 0x466u: // STATE_KNCK_GND
    case 0x8B0u: // STATE_F_KNCK_H
    case 0x8B7u: // STATE_FIRE_HIT
    case 0x239u: // STATE_FIRE_RVR
    case 0x8B8u: // STATE_THROWN
        state = (state & ~0x1FFu) | (TMario::STATE_IDLE & 0x1FFu);
        state &= ~smso::kBlooperSurfDrawFlag;
        break;
    default:
        break;
    }
    return state;
}

static f32 *getFluddDeploy(TWaterGun *fludd);
static f32 *getFluddSwitchSpeed(TWaterGun *fludd);

static void applyRemoteFluddPresence(TMario *body, bool showFluddOnMarioBack, bool hostOnYoshi) {
    body->mAttributes.mHasFludd = showFluddOnMarioBack;
    body->mAttributes.mIsFluddEmitting = false;
    if (!body->mFludd)
        return;
    body->mFludd->mIsEmitWater = false;
    if (!showFluddOnMarioBack || hostOnYoshi) {
        f32 *deploy = getFluddDeploy(body->mFludd);
        if (deploy)
            *deploy = 0.0f;
    }
    // Never drive puppet FLUDD with the Yoshi nozzle — global water tint / juice HUD (doldecomp
    // TModelWaterManager::unk5D5F) and retail thinkUpper read live pack state.
    if (hostOnYoshi) {
        f32 *speed = getFluddSwitchSpeed(body->mFludd);
        if (speed)
            *speed = 0.0f;
        if (body->mFludd->mCurrentNozzle == TWaterGun::Yoshi)
            body->mFludd->mCurrentNozzle = TWaterGun::Spray;
    }
}

static void applyRemoteRunHandBlend(TMario *mario) {
    // Hand pose is synced via health/animAux; skip local speed-derived override.
    (void)mario;
}

static void syncRemoteNozzleGunAngle(TMario *mario, s16 pitch);

static void resetRemoteYCamPose(TMario *mario) {
    mario->_FC = 0;
    mario->_100 = 0;
    syncRemoteNozzleGunAngle(mario, 0);
    if (mario->mModelData && mario->mModelData->mFrameCtrl) {
        mario->mModelData->mFrameCtrl[1].mCurFrame = 0.0f;
        mario->mModelData->mFrameCtrl[1].mFrameRate = 0.0f;
    }
}

static void beginRemoteYCamPose(TMario *mario) {
    if (mario->mModelData && mario->mModelData->mFrameCtrl) {
        mario->mModelData->mFrameCtrl[1].mCurFrame = 0.0f;
        mario->mModelData->mFrameCtrl[1].mFrameRate = 0.0f;
    }
    if (mario->mFludd)
        mario->mFludd->mIsEmitWater = false;
    mario->mAttributes.mIsFluddEmitting = false;
}

static void ensureCapOnHead(TMario *mario) {
    if (!mario->mCap)
        return;
    u16 *capFlags = reinterpret_cast<u16 *>(reinterpret_cast<u8 *>(mario->mCap) + 0x4);
    *capFlags = static_cast<u16>(*capFlags | kCapModelHat);
}

static s16 resolveSnapshotYaw(const PlayerSnapshot &snap) {
    return static_cast<s16>(snap.rotationY);
}

static f32 remoteMotionDeltaTime() {
    const f32 fps = BetterSMS::getFrameRate();
    return fps > 1.0f ? (1.0f / fps) : (1.0f / 60.0f);
}

static f32 expDecayAlpha(f32 rate, f32 dt) {
    return 1.0f - expf(-rate * dt);
}

static f32 lerpAngleShortest(f32 from, f32 to, f32 t) {
    f32 delta = to - from;
    while (delta > 32768.0f)
        delta -= 65536.0f;
    while (delta < -32768.0f)
        delta += 65536.0f;
    return from + delta * t;
}

static void hardSnapRemoteDisplayMotion(RemoteActorSlot &slot, const Vec3 &pos, const Vec3 &vel,
                                        f32 rotY) {
    slot.targetPos = pos;
    slot.targetVel = vel;
    slot.targetRotY = rotY;
    slot.displayPos = pos;
    slot.displayVel = vel;
    slot.displayRotY = rotY;
    slot.displayMotionInit = true;
}

static void advanceRemoteDisplayMotion(RemoteActorSlot &slot, TMario *body) {
    if (!body)
        return;

    if (!slot.displayMotionInit) {
        hardSnapRemoteDisplayMotion(slot, slot.targetPos, slot.targetVel, slot.targetRotY);
        body->mTranslation.x = slot.displayPos.x;
        body->mTranslation.y = slot.displayPos.y;
        body->mTranslation.z = slot.displayPos.z;
        body->mSpeed.x = slot.displayVel.x;
        body->mSpeed.y = slot.displayVel.y;
        body->mSpeed.z = slot.displayVel.z;
        body->mForwardSpeed =
            sqrtf(slot.displayVel.x * slot.displayVel.x + slot.displayVel.z * slot.displayVel.z);
        return;
    }

    const f32 dt = remoteMotionDeltaTime();
    const f32 posAlpha = expDecayAlpha(kRemotePositionBlendRate, dt);
    const f32 velAlpha = expDecayAlpha(kRemoteVelocityBlendRate, dt);
    const f32 rotAlpha = expDecayAlpha(kRemoteRotationBlendRate, dt);

    slot.displayPos.x += (slot.targetPos.x - slot.displayPos.x) * posAlpha;
    slot.displayPos.y += (slot.targetPos.y - slot.displayPos.y) * posAlpha;
    slot.displayPos.z += (slot.targetPos.z - slot.displayPos.z) * posAlpha;

    slot.displayVel.x += (slot.targetVel.x - slot.displayVel.x) * velAlpha;
    slot.displayVel.y += (slot.targetVel.y - slot.displayVel.y) * velAlpha;
    slot.displayVel.z += (slot.targetVel.z - slot.displayVel.z) * velAlpha;

    slot.displayRotY = lerpAngleShortest(slot.displayRotY, slot.targetRotY, rotAlpha);

    body->mTranslation.x = slot.displayPos.x;
    body->mTranslation.y = slot.displayPos.y;
    body->mTranslation.z = slot.displayPos.z;
    body->mSpeed.x = slot.displayVel.x;
    body->mSpeed.y = slot.displayVel.y;
    body->mSpeed.z = slot.displayVel.z;
    body->mForwardSpeed =
        sqrtf(slot.displayVel.x * slot.displayVel.x + slot.displayVel.z * slot.displayVel.z);
}

static f32 remoteDistanceFromLocalSq(const TMario *body) {
    if (!body || !gpMarioAddress)
        return 0.0f;
    return vecSquareDistance(body->mTranslation, gpMarioAddress->mTranslation);
}

static bool remotePotentiallyOnScreen(const TMario *body) {
    if (!body || !gpCamera)
        return true;

    const Vec world = {body->mTranslation.x, body->mTranslation.y + 100.0f,
                       body->mTranslation.z};
    Vec view{};
    MTXMultVec(gpCamera->mTRSMatrix, &world, &view);
    if (view.z >= -40.0f)
        return false;

    // Deliberately wider than the actual projection. This is a conservative
    // rejection test, not final clipping: camera turns reveal a remote on the
    // very next update without edge popping.
    const f32 depth = -view.z;
    return fabsf(view.x) <= depth * 2.2f + 350.0f &&
           fabsf(view.y) <= depth * 1.7f + 350.0f;
}

static bool remoteHasActiveCosmetics(const RemoteActorSlot &slot) {
    const u16 activeVfx = VFX_WATER_SPRAY | VFX_FLUDD_EMPTY | VFX_HOVER | VFX_ROCKET |
                          VFX_TURBO | VFX_NOZZLE_SWITCHING | VFX_WET_SLIDE;
    return (slot.vfxFlags & activeVfx) != 0 || slot.fluddSwitchLatched ||
           snapshotHostOnYoshi(slot.nozzleId, slot.vfxFlags) ||
           smso::isBlooperSurfState(slot.body ? slot.body->mState : 0);
}

static void updateRemoteVisualSchedule(RemoteActorSlot &slot, u8 networkSlot) {
    TMario *body = slot.body;
    if (!body)
        return;

    const f32 distanceSq = remoteDistanceFromLocalSq(body);
    const bool onScreen = remotePotentiallyOnScreen(body);
    const bool wasRenderVisible = slot.renderVisible;

    if (onScreen) {
        slot.offscreenFrames = 0;
        slot.renderVisible = true;
    } else {
        if (slot.offscreenFrames < 0xFF)
            ++slot.offscreenFrames;
        // Short hysteresis masks one-frame camera/projection disagreement.
        if (slot.offscreenFrames >= kRemoteOffscreenGraceFrames)
            slot.renderVisible = false;
    }

    u8 interval =
        selectRemoteVisualInterval(slot.visualUpdateInterval, onScreen, distanceSq);
    // A just-hidden Yoshi/blooper/hover gets one extra 30 Hz visual tail. This
    // preserves accessory continuity without paying visible-body draw. FLUDD
    // ModelWater droplets are NOT coupled here — they use a dedicated 60 Hz tick.
    if (remoteHasActiveCosmetics(slot) && interval > kRemoteVisibleAnimInterval &&
        slot.offscreenFrames < kRemoteOffscreenGraceFrames)
        interval = kRemoteVisibleAnimInterval;

    slot.visualUpdateInterval = interval;
    const bool staggerDue = ((gRemoteVisualFrame + networkSlot) % interval) == 0;
    slot.visualUpdateThisFrame =
        slot.renderVisible &&
        (staggerDue || slot.visualStateDirty || !wasRenderVisible);
    slot.cosmeticUpdateThisFrame = slot.visualUpdateThisFrame;
    slot.drawShadowThisFrame =
        slot.renderVisible && distanceSq <= kRemoteShadowDistanceSq;
}

static s16 decodeYCamPitch(u16 vfxFlags) {
    return decodeSnapshotAngle6(unpackVfxAuxAngle(vfxFlags));
}

static void applyRemoteCosmetics(TMario *body, u8 rosterSlot) {
    if (isHideSeekActive()) {
        (void)rosterSlot;
        return;
    }

    body->mAttributes.mIsShineShirt = kEnableRemoteShineShirt;
    if (!kEnableRemoteYCamHelmet) {
        body->mAttributes.mGainHelmet = false;
        body->mAttributes.mGainHelmetFlwCamera = false;
    }
}

static J3DShape *getRetailShapePointer(J3DModelData *modelData, u16 index) {
    if (!modelData)
        return nullptr;

    auto *raw = reinterpret_cast<u8 *>(modelData);
    const u16 shapeNum = *reinterpret_cast<const u16 *>(raw + kModelDataShapeNumOffset);
    if (index >= shapeNum)
        return nullptr;

    J3DShape **table = *reinterpret_cast<J3DShape ***>(raw + kModelDataShapeTableOffset);
    return table ? table[index] : nullptr;
}

static void setRetailShapeDrawFlag(J3DShape *shape, u32 flag, bool enabled) {
    if (!shape)
        return;

    u32 &flags = *reinterpret_cast<u32 *>(reinterpret_cast<u8 *>(shape) + kShapeDrawFlagsOffset);
    if (enabled)
        flags |= flag;
    else
        flags &= ~flag;
}

// doldecomp TMario::thinkAloha — remote perform skips it, so the shine shirt mesh stays visible.
static void applyRemoteShirtVisibility(TMario *body) {
    body->mAttributes.mIsShineShirt = false;
    body->mPrevAttributes.mIsShineShirt = false;

    if (!body->mModelData || !body->mModelData->mModel || !body->mModelData->mModel->mModelData)
        return;

    J3DShape *shirtShape =
        getRetailShapePointer(body->mModelData->mModel->mModelData, kShirtShapeIndex);
    if (!shirtShape)
        return;

    setRetailShapeDrawFlag(shirtShape, 0x1, true);
}

static f32 remoteFacingDegrees(const TMario *body) {
    return static_cast<f32>(body->mAngle.y) * (360.0f / 65536.0f);
}

static u8 resolveWarpOutKind(u16 animId) {
    if (animId == kAnimWarpOutAlt)
        return 2;
    return 0;
}

static void setBodyVisible(TMario *body, bool visible);
static u8 networkSlotOf(const RemoteActorSlot *slot);
static void beginRemoteAppearHide(RemoteActorSlot &slot, u16 frames);
static void tickRemoteAppearReveal(RemoteActorSlot *slot);

static void queueRemoteWarpEdges(RemoteActorSlot &slot, u32 prevState, u32 state, u16 animId) {
    (void)slot;
    (void)prevState;
    (void)state;
    (void)animId;
    // Intentionally not synced — remote puppets never replay warp VFX or hide cycles.
}

static void emitPendingRemoteWarpVfx(TMario *body, RemoteActorSlot *slot) {
    if (!body || !slot)
        return;

    const f32 facing = remoteFacingDegrees(body);

    if (slot->pendingWarpOutVfx) {
        slot->pendingWarpOutVfx = false;
        if (sWarpOutEffect)
            sWarpOutEffect(body, slot->pendingWarpOutKind, facing);
    }

    if (slot->pendingWarpInVfx) {
        slot->pendingWarpInVfx = false;
        if (sWarpInLight)
            sWarpInLight(body);
        if (sWarpInEffect)
            sWarpInEffect(body);
        beginRemoteAppearHide(*slot, kWarpInAppearHideFrames);
    }

}

static void bindModelToJoint(J3DModel *model, Mtx *jointMtx) {
    if (!model || !jointMtx)
        return;
    MTXCopy(*jointMtx, model->mBaseMtx);
    model->calc();
}

// Tail of doldecomp TMario::calcAnim: hands + cap after the body model perform.
static void remoteBindHandsAndCap(TMario *mario, JDrama::TGraphics *graphics, bool showSeekerGlasses,
                                  bool hideCaps) {
    J3DModel *body = mario->mModelData ? mario->mModelData->mModel : nullptr;
    if (!body || !body->mJointArray)
        return;

    const u8 handR = mario->mBindBoneIDArray[4];
    const u8 handL = mario->mBindBoneIDArray[5];
    const u8 head = mario->mBindBoneIDArray[10];
    const u8 mhead = mario->mBindBoneIDArray[11];

    Mtx *handRMtx = &body->mJointArray[handR];
    Mtx *handLMtx = &body->mJointArray[handL];
    Mtx *headMtx = &body->mJointArray[head];
    Mtx *mheadMtx = &body->mJointArray[mhead];

    bindModelToJoint(mario->mHandModel2R, handRMtx);
    bindModelToJoint(mario->mHandModel2L, handLMtx);
    bindModelToJoint(mario->mHandModel3R, handRMtx);
    bindModelToJoint(mario->mHandModel3L, handLMtx);
    bindModelToJoint(mario->mHandModel4R, handRMtx);

    if (mario->mCap) {
        ensureCapOnHead(mario);
        // Capless skins: keep TMarioCap (glasses/helm still bind) but never leave
        // retail hat meshes unbound — entryModels → mCap->perform(0x200) would
        // draw them at stale/mtx-effect transforms (floating orphan caps).
        if (hideCaps) {
            smso::squashHiddenCapDrawInstance(mario);
        } else {
            if (mario->mCap->mCap1)
                bindModelToJoint(mario->mCap->mCap1, mheadMtx);
            if (mario->mCap->mCap3)
                bindModelToJoint(mario->mCap->mCap3, mheadMtx);
        }
        if (kEnableRemoteYCamHelmet && mario->mCap->mDiverHelm)
            bindModelToJoint(mario->mCap->mDiverHelm, headMtx);
        if (showSeekerGlasses && mario->mCap->maGlass1)
            bindModelToJoint(mario->mCap->maGlass1, mheadMtx);
        mario->mCap->perform(2, graphics);
        // perform(2) recalcs active hat meshes — re-squash so entryModels is empty.
        if (hideCaps)
            smso::squashHiddenCapDrawInstance(mario);
    }
}

static void syncRemoteNozzleGunAngle(TMario *mario, s16 pitch) {
    TWaterGun *fludd = mario->mFludd;
    if (!fludd)
        return;

    // Apply to every nozzle, not just mCurrentNozzle. Retail MarioHeadCtrl/MarioWaistCtrl
    // read getCurrentNozzle()->getGunAngle(), and a transient mCurrentNozzle mismatch (host
    // switched nozzles this frame, remote hasn't yet) would otherwise read a stale/zero angle
    // on the wrong nozzle and flip the head up. Keeping all nozzles in sync is cheap and only
    // the current one is ever read.
    for (u8 i = 0; i < 6; ++i) {
        TNozzleBase *nozzle = fludd->mNozzleList[i];
        if (nozzle)
            nozzle->mGunAngle = pitch;
    }
}

// Retail MarioWaistCtrl skips Y-cam on remotes (gpMarioOriginal check). This mirrors the
// waist/FLUDD/run paths using network-synced angles instead of local camera state.
static int RemoteWaistCtrl(J3DNode *node, int stage) {
    (void)node;
    if (stage != 0)
        return 1;

    TMario *mario = gpMarioForCallBackRef();
    if (!mario)
        return 1;

    Mtx transform;
    Mtx &cur = j3dCurrentMtx();
    const u16 vfx = gRemoteWaistSlot ? gRemoteWaistSlot->vfxFlags : static_cast<u16>(0);

    if ((vfx & VFX_Y_CAM) && mario->canBendBody()) {
        const s16 pitch = decodeYCamPitch(vfx);
        mario->_FC = static_cast<u16>(pitch);
        if (pitch > 0) {
            MsMtxSetRotRPH__FPA4_ffff(
                transform, convertAngleS16ToFloat(static_cast<s16>(-mario->_100)), 0.0f,
                convertAngleS16ToFloat(pitch));
            MTXConcat(cur, transform, cur);
        }
        return 1;
    }

    if (mario->mAttributes.mHasFludd && !mario->onYoshi()) {
        TWaterGun *gun = mario->mFludd;
        if (gun) {
            TNozzleBase *nozzle = gun->mNozzleList[gun->mCurrentNozzle];
            if (nozzle) {
                const s16 gunAngle = nozzle->getGunAngle();
                if (gunAngle > 0) {
                    MsMtxSetRotRPH__FPA4_ffff(transform, 0.0f, 0.0f,
                                              convertAngleS16ToFloat(gunAngle));
                    MTXConcat(cur, transform, cur);
                    return 1;
                }
            }
        }
    }

    const u16 anim = mario->mAnimationID;
    if ((anim == kAnimRun1 || anim == kAnimRun2 || anim == smso::kBlooperSurfRideShellAnim) &&
        !mario->mAttributes.mIsFluddEmitting) {
        const s16 roll = static_cast<s16>(mario->_3D8);
        const s16 pitch = static_cast<s16>(mario->_3DC);
        MsMtxSetRotRPH__FPA4_ffff(transform, convertAngleS16ToFloat(roll), 0.0f,
                                  convertAngleS16ToFloat(pitch));
        MTXConcat(cur, transform, cur);
        return 1;
    }

    mario->_FC = 0;
    mario->_100 = 0;
    return 1;
}

static void remoteSetJointCallbacks(TMario *mario, RemoteActorSlot *slot) {
    gRemoteWaistSlot = slot;
    gpMarioForCallBackRef() = mario;

    J3DModel *model = mario->mModelData ? mario->mModelData->mModel : nullptr;
    if (!model || !model->mModelData || !model->mModelData->mJoints)
        return;

    const u8 head = mario->mBindBoneIDArray[10];
    const u8 chest = mario->mBindBoneIDArray[1];
    model->mModelData->mJoints[head]->mCallback = MarioHeadCtrlFn;
    model->mModelData->mJoints[chest]->mCallback = RemoteWaistCtrl;
}

static void remoteClearJointCallbacks(TMario *mario) {
    J3DModel *model = mario->mModelData ? mario->mModelData->mModel : nullptr;
    if (model && model->mModelData && model->mModelData->mJoints) {
        const u8 head = mario->mBindBoneIDArray[10];
        const u8 chest = mario->mBindBoneIDArray[1];
        model->mModelData->mJoints[head]->mCallback = nullptr;
        model->mModelData->mJoints[chest]->mCallback = nullptr;
    }
    gpMarioForCallBackRef() = nullptr;
    gRemoteWaistSlot = nullptr;
}

static void applySyncedHeadWaist(TMario *mario, const RemoteActorSlot *slot) {
    if (!slot)
        return;

    if (slot->vfxFlags & VFX_Y_CAM) {
        mario->_100 = slot->syncHeadLook;
        return;
    }

    if (snapshotPacksGunAngle(slot->vfxFlags)) {
        // Re-assert the synced FLUDD gun angle immediately before the joint callbacks run so
        // retail MarioHeadCtrl/MarioWaistCtrl aim the head and chest with the host's pitch.
        syncRemoteNozzleGunAngle(mario, slot->syncGunAngle);
        return;
    }

    if (isRunningAnim(mario) || mario->mAnimationID == smso::kBlooperSurfRideShellAnim ||
        smso::isBlooperSurfState(mario->mState)) {
        mario->_3DC = slot->syncWaistPitch;
        mario->_3D8 = slot->syncWaistRoll;
    }
}

// Mirrors doldecomp TMario::calcAnim: retail MarioHeadCtrl + remote waist callback.
static void remoteCalcAnim(TMario *mario, RemoteActorSlot *slot, JDrama::TGraphics *graphics) {
    if (slot)
        advanceRemoteSpinYaw(mario, slot, mario->mState);

    if (isHideSeekActive())
        applyHideSeekPlayerCosmetics(mario, slot && slot->hideSeekSeekerLook, true);
    else
        applyRemoteShirtVisibility(mario);

    mario->setPositions();

    bool hideCaps = false;
    if (slot) {
        const u32 idx = static_cast<u32>(slot - gActors);
        if (idx < MAX_REMOTE_SLOTS)
            hideCaps = gBodyPoolHideCaps[idx];
    }

    // Capless packs: never enable TMultiMtxEffect — lag ghosts float in the stage
    // when hat meshes are not bound to the head.
    if (mario->mCap) {
        if (hideCaps)
            mario->mCap->mtxEffectHide();
        else
            mario->mCap->mtxEffectShow();
    }

    mario->addUpper();
    mario->considerWaist();
    applySyncedHeadWaist(mario, slot);

    Mtx baseMtx;
    mario->calcBaseMtx(baseMtx);

    if (mario->mModelData && mario->mModelData->mModel)
        MTXCopy(baseMtx, mario->mModelData->mModel->mBaseMtx);

    remoteSetJointCallbacks(mario, slot);
    if (mario->mModelData)
        mario->mModelData->perform(2, graphics);
    applyRemoteSlideAnimOverrides(mario, slot, mario->mState);
    remoteClearJointCallbacks(mario);
    reapplySyncedUpperFrame(mario, slot);
    if (!isHideSeekActive())
        applyRemoteShirtVisibility(mario);

    applyRemoteRunHandBlend(mario);
    remoteBindHandsAndCap(mario, graphics, slot && slot->hideSeekSeekerLook, hideCaps);

    if (slot && (slot->vfxFlags & VFX_DEAD) == 0)
        smso::updateRemoteBlooperSurfFrame(mario, &slot->surf, graphics);

    calcRemoteYoshiAnim(mario, slot ? &slot->yoshi : nullptr);
}

// On budgeted frames, keep world/root motion smooth while reusing the previous
// skeletal pose. The expensive J3D animation graph and accessory model calc
// resume on the slot's staggered visual tick.
static void captureRemotePoseRoot(TMario *mario, RemoteActorSlot *slot) {
    if (!mario || !slot || !mario->mModelData || !mario->mModelData->mModel)
        return;
    J3DModel *model = mario->mModelData->mModel;
    MTXCopy(model->mBaseMtx, slot->cachedPoseRoot);
    slot->cachedPoseRootValid = true;
}

static void transformCachedModelBase(J3DModel *model, const Mtx delta) {
    if (!model)
        return;
    Mtx moved;
    MTXConcat(delta, model->mBaseMtx, moved);
    MTXCopy(moved, model->mBaseMtx);
}

// J3D joint matrices are already concatenated world matrices. Merely replacing
// J3DModel::mBaseMtx leaves those matrices at the prior sampled root, which made
// a 15/30 Hz pose schedule also quantize the entire actor's movement. Re-root
// the cached pose by newRoot * inverse(oldRoot): O(joints), no BCK evaluation,
// material animation, particles, or accessory calc. This keeps translation and
// rotation at render rate while preserving the sampled local skeletal pose.
static void updateRemoteRootTransform(TMario *mario, RemoteActorSlot *slot) {
    if (!mario || !slot)
        return;

    Mtx newRoot;
    mario->calcBaseMtx(newRoot);

    J3DModel *model =
        mario->mModelData ? mario->mModelData->mModel : nullptr;
    if (!model) {
        slot->cachedPoseRootValid = false;
        return;
    }

    if (!slot->cachedPoseRootValid || !model->mJointArray || !model->mModelData ||
        model->mModelData->mJointNum == 0) {
        MTXCopy(newRoot, model->mBaseMtx);
        MTXCopy(newRoot, slot->cachedPoseRoot);
        slot->cachedPoseRootValid = true;
        return;
    }

    Mtx inverseOld;
    if (MTXInverse(slot->cachedPoseRoot, inverseOld) == 0) {
        MTXCopy(newRoot, model->mBaseMtx);
        MTXCopy(newRoot, slot->cachedPoseRoot);
        return;
    }

    Mtx delta;
    MTXConcat(newRoot, inverseOld, delta);
    for (u16 i = 0; i < model->mModelData->mJointNum; ++i) {
        Mtx moved;
        MTXConcat(delta, model->mJointArray[i], moved);
        MTXCopy(moved, model->mJointArray[i]);
    }
    MTXCopy(newRoot, model->mBaseMtx);

    // These models are bound to Mario joints on sampled-pose frames. Carry
    // their cached world roots with Mario on the intervening frame.
    transformCachedModelBase(mario->mHandModel2R, delta);
    transformCachedModelBase(mario->mHandModel2L, delta);
    transformCachedModelBase(mario->mHandModel3R, delta);
    transformCachedModelBase(mario->mHandModel3L, delta);
    transformCachedModelBase(mario->mHandModel4R, delta);
    if (mario->mCap) {
        transformCachedModelBase(mario->mCap->mCap1, delta);
        transformCachedModelBase(mario->mCap->mCap3, delta);
        transformCachedModelBase(mario->mCap->mDiverHelm, delta);
        transformCachedModelBase(mario->mCap->maGlass1, delta);
    }

    // FLUDD's own perform remains budgeted, but its root follows the translated
    // chest immediately so the pack does not lag one sampled pose behind.
    if (mario->mFludd) {
        const u8 chestJoint = mario->mBindBoneIDArray[0];
        if (chestJoint < model->mModelData->mJointNum)
            mario->mFludd->setBaseTRMtx(model->mJointArray[chestJoint]);
    }

    MTXCopy(newRoot, slot->cachedPoseRoot);
}

static Mtx *getFluddChestMtx(TMario *mario) {
    if (!mario->mModelData || !mario->mModelData->mModel || !mario->mModelData->mModel->mJointArray)
        return nullptr;

    const u8 chestJoint = mario->mBindBoneIDArray[0];
    return &mario->mModelData->mModel->mJointArray[chestJoint];
}

static f32 *getFluddSwitchProgress(TWaterGun *fludd) {
    return reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(fludd) + kFluddSwitchProgressOffset);
}

static f32 *getFluddSwitchSpeed(TWaterGun *fludd) {
    return reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(fludd) + kFluddSwitchSpeedOffset);
}

static f32 *getFluddDeploy(TWaterGun *fludd) {
    return reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(fludd) + kFluddDeployOffset);
}

static u8 clampNozzleId(u8 nozzle) {
    if (nozzle > TWaterGun::Turbo)
        return TWaterGun::Spray;
    return nozzle;
}

static void applyRemoteNozzle(TWaterGun *fludd, u8 nozzleType);

static bool isTriggerNozzleType(u8 nozzleType);

static void ensureTriggerNozzleReady(TWaterGun *fludd, u8 nozzleType) {
    if (!fludd || !isTriggerNozzleType(nozzleType))
        return;

    auto *trigger = reinterpret_cast<TNozzleTrigger *>(fludd->mNozzleList[nozzleType]);
    if (trigger && trigger->mSprayState == TNozzleTrigger::DEAD)
        trigger->mSprayState = TNozzleTrigger::INACTIVE;
}

static void ensureAllTriggerNozzlesReady(TWaterGun *fludd) {
    ensureTriggerNozzleReady(fludd, TWaterGun::Hover);
    ensureTriggerNozzleReady(fludd, TWaterGun::Rocket);
    ensureTriggerNozzleReady(fludd, TWaterGun::Turbo);
}

static void setRemoteSwitchProgress(TWaterGun *fludd, u8 nozzleType) {
    f32 *prog = getFluddSwitchProgress(fludd);
    if (prog)
        *prog = nozzleType == TWaterGun::Spray ? 0.0f : 1.0f;
}

static void reconcileRemoteFluddNozzle(RemoteActorSlot &slot, TWaterGun *fludd) {
    if (!fludd)
        return;

    const u8 targetNozzle = clampNozzleId(unpackCurrentNozzle(slot.nozzleId));
    const u8 secondNozzle = clampNozzleId(unpackSecondNozzle(slot.nozzleId));
    fludd->mSecondNozzle = secondNozzle;

    if (fludd->mCurrentNozzle != targetNozzle) {
        applyRemoteNozzle(fludd, targetNozzle);
        setRemoteSwitchProgress(fludd, targetNozzle);
        f32 *speed = getFluddSwitchSpeed(fludd);
        if (speed)
            *speed = 0.0f;
    }

    ensureAllTriggerNozzlesReady(fludd);
}

static bool remoteFluddSwitchActive(const RemoteActorSlot &slot, u16 vfxFlags) {
    return (vfxFlags & VFX_NOZZLE_SWITCHING) != 0 || slot.fluddSwitchLatched;
}

// Retail TWaterGun::movement() crosses nozzles at progress 0.5. Don't rewind local
// progress from quantized network samples while a switch is replaying locally.
static void mergeRemoteSwitchProgress(f32 *local, f32 network, bool towardSpray, bool hostSwitching) {
    if (!local)
        return;
    if (hostSwitching) {
        *local = network;
        return;
    }
    if (towardSpray) {
        if (network < *local)
            *local = network;
    } else if (network > *local) {
        *local = network;
    }
}

static void syncRemoteAnimAux(TMario *body, TWaterGun *fludd, u8 animAux, bool showFluddOnBack) {
    const bool onYoshi = body && body->onYoshi();
    const u8 hand = onYoshi ? unpackYoshiTongueHand(animAux) : unpackAnimAuxHand(animAux);
    body->changeHand(static_cast<int>(hand));

    if (!fludd || !showFluddOnBack || onYoshi)
        return;

    f32 *deploy = getFluddDeploy(fludd);
    if (deploy)
        *deploy = unpackAnimAuxDeploy(animAux);
}

static bool snapshotAnimChanged(const RemoteActorSlot &slot, const PlayerSnapshot &snap) {
    return slot.lastAnimId != snap.animId || slot.lastMovementState != snap.movementState ||
           slot.lastNozzleId != snap.nozzleId || slot.lastVfxFlags != snap.vfxFlags ||
           slot.lastHealth != snap.health;
}

static void applyRemoteNozzle(TWaterGun *fludd, u8 nozzleType) {
    if (!fludd || fludd->mCurrentNozzle == nozzleType)
        return;

    if (nozzleType == TWaterGun::Yoshi && isRemoteBody(fludd->mMario) &&
        !fludd->mMario->onYoshi()) {
        return;
    }

    // Retail changeNozzle(Yoshi) touches tongue/ride state — assign directly on puppets.
    if (isRemoteBody(fludd->mMario) && nozzleType == TWaterGun::Yoshi) {
        fludd->mCurrentNozzle = TWaterGun::Yoshi;
        setRemoteSwitchProgress(fludd, nozzleType);
        return;
    }

    fludd->changeNozzle(static_cast<TWaterGun::TNozzleType>(nozzleType), false);
    ensureTriggerNozzleReady(fludd, nozzleType);
    setRemoteSwitchProgress(fludd, nozzleType);
}

static bool remoteFluddPerformSafe(const TMario *mario, TWaterGun *fludd) {
    if (!mario || !fludd)
        return false;
    if (!isRemoteBody(mario))
        return true;
    // Never run retail FLUDD perform/movement with the Yoshi nozzle on network puppets.
    if (fludd->mCurrentNozzle == TWaterGun::Yoshi || mario->onYoshi())
        return false;
    return true;
}

static void maintainRemoteFluddSwitchSpeed(TWaterGun *fludd, const RemoteActorSlot &slot) {
    if (!fludd || !slot.fluddSwitchLatched)
        return;

    f32 *speed = getFluddSwitchSpeed(fludd);
    if (speed)
        *speed = slot.fluddTowardSpray ? -kFluddNozzleChangeSpeed : kFluddNozzleChangeSpeed;
}

static void finishRemoteFluddSwitchIfDone(RemoteActorSlot &slot, TWaterGun *fludd) {
    if (!fludd || !slot.fluddSwitchLatched)
        return;

    f32 *speed = getFluddSwitchSpeed(fludd);
    if (!speed || *speed != 0.0f)
        return;

    slot.fluddSwitchLatched = false;
    reconcileRemoteFluddNozzle(slot, fludd);
}

// Mirror host FLUDD nozzle state when a new network sample arrives; latch switch
// motion locally so 60fps movement() can finish between network snapshots.
static void syncRemoteFluddState(RemoteActorSlot &slot, TWaterGun *fludd, u8 packedNozzle,
                                 u8 packedMovement, u16 vfxFlags, u8 upperState) {
    (void)upperState;
    if (!fludd)
        return;

    if (snapshotHostOnYoshi(packedNozzle, vfxFlags)) {
        f32 *speed = getFluddSwitchSpeed(fludd);
        if (speed)
            *speed = 0.0f;
        fludd->mIsEmitWater = false;
        slot.fluddSwitchLatched = false;
        if (fludd->mCurrentNozzle == TWaterGun::Yoshi)
            fludd->mCurrentNozzle = TWaterGun::Spray;
        f32 *deploy = getFluddDeploy(fludd);
        if (deploy)
            *deploy = 0.0f;
        return;
    }

    const u8 secondNozzle = clampNozzleId(unpackSecondNozzle(packedNozzle));
    const u8 currentNozzle = clampNozzleId(unpackCurrentNozzle(packedNozzle));
    const f32 progress = unpackFluddSwitchProgress(packedMovement);
    const bool towardSpray = unpackFluddSwitchTowardSpray(packedMovement);
    const bool hostSwitching = (vfxFlags & VFX_NOZZLE_SWITCHING) != 0;
    const bool switchActive = hostSwitching || slot.fluddSwitchLatched;

    if (!hostSwitching && slot.lastNozzleId != 0xFF) {
        const u8 prevCurrent = clampNozzleId(unpackCurrentNozzle(slot.lastNozzleId));
        if (prevCurrent != currentNozzle) {
            slot.fluddSwitchLatched = false;
            f32 *speedNow = getFluddSwitchSpeed(fludd);
            f32 *progNow = getFluddSwitchProgress(fludd);
            if (speedNow)
                *speedNow = 0.0f;
            if (progNow)
                *progNow = currentNozzle == TWaterGun::Spray ? 0.0f : 1.0f;
            applyRemoteNozzle(fludd, currentNozzle);
        }
    }

    fludd->mSecondNozzle = secondNozzle;
    slot.fluddSecondNozzle = secondNozzle;

    if (hostSwitching) {
        slot.fluddSwitchLatched = true;
        slot.fluddTowardSpray = towardSpray;
    }

    f32 *prog = getFluddSwitchProgress(fludd);
    f32 *speed = getFluddSwitchSpeed(fludd);
    if (prog) {
        if (switchActive)
            mergeRemoteSwitchProgress(prog, progress, slot.fluddTowardSpray, hostSwitching);
        else
            *prog = progress;
    }
    if (speed) {
        if (hostSwitching || slot.fluddSwitchLatched)
            *speed = slot.fluddTowardSpray ? -kFluddNozzleChangeSpeed : kFluddNozzleChangeSpeed;
        else
            *speed = 0.0f;
    }

    if (!switchActive)
        applyRemoteNozzle(fludd, currentNozzle);

    ensureAllTriggerNozzlesReady(fludd);
    fludd->mIsEmitWater = false;
}

static void syncRemoteFluddDeploy(TMario *body, TWaterGun *fludd, u8 animAux) {
    if (!body || !fludd)
        return;

    syncRemoteAnimAux(body, fludd, animAux, true);
    const f32 deploy = unpackAnimAuxDeploy(animAux);
    if (deploy < 0.05f) {
        // Folded / sleep pose: keep trigger nozzles alive so they can wake on switch.
        ensureAllTriggerNozzlesReady(fludd);
    }
}

static void playRemoteFluddActorSound(u32 soundId, TMario *body) {
    if (!gpMSound || !body || !gpMSound->gateCheck(soundId))
        return;

    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (gActors[i].spawned && gActors[i].body == body &&
            shouldSuppressRemoteHiderFromSeekerGrace(static_cast<u8>(i)))
            return;
    }

    if (!isRemoteMarioSoundAudible(body->mTranslation))
        return;

    auto *actor = reinterpret_cast<JAIActor *>(body);
    if (MSoundSESystem::MSoundSE::checkMonoSound(soundId, actor))
        return;

    const Vec *pos = reinterpret_cast<const Vec *>(&body->mTranslation);
    MSoundSESystem::MSoundSE::startSoundActor(soundId, pos, 0, nullptr, 0, 4);
}

static void updateRemoteFluddSounds(TMario *body, TWaterGun *fludd, RemoteActorSlot &slot, u16 vfx) {
    if (!body || !fludd || !gpMSound)
        return;

    const u16 prev = slot.lastSoundVfx;
    const bool spraying = (vfx & VFX_WATER_SPRAY) != 0;
    const bool drySpray = (vfx & VFX_FLUDD_EMPTY) != 0;

    if (drySpray && !(prev & VFX_FLUDD_EMPTY))
        playRemoteFluddActorSound(MSD_SE_PO_ACTION_ON_EMPTY, body);

    if (!drySpray && (prev & VFX_FLUDD_EMPTY))
        playRemoteFluddActorSound(MSD_SE_PO_ACTION_OFF_EMPTY, body);

    if (spraying && !(prev & VFX_WATER_SPRAY)) {
        const u32 triggerSe =
            (vfx & VFX_HOVER) ? MSD_SE_PO_WATER_LOW_TRG : MSD_SE_PO_WATER_HI_TRG;
        playRemoteFluddActorSound(triggerSe, body);
    }

    if (!spraying && (prev & VFX_WATER_SPRAY))
        slot.fluddSprayTick = 0;

    if (spraying && (slot.fluddSprayTick % kFluddSpraySoundInterval) == 0) {
        const Vec *pos = reinterpret_cast<const Vec *>(&body->mTranslation);
        if (vfx & VFX_HOVER)
            playRemoteMarioPositionalSound(MSD_SE_PO_HOVER, *pos);
        else if ((vfx & VFX_ROCKET) && gpMSound->gateCheck(MSD_SE_PO_ROCKET_TRIGGER))
            playRemoteFluddActorSound(MSD_SE_PO_ROCKET_TRIGGER, body);
        else if ((vfx & VFX_TURBO) && gpMSound->gateCheck(MSD_SE_PO_SNIPER_TRIGGER))
            playRemoteFluddActorSound(MSD_SE_PO_SNIPER_TRIGGER, body);
        else
            playRemoteFluddActorSound(MSD_SE_PO_WATER_HI, body);
    }

    slot.lastSoundVfx = vfx;
}

// Positional Mario movement SE on remotes. Mirrors playRemoteFluddActorSound /
// soundMovement() one-shots without setPlayerInfo / MSRandPlay registration.
static void playRemoteMarioActorSound(u32 soundId, TMario *body) {
    if (!gpMSound || !body || !gpMSound->gateCheck(soundId))
        return;

    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (gActors[i].spawned && gActors[i].body == body &&
            shouldSuppressRemoteHiderFromSeekerGrace(static_cast<u8>(i)))
            return;
    }

    if (!isRemoteMarioSoundAudible(body->mTranslation))
        return;

    auto *actor = reinterpret_cast<JAIActor *>(body);
    if (MSoundSESystem::MSoundSE::checkMonoSound(soundId, actor))
        return;

    const Vec *pos = reinterpret_cast<const Vec *>(&body->mTranslation);
    MSoundSESystem::MSoundSE::startSoundActor(soundId, pos, 0, nullptr, 0, 4);
}

// doldecomp MarioStatus.hpp status type+id mask (low 9 bits of mState).
constexpr u32 kStatusIdTurnMid = 0x043u;
constexpr u32 kStatusIdTurnEnd = 0x044u;

static bool isWetSlideState(u32 state, u16 vfxFlags) {
    return remoteStatusId(state) == kStatusIdCatchSlide && (vfxFlags & VFX_WET_SLIDE) != 0;
}

static bool isDrySlideState(u32 state, u16 vfxFlags) {
    switch (remoteStatusId(state)) {
    case 0x050u: // MARIO_STATUS_SLIP
    case 0x052u: // MARIO_STATUS_SLIP_FORE
    case 0x053u: // MARIO_STATUS_SLIP_BACK
    case 0x04Cu: // MARIO_STATUS_SPEED_SLIDING
    case 0x059u: // MARIO_STATUS_SQUAT_SLIP
    case 0x05Du: // MARIO_STATUS_OIL_SLIP / GOOPSLIDE
        return true;
    case kStatusIdCatchSlide:
        return (vfxFlags & VFX_WET_SLIDE) == 0;
    default:
        return false;
    }
}

static bool isTurnState(u32 state) {
    const u32 id = remoteStatusId(state);
    return id == kStatusIdTurnMid || id == kStatusIdTurnEnd;
}

static bool isAirSpinJump(u32 state) {
    const u32 id = remoteStatusId(state);
    return id == 0x095u || id == 0x096u;
}

static bool isGroundSpinCharge(u32 state) {
    const u32 id = remoteStatusId(state);
    return id == 0x041u || id == 0x042u;
}

static bool isGroundPoundFallState(u32 state) {
    return state == TMario::STATE_SLAMSTART || state == TMario::STATE_G_POUND;
}

static bool isLandSlipState(u32 state) {
    return state == TMario::STATE_JMP_LAND || state == TMario::STATE_HVY_LAND ||
           state == TMario::STATE_D_LAND || state == TMario::STATE_WALL_S_L;
}

static bool isHeavyLanding(u32 state, u32 prevState) {
    if (state == TMario::STATE_HVY_LAND)
        return true;
    return isAirSpinJump(prevState);
}

// Ground dive-slide roll (speed sliding) — retail MSRandPlay drive;
// Phase A approximates with one-shot + rate-limited ROLL SE.
static bool isRolloutState(u32 state) {
    return remoteStatusId(state) == 0x04Cu; // MARIO_STATUS_SPEED_SLIDING
}

static bool isSwimPaddleActive(u32 state) {
    if ((state & TMario::STATE_WATERBORN) == 0)
        return false;
    const u32 id = remoteStatusId(state);
    // MARIO_STATUS_SWIM_WAIT_TO_PADDLE .. SWIM_PADDLE_END_TO_WAIT (0xD3..0xD7)
    return id >= 0x0D3u && id <= 0x0D7u;
}

static bool isWallKnockState(u32 state) {
    return state == TMario::STATE_F_KNCK_H || state == TMario::STATE_KNCK_GND ||
           state == TMario::STATE_KNCK_LND;
}

static f32 remoteHorizontalSpeed(const TMario *body) {
    if (!body)
        return 0.0f;
    return sqrtf(body->mSpeed.x * body->mSpeed.x + body->mSpeed.z * body->mSpeed.z);
}

static u32 remoteFootstepSoundId(u8 phase) {
    switch (phase & 3u) {
    case 0:
        return MSD_SE_MA_WALK_STONE_L_HEEL;
    case 1:
        return MSD_SE_MA_WALK_STONE_L_TIP;
    case 2:
        return MSD_SE_MA_WALK_STONE_R_HEEL;
    default:
        return MSD_SE_MA_WALK_STONE_R_TIP;
    }
}

// One-shot movement SE edges — same window as syncRemoteParticleEdges.
static void updateRemoteMovementSoundEdges(TMario *body, RemoteActorSlot &slot, u32 prevState,
                                           u32 state, u16 vfxFlags) {
    if (!body || !gpMSound)
        return;
    if ((vfxFlags & VFX_DEAD) != 0)
        return;
    if (prevState == kInvalidTrackState || prevState == state)
        return;

    // Slide type uses current vfx (CATCH wet bit). Status ids for other slips
    // don't need vfx; misclassifying CATCH the frame wet flips is acceptable.
    const bool prevDry = isDrySlideState(prevState, vfxFlags);
    const bool prevWet = isWetSlideState(prevState, vfxFlags);
    const bool nowDry = isDrySlideState(state, vfxFlags);
    const bool nowWet = isWetSlideState(state, vfxFlags);

    if ((!prevDry && !prevWet) && (nowDry || nowWet)) {
        // Speed-sliding uses ROLL; other slip/slide statuses use SLIP.
        if (remoteStatusId(state) == 0x04Cu)
            playRemoteMarioActorSound(MSD_SE_MA_ROLL, body);
        else if (nowWet || remoteStatusId(state) == 0x05Du)
            playRemoteMarioActorSound(MSD_SE_MA_SLIP_POLLUT, body);
        else
            playRemoteMarioActorSound(MSD_SE_MA_SLIP, body);
    } else if (!isRolloutState(prevState) && isRolloutState(state)) {
        playRemoteMarioActorSound(MSD_SE_MA_ROLL, body);
    }

    if (isGroundPoundFallState(prevState) && state == TMario::STATE_SLAM)
        playRemoteMarioActorSound(MSD_SE_MA_HIP_ATTACK, body);

    const bool enteringLand = !isLandSlipState(prevState) && isLandSlipState(state);
    if (slot.wasAirborne && enteringLand) {
        if (isHeavyLanding(state, prevState)) {
            playRemoteMarioActorSound(MSD_SE_MA_BOUND, body);
            playRemoteMarioActorSound(MSD_SE_MA_TIMP_HI, body);
        } else {
            playRemoteMarioActorSound(MSD_SE_MA_TIMP_LOW, body);
        }
    }

    if (prevState != TMario::STATE_SLIP_JUMP && state == TMario::STATE_SLIP_JUMP)
        playRemoteMarioActorSound(MSD_SE_MA_STALL_JUMP, body);

    if (!isWallKnockState(prevState) && isWallKnockState(state))
        playRemoteMarioActorSound(MSD_SE_MA_WALL_COL_SOFT, body);

    if (isAirSpinJump(state) && !isAirSpinJump(prevState))
        playRemoteMarioActorSound(MSD_SE_MA_ROLL_JUMP, body);
}

// Continuous / rate-limited movement SE — called each remote perform tick
// BEFORE wasInWater is advanced so water enter/exit edges are visible.
static void updateRemoteMovementSoundLoops(TMario *body, RemoteActorSlot *slot) {
    if (!body || !slot || !gpMSound)
        return;

    const u16 vfxFlags = slot->vfxFlags;
    if ((vfxFlags & VFX_DEAD) != 0) {
        slot->wasSurfRide = false;
        slot->wasSurfJump = false;
        return;
    }

    const u32 state = body->mState;
    const u16 animId = body->mAnimationID;
    ++slot->moveSeTick;

    // Water enter / exit (reads wasInWater before continuous-VFX updates it).
    const bool swimming = isRemoteSwimming(body);
    if (swimming && !slot->wasInWater) {
        if (isDiveSwimAnim(animId))
            playRemoteMarioActorSound(MSD_SE_MA_FALL_IN_WATER_DEP, body);
        else
            playRemoteMarioActorSound(MSD_SE_MA_FALL_IN_WATER_SLW, body);
    } else if (!swimming && slot->wasInWater) {
        if ((state & TMario::STATE_AIRBORN) != 0)
            playRemoteMarioActorSound(MSD_SE_MA_JUMP_FR_WATER_SLW, body);
    }

    const bool surfRide = smso::isBlooperSurfRideState(state);
    const bool surfJump = smso::isBlooperSurfState(state) && !surfRide;
    if (surfJump && slot->wasSurfRide && !slot->wasSurfJump)
        playRemoteMarioActorSound(MSD_SE_MA_SURF_JUMP, body);
    if (surfRide && slot->wasSurfJump)
        playRemoteMarioActorSound(MSD_SE_MA_SURF_GND_ON_WATER, body);
    if (surfRide && !slot->wasSurfRide)
        playRemoteMarioActorSound(MSD_SE_MA_SURF_START_ACCEL, body);
    slot->wasSurfRide = surfRide;
    slot->wasSurfJump = surfJump;

    const bool drySlide = isDrySlideState(state, vfxFlags);
    const bool wetSlide = isWetSlideState(state, vfxFlags);
    const bool speedSlide = isRolloutState(state);
    if ((drySlide || wetSlide) && !speedSlide &&
        (slot->moveSeTick % kMoveSeSlipInterval) == 0) {
        if (wetSlide || remoteStatusId(state) == 0x05Du)
            playRemoteMarioActorSound(MSD_SE_MA_SLIP_POLLUT, body);
        else
            playRemoteMarioActorSound(MSD_SE_MA_SLIP, body);
    }

    if (speedSlide && remoteHorizontalSpeed(body) >= kMoveSeRollSpeedMin &&
        (slot->moveSeTick % kMoveSeRollInterval) == 0) {
        playRemoteMarioActorSound(MSD_SE_MA_ROLL, body);
    }

    if (swimming && isSwimPaddleActive(state) &&
        (slot->moveSeTick % kMoveSeSwimInterval) == 0) {
        playRemoteMarioActorSound(MSD_SE_MA_SWIM_MOVE, body);
    }

    if (surfRide && (slot->moveSeTick % kMoveSeSurfInterval) == 0)
        playRemoteMarioActorSound(MSD_SE_MA_SURF_WATER, body);

    // Generic stone footsteps — no setPlayerInfo / MSRandPlay, no surface codes.
    const bool grounded = (state & TMario::STATE_AIRBORN) == 0 && !swimming && !surfRide &&
                          !drySlide && !wetSlide;
    if (grounded && isRunningAnim(body) &&
        remoteHorizontalSpeed(body) >= kMoveSeFootstepSpeedMin &&
        (slot->moveSeTick % kMoveSeFootstepInterval) == 0) {
        playRemoteMarioActorSound(remoteFootstepSoundId(slot->footstepPhase), body);
        ++slot->footstepPhase;
    }
}

static void emitRemoteWetSlideVfx(TMario *body) {
    if (!body || !gpMarioParticleManager)
        return;

    Mtx *mtx = body->getCenterAnmMtx();
    if (!mtx)
        return;

    // Match retail frontSlipEffect() wet path — WATSLIDE only.
    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kWaterSlideA, *mtx, 3, body);
    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kWaterSlideB, *mtx, 1, body);
    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kWaterSlideC, *mtx, 1, body);
}

static void emitRemoteDrySlideVfx(TMario *body) {
    if (!body || !gpMarioParticleManager)
        return;

    Mtx *mtx = body->getRootAnmMtx();
    if (!mtx)
        return;

    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kSlipSmoke, *mtx, 1, body);
}

static void emitRemoteSurfVfx(TMario *body, const RemoteActorSlot *slot) {
    if (!body || !gpMarioParticleManager)
        return;
    if (slot && (slot->vfxFlags & VFX_DEAD) != 0)
        return;

    body->mWaterHeight = body->mTranslation.y;
    body->surfingEffect();
}

static void emitRemoteTurnEntryVfx(TMario *body) {
    if (!body)
        return;

    // running() one-shot when entering MARIO_STATUS_TURN — walk dust only.
    const s16 rot = static_cast<s16>(body->mAngle.y + 0x8000);
    body->emitParticle(particles::kWalkDust, rot);
    body->emitParticle(particles::kWalkDustC, rot);
    body->emitParticle(particles::kWalkDustB, rot);
}

static void emitRemoteSpinJumpBlur(TMario *body) {
    if (!body)
        return;

    // Retail emitBlurSpinJump() — must run after calcAnim so the center mtx is posed.
    body->emitBlurSpinJump();
}

// Deep-water swim VFX — body bubbles + surface ripple. Mirrors the retail swimMain
// path (swimmingBubbleEffect / rippleEffect) on the puppet body. Matrices are posed
// by remoteCalcAnim() before this runs, so bubble binding matches the host.
static void emitRemoteSwimVfx(TMario *body, const RemoteActorSlot *slot) {
    if (!body)
        return;

    // Remotes skip thinkHeight(), so mWaterHeight is stale (0). Swimming keeps Mario
    // at the surface; mirror the body y so retail ripple/splash placement lands on
    // the surface instead of y=0.
    body->mWaterHeight = body->mTranslation.y;

    const u8 tick = slot ? slot->swimVfxTick : 0;
    if ((tick % kSwimBubbleEmitInterval) == 0)
        body->swimmingBubbleEffect();
    if ((tick % kSwimRippleEmitInterval) == 0)
        body->rippleEffect();
}

// Looping movement VFX — called each perform tick (retail slippingBasic / rotating equivalent).
static void syncRemoteContinuousParticles(TMario *body, RemoteActorSlot *slot) {
    if (!body || !gpMarioParticleManager)
        return;

    const u32 state = body->mState;
    const u16 animId = body->mAnimationID;
    const u16 vfxFlags = slot ? slot->vfxFlags : static_cast<u16>(0);
    if ((vfxFlags & VFX_DEAD) != 0) {
        if (slot) {
            slot->wasInWater = false;
            slot->wasSurfRide = false;
            slot->wasSurfJump = false;
        }
        return;
    }

    // Movement SE before wasInWater advances (water enter/exit edges).
    if (slot)
        updateRemoteMovementSoundLoops(body, slot);

    const bool turboDashActive =
        slot && (vfxFlags & VFX_TURBO) != 0 && (vfxFlags & VFX_WATER_SPRAY) != 0 && body->mFludd &&
        body->mFludd->mCurrentNozzle == TWaterGun::Turbo;

    // Water-entry splash (one-shot) + swim tick bookkeeping. Blooper surf is a
    // separate non-WATERBORN status and is handled by the surf branch below.
    const bool swimming = isRemoteSwimming(body);
    if (slot) {
        if (swimming && !slot->wasInWater) {
            body->mWaterHeight = body->mTranslation.y;
            body->inOutWaterEffect(body->mTranslation.y);
        }
        slot->wasInWater = swimming;
        if (swimming)
            ++slot->swimVfxTick;
    }

    if (turboDashActive && remoteMarioInWater(body)) {
        if (smso::remoteBlooperSurfUsesVfx(state))
            emitRemoteSurfVfx(body, slot);
        else if ((vfxFlags & VFX_WET_SLIDE) != 0)
            emitRemoteWetSlideVfx(body);
        return;
    }

    // Slide/surf take priority over swim: a wet belly-slide can also carry
    // WATERBORN, and Blooper surf is its own status. Deep-water swim VFX
    // (bubbles + surface ripple) only fires when no other movement VFX matched.
    if (isDrySlideState(state, vfxFlags) && !turboDashActive) {
        emitRemoteDrySlideVfx(body);
    } else if (isWetSlideState(state, vfxFlags) && !turboDashActive) {
        emitRemoteWetSlideVfx(body);
    } else if (smso::remoteBlooperSurfUsesVfx(state) && !turboDashActive) {
        emitRemoteSurfVfx(body, slot);
    } else if (swimming && !turboDashActive) {
        emitRemoteSwimVfx(body, slot);
    } else if (isSpinJumpPlayback(animId, state)) {
        // doldecomp rotating()/rotateJumping() call emitBlurSpinJump() every frame.
        emitRemoteSpinJumpBlur(body);
    }

    if (isGroundPoundFallState(state)) {
        if (animId == kAnimHipAttack)
            body->emitBlurHipDropSuper();
        else
            body->emitBlurHipDrop();
    }
}

enum PendingContactVfx : u8 {
    CONTACT_VFX_LANDING = 1 << 0,
    CONTACT_VFX_GROUND_POUND = 1 << 1,
};

static void queueRemoteContactVfx(RemoteActorSlot &slot, u8 flags,
                                  const PlayerSnapshot &snap) {
    slot.pendingContactVfx = static_cast<u8>(slot.pendingContactVfx | flags);
    slot.pendingContactPos.x = snap.position.x;
    slot.pendingContactPos.y = snap.position.y;
    slot.pendingContactPos.z = snap.position.z;
}

static void emitPendingRemoteContactVfx(TMario *body, RemoteActorSlot &slot) {
    const u8 pending = slot.pendingContactVfx;
    if (!body || pending == 0 || !gpMarioParticleManager)
        return;

    TVec3f contact = {body->mTranslation.x, slot.pendingContactPos.y,
                      body->mTranslation.z};
    if (gpMap) {
        const TBGCheckData *plane = nullptr;
        const f32 groundY =
            gpMap->checkGround(contact.x, body->mTranslation.y + kShadowGroundProbeLift,
                               contact.z, &plane);
        if (groundY == groundY && fabsf(groundY - body->mTranslation.y) < 800.0f)
            contact.y = groundY;
    }

    // World-position emit is deliberate. TMario::emitParticle and touchdown
    // helpers bind to the sampled body joints, which can still represent the
    // preceding airborne pose under temporal LOD.
    if ((pending & CONTACT_VFX_LANDING) != 0) {
        gpMarioParticleManager->emit(particles::kJumpLandA, &contact, 0, nullptr);
        gpMarioParticleManager->emit(particles::kJumpLandB, &contact, 0, nullptr);
    }
    if ((pending & CONTACT_VFX_GROUND_POUND) != 0) {
        gpMarioParticleManager->emit(particles::kHipDropA, &contact, 0, nullptr);
        gpMarioParticleManager->emit(particles::kHipDropB, &contact, 0, nullptr);
        gpMarioParticleManager->emit(particles::kHipDropC, &contact, 0, nullptr);
    }
    slot.pendingContactVfx = 0;
}

// One-shot movement VFX — landing puff, ground-pound impact. State edges only
// enqueue events; world-space emission occurs after render-root interpolation.
static void syncRemoteParticleEdges(TMario *body, RemoteActorSlot &slot, const PlayerSnapshot &snap) {
    if (!body || !gpMarioParticleManager)
        return;
    if (shouldSuppressRemoteHiderFromSeekerGrace(networkSlotOf(&slot)))
        return;

    const u32 state = body->mState;
    const u32 prevState = slot.lastState;

    queueRemoteWarpEdges(slot, prevState, state, snap.animId);

    if (prevState != kInvalidTrackState && prevState != state) {
        const bool enteringLand = !isLandSlipState(prevState) && isLandSlipState(state);
        if (slot.wasAirborne && enteringLand) {
            (void)isHeavyLanding(state, prevState);
            queueRemoteContactVfx(slot, CONTACT_VFX_LANDING, snap);
        }

        if (!isTurnState(prevState) && isTurnState(state))
            emitRemoteTurnEntryVfx(body);

        const bool wasSpin = isSpinJumpState(prevState);
        const bool nowSpin = isSpinJumpState(state);
        if (!wasSpin && nowSpin) {
            if (isGroundSpinCharge(state))
                body->emitRotateShootEffect();
        }

        if (isGroundPoundFallState(prevState) && state == TMario::STATE_SLAM) {
            queueRemoteContactVfx(slot, CONTACT_VFX_GROUND_POUND, snap);
        }
    }

    updateRemoteMovementSoundEdges(body, slot, prevState, state, snap.vfxFlags);

    slot.lastState = state;
    slot.lastAnimId = snap.animId;
    slot.wasAirborne = (state & TMario::STATE_AIRBORN) != 0;
}

static bool isTriggerNozzleType(u8 nozzleType) {
    return nozzleType == TWaterGun::Hover || nozzleType == TWaterGun::Rocket ||
           nozzleType == TWaterGun::Turbo;
}

static void applyRemoteNozzlePressure(TWaterGun *fludd, f32 pressure) {
    if (!fludd)
        return;
    TNozzleBase *nozzle = fludd->mNozzleList[fludd->mCurrentNozzle];
    if (!nozzle)
        return;

    if (pressure < 0.0f)
        pressure = 0.0f;
    if (pressure > 1.0f)
        pressure = 1.0f;

    const u8 nozzleType = fludd->mCurrentNozzle;
    if (isTriggerNozzleType(nozzleType)) {
        TNozzleTrigger *trigger = static_cast<TNozzleTrigger *>(nozzle);
        // Turbo dash is Mario-bound smoke/ripple only — never drive trigger emitCommon().
        if (nozzleType == TWaterGun::Turbo) {
            trigger->mTriggerFill = 0.0f;
            trigger->mSprayState = TNozzleTrigger::INACTIVE;
            return;
        }
        const f32 maxPressure = trigger->mEmitParams.mInsidePressureMax.get();
        trigger->mTriggerFill = pressure * maxPressure;
        if (pressure > 0.01f)
            trigger->mSprayState = TNozzleTrigger::ACTIVE;
        else
            trigger->mSprayState = TNozzleTrigger::INACTIVE;
        return;
    }

    nozzle->_378 = pressure;
    u16 *triggerStep = reinterpret_cast<u16 *>(reinterpret_cast<u8 *>(nozzle) + 0x372);
    *triggerStep = static_cast<u16>(pressure * static_cast<f32>(256 * 150));
}

static void syncRemoteSprayPressure(TWaterGun *fludd, RemoteActorSlot &slot, u16 vfxFlags) {
    if (!fludd)
        return;

    if (fludd->mCurrentNozzle == TWaterGun::Turbo) {
        slot.remoteSprayPressure = 0.0f;
        applyRemoteNozzlePressure(fludd, 0.0f);
        return;
    }

    if (!(vfxFlags & VFX_WATER_SPRAY)) {
        slot.remoteSprayPressure = 0.0f;
        applyRemoteNozzlePressure(fludd, 0.0f);
        return;
    }

    // Apply host pressure immediately. Ascent smoothing (*0.65) lagged pump-up and
    // produced a thinner stream than local Mario's live _378 / trigger fill.
    f32 target = decodeSprayPressure(slot.syncedSprayPressure);
    if (target > 1.0f)
        target = 1.0f;
    if (target < 0.0f)
        target = 0.0f;
    slot.remoteSprayPressure = target;
    applyRemoteNozzlePressure(fludd, target);
}

static bool shouldEmitRemoteSprayThisFrame(RemoteActorSlot &slot) {
    ++slot.fluddSprayTick;
    return (slot.fluddSprayTick % kFluddSprayEmitInterval) == 0;
}

static void visualEmitNozzleBase(TNozzleBase *nozzle, int emitterIndex) {
    TWaterGun *fludd = nozzle->mFludd;
    if (!fludd || !fludd->mEmitInfo || !gpModelWaterManager || nozzle->_378 <= 0.0f)
        return;

    TWaterEmitInfo *emitInfo = fludd->mEmitInfo;
    nozzle->emitCommon(emitterIndex, emitInfo);

    const f32 emitNum = nozzle->mEmitParams.mNum.get();
    nozzle->_37C += emitNum;

    const s32 emitCount = static_cast<s32>(nozzle->_37C);
    if (emitCount == 0)
        return;
    nozzle->_37C -= static_cast<f32>(emitCount);

    emitInfo->mNum.set(emitCount);
    emitInfo->mAttack.set(nozzle->mEmitParams.mAttack.get());

    const f32 emitPow = nozzle->mEmitParams.mEmitPow.get();
    const f32 emitCtrl = nozzle->mEmitParams.mEmitCtrl.get();
    const f32 pressure = nozzle->_378;
    emitInfo->mPow.set(emitPow * pressure * emitCtrl + emitPow * (1.0f - emitCtrl));
    emitInfo->mFlag.set(0x40);

    emitRemoteWaterRequest(emitInfo);
}

static void visualEmitNozzleDeform(TNozzleBase *nozzle, int emitterIndex) {
    TWaterGun *fludd = nozzle->mFludd;
    if (!fludd || !fludd->mEmitInfo || !gpModelWaterManager || nozzle->_378 <= 0.0f)
        return;

    TWaterEmitInfo *emitInfo = fludd->mEmitInfo;
    nozzle->emitCommon(emitterIndex, emitInfo);

    const f32 pressure = nozzle->_378;
    const f32 emitNum = nozzle->mEmitParams.mNum.get();
    const f32 emitNumMin = nozzle->mEmitParams.mNumMin.get();
    nozzle->_37C += pressure * (emitNum - emitNumMin) + emitNumMin;

    const s32 emitCount = static_cast<s32>(nozzle->_37C);
    if (emitCount == 0)
        return;
    nozzle->_37C -= static_cast<f32>(emitCount);

    emitInfo->mNum.set(emitCount);

    const s16 attackMin = nozzle->mEmitParams.mAttackMin.get();
    const s16 attack = nozzle->mEmitParams.mAttack.get();
    emitInfo->mAttack.set(pressure * static_cast<f32>(attack - attackMin) + static_cast<f32>(attackMin));

    const f32 dirTrembleMin = nozzle->mEmitParams.mDirTrembleMin.get();
    const f32 dirTremble = nozzle->mEmitParams.mDirTremble.get();
    emitInfo->mDirTremble.set(pressure * (dirTremble - dirTrembleMin) + dirTrembleMin);

    const f32 emitPowMin = nozzle->mEmitParams.mEmitPowMin.get();
    const f32 emitPow = nozzle->mEmitParams.mEmitPow.get();
    emitInfo->mPow.set(pressure * (emitPow - emitPowMin) + emitPowMin);
    emitInfo->mFlag.set(0x40);

    const f32 sizeMinPressure = nozzle->mEmitParams.mSizeMinPressure.get();
    const f32 sizeMin = nozzle->mEmitParams.mSizeMin.get();
    const f32 size = nozzle->mEmitParams.mSize.get();
    const f32 sizeMaxPressure = nozzle->mEmitParams.mSizeMaxPressure.get();

    f32 emitSizeLerp = 1.0f;
    if (pressure < sizeMinPressure) {
        emitSizeLerp = 0.0f;
    } else if (pressure < sizeMaxPressure) {
        emitSizeLerp = (sizeMinPressure - pressure) / (sizeMaxPressure - pressure);
    }
    emitInfo->mSize.set(emitSizeLerp * (size - sizeMin) + sizeMin);

    emitRemoteWaterRequest(emitInfo);
}

static void visualEmitNozzleTrigger(TNozzleTrigger *nozzle, int emitterIndex) {
    TWaterGun *fludd = nozzle->mFludd;
    if (!fludd || !fludd->mEmitInfo || !gpModelWaterManager)
        return;
    if (nozzle->mSprayState != TNozzleTrigger::ACTIVE)
        return;

    TWaterEmitInfo *emitInfo = fludd->mEmitInfo;
    nozzle->emitCommon(emitterIndex, emitInfo);

    const f32 insidePressureMax = nozzle->mEmitParams.mInsidePressureMax.get();
    const f32 emitNumMin = nozzle->mEmitParams.mNumMin.get();
    const f32 emitNum = nozzle->mEmitParams.mNum.get();
    const f32 pressure =
        insidePressureMax > 0.0f ? nozzle->mTriggerFill / insidePressureMax : 0.0f;
    if (pressure <= 0.0f)
        return;

    nozzle->_37C += pressure * (emitNum - emitNumMin) + emitNumMin;

    const s32 emitCount = static_cast<s32>(nozzle->_37C);
    if (emitCount == 0)
        return;
    nozzle->_37C -= static_cast<f32>(emitCount);

    emitInfo->mNum.set(emitCount);

    const s16 attackMin = nozzle->mEmitParams.mAttackMin.get();
    const s16 attack = nozzle->mEmitParams.mAttack.get();
    emitInfo->mAttack.set(pressure * static_cast<f32>(attack - attackMin) + static_cast<f32>(attackMin));

    const f32 emitPowMin = nozzle->mEmitParams.mEmitPowMin.get();
    const f32 emitPow = nozzle->mEmitParams.mEmitPow.get();
    emitInfo->mPow.set(pressure * (emitPow - emitPowMin) + emitPowMin);
    emitInfo->mFlag.set(0x40);

    emitRemoteWaterRequest(emitInfo);
}

static int remoteFluddEmitterCount(u8 nozzleType) {
    switch (nozzleType) {
    case TWaterGun::Hover:
    case TWaterGun::Underwater:
        return 2;
    default:
        return 1;
    }
}

static void *remoteSprayParticleOwner(TNozzleBase *nozzle, int emitterIndex) {
    if (emitterIndex == 0)
        return nozzle;
    return reinterpret_cast<void *>(reinterpret_cast<u8 *>(nozzle) +
                                  emitterIndex * sizeof(TNozzleTrigger));
}

static void emitRemoteWaterDroplets(TWaterGun *fludd, RemoteActorSlot &slot, bool emitThisFrame) {
    if (!emitThisFrame || !fludd || !gpModelWaterManager)
        return;

    TNozzleBase *nozzle = fludd->mNozzleList[fludd->mCurrentNozzle];
    if (!nozzle)
        return;

    const u8 nozzleType = fludd->mCurrentNozzle;
    const int emitterCount = remoteFluddEmitterCount(nozzleType);

    for (int i = 0; i < emitterCount; ++i) {
        switch (nozzleType) {
        case TWaterGun::Hover:
        case TWaterGun::Rocket:
            visualEmitNozzleTrigger(static_cast<TNozzleTrigger *>(nozzle), i);
            break;
        case TWaterGun::Spray:
        case TWaterGun::Yoshi:
            visualEmitNozzleDeform(nozzle, i);
            break;
        default:
            visualEmitNozzleBase(nozzle, i);
            break;
        }
    }

    // Drive viewer graffiti assist from the same emit ray as visible droplets.
    // A later-frame mtx probe alone left assist≈0 while spray VFX hit plaza walls.
    Mtx *emitMtx = fludd->getEmitMtx(0);
    if (emitMtx) {
        const f32 ox = (*emitMtx)[0][3];
        const f32 oy = (*emitMtx)[1][3];
        const f32 oz = (*emitMtx)[2][3];
        if (ox == ox && oy == oy && oz == oz) {
            const f32 dx = (*emitMtx)[0][2];
            const f32 dy = (*emitMtx)[1][2];
            const f32 dz = (*emitMtx)[2][2];
            smso::notifyRemoteSprayEmit(ox, oy, oz, dx, dy, dz);
        }
    }
}

static bool emitMtxTranslationValid(const Mtx &mtx) {
    const f32 x = mtx[0][3];
    const f32 y = mtx[1][3];
    const f32 z = mtx[2][3];
    return x == x && y == y && z == z;
}

static void emitRemoteTurboNozzleSpray(TWaterGun *fludd) {
    if (!fludd || !gpMarioParticleManager)
        return;

    TNozzleBase *nozzle = fludd->mNozzleList[TWaterGun::Turbo];
    if (!nozzle)
        return;

    Mtx *emitMtx = fludd->getEmitMtx(0);
    if (!emitMtx || !emitMtxTranslationValid(*emitMtx))
        return;

    // doldecomp TNozzleTrigger::animation — 0x10D from getEmitMtx(); manual emit avoids
    // retail owner binding (&this[i]) on network puppets which crashes in water.
    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kWaterSpray, *emitMtx, 1, nozzle);
}

static void emitRemoteTurboDashBoostVfx(TWaterGun *fludd) {
    if (!fludd || !gpMarioParticleManager)
        return;

    TNozzleBase *nozzle = fludd->mNozzleList[TWaterGun::Turbo];
    if (!nozzle)
        return;

    Mtx *emitMtx = fludd->getEmitMtx(0);
    if (!emitMtx || !emitMtxTranslationValid(*emitMtx))
        return;

    // doldecomp MarioEffect.cpp — dash boost particles while MARIO_FLAG_FLUDD_EMITTING.
    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kTurboDashBoostA, *emitMtx, 1, nozzle);
    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kTurboDashBoostB, *emitMtx, 1, nozzle);
}

static void emitRemoteTurboRunningRipple(TMario *body) {
    if (!body || !gpMarioParticleManager)
        return;
    if (body->mForwardSpeed <= 30.0f)
        return;

    // doldecomp runningRippleEffect() — position emit, never ModelWaterManager.
    JGeometry::TVec3<f32> pos;
    pos.x = body->mTranslation.x;
    pos.y = body->mFloorBelow;
    pos.z = body->mTranslation.z;
    gpMarioParticleManager->emit(particles::kTurboWaterRipple, &pos, 0, nullptr);
}

static void emitRemoteSprayVfx(TWaterGun *fludd, u16 vfxFlags, TMario *body, RemoteActorSlot *slot) {
    if (!fludd || !gpMarioParticleManager)
        return;

    const u8 nozzleType = fludd->mCurrentNozzle;
    if (nozzleType == TWaterGun::Turbo)
        return;

    TNozzleBase *nozzle = fludd->mNozzleList[fludd->mCurrentNozzle];
    if (!nozzle)
        return;

    const int emitterCount = remoteFluddEmitterCount(nozzleType);

    for (int i = 0; i < emitterCount; ++i) {
        Mtx *emitMtx = fludd->getEmitMtx(i);
        if (!emitMtx)
            continue;

        void *owner = remoteSprayParticleOwner(nozzle, i);

        gpMarioParticleManager->emitAndBindToMtxPtr(particles::kWaterSpray, *emitMtx, 1, owner);

        if (nozzleType == TWaterGun::Rocket)
            gpMarioParticleManager->emitAndBindToMtxPtr(particles::kRocketExhaustA, *emitMtx, 1, owner);
    }
}

static void emitRemoteSprayStartVfx(TWaterGun *fludd) {
    if (!fludd || !gpMarioParticleManager)
        return;

    TNozzleBase *nozzle = fludd->mNozzleList[fludd->mCurrentNozzle];
    if (!nozzle)
        return;

    Mtx *emitMtx = fludd->getEmitMtx(0);
    if (!emitMtx)
        return;

    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kSpraySplashA, *emitMtx, 1, nozzle);
    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kSpraySplashB, *emitMtx, 1, nozzle);
}

static void emitRemoteTurboWaterVfx(TMario *body, TWaterGun *fludd, RemoteActorSlot *slot, bool emitThisFrame) {
    if (!fludd || !body)
        return;

    if (emitThisFrame) {
        emitRemoteTurboNozzleSpray(fludd);
        emitRemoteTurboDashBoostVfx(fludd);
    }

    if (emitThisFrame && body->mForwardSpeed > 30.0f)
        emitRemoteTurboRunningRipple(body);

    if (slot) {
        const u16 prev = slot->lastSoundVfx;
        if (!(prev & VFX_WATER_SPRAY))
            emitRemoteSprayStartVfx(fludd);
    }
}

static void emitRemoteFluddVfx(TMario *body, TWaterGun *fludd, RemoteActorSlot *slot, u16 vfxFlags) {
    if (!fludd || !(vfxFlags & VFX_WATER_SPRAY) || (vfxFlags & VFX_FLUDD_EMPTY) != 0)
        return;
    if (body && body->onYoshi())
        return;
    if (fludd->mCurrentWater <= 0)
        return;

    // Turbo particles used to share the 30 Hz shouldEmitRemoteSprayThisFrame gate;
    // emit every visual frame so turbo spray density matches local.
    constexpr bool emitThisFrame = true;

    if (fludd->mCurrentNozzle == TWaterGun::Turbo) {
        if (body && remoteMarioInWater(body)) {
            emitRemoteTurboWaterVfx(body, fludd, slot, emitThisFrame);
            return;
        }

        emitRemoteTurboNozzleSpray(fludd);
        emitRemoteTurboDashBoostVfx(fludd);
        if (slot) {
            const u16 prev = slot->lastSoundVfx;
            if (!(prev & VFX_WATER_SPRAY))
                emitRemoteSprayStartVfx(fludd);
        }
        return;
    }

    emitRemoteSprayVfx(fludd, vfxFlags, body, slot);

    if (slot) {
        // Droplets must be emitted every frame (60 Hz) to match local Mario's FLUDD.
        emitRemoteWaterDroplets(fludd, *slot, /*emitThisFrame=*/true);
    }

    if (slot) {
        const u16 prev = slot->lastSoundVfx;
        if (!(prev & VFX_WATER_SPRAY))
            emitRemoteSprayStartVfx(fludd);
    }
}

struct RemoteYoshiEmitFluddState {
    u8 nozzle;
    s32 water;
    f32 pressure;
};

static void stageRemoteYoshiEmitFludd(TWaterGun *fludd, f32 pressure, RemoteYoshiEmitFluddState &saved) {
    if (!fludd)
        return;

    saved.nozzle = fludd->mCurrentNozzle;
    saved.water = fludd->mCurrentWater;

    TNozzleBase *yoshiNozzle = fludd->mNozzleList[TWaterGun::Yoshi];
    saved.pressure = yoshiNozzle ? yoshiNozzle->_378 : 0.0f;

    fludd->mCurrentNozzle = TWaterGun::Yoshi;
    if (yoshiNozzle) {
        fludd->mCurrentWater = yoshiNozzle->mEmitParams.mAmountMax.get();
        yoshiNozzle->_378 = pressure;
    }
}

static void restoreRemoteYoshiEmitFludd(TWaterGun *fludd, const RemoteYoshiEmitFluddState &saved) {
    if (!fludd)
        return;

    fludd->mCurrentNozzle = saved.nozzle;
    fludd->mCurrentWater = saved.water;

    TNozzleBase *yoshiNozzle = fludd->mNozzleList[TWaterGun::Yoshi];
    if (yoshiNozzle)
        yoshiNozzle->_378 = saved.pressure;
}

static void syncFluddEmitPosFromMtx(TWaterGun *fludd, const Mtx &emitMtx) {
    if (!fludd)
        return;

    // doldecomp TWaterGun::mEmitPos @ 0x1C90 (BSE header names the field mGeometry).
    TVec3f *emitPos = reinterpret_cast<TVec3f *>(reinterpret_cast<u8 *>(fludd) + 0x1C90);
    emitPos[0].x = emitMtx[0][3];
    emitPos[0].y = emitMtx[1][3];
    emitPos[0].z = emitMtx[2][3];
}

static void emitRemoteYoshiJuiceDroplets(TWaterGun *fludd, f32 pressure, u8 juiceCardType) {
    if (!fludd || !fludd->mEmitInfo || !gpModelWaterManager || pressure <= 0.0f)
        return;

    TNozzleBase *nozzle = fludd->mNozzleList[TWaterGun::Yoshi];
    if (!nozzle)
        return;

    // doldecomp TNozzleDeform::emit — green Yoshi (type 0) skips juice droplets in retail too.
    if (fludd->mMario && fludd->mMario->mYoshi && (fludd->mMario->mYoshi->mType & 0x03) == 0)
        return;

    TWaterEmitInfo *emitInfo = fludd->mEmitInfo;
    nozzle->emitCommon(0, emitInfo);

    const f32 emitNum = nozzle->mEmitParams.mNum.get();
    const f32 emitNumMin = nozzle->mEmitParams.mNumMin.get();
    nozzle->_37C += pressure * (emitNum - emitNumMin) + emitNumMin;

    const s32 emitCount = static_cast<s32>(nozzle->_37C);
    if (emitCount == 0)
        return;
    nozzle->_37C -= static_cast<f32>(emitCount);

    emitInfo->mNum.set(emitCount);

    const s16 attackMin = nozzle->mEmitParams.mAttackMin.get();
    const s16 attack = nozzle->mEmitParams.mAttack.get();
    emitInfo->mAttack.set(pressure * static_cast<f32>(attack - attackMin) + static_cast<f32>(attackMin));

    const f32 dirTrembleMin = nozzle->mEmitParams.mDirTrembleMin.get();
    const f32 dirTremble = nozzle->mEmitParams.mDirTremble.get();
    emitInfo->mDirTremble.set(pressure * (dirTremble - dirTrembleMin) + dirTrembleMin);

    const f32 emitPowMin = nozzle->mEmitParams.mEmitPowMin.get();
    const f32 emitPow = nozzle->mEmitParams.mEmitPow.get();
    emitInfo->mPow.set(pressure * (emitPow - emitPowMin) + emitPowMin);
    emitInfo->mFlag.set(0x40);

    const f32 sizeMinPressure = nozzle->mEmitParams.mSizeMinPressure.get();
    const f32 sizeMin = nozzle->mEmitParams.mSizeMin.get();
    const f32 size = nozzle->mEmitParams.mSize.get();
    const f32 sizeMaxPressure = nozzle->mEmitParams.mSizeMaxPressure.get();

    f32 emitSizeLerp = 1.0f;
    if (pressure < sizeMinPressure) {
        emitSizeLerp = 0.0f;
    } else if (pressure < sizeMaxPressure) {
        emitSizeLerp = (sizeMinPressure - pressure) / (sizeMaxPressure - pressure);
    }
    emitInfo->mSize.set(emitSizeLerp * (size - sizeMin) + sizeMin);

    smso::emitRemoteWaterRequestWithCardTint(emitInfo, juiceCardType);
}

static void emitRemoteYoshiJuiceSpray(TMario *body, RemoteActorSlot *slot, u16 vfxFlags) {
    if (!body || !slot || !body->mFludd || !body->mYoshi)
        return;
    if (!snapshotHostOnYoshi(slot->nozzleId, slot->vfxFlags))
        return;
    if (!(vfxFlags & VFX_WATER_SPRAY) || (vfxFlags & VFX_FLUDD_EMPTY) != 0)
        return;
    if (body->mYoshi->mState != TYoshi::MOUNTED)
        return;

    Mtx *tongueMtx = getRemoteYoshiSprayEmitMtx(body);
    if (!tongueMtx || !emitMtxTranslationValid(*tongueMtx))
        return;

    const u8 juiceType = static_cast<u8>(slot->yoshi.type & 0x03);
    smso::notifyRemoteYoshiJuiceDrawTint(juiceType);

    const f32 pressure = decodeSprayPressure(slot->yoshi.sprayPressureEnc);
    if (pressure <= 0.0f)
        return;

    TWaterGun *fludd = body->mFludd;
    RemoteYoshiEmitFluddState savedFludd = {};
    stageRemoteYoshiEmitFludd(fludd, pressure, savedFludd);
    syncFluddEmitPosFromMtx(fludd, *tongueMtx);

    // Match remote FLUDD droplets: emit every visual frame (60 Hz). The old 30 Hz
    // shouldEmitRemoteSprayThisFrame gate left Yoshi juice half as dense as local.
    if (gpMarioParticleManager)
        gpMarioParticleManager->emitAndBindToMtxPtr(particles::kWaterSpray, *tongueMtx, 1,
                                                    fludd->mNozzleList[TWaterGun::Yoshi]);
    emitRemoteYoshiJuiceDroplets(fludd, pressure, juiceType);
    smso::notifyRemoteSprayEmit((*tongueMtx)[0][3], (*tongueMtx)[1][3], (*tongueMtx)[2][3],
                                (*tongueMtx)[0][2], (*tongueMtx)[1][2], (*tongueMtx)[2][2]);

    const u16 prev = slot->lastSoundVfx;
    if (!(prev & VFX_WATER_SPRAY))
        emitRemoteSprayStartVfx(fludd);

    updateRemoteFluddSounds(body, fludd, *slot, vfxFlags);
    restoreRemoteYoshiEmitFludd(fludd, savedFludd);
}

static void applyRemoteYCamHelmet(TMario *body, u16 vfxFlags, bool &wasYCam) {
    const bool yCam = (vfxFlags & VFX_Y_CAM) != 0;
    if (yCam && !wasYCam)
        beginRemoteYCamPose(body);
    if (wasYCam && !yCam)
        resetRemoteYCamPose(body);

    if (!kEnableRemoteYCamHelmet) {
        body->mAttributes.mGainHelmet = false;
        body->mAttributes.mGainHelmetFlwCamera = false;
        wasYCam = yCam;
        return;
    }

    if (yCam) {
        body->mAttributes.mGainHelmetFlwCamera = true;
        body->mAttributes.mGainHelmet = true;
        if (!wasYCam)
            body->setDivHelm();
    } else if (wasYCam) {
        body->mAttributes.mGainHelmetFlwCamera = false;
    }
    wasYCam = yCam;
}

static void bindRemoteFludd(TMario *mario, RemoteActorSlot *slot, u16 vfxFlags,
                            JDrama::TGraphics *graphics) {
    TWaterGun *fludd = mario->mFludd;
    if (!fludd)
        return;

    // Yoshi nozzle movement() resolves tongue emit matrices — unsafe on network puppets.
    if (mario->onYoshi())
        return;

    if (slot)
        syncRemoteFluddDeploy(mario, fludd, slot->lastHealth);

    if (vfxFlags & VFX_Y_CAM)
        syncRemoteNozzleGunAngle(mario, decodeYCamPitch(vfxFlags));

    Mtx *chestMtx = getFluddChestMtx(mario);
    if (chestMtx)
        fludd->setBaseTRMtx(*chestMtx);

    fludd->mIsEmitWater = false;
    if (slot)
        maintainRemoteFluddSwitchSpeed(fludd, *slot);
    syncRemoteShadowGround(mario);

    const bool remoteTurboSpray =
        fludd->mCurrentNozzle == TWaterGun::Turbo && (vfxFlags & VFX_WATER_SPRAY) != 0;
    if (remoteTurboSpray) {
        mario->mAttributes.mIsFluddEmitting = false;
        fludd->mIsEmitWater = false;
        // Safe with mIsEmitWater=false: drives turbo BCK only, particles are manual.
        fludd->mNozzleList[fludd->mCurrentNozzle]->animation(fludd->mCurrentNozzle);
    } else {
        fludd->movement();
    }

    if (slot)
        syncRemoteSprayPressure(fludd, *slot, vfxFlags);
    if (slot) {
        finishRemoteFluddSwitchIfDone(*slot, fludd);
        if (!remoteFluddSwitchActive(*slot, vfxFlags))
            reconcileRemoteFluddNozzle(*slot, fludd);
    }
    fludd->perform(2, graphics);

    emitRemoteFluddVfx(mario, fludd, slot, vfxFlags);
    if (slot)
        updateRemoteFluddSounds(mario, fludd, *slot, vfxFlags);
}

// Number of puppet bodies physically allocated this stage. With the pre-spawned
// pool this is simply the pool fill count (bodies are never freed individually).
static u32 countSpawnedBodies() { return gBodyPoolCount; }

static void removeBodyFromViewList(TMario *body);

static void setBodyVisible(TMario *body, bool visible) {
    u32 *attr = reinterpret_cast<u32 *>(reinterpret_cast<u8 *>(body) + 0x114);
    if (visible)
        *attr |= kAttr114VisibleBit;
    else
        *attr &= ~kAttr114VisibleBit;
}

static u8 networkSlotOf(const RemoteActorSlot *slot) {
    if (!slot)
        return 0xFF;
    if (slot < &gActors[0] || slot >= &gActors[MAX_REMOTE_SLOTS])
        return 0xFF;
    return static_cast<u8>(slot - &gActors[0]);
}

// Remote perform bypasses retail Mario draw gating — it must honor appear-hide,
// attr 0x114 (setBodyVisible), AND Start Tag seeker-vs-hider grace suppress.
// Prior grace fix only cleared attr 0x114; draw still ran because this helper
// previously only checked appearRevealFrames.
static bool isRemoteBodyDrawVisible(const RemoteActorSlot *slot) {
    if (!slot || !slot->renderVisible || slot->appearRevealFrames != 0)
        return false;

    const u8 netSlot = networkSlotOf(slot);
    if (netSlot != 0xFF && shouldSuppressRemoteHiderFromSeekerGrace(netSlot))
        return false;

    if (slot->body) {
        const u32 attr114 =
            *reinterpret_cast<const u32 *>(reinterpret_cast<const u8 *>(slot->body) + 0x114);
        if ((attr114 & kAttr114VisibleBit) == 0)
            return false;
    }
    return true;
}

static void resetRemoteRuntimeState(RemoteActorSlot &slot) {
    slot.wasYCam = false;
    slot.turnRootLatched = false;
    slot.sideFlipOffsetLatched = false;
    slot.fluddSwitchLatched = false;
    slot.fluddTowardSpray = false;
    slot.yaw = 0;
    slot.turnRootYaw = 0;
    slot.syncHeadLook = 0;
    slot.syncGunAngle = 0;
    slot.syncWaistPitch = 0.0f;
    slot.syncWaistRoll = 0.0f;
    slot.lastWaterTank = 0;
    slot.nozzleId = 0;
    slot.lastNozzleId = 0xFF;
    slot.lastMovementState = 0xFF;
    slot.fluddSecondNozzle = 0;
    slot.vfxFlags = 0;
    smso::resetBlooperSurfSlot(slot.surf);
    slot.lastHealth = 0xFF;
    slot.lastVfxFlags = 0xFFFF;
    slot.lastState = kInvalidTrackState;
    slot.lastAnimId = 0xFFFF;
    slot.wasAirborne = false;
    slot.syncPumpUpper = false;
    slot.syncUpperFrame = 0.0f;
    slot.syncAnimFrame = 0.0f;
    slot.syncAnimRate = 1.0f;
    slot.spinYawLatched = false;
    slot.spinYaw = 0;
    slot.pendingWarpInVfx = false;
    slot.pendingWarpOutVfx = false;
    slot.pendingWarpOutKind = 0;
    slot.appearRevealFrames = 0;
    slot.lastSoundVfx = 0xFFFF;
    slot.fluddSprayTick = 0;
    slot.swimVfxTick = 0;
    slot.wasInWater = false;
    slot.moveSeTick = 0;
    slot.footstepPhase = 0;
    slot.wasSurfRide = false;
    slot.wasSurfJump = false;
    slot.syncedSprayPressure = 0;
    slot.invalidSnapshotStreak = 0;
    slot.remoteSprayPressure = 0.0f;
    slot.rosterSlot = 0xFF;
    slot.hideSeekSeekerLook = false;
    slot.hideSeekSeekerLookWas = false;
    slot.inWarpTransition = false;
    slot.renderVisible = true;
    slot.visualStateDirty = true;
    slot.visualUpdateThisFrame = true;
    slot.cosmeticUpdateThisFrame = true;
    slot.drawShadowThisFrame = true;
    slot.visualUpdateInterval = 1;
    slot.offscreenFrames = 0;
    slot.lastVisualWorkFrame = 0xFFFFFFFFu;
    slot.cachedPoseRootValid = false;
    slot.pendingContactVfx = 0;
    slot.pendingContactPos = {};
    slot.displayMotionInit = false;
    slot.lastAppliedState = kInvalidTrackState;
    slot.lastAppliedAnimId = 0xFFFF;
    slot.lastAppliedVfx = 0xFFFF;
    slot.lastAppliedNozzle = 0xFF;
    slot.lastAppliedHealth = 0xFF;
    slot.lastAppliedWater = 0xFF;
    slot.lastAppliedMovement = 0xFF;
    slot.yoshi = {};
}

static void beginRemoteAppearHide(RemoteActorSlot &slot, u16 frames) {
    if (!slot.body || frames == 0)
        return;
    setBodyVisible(slot.body, false);
    if (frames > slot.appearRevealFrames)
        slot.appearRevealFrames = frames;
}

static void tickRemoteAppearReveal(RemoteActorSlot *slot) {
    if (!slot || slot->appearRevealFrames == 0)
        return;
    if (--slot->appearRevealFrames != 0 || !slot->body)
        return;
    // Do not re-show mid-perform if Start Tag grace still suppresses this hider.
    if (shouldSuppressRemoteHiderFromSeekerGrace(networkSlotOf(slot))) {
        setBodyVisible(slot->body, false);
        return;
    }
    setBodyVisible(slot->body, true);
}

static bool isStageReady(TMarDirector *director) {
    return director && director->mCurState == TMarDirector::STATE_NORMAL;
}

static bool isSameStage(const CommBuffer *buf, const PlayerSnapshot &snap) {
    const u8 localArea =
        gpMarDirector ? gpMarDirector->mAreaID : buf->localSnapshot.stageId;
    const u8 localEpisode =
        gpMarDirector ? gpMarDirector->mEpisodeID : buf->localSnapshot.episodeId;

    u8 remoteArea = snap.stageId;
    // Back-compat: older builds stuffed tongue mProgress into stageId.
    if (snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags) &&
        yoshiTongueIsActive(unpackYoshiTongueState(snap.health)) && remoteArea != localArea)
        remoteArea = localArea;

    const u8 remoteEpisode = smso::snapshotLogicalEpisodeId(snap, localEpisode);
    const u8 localResolvedEpisode =
        smso::snapshotLogicalEpisodeId(buf->localSnapshot, localEpisode);

    return remoteArea == localArea && remoteEpisode == localResolvedEpisode;
}

static bool isFiniteVec(f32 x, f32 y, f32 z) {
    return x == x && y == y && z == z;
}

static bool isReasonableWorldPos(f32 x, f32 y, f32 z) {
    return x > -50000.0f && x < 50000.0f && y > -50000.0f && y < 50000.0f &&
           z > -50000.0f && z < 50000.0f;
}

static bool isValidSnapshot(const PlayerSnapshot &snap) {
    return snap.connected != 0 && isFiniteVec(snap.position.x, snap.position.y, snap.position.z) &&
           isReasonableWorldPos(snap.position.x, snap.position.y, snap.position.z);
}

static bool snapshotHeavyDirty(const RemoteActorSlot &slot, const PlayerSnapshot &snap) {
    const u32 rawState = static_cast<u32>(snap.actionId) |
                         (static_cast<u32>(snap.actionIdHi) << 16);
    return slot.lastAppliedState != rawState || slot.lastAppliedAnimId != snap.animId ||
           slot.lastAppliedVfx != snap.vfxFlags || slot.lastAppliedNozzle != snap.nozzleId ||
           slot.lastAppliedHealth != snap.health || slot.lastAppliedWater != snap.water ||
           slot.lastAppliedMovement != snap.movementState ||
           slot.fluddSwitchLatched || slot.hideSeekSeekerLook !=
               (isHideSeekActive() && isHideSeekSeekerSlot(snap.slot));
}

static bool listContains(JDrama::TViewObjPtrListT<JDrama::TViewObj> *group, const void *obj) {
    if (!group || !obj)
        return false;
    for (auto it = group->mViewObjList.begin(); it != group->mViewObjList.end(); ++it) {
        if (static_cast<const void *>(*it) == obj)
            return true;
    }
    return false;
}

static bool isPlayerGroupName(const char *name) {
    return name && strcmp(name, kPlayerGroupName) == 0;
}

// Locate the stage's player group by name and sanity-check that it actually
// contains the local Mario before trusting it.
static JDrama::TViewObjPtrListT<JDrama::TViewObj> *findPlayerGroup() {
    JDrama::TNameRefGen *gen = JDrama::TNameRefGen::getInstance();
    if (!gen)
        return nullptr;

    JDrama::TNameRef *ref = gen->getNameRef(kPlayerGroupName);
    if (!ref) {
        JDrama::TNameRef *root = gen->getRootNameRef();
        if (root)
            ref = root->search(kPlayerGroupName);
    }
    if (!ref)
        return nullptr;

    auto *group = reinterpret_cast<JDrama::TViewObjPtrListT<JDrama::TViewObj> *>(ref);
    if (!listContains(group, gpMarioAddress)) {
        OSReport("[SMSO] Player group found but local Mario not inside, ignoring\n");
        return nullptr;
    }
    return group;
}

static JKRHeap *resolveRemoteHeapParent() {
    if (JKRHeap::sRootHeap)
        return JKRHeap::sRootHeap;
    if (JKRHeap::sSystemHeap)
        return JKRHeap::sSystemHeap;
    if (JKRHeap::sCurrentHeap)
        return JKRHeap::sCurrentHeap;
    return nullptr;
}

static bool tryCreateExpandedMem1RemoteHeap();

// ---- Extended MEM1 (Dolphin 48 MiB) CPU mapping ------------------------------
//
// Retail SMS only configures CPU BATs for the stock 24 MiB, so guest addresses at
// 0x81800000+ fault even though Dolphin backs 48 MiB of host RAM (JKRHeap::
// mMemorySize reports it). Writing a JKRExpHeap header there with no mapping
// instantly resets the console (proven in dolphin.log).
//
// Fix: map a pack+puppet arena at 0x81810000 (ABOVE retail arenaHi / mailbox)
// with TWO non-overlapping BATs when possible:
//   DBAT0 32 MiB (0x80000000..0x82000000) + DBAT2 16 MiB (0x82000000..0x83000000)
// covering ~23.9 MiB through 0x82FF0000. A 30 MiB heap at 0x81000000 dual-BAT
// mapped and probed OK but froze stage load — it overlapped the live root heap.
// Proven fallback: widen DBAT2 8→16 MiB and use 7.5 MiB @ 0x81810000.
// Physical = EA - 0x80000000. Sentinel-probe before trusting. NEVER carve a
// tiny stage-child heap that then OOMs during pack/prewarm — soft-fail instead.
// SPRs: DBATnU/L = 536+2n / 537+2n.

static u32 readDBATU(int idx) {
    u32 v = 0;
    switch (idx) {
    case 0: asm volatile("mfspr %0, 536" : "=r"(v)); break;
    case 1: asm volatile("mfspr %0, 538" : "=r"(v)); break;
    case 2: asm volatile("mfspr %0, 540" : "=r"(v)); break;
    case 3: asm volatile("mfspr %0, 542" : "=r"(v)); break;
    default: break;
    }
    return v;
}

static u32 readDBATL(int idx) {
    u32 v = 0;
    switch (idx) {
    case 0: asm volatile("mfspr %0, 537" : "=r"(v)); break;
    case 1: asm volatile("mfspr %0, 539" : "=r"(v)); break;
    case 2: asm volatile("mfspr %0, 541" : "=r"(v)); break;
    case 3: asm volatile("mfspr %0, 543" : "=r"(v)); break;
    default: break;
    }
    return v;
}

// DBAT2/DBAT3 are the usual application-writable slots. DBAT0 is the main RAM
// BAT (0x80000000); we only enlarge it (never retarget) so the extra Dolphin
// MEM1 above 24 MiB becomes CPU-reachable for the pack+puppet arena.
static void writeDBAT(int idx, u32 upper, u32 lower) {
    switch (idx) {
    case 0:
        asm volatile("mtspr 537, %0; mtspr 536, %1; isync" : : "r"(lower), "r"(upper) : "memory");
        break;
    case 2:
        asm volatile("mtspr 541, %0; mtspr 540, %1; isync" : : "r"(lower), "r"(upper) : "memory");
        break;
    case 3:
        asm volatile("mtspr 543, %0; mtspr 542, %1; isync" : : "r"(lower), "r"(upper) : "memory");
        break;
    default:
        break;
    }
}

static bool dbatCovers(u32 upper, u32 base, u32 end) {
    if ((upper & 0x3u) == 0)
        return false; // neither Vs nor Vp set -> invalid
    const u32 bl = (upper >> 2) & 0x7FFu;
    const u32 blockSize = (bl + 1u) << 17; // BL counts 128 KiB units
    const u32 blockStart = (upper & 0xFFFE0000u) & ~(blockSize - 1u);
    const u32 blockEnd = blockStart + blockSize;
    return base >= blockStart && end <= blockEnd;
}

// Grow an existing cached-RAM DBAT by repeatedly doubling until it covers
// [regionStart, regionEnd), or until the next double would break alignment /
// exceed Dolphin's 48 MiB MEM1. Returns true only when the region is covered.
// When widening DBAT0 past 0x81000000, invalidate DBAT2 first — overlapping
// BATs on Gekko are undefined and crash Dolphin.
static bool tryWidenDbatToCover(int idx, u32 regionStart, u32 regionEnd) {
    if (idx != 0 && idx != 2 && idx != 3)
        return false;

    u32 upper = readDBATU(idx);
    const u32 lower = readDBATL(idx);
    if ((upper & 0x3u) == 0)
        return false;

    const u32 bepi = upper & 0xFFFE0000u;
    const u32 brpn = lower & 0xFFFE0000u;
    if (brpn != bepi - 0x80000000u)
        return false;
    if ((lower & 0x10u) != 0) // I (cache-inhibit) set -> not cached RAM
        return false;
    // Block must start at or below the arena.
    if (bepi > regionStart)
        return false;

    u32 bl = (upper >> 2) & 0x7FFu;
    u32 blockSize = (bl + 1u) << 17;
    if (dbatCovers(upper, regionStart, regionEnd))
        return true;

    const u32 origUpper = upper;
    const u32 origSize = blockSize;
    bool widened = false;
    while (bepi + blockSize < regionEnd) {
        const u32 newBlockSize = blockSize << 1;
        const u32 newBl = (newBlockSize >> 17) - 1u;
        if (newBl > 0x7FFu)
            break;
        if (bepi & (newBlockSize - 1u))
            break; // next size not naturally aligned (e.g. DBAT2 @ 0x81000000 → 32 MiB)
        // Refuse to map past Dolphin's 48 MiB MEM1 ceiling (0x83000000).
        // 64 MiB from 0x80000000 would end at 0x84000000 — blocked here.
        if (bepi + newBlockSize > kMem1CachedEnd)
            break;

        // Before DBAT0 swallows the DBAT2 window, clear DBAT2 so the ranges
        // never overlap (Gekko undefined behavior / Dolphin crash).
        if (idx == 0) {
            const u32 newEnd = bepi + newBlockSize;
            for (int other = 2; other <= 3; ++other) {
                const u32 ou = readDBATU(other);
                if ((ou & 0x3u) == 0)
                    continue;
                const u32 obl = (ou >> 2) & 0x7FFu;
                const u32 oSize = (obl + 1u) << 17;
                const u32 oStart = ou & 0xFFFE0000u;
                const u32 oEnd = oStart + oSize;
                // Only invalidate cached-RAM BATs that would overlap the new range.
                const u32 ol = readDBATL(other);
                if ((ol & 0x10u) != 0)
                    continue;
                if (oStart < newEnd && oEnd > bepi) {
                    writeDBAT(other, ou & ~0x3u, ol); // clear Vs/Vp
                    OSReport("[SMSO] Invalidated overlapping DBAT%d before widening DBAT0\n",
                             other);
                }
            }
        }

        upper = bepi | (newBl << 2) | 0x3u;
        writeDBAT(idx, upper, lower);
        blockSize = newBlockSize;
        widened = true;
    }

    if (widened) {
        OSReport("[SMSO] Widened DBAT%d %08X->%08X (%u->%u MiB) toward 0x%08X..0x%08X\n", idx,
                 origUpper, readDBATU(idx), origSize >> 20, blockSize >> 20, regionStart, regionEnd);
    }
    return dbatCovers(readDBATU(idx), regionStart, regionEnd);
}

// True when every byte of [base, end) is covered by at least one live DBAT
// (union of ranges). Needed because the 30 MiB arena spans DBAT0 (32 MiB) and
// DBAT2 (16 MiB) — no single BAT can cover it under the 48 MiB MEM1 ceiling.
static bool dbatsCoverRegion(u32 base, u32 end) {
    if (end <= base)
        return true;
    u32 cursor = base;
    while (cursor < end) {
        bool advanced = false;
        for (int i = 0; i < 4; ++i) {
            const u32 upper = readDBATU(i);
            if ((upper & 0x3u) == 0)
                continue;
            const u32 bl = (upper >> 2) & 0x7FFu;
            const u32 blockSize = (bl + 1u) << 17;
            const u32 blockStart = (upper & 0xFFFE0000u) & ~(blockSize - 1u);
            const u32 blockEnd = blockStart + blockSize;
            if (cursor >= blockStart && cursor < blockEnd) {
                cursor = blockEnd;
                advanced = true;
                break;
            }
        }
        if (!advanced)
            return false;
    }
    return true;
}

// Install a free application BAT (DBAT2/3) as a 16 MiB cached-RAM window at
// 0x82000000 so the upper half of the dual-BAT arena is reachable after DBAT0
// has been widened to 32 MiB (0x80000000..0x82000000).
static bool tryInstallUpperMem1Bat() {
    int slot = -1;
    if ((readDBATU(2) & 0x3u) == 0)
        slot = 2;
    else if ((readDBATU(3) & 0x3u) == 0)
        slot = 3;
    else {
        // Prefer reclaiming DBAT2 if it still holds the retail 8 MiB window
        // that we invalidated (or a stale partial widen). Overwriting a free
        // or already-invalidated slot is safest.
        const u32 ou = readDBATU(2);
        const u32 ol = readDBATL(2);
        if ((ol & 0x10u) == 0) {
            writeDBAT(2, ou & ~0x3u, ol);
            slot = 2;
            OSReport("[SMSO] Cleared DBAT2 to reclaim for upper MEM1 window\n");
        }
    }
    if (slot < 0) {
        OSReport("[SMSO] Upper MEM1 BAT unavailable (DBAT2/3 busy)\n");
        return false;
    }

    // 16 MiB @ 0x82000000 → phys 0x02000000. BL for 16 MiB = (16MiB/128KiB)-1 = 0x7F.
    const u32 upper = 0x820001FFu; // 0x82000000 | BL(16 MiB) | Vs | Vp
    const u32 lower = 0x02000002u; // phys 0x02000000 | WIMG cached | PP r/w
    writeDBAT(slot, upper, lower);
    OSReport("[SMSO] Installed DBAT%d = %08X %08X (16 MiB 0x82000000 -> phys 0x02000000)\n", slot,
             readDBATU(slot), readDBATL(slot));
    return dbatCovers(readDBATU(slot), kMem1DualBatSplit, kMem1CachedEnd);
}

// Probe [start, end) at start / mid / end-4. Mid uses the dual-BAT seam when
// the region crosses 0x82000000 so both BATs are exercised.
static bool probeMappedRegion(u32 regionStart, u32 regionEnd) {
    if (regionEnd <= regionStart + 8)
        return false;

    volatile u32 *p0 = reinterpret_cast<volatile u32 *>(regionStart);
    volatile u32 *p1 = reinterpret_cast<volatile u32 *>(regionEnd - 4);
    u32 midAddr = regionStart + ((regionEnd - regionStart) / 2u);
    if (regionStart < kMem1DualBatSplit && regionEnd > kMem1DualBatSplit)
        midAddr = kMem1DualBatSplit;
    volatile u32 *pMid = reinterpret_cast<volatile u32 *>(midAddr);

    *p0 = 0x5A5AA5A5u;
    *pMid = 0x3C3CC3C3u;
    *p1 = 0xA5A55A5Au;
    const bool ok = (*p0 == 0x5A5AA5A5u) && (*pMid == 0x3C3CC3C3u) && (*p1 == 0xA5A55A5Au);
    OSReport("[SMSO] Extended MEM1 probe 0x%08X/0x%08X/0x%08X -> %s\n", regionStart, midAddr,
             regionEnd - 4, ok ? "OK" : "FAIL");
    return ok;
}

// Preferred mapping for the ~24 MiB arena above arenaHi:
//   1) Widen DBAT0 16→32 MiB (covers 0x80000000..0x82000000, invalidates DBAT2)
//   2) Install DBAT2 as 16 MiB at 0x82000000 (covers 0x82000000..0x83000000)
// Arena 0x81810000..0x82FF0000 is then covered by the union of both BATs.
// Fallback: widen DBAT2 8→16 MiB for the proven 7.5 MiB window only.
static bool tryMapExpandedArena(u32 regionStart, u32 regionEnd) {
    // Step 1: get DBAT0 to cover at least through 0x82000000 (lower half).
    if (tryWidenDbatToCover(0, regionStart, kMem1DualBatSplit)) {
        // DBAT0 alone covers any arena that ends at/before 0x82000000 (7.5 MiB
        // fallback). Larger arenas need the upper BAT.
        if (dbatsCoverRegion(regionStart, regionEnd))
            return true;
        if (tryInstallUpperMem1Bat() && dbatsCoverRegion(regionStart, regionEnd))
            return true;
        OSReport("[SMSO] Dual-BAT upper window failed; trying DBAT2-only fallback\n");
    } else {
        OSReport("[SMSO] DBAT0 could not cover 0x%08X..0x%08X for expanded arena\n", regionStart,
                 kMem1DualBatSplit);
    }

    // Step 2: proven DBAT2-only path for the smaller fallback window.
    // If DBAT0 widen invalidated DBAT2, restore the retail 8 MiB cached window
    // at 0x81000000 so we can widen it 8→16 MiB again.
    if ((readDBATU(2) & 0x3u) == 0) {
        writeDBAT(2, 0x810000FFu, 0x01000002u); // retail-like 8 MiB @ 0x81000000
        OSReport("[SMSO] Restored retail DBAT2 8 MiB window for fallback widen\n");
    }
    if (tryWidenDbatToCover(2, regionStart, regionEnd))
        return true;
    if (tryWidenDbatToCover(3, regionStart, regionEnd))
        return true;
    return false;
}

static bool ensureExtendedMem1Mapping(u32 regionStart, u32 regionEnd) {
    if (gExtendedMappingReady && gExtendedMappedEnd >= regionEnd &&
        dbatsCoverRegion(regionStart, regionEnd))
        return true;

    for (int i = 0; i < 4; ++i)
        OSReport("[SMSO] DBAT%d = %08X %08X\n", i, readDBATU(i), readDBATL(i));

    bool alreadyCovered = dbatsCoverRegion(regionStart, regionEnd);
    if (alreadyCovered)
        OSReport("[SMSO] Extended MEM1 already covered by DBAT union — probing\n");

    bool mapped = alreadyCovered || tryMapExpandedArena(regionStart, regionEnd);
    if (!mapped) {
        OSReport("[SMSO] Extended MEM1 mapping failed for arena 0x%08X..0x%08X\n", regionStart,
                 regionEnd);
        return false;
    }

    OSReport("[SMSO] Extended MEM1 arena mapped 0x%08X..0x%08X (%u KiB)\n", regionStart, regionEnd,
             (regionEnd - regionStart) >> 10);

    if (!probeMappedRegion(regionStart, regionEnd))
        return false;

    gExtendedMappingReady = true;
    if (regionEnd > gExtendedMappedEnd)
        gExtendedMappedEnd = regionEnd;
    return true;
}

static bool tryCreateExpandedMem1RemoteHeap() {
    if (gRemoteActorHeapOwned && gRemoteActorHeap)
        return true;
    if (gExpandedHeapFailed)
        return false;

    const u32 memorySize = static_cast<u32>(JKRHeap::mMemorySize);
    if (memorySize < kMinMem1ForExpandedHeap) {
        gExpandedHeapFailed = true;
        OSReport("[SMSO] Expanded MEM1 heap skipped: memSize=%u (<%u, no extra RAM)\n", memorySize,
                 kMinMem1ForExpandedHeap);
        return false;
    }

    // Refuse to place the arena inside the live retail root heap / mailbox.
    // arenaHi is typically ~0x817fe4c0; mailbox is 0x817FC000.
    const u32 userRamEnd = reinterpret_cast<u32>(JKRHeap::mUserRamEnd);
    if (kRemoteActorExpandedHeapAddress < 0x81800000u ||
        (userRamEnd != 0 && kRemoteActorExpandedHeapAddress < userRamEnd)) {
        gExpandedHeapFailed = true;
        OSReport("[SMSO] Expanded MEM1 arena 0x%08X overlaps retail RAM end 0x%08X — refused\n",
                 kRemoteActorExpandedHeapAddress, userRamEnd);
        return false;
    }

    static const size_t kSizes[] = {kRemoteActorExpandedHeapSize, kRemoteActorExpandedHeapSizeFallback};
    for (size_t i = 0; i < sizeof(kSizes) / sizeof(kSizes[0]); ++i) {
        const size_t size = kSizes[i];
        const u32 regionStart = kRemoteActorExpandedHeapAddress;
        const u32 regionEnd = regionStart + static_cast<u32>(size);
        if (regionEnd > kMem1CachedEnd)
            continue;

        if (!ensureExtendedMem1Mapping(regionStart, regionEnd)) {
            OSReport("[SMSO] Expanded MEM1 map/probe failed for size=%u — trying smaller\n",
                     static_cast<u32>(size));
            continue;
        }

        JKRHeap *parent = resolveRemoteHeapParent();
        const size_t packSize =
            size == kRemoteActorExpandedHeapSize ? kRemotePackHeapSize
                                                 : kRemotePackHeapSizeFallback;
        if (packSize >= size || size - packSize < kRemoteBodySpawnMinFree) {
            OSReport("[SMSO] Expanded MEM1 split invalid total=%u pack=%u\n",
                     static_cast<u32>(size), static_cast<u32>(packSize));
            continue;
        }

        // Construct two non-overlapping JKR heaps directly over the mapped arena.
        // Neither heap allocates storage from the parent; parent is used only for
        // JKR disposer ownership. Destroy body first, then pack, at arena teardown.
        JKRExpHeap *packHeap =
            JKRExpHeap::create(reinterpret_cast<void *>(regionStart), packSize, parent, false);
        JKRExpHeap *bodyHeap = nullptr;
        if (packHeap) {
            bodyHeap = JKRExpHeap::create(
                reinterpret_cast<void *>(regionStart + static_cast<u32>(packSize)),
                size - packSize, parent, false);
        }
        if (!packHeap || !bodyHeap) {
            OSReport("[SMSO] Expanded MEM1 split heap create FAILED @ 0x%08X total=%u "
                     "pack=%u parent=%p\n",
                     regionStart, static_cast<u32>(size), static_cast<u32>(packSize), parent);
            if (bodyHeap)
                bodyHeap->destroy();
            if (packHeap)
                packHeap->destroy();
            continue;
        }

        gRemoteActorPackHeap = packHeap;
        gRemoteActorHeap = bodyHeap;
        gRemoteActorHeapOwned = true;
        gRemoteActorPackHeapCapacity = static_cast<u32>(packSize);
        gRemoteActorHeapCapacity = static_cast<u32>(size - packSize);
        OSReport("[SMSO] Remote arena split pack=%p/%u (%u free) body=%p/%u (%u free) "
                 "memSize=%u\n",
                 gRemoteActorPackHeap, gRemoteActorPackHeapCapacity,
                 static_cast<u32>(gRemoteActorPackHeap->getTotalFreeSize()),
                 gRemoteActorHeap, gRemoteActorHeapCapacity,
                 static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()), memorySize);
        return true;
    }

    gExpandedHeapFailed = true;
    OSReport("[SMSO] Expanded MEM1 heap unavailable; soft-fail (no stage-child carve) "
             "memSize=%u\n",
             memorySize);
    return false;
}

static bool tryUpgradeToOwnedRemoteHeap() {
    if (gRemoteActorHeapOwned)
        return gRemoteActorHeap != nullptr;

    // Only the expanded-MEM1 arena is safe for packs + body prewarm. Carving a
    // dedicated/stage-child heap from the live stage parent has aborted stage
    // load (JKRHeap.cpp:694) after leaving ~1.8 MiB — never do that.
    return tryCreateExpandedMem1RemoteHeap();
}

static bool ensureRemoteActorHeap() {
    if (gRemoteActorHeap)
        return true;

    if (tryCreateExpandedMem1RemoteHeap())
        return true;

    // Soft-fail path: no remote heap. Packs fall back to retail; body prewarm
    // skips. Stage load must survive — do NOT borrow/carve the stage heap.
    OSReport("[SMSO] Remote actor heap unavailable — soft-fail (packs=retail, "
             "bodies=deferred/limited)\n");
    return false;
}

static void destroyRemoteActorHeap() {
    if (!gRemoteActorHeap && !gRemoteActorPackHeap)
        return;

    // Drop ping-pong children before the parent body heap is destroyed.
    for (u32 i = 0; i < kBodyPingPongArenaCount; ++i) {
        if (!gBodyPingPongArenas[i])
            continue;
        gBodyPingPongArenas[i]->freeAll();
        gBodyPingPongArenas[i]->destroy();
        gBodyPingPongArenas[i] = nullptr;
    }

    if (gRemoteActorHeapOwned) {
        OSReport("[SMSO] Remote split heaps destroy body=%p free=%u pack=%p free=%u\n",
                 gRemoteActorHeap,
                 gRemoteActorHeap ? static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()) : 0,
                 gRemoteActorPackHeap,
                 gRemoteActorPackHeap
                     ? static_cast<u32>(gRemoteActorPackHeap->getTotalFreeSize())
                     : 0);
        if (gRemoteActorHeap)
            static_cast<JKRExpHeap *>(gRemoteActorHeap)->destroy();
        if (gRemoteActorPackHeap)
            static_cast<JKRExpHeap *>(gRemoteActorPackHeap)->destroy();
    } else {
        OSReport("[SMSO] Remote actor heap released borrowed heap @ %p\n", gRemoteActorHeap);
    }
    gRemoteActorHeap = nullptr;
    gRemoteActorPackHeap = nullptr;
    gRemoteActorHeapOwned = false;
    gRemoteActorHeapCapacity = 0;
    gRemoteActorPackHeapCapacity = 0;
    gExpandedHeapFailed = false;
}

static bool modelIdsMatch(const char a[8], const char b[8]) {
    return memcmp(a, b, 8) == 0;
}

constexpr bool modelGenerationCanCommit(u32 desiredGeneration, u32 readyGeneration,
                                        bool readyBodyComplete) {
    return readyBodyComplete && desiredGeneration != 0 &&
           desiredGeneration == readyGeneration;
}
static_assert(modelGenerationCanCommit(7, 7, true));
static_assert(!modelGenerationCanCommit(8, 7, true));
static_assert(!modelGenerationCanCommit(7, 7, false));

static void clearRemoteModelPreparationState(u32 slotId) {
    if (slotId >= MAX_REMOTE_SLOTS)
        return;
    gBodyPreparingGeneration[slotId] = 0;
    memset(gBodyPreparingModelIds[slotId], 0, MARIO_MODEL_ID_SIZE);
    gBodyReadyGeneration[slotId] = 0;
    memset(gBodyReadyModelIds[slotId], 0, MARIO_MODEL_ID_SIZE);
}

static u32 refreshRemoteModelRequest(u32 slotId) {
    if (slotId >= MAX_REMOTE_SLOTS)
        return 0;
    char desired[MARIO_MODEL_ID_SIZE] = {};
    smso::readMarioModelIdForSlot(slotId, desired);
    if (!modelIdsMatch(gBodyRequestedModelIds[slotId], desired)) {
        memcpy(gBodyRequestedModelIds[slotId], desired, MARIO_MODEL_ID_SIZE);
        ++gBodyModelRequestGeneration[slotId];
        if (gBodyModelRequestGeneration[slotId] == 0)
            ++gBodyModelRequestGeneration[slotId];
        gBodyModelApplied[slotId] = false;
        gBodyAppliedGeneration[slotId] = 0;
        clearRemoteModelPreparationState(slotId);
        gBodyRetailGraceFrames[slotId] = 0;
        gBodyModelRetryCooldown[slotId] = 0;
    }
    return gBodyModelRequestGeneration[slotId];
}

static void beginRemoteModelRequest(u32 slotId) {
    if (slotId >= MAX_REMOTE_SLOTS)
        return;
    smso::readMarioModelIdForSlot(slotId, gBodyRequestedModelIds[slotId]);
    ++gBodyModelRequestGeneration[slotId];
    if (gBodyModelRequestGeneration[slotId] == 0)
        ++gBodyModelRequestGeneration[slotId];
    gBodyModelApplied[slotId] = false;
    gBodyAppliedGeneration[slotId] = 0;
    clearRemoteModelPreparationState(slotId);
    gBodyRetailGraceFrames[slotId] = 0;
    gBodyModelRetryCooldown[slotId] = 0;
}

static bool remoteModelRequestStillCurrent(u32 slotId,
                                           const char desired[MARIO_MODEL_ID_SIZE],
                                           u32 generation) {
    if (slotId >= MAX_REMOTE_SLOTS ||
        generation != gBodyModelRequestGeneration[slotId])
        return false;
    char live[MARIO_MODEL_ID_SIZE] = {};
    smso::readMarioModelIdForSlot(slotId, live);
    return modelIdsMatch(live, desired) &&
           modelIdsMatch(gBodyRequestedModelIds[slotId], desired);
}

static void formatModelIdStr(char out[9], const char id[8]) {
    u32 n = 0;
    for (; n < 8 && id[n]; ++n)
        out[n] = id[n];
    out[n] = '\0';
}

static void destroyRemoteBodyArena(JKRExpHeap *&arena) {
    if (!arena)
        return;
    // Ping-pong arenas are lifetime-owned by the module — reset in place only.
    for (u32 i = 0; i < kBodyPingPongArenaCount; ++i) {
        if (gBodyPingPongArenas[i] == arena) {
            arena->freeAll();
            arena = nullptr; // drop alias; ExpHeap object stays in gBodyPingPongArenas
            return;
        }
    }
    arena->freeAll();
    arena->destroy();
    arena = nullptr;
}

static bool arenaHasLiveOwner(const JKRExpHeap *arena) {
    if (!arena)
        return false;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (gBodyPoolArenas[i] == arena)
            return true;
    }
    return false;
}

// Tear down a parked TMario graph before freeAll. Prefer the real destructor so
// FLUDD/cap/Yoshi/J3D subsystems release engine registrations; freeAll alone
// leaves dangling manager pointers that crash on address reuse.
static void teardownRemoteBodyGraph(TMario *body) {
    if (!body)
        return;
    smso::releaseMarioTexAnims(body);
    // Clear interaction / accessory aliases before ~TMario so any hooked
    // paths that still observe this pointer cannot chase into half-destroyed state.
    body->mHeldObject = nullptr;
    body->mGrabTarget = nullptr;
    body->mHolder = nullptr;
    body->mSurfGesso = nullptr;
    body->mSurfGessoID = 0;
    body->mController = nullptr;
    body->~TMario();
}

// Park a main-heap (arena == nullptr) TMario as a permanent mid-stage spare.
// Never scrub-and-forget these graphs: FLUDD/cap/shadow/J3D stay engine-registered
// until stage-boundary heap recycle. Returns false if the spare table is full
// (caller must keep the pointer in ready/variant — never ~TMario mid-stage).
static bool parkMainHeapBodySpare(TMario *body) {
    if (!body)
        return true;
    for (u32 i = 0; i < kMainHeapParkedSpareCapacity; ++i) {
        if (gMainHeapParkedSpares[i] == body)
            return true;
    }
    for (u32 i = 0; i < kMainHeapParkedSpareCapacity; ++i) {
        if (gMainHeapParkedSpares[i])
            continue;
        detachBodyBeforeReclaim(body);
        gMainHeapParkedSpares[i] = body;
        OSReport("[SMSO] Parked main-heap body %p as mid-stage spare[%u]\n", body, i);
        return true;
    }
    return false;
}

// Keep spare pointers module-owned across stage keep-alive; only hide them.
static void hideMainHeapParkedSpares() {
    for (u32 i = 0; i < kMainHeapParkedSpareCapacity; ++i) {
        if (gMainHeapParkedSpares[i])
            parkRemoteBody(gMainHeapParkedSpares[i]);
    }
}

// Stage-boundary / heap destroy: ~TMario then drop the spare table.
static void destroyMainHeapParkedSpares() {
    for (u32 i = 0; i < kMainHeapParkedSpareCapacity; ++i) {
        TMario *body = gMainHeapParkedSpares[i];
        if (!body)
            continue;
        gMainHeapParkedSpares[i] = nullptr;
        teardownRemoteBodyGraph(body);
    }
}

// freeAll() invalidates the parked TMario*. Custom bodies retain TexBinding
// entries keyed by that pointer — release + destroy before the arena dies or
// the next spawn can reuse the address and inherit stale MActor/BTK state.
// Ping-pong arenas are freeAll'd in place (never destroy mid-stage). Live pool
// arenas must never reach this path while still indexed by gBodyPoolArenas.
static void destroyRemoteBodyGraph(TMario *body, JKRExpHeap *&arena) {
    if (arena && arenaHasLiveOwner(arena)) {
        OSReport("[SMSO] Refusing freeAll of live pool arena %p body=%p\n", arena, body);
        if (body)
            smso::releaseMarioTexAnims(body);
        // Drop the cache alias only — live owner keeps the ExpHeap.
        arena = nullptr;
        return;
    }
    if (body)
        teardownRemoteBodyGraph(body);
    destroyRemoteBodyArena(arena);
}

static void removeBodyFromPlayerGroup(TMario *body) {
    if (!body || !gPlayerGroup)
        return;
    for (auto it = gPlayerGroup->mViewObjList.begin();
         it != gPlayerGroup->mViewObjList.end(); ++it) {
        if (*it == body) {
            gPlayerGroup->mViewObjList.erase(it);
            OSReport("[SMSO] Removed remote body %p from Player group before reclaim\n", body);
            return;
        }
    }
}

// Hard-detach a body before freeAll. Park alone is insufficient — perform-group
// membership and leftover interaction pointers can outlive the pointer-only
// commit that demoted a live graph into a variant/ready cache entry.
static void detachBodyBeforeReclaim(TMario *body) {
    if (!body)
        return;
    removeBodyFromViewList(body);
    removeBodyFromPlayerGroup(body);
    parkRemoteBody(body);
    body->mHeldObject = nullptr;
    body->mGrabTarget = nullptr;
    body->mHolder = nullptr;
    body->mSurfGesso = nullptr;
    body->mSurfGessoID = 0;
    body->mController = nullptr;
}

static void stampBodyReclaimDelay(RemoteBodyVariant &variant) {
    if (!variant.body)
        return;
    const u32 earliest = gBodyReclaimTick + kBodyGraphReclaimDelayTicks;
    if (variant.reclaimAfterTick < earliest)
        variant.reclaimAfterTick = earliest;
}

static void ensurePingPongArenas() {
    if (!gRemoteActorHeap || !gRemoteActorHeapOwned)
        return;
    for (u32 i = 0; i < kBodyPingPongArenaCount; ++i) {
        if (gBodyPingPongArenas[i])
            continue;
        if (gRemoteActorHeap->getFreeSize() < kRemoteBodyArenaBytes ||
            gRemoteActorHeap->getTotalFreeSize() < kRemoteBodyArenaBytes)
            break;
        gBodyPingPongArenas[i] =
            JKRExpHeap::create(kRemoteBodyArenaBytes, gRemoteActorHeap, false);
        if (gBodyPingPongArenas[i])
            OSReport("[SMSO] Ping-pong body arena[%u] ready @ %p (%u KiB)\n", i,
                     gBodyPingPongArenas[i],
                     static_cast<u32>(kRemoteBodyArenaBytes / 1024));
    }
}

static bool arenaIsReferencedByBodyCache(const JKRExpHeap *arena) {
    if (!arena)
        return false;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (gBodyPoolArenas[i] == arena)
            return true;
        if (gBodyVariants[i].arena == arena)
            return true;
    }
    for (const auto &ready : gReadyCustomBodies) {
        if (ready.arena == arena)
            return true;
    }
    return false;
}

static JKRExpHeap *acquireRemoteBodyArena() {
    if (!gRemoteActorHeap || !gRemoteActorHeapOwned)
        return nullptr;

    ensurePingPongArenas();

    // Prefer a ping-pong arena that is not referenced by live/ready/variant.
    // Never freeAll mid-stage: occupied graphs stay parked until stage recycle.
    // Unreferenced ping-pong slots are virgin (stage-init) or already empty.
    for (u32 i = 0; i < kBodyPingPongArenaCount; ++i) {
        JKRExpHeap *arena = gBodyPingPongArenas[i];
        if (!arena || arenaIsReferencedByBodyCache(arena))
            continue;
        return arena;
    }

    // Additional child ExpHeap while the body heap has room. Soft-defer (nullptr)
    // when RAM is exhausted — keep the current visible model and retry later.
    if (gRemoteActorHeap->getFreeSize() < kRemoteBodyArenaBytes ||
        gRemoteActorHeap->getTotalFreeSize() < kRemoteBodyArenaBytes)
        return nullptr;
    JKRExpHeap *created =
        JKRExpHeap::create(kRemoteBodyArenaBytes, gRemoteActorHeap, false);
    if (created)
        OSReport("[SMSO] Allocated overflow body arena @ %p (%u KiB; stage-only reclaim)\n",
                 created, static_cast<u32>(kRemoteBodyArenaBytes / 1024));
    return created;
}

static bool bodyIsOnRemoteViewList(const TMario *body) {
    if (!body || !gRemotePerformGroup)
        return false;
    for (auto it = gRemotePerformGroup->mViewObjList.begin();
         it != gRemotePerformGroup->mViewObjList.end(); ++it) {
        if (*it == body)
            return true;
    }
    return false;
}

// True when any live owner still depends on this pointer: pool slot, actor
// binding, or perform-group view list. Parked ready/variant entries are not
// "live owners" — reclaim may target those after tex release.
static bool bodyPointerIsLiveOwner(const TMario *body) {
    if (!body)
        return false;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (gBodyPool[i] == body)
            return true;
        if (gActors[i].body == body)
            return true;
    }
    return bodyIsOnRemoteViewList(body);
}

// Duplicate ready/variant refs to one TMario* must never freeAll the graph.
static bool bodyPointerHeldInOtherCaches(const TMario *body,
                                         const RemoteBodyVariant *self) {
    if (!body)
        return false;
    for (auto &ready : gReadyCustomBodies) {
        if (&ready != self && ready.body == body)
            return true;
    }
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (&gBodyVariants[i] != self && gBodyVariants[i].body == body)
            return true;
    }
    return false;
}

// Drop a stale cache entry that aliases a live (or otherwise owned) body without
// freeAll — the surviving owner keeps the arena.
static void scrubStaleBodyCacheEntry(RemoteBodyVariant &entry) {
    if (!entry.body && !entry.arena)
        return;
    OSReport("[SMSO] Scrubbed stale body cache entry body=%p arena=%p (no freeAll)\n",
             entry.body, entry.arena);
    entry = {};
}

static bool modelIdHasOutstandingRequest(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!id || marioModelIdIsEmpty(id))
        return false;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (modelIdsMatch(gBodyRequestedModelIds[i], id) ||
            modelIdsMatch(gBodyPreparingModelIds[i], id) ||
            modelIdsMatch(gBodyReadyModelIds[i], id))
            return true;
    }
    return false;
}

// Forward decls used by spawn (definitions follow hasBodyForModelId).
static bool isModelDesiredByRemote(const char id[MARIO_MODEL_ID_SIZE]);

static TMario *spawnRemoteBody(u32 archiveSlot, bool bindPack,
                               const char *explicitCachedModelId = nullptr,
                               RemoteBodyVariant *outVariant = nullptr) {
    if (!ensureRemoteActorHeap())
        return nullptr;

    if (!gRemoteActorHeapOwned) {
        if (gRemoteActorHeap->getTotalFreeSize() < kRemoteBodySpawnMinFree) {
            gRemoteActorHeap = nullptr;
            tryUpgradeToOwnedRemoteHeap();
        }
        if (!gRemoteActorHeap && !ensureRemoteActorHeap())
            return nullptr;
        if (!gRemoteActorHeapOwned && gRemoteActorHeap &&
            gRemoteActorHeap->getTotalFreeSize() < kRemoteBodySpawnMinFree &&
            countSpawnedBodies() >= kSessionMaxRemotes - 1) {
            gRemoteActorHeap = nullptr;
            tryCreateExpandedMem1RemoteHeap();
        }
        if (!gRemoteActorHeap && !ensureRemoteActorHeap())
            return nullptr;
    }

    if (gRemoteActorHeap->getTotalFreeSize() < kRemoteBodySpawnMinFree) {
        if (!gReportedHeapShortage) {
            OSReport("[SMSO] Remote Mario body spawn skipped: heap free=%u below safe minimum=%u owned=%d\n",
                     static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()),
                     static_cast<u32>(kRemoteBodySpawnMinFree), gRemoteActorHeapOwned ? 1 : 0);
            gReportedHeapShortage = true;
        }
        return nullptr;
    }

    // Bind / remount this slot's pack BEFORE constructing the body so initValues
    // resolves BMD/BTK from the correct archive. Prefer pack-cache remount;
    // SMSLoadArchive only on cache miss (see syncRemoteMarioArchiveSlot).
    // Prewarm passes bindPack=false so unique lobby packs cannot starve later
    // body slots — first-residency applies custom models once bodies exist.
    if (!explicitCachedModelId && bindPack && archiveSlot < MAX_REMOTE_SLOTS)
        smso::syncRemoteMarioArchiveSlot(archiveSlot);

    // SMSCoop pattern: mount this network slot's character archive before
    // initValues so BMD/BTK resolve from the correct pack, then restore local.
    // Soft-fail: if remount fails, keep retail mounted and still spawn a body.
    bool mountedCustom = false;
    char mountedModelId[MARIO_MODEL_ID_SIZE] = {};
    if (explicitCachedModelId) {
        memcpy(mountedModelId, explicitCachedModelId, MARIO_MODEL_ID_SIZE);
        const bool mounted = smso::mountCachedMarioModelPack(explicitCachedModelId);
        mountedCustom = mounted && !marioModelIdIsEmpty(explicitCachedModelId);
        if (!mounted) {
            OSReport("[SMSO] Ready-body cached archive remount failed — prewarm skipped\n");
            return nullptr;
        }
    } else if (bindPack) {
        // syncRemoteMarioArchiveSlot above snapshots the live id into this slot.
        // Capture that identity before initValues: the bridge may publish a new
        // intent while construction runs, but the completed graph still belongs
        // to the archive that was actually mounted here.
        smso::readMarioModelIdForSlot(archiveSlot, mountedModelId);
        if (!smso::setActiveMarioArchive(archiveSlot)) {
            OSReport("[SMSO] Remote body slot=%u archive remount failed — spawning with retail\n",
                     archiveSlot);
            memset(mountedModelId, 0, MARIO_MODEL_ID_SIZE);
        } else
            mountedCustom = smso::marioSlotHasCustomPack(archiveSlot);
    } else {
        // Force retail for pool prewarm so body count is deterministic and pack
        // loads cannot starve later slots. First-residency applies custom packs.
        // Skip remount when already on retail — avoids custom↔retail thrash when
        // the local player is also on a custom Shadow pack.
        if (!smso::mountRetailMarioArchive()) {
            OSReport("[SMSO] Remote body slot=%u retail mount failed during prewarm\n",
                     archiveSlot);
        }
    }

    // Allocate only after every fallible archive bind. Previously a failed
    // ready-pack remount returned after allocating TMario and silently leaked
    // that object from the body heap.
    JKRExpHeap *bodyArena = nullptr;
    JKRHeap *allocHeap = gRemoteActorHeap;
    if (outVariant && explicitCachedModelId) {
        // Replacement / ready graphs allocate into child arenas so live
        // gBodyPool pointers stay allocation-stable. Arenas are never freeAll'd
        // mid-stage — only stage-boundary recycle returns their RAM.
        bodyArena = acquireRemoteBodyArena();
        if (!bodyArena) {
            gRemoteHeapRecycleOnStageExit = true;
            OSReport("[SMSO] Soft-defer ready body: body heap cannot allocate arena "
                     "(keep visible model; stage recycle recovers RAM)\n");
            smso::restoreLocalMarioArchiveGuarded();
            return nullptr;
        }
        allocHeap = bodyArena;
    }

    JKRHeap *previousHeap = JKRHeap::sCurrentHeap;
    allocHeap->becomeCurrentHeap();
    auto *body = new (allocHeap, 4) TMario();
    if (!body) {
        if (bodyArena)
            destroyRemoteBodyArena(bodyArena);
        if (previousHeap)
            previousHeap->becomeCurrentHeap();
        smso::restoreLocalMarioArchiveGuarded();
        return nullptr;
    }

    // TMario::initValues fully builds the puppet: it internally calls
    // initModel() and creates the cap, FLUDD, Yoshi, effects, and shadow
    // body (see decomp MarioInit.cpp). Do NOT call initModel separately.
    // initMirrorModel() duplicates a reflection rig and can leave a second FLUDD
    // pack visible on network puppets — skip it for remotes.
    // Mark the shared heavy-work budget before initValues: even a soft failure
    // after this point has paid the expensive construction cost.
    gBodyConstructedSinceActorUpdate = true;
    gHeavyPreparationCooldown = kHeavyPreparationSpacingFrames;
    const OSTime constructionStart = OSGetTime();
    body->initValues();
    const u32 constructionMs =
        static_cast<u32>(OSTicksToMilliseconds(OSGetTime() - constructionStart));
    ++gModelBuildCount;
    gModelBuildMilliseconds += constructionMs;

    // Bind custom BTKs while this slot's pack is still mounted — getGlbResource
    // resolves /mario/btk/* from the active volume. Remounting local first would
    // miss Shadow Mario/Luigi BTKs that only exist in the remote pack.
    // Allocate AnmData/MActors on the remote heap (same lifetime as pool bodies)
    // or system heap — NEVER the stage heap. Stage teardown frees stage allocs
    // while keep-alive pool bodies retain J3D MaterialAnm pointers → UAF crash
    // when more remotes draw after the next stage enter.
    // Texture-animation actors are part of the body graph and must share the
    // body heap lifetime. Never spill them into system/stage memory.
    JKRHeap *texHeap = allocHeap;
    if (texHeap)
        texHeap->becomeCurrentHeap();
    else if (previousHeap)
        previousHeap->becomeCurrentHeap();

    smso::rebindMarioTexAnimsForSlot(
        body, explicitCachedModelId ? static_cast<u8>(0xFF) : static_cast<u8>(archiveSlot));

    if (!smso::restoreLocalMarioArchiveGuarded()) {
        OSReport("[SMSO] Remote body slot=%u post-spawn local restore failed\n", archiveSlot);
    }
    // Do NOT call rebindLocalMarioPlayerInfo / setPlayerInfo here. Archive
    // remount only swaps the mario RARC for getGlbResource — it does not
    // invalidate MSound's stored &mTranslation / &mSpeed / anmMtx(1) pointers
    // (those are TMario members + the live local J3DModel). setPlayerInfo
    // recreates MSRandPlay cry / exert-cont / water-wait entries; calling it
    // once per pool spawn (9x on stage prewarm, again on first-residency
    // rebuild) resets those timers and produces a spurious Mario SE on stage
    // load / when remotes spawn nearby. Rebind only after local initModel
    // rebuild (new joint matrices) — see rebuildLocalMarioVisuals.

    if (previousHeap)
        previousHeap->becomeCurrentHeap();

    body->changeHand(0);
    ensureCapOnHead(body);

    body->mController = nullptr;
    body->mHeldObject = nullptr;
    body->mGrabTarget = nullptr;
    body->mHolder = nullptr;
    body->mSurfGesso = nullptr;
    body->mSurfGessoID = 0;
    configureRemoteMarioCollision(body, 0xFF);
    body->mAttributes.mIsInvisible = false;
    body->mAttributes.mIsGameOver = false;
    body->mAttributes.mHasFludd = true;
    applyRemoteCosmetics(body, 0);
    if (!isHideSeekActive())
        applyRemoteShirtVisibility(body);
    setBodyVisible(body, false);

    const bool hideCaps = mountedCustom &&
                          smso::marioModelIdWantsHiddenCaps(mountedModelId);

    if (outVariant && explicitCachedModelId) {
        outVariant->body = body;
        outVariant->arena = bodyArena;
        memcpy(outVariant->modelId, mountedModelId, MARIO_MODEL_ID_SIZE);
        outVariant->isCustom = mountedCustom;
        outVariant->hideCaps = hideCaps;
        outVariant->ownerSlot = 0xFF;
        outVariant->generation = 0;
    } else if (archiveSlot < MAX_REMOTE_SLOTS) {
        if (bindPack) {
            gBodyPoolIsCustom[archiveSlot] = mountedCustom;
            // Stamp the archive used for construction, not a potentially newer
            // mailbox id observed after initValues.
            if (mountedCustom)
                memcpy(gBodyPoolModelIds[archiveSlot], mountedModelId,
                       MARIO_MODEL_ID_SIZE);
            else
                memset(gBodyPoolModelIds[archiveSlot], 0,
                       sizeof(gBodyPoolModelIds[archiveSlot]));
            gBodyPoolHideCaps[archiveSlot] = hideCaps;
        } else {
            memset(gBodyPoolModelIds[archiveSlot], 0, sizeof(gBodyPoolModelIds[archiveSlot]));
            gBodyPoolIsCustom[archiveSlot] = false;
            gBodyPoolHideCaps[archiveSlot] = false;
        }
    }

    char idStr[9] = {};
    if (explicitCachedModelId)
        formatModelIdStr(idStr, explicitCachedModelId);
    else if (archiveSlot < MAX_REMOTE_SLOTS)
        formatModelIdStr(idStr, gBodyPoolModelIds[archiveSlot]);
    OSReport("[SMSO] Remote Mario body spawned @ %p slot=%u model='%s' ready=%d "
             "buildMs=%u bodyHeapFree=%u totalFree=%u\n",
             body, archiveSlot, idStr, explicitCachedModelId ? 1 : 0,
             constructionMs,
             static_cast<u32>(gRemoteActorHeap->getFreeSize()),
             static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
    return body;
}

static TMario *spawnRemoteBody(u32 archiveSlot) { return spawnRemoteBody(archiveSlot, true); }

// Park an idle pool body well outside any playfield and hide it, so a reused
// body can never flash at a stale position for a frame before its first
// snapshot lands.
static void parkRemoteBody(TMario *body) {
    if (!body)
        return;
    setBodyVisible(body, false);
    body->mTranslation.x = 0.0f;
    body->mTranslation.y = -100000.0f;
    body->mTranslation.z = 0.0f;
    body->mSpeed.x = 0.0f;
    body->mSpeed.y = 0.0f;
    body->mSpeed.z = 0.0f;
    body->mSurfGesso = nullptr;
    body->mSurfGessoID = 0;
}

static bool isPoolBodyAssigned(const TMario *body) {
    if (!body)
        return false;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (gActors[i].spawned && gActors[i].body == body)
            return true;
    }
    return false;
}

// Pool bodies are indexed by network slot so each can be initValues()'d under
// that slot's character archive (custom models differ per player).
static TMario *acquirePoolBodyForSlot(u32 slotId) {
    if (slotId >= MAX_REMOTE_SLOTS)
        return nullptr;
    TMario *body = gBodyPool[slotId];
    if (body && !isPoolBodyAssigned(body))
        return body;

    // Baseline bodies are prewarmed into whichever network slots are available
    // before the roster is known. Move an unused retail spare to the joining
    // slot instead of paying TMario::initValues on first appearance.
    CommBuffer *buf = getCommBuffer();
    for (u32 source = 0; source < MAX_REMOTE_SLOTS; ++source) {
        if (source == slotId || !gBodyPool[source] ||
            isPoolBodyAssigned(gBodyPool[source]) || gBodyPoolIsCustom[source])
            continue;

        char announced[MARIO_MODEL_ID_SIZE] = {};
        smso::readMarioModelIdForSlot(source, announced);
        const bool sourceLikelyNeeded =
            buf && (buf->remoteSnapshots[source].connected != 0 ||
                    !marioModelIdIsEmpty(announced));
        if (sourceLikelyNeeded)
            continue;

        body = gBodyPool[source];
        gBodyPool[slotId] = body;
        memcpy(gBodyPoolModelIds[slotId], gBodyPoolModelIds[source],
               MARIO_MODEL_ID_SIZE);
        gBodyPoolIsCustom[slotId] = gBodyPoolIsCustom[source];
        gBodyPoolHideCaps[slotId] = gBodyPoolHideCaps[source];
        gBodyPoolArenas[slotId] = gBodyPoolArenas[source];

        gBodyPool[source] = nullptr;
        memset(gBodyPoolModelIds[source], 0, MARIO_MODEL_ID_SIZE);
        gBodyPoolIsCustom[source] = false;
        gBodyPoolHideCaps[source] = false;
        gBodyPoolArenas[source] = nullptr;
        return body;
    }
    return nullptr;
}

static bool hasPendingActiveModelUpgrade() {
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (!gActors[i].spawned || !gActors[i].body || gBodyModelApplied[i])
            continue;

        char desired[MARIO_MODEL_ID_SIZE] = {};
        smso::readMarioModelIdForSlot(i, desired);
        if (!modelIdsMatch(gBodyPoolModelIds[i], desired))
            return true;
        if (!marioModelIdIsEmpty(desired) && !gBodyPoolIsCustom[i])
            return true;
    }
    return false;
}

static bool isSafeModelPreparationIdleFrame(TMarDirector *director) {
    // Loading/file-select frames are ideal preparation windows. During normal
    // play, wait for the local player to settle so synchronous DVD/J3D work is
    // not inserted into active movement.
    if (!isStageReady(director) || !gpMarioAddress)
        return true;
    const f32 speedSq = gpMarioAddress->mSpeed.x * gpMarioAddress->mSpeed.x +
                        gpMarioAddress->mSpeed.y * gpMarioAddress->mSpeed.y +
                        gpMarioAddress->mSpeed.z * gpMarioAddress->mSpeed.z;
    return speedSq <= kPreparationIdleSpeedSq &&
           fabsf(gpMarioAddress->mForwardSpeed) <= 4.0f;
}

static bool hasValidRemoteWaitingForBody(const CommBuffer *buf) {
    if (!buf)
        return false;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (i == buf->localSlot || gActors[i].spawned)
            continue;
        const PlayerSnapshot &snap = buf->remoteSnapshots[i];
        if (!isValidSnapshot(snap) || !isSameStage(buf, snap))
            continue;
        if (acquirePoolBodyForSlot(i))
            continue;
        return true;
    }
    return false;
}

// Allocate at most one remote puppet body per call. Prefer spawning under an
// already-cached custom pack for that slot so first-residency skips rebuild.
// If the pack is not ready yet, spawn retail and let first-residency upgrade later.
static void prewarmRemoteBodyPoolStep() {
    if (gBodyPoolPrewarmComplete)
        return;
    if (!ensureRemoteActorHeap())
        return;

    CommBuffer *buf = getCommBuffer();
    const u8 localSlot = buf ? buf->localSlot : 0xFF;

    // Scan forward until we spawn one body or exhaust the slot range.
    while (gBodyPoolPrewarmIndex < MAX_REMOTE_SLOTS) {
        const u32 i = gBodyPoolPrewarmIndex;

        if (i == localSlot) {
            gBodyPool[i] = nullptr;
            ++gBodyPoolPrewarmIndex;
            continue;
        }

        if (gBodyPool[i]) {
            ++gBodyPoolPrewarmIndex;
            continue;
        }

        if (gBodyPoolCount >= kBaselinePrewarmBodies) {
            gBodyPoolPrewarmComplete = true;
            OSReport("[SMSO] Remote body pool prewarm complete: %u/%u bodies heapFree=%u owned=%d\n",
                     gBodyPoolCount, kBaselinePrewarmBodies,
                     gRemoteActorHeap ? static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()) : 0,
                     gRemoteActorHeapOwned ? 1 : 0);
            return;
        }

        // Prefer custom pack when already cached — avoids retail-prewarm → rebuild.
        bool bindPack = false;
        char desired[8] = {};
        smso::readMarioModelIdForSlot(i, desired);
        if (!marioModelIdIsEmpty(desired)) {
            if (smso::isMarioModelPackReadyForBodyInit(desired)) {
                bindPack = true;
            } else if (smso::isMarioModelPackCached(desired)) {
                // The DVD read just completed. Wait for the body-ready delay
                // instead of constructing retail now and custom again next frame.
                return;
            }
        }

        TMario *body = spawnRemoteBody(i, bindPack);
        if (!body) {
            // Retry this slot next frame (heap may free up / mapping may finish).
            OSReport("[SMSO] Remote body pool prewarm miss at slot %u (%u ready) — retry next frame\n",
                     i, gBodyPoolCount);
            return;
        }

        parkRemoteBody(body);
        gBodyPool[i] = body;
        ++gBodyPoolCount;
        ++gBodyPoolPrewarmIndex;

        OSReport("[SMSO] Remote body pool prewarm step: slot=%u bindPack=%d pool=%u/%u "
                 "heapFree=%u\n",
                 i, bindPack ? 1 : 0, gBodyPoolCount, kBaselinePrewarmBodies,
                 gRemoteActorHeap ? static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()) : 0);

        if (gBodyPoolCount >= kBaselinePrewarmBodies ||
            gBodyPoolPrewarmIndex >= MAX_REMOTE_SLOTS) {
            gBodyPoolPrewarmComplete = true;
            OSReport("[SMSO] Remote body pool prewarm complete: %u/%u bodies heapFree=%u owned=%d\n",
                     gBodyPoolCount, kBaselinePrewarmBodies,
                     gRemoteActorHeap ? static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()) : 0,
                     gRemoteActorHeapOwned ? 1 : 0);
        }
        // One spawn per frame — return even if more slots remain.
        return;
    }

    gBodyPoolPrewarmComplete = true;
    OSReport("[SMSO] Remote body pool prewarm complete: %u/%u bodies heapFree=%u owned=%d\n",
             gBodyPoolCount, kBaselinePrewarmBodies,
             gRemoteActorHeap ? static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()) : 0,
             gRemoteActorHeapOwned ? 1 : 0);
}

static bool hasBodyForModelId(const char id[MARIO_MODEL_ID_SIZE]) {
    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (gBodyPool[slot] && modelIdsMatch(gBodyPoolModelIds[slot], id) &&
            (marioModelIdIsEmpty(id) || gBodyPoolIsCustom[slot]))
            return true;
        const RemoteBodyVariant &variant = gBodyVariants[slot];
        if (variant.body && modelIdsMatch(variant.modelId, id) &&
            (marioModelIdIsEmpty(id) || variant.isCustom))
            return true;
    }
    for (const auto &ready : gReadyCustomBodies) {
        if (ready.body && modelIdsMatch(ready.modelId, id) &&
            (marioModelIdIsEmpty(id) || ready.isCustom))
            return true;
    }
    return false;
}

static bool isModelDesiredByRemote(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!id || marioModelIdIsEmpty(id))
        return false;
    CommBuffer *buf = getCommBuffer();
    if (!buf)
        return false;
    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (slot == buf->localSlot)
            continue;
        char desired[MARIO_MODEL_ID_SIZE] = {};
        smso::readMarioModelIdForSlot(slot, desired);
        if (modelIdsMatch(id, desired))
            return true;
    }
    return false;
}

static void prewarmReadyCustomBodyStep() {
    if (!gBodyPoolPrewarmComplete ||
        gRemoteActorHeapCapacity <
            kRemoteActorExpandedHeapSize - kRemotePackHeapSize)
        return;

    // Background look-ahead may occupy only the primary slot. Preserve the
    // second entry for an active request that supersedes a just-finished body.
    RemoteBodyVariant *empty =
        gReadyCustomBodies[0].body ? nullptr : &gReadyCustomBodies[0];
    if (!empty || !gRemoteActorHeap ||
        gRemoteActorHeap->getTotalFreeSize() < kRemoteBodySpawnMinFree)
        return;

    const u32 cacheCount = smso::marioModelPackCacheCount();
    for (u32 n = 0; n < cacheCount; ++n) {
        const u32 index = (gReadyCustomPrewarmCursor + n) % cacheCount;
        char id[MARIO_MODEL_ID_SIZE] = {};
        if (!smso::readMarioModelPackCacheId(index, id) ||
            !smso::isMarioModelPackReadyForBodyInit(id) ||
            !isModelDesiredByRemote(id) || hasBodyForModelId(id))
            continue;

        gReadyCustomPrewarmCursor = (index + 1) % cacheCount;
        RemoteBodyVariant ready{};
        TMario *body = spawnRemoteBody(0xFF, true, id, &ready);
        if (!body)
            return;
        parkRemoteBody(body);
        ready.readyDelay = kReadyBodyActivationDelayTicks;
        *empty = ready;
        char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
        formatModelIdStr(idStr, id);
        OSReport("[SMSO] Ready custom body prewarmed id='%s' body=%p heapFree=%u\n", idStr,
                 body, static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
        return;
    }
}

static void resetBodyPoolForFreshHeap() {
    gBodyPoolCount = 0;
    gBodyPoolPrewarmIndex = 0;
    gBodyPoolPrewarmComplete = false;
    gRemoteHeapRecycleOnStageExit = false;
    gReadyCustomPrewarmCursor = 0;
    destroyMainHeapParkedSpares();
    for (auto &ready : gReadyCustomBodies) {
        if (ready.body || ready.arena)
            destroyRemoteBodyGraph(ready.body, ready.arena);
        ready = {};
    }
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        // Clear live-owner index before teardown so destroyRemoteBodyGraph does
        // not refuse freeAll of this slot's arena.
        TMario *poolBody = gBodyPool[i];
        JKRExpHeap *poolArena = gBodyPoolArenas[i];
        gBodyPool[i] = nullptr;
        gBodyPoolArenas[i] = nullptr;
        if (poolBody || poolArena)
            destroyRemoteBodyGraph(poolBody, poolArena);
        memset(gBodyPoolModelIds[i], 0, sizeof(gBodyPoolModelIds[i]));
        gBodyPoolIsCustom[i] = false;
        gBodyPoolHideCaps[i] = false;
        if (gBodyVariants[i].body || gBodyVariants[i].arena)
            destroyRemoteBodyGraph(gBodyVariants[i].body, gBodyVariants[i].arena);
        gBodyVariants[i] = {};
        gBodyModelApplied[i] = false;
        gBodyModelRequestGeneration[i] = 0;
        memset(gBodyRequestedModelIds[i], 0, MARIO_MODEL_ID_SIZE);
        clearRemoteModelPreparationState(i);
        gBodyAppliedGeneration[i] = 0;
        gBodyRetailGraceFrames[i] = 0;
        gBodyModelRetryCooldown[i] = 0;
    }
    // Destroy fixed ping-pong ExpHeaps (already freeAll'd above when claimed).
    for (u32 i = 0; i < kBodyPingPongArenaCount; ++i) {
        if (!gBodyPingPongArenas[i])
            continue;
        gBodyPingPongArenas[i]->freeAll();
        gBodyPingPongArenas[i]->destroy();
        gBodyPingPongArenas[i] = nullptr;
    }
}

// After a keep-alive stage transition, park surviving bodies and clear residency
// flags so first-residency can re-validate (usually a no-rebuild cache hit).
static void prepareSurvivingBodyPoolForNewStage() {
    CommBuffer *buf = getCommBuffer();
    const u8 localSlot = buf ? buf->localSlot : 0xFF;

    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        if (i == localSlot) {
            // Local slot must not keep a remote pool body.
            if (gBodyPool[i]) {
                parkRemoteBody(gBodyPool[i]);
                // Arena-backed graphs can be reclaimed; main-heap prewarm bodies
                // stay allocated (abandoned) until heap destroy — rare localSlot change.
                TMario *localBody = gBodyPool[i];
                JKRExpHeap *localArena = gBodyPoolArenas[i];
                gBodyPool[i] = nullptr;
                gBodyPoolArenas[i] = nullptr;
                if (localArena)
                    destroyRemoteBodyGraph(localBody, localArena);
                if (gBodyPoolCount > 0)
                    --gBodyPoolCount;
            }
            memset(gBodyPoolModelIds[i], 0, sizeof(gBodyPoolModelIds[i]));
            gBodyPoolIsCustom[i] = false;
            gBodyPoolHideCaps[i] = false;
        } else if (gBodyPool[i]) {
            parkRemoteBody(gBodyPool[i]);
        }
        if (gBodyVariants[i].body)
            parkRemoteBody(gBodyVariants[i].body);
        gBodyModelApplied[i] = false;
        ++gBodyModelRequestGeneration[i];
        memset(gBodyRequestedModelIds[i], 0, MARIO_MODEL_ID_SIZE);
        clearRemoteModelPreparationState(i);
        gBodyAppliedGeneration[i] = 0;
        gBodyRetailGraceFrames[i] = 0;
        gBodyModelRetryCooldown[i] = 0;
    }
    for (auto &ready : gReadyCustomBodies) {
        if (ready.body)
            parkRemoteBody(ready.body);
    }
    hideMainHeapParkedSpares();

    // Resume staggered prewarm if the pool still has holes.
    if (gBodyPoolCount >= kBaselinePrewarmBodies) {
        gBodyPoolPrewarmComplete = true;
        gBodyPoolPrewarmIndex = MAX_REMOTE_SLOTS;
    } else {
        gBodyPoolPrewarmComplete = false;
        gBodyPoolPrewarmIndex = 0;
    }

    OSReport("[SMSO] Remote body pool kept across stage (%u bodies, prewarmComplete=%d)\n",
             gBodyPoolCount, gBodyPoolPrewarmComplete ? 1 : 0);
}

static bool reuseRemoteBodyVariant(u32 slotId, const char desired[MARIO_MODEL_ID_SIZE],
                                   u32 generation, TMario *&body,
                                   bool requireCurrentRequest = true) {
    if (slotId >= MAX_REMOTE_SLOTS || !body)
        return false;

    RemoteBodyVariant &variant = gBodyVariants[slotId];
    if (!variant.body || !modelIdsMatch(variant.modelId, desired))
        return false;
    if (requireCurrentRequest &&
        !remoteModelRequestStillCurrent(slotId, desired, generation))
        return false;

    TMario *previous = body;
    char previousId[MARIO_MODEL_ID_SIZE] = {};
    memcpy(previousId, gBodyPoolModelIds[slotId], sizeof(previousId));
    const bool previousCustom = gBodyPoolIsCustom[slotId];
    const bool previousHideCaps = gBodyPoolHideCaps[slotId];
    JKRExpHeap *previousArena = gBodyPoolArenas[slotId];

    // Detach before clearing the pool entry (see reuseReadyCustomBody).
    removeBodyFromViewList(previous);
    removeBodyFromPlayerGroup(previous);
    parkRemoteBody(previous);

    body = variant.body;
    gBodyPool[slotId] = body;
    memcpy(gBodyPoolModelIds[slotId], variant.modelId, sizeof(gBodyPoolModelIds[slotId]));
    gBodyPoolIsCustom[slotId] = variant.isCustom;
    gBodyPoolHideCaps[slotId] = variant.hideCaps;
    gBodyPoolArenas[slotId] = variant.arena;
    // Close the race where perform still observes slot.body == previous after
    // the pool pointer has already moved (previous is demoted, not retail).
    for (auto &actor : gActors) {
        if (actor.body == previous)
            actor.body = body;
    }

    if (previousArena) {
        variant.body = previous;
        variant.arena = previousArena;
        memcpy(variant.modelId, previousId, sizeof(variant.modelId));
        variant.isCustom = previousCustom;
        variant.hideCaps = previousHideCaps;
        variant.readyDelay = 0;
        variant.generation = 0;
        stampBodyReclaimDelay(variant);
    } else {
        // Main-heap prewarm: never ~TMario mid-stage. Prefer permanent spare;
        // if the spare table is full, keep as a parked variant for ownership.
        variant = {};
        if (!parkMainHeapBodySpare(previous)) {
            variant.body = previous;
            variant.arena = nullptr;
            memcpy(variant.modelId, previousId, sizeof(variant.modelId));
            variant.isCustom = previousCustom;
            variant.hideCaps = previousHideCaps;
            variant.readyDelay = 0;
            variant.generation = 0;
            stampBodyReclaimDelay(variant);
            OSReport("[SMSO] Main-heap body %p kept as variant (spare full; "
                     "stage-only reclaim)\n",
                     previous);
        }
    }

    gBodyModelApplied[slotId] = true;
    gBodyAppliedGeneration[slotId] = generation;
    clearRemoteModelPreparationState(slotId);
    gBodyRetailGraceFrames[slotId] = 0;
    gBodyModelRetryCooldown[slotId] = 0;

    char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
    formatModelIdStr(idStr, desired);
    OSReport("[SMSO] Remote body slot=%u reused cached model id='%s' @ %p "
             "(allocation-stable swap)\n",
             slotId, idStr, body);
    ++gModelPointerCommitCount;
    return true;
}

// Mid-stage no-op: TMario body graphs are never torn down until stage recycle.
// Returns false always so callers soft-defer instead of expecting freed RAM.
static bool reclaimInactiveBodyGraphs(bool forceNonDesired) {
    (void)forceNonDesired;
    static bool sLogged = false;
    if (!sLogged) {
        sLogged = true;
        OSReport("[SMSO] reclaimInactiveBodyGraphs: stage-only policy (no mid-stage "
                 "freeAll/~TMario)\n");
    }
    return false;
}

static bool ensureReadyCustomBodySlot(RemoteBodyVariant *&outTarget) {
    outTarget = nullptr;
    for (auto &entry : gReadyCustomBodies) {
        if (!entry.body) {
            outTarget = &entry;
            return true;
        }
    }
    // Never freeAll to vacate a slot — soft-defer until stage recycle or a
    // pointer-only commit vacates an entry naturally.
    (void)reclaimInactiveBodyGraphs(/*forceNonDesired=*/true);
    return false;
}

static void ensureVariantCapacityForCommit(u32 slotId) {
    // Per-slot variant is overwritten on commit; soft retention is advisory only.
    // Mid-stage never frees other slots' parked graphs to make room.
    (void)slotId;
    (void)kRecentBodyVariantCapacity;
}

static bool reuseReadyCustomBody(u32 slotId, const char desired[MARIO_MODEL_ID_SIZE],
                                 u32 generation, TMario *&body) {
    if (slotId >= MAX_REMOTE_SLOTS || !body)
        return false;

    // Exclusive ready claim: prefer this slot's reserved entry, else an unowned
    // (prewarm) entry. Never steal a body reserved for a different ownerSlot.
    RemoteBodyVariant *ready = nullptr;
    RemoteBodyVariant *unowned = nullptr;
    for (auto &entry : gReadyCustomBodies) {
        if (!entry.body || entry.readyDelay != 0)
            continue;
        if (!modelIdsMatch(entry.modelId, desired) ||
            (!marioModelIdIsEmpty(desired) && !entry.isCustom))
            continue;
        if (entry.ownerSlot == static_cast<u8>(slotId)) {
            ready = &entry;
            break;
        }
        if (entry.ownerSlot == 0xFF && !unowned)
            unowned = &entry;
    }
    if (!ready)
        ready = unowned;
    if (!ready)
        return false;
    if (!remoteModelRequestStillCurrent(slotId, desired, generation))
        return false;

    // Refuse double ownership: a ready entry must never alias a live pool/actor
    // body. Scrub the stale cache ref without freeAll (live owner keeps arena).
    if (bodyPointerIsLiveOwner(ready->body)) {
        OSReport("[SMSO] Ready body claim refused slot=%u: body %p already live\n",
                 slotId, ready->body);
        scrubStaleBodyCacheEntry(*ready);
        return false;
    }

    ensureVariantCapacityForCommit(slotId);

    // Reclaim may have invalidated the entry — re-resolve exclusively.
    ready = nullptr;
    unowned = nullptr;
    for (auto &entry : gReadyCustomBodies) {
        if (!entry.body || entry.readyDelay != 0)
            continue;
        if (!modelIdsMatch(entry.modelId, desired) ||
            (!marioModelIdIsEmpty(desired) && !entry.isCustom))
            continue;
        if (bodyPointerIsLiveOwner(entry.body)) {
            scrubStaleBodyCacheEntry(entry);
            continue;
        }
        if (entry.ownerSlot == static_cast<u8>(slotId)) {
            ready = &entry;
            break;
        }
        if (entry.ownerSlot == 0xFF && !unowned)
            unowned = &entry;
    }
    if (!ready)
        ready = unowned;
    if (!ready)
        return false;

    // A prepared graph is immutable for its model identity. If a rapid A→B→A→B
    // sequence made its original request generation stale, re-admit it only
    // after matching the current desired identity and restamp readiness.
    ready->ownerSlot = static_cast<u8>(slotId);
    ready->generation = generation;
    gBodyReadyGeneration[slotId] = generation;
    memcpy(gBodyReadyModelIds[slotId], desired, MARIO_MODEL_ID_SIZE);
    if (!modelGenerationCanCommit(gBodyModelRequestGeneration[slotId],
                                  gBodyReadyGeneration[slotId], ready->body != nullptr))
        return false;

    // Take exclusive ownership: remove from global ready BEFORE installing into
    // the pool so no other slot can claim the same pointer.
    const RemoteBodyVariant prepared = *ready;
    *ready = {};

    // Scrub any duplicate ready refs to the claimed pointer (no freeAll).
    for (auto &entry : gReadyCustomBodies) {
        if (entry.body == prepared.body)
            scrubStaleBodyCacheEntry(entry);
    }

    RemoteBodyVariant &variant = gBodyVariants[slotId];
    const RemoteBodyVariant displaced = variant;

    TMario *previous = body;
    JKRExpHeap *previousArena = gBodyPoolArenas[slotId];

    // Hard-detach the demoted live body BEFORE clearing gBodyPool so the hooked
    // perform path cannot observe a non-owned pointer still on the view list.
    removeBodyFromViewList(previous);
    removeBodyFromPlayerGroup(previous);
    parkRemoteBody(previous);

    if (previousArena) {
        variant.body = previous;
        variant.arena = previousArena;
        memcpy(variant.modelId, gBodyPoolModelIds[slotId], MARIO_MODEL_ID_SIZE);
        variant.isCustom = gBodyPoolIsCustom[slotId];
        variant.hideCaps = gBodyPoolHideCaps[slotId];
        variant.ownerSlot = static_cast<u8>(slotId);
        variant.generation = 0;
        variant.readyDelay = 0;
        stampBodyReclaimDelay(variant);
    } else {
        // Main-heap prewarm: permanent spare — never reclaimable mid-stage.
        variant = {};
        if (!parkMainHeapBodySpare(previous)) {
            variant.body = previous;
            variant.arena = nullptr;
            memcpy(variant.modelId, gBodyPoolModelIds[slotId], MARIO_MODEL_ID_SIZE);
            variant.isCustom = gBodyPoolIsCustom[slotId];
            variant.hideCaps = gBodyPoolHideCaps[slotId];
            variant.ownerSlot = static_cast<u8>(slotId);
            variant.generation = 0;
            variant.readyDelay = 0;
            stampBodyReclaimDelay(variant);
            OSReport("[SMSO] Main-heap body %p kept as variant on ready claim "
                     "(spare full; stage-only reclaim)\n",
                     previous);
        }
    }

    body = prepared.body;
    gBodyPool[slotId] = body;
    gBodyPoolArenas[slotId] = prepared.arena;
    memcpy(gBodyPoolModelIds[slotId], prepared.modelId, MARIO_MODEL_ID_SIZE);
    gBodyPoolIsCustom[slotId] = prepared.isCustom;
    gBodyPoolHideCaps[slotId] = prepared.hideCaps;
    // Atomic with pool install: demoted previous must not remain the actor body
    // while isRemoteBody(previous) is only true via the variant table.
    for (auto &actor : gActors) {
        if (actor.body == previous)
            actor.body = body;
    }
    smso::retargetMarioTexAnimsForSlot(body, static_cast<u8>(slotId));

    // Offer the displaced per-slot variant into the vacated ready slot only when
    // it is a distinct arena-backed parked graph (not already live elsewhere).
    // Main-heap displaced graphs go to the permanent spare table instead.
    if (displaced.body && displaced.body != previous && displaced.body != body &&
        !bodyPointerIsLiveOwner(displaced.body)) {
        if (displaced.arena) {
            *ready = displaced;
            ready->ownerSlot = 0xFF;
            ready->generation = 0;
            ready->readyDelay = 0;
            smso::retargetMarioTexAnimsForSlot(ready->body, 0xFF);
            removeBodyFromViewList(ready->body);
            parkRemoteBody(ready->body);
        } else if (parkMainHeapBodySpare(displaced.body)) {
            // parked in spare table
        } else {
            // Spare full: park in the vacated ready slot (never ~TMario mid-stage).
            *ready = displaced;
            ready->ownerSlot = 0xFF;
            ready->generation = 0;
            ready->readyDelay = 0;
            smso::retargetMarioTexAnimsForSlot(ready->body, 0xFF);
            removeBodyFromViewList(ready->body);
            parkRemoteBody(ready->body);
            OSReport("[SMSO] Main-heap displaced body %p parked in ready "
                     "(spare full; stage-only reclaim)\n",
                     displaced.body);
        }
    } else if (displaced.body && displaced.body != previous && displaced.body != body) {
        OSReport("[SMSO] Dropped displaced variant alias body=%p on claim slot=%u\n",
                 displaced.body, slotId);
    }
    parkRemoteBody(body);

    gBodyModelApplied[slotId] = true;
    gBodyAppliedGeneration[slotId] = generation;
    clearRemoteModelPreparationState(slotId);
    gBodyRetailGraceFrames[slotId] = 0;
    gBodyModelRetryCooldown[slotId] = 0;
    char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
    formatModelIdStr(idStr, desired);
    OSReport("[SMSO] Remote body slot=%u claimed ready model id='%s' @ %p "
             "(pointer-only commit; no mount/init/allocation)\n",
             slotId, idStr, body);
    ++gModelPointerCommitCount;
    return true;
}

static bool readyCustomBodyPendingForSlot(u32 slotId,
                                          const char desired[MARIO_MODEL_ID_SIZE]) {
    for (const auto &entry : gReadyCustomBodies) {
        if (!entry.body || !modelIdsMatch(entry.modelId, desired) ||
            (!marioModelIdIsEmpty(desired) && !entry.isCustom))
            continue;
        // Unowned prewarm or this slot's reserved entry satisfies the request.
        // Another slot's reserved ready is exclusive — do not treat it as ours.
        if (entry.ownerSlot == 0xFF || entry.ownerSlot == static_cast<u8>(slotId))
            return true;
    }
    return false;
}

static void queueRequestedReadyBody(u32 slotId,
                                    const char desired[MARIO_MODEL_ID_SIZE],
                                    u32 generation) {
    if (readyCustomBodyPendingForSlot(slotId, desired) ||
        !canConstructRemoteBodyThisUpdate() || !gRemoteActorHeap)
        return;

    // Soft RAM limit: never freeAll mid-stage to make room. Keep the live model
    // visible and retry when a pointer-only commit frees a cache slot or the
    // player warps (stage recycle recovers all body-heap arenas).
    if (gRemoteActorHeap->getTotalFreeSize() < kRemoteBodySpawnMinFree ||
        gRemoteActorHeap->getFreeSize() < kRemoteBodyArenaBytes) {
        gRemoteHeapRecycleOnStageExit = true;
        ++gModelBuildDeferredCount;
        OSReport("[SMSO] Soft-defer ready body queue slot=%u (body heap low; "
                 "stage-only reclaim)\n",
                 slotId);
        return;
    }

    // Committing a staged replacement parks the current active body. Soft
    // retention never permanently blocks — OOM soft-defer is the only stop.
    ensureVariantCapacityForCommit(slotId);

    RemoteBodyVariant *target = nullptr;
    if (!ensureReadyCustomBodySlot(target) || !target) {
        // Ready-cache full of parked graphs: keep live model, retry later.
        gRemoteHeapRecycleOnStageExit = true;
        ++gModelBuildDeferredCount;
        OSReport("[SMSO] Soft-defer ready body queue slot=%u (ready cache full; "
                 "no mid-stage freeAll)\n",
                 slotId);
        return;
    }

    gBodyPreparingGeneration[slotId] = generation;
    memcpy(gBodyPreparingModelIds[slotId], desired, MARIO_MODEL_ID_SIZE);
    RemoteBodyVariant ready{};
    TMario *prewarmed = spawnRemoteBody(0xFF, true, desired, &ready);
    if (!prewarmed) {
        gBodyPreparingGeneration[slotId] = 0;
        memset(gBodyPreparingModelIds[slotId], 0, MARIO_MODEL_ID_SIZE);
        ++gModelBuildDeferredCount;
        return;
    }
    parkRemoteBody(prewarmed);
    ready.readyDelay = kReadyBodyActivationDelayTicks;
    ready.ownerSlot = static_cast<u8>(slotId);
    ready.generation = generation;
    *target = ready;
    gBodyPreparingGeneration[slotId] = 0;
    memset(gBodyPreparingModelIds[slotId], 0, MARIO_MODEL_ID_SIZE);
    gBodyReadyGeneration[slotId] = generation;
    memcpy(gBodyReadyModelIds[slotId], desired, MARIO_MODEL_ID_SIZE);
    char idStr[MARIO_MODEL_ID_SIZE + 1] = {};
    formatModelIdStr(idStr, desired);
    OSReport("[SMSO] Requested ready body queued slot=%u gen=%u id='%s' body=%p "
             "(activation delayed%s)\n",
             slotId, generation, idStr, prewarmed,
             remoteModelRequestStillCurrent(slotId, desired, generation) ? "" : ", stale-cancelled");
}

static void prewarmRequestedCustomBodyStep() {
    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (!gActors[slot].spawned || !gActors[slot].body)
            continue;
        const u32 generation = refreshRemoteModelRequest(slot);
        if (gBodyModelApplied[slot])
            continue;
        char desired[MARIO_MODEL_ID_SIZE] = {};
        smso::readMarioModelIdForSlot(slot, desired);
        // Pointer-only commit paths already cover these identities — do not burn
        // the shared initValues budget rebuilding a graph that applyRemoteBody*
        // will freeze or swap without allocation (A↔B variant reuse, or an
        // already-correct pool body waiting to freeze).
        if (modelIdsMatch(gBodyPoolModelIds[slot], desired) &&
            (marioModelIdIsEmpty(desired) || gBodyPoolIsCustom[slot]))
            continue;
        const RemoteBodyVariant &variant = gBodyVariants[slot];
        if (variant.body && modelIdsMatch(variant.modelId, desired) &&
            (marioModelIdIsEmpty(desired) || variant.isCustom))
            continue;
        if (readyCustomBodyPendingForSlot(slot, desired) ||
            !smso::isMarioModelPackReadyForBodyInit(desired))
            continue;
        queueRequestedReadyBody(slot, desired, generation);
        return; // one TMario::initValues budget per update
    }
}

// First stage residency for a remote: ensure the pool body matches the live
// CommBuffer model id. Prewarm often builds under retail (empty id); late join
// with a custom pack must rebuild THAT slot once before the body appears.
// After a definitive apply, gBodyModelApplied freezes the visual until dismiss,
// stage exit, or requestRemoteMarioModelReapply. Soft-fail (pack missing / heap
// low / spawn fail) keeps the current visible body and retries with cooldown —
// never leave a remote invisible, and never permanently stick on retail when the
// pack later becomes available.
// Empty (retail) ids get a short grace window so a late CommBuffer write can still
// upgrade once; after grace, retail freezes like any other applied id.
static bool applyRemoteBodyModelOnFirstResidency(u32 slotId, TMario *&body) {
    if (slotId >= MAX_REMOTE_SLOTS || !body)
        return false;
    const u32 generation = refreshRemoteModelRequest(slotId);
    if (gBodyModelApplied[slotId])
        return true;

    if (gBodyModelRetryCooldown[slotId] > 0) {
        --gBodyModelRetryCooldown[slotId];
        return true;
    }

    char desired[8] = {};
    smso::readMarioModelIdForSlot(slotId, desired);

    char haveStr[9] = {};
    char wantStr[9] = {};
    formatModelIdStr(haveStr, gBodyPoolModelIds[slotId]);
    formatModelIdStr(wantStr, desired);

    if (modelIdsMatch(gBodyPoolModelIds[slotId], desired)) {
        if (!marioModelIdIsEmpty(desired)) {
            // Never SMSLoadArchive on the visibility path — wait for prefetch.
            if (!smso::isMarioModelPackReadyForBodyInit(desired)) {
                gBodyModelRetryCooldown[slotId] = kRemoteModelPendingRetryFrames;
                return true;
            }
            smso::syncRemoteMarioArchiveSlot(slotId);
            const bool packReady = smso::marioSlotHasCustomPack(slotId);
            if (packReady && gBodyPoolIsCustom[slotId]) {
                gBodyModelApplied[slotId] = true;
                gBodyAppliedGeneration[slotId] = generation;
                clearRemoteModelPreparationState(slotId);
                gBodyRetailGraceFrames[slotId] = 0;
                gBodyModelRetryCooldown[slotId] = 0;
                OSReport("[SMSO] Remote body slot=%u first-residency model ok id='%s' (no rebuild)\n",
                         slotId, wantStr);
                return true;
            }
            if (!packReady) {
                OSReport("[SMSO] Remote body slot=%u first-residency id='%s' pack still "
                         "unavailable — keep retail, retry in %u frames\n",
                         slotId, wantStr, kRemoteModelRetryCooldownFrames);
                gBodyModelRetryCooldown[slotId] = kRemoteModelRetryCooldownFrames;
                return true;
            }
            // Pack is ready but the live body was built under retail — fall through
            // to rebuild under the custom archive.
            OSReport("[SMSO] Remote body slot=%u first-residency pack ready for '%s' — "
                     "rebuilding retail body\n",
                     slotId, wantStr);
        } else {
            // Retail match: wait briefly for a late-arriving custom id, then freeze.
            if (gBodyRetailGraceFrames[slotId] < 255)
                ++gBodyRetailGraceFrames[slotId];
            if (gBodyRetailGraceFrames[slotId] >= kRemoteModelRetailGraceFrames) {
                gBodyModelApplied[slotId] = true;
                gBodyAppliedGeneration[slotId] = generation;
                clearRemoteModelPreparationState(slotId);
                OSReport("[SMSO] Remote body slot=%u first-residency retail frozen "
                         "(grace=%u, no custom id)\n",
                         slotId, gBodyRetailGraceFrames[slotId]);
            }
            return true;
        }
    }

    // Repeated toggles should not consume more heap. A superseded body retains
    // its complete model/FLUDD/cap graph and can be swapped back safely.
    if (reuseRemoteBodyVariant(slotId, desired, generation, body))
        return true;
    if (reuseReadyCustomBody(slotId, desired, generation, body))
        return true;
    // All model changes, including custom->retail, prepare off the activation
    // path. Keep the old body fully visible until a generation-matching body is
    // complete; this path performs no archive I/O, initValues, or allocation.
    gBodyModelRetryCooldown[slotId] = kRemoteModelPendingRetryFrames;
    return true;
}

static bool ensureRemoteBody(RemoteActorSlot &slot, u32 slotId) {
    if (slot.spawned && slot.body) {
        // Still within first-residency grace (retail pending custom id) — recheck.
        if (slotId < MAX_REMOTE_SLOTS && !gBodyModelApplied[slotId]) {
            TMario *prev = slot.body;
            TMario *body = prev;
            applyRemoteBodyModelOnFirstResidency(slotId, body);
            if (body != prev) {
                // Swap the live view-list entry so the rebuilt body is drawn.
                // Demotion paths already remove `prev`; always push the new body.
                if (slot.inViewList && gRemotePerformGroup) {
                    removeBodyFromViewList(prev);
                    if (!bodyIsOnRemoteViewList(body)) {
                        gRemotePerformGroup->mViewObjList.push_back(body);
                        ++gRemotePerformBodyCount;
                    }
                }
                parkRemoteBody(prev);
                slot.body = body;
                resetRemoteRuntimeState(slot);
                OSReport("[SMSO] Remote Mario body slot %u swapped to rebuilt @ %p\n", slotId,
                         body);
            }
        }
        return true;
    }

    TMario *body = acquirePoolBodyForSlot(slotId);
    if (!body) {
        // Lazy spawn path (prewarm missed this slot). Prefer cache-only bind —
        // if the custom pack is not cached yet, spawn retail and let prefetch +
        // first-residency upgrade on a later frame (never load+init same frame).
        char desired[8] = {};
        const u32 requestedGeneration = refreshRemoteModelRequest(slotId);
        memcpy(desired, gBodyRequestedModelIds[slotId], MARIO_MODEL_ID_SIZE);
        const bool packCached =
            marioModelIdIsEmpty(desired) || smso::isMarioModelPackReadyForBodyInit(desired);
        if (packCached && !marioModelIdIsEmpty(desired))
            smso::syncRemoteMarioArchiveSlot(slotId);
        if (!canConstructRemoteBodyThisUpdate())
            return false;
        body = spawnRemoteBody(slotId, packCached && !marioModelIdIsEmpty(desired));
        if (body) {
            parkRemoteBody(body);
            if (slotId < MAX_REMOTE_SLOTS && !gBodyPool[slotId]) {
                gBodyPool[slotId] = body;
                ++gBodyPoolCount;
            }
            if (slotId < MAX_REMOTE_SLOTS) {
                const u32 currentGeneration = refreshRemoteModelRequest(slotId);
                // Freeze immediately when we spawned under a known custom pack;
                // empty / retail-pending stays in grace so a late write can rebuild.
                if (currentGeneration == requestedGeneration &&
                    modelIdsMatch(gBodyPoolModelIds[slotId], desired) &&
                    !marioModelIdIsEmpty(desired) &&
                    gBodyPoolIsCustom[slotId]) {
                    gBodyModelApplied[slotId] = true;
                    gBodyAppliedGeneration[slotId] = currentGeneration;
                    clearRemoteModelPreparationState(slotId);
                    gBodyRetailGraceFrames[slotId] = 0;
                } else {
                    gBodyModelApplied[slotId] = false;
                    gBodyRetailGraceFrames[slotId] = 0;
                }
            }
        }
    } else if (slotId < MAX_REMOTE_SLOTS) {
        char desired[MARIO_MODEL_ID_SIZE] = {};
        smso::readMarioModelIdForSlot(slotId, desired);
        if (modelIdsMatch(gBodyPoolModelIds[slotId], desired)) {
            applyRemoteBodyModelOnFirstResidency(slotId, body);
        } else if (!gBodyPoolIsCustom[slotId]) {
            // First-visible priority: assign the already-constructed retail
            // body now. The active-slot path upgrades it atomically on a later
            // budgeted frame instead of blocking first draw on custom init.
            gBodyModelApplied[slotId] = false;
            gBodyModelRetryCooldown[slotId] = 0;
        } else {
            // Never flash another player's stale custom model. A cached retail
            // variant is safe to activate immediately when available.
            const char retailId[MARIO_MODEL_ID_SIZE] = {};
            const u32 generation = refreshRemoteModelRequest(slotId);
            if (reuseRemoteBodyVariant(slotId, retailId, generation, body, false)) {
                gBodyModelApplied[slotId] = false;
                gBodyAppliedGeneration[slotId] = 0;
                gBodyModelRetryCooldown[slotId] = 0;
            } else {
                applyRemoteBodyModelOnFirstResidency(slotId, body);
                if (!modelIdsMatch(gBodyPoolModelIds[slotId], desired))
                    return false;
            }
        }
    }

    if (!body) {
        if (!gReportedBodyCap) {
            OSReport("[SMSO] Remote body unavailable for slot %u (pool=%u)\n", slotId, gBodyPoolCount);
            gReportedBodyCap = true;
        }
        return false;
    }

    slot = {};
    slot.spawned = true;
    slot.body = body;
    resetRemoteRuntimeState(slot);
    gReportedHeapShortage = false;
    return true;
}

static void removeBodyFromViewList(TMario *body) {
    if (!gRemotePerformGroup || !body)
        return;

    for (auto it = gRemotePerformGroup->mViewObjList.begin();
         it != gRemotePerformGroup->mViewObjList.end(); ++it) {
        if (*it == body) {
            gRemotePerformGroup->mViewObjList.erase(it);
            if (gRemotePerformBodyCount > 0)
                --gRemotePerformBodyCount;
            return;
        }
    }
}

static void hideRemoteBody(RemoteActorSlot &slot) {
    if (slot.body && slot.visible) {
        setBodyVisible(slot.body, false);
        slot.visible = false;
    }
}

static void dismissRemoteBody(u8 slotIndex, RemoteActorSlot &slot) {
    const bool wasActive = slot.visible || slot.inViewList;
    hideRemoteBody(slot);
    if (slot.inViewList && slot.body) {
        removeBodyFromViewList(slot.body);
        slot.inViewList = false;
    }

    // The body stays allocated in the pool; just unbind it from this slot and
    // park it so acquirePoolBody() can hand it to the next player who needs one.
    // Clear the apply-once freeze so a later first residency (rejoin / late join
    // after leave) can rebuild if the CommBuffer id differs from the pool body.
    // Keep TexAnim bindings on parked Shadow bodies — MActors live on the remote
    // heap with the body, and updateAllMarioTexAnims skips unassigned remotes so
    // they will not remount packs while parked. releaseMarioTexAnims only on
    // rebuild abandon (body never drawn again).
    if (slot.body) {
        parkRemoteBody(slot.body);
        if (wasActive)
            OSReport("[SMSO] Remote Mario body released to pool @ %p\n", slot.body);
    }
    if (slotIndex < MAX_REMOTE_SLOTS) {
        gBodyModelApplied[slotIndex] = false;
        ++gBodyModelRequestGeneration[slotIndex];
        memset(gBodyRequestedModelIds[slotIndex], 0, MARIO_MODEL_ID_SIZE);
        gBodyRetailGraceFrames[slotIndex] = 0;
        gBodyModelRetryCooldown[slotIndex] = 0;
    }

    slot.spawned = false;
    slot.body = nullptr;
    smso::clearRemoteCarriedFruit(slotIndex);
    resetRemoteRuntimeState(slot);
    resetRemoteMarioVoiceSlot(slotIndex);
}

static void applySnapshotToBody(RemoteActorSlot &slot, const PlayerSnapshot &snap) {
    TMario *body = slot.body;
    if (!body)
        return;

    slot.yaw = resolveSnapshotYaw(snap);
    slot.rosterSlot = snap.slot;
    const bool seekerLook = isHideSeekActive() && isHideSeekSeekerSlot(snap.slot);
    if (seekerLook != slot.hideSeekSeekerLook) {
        applyHideSeekPlayerCosmetics(body, seekerLook, true);
        playHideSeekSeekerCosmeticVfx(body);
    }
    slot.hideSeekSeekerLook = seekerLook;
    slot.hideSeekSeekerLookWas = seekerLook;
    slot.vfxFlags = snap.vfxFlags;
    slot.nozzleId = snap.nozzleId;

    const u32 rawState = static_cast<u32>(snap.actionId) |
                         (static_cast<u32>(snap.actionIdHi) << 16);
    const bool incomingWarp = isRemoteWarpTransitionState(rawState);
    const bool hostOnYoshi = snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags);
    const bool tongueTipOffset =
        hostOnYoshi && yoshiTongueIsActive(unpackYoshiTongueState(snap.health));
    const bool heavyDirty = snapshotHeavyDirty(slot, snap);
    slot.visualStateDirty = slot.visualStateDirty || heavyDirty;

    if (!incomingWarp) {
        slot.targetPos = snap.position;
        if (!tongueTipOffset)
            slot.targetVel = snap.velocity;
        slot.targetRotY = snap.rotationY;

        const f32 dx = snap.position.x - slot.displayPos.x;
        const f32 dy = snap.position.y - slot.displayPos.y;
        const f32 dz = snap.position.z - slot.displayPos.z;
        const f32 dist = sqrtf(dx * dx + dy * dy + dz * dz);
        if (!slot.displayMotionInit || dist > kRemoteMotionSnapDistance)
            hardSnapRemoteDisplayMotion(slot, snap.position,
                                        tongueTipOffset ? slot.targetVel : snap.velocity,
                                        snap.rotationY);

        slot.inWarpTransition = false;
        // Defer final visibility to updateRemoteActors (grace / appear / warp).
        // Forcing visible here raced grace suppress and was overwritten later —
        // but perform can still run between apply and that final write on some
        // orderings, so never enable during seeker grace.
        if (slot.appearRevealFrames == 0 &&
            !shouldSuppressRemoteHiderFromSeekerGrace(networkSlotOf(&slot)))
            setBodyVisible(body, true);
        else
            setBodyVisible(body, false);
    } else {
        slot.inWarpTransition = true;
        setBodyVisible(body, false);
    }

    body->mPrevState = body->mState;
    if (!smso::isRemoteShineCollectActive(snap.slot))
        body->mState = sanitizeRemoteState(rawState);

    const u8 netUpper = unpackUpperState(snap.movementState);
    u8 upperState = netUpper;
    // Native Mario's UPPER_STATE_PUMPING path reads mGamePad. Remote puppets do
    // not have a gamepad, so use HOLDING_PUMP for the same FLUDD pose without
    // the input-dependent pump-rate path.
    if (upperState == kUpperStatePumping)
        upperState = kUpperStateHoldingPump;
    body->mFluddUsageState = upperState;

    const bool showFluddOnMarioBack = (snap.vfxFlags & VFX_NO_FLUDD) == 0;

    // Heavy systems: only when snapshot meaningfully changed (or first frames).
    if (heavyDirty || hostOnYoshi) {
        syncRemoteYoshiFromSnapshot(body, slot.yoshi, snap);
        applyRemoteFluddPresence(body, showFluddOnMarioBack, hostOnYoshi);
    }

    if (!smso::isRemoteShineCollectActive(snap.slot))
        syncRemoteAnimation(body, &slot, snap, netUpper);
    syncRemoteHeadWaist(body, slot, snap);
    syncRemoteAnimAux(body, body->mFludd, snap.health, showFluddOnMarioBack);
    applyRemoteFacing(body, slot.yaw, snap.animId, body->mState, &slot);

    if (heavyDirty) {
        const bool isDead = (snap.vfxFlags & VFX_DEAD) != 0;
        smso::applyRemoteBlooperSurfSnapshot(body, slot.surf, snap);
        (void)isDead;

        applyRemoteCosmetics(body, snap.slot);
        if (!isHideSeekActive())
            applyRemoteShirtVisibility(body);
        applyRemoteYCamHelmet(body, snap.vfxFlags, slot.wasYCam);
        configureRemoteMarioCollision(body, snap.slot);
    }

    const bool surfing = !(snap.vfxFlags & VFX_DEAD) && smso::snapshotIsBlooperSurfing(snap);

    if (showFluddOnMarioBack && body->mFludd) {
        slot.lastHealth = snap.health;
        const bool hostSwitching = (snap.vfxFlags & VFX_NOZZLE_SWITCHING) != 0;
        const u8 targetNozzle = clampNozzleId(unpackCurrentNozzle(snap.nozzleId));
        const bool nozzleMismatch =
            !hostSwitching && !slot.fluddSwitchLatched &&
            body->mFludd->mCurrentNozzle != targetNozzle;
        const bool needFluddSync = heavyDirty || snapshotAnimChanged(slot, snap) || hostSwitching ||
                                   slot.fluddSwitchLatched || nozzleMismatch;
        if (needFluddSync) {
            if (!hostSwitching && slot.lastMovementState != 0xFF) {
                const f32 prevProgress = unpackFluddSwitchProgress(slot.lastMovementState);
                const f32 newProgress = unpackFluddSwitchProgress(snap.movementState);
                if (newProgress != prevProgress && newProgress > 0.0f && newProgress < 1.0f) {
                    slot.fluddSwitchLatched = true;
                    slot.fluddTowardSpray = newProgress < prevProgress;
                }
            }

            syncRemoteFluddState(slot, body->mFludd, snap.nozzleId, snap.movementState, snap.vfxFlags,
                                 netUpper);
            slot.lastMovementState = snap.movementState;
            slot.lastNozzleId = snap.nozzleId;
            slot.lastVfxFlags = snap.vfxFlags;
            slot.lastHealth = snap.health;
        }

        // Spray pressure must sync whenever VFX_WATER_SPRAY is set — including
        // Y-cam (C-up). Previously Y-cam skipped this block so syncedSprayPressure
        // stayed 0 → nozzle->_378 stayed 0 → visualEmitNozzleDeform emitted nothing.
        const bool sprayingWater = (snap.vfxFlags & VFX_WATER_SPRAY) != 0;
        const bool drySpray = (snap.vfxFlags & VFX_FLUDD_EMPTY) != 0;
        const bool yCam = (snap.vfxFlags & VFX_Y_CAM) != 0;

        if (sprayingWater) {
            slot.syncedSprayPressure = snap.water;
            if (slot.lastWaterTank > 0)
                body->mFludd->mCurrentWater = slot.lastWaterTank;
            else if (body->mFludd->mCurrentWater <= 0) {
                // Mid-spray join / never saw a tank byte — keep emit path alive.
                TNozzleBase *n = body->mFludd->mNozzleList[body->mFludd->mCurrentNozzle];
                if (n)
                    body->mFludd->mCurrentWater = n->mEmitParams.mAmountMax.get();
            }
        } else if (drySpray) {
            slot.lastWaterTank = 0;
            slot.syncedSprayPressure = 0;
            body->mFludd->mCurrentWater = 0;
        } else if (yCam) {
            body->mFludd->mCurrentWater = slot.lastWaterTank;
        } else if (!surfing && netUpper > kUpperStateHoldingPump) {
            slot.lastWaterTank = snap.water;
            body->mFludd->mCurrentWater = snap.water;
        } else {
            body->mFludd->mCurrentWater = slot.lastWaterTank;
        }
    }

    if (heavyDirty)
        syncRemoteParticleEdges(body, slot, snap);

    slot.lastAppliedState = rawState;
    slot.lastAppliedAnimId = snap.animId;
    slot.lastAppliedVfx = snap.vfxFlags;
    slot.lastAppliedNozzle = snap.nozzleId;
    slot.lastAppliedHealth = snap.health;
    slot.lastAppliedWater = snap.water;
    slot.lastAppliedMovement = snap.movementState;
}

// Replaces BSE's checkExecWaterGun patch with a remote-safe version. Remote
// puppets never call emit(); spray is visual-only via emitRemoteSprayVfx().
static void smso_emitLocalTurboWaterSprayCone(TWaterGun *fludd) {
    if (!fludd || !gpMarioParticleManager)
        return;

    TNozzleTrigger *trigger =
        static_cast<TNozzleTrigger *>(fludd->mNozzleList[TWaterGun::Turbo]);
    if (!trigger || trigger->mSprayState != TNozzleTrigger::ACTIVE)
        return;

    Mtx *emitMtx = fludd->getEmitMtx(0);
    if (!emitMtx || !emitMtxTranslationValid(*emitMtx))
        return;

    // doldecomp TNozzleTrigger::animation when mIsEmitWater — skip ModelWaterManager path.
    gpMarioParticleManager->emitAndBindToMtxPtr(particles::kWaterSpray, *emitMtx, 1, trigger);
}

static void smso_checkExecWaterGun(TWaterGun *fludd) {
    if (!fludd || !fludd->mMario)
        return;

    if (isRemoteBody(fludd->mMario))
        return;

    const bool turboInWater =
        fludd->mCurrentNozzle == TWaterGun::Turbo && remoteMarioInWater(fludd->mMario);

    if (!BetterSMS::areExploitsPatched()) {
        if (turboInWater) {
            smso_emitLocalTurboWaterSprayCone(fludd);
            return;
        }
        fludd->emit();
        return;
    }

    if (fludd->mCurrentNozzle != TWaterGun::Hover && fludd->mCurrentNozzle != TWaterGun::Rocket) {
        // doldecomp TWaterGun::emit skips model-water below surface; manual 0x10D matches retail.
        if (turboInWater) {
            smso_emitLocalTurboWaterSprayCone(fludd);
            return;
        }
        fludd->emit();
        return;
    }

    auto *playerData = BetterSMS::Player::getData(fludd->mMario);
    if (playerData && playerData->getCanSprayFludd())
        fludd->emit();
}
SMS_PATCH_BL(SMS_PORT_REGION(0x8024E548, 0x802462D4, 0, 0), smso_checkExecWaterGun);

static void smso_killTriggerNozzle() {
    TNozzleTrigger *nozzle;
    SMS_FROM_GPR(29, nozzle);

    if (!nozzle->mFludd || !nozzle->mFludd->mMario)
        return;
    if (isRemoteBody(nozzle->mFludd->mMario))
        return;

    nozzle->mSprayState = TNozzleTrigger::DEAD;

    if (!BetterSMS::areExploitsPatched())
        return;

    if (nozzle->mFludd->mCurrentNozzle == TWaterGun::Hover ||
        nozzle->mFludd->mCurrentNozzle == TWaterGun::Rocket) {
        auto *playerData = BetterSMS::Player::getData(nozzle->mFludd->mMario);
        if (playerData)
            playerData->setCanSprayFludd(false);
    }
}
SMS_PATCH_BL(SMS_PORT_REGION(0x8026C370, 0x802640FC, 0, 0), smso_killTriggerNozzle);

} // namespace

static void JDrama_TViewObjPtrListT_load(JDrama::TViewObjPtrListT<THitActor, JDrama::TViewObj> *viewObjPtrList,
                                         JSUMemoryInputStream *stream) {
    if (viewObjPtrList && isPlayerGroupName(viewObjPtrList->mKeyName)) {
        gPlayerGroup = reinterpret_cast<JDrama::TViewObjPtrListT<JDrama::TViewObj> *>(viewObjPtrList);
        OSReport("[SMSO] Player group captured during load @ %p\n", gPlayerGroup);
    }

    load__Q26JDrama47TViewObjPtrListT_9(viewObjPtrList, stream);
}
SMS_PATCH_BL(SMS_PORT_REGION(0x80223548, 0x8021B3C4, 0, 0), JDrama_TViewObjPtrListT_load);

// TMario perform vtable hook. Remote puppet bodies must never run
// playerControl (no gamepad). They also must not enter BSE's patched Mario
// draw wrapper because that wrapper assumes every TMario has BetterSMS
// Player::TPlayerData registered. These synthetic network puppets don't, so
// dispatch the native Mario draw steps directly.
using TMarioPerformFn = int (*)(TMario *, u32, JDrama::TGraphics *);
static TMarioPerformFn sOrigMarioPerform =
    reinterpret_cast<TMarioPerformFn>(SMS_PORT_REGION(0x8024D2A8, 0x80245034, 0, 0));

static int TMario_perform_remote(TMario *mario, u32 flags, JDrama::TGraphics *graphics) {
    if (!isRemoteBody(mario)) {
        // Before draw/entryModels: zero hats so retail cannot submit them this pass.
        if (mario == gpMarioAddress)
            smso::maintainLocalHiddenCaps(mario, flags);
        const int result = sOrigMarioPerform(mario, flags, graphics);
        if (mario == gpMarioAddress) {
            mirrorRemotePerformGroup(flags, graphics);
            if ((flags & 0x205) != 0)
                maintainLocalHideSeekSeekerDraw(mario, graphics);
            // After retail calcAnim / seeker rebind: keep hats squashed for the
            // next entryModels and kill mtx-effect ghosts retail re-enabled.
            smso::maintainLocalHiddenCaps(mario, flags | 0x2u);
        }
        return result;
    }

    RemoteActorSlot *slot = findRemoteSlot(mario);
    // Module-owned but demoted / parked / not the slot's current body: never
    // fall through to retail perform (BetterSMS player data / gamepad). Active
    // visible slot bodies keep the remote visual path below.
    if (!slot || !slot->visible || slot->body != mario)
        return 0;

    // Start Tag grace: seekers must not calc/draw/hear remote hiders at all
    // (body, Yoshi, FLUDD, shadow, particles). setBodyVisible alone is insufficient
    // because this path does not use retail attr-gated draw.
    const u8 netSlot = networkSlotOf(slot);
    if (netSlot != 0xFF && shouldSuppressRemoteHiderFromSeekerGrace(netSlot)) {
        setBodyVisible(mario, false);
        static u32 sGraceSuppressDiag = 0;
        if ((++sGraceSuppressDiag % 180) == 1)
            OSReport("[SMSO] Grace suppress remote perform slot=%u (seeker POV)\n", netSlot);
        return 0;
    }

    configureRemoteMarioCollision(mario, slot ? slot->rosterSlot : 0xFF);

    const bool showFluddOnMarioBack = slot && (slot->vfxFlags & VFX_NO_FLUDD) == 0;

    // Local Mario can dispatch separate calc/view/entry passes whose masks all
    // intersect 0x205. Run remote visual work at most once per game update;
    // previously the full J3D skeleton path could execute again on later passes.
    if ((flags & 0x205) && slot->lastVisualWorkFrame != gRemoteVisualFrame) {
        slot->lastVisualWorkFrame = gRemoteVisualFrame;
        tickRemoteAppearReveal(slot);

        const u16 vfx = slot->vfxFlags;
        const bool sprayingFludd =
            (vfx & (VFX_WATER_SPRAY | VFX_FLUDD_EMPTY)) != 0 &&
            !snapshotHostOnYoshi(slot->nozzleId, vfx);
        const bool yoshiJuice =
            snapshotHostOnYoshi(slot->nozzleId, vfx) && smso::remoteBodyRidingYoshi(mario);
        bool ranBodyVisual = false;

        // Body / anim LOD path — distance + on-screen interval only.
        if (slot->renderVisible) {
            if (slot->visualUpdateThisFrame) {
                remoteCalcAnim(mario, slot, graphics);
                captureRemotePoseRoot(mario, slot);
                slot->visualStateDirty = false;
                ranBodyVisual = true;

                emitPendingRemoteWarpVfx(mario, slot);
                syncRemoteContinuousParticles(mario, slot);

                if (yoshiJuice)
                    emitRemoteYoshiJuiceSpray(mario, slot, slot->vfxFlags);
            } else {
                updateRemoteRootTransform(mario, slot);
            }
        } else if (sprayingFludd) {
            // Offscreen spraying remotes still need a fresh chest/root for emit mtx.
            updateRemoteRootTransform(mario, slot);
        }

        // Dedicated 60 Hz FLUDD spray tick — fully exempt from body LOD /
        // renderVisible / stagger. Nearby and far remotes both emit every frame
        // while VFX_WATER_SPRAY (or dry pump) is set and the body exists.
        if (sprayingFludd && showFluddOnMarioBack && mario->mFludd &&
            remoteFluddPerformSafe(mario, mario->mFludd)) {
            bindRemoteFludd(mario, slot, vfx, graphics);
        } else if (ranBodyVisual && showFluddOnMarioBack && mario->mFludd &&
                   remoteFluddPerformSafe(mario, mario->mFludd) && !yoshiJuice) {
            // Non-spray FLUDD pose/nozzle follow body visual cadence only.
            bindRemoteFludd(mario, slot, vfx, graphics);
        }
    }

    const bool drawBody = isRemoteBodyDrawVisible(slot);

    u32 savedSurfState = 0;
    bool strippedSurfDraw = false;
    // Surf draw flag (0x10000) makes retail calcView/entryModels call
    // MActor::perform(mSurfGesso, 4/0x200). Safe when mSurfGesso is a real SDLModel
    // clone from SMS_MakeMActorFromSDLModelData. If bind failed (templates not ready /
    // heap), strip the flag so a null mSurfGesso cannot null-deref in calcView.
    if (smso::isBlooperSurfState(mario->mState) && !mario->mSurfGesso) {
        savedSurfState = mario->mState;
        mario->mState = stripSurfDrawFlag(savedSurfState);
        strippedSurfDraw = true;
    }

    if (flags & 0x4) {
        mario->calcView(graphics);
        performRemoteYoshiDraw(mario, 0x4, graphics, drawBody);
        if (drawBody && showFluddOnMarioBack && remoteFluddPerformSafe(mario, mario->mFludd)) {
            mario->mFludd->mIsEmitWater = false;
            mario->mFludd->perform(0x4, graphics);
        }
    }

    if (flags & 0x200) {
        if (drawBody) {
            if (kRemoteHotPathOsReport && ++gRemotePerformDrawDiag <= 3)
                OSReport("[SMSO] Remote body draw perform slot=%p flags=0x200\n", mario);
            // Remote perform group is not on Player Group's calcView pass in every pipeline
            // ordering; ensure view matrices exist before entryModels (doldecomp preEntry 0x4).
            if ((flags & 0x4) == 0)
                mario->calcView(graphics);
            // Capless remotes: re-assert zero-scale hats immediately before
            // entryModels → mCap->perform(0x200) submits them.
            {
                const u32 idx = static_cast<u32>(slot - gActors);
                if (idx < MAX_REMOTE_SLOTS && gBodyPoolHideCaps[idx])
                    smso::squashHiddenCapDrawInstance(mario);
            }
            mario->addDirty();
            if (showFluddOnMarioBack && remoteFluddPerformSafe(mario, mario->mFludd)) {
                mario->mFludd->mIsEmitWater = false;
                mario->mFludd->perform(0x200, graphics);
            }
            const bool shadowMActors = smso::marioHasShadowMActors(mario);
            if (shadowMActors && mario->mModelData && mario->mModelData->mModel)
                mario->mModelData->mModel->unlock();
            if (shadowMActors)
                smso::entryInMarioShadowMActors(mario);
            mario->entryModels(graphics);
            if (shadowMActors)
                smso::entryOutMarioShadowMActors(mario);
            if (shadowMActors && mario->mModelData && mario->mModelData->mModel)
                mario->mModelData->mModel->lock();
            performRemoteYoshiDraw(mario, flags, graphics, drawBody);
            const u16 vfx = slot ? slot->vfxFlags : static_cast<u16>(0);
            if (slot->drawShadowThisFrame)
                drawRemoteMarioShadow(mario, vfx);
        }
    }

    if (strippedSurfDraw)
        mario->mState = savedSurfState;

    return 0;
}
SMS_WRITE_32(SMS_PORT_REGION(0x803dd680, 0x803d4e60, 0, 0),
             reinterpret_cast<u32>(&TMario_perform_remote));

using TMarioReceiveMessageFn = bool (*)(TMario *, THitActor *, u32);
static TMarioReceiveMessageFn sOrigMarioReceiveMessage =
    reinterpret_cast<TMarioReceiveMessageFn>(SMS_PORT_REGION(0x80282AF4, 0x8027A880, 0, 0));

static bool TMario_receiveMessage_remoteSafe(TMario *mario, THitActor *sender, u32 msg) {
    if (isRemoteBody(mario) || isRemoteBody(reinterpret_cast<TMario *>(sender)))
        return false;

    return sOrigMarioReceiveMessage(mario, sender, msg);
}
SMS_WRITE_32(SMS_PORT_REGION(0x803dd684, 0x803d4e64, 0, 0),
             reinterpret_cast<u32>(&TMario_receiveMessage_remoteSafe));

using TMarioCanTakeFn = bool (*)(TMario *, THitActor *);
static TMarioCanTakeFn sOrigMarioCanTake =
    reinterpret_cast<TMarioCanTakeFn>(SMS_PORT_REGION(0x80243550, 0x8023b2dc, 0, 0));

static bool TMario_canTake_remoteSafe(TMario *mario, THitActor *actor) {
    if (isRemoteBody(mario) || isRemoteBody(reinterpret_cast<TMario *>(actor)))
        return false;

    return sOrigMarioCanTake(mario, actor);
}

// Patch canTake call sites (not the function entry) so the original implementation remains callable.
SMS_PATCH_BL(SMS_PORT_REGION(0x80281604, 0x80279390, 0, 0), TMario_canTake_remoteSafe);
SMS_PATCH_BL(SMS_PORT_REGION(0x80281B44, 0x802798D0, 0, 0), TMario_canTake_remoteSafe);
SMS_PATCH_BL(SMS_PORT_REGION(0x80281D94, 0x80279B20, 0, 0), TMario_canTake_remoteSafe);
SMS_PATCH_BL(SMS_PORT_REGION(0x80281E78, 0x80279C04, 0, 0), TMario_canTake_remoteSafe);
SMS_PATCH_BL(SMS_PORT_REGION(0x80281F88, 0x80279D14, 0, 0), TMario_canTake_remoteSafe);
SMS_PATCH_BL(SMS_PORT_REGION(0x802820F0, 0x80279E7C, 0, 0), TMario_canTake_remoteSafe);
SMS_PATCH_BL(SMS_PORT_REGION(0x802821F8, 0x80279F84, 0, 0), TMario_canTake_remoteSafe);
SMS_PATCH_BL(SMS_PORT_REGION(0x80282238, 0x80279FC4, 0, 0), TMario_canTake_remoteSafe);

using TMarioCheckCollisionFn = void (*)(TMario *);
static TMarioCheckCollisionFn sOrigMarioCheckCollision =
    reinterpret_cast<TMarioCheckCollisionFn>(SMS_PORT_REGION(0x80280FE8, 0x80278D74, 0, 0));

static void TMario_checkCollision_remoteSafe(TMario *mario) {
    if (isRemoteBody(mario))
        return;

    sOrigMarioCheckCollision(mario);
}
SMS_WRITE_32(SMS_PORT_REGION(0x803dd6A8, 0x803d4e88, 0, 0),
             reinterpret_cast<u32>(&TMario_checkCollision_remoteSafe));

using TMarioDamageExecFn = void (*)(TMario *, THitActor *, int, int, int, f32, int, f32, s16);
static TMarioDamageExecFn sOrigMarioDamageExec =
    reinterpret_cast<TMarioDamageExecFn>(SMS_PORT_REGION(0x8024280C, 0x8023A598, 0, 0));

static void TMario_damageExec_remoteSafe(TMario *mario, THitActor *hit, int damage, int damageAnimType,
                                         int emitCount, f32 minSpeed, int motor, f32 waterEmit,
                                         s16 invincibility) {
    if (isRemoteBody(mario) || isRemoteBody(reinterpret_cast<TMario *>(hit)))
        return;
    if (isRemoteProxiedLocalDamage(mario, hit))
        return;

    sOrigMarioDamageExec(mario, hit, damage, damageAnimType, emitCount, minSpeed, motor, waterEmit,
                         invincibility);
}
SMS_WRITE_32(SMS_PORT_REGION(0x803dd6AC, 0x803d4e8C, 0, 0),
             reinterpret_cast<u32>(&TMario_damageExec_remoteSafe));

namespace smso {

void initRemoteActors() {
    initBlooperSurfSync();
    gRemoteVisualFrame = 0;
    gArchiveLoadAttemptedSinceActorUpdate = false;
    gBodyConstructedSinceActorUpdate = false;
    gFirstVisibleBodyPendingThisUpdate = false;
    gBodyConstructionWindowOpen = false;
    gHeavyPreparationCooldown = 0;
    gPreparationIdleWaitFrames = 0;
    gPlayerGroup = nullptr;
    gRemotePerformGroupRegistered = false;
    gRemotePerformDrawDiag = 0;
    clearRemotePerformGroupMembers();
    gRemotePerformGroup = nullptr;
    gReportedPerformGroupAllocFail = false;
    gReportedMissingPlayerGroup = false;
    gReportedBodyCap = false;
    gReportedHeapShortage = false;

    for (auto &slot : gActors)
        slot = {};

    CommBuffer *buf = getCommBuffer();
    const bool connected = buf && (buf->bridgeFlags & BF_CONNECTED) != 0;

    // Keep-alive path: heap + pool survived the previous stage exit while connected.
    if (gRemoteActorHeap && gBodyPoolCount > 0) {
        gRemoteHeapReserved = true;
        prepareSurvivingBodyPoolForNewStage();
        OSReport("[SMSO] Remote actor heap reused at stage init @ %p owned=%d free=%u pool=%u\n",
                 gRemoteActorHeap, gRemoteActorHeapOwned ? 1 : 0,
                 static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()), gBodyPoolCount);
    } else {
        gRemoteHeapReserved = false;
        resetBodyPoolForFreshHeap();
        if (connected) {
            ensureRemoteActorHeap();
            gRemoteHeapReserved = gRemoteActorHeap != nullptr;
            if (gRemoteHeapReserved) {
                OSReport("[SMSO] Remote actor heap reserved at stage init @ %p owned=%d free=%u\n",
                         gRemoteActorHeap, gRemoteActorHeapOwned ? 1 : 0,
                         static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
            }
        }
    }
}

void updateRemoteModelPreload(TMarDirector *director) {
    gBodyConstructionWindowOpen = false;
    // stageUpdate invokes preload before updateRemoteActors, so this is the
    // per-video-frame body budget reset even on loading/file-select paths where
    // actor update is intentionally skipped.
    gBodyConstructedSinceActorUpdate = false;
    // Advance reclaim tick every preload so demoted graphs age out even when
    // construction is deferred (idle wait / heavy cooldown / first-visible).
    if (++gBodyReclaimTick == 0)
        gBodyReclaimTick = 1;
    CommBuffer *buf = getCommBuffer();
    if (!buf || buf->magic != COMM_MAGIC)
        return;
    if ((buf->bridgeFlags & BF_CONNECTED) == 0)
        return;

    if (!gRemoteHeapReserved) {
        gRemoteHeapReserved = true;
        ensureRemoteActorHeap();
    }
    if (!gRemoteActorHeap)
        return;

    for (auto &ready : gReadyCustomBodies) {
        if (ready.body && ready.readyDelay > 0)
            --ready.readyDelay;
    }

    // The archive state machine must advance every frame, including active
    // gameplay. DVD DMA is asynchronous; publication/validation occurs here on
    // the main thread and never constructs or mounts a body.
    bool archiveLoadStarted = false;
    smso::prefetchRemoteMarioPacks(&archiveLoadStarted);
    gArchiveLoadAttemptedSinceActorUpdate =
        gArchiveLoadAttemptedSinceActorUpdate || archiveLoadStarted;

    // Body/J3D construction is the remaining indivisible expensive phase.
    // Loading screens may build immediately; active gameplay requires several
    // consecutive genuinely idle frames. There is no forced deadline.
    const bool loadingWindow = !isStageReady(director) || !gpMarioAddress;
    if (loadingWindow) {
        gPreparationIdleWaitFrames = kSafePreparationIdleFrames;
    } else if (isSafeModelPreparationIdleFrame(director)) {
        if (gPreparationIdleWaitFrames < kSafePreparationIdleFrames)
            ++gPreparationIdleWaitFrames;
    } else {
        gPreparationIdleWaitFrames = 0;
        ++gModelBuildDeferredCount;
        return;
    }
    if (!loadingWindow && gPreparationIdleWaitFrames < kSafePreparationIdleFrames) {
        ++gModelBuildDeferredCount;
        return;
    }
    if (gHeavyPreparationCooldown > 0) {
        --gHeavyPreparationCooldown;
        return;
    }
    gBodyConstructionWindowOpen = true;

    // When a valid live remote has no prebuilt body, first-visible latency wins:
    // leave this safe window's body budget to updateRemoteActors, which
    // constructs a retail fallback (or cache-ready custom body).
    gFirstVisibleBodyPendingThisUpdate =
        isStageReady(director) && gpMarioAddress && hasValidRemoteWaitingForBody(buf);
    if (gFirstVisibleBodyPendingThisUpdate)
        return;

    // A visible retail fallback waiting for its custom body owns the next
    // construction budget. Do not let unrelated background pool fill delay it.
    if (hasPendingActiveModelUpgrade()) {
        prewarmRequestedCustomBodyStep();
    } else if (!gBodyPoolPrewarmComplete) {
        prewarmRemoteBodyPoolStep();
    } else {
        prewarmReadyCustomBodyStep();
    }
}

void updateRemoteActors(TMarDirector *director) {
    if (!isStageReady(director) || !gpMarioAddress)
        return;

    if (!gPlayerGroup) {
        gPlayerGroup = findPlayerGroup();
        if (gPlayerGroup) {
            gReportedMissingPlayerGroup = false;
            OSReport("[SMSO] Player group captured @ %p\n", gPlayerGroup);
        } else if (!gReportedMissingPlayerGroup) {
            OSReport("[SMSO] Player group NOT found, bodies disabled this stage\n");
            gReportedMissingPlayerGroup = true;
        }
    }

    if (!gPlayerGroup)
        return;

    registerRemotePerformGroup(director);
    if (!gRemotePerformGroupRegistered || !gRemotePerformGroup)
        return;

    CommBuffer *buf = getCommBuffer();
    const u8 localSlot = buf->localSlot;

    if ((buf->bridgeFlags & BF_CONNECTED) == 0) {
        for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i)
            dismissRemoteBody(static_cast<u8>(i), gActors[i]);
        gArchiveLoadAttemptedSinceActorUpdate = false;
        gBodyConstructedSinceActorUpdate = false;
        gFirstVisibleBodyPendingThisUpdate = false;
        gBodyConstructionWindowOpen = false;
        return;
    }

    if (!gRemoteHeapReserved) {
        gRemoteHeapReserved = true;
        ensureRemoteActorHeap();
    }

    // Pack prefetch + staggered body prewarm run from updateRemoteModelPreload
    // (hub / loading / stageUpdate) so this path stays cache-hit / assign-only.

    ++gRemoteVisualFrame;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        const PlayerSnapshot &snap = buf->remoteSnapshots[i];
        RemoteActorSlot &slot = gActors[i];

        if (i == localSlot) {
            dismissRemoteBody(static_cast<u8>(i), slot);
            continue;
        }

        if (!isValidSnapshot(snap) || !isSameStage(buf, snap)) {
            if (slot.invalidSnapshotStreak < kRemoteDismissInvalidStreak)
                ++slot.invalidSnapshotStreak;
            if (slot.invalidSnapshotStreak >= kRemoteDismissInvalidStreak)
                dismissRemoteBody(static_cast<u8>(i), slot);
            continue;
        }

        slot.invalidSnapshotStreak = 0;

        if (!ensureRemoteBody(slot, i))
            continue;
        gReportedBodyCap = false;

        if (!slot.visible && !slot.inViewList) {
            resetRemoteRuntimeState(slot);
            if (kRemoteHotPathOsReport)
                OSReport("[SMSO] Remote Mario body slot %u activated @ %p\n", i, slot.body);
        }

        if (!slot.inViewList) {
            gRemotePerformGroup->mViewObjList.push_back(slot.body);
            ++gRemotePerformBodyCount;
            slot.inViewList = true;
            if (kRemoteHotPathOsReport)
                OSReport("[SMSO] Remote Mario body slot %u registered in remote perform group\n", i);
        }

        if (!slot.visible)
            slot.visible = true;

        applySnapshotToBody(slot, snap);

        if (!slot.inWarpTransition)
            advanceRemoteDisplayMotion(slot, slot.body);

        updateRemoteVisualSchedule(slot, static_cast<u8>(i));
        emitPendingRemoteContactVfx(slot.body, slot);

        if (slot.renderVisible && slot.appearRevealFrames == 0 && !slot.pendingWarpInVfx &&
            !shouldSuppressRemoteHiderFromSeekerGrace(static_cast<u8>(i)))
            setBodyVisible(slot.body, true);
        else
            setBodyVisible(slot.body, false);
    }

    updateRemoteCarriedFruit();

    if (isHideSeekGraceActive()) {
        static u32 sGraceVisDiag = 0;
        if ((++sGraceVisDiag % 180) == 1) {
            const CommBuffer *gmBuf = getCommBuffer();
            u32 suppressed = 0;
            for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
                if (i == localSlot)
                    continue;
                if (shouldSuppressRemoteHiderFromSeekerGrace(static_cast<u8>(i)))
                    ++suppressed;
            }
            OSReport("[SMSO] Grace vis localRole=%u flags=0x%02x suppressed=%u\n",
                     gmBuf ? gmBuf->gameModeState.localRole : 0xFFu,
                     gmBuf ? gmBuf->gameModeState.flags : 0u, suppressed);
        }
    }

    if (kRemoteHotPathOsReport && ++gVisibilityDiagFrame % kVisibilityDiagInterval == 0) {
        u32 connected = 0;
        u32 spawned = 0;
        u32 visible = 0;
        u32 inView = 0;
        for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
            if (i == localSlot)
                continue;
            const PlayerSnapshot &snap = buf->remoteSnapshots[i];
            if (!isValidSnapshot(snap) || !isSameStage(buf, snap))
                continue;
            ++connected;
            if (gActors[i].spawned && gActors[i].body)
                ++spawned;
            if (gActors[i].visible)
                ++visible;
            if (gActors[i].inViewList)
                ++inView;
        }

        const u32 heapFree =
            gRemoteActorHeap ? static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()) : 0;
        OSReport("[SMSO] Remote visibility diag localSlot=%u connected=%u spawned=%u visible=%u "
                 "inView=%u pool=%u/%u heapFree=%u owned=%d\n",
                 localSlot, connected, spawned, visible, inView,
                 gBodyPoolCount, kSessionMaxRemotes, heapFree, gRemoteActorHeapOwned ? 1 : 0);
    }

    if (++gModelDiagnosticsFrame >= 600) {
        gModelDiagnosticsFrame = 0;
        OSReport("[SMSO] Model body diag builds=%u buildMs=%u deferred=%u commits=%u "
                 "desired/preparing/ready/applied tracked; bodyFree=%u/%u packFree=%u/%u\n",
                 gModelBuildCount, gModelBuildMilliseconds, gModelBuildDeferredCount,
                 gModelPointerCommitCount,
                 gRemoteActorHeap
                     ? static_cast<u32>(gRemoteActorHeap->getTotalFreeSize())
                     : 0,
                 gRemoteActorHeapCapacity,
                 gRemoteActorPackHeap
                     ? static_cast<u32>(gRemoteActorPackHeap->getTotalFreeSize())
                     : 0,
                 gRemoteActorPackHeapCapacity);
    }

    // Consume the cross-phase heavy-work budget. The next preload/actor update
    // may perform one archive load OR one body construction.
    gArchiveLoadAttemptedSinceActorUpdate = false;
    gBodyConstructedSinceActorUpdate = false;
    gFirstVisibleBodyPendingThisUpdate = false;
    gBodyConstructionWindowOpen = false;
}

void clearRemoteActors(bool keepHeapAndPool) {
    gArchiveLoadAttemptedSinceActorUpdate = false;
    gBodyConstructedSinceActorUpdate = false;
    gFirstVisibleBodyPendingThisUpdate = false;
    gBodyConstructionWindowOpen = false;
    gHeavyPreparationCooldown = 0;
    gPreparationIdleWaitFrames = 0;
    gPlayerGroup = nullptr;
    gRemotePerformGroupRegistered = false;
    gRemotePerformDrawDiag = 0;
    clearRemotePerformGroupMembers();
    // The perform group may have been allocated from a heap that is torn down with
    // the stage (remote actor heap / current stage heap). Drop the reference so the
    // next stage recreates it cleanly instead of dereferencing a dangling pointer.
    gRemotePerformGroup = nullptr;
    gReportedPerformGroupAllocFail = false;
    gReportedMissingPlayerGroup = false;
    for (auto &slot : gActors)
        slot = {};

    if (keepHeapAndPool && gRemoteActorHeap) {
        // Connected stage exit: park pool bodies, keep heap + pack buffers + pool.
        for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
            if (gBodyPool[i])
                parkRemoteBody(gBodyPool[i]);
            if (gBodyVariants[i].body)
                parkRemoteBody(gBodyVariants[i].body);
            gBodyModelApplied[i] = false;
            gBodyRetailGraceFrames[i] = 0;
            gBodyModelRetryCooldown[i] = 0;
        }
        for (auto &ready : gReadyCustomBodies) {
            if (ready.body)
                parkRemoteBody(ready.body);
        }
        hideMainHeapParkedSpares();
        gRemoteHeapReserved = true;
        OSReport("[SMSO] Remote actors cleared (heap+pool kept: %u bodies @ %p free=%u)\n",
                 gBodyPoolCount, gRemoteActorHeap,
                 static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
        return;
    }

    gRemoteHeapReserved = false;
    resetBodyPoolForFreshHeap();
    destroyRemoteActorHeap();
}

bool remoteActorHeapNeedsRecycleOnStageExit() {
    return gRemoteHeapRecycleOnStageExit;
}

bool isRemoteMarioModelFrozen(u8 slot) {
    return slot < MAX_REMOTE_SLOTS && gBodyModelApplied[slot];
}

void requestRemoteMarioModelReapply(u8 slot) {
    if (slot >= MAX_REMOTE_SLOTS)
        return;
    beginRemoteModelRequest(slot);
}

bool remoteActorReferencesModelId(const char id[8]) {
    if (!id || marioModelIdIsEmpty(id))
        return false;
    if (hasBodyForModelId(id))
        return true;
    return modelIdHasOutstandingRequest(id) || isModelDesiredByRemote(id);
}

bool hasRemoteBodyForSlot(u8 slot) {
    return slot < MAX_REMOTE_SLOTS && gActors[slot].spawned && gActors[slot].body != nullptr &&
           gActors[slot].inViewList;
}

bool hasRemoteBodyForSlotLoose(u8 slot) {
    return slot < MAX_REMOTE_SLOTS && gActors[slot].spawned && gActors[slot].body != nullptr;
}

TMario *getRemoteBodyForSlot(u8 slot) {
    if (!hasRemoteBodyForSlot(slot))
        return nullptr;
    return gActors[slot].body;
}

TMario *getRemoteBodyForSlotLoose(u8 slot) {
    if (!hasRemoteBodyForSlotLoose(slot))
        return nullptr;
    return gActors[slot].body;
}

bool getRemoteBodyPosition(u8 slot, f32 &x, f32 &y, f32 &z) {
    // Loose: Far-culled remotes stay spawned with live translation for nametags / pop-in.
    if (!hasRemoteBodyForSlotLoose(slot))
        return false;

    const TMario *body = gActors[slot].body;
    x = body->mTranslation.x;
    y = body->mTranslation.y;
    z = body->mTranslation.z;
    return true;
}

bool getRemoteHeadAnchorPosition(u8 slot, f32 &x, f32 &y, f32 &z) {
    if (!hasRemoteBodyForSlotLoose(slot))
        return false;

    const TMario *body = gActors[slot].body;
    if (body->mModelData && body->mModelData->mModel && body->mModelData->mModel->mJointArray &&
        body->mModelData->mModel->mModelData) {
        const u8 headJoint = body->mBindBoneIDArray[10];
        if (headJoint < body->mModelData->mModel->mModelData->mJointNum) {
            const Mtx &headMtx = body->mModelData->mModel->mJointArray[headJoint];
            const Vec localCrown = {0.0f, kHeadCrownWorldOffset, 0.0f};
            Vec worldCrown{};
            MTXMultVec(headMtx, &localCrown, &worldCrown);
            // Temporal LOD can leave an airborne crown briefly after landing while
            // the mesh is already rooted to the ground. Cap how far above the body
            // the tag may sit so it never floats a second Mario-height overhead.
            const f32 maxY = body->mTranslation.y + kHeadAnchorMaxAboveBody;
            if (worldCrown.y > maxY)
                worldCrown.y = maxY;
            x = worldCrown.x;
            y = worldCrown.y;
            z = worldCrown.z;
            return x == x && y == y && z == z;
        }
    }

    x = body->mTranslation.x;
    y = body->mTranslation.y + kHeadFallbackWorldOffset;
    z = body->mTranslation.z;
    return true;
}

JKRHeap *borrowRemoteActorHeap() {
    if (!gRemoteHeapReserved) {
        gRemoteHeapReserved = true;
        ensureRemoteActorHeap();
    }
    return gRemoteActorHeap;
}

JKRHeap *borrowRemoteActorPackHeap() {
    if (!gRemoteHeapReserved) {
        gRemoteHeapReserved = true;
        ensureRemoteActorHeap();
    }
    return gRemoteActorPackHeap;
}

u32 remoteActorBodyHeapCapacityBytes() { return gRemoteActorHeapCapacity; }
u32 remoteActorPackHeapCapacityBytes() { return gRemoteActorPackHeapCapacity; }

bool isRemoteMarioBody(const TMario *mario) {
    return isRemoteBody(mario);
}

bool shouldUpdateRemoteMarioCosmetics(const TMario *mario) {
    if (!mario)
        return false;
    if (mario == gpMarioAddress)
        return true;
    for (u32 i = 0; i < MAX_REMOTE_SLOTS; ++i) {
        const RemoteActorSlot &slot = gActors[i];
        if (slot.spawned && slot.body == mario)
            return slot.cosmeticUpdateThisFrame;
    }
    return false;
}

} // namespace smso

#else

namespace smso {

void initRemoteActors() {}
void updateRemoteActors(TMarDirector *) {}
void clearRemoteActors(bool) {}
void updateRemoteModelPreload(TMarDirector *) {}
bool remoteActorHeapNeedsRecycleOnStageExit() { return false; }

bool hasRemoteBodyForSlot(u8 slot) {
    (void)slot;
    return false;
}

bool hasRemoteBodyForSlotLoose(u8 slot) {
    (void)slot;
    return false;
}

TMario *getRemoteBodyForSlot(u8 slot) {
    (void)slot;
    return nullptr;
}

TMario *getRemoteBodyForSlotLoose(u8 slot) {
    (void)slot;
    return nullptr;
}

bool getRemoteBodyPosition(u8 slot, f32 &x, f32 &y, f32 &z) {
    (void)slot;
    (void)x;
    (void)y;
    (void)z;
    return false;
}

bool getRemoteHeadAnchorPosition(u8 slot, f32 &x, f32 &y, f32 &z) {
    (void)slot;
    (void)x;
    (void)y;
    (void)z;
    return false;
}

JKRHeap *borrowRemoteActorHeap() {
    return nullptr;
}

JKRHeap *borrowRemoteActorPackHeap() { return nullptr; }
u32 remoteActorBodyHeapCapacityBytes() { return 0; }
u32 remoteActorPackHeapCapacityBytes() { return 0; }

bool isRemoteMarioModelFrozen(u8) { return false; }
void requestRemoteMarioModelReapply(u8) {}
bool remoteActorReferencesModelId(const char *) { return false; }

bool isRemoteMarioBody(const TMario *mario) {
    (void)mario;
    return false;
}

bool shouldUpdateRemoteMarioCosmetics(const TMario *mario) {
    (void)mario;
    return true;
}

} // namespace smso

#endif
