#include "graffiti_clean_sync.hpp"

#include "remote_actor.hpp"
#include "world_sync.hpp"

#include <Dolphin/MTX.h>
#include <Dolphin/OS.h>
#include <SMS/Map/BGCheck.hxx>
#include <SMS/Map/MapCollisionData.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/Watergun.hxx>
#include <SMS/Player/NozzleBase.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/raw_fn.hxx>
#include <math.h>
#include <sdk.h>

extern TMarDirector *gpMarDirector;
extern TMario *gpMarioAddress;
extern TMapCollisionData *gpMapCollisionData;

// doldecomp gpPollution @ NTSC-U 0x8040DED0
#define smso_gpPollution (*reinterpret_cast<void **>(SMS_PORT_REGION(0x8040DED0, 0, 0, 0x8040DED0)))

namespace {

// Dense 3D grid for progressive spray fill-in. Local clean() fires every particle
// hit; remotes only get one durable stamp per newly entered cell. Wall M graffiti
// moves primarily in Y at nearly fixed XZ — XZ-only cells collapsed an entire
// letter into one grow-only stamp. 32u XYZ cells track all axes.
constexpr f32 kGraffitiCellSize = 32.0f;
constexpr u16 kMaxStageGraffitiCleans = 384;
constexpr u16 kStageSettleFrames = 90;
constexpr f32 kDefaultCleanRadius = 64.0f;
// Wall splash uses mCleanSize*32 (~400–576). That radius with stamp() previously
// corrupted pollution mesh vertices (sky spikes). Clamp keeps all-plane stamp safe.
constexpr f32 kMaxSyncCleanRadius = 128.0f;
// Wall catch-up brush. Keep well under retail splashWall (~400–576): one-shot is
// safe, but assist/replay stacking at near-retail sizes revived sky-spike stretch.
constexpr f32 kMaxWallSyncCleanRadius = 160.0f;
// Floor for remote apply so an M letter / large splat is covered from the first
// spray point in a cell (cell-center-only stamps left edges behind).
constexpr f32 kMinSyncCleanRadius = 96.0f;
constexpr f32 kSizeQuantScale = 8.0f;
constexpr f32 kWallSplashSizeThreshold = 200.0f; // splashWall is typically 400–576
// Vertical wall M / plaza X: modest Y satellites (wall tex is Y-major).
constexpr f32 kWallStampYStep = 64.0f;
constexpr int kWallStampYSteps = 2; // ±64, ±128
constexpr u16 kDiagReportIntervalFrames = 180; // ~3s at 60Hz

// Viewer-side remote spray clean assist (does NOT publish).
// Retail splashGround ≈ mCleanSize*10; splashWall ≈ mCleanSize*32.
// Assist must NOT re-stamp every frame at retail wall sizes — that is the
// remaining sky-spike / stretch source after durable one-shot fixes.
constexpr f32 kAssistGroundCleanSize = 96.0f;
constexpr f32 kAssistWallCleanSize = 128.0f;
constexpr f32 kSprayAssistRayLength = 1200.0f;
// Pull ray start back so close-range wall spray does not begin inside geometry
// (plaza monument M — nozzle often clips the face → intersectLine miss).
constexpr f32 kSprayAssistRayPullback = 250.0f;
// Plaza dock pedestal face is slightly slanted; |ny|<0.55 rejected real wall hits.
constexpr f32 kWallNormalYAbsMax = 0.85f;
constexpr f32 kAssistBodyChestY = 80.0f;
constexpr u8 kAssistEmitFreshFrames = 8;

// packCollectibleWorldPos: scale 16, bias 256 → world roughly -4096..12272.
constexpr f32 kWorldPosPackMin = -4096.0f;
constexpr f32 kWorldPosPackMax = 12272.0f;

// Plaza hub episode coalesce (matches StoryFlagAuthority.PlazaHubEpisode).
constexpr u8 kPlazaAreaId = 1;
constexpr u8 kPlazaHubEpisode = 0xFFu;

// reserved bits on WE_GRAFFITI_CLEANED
constexpr u8 kGraffitiReservedWall = 0x01;
constexpr u8 kGraffitiReservedFinishing = 0x02;

// payload2 packing: 10-bit signed cellX|cellY|cellZ + bit30 valid marker.
// Signed 10-bit range [-512,511] → world ±16k at 32u — enough for SMS stages.
constexpr u32 kCellPackValidBit = 1u << 30;
constexpr u32 kCellPackAxisMask = 0x3FFu;

struct GraffitiStamp {
    f32 x, y, z, size;
    s16 cellX, cellY, cellZ;
    bool active;
};

static GraffitiStamp sLocalPublished[kMaxStageGraffitiCleans] = {};
static u16 sLocalPublishedCount = 0;
static GraffitiStamp sPendingApply[kMaxStageGraffitiCleans] = {};
static u16 sPendingApplyCount = 0;
// Assist one-shot cells — prevent per-frame re-stamp while a remote keeps spraying
// the same wall face (historical sky-spike cause with size≈480 every frame).
static GraffitiStamp sAssistApplied[kMaxStageGraffitiCleans] = {};
static u16 sAssistAppliedCount = 0;
static u16 sStageSettleFrames = 0;
static u8 sLastCourseId = 0xFF;
static u8 sLastEpisodeId = 0xFF;
static bool sApplyingRemote = false;
static bool sHookInstalled = false;
static u32 sGraffitiPublishCount = 0;
static u32 sGraffitiApplyCount = 0;
static u32 sGraffitiAssistCount = 0;
static u32 sGraffitiAssistTryCount = 0;
static u32 sGraffitiAssistMissHitCount = 0;
static u32 sGraffitiPackFallbackCount = 0;
static u16 sDiagFrameCounter = 0;

// Last remote spray emit ray (filled by notifyRemoteSprayEmit during droplet emit).
struct SprayAssistRay {
    f32 ox, oy, oz;
    f32 dx, dy, dz;
    u8 freshFrames;
    bool valid;
};
static SprayAssistRay sLastSprayEmitRay = {};
static bool sEmitAssistThisFrame = false;

static u8 currentCourseId() {
    return gpMarDirector ? static_cast<u8>(gpMarDirector->mAreaID) : 0;
}

static u8 currentEpisodeId() {
    return gpMarDirector ? static_cast<u8>(gpMarDirector->mEpisodeID) : 0;
}

// Publish plaza graffiti under hub episode 255 so server authority does not split
// across decideNextScenario episode buckets.
static u8 graffitiPublishEpisodeId() {
    if (currentCourseId() == kPlazaAreaId)
        return kPlazaHubEpisode;
    return currentEpisodeId();
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

// Graffiti apply / stage-tracker: course must match; episodes need not be byte-equal.
// Plaza (1): decideNextScenario advances mEpisodeID without soft-reload — treat all
// plaza episodes (including hub 255) as equivalent so remotes do not consume-and-skip.
// Casino (14): catalog 0/1 ↔ mission 3/4.
static bool graffitiEpisodesEquivalent(u8 courseId, u8 a, u8 b) {
    if (a == b)
        return true;
    if (courseId == kPlazaAreaId)
        return true; // Delfino Plaza hub (any episode incl. 0xFF)
    if (courseId == 14)
        return sameCasinoEpisode(a, b);
    return false;
}

static bool graffitiStageMatches(u8 courseId, u8 episodeId) {
    if (courseId != currentCourseId())
        return false;
    return graffitiEpisodesEquivalent(courseId, episodeId, currentEpisodeId());
}

static bool graffitiPublishEnabled(const smso::CommBuffer *buf) {
    return buf && (buf->bridgeFlags & smso::BF_CONNECTED) != 0 &&
           (buf->bridgeFlags & (smso::BF_SYNC_EVENT | smso::BF_SYNC_MISSION)) != 0;
}

static bool localPlayerAuthoredClean() {
    if (!gpMarioAddress || !gpMarioAddress->mFludd)
        return false;
    // Only the spraying client's water particles should author graffiti events.
    // Remote visual droplets also call clean(); gating prevents echo floods.
    if (gpMarioAddress->mFludd->mIsEmitWater)
        return true;
    if (gpMarioAddress->mAttributes.mIsFluddEmitting)
        return true;
    return false;
}

static s16 cellCoord(f32 v) {
    // Avoid floorf (not always available under Kuribo). Truncate toward -inf for negatives.
    const f32 scaled = v / kGraffitiCellSize;
    const s32 trunc = static_cast<s32>(scaled);
    if (scaled < 0.0f && scaled != static_cast<f32>(trunc))
        return static_cast<s16>(trunc - 1);
    return static_cast<s16>(trunc);
}

static s16 signExtend10(u32 bits) {
    bits &= kCellPackAxisMask;
    if (bits & 0x200u)
        return static_cast<s16>(static_cast<s32>(bits) - 0x400);
    return static_cast<s16>(bits);
}

static u32 packCell3(s16 cellX, s16 cellY, s16 cellZ) {
    return (static_cast<u32>(cellX) & kCellPackAxisMask) |
           ((static_cast<u32>(cellY) & kCellPackAxisMask) << 10) |
           ((static_cast<u32>(cellZ) & kCellPackAxisMask) << 20) | kCellPackValidBit;
}

static bool unpackCell3(u32 packed, s16 &cellX, s16 &cellY, s16 &cellZ) {
    if ((packed & kCellPackValidBit) == 0)
        return false;
    cellX = signExtend10(packed);
    cellY = signExtend10(packed >> 10);
    cellZ = signExtend10(packed >> 20);
    return true;
}

static f32 clampSyncCleanRadius(f32 size, bool allowWallBoost) {
    const f32 maxR = allowWallBoost ? kMaxWallSyncCleanRadius : kMaxSyncCleanRadius;
    if (size < kMinSyncCleanRadius)
        size = kMinSyncCleanRadius;
    if (size > maxR)
        size = maxR;
    return size;
}

static u8 quantizeSize(f32 size, bool allowWallBoost) {
    size = clampSyncCleanRadius(size, allowWallBoost);
    const u32 q = static_cast<u32>(size / kSizeQuantScale + 0.5f);
    if (q < 1)
        return 1;
    if (q > 255)
        return 255;
    return static_cast<u8>(q);
}

static f32 dequantizeSize(u8 quant, bool allowWallBoost) {
    if (quant == 0)
        return kDefaultCleanRadius;
    return clampSyncCleanRadius(static_cast<f32>(quant) * kSizeQuantScale, allowWallBoost);
}

static bool stampExists(const GraffitiStamp *list, u16 count, s16 cellX, s16 cellY, s16 cellZ) {
    for (u16 i = 0; i < count; ++i) {
        if (list[i].active && list[i].cellX == cellX && list[i].cellY == cellY &&
            list[i].cellZ == cellZ)
            return true;
    }
    return false;
}

static bool rememberStamp(GraffitiStamp *list, u16 &count, f32 x, f32 y, f32 z, f32 size, s16 cellX,
                          s16 cellY, s16 cellZ) {
    if (stampExists(list, count, cellX, cellY, cellZ))
        return false;
    if (count >= kMaxStageGraffitiCleans)
        return false;
    GraffitiStamp &slot = list[count++];
    slot.x = x;
    slot.y = y;
    slot.z = z;
    slot.size = size;
    slot.cellX = cellX;
    slot.cellY = cellY;
    slot.cellZ = cellZ;
    slot.active = true;
    return true;
}

static void clearTrackers() {
    sLocalPublishedCount = 0;
    sPendingApplyCount = 0;
    sAssistAppliedCount = 0;
    for (u32 i = 0; i < kMaxStageGraffitiCleans; ++i) {
        sLocalPublished[i] = {};
        sPendingApply[i] = {};
        sAssistApplied[i] = {};
    }
    sGraffitiPublishCount = 0;
    sGraffitiApplyCount = 0;
    sGraffitiAssistCount = 0;
    sGraffitiAssistTryCount = 0;
    sGraffitiAssistMissHitCount = 0;
    sGraffitiPackFallbackCount = 0;
    sDiagFrameCounter = 0;
    sLastSprayEmitRay = {};
    sEmitAssistThisFrame = false;
}

static void retailClean(void *pollution, f32 x, f32 y, f32 z, f32 size) {
    // doldecomp TPollutionManager::clean — skip deep Bianco water, then stamp clean.bti.
    if (gpMarDirector && gpMarDirector->mAreaID == 1 && y < -10.0f)
        return;
    stamp__17TPollutionManagerFUsffff(pollution, 0, x, y, z, size);
}

// Remote apply: retail stamp(0,…) across ALL pollution planes (ground + wall
// PlusX/PlusZ/etc.). stampGround only hits planeType==0 and silently missed wall
// M graffiti. Radius clamp prevents the old sky-spike stretch from huge wall
// splash sizes (~400–576) on wall tex axes.
static void applyCleanStampOnce(f32 x, f32 y, f32 z, f32 size, bool allowWallBoost) {
    void *pollution = smso_gpPollution;
    if (!pollution)
        return;

    size = clampSyncCleanRadius(size, allowWallBoost);
    if (!(x == x) || !(y == y) || !(z == z) || !(size == size))
        return;
    if (size < 1.0f)
        return;

    // Plaza / Bianco deep-water guard (same as retail clean).
    if (gpMarDirector && gpMarDirector->mAreaID == 1 && y < -10.0f)
        return;

    sApplyingRemote = true;
    retailClean(pollution, x, y, z, size);
    sApplyingRemote = false;
}

// Wall catch-up: center + modest Y satellites. Vertical wall M / plaza X need Y
// coverage; keep satellite radius on the ground clamp so stacked stamps cannot
// explode wall-tex vertices (sky-spike).
static void applyCleanStampPattern(f32 x, f32 y, f32 z, f32 size, bool allowWallBoost) {
    applyCleanStampOnce(x, y, z, size, allowWallBoost);
    if (!allowWallBoost)
        return;

    // Satellites always use ground clamp (allowWallBoost=false → max 128).
    const f32 satellite = clampSyncCleanRadius(size * 0.75f, false);
    for (int step = 1; step <= kWallStampYSteps; ++step) {
        const f32 dy = kWallStampYStep * static_cast<f32>(step);
        applyCleanStampOnce(x, y + dy, z, satellite, false);
        applyCleanStampOnce(x, y - dy, z, satellite, false);
    }
}

// Pack spray XYZ for payload1. If the true hit is outside the 10-bit collectible
// pack range, fall back to cell-center then axis-clamped coords so rememberStamp
// never advances cellsLocal without a matching publish (dolphin.log: publish stuck
// while cellsLocal kept growing).
static u32 packGraffitiWorldPos(f32 x, f32 y, f32 z, s16 cellX, s16 cellY, s16 cellZ) {
    u32 packed = smso::packCollectibleWorldPos(x, y, z);
    if (smso::isValidPackedWorldPos(packed))
        return packed;

    const f32 cx = (static_cast<f32>(cellX) + 0.5f) * kGraffitiCellSize;
    const f32 cy = (static_cast<f32>(cellY) + 0.5f) * kGraffitiCellSize;
    const f32 cz = (static_cast<f32>(cellZ) + 0.5f) * kGraffitiCellSize;
    packed = smso::packCollectibleWorldPos(cx, cy, cz);
    if (smso::isValidPackedWorldPos(packed)) {
        ++sGraffitiPackFallbackCount;
        return packed;
    }

    auto clampAxis = [](f32 v) -> f32 {
        if (v < kWorldPosPackMin)
            return kWorldPosPackMin;
        if (v > kWorldPosPackMax)
            return kWorldPosPackMax;
        return v;
    };
    packed = smso::packCollectibleWorldPos(clampAxis(x), clampAxis(y), clampAxis(z));
    if (smso::isValidPackedWorldPos(packed))
        ++sGraffitiPackFallbackCount;
    return packed;
}

static void publishLocalGraffitiClean(f32 x, f32 y, f32 z, f32 size, s16 cellX, s16 cellY,
                                      s16 cellZ, u8 reserved) {
    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!graffitiPublishEnabled(buf))
        return;

    const u32 packed = packGraffitiWorldPos(x, y, z, cellX, cellY, cellZ);
    if (!smso::isValidPackedWorldPos(packed)) {
        OSReport("[SMSOBB] graffiti publish skipped — pack failed cell=(%d,%d,%d) "
                 "pos=(%.0f,%.0f,%.0f)\n",
                 cellX, cellY, cellZ, x, y, z);
        return;
    }

    const bool wallBoost =
        (reserved & (kGraffitiReservedWall | kGraffitiReservedFinishing)) != 0;
    const u8 sizeQuant = quantizeSize(size, wallBoost);
    const u32 cellPacked = packCell3(cellX, cellY, cellZ);
    smso::enqueueLocalWorldEvent(static_cast<u8>(smso::WE_GRAFFITI_CLEANED), currentCourseId(),
                                 graffitiPublishEpisodeId(), sizeQuant, reserved, packed,
                                 cellPacked);
    ++sGraffitiPublishCount;
}

// One-shot settle reapply for stamps that deferred because gpPollution was null.
// CRITICAL: mark inactive after apply — re-stamping every frame forever corrupts
// pollution geometry (Sirena electric / plaza sky-spike stretch).
static void reconcilePendingApplies() {
    if (sPendingApplyCount == 0 || sApplyingRemote)
        return;
    if (sStageSettleFrames < kStageSettleFrames)
        return;
    if (!smso_gpPollution)
        return;

    for (u16 i = 0; i < sPendingApplyCount; ++i) {
        if (!sPendingApply[i].active)
            continue;
        applyCleanStampPattern(sPendingApply[i].x, sPendingApply[i].y, sPendingApply[i].z,
                               sPendingApply[i].size, /*allowWallBoost=*/true);
        sPendingApply[i].active = false;
        ++sGraffitiApplyCount;
    }
}

// ---------------------------------------------------------------------------
 // Viewer-side remote spray clean assist
 // Remote FLUDD is visual-only (mIsEmitWater=false). Particles *can* splashWall→
 // clean but are unreliable. Primary path: notifyRemoteSprayEmit (same emit ray
 // as droplets) immediately retail-cleans. Backup: per-frame snapshot spray +
 // pullback raycast / body-forward fallback. Never publishes.
 // ---------------------------------------------------------------------------

using MapCollisionIntersectLineFn = const TBGCheckData *(*)(const TMapCollisionData *, const TVec3f &,
                                                            const TVec3f &, bool, TVec3f *);

static const TBGCheckData *mapCollisionIntersectLine(const TVec3f &start, const TVec3f &end,
                                                     TVec3f *hitPos) {
    if (!gpMapCollisionData)
        return nullptr;
    const auto fn = reinterpret_cast<MapCollisionIntersectLineFn>(
        intersectLine__17TMapCollisionDataCFRCQ29JGeometry8TVec3_f);
    return fn(gpMapCollisionData, start, end, false, hitPos);
}

static bool emitMtxTranslationValid(const Mtx &mtx) {
    const f32 x = mtx[0][3];
    const f32 y = mtx[1][3];
    const f32 z = mtx[2][3];
    return x == x && y == y && z == z;
}

static bool normalizeDir(TVec3f *dir) {
    if (!dir)
        return false;
    const f32 lenSq = dir->x * dir->x + dir->y * dir->y + dir->z * dir->z;
    if (lenSq < 1.0e-4f)
        return false;
    const f32 inv = 1.0f / sqrtf(lenSq);
    dir->x *= inv;
    dir->y *= inv;
    dir->z *= inv;
    return true;
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

    return normalizeDir(outDir);
}

static bool tryGetBodyForwardAim(TMario *mario, TVec3f *outOrigin, TVec3f *outDir) {
    if (!mario || !outOrigin || !outDir)
        return false;

    outOrigin->x = mario->mTranslation.x;
    outOrigin->y = mario->mTranslation.y + kAssistBodyChestY;
    outOrigin->z = mario->mTranslation.z;

    // SMS yaw: s16 angle → approx radians via /182.04445 (65536/360).
    const f32 yaw = static_cast<f32>(mario->mAngle.y) / 182.04445f;
    outDir->x = sinf(yaw);
    outDir->y = 0.0f;
    outDir->z = cosf(yaw);

    // Blend in FLUDD gun pitch when available (negative while aiming up/hover).
    if (mario->mFludd) {
        const u8 nozzleId = mario->mFludd->mCurrentNozzle;
        TNozzleBase *nozzle =
            nozzleId <= TWaterGun::Turbo ? mario->mFludd->mNozzleList[nozzleId] : nullptr;
        if (nozzle) {
            const s16 gun = nozzle->getGunAngle();
            const f32 pitch = static_cast<f32>(gun) / 182.04445f;
            const f32 cp = cosf(pitch);
            const f32 sp = sinf(pitch);
            outDir->x *= cp;
            outDir->z *= cp;
            outDir->y = sp;
        }
    }

    return normalizeDir(outDir);
}

static bool hitLooksLikeWall(const TBGCheckData *hit) {
    if (!hit)
        return false;
    // Near-horizontal normals are floors/roofs. Plaza monument faces are slanted —
    // keep threshold loose so wall graffiti uses splashWall-sized brushes.
    const TVec3f *n = hit->getNormal();
    if (!n)
        return true; // unknown → treat as wall (safer for graffiti clear)
    const f32 ay = n->y < 0.0f ? -n->y : n->y;
    return ay < kWallNormalYAbsMax;
}

static void assistCleanAtHit(f32 x, f32 y, f32 z, f32 size, bool wall) {
    void *pollution = smso_gpPollution;
    if (!pollution)
        return;
    if (!(x == x) || !(y == y) || !(z == z) || !(size == size) || size < 1.0f)
        return;
    if (gpMarDirector && gpMarDirector->mAreaID == 1 && y < -10.0f)
        return;

    // One-shot per 32u cell — perpetual assist re-stamp was the remaining stretch
    // source (dolphin.log assist climbing while wall size≈480 every frame).
    const s16 cx = cellCoord(x);
    const s16 cy = cellCoord(y);
    const s16 cz = cellCoord(z);
    if (!rememberStamp(sAssistApplied, sAssistAppliedCount, x, y, z, size, cx, cy, cz))
        return;

    size = clampSyncCleanRadius(size, wall);

    // sApplyingRemote → trampoline skips publish (cleaner already publishes).
    sApplyingRemote = true;
    retailClean(pollution, x, y, z, size);
    if (wall) {
        const f32 sat = clampSyncCleanRadius(size * 0.75f, false);
        retailClean(pollution, x, y + kWallStampYStep, z, sat);
        retailClean(pollution, x, y - kWallStampYStep, z, sat);
    }
    sApplyingRemote = false;
    ++sGraffitiAssistCount;
}

static bool assistCleanFromRay(f32 ox, f32 oy, f32 oz, f32 dx, f32 dy, f32 dz) {
    ++sGraffitiAssistTryCount;

    TVec3f dir{dx, dy, dz};
    if (!normalizeDir(&dir)) {
        ++sGraffitiAssistMissHitCount;
        return false;
    }

    // Pull origin back along -dir so close-range wall spray is not inside the face.
    const TVec3f start{ox - dir.x * kSprayAssistRayPullback, oy - dir.y * kSprayAssistRayPullback,
                       oz - dir.z * kSprayAssistRayPullback};
    const TVec3f end{ox + dir.x * kSprayAssistRayLength, oy + dir.y * kSprayAssistRayLength,
                     oz + dir.z * kSprayAssistRayLength};

    TVec3f hitPos{};
    const TBGCheckData *hit = mapCollisionIntersectLine(start, end, &hitPos);
    if (!hit || hit->isIllegalData() || hit->isWaterSurface()) {
        ++sGraffitiAssistMissHitCount;
        return false;
    }
    // Do not reject isMarioThrough — some wall faces flag through for Mario movement
    // but still hold pollution layers (plaza pedestal).

    const bool wall = hitLooksLikeWall(hit);
    const f32 size = wall ? kAssistWallCleanSize : kAssistGroundCleanSize;
    assistCleanAtHit(hitPos.x, hitPos.y, hitPos.z, size, wall);
    return true;
}

static void updateRemoteSprayCleanAssist() {
    if (sApplyingRemote || !smso_gpPollution)
        return;
    if (!gpMapCollisionData || gpMapCollisionData->mCheckDataCount == 0)
        return;

    smso::CommBuffer *buf = smso::getCommBuffer();
    if (!graffitiPublishEnabled(buf))
        return;

    // Droplet emit already cleaned this frame — do not double-stamp.
    if (sEmitAssistThisFrame) {
        sEmitAssistThisFrame = false;
        if (sLastSprayEmitRay.freshFrames > 0)
            --sLastSprayEmitRay.freshFrames;
        if (sLastSprayEmitRay.freshFrames == 0)
            sLastSprayEmitRay.valid = false;
        return;
    }

    // Prefer a recently captured emit ray (matches VFX between 30 Hz emit ticks).
    if (sLastSprayEmitRay.valid && sLastSprayEmitRay.freshFrames > 0) {
        assistCleanFromRay(sLastSprayEmitRay.ox, sLastSprayEmitRay.oy, sLastSprayEmitRay.oz,
                           sLastSprayEmitRay.dx, sLastSprayEmitRay.dy, sLastSprayEmitRay.dz);
        --sLastSprayEmitRay.freshFrames;
        if (sLastSprayEmitRay.freshFrames == 0)
            sLastSprayEmitRay.valid = false;
        return;
    }

    for (u32 i = 0; i < smso::MAX_REMOTE_SLOTS; ++i) {
        if (i == buf->localSlot)
            continue;

        const smso::PlayerSnapshot &snap = buf->remoteSnapshots[i];
        if (snap.connected == 0)
            continue;
        if ((snap.vfxFlags & smso::VFX_WATER_SPRAY) == 0)
            continue;
        if ((snap.vfxFlags & smso::VFX_FLUDD_EMPTY) != 0)
            continue;

        // Mailbox index == actor slot (bridge writes remoteSnapshots[slot]).
        TMario *body = smso::getRemoteBodyForSlot(static_cast<u8>(i));
        if (!body)
            body = smso::getRemoteBodyForSlotLoose(static_cast<u8>(i));
        if (!body)
            continue;

        TVec3f origin{}, dir{};
        bool haveRay = tryGetFluddEmit(body, &origin, &dir);
        if (!haveRay)
            haveRay = tryGetBodyForwardAim(body, &origin, &dir);
        if (!haveRay)
            continue;

        assistCleanFromRay(origin.x, origin.y, origin.z, dir.x, dir.y, dir.z);
    }
}

static void maybeReportGraffitiDiag() {
    if (++sDiagFrameCounter < kDiagReportIntervalFrames)
        return;
    sDiagFrameCounter = 0;
    if (sGraffitiPublishCount == 0 && sGraffitiApplyCount == 0 && sGraffitiAssistCount == 0 &&
        sGraffitiAssistTryCount == 0)
        return;
    OSReport("[SMSOBB] graffiti diag publish=%u apply=%u assist=%u try=%u missHit=%u packFb=%u "
             "cellsLocal=%u\n",
             sGraffitiPublishCount, sGraffitiApplyCount, sGraffitiAssistCount,
             sGraffitiAssistTryCount, sGraffitiAssistMissHitCount, sGraffitiPackFallbackCount,
             sLocalPublishedCount);
}

} // namespace

