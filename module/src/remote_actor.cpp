#include "remote_actor.hpp"
#include "blooper_surf_sync.hpp"
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

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;
extern TMap *gpMap;
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
// in practice. We pre-spawn the whole pool once per stage (SMSO 2 makeMarios()
// principle) and reuse them for the whole stage. A full MAX_PLAYERS session has
// MAX_PLAYERS-1 remotes, so the pool holds that many bodies; 9 * ~612 KiB ≈ 5.4 MiB
// fits inside the 7.5 MiB expanded-MEM1 arena below with ~2 MiB of runtime margin.
constexpr u32 kSessionMaxRemotes = MAX_PLAYERS - 1;
constexpr size_t kRemoteActorDedicatedHeapSize = 0x00780000u;
constexpr size_t kStageHeapReserveMargin = 0x00060000u;
constexpr size_t kStageHeapReserveMarginTight = 0x00020000u;
constexpr size_t kRemoteBodySpawnMinFree = 0x00090000u;
// Dolphin expanded MEM1 (48 MiB) puppet-pool arena. Retail SMS only configures
// CPU BATs for the stock 24 MiB, so this region faults until we add a data BAT
// for it (see ensureExtendedMem1Mapping). 0x81810000 sits in the block based at
// 0x81000000; ensureExtendedMem1Mapping widens DBAT2 from 8 -> 16 MiB, exposing
// 0x81000000-0x81FFFFFF -> phys 0x01000000-0x01FFFFFF (Dolphin backs the full
// 48 MiB). 0x780000 = 7.5 MiB holds nine ~612 KiB bodies and ends at 0x81F90000,
// well within the widened 16 MiB block (ends 0x82000000).
constexpr u32 kRemoteActorExpandedHeapAddress = 0x81810000u;
constexpr size_t kRemoteActorExpandedHeapSize = 0x00780000u;
// Only attempt the extended arena when Dolphin actually backs >24 MiB of MEM1.
constexpr u32 kMinMem1ForExpandedHeap = 0x02800000u; // 40 MiB

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
constexpr u8 kFluddSprayEmitHz = 30; // remote FLUDD spray + turbo dash particles (~30 Hz)
constexpr u8 kFluddSprayEmitInterval = 60 / kFluddSprayEmitHz;
constexpr u8 kFluddSpraySoundHz = 30;
constexpr u8 kFluddSpraySoundInterval = 60 / kFluddSpraySoundHz;
// Remote swim VFX cadence. Retail swimMain() fires bubbles + a surface ripple each
// frame for one local Mario; rate-limiting keeps 9 simultaneous swimmers affordable.
constexpr u8 kSwimBubbleEmitHz = 30;
constexpr u8 kSwimBubbleEmitInterval = 60 / kSwimBubbleEmitHz; // every 2 frames
constexpr u8 kSwimRippleEmitHz = 10;
constexpr u8 kSwimRippleEmitInterval = 60 / kSwimRippleEmitHz; // every 6 frames
constexpr u8 kRemoteDismissInvalidStreak = 3;

// doldecomp MarioStatus.hpp status type+id mask (low 9 bits of mState).
constexpr u32 kStatusTypeAndIdMask = 0x1FFu;

static u32 remoteStatusId(u32 state) {
    return state & kStatusTypeAndIdMask;
}

struct RemoteActorSlot;

static void syncRemoteNozzleGunAngle(TMario *mario, s16 pitch);

static bool ensureRemoteActorHeap();

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
// doldecomp TMario::warpOut() case 1 waits 0xB4 frames before rolling (kind 2 appear).
constexpr u16 kStageAppearHideFrames = 0xB4u;
// doldecomp TMario::warpIn() after warpInEffect: mWarpInBallsTime (70) + mWarpInCapturedTime (120).
constexpr u16 kWarpInAppearHideFrames = 190u;
constexpr u8 kShirtShapeIndex = 10u;
constexpr s16 kSpinYawStepPerFrame = 4096; // retail rotating()/rotateJumping() use mStatusTimer * 0x1000
constexpr f32 kRemoteMotionSnapDistance = 4.0f;
constexpr f32 kRemotePositionBlendRate = 24.0f;
constexpr f32 kRemoteVelocityBlendRate = 20.0f;
constexpr f32 kRemoteRotationBlendRate = 30.0f;
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
    bool pendingStageAppear;
    bool pendingWarpInVfx;
    bool pendingWarpOutVfx;
    u8 pendingWarpOutKind;
    u16 appearRevealFrames;
    u16 lastSoundVfx;
    u8 fluddSprayTick;
    u8 swimVfxTick;
    bool wasInWater;
    u8 syncedSprayPressure;
    u8 invalidSnapshotStreak;
    f32 remoteSprayPressure;
    bool inWarpTransition;
    Vec3 targetPos;
    Vec3 targetVel;
    Vec3 displayPos;
    Vec3 displayVel;
    f32 targetRotY;
    f32 displayRotY;
    bool displayMotionInit;
    RemoteYoshiSlot yoshi;
};

static RemoteActorSlot gActors[MAX_REMOTE_SLOTS];

