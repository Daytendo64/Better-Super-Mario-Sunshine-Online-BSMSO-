#include "collision_los.hpp"

#include <math.h>

#include <SMS/Camera/PolarSubCamera.hxx>
#include <SMS/Map/MapCollisionData.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/raw_fn.hxx>

extern CPolarSubCamera *gpCamera;
extern TMapCollisionData *gpMapCollisionData;
extern TMarDirector *gpMarDirector;

namespace smso::collision_los {

namespace {

// Match nametag occlusion margins so body/tag LOS stay consistent in Hide & Seek.
constexpr f32 kOcclusionAnchorMargin = 72.0f;
constexpr f32 kOcclusionMinRayLength = 120.0f;

bool getCameraPosition(Vec &out) {
    if (!gpCamera)
        return false;
    out.x = gpCamera->mWorldTranslation.x;
    out.y = gpCamera->mWorldTranslation.y;
    out.z = gpCamera->mWorldTranslation.z;
    return true;
}

bool isValidCollisionHitPointer(const TBGCheckData *tri) {
    if (!tri)
        return false;

    const u32 addr = reinterpret_cast<u32>(tri);
    if (addr < 0x80400000u || addr >= 0x81800000u)
        return false;

    if (gpMapCollisionData && tri == &gpMapCollisionData->mIllegalCheckData)
        return false;

    return true;
}

bool isOcclusionTriangleBlocking(const TBGCheckData *tri) {
    if (!isValidCollisionHitPointer(tri))
        return false;

    if (tri->isIllegalData() || tri->isMarioThrough() || tri->isWaterSurface())
        return false;

    const TVec3f *normal = tri->getNormal();
    if (!normal)
        return false;

    // doldecomp bgIntersectLine + CameraBGCheck: floors (up-facing) do not block sight.
    if (normal->y > 0.55f)
        return false;

    return true;
}

using MapCollisionIntersectLineFn = const TBGCheckData *(*)(const TMapCollisionData *,
                                                            const TVec3f &, const TVec3f &, bool,
                                                            TVec3f *);

const TBGCheckData *mapCollisionIntersectLine(const TVec3f &start, const TVec3f &end,
                                              TVec3f *hitPos) {
    // BSE declares intersectLine as u32, but retail returns const TBGCheckData* in r3.
    const auto fn = reinterpret_cast<MapCollisionIntersectLineFn>(
        intersectLine__17TMapCollisionDataCFRCQ29JGeometry8TVec3_f);
    return fn(gpMapCollisionData, start, end, false, hitPos);
}

} // namespace

bool isReady() {
    if (!gpMapCollisionData || gpMapCollisionData->mCheckDataCount == 0)
        return false;

    if (gpMarDirector && gpMarDirector->mCurState < TMarDirector::STATE_NORMAL)
        return false;

    return true;
}

bool isPointOccludedFromCamera(f32 worldX, f32 worldY, f32 worldZ) {
    if (!gpCamera || !isReady())
        return false;

    Vec camera{};
    if (!getCameraPosition(camera))
        return false;

    TVec3f segStart = {camera.x, camera.y, camera.z};
    const TVec3f anchor = {worldX, worldY, worldZ};

    const f32 fullDx = anchor.x - segStart.x;
    const f32 fullDy = anchor.y - segStart.y;
    const f32 fullDz = anchor.z - segStart.z;
    const f32 fullLenSq = fullDx * fullDx + fullDy * fullDy + fullDz * fullDz;
    if (fullLenSq < kOcclusionMinRayLength * kOcclusionMinRayLength)
        return false;

    const f32 guardedLength = sqrtf(fullLenSq) - kOcclusionAnchorMargin;
    if (guardedLength <= 0.0f)
        return false;
    const f32 anchorGuard = guardedLength * guardedLength;

    for (int attempt = 0; attempt < 4; ++attempt) {
        TVec3f hitPos{};
        const TBGCheckData *hit = mapCollisionIntersectLine(segStart, anchor, &hitPos);
        if (!isValidCollisionHitPointer(hit))
            return false;

        if (!isOcclusionTriangleBlocking(hit)) {
            TVec3f dir = {anchor.x - segStart.x, anchor.y - segStart.y, anchor.z - segStart.z};
            const f32 segLen = sqrtf(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
            if (segLen < 1.0f)
                return false;

            dir.x /= segLen;
            dir.y /= segLen;
            dir.z /= segLen;

            segStart.x = hitPos.x + dir.x * 8.0f;
            segStart.y = hitPos.y + dir.y * 8.0f;
            segStart.z = hitPos.z + dir.z * 8.0f;
            continue;
        }

        const f32 hx = hitPos.x - camera.x;
        const f32 hy = hitPos.y - camera.y;
        const f32 hz = hitPos.z - camera.z;
        const f32 hitLenSq = hx * hx + hy * hy + hz * hz;
        return hitLenSq < anchorGuard;
    }

    return false;
}

} // namespace smso::collision_los