// Trampoline target for clean__17TPollutionManagerFffff @ 0x8019DDB4.
// Must keep C++ ABI: this-pointer first.
extern "C" void smso_pollutionCleanHook(void *self, f32 x, f32 y, f32 z, f32 size) {
    retailClean(self, x, y, z, size);

    if (sApplyingRemote)
        return;

    smso::onLocalPollutionClean(x, y, z, size);
}

// Redirect retail clean entry to our trampoline (NTSC-U).
SMS_PATCH_B(SMS_PORT_REGION(0x8019DDB4, 0, 0, 0x8019DDB4), smso_pollutionCleanHook);

namespace smso {

void onLocalPollutionClean(f32 x, f32 y, f32 z, f32 size) {
    if (sApplyingRemote)
        return;
    if (!localPlayerAuthoredClean())
        return;

    CommBuffer *buf = getCommBuffer();
    if (!graffitiPublishEnabled(buf))
        return;
    if (!gpMarDirector || gpMarDirector->mCurState != TMarDirector::STATE_NORMAL)
        return;

    const bool wallHit = size >= kWallSplashSizeThreshold;
    // Cap before publish so wall splash sizes (~576) never enter the event stream raw.
    size = clampSyncCleanRadius(size, wallHit);

    const s16 cellX = cellCoord(x);
    const s16 cellY = cellCoord(y);
    const s16 cellZ = cellCoord(z);
    const bool isNewCell =
        rememberStamp(sLocalPublished, sLocalPublishedCount, x, y, z, size, cellX, cellY, cellZ);

    if (isNewCell) {
        // Publish actual spray XYZ (payload1). Cell is dedupe-only — remotes must
        // stamp at the first-spray point in the cell, not the cell center, or M
        // graffiti / large splats leave uncleared edges.
        // Wall reserved lets remotes multi-stamp along Y for vertical coverage.
        const u8 reserved = wallHit ? kGraffitiReservedWall : 0;
        publishLocalGraffitiClean(x, y, z, size, cellX, cellY, cellZ, reserved);
    }
}

void initGraffitiCleanSync() {
    clearTrackers();
    sStageSettleFrames = 0;
    sLastCourseId = 0xFF;
    sLastEpisodeId = 0xFF;
    sApplyingRemote = false;
    if (!sHookInstalled) {
        sHookInstalled = true;
        OSReport("[SMSOBB] graffiti-clean sync ready (cell=%.0f max=%u radius=%.0f-%.0f/%.0f wall "
                 "3D emitAssist+packFb)\n",
                 kGraffitiCellSize, kMaxStageGraffitiCleans, kMinSyncCleanRadius,
                 kMaxSyncCleanRadius, kMaxWallSyncCleanRadius);
    }
}

void notifyGraffitiCleanStageEnter() {
    clearTrackers();
    sStageSettleFrames = 0;
    sLastCourseId = currentCourseId();
    sLastEpisodeId = currentEpisodeId();
}

void updateGraffitiCleanSync() {
    if (!gpMarDirector)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!graffitiPublishEnabled(buf))
        return;
    if (gpMarDirector->mCurState != TMarDirector::STATE_NORMAL)
        return;