// Pre-spawned remote puppet pool. Adapted from the SMSO 2 design principle in
// createMarios.c (makeMarios): every remote body is allocated ONCE, up front,
// while the heap is freshest, then reused for the whole stage. The previous
// design spawned bodies lazily mid-gameplay, so by the time the 3rd remote body
// (the 4th player) was needed the stage heap was already fragmented/low and the
// spawn intermittently failed — that is why the 4th Mario never appeared.
static TMario *gBodyPool[kSessionMaxRemotes] = {};
static u32 gBodyPoolCount = 0;
static bool gBodyPoolPrewarmAttempted = false;
static u32 gVisibilityDiagFrame = 0;
static bool gRemoteHeapReserved = false;
static JDrama::TViewObjPtrListT<JDrama::TViewObj> *gPlayerGroup = nullptr;
static JDrama::TViewObjPtrListT<JDrama::TViewObj> *gRemotePerformGroup = nullptr;
static bool gRemotePerformGroupRegistered = false;
static bool gReportedMissingPlayerGroup = false;
static bool gReportedBodyCap = false;
static JKRHeap *gRemoteActorHeap = nullptr;
static bool gRemoteActorHeapOwned = false;
static bool gReportedHeapShortage = false;
static bool gExpandedHeapFailed = false;
// Set once the extended-MEM1 data BAT is installed and verified. Persists for the
// module's lifetime (the CPU mapping survives stage teardown); only a full console
// reset clears it, after which we re-install on the next session.
static bool gExtendedMappingReady = false;
static RemoteActorSlot *gRemoteWaistSlot = nullptr;

static u32 gRemotePerformDrawDiag = 0;

static JDrama::TViewObjPtrListT<JDrama::TViewObj> *ensureRemotePerformGroup() {
    if (gRemotePerformGroup)
        return gRemotePerformGroup;

    if (!JKRHeap::sRootHeap)
        return nullptr;

    void *mem = JKRHeap::sRootHeap->alloc(sizeof(JDrama::TViewObjPtrListT<JDrama::TViewObj>), 0x20);
    if (!mem)
        return nullptr;

    gRemotePerformGroup =
        new (mem) JDrama::TViewObjPtrListT<JDrama::TViewObj>("SMSO_RemotePuppets");
    return gRemotePerformGroup;
}

static void clearRemotePerformGroupMembers() {
    if (!gRemotePerformGroup)
        return;

    for (auto it = gRemotePerformGroup->mViewObjList.begin();
         it != gRemotePerformGroup->mViewObjList.end();) {
        gRemotePerformGroup->mViewObjList.erase(it++);
    }
}

static bool remotePerformGroupHasActiveBodies() {
    if (!gRemotePerformGroup)
        return false;
    for (auto it = gRemotePerformGroup->mViewObjList.begin();
         it != gRemotePerformGroup->mViewObjList.end(); ++it) {
        if (*it)
            return true;
    }
    return false;
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

    // Reset juice draw tint only on the calc pass — later mirror passes (0x4 / 0x200) must
    // keep the tint until ModelWaterManager draws droplets (doldecomp uses global unk5D5F).
    if (mirrorFlags & 0x205)
        smso::resetRemoteYoshiJuiceDrawTint();

    gRemotePerformGroup->perform(mirrorFlags, graphics);
}