    const u8 course = currentCourseId();
    const u8 episode = currentEpisodeId();
    // Plaza/casino episode aliasing: do not clear trackers on equivalent episode drift
    // (decideNextScenario without soft-reload), or remotes re-stamp / republish noisily.
    if (course != sLastCourseId ||
        !graffitiEpisodesEquivalent(course, episode, sLastEpisodeId)) {
        notifyGraffitiCleanStageEnter();
        sLastCourseId = course;
        sLastEpisodeId = episode;
    }

    if (sStageSettleFrames < kStageSettleFrames)
        ++sStageSettleFrames;

    // Primary live clear: raycast remote spray → retail clean (no publish).
    updateRemoteSprayCleanAssist();

    reconcilePendingApplies();
    maybeReportGraffitiDiag();
}

bool applyGraffitiCleanWorldEvent(const CommWorldEvent &event) {
    if (event.type != static_cast<u8>(WE_GRAFFITI_CLEANED))
        return false;

    // Off-course: consume so durable mailbox advances (launcher should have deferred).
    // Same course + equivalent episode (plaza hub / casino alias): APPLY — never
    // consume-and-skip when the physical stage matches.
    if (!graffitiStageMatches(event.courseId, event.episodeId))
        return true;

    const bool wallBoost =
        (event.reserved & (kGraffitiReservedWall | kGraffitiReservedFinishing)) != 0;

    f32 x = 0.0f, y = 0.0f, z = 0.0f;
    f32 size = dequantizeSize(event.payload0, wallBoost);
    s16 cellX = 0;
    s16 cellY = 0;
    s16 cellZ = 0;

    // Prefer packed spray XYZ. Cell is for dedupe / late-join fallback only.
    const bool haveCell = unpackCell3(event.payload2, cellX, cellY, cellZ);
    if (isValidPackedWorldPos(event.payload1)) {
        unpackCollectibleWorldPos(event.payload1, x, y, z);
        if (!haveCell) {
            cellX = cellCoord(x);
            cellY = cellCoord(y);
            cellZ = cellCoord(z);
        }
    } else if (haveCell) {
        x = (static_cast<f32>(cellX) + 0.5f) * kGraffitiCellSize;
        y = (static_cast<f32>(cellY) + 0.5f) * kGraffitiCellSize;
        z = (static_cast<f32>(cellZ) + 0.5f) * kGraffitiCellSize;
    } else {
        return true; // malformed — free mailbox
    }

    size = clampSyncCleanRadius(size, wallBoost);

    // One-shot only — including finishing. Re-stamping a known cell (live assist +
    // durable + finishing + WorldStateReplay after tracker clear is OK once; mid-stage
    // finishing rebroadcast must not pushStampTask again or mesh vertices stretch).
    if (!rememberStamp(sLocalPublished, sLocalPublishedCount, x, y, z, size, cellX, cellY,
                       cellZ))
        return true;

    if (!smso_gpPollution) {
        // Defer one-shot until settle + gpPollution exists. Do not leave active
        // forever if we somehow apply later — reconcile marks inactive after apply.
        rememberStamp(sPendingApply, sPendingApplyCount, x, y, z, size, cellX, cellY, cellZ);
        OSReport("[SMSOBB] graffiti apply-mask cell=(%d,%d,%d) defer (no gpPollution)\n", cellX,
                 cellY, cellZ);
        return true;
    }

    applyCleanStampPattern(x, y, z, size, wallBoost);
    ++sGraffitiApplyCount;
    OSReport("[SMSOBB] graffiti apply cell=(%d,%d,%d) size=%.0f flags=0x%02x pos=(%.0f,%.0f,%.0f) "
             "apply=%u\n",
             cellX, cellY, cellZ, size, event.reserved, x, y, z, sGraffitiApplyCount);
    return true;
}

void notifyRemoteSprayEmit(f32 ox, f32 oy, f32 oz, f32 dx, f32 dy, f32 dz) {
    // Capture for inter-emit frames, then clean immediately while the emit ray
    // matches the droplets the viewer can see.
    sLastSprayEmitRay.ox = ox;
    sLastSprayEmitRay.oy = oy;
    sLastSprayEmitRay.oz = oz;
    sLastSprayEmitRay.dx = dx;
    sLastSprayEmitRay.dy = dy;
    sLastSprayEmitRay.dz = dz;
    sLastSprayEmitRay.freshFrames = kAssistEmitFreshFrames;
    sLastSprayEmitRay.valid = true;
    sEmitAssistThisFrame = true;

    if (sApplyingRemote || !smso_gpPollution)
        return;
    if (!gpMapCollisionData || gpMapCollisionData->mCheckDataCount == 0)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!graffitiPublishEnabled(buf))
        return;

    assistCleanFromRay(ox, oy, oz, dx, dy, dz);
}

} // namespace smso