static void registerRemotePerformGroup(TMarDirector *director) {
    if (gRemotePerformGroupRegistered || !director || !gPlayerGroup)
        return;

    if (!ensureRemotePerformGroup())
        return;

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

static bool isRemoteBody(const TMario *mario) {
    if (!mario)
        return false;
    // Every pooled puppet is a remote body, whether or not it is currently
    // assigned to an active network slot. Parked (idle) pool bodies must still
    // be treated as remote so the gameplay-safety patches never run on them.
    for (u32 i = 0; i < gBodyPoolCount; ++i) {
        if (gBodyPool[i] == mario)
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

static void disableRemotePickupInteraction(TMario *body, u8 rosterSlot) {
    if (!body)
        return;

    body->mAttackRadius = 0.0f;
    body->mAttackHeight = 0.0f;
    body->mReceiveRadius = 0.0f;
    body->mReceiveHeight = 0.0f;
    body->mEntryRadius = 0.0f;
    body->mHeldObject = nullptr;
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
        // The host packs the upper BCK frame into snap.water EXCEPT while spraying —
        // while spraying snap.water carries spray pressure (see puppets.cpp), and the
        // upper frame is packed in the pingMs high byte (upperEnc) instead. Using the
        // pressure value as a frame is what froze the pump during hover; read the frame
        // from upperEnc whenever the host is spraying so the pump keeps animating.
        const bool hostSpraying = (snap.vfxFlags & (VFX_WATER_SPRAY | VFX_FLUDD_EMPTY)) != 0;
        const bool upperFromWater = yCam || (pumpUpper && !hostSpraying);
        const f32 syncedUpper = upperFromWater
            ? (static_cast<f32>(snap.water) / 8.0f)
            : upperFrame;
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

    if (slot->pendingStageAppear) {
        slot->pendingStageAppear = false;
        if (sWarpOutEffect)
            sWarpOutEffect(body, 2, facing);
        beginRemoteAppearHide(*slot, kStageAppearHideFrames);
    }
}

static void bindModelToJoint(J3DModel *model, Mtx *jointMtx) {
    if (!model || !jointMtx)
        return;
    MTXCopy(*jointMtx, model->mBaseMtx);
    model->calc();
}

// Tail of doldecomp TMario::calcAnim: hands + cap after the body model perform.
static void remoteBindHandsAndCap(TMario *mario, JDrama::TGraphics *graphics, bool showSeekerGlasses) {
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
        if (mario->mCap->mCap1)
            bindModelToJoint(mario->mCap->mCap1, mheadMtx);
        if (mario->mCap->mCap3)
            bindModelToJoint(mario->mCap->mCap3, mheadMtx);
        if (kEnableRemoteYCamHelmet && mario->mCap->mDiverHelm)
            bindModelToJoint(mario->mCap->mDiverHelm, headMtx);
        if (showSeekerGlasses && mario->mCap->maGlass1)
            bindModelToJoint(mario->mCap->maGlass1, mheadMtx);
        mario->mCap->perform(2, graphics);
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

    if (mario->mCap)
        mario->mCap->mtxEffectShow();

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
    remoteBindHandsAndCap(mario, graphics, slot && slot->hideSeekSeekerLook);

    if (slot && (slot->vfxFlags & VFX_DEAD) == 0)
        smso::updateRemoteBlooperSurfFrame(mario, &slot->surf, graphics);

    calcRemoteYoshiAnim(mario, slot ? &slot->yoshi : nullptr);
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
        if (slot)
            slot->wasInWater = false;
        return;
    }

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

// One-shot movement VFX — landing puff, ground-pound impact.
static void syncRemoteParticleEdges(TMario *body, RemoteActorSlot &slot, const PlayerSnapshot &snap) {
    if (!body || !gpMarioParticleManager)
        return;

    const u32 state = body->mState;
    const u32 prevState = slot.lastState;

    queueRemoteWarpEdges(slot, prevState, state, snap.animId);

    if (prevState != kInvalidTrackState && prevState != state) {
        const bool enteringLand = !isLandSlipState(prevState) && isLandSlipState(state);
        if (slot.wasAirborne && enteringLand) {
            if (isHeavyLanding(state, prevState))
                body->strongTouchDownEffect();
            else
                body->smallTouchDownEffect();
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
            body->emitParticle(particles::kHipDropA);
            body->emitParticle(particles::kHipDropB);
            body->emitParticle(particles::kHipDropC);
        }
    }

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

    const f32 target = decodeSprayPressure(slot.syncedSprayPressure);
    f32 &sim = slot.remoteSprayPressure;
    if (target > sim)
        sim += (target - sim) * 0.65f;
    else
        sim = target;
    if (sim > 1.0f)
        sim = 1.0f;
    applyRemoteNozzlePressure(fludd, sim);
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

    bool emitThisFrame = false;
    if (slot)
        emitThisFrame = shouldEmitRemoteSprayThisFrame(*slot);

    if (fludd->mCurrentNozzle == TWaterGun::Turbo) {
        if (body && remoteMarioInWater(body)) {
            emitRemoteTurboWaterVfx(body, fludd, slot, emitThisFrame);
            return;
        }

        if (emitThisFrame) {
            emitRemoteTurboNozzleSpray(fludd);
            emitRemoteTurboDashBoostVfx(fludd);
        }
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
        // Each water particle that lands calls gpPollution->clean() (doldecomp
        // ModelWaterManager splashGround/splashWall), so the graffiti clean rate is
        // directly proportional to droplet count. The previous 30 Hz throttle made
        // remote graffiti clean at ~half the local rate, so the host finished cleaning
        // (and revealed the blue coin) before remote screens caught up. Cosmetic spray
        // particles above still run at 30 Hz via emitThisFrame; only the cleaning
        // droplets are promoted to per-frame.
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

    const bool emitThisFrame = shouldEmitRemoteSprayThisFrame(*slot);
    if (emitThisFrame) {
        if (gpMarioParticleManager)
            gpMarioParticleManager->emitAndBindToMtxPtr(particles::kWaterSpray, *tongueMtx, 1,
                                                        fludd->mNozzleList[TWaterGun::Yoshi]);
        emitRemoteYoshiJuiceDroplets(fludd, pressure, juiceType);
    }

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

static bool isRemoteBodyDrawVisible(const RemoteActorSlot *slot) {
    return !slot || slot->appearRevealFrames == 0;
}

static void resetRemoteRuntimeState(RemoteActorSlot &slot, bool stageAppear) {
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
    slot.pendingStageAppear = stageAppear;
    slot.pendingWarpInVfx = false;
    slot.pendingWarpOutVfx = false;
    slot.pendingWarpOutKind = 0;
    slot.appearRevealFrames = 0;
    slot.lastSoundVfx = 0xFFFF;
    slot.fluddSprayTick = 0;
    slot.swimVfxTick = 0;
    slot.wasInWater = false;
    slot.syncedSprayPressure = 0;
    slot.invalidSnapshotStreak = 0;
    slot.remoteSprayPressure = 0.0f;
    slot.rosterSlot = 0xFF;
    slot.hideSeekSeekerLook = false;
    slot.hideSeekSeekerLookWas = false;
    slot.inWarpTransition = false;
    slot.displayMotionInit = false;
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
    if (--slot->appearRevealFrames == 0 && slot->body)
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

static bool tryCreateDedicatedRemoteHeap();

// ---- Extended MEM1 (Dolphin 48 MiB) CPU mapping ------------------------------
//
// Retail SMS only configures CPU BATs for the stock 24 MiB, so guest addresses at
// 0x81800000+ fault even though Dolphin backs 48 MiB of host RAM (JKRHeap::
// mMemorySize reports it). Writing a JKRExpHeap header there with no mapping
// instantly resets the console (proven in dolphin.log). The fix is to add ONE
// data BAT (0x81800000 -> physical 0x01800000, 8 MiB, cached, R/W) into an
// otherwise-unused DBAT. Physical = EA - 0x80000000 keeps OSCachedToPhysical
// consistent, so GX DMA reads the same bytes the CPU writes.
//
// Block encoding verified against the GameCube kexec setup: 0x810000FF == an
// 8 MiB block at 0x81000000 (BL 0x3F << 2 | Vs | Vp). All BAT reads are harmless;
// we only WRITE an application-usable DBAT (2 or 3) when it is currently invalid,
// then sentinel-probe before trusting the region. Any failure path falls back to
// the stage heap rather than crashing. SPRs: DBATnU/L = 536+2n / 537+2n.

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

// Only DBAT2/DBAT3 are application-usable (Dolphin-OS reserves DBAT0/DBAT1).
static void writeDBAT23(int idx, u32 upper, u32 lower) {
    if (idx == 2)
        asm volatile("mtspr 541, %0; mtspr 540, %1; isync" : : "r"(lower), "r"(upper) : "memory");
    else if (idx == 3)
        asm volatile("mtspr 543, %0; mtspr 542, %1; isync" : : "r"(lower), "r"(upper) : "memory");
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

// Preferred mapping route on retail SMS: DBAT2 maps the second RAM block
// (0x81000000 -> phys 0x01000000, 8 MiB) and ends exactly at 0x81800000, right
// where the puppet arena begins. Doubling its block size to 16 MiB is fully
// transparent to the existing 0x81000000-0x817FFFFF data (phys = BRPN + (EA-BEPI)
// is independent of BL) and newly exposes 0x81800000-0x81FFFFFF -> phys
// 0x01800000, which Dolphin already backs (48 MiB). Far safer than claiming a new
// BAT because it never introduces an overlapping/aliased mapping.
static bool tryWidenAdjacentDbat(u32 regionStart, u32 regionEnd) {
    for (int i = 2; i <= 3; ++i) { // only DBAT2/DBAT3 are application-writable
        const u32 upper = readDBATU(i);
        const u32 lower = readDBATL(i);
        if ((upper & 0x3u) == 0)
            continue;

        const u32 bl = (upper >> 2) & 0x7FFu;
        const u32 blockSize = (bl + 1u) << 17;
        const u32 bepi = upper & 0xFFFE0000u;
        const u32 brpn = lower & 0xFFFE0000u;

        // Must be a RAM-cached mapping with phys = EA - 0x80000000 so GX DMA and
        // OSCachedToPhysical stay consistent over the widened range.
        if (brpn != bepi - 0x80000000u)
            continue;
        if ((lower & 0x10u) != 0) // I (cache-inhibit) set -> not cached RAM
            continue;
        // The block must sit strictly below the region and double to cover it,
        // staying naturally aligned.
        if (bepi + blockSize > regionStart)
            continue;
        const u32 newBlockSize = blockSize << 1;
        const u32 newBl = (newBlockSize >> 17) - 1u;
        if (newBl > 0x7FFu)
            continue;
        if (bepi & (newBlockSize - 1u))
            continue;
        if (bepi + newBlockSize < regionEnd)
            continue;

        const u32 newUpper = bepi | (newBl << 2) | 0x3u;
        writeDBAT23(i, newUpper, lower);
        OSReport("[SMSO] Widened DBAT%d %08X->%08X (%u->%u MiB) covering 0x%08X\n", i, upper,
                 readDBATU(i), blockSize >> 20, newBlockSize >> 20, regionStart);
        return true;
    }
    return false;
}

static bool ensureExtendedMem1Mapping() {
    if (gExtendedMappingReady)
        return true;

    const u32 regionStart = kRemoteActorExpandedHeapAddress;
    const u32 regionEnd = regionStart + static_cast<u32>(kRemoteActorExpandedHeapSize);

    for (int i = 0; i < 4; ++i)
        OSReport("[SMSO] DBAT%d = %08X %08X\n", i, readDBATU(i), readDBATL(i));

    // If a live BAT already covers the region, an earlier write-crash proves the
    // physical RAM behind it is not actually backed -> bail without probing.
    for (int i = 0; i < 4; ++i) {
        if (dbatCovers(readDBATU(i), regionStart, regionEnd)) {
            OSReport("[SMSO] Extended MEM1 already mapped by DBAT%d but unbacked; staying on stage heap\n", i);
            return false;
        }
    }

    // Preferred: widen the adjacent RAM BAT (DBAT2 8->16 MiB on retail SMS).
    bool mapped = tryWidenAdjacentDbat(regionStart, regionEnd);

    // Fallback: claim a free application BAT if one exists (non-retail configs).
    if (!mapped) {
        int slot = -1;
        if ((readDBATU(2) & 0x3u) == 0)
            slot = 2;
        else if ((readDBATU(3) & 0x3u) == 0)
            slot = 3;
        if (slot < 0) {
            OSReport("[SMSO] Extended MEM1 mapping skipped: no widenable/free DBAT\n");
            return false;
        }
        const u32 upper = 0x818000FFu; // 0x81800000 | BL(8 MiB) | Vs | Vp
        const u32 lower = 0x01800002u; // phys 0x01800000 | WIMG 0000 (cached) | PP r/w
        writeDBAT23(slot, upper, lower);
        OSReport("[SMSO] Installed DBAT%d = %08X %08X (8 MiB 0x81800000 -> phys 0x01800000)\n", slot,
                 readDBATU(slot), readDBATL(slot));
    }

    // Region is now CPU-mapped to backed host RAM, so this cannot fault. Verify
    // Dolphin honored the BAT and the RAM is present before building a heap here.
    volatile u32 *p0 = reinterpret_cast<volatile u32 *>(regionStart);
    volatile u32 *p1 = reinterpret_cast<volatile u32 *>(regionEnd - 4);
    *p0 = 0x5A5AA5A5u;
    *p1 = 0xA5A55A5Au;
    const bool ok = (*p0 == 0x5A5AA5A5u) && (*p1 == 0xA5A55A5Au);
    OSReport("[SMSO] Extended MEM1 probe 0x%08X/0x%08X -> %s\n", regionStart, regionEnd - 4,
             ok ? "OK" : "FAIL");
    if (!ok)
        return false;

    gExtendedMappingReady = true;
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

    if (!ensureExtendedMem1Mapping()) {
        gExpandedHeapFailed = true;
        OSReport("[SMSO] Expanded MEM1 heap unavailable; using stage heap (memSize=%u)\n",
                 memorySize);
        return false;
    }

    JKRHeap *parent = resolveRemoteHeapParent();
    void *heapStart = reinterpret_cast<void *>(kRemoteActorExpandedHeapAddress);
    JKRHeap *heap = JKRExpHeap::create(heapStart, kRemoteActorExpandedHeapSize, parent, false);
    if (!heap) {
        gExpandedHeapFailed = true;
        OSReport("[SMSO] Expanded MEM1 heap create FAILED @ 0x%08X size=%u parent=%p\n",
                 kRemoteActorExpandedHeapAddress, static_cast<u32>(kRemoteActorExpandedHeapSize),
                 parent);
        return false;
    }

    gRemoteActorHeap = heap;
    gRemoteActorHeapOwned = true;
    OSReport("[SMSO] Remote actor EXPANDED MEM1 heap created @ %p size=%u heapFree=%u memSize=%u\n",
             gRemoteActorHeap, static_cast<u32>(kRemoteActorExpandedHeapSize),
             static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()), memorySize);
    return true;
}

static bool tryCreateStageChildRemoteHeap(JKRHeap *parent) {
    if (!parent)
        return false;

    const u32 parentFree = static_cast<u32>(parent->getTotalFreeSize());
    static const size_t kStageChildSizes[] = {0x001C0000u, 0x001B0000u, 0x00190000u, 0x00180000u,
                                              0x00160000u, 0x00140000u, 0x00120000u};

    for (size_t i = 0; i < sizeof(kStageChildSizes) / sizeof(kStageChildSizes[0]); ++i) {
        const size_t size = kStageChildSizes[i];
        if (parentFree < size + kStageHeapReserveMargin)
            continue;

        JKRHeap *child = JKRExpHeap::create(size, parent, false);
        if (!child)
            continue;

        gRemoteActorHeap = child;
        gRemoteActorHeapOwned = true;
        OSReport("[SMSO] Remote actor stage-child heap created @ %p size=%u parent=%p parentFree=%u heapFree=%u\n",
                 gRemoteActorHeap, static_cast<u32>(size), parent, parentFree,
                 static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
        return true;
    }

    if (parentFree > kStageHeapReserveMarginTight + 0x000A0000u) {
        size_t adaptiveSize = parentFree - kStageHeapReserveMarginTight;
        adaptiveSize &= ~0x7FFFu;
        if (adaptiveSize >= 0x000A0000u) {
            JKRHeap *child = JKRExpHeap::create(adaptiveSize, parent, false);
            if (child) {
                gRemoteActorHeap = child;
                gRemoteActorHeapOwned = true;
                OSReport("[SMSO] Remote actor adaptive stage-child heap @ %p size=%u parent=%p parentFree=%u heapFree=%u\n",
                         gRemoteActorHeap, static_cast<u32>(adaptiveSize), parent, parentFree,
                         static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
                return true;
            }
        }
    }

    OSReport("[SMSO] Remote stage-child heap skipped: parent=%p parentFree=%u\n", parent,
             parentFree);
    return false;
}

static bool tryUpgradeToOwnedRemoteHeap() {
    if (gRemoteActorHeapOwned)
        return gRemoteActorHeap != nullptr;

    if (tryCreateExpandedMem1RemoteHeap())
        return true;
    if (tryCreateDedicatedRemoteHeap())
        return true;

    JKRHeap *parent = JKRHeap::sCurrentHeap ? JKRHeap::sCurrentHeap : resolveRemoteHeapParent();
    return tryCreateStageChildRemoteHeap(parent);
}

static bool tryCreateDedicatedRemoteHeap() {
    JKRHeap *parent = resolveRemoteHeapParent();
    if (!parent)
        return false;

    const u32 parentFree = static_cast<u32>(parent->getTotalFreeSize());
    static const size_t kDedicatedHeapSizes[] = {0x00280000u, 0x00200000u, 0x00180000u,
                                                 0x00140000u, 0x00100000u};

    for (size_t i = 0; i < sizeof(kDedicatedHeapSizes) / sizeof(kDedicatedHeapSizes[0]); ++i) {
        const size_t size = kDedicatedHeapSizes[i];
        if (parentFree < size)
            continue;

        gRemoteActorHeap = JKRExpHeap::create(size, parent, false);
        if (!gRemoteActorHeap)
            continue;

        gRemoteActorHeapOwned = true;
        OSReport("[SMSO] Remote actor dedicated heap created @ %p size=%u parent=%p parentFree=%u heapFree=%u\n",
                 gRemoteActorHeap, static_cast<u32>(size), parent, parentFree,
                 static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
        return true;
    }

    OSReport("[SMSO] Remote dedicated heap skipped: parent=%p parentFree=%u\n", parent, parentFree);
    return false;
}

static bool ensureRemoteActorHeap() {
    if (gRemoteActorHeap)
        return true;

    if (tryCreateExpandedMem1RemoteHeap())
        return true;
    if (tryCreateDedicatedRemoteHeap())
        return true;

    JKRHeap *parent = JKRHeap::sCurrentHeap ? JKRHeap::sCurrentHeap : resolveRemoteHeapParent();
    if (parent && tryCreateStageChildRemoteHeap(parent))
        return true;

    if (!parent) {
        OSReport("[SMSO] Remote heap unavailable: no parent heap\n");
        return false;
    }

    gRemoteActorHeap = parent;
    gRemoteActorHeapOwned = false;
    OSReport("[SMSO] Remote actor heap borrowing active heap @ %p free=%u totalFree=%u memSize=%u userRamEnd=%p\n",
             gRemoteActorHeap, static_cast<u32>(gRemoteActorHeap->getFreeSize()),
             static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()),
             static_cast<u32>(JKRHeap::mMemorySize), JKRHeap::mUserRamEnd);
    return true;
}

static void destroyRemoteActorHeap() {
    if (!gRemoteActorHeap)
        return;

    if (gRemoteActorHeapOwned) {
        OSReport("[SMSO] Remote actor heap destroyed @ %p free=%u totalFree=%u\n", gRemoteActorHeap,
                 static_cast<u32>(gRemoteActorHeap->getFreeSize()),
                 static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
        static_cast<JKRExpHeap *>(gRemoteActorHeap)->destroy();
    } else {
        OSReport("[SMSO] Remote actor heap released borrowed heap @ %p\n", gRemoteActorHeap);
    }
    gRemoteActorHeap = nullptr;
    gRemoteActorHeapOwned = false;
    gExpandedHeapFailed = false;
}

static TMario *spawnRemoteBody() {
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

    JKRHeap *previousHeap = JKRHeap::sCurrentHeap;
    gRemoteActorHeap->becomeCurrentHeap();

    auto *body = new (gRemoteActorHeap, 4) TMario();
    if (!body) {
        if (previousHeap)
            previousHeap->becomeCurrentHeap();
        return nullptr;
    }

    // TMario::initValues fully builds the puppet: it internally calls
    // initModel() and creates the cap, FLUDD, Yoshi, effects, and shadow
    // body (see decomp MarioInit.cpp). Do NOT call initModel separately.
    // initMirrorModel() duplicates a reflection rig and can leave a second FLUDD
    // pack visible on network puppets — skip it for remotes.
    body->initValues();

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
    disableRemotePickupInteraction(body, 0xFF);
    body->mAttributes.mIsInvisible = false;
    body->mAttributes.mIsGameOver = false;
    body->mAttributes.mHasFludd = true;
    applyRemoteCosmetics(body, 0);
    if (!isHideSeekActive())
        applyRemoteShirtVisibility(body);
    setBodyVisible(body, false);

    OSReport("[SMSO] Remote Mario body spawned @ %p remoteHeapFree=%u totalFree=%u\n", body,
             static_cast<u32>(gRemoteActorHeap->getFreeSize()),
             static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
    return body;
}

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

// Hand out the first pool body that is not currently bound to an active slot.
// All bodies already exist (see prewarmRemoteBodyPool), so this never allocates.
static TMario *acquirePoolBody() {
    for (u32 i = 0; i < gBodyPoolCount; ++i) {
        TMario *body = gBodyPool[i];
        if (body && !isPoolBodyAssigned(body))
            return body;
    }
    return nullptr;
}

// Allocate the entire remote puppet set in one shot on the first eligible
// gameplay frame. This is the SMSO 2 makeMarios() principle: front-load every
// puppet while the heap is freshest instead of spawning during play. Runs under
// the same gates as the old lazy spawn (stage normal, gpMarioAddress valid,
// player group found, connected) so TMario::initValues() is equally safe here.
static void prewarmRemoteBodyPool() {
    if (gBodyPoolPrewarmAttempted)
        return;
    gBodyPoolPrewarmAttempted = true;

    for (u32 i = 0; i < kSessionMaxRemotes; ++i) {
        TMario *body = spawnRemoteBody();
        if (!body) {
            OSReport("[SMSO] Remote body pool prewarm stopped at %u/%u (heap limited)\n",
                     gBodyPoolCount, kSessionMaxRemotes);
            break;
        }
        parkRemoteBody(body);
        gBodyPool[gBodyPoolCount++] = body;
    }

    OSReport("[SMSO] Remote body pool prewarmed: %u/%u bodies heapFree=%u owned=%d\n",
             gBodyPoolCount, kSessionMaxRemotes,
             gRemoteActorHeap ? static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()) : 0,
             gRemoteActorHeapOwned ? 1 : 0);
}

static bool ensureRemoteBody(RemoteActorSlot &slot, u32 slotId) {
    if (slot.spawned && slot.body)
        return true;

    TMario *body = acquirePoolBody();
    if (!body) {
        if (!gReportedBodyCap) {
            OSReport("[SMSO] Remote body pool exhausted (%u bodies), slot %u hidden\n",
                     gBodyPoolCount, slotId);
            gReportedBodyCap = true;
        }
        return false;
    }

    slot = {};
    slot.spawned = true;
    slot.body = body;
    resetRemoteRuntimeState(slot, true);
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
    if (slot.body) {
        parkRemoteBody(slot.body);
        if (wasActive)
            OSReport("[SMSO] Remote Mario body released to pool @ %p\n", slot.body);
    }

    slot.spawned = false;
    slot.body = nullptr;
    resetRemoteRuntimeState(slot, false);
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
        if (slot.appearRevealFrames == 0 && isRemoteBodyDrawVisible(&slot))
            setBodyVisible(body, true);
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

    // Mount puppet TYoshi before animation sync — host riding anims (0xB6..0xC6) only
    // work through TMario::setAnimation when onYoshi() is true (doldecomp MarioDraw.cpp).
    syncRemoteYoshiFromSnapshot(body, slot.yoshi, snap);
    applyRemoteFluddPresence(body, showFluddOnMarioBack, hostOnYoshi);

    if (!smso::isRemoteShineCollectActive(snap.slot))
        syncRemoteAnimation(body, &slot, snap, netUpper);
    syncRemoteHeadWaist(body, slot, snap);
    syncRemoteAnimAux(body, body->mFludd, snap.health, showFluddOnMarioBack);
    applyRemoteFacing(body, slot.yaw, snap.animId, body->mState, &slot);

    const bool isDead = (snap.vfxFlags & VFX_DEAD) != 0;
    smso::applyRemoteBlooperSurfSnapshot(body, slot.surf, snap);
    const bool surfing = !isDead && smso::snapshotIsBlooperSurfing(snap);

    applyRemoteCosmetics(body, snap.slot);
    if (!isHideSeekActive())
        applyRemoteShirtVisibility(body);
    applyRemoteYCamHelmet(body, snap.vfxFlags, slot.wasYCam);

    disableRemotePickupInteraction(body, snap.slot);

    if (showFluddOnMarioBack && body->mFludd) {
        slot.lastHealth = snap.health;
        const bool hostSwitching = (snap.vfxFlags & VFX_NOZZLE_SWITCHING) != 0;
        const u8 targetNozzle = clampNozzleId(unpackCurrentNozzle(snap.nozzleId));
        const bool nozzleMismatch =
            !hostSwitching && !slot.fluddSwitchLatched &&
            body->mFludd->mCurrentNozzle != targetNozzle;
        const bool needFluddSync = snapshotAnimChanged(slot, snap) || hostSwitching ||
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

        if (!(snap.vfxFlags & VFX_Y_CAM)) {
            const bool sprayingWater = (snap.vfxFlags & VFX_WATER_SPRAY) != 0;
            const bool drySpray = (snap.vfxFlags & VFX_FLUDD_EMPTY) != 0;

            if (sprayingWater) {
                slot.syncedSprayPressure = snap.water;
                if (slot.lastWaterTank > 0)
                    body->mFludd->mCurrentWater = slot.lastWaterTank;
            } else if (drySpray) {
                slot.lastWaterTank = 0;
                slot.syncedSprayPressure = 0;
                body->mFludd->mCurrentWater = 0;
            } else if (!surfing && netUpper > kUpperStateHoldingPump) {
                slot.lastWaterTank = snap.water;
                body->mFludd->mCurrentWater = snap.water;
            } else {
                body->mFludd->mCurrentWater = slot.lastWaterTank;
            }
        } else {
            body->mFludd->mCurrentWater = slot.lastWaterTank;
        }
    }

    syncRemoteParticleEdges(body, slot, snap);
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
        const int result = sOrigMarioPerform(mario, flags, graphics);
        if (mario == gpMarioAddress)
            mirrorRemotePerformGroup(flags, graphics);
        if ((flags & 0x205) != 0 && mario == gpMarioAddress)
            maintainLocalHideSeekSeekerDraw(mario, graphics);
        return result;
    }

    RemoteActorSlot *slot = findRemoteSlot(mario);
    // A pooled body with no slot is parked/idle (only assigned bodies are added
    // to the remote perform group), and an assigned-but-hidden body must not draw.
    if (!slot || !slot->visible)
        return 0;

    disableRemotePickupInteraction(mario, slot ? slot->rosterSlot : 0xFF);

    const bool showFluddOnMarioBack = slot && (slot->vfxFlags & VFX_NO_FLUDD) == 0;

    if (flags & 0x205) {
        tickRemoteAppearReveal(slot);
        remoteCalcAnim(mario, slot, graphics);

        emitPendingRemoteWarpVfx(mario, slot);

        syncRemoteContinuousParticles(mario, slot);

        if (snapshotHostOnYoshi(slot->nozzleId, slot->vfxFlags) &&
            smso::remoteBodyRidingYoshi(mario)) {
            emitRemoteYoshiJuiceSpray(mario, slot, slot->vfxFlags);
        } else if (showFluddOnMarioBack && mario->mFludd &&
                   remoteFluddPerformSafe(mario, mario->mFludd)) {
            const u16 vfx = slot ? slot->vfxFlags : static_cast<u16>(0);
            bindRemoteFludd(mario, slot, vfx, graphics);
        }
    }

    const bool drawBody = isRemoteBodyDrawVisible(slot);

    u32 savedSurfState = 0;
    bool strippedSurfDraw = false;
    // Remote mSurfGesso is an MActor mesh clone, not a live TSurfGesso. Retail calcView /
    // entryModels would dispatch TSurfGesso::perform through the wrong vtable and crash.
    if (smso::isBlooperSurfState(mario->mState)) {
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
            if (++gRemotePerformDrawDiag <= 3)
                OSReport("[SMSO] Remote body draw perform slot=%p flags=0x200\n", mario);
            // Remote perform group is not on Player Group's calcView pass in every pipeline
            // ordering; ensure view matrices exist before entryModels (doldecomp preEntry 0x4).
            if ((flags & 0x4) == 0)
                mario->calcView(graphics);
            mario->addDirty();
            if (showFluddOnMarioBack && remoteFluddPerformSafe(mario, mario->mFludd)) {
                mario->mFludd->mIsEmitWater = false;
                mario->mFludd->perform(0x200, graphics);
            }
            mario->entryModels(graphics);
            performRemoteYoshiDraw(mario, flags, graphics, drawBody);
            const u16 vfx = slot ? slot->vfxFlags : static_cast<u16>(0);
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
    gPlayerGroup = nullptr;
    gRemotePerformGroupRegistered = false;
    gRemotePerformDrawDiag = 0;
    clearRemotePerformGroupMembers();
    gReportedMissingPlayerGroup = false;
    gReportedBodyCap = false;
    gReportedHeapShortage = false;
    gRemoteHeapReserved = false;
    // Pool bodies live in the remote heap, which is destroyed on stage exit, so
    // every stage starts with an empty pool to be prewarmed on the first frame.
    gBodyPoolCount = 0;
    gBodyPoolPrewarmAttempted = false;
    for (auto &body : gBodyPool)
        body = nullptr;
    for (auto &slot : gActors)
        slot = {};

    // Reserve remote heap at stage init while the stage heap is still mostly empty.
    // Waiting until the first connected update often leaves too little free memory for 3 bodies.
    CommBuffer *buf = getCommBuffer();
    if (buf && (buf->bridgeFlags & BF_CONNECTED) != 0) {
        ensureRemoteActorHeap();
        gRemoteHeapReserved = gRemoteActorHeap != nullptr;
        if (gRemoteHeapReserved) {
            OSReport("[SMSO] Remote actor heap reserved at stage init @ %p owned=%d free=%u\n",
                     gRemoteActorHeap, gRemoteActorHeapOwned ? 1 : 0,
                     static_cast<u32>(gRemoteActorHeap->getTotalFreeSize()));
        }
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
        return;
    }

    if (!gRemoteHeapReserved) {
        gRemoteHeapReserved = true;
        ensureRemoteActorHeap();
    }

    // Front-load the whole puppet pool the first time we reach a connected,
    // stage-ready frame. After this, no remote body is ever allocated during
    // gameplay — joins/leaves only assign or release an existing pool body.
    prewarmRemoteBodyPool();

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
            resetRemoteRuntimeState(slot, true);
            OSReport("[SMSO] Remote Mario body slot %u activated @ %p\n", i, slot.body);
        }

        if (!slot.inViewList) {
            gRemotePerformGroup->mViewObjList.push_back(slot.body);
            slot.inViewList = true;
            OSReport("[SMSO] Remote Mario body slot %u registered in remote perform group\n", i);
        }

        if (!slot.visible)
            slot.visible = true;

        applySnapshotToBody(slot, snap);

        if (!slot.inWarpTransition)
            advanceRemoteDisplayMotion(slot, slot.body);

        if (slot.appearRevealFrames == 0 && !slot.pendingStageAppear && !slot.pendingWarpInVfx)
            setBodyVisible(slot.body, true);
        else
            setBodyVisible(slot.body, false);
    }

    if (++gVisibilityDiagFrame % 300 == 0) {
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
        OSReport("[SMSO] Remote visibility diag localSlot=%u connected=%u spawned=%u visible=%u inView=%u pool=%u/%u heapFree=%u owned=%d\n",
                 localSlot, connected, spawned, visible, inView, gBodyPoolCount, kSessionMaxRemotes,
                 heapFree, gRemoteActorHeapOwned ? 1 : 0);
    }
}

void clearRemoteActors() {
    gPlayerGroup = nullptr;
    gRemotePerformGroupRegistered = false;
    gRemotePerformDrawDiag = 0;
    clearRemotePerformGroupMembers();
    gReportedMissingPlayerGroup = false;
    gRemoteHeapReserved = false;
    for (auto &slot : gActors)
        slot = {};
    // Pool bodies are owned by the remote heap; destroying it frees them all.
    gBodyPoolCount = 0;
    gBodyPoolPrewarmAttempted = false;
    for (auto &body : gBodyPool)
        body = nullptr;
    destroyRemoteActorHeap();
}

bool hasRemoteBodyForSlot(u8 slot) {
    return slot < MAX_REMOTE_SLOTS && gActors[slot].spawned && gActors[slot].body != nullptr &&
           gActors[slot].inViewList;
}

TMario *getRemoteBodyForSlot(u8 slot) {
    if (!hasRemoteBodyForSlot(slot))
        return nullptr;
    return gActors[slot].body;
}

bool getRemoteBodyPosition(u8 slot, f32 &x, f32 &y, f32 &z) {
    if (!hasRemoteBodyForSlot(slot))
        return false;

    const TMario *body = gActors[slot].body;
    x = body->mTranslation.x;
    y = body->mTranslation.y;
    z = body->mTranslation.z;
    return true;
}

static constexpr f32 kHeadCrownWorldOffset = 18.0f;
static constexpr f32 kHeadFallbackWorldOffset = 232.0f;

bool getRemoteHeadAnchorPosition(u8 slot, f32 &x, f32 &y, f32 &z) {
    if (!hasRemoteBodyForSlot(slot))
        return false;

    const TMario *body = gActors[slot].body;
    if (body->mModelData && body->mModelData->mModel && body->mModelData->mModel->mJointArray) {
        const u8 headJoint = body->mBindBoneIDArray[10];
        const Mtx &headMtx = body->mModelData->mModel->mJointArray[headJoint];
        const Vec localCrown = {0.0f, kHeadCrownWorldOffset, 0.0f};
        Vec worldCrown{};
        MTXMultVec(headMtx, &localCrown, &worldCrown);
        x = worldCrown.x;
        y = worldCrown.y;
        z = worldCrown.z;
        return x == x && y == y && z == z;
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

} // namespace smso

#else

namespace smso {

void initRemoteActors() {}
void updateRemoteActors(TMarDirector *) {}
void clearRemoteActors() {}

bool hasRemoteBodyForSlot(u8 slot) {
    (void)slot;
    return false;
}

TMario *getRemoteBodyForSlot(u8 slot) {
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

} // namespace smso

#endif
