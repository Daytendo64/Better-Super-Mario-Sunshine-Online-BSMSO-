#include "nametag_system.hpp"

#include <BetterSMS/module.hxx>
#include <Dolphin/MTX.h>
#include <Dolphin/mem.h>
#include <Dolphin/string.h>
#include <math.h>
#include <JSystem/J2D/J2DOrthoGraph.hxx>
#include <JSystem/J2D/J2DPrint.hxx>
#include <JSystem/JUtility/JUTColor.hxx>
#include <SMS/Camera/PolarSubCamera.hxx>
#include <SMS/Map/MapCollisionData.hxx>
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>
#include <SMS/raw_fn.hxx>

#include "comm_buffer.hpp"
#include "hide_seek.hpp"

extern CPolarSubCamera *gpCamera;
extern TMapCollisionData *gpMapCollisionData;
extern TMarDirector *gpMarDirector;

namespace smso::nametag {

namespace {

// ---------------------------------------------------------------------------
// Nametag layout — anchor is the screen-space center of the tag above the head.
// Scale is applied symmetrically around that center so shrinking stays in place.
// ---------------------------------------------------------------------------
static constexpr f32 kGapAboveAnchorPx = 5.0f;

// ---------------------------------------------------------------------------
// Distance bands (WoW-style min/max scale distances + smoothstep easing).
// Close: hold full readability. Far: taper smoothly. Extreme: fade out.
// ---------------------------------------------------------------------------
static constexpr f32 kFullScaleDistance = 900.0f;
static constexpr f32 kMinScaleDistance = 5800.0f;
static constexpr f32 kFadeStartDistance = 6800.0f;
static constexpr f32 kCullDistance = 9200.0f;

// Perspective measurement — vertical world span used to validate screen pixel height.
static constexpr f32 kPerspectiveMeasureWorldHeight = 44.0f;
static constexpr f32 kPerspectiveMeasureFill = 0.92f;

// Font bounds — nominal 22px is the unchanged retail HUD size.
static constexpr f32 kNominalFontSize = 22.0f;
static constexpr f32 kMinFontSize = 5.0f;
static constexpr f32 kMaxFontSize = 22.0f;
static constexpr f32 kHideSeekFontSizeReduce = 2.0f;

static constexpr f32 kScaleSmoothRate = 16.0f;
static constexpr f32 kAlphaSmoothRate = 14.0f;
static constexpr f32 kOcclusionSmoothRate = 18.0f;

// doldecomp TMapCollisionData::intersectLine — margin before the head anchor so floor
// collision under the target does not false-positive as a wall block.
static constexpr f32 kOcclusionAnchorMargin = 72.0f;
static constexpr f32 kOcclusionMinRayLength = 120.0f;

// Snap thresholds for teleports and large scale discontinuities.
static constexpr f32 kTeleportDistance = 700.0f;
static constexpr f32 kSnapFontDelta = 8.0f;

static constexpr f32 kGameScreenHeight = 448.0f;
static constexpr int kJ2DPrintDefaultLeading = static_cast<int>(0x80000000);

struct SlotRuntime {
    bool active;
    bool initialized;
    Appearance appearance;
    char name[MAX_PLAYER_NAME];

    f32 lastBodyX;
    f32 lastBodyY;
    f32 lastBodyZ;

    f32 anchorWorldX;
    f32 anchorWorldY;
    f32 anchorWorldZ;

    f32 smoothedFontSize;
    f32 smoothedAlpha;

    f32 cameraDistance;
    f32 targetFontSize;
    f32 targetAlpha;
    f32 targetOcclusion;
    f32 smoothedOcclusion;
    bool drawVisible;
};

static SlotRuntime gSlots[MAX_REMOTE_SLOTS];
static bool gDebugOverlay = false;

static f32 clampf(f32 value, f32 minValue, f32 maxValue) {
    if (value < minValue)
        return minValue;
    if (value > maxValue)
        return maxValue;
    return value;
}

static f32 smoothstep01(f32 t) {
    t = clampf(t, 0.0f, 1.0f);
    return t * t * (3.0f - 2.0f * t);
}

static f32 getFrameDelta() {
    const f32 fps = BetterSMS::getFrameRate();
    return fps > 1.0f ? (1.0f / fps) : (1.0f / 30.0f);
}

static f32 exponentialSmooth(f32 current, f32 target, f32 rate, f32 dt) {
    const f32 blend = 1.0f - expf(-rate * dt);
    return current + (target - current) * blend;
}

static bool getCameraPosition(Vec &out) {
    if (!gpCamera)
        return false;

    out.x = gpCamera->mWorldTranslation.x;
    out.y = gpCamera->mWorldTranslation.y;
    out.z = gpCamera->mWorldTranslation.z;
    return true;
}

static bool isValidCollisionHitPointer(const TBGCheckData *tri) {
    if (!tri)
        return false;

    const u32 addr = reinterpret_cast<u32>(tri);
    // Reject garbage returns (e.g. small integers) before touching triangle fields.
    if (addr < 0x80400000u || addr >= 0x81800000u)
        return false;

    if (gpMapCollisionData && tri == &gpMapCollisionData->mIllegalCheckData)
        return false;

    return true;
}

static bool isMapCollisionReadyForOcclusion() {
    if (!gpMapCollisionData || gpMapCollisionData->mCheckDataCount == 0)
        return false;

    if (gpMarDirector && gpMarDirector->mCurState < TMarDirector::STATE_NORMAL)
        return false;

    return true;
}

using MapCollisionIntersectLineFn = const TBGCheckData *(*)(const TMapCollisionData *, const TVec3f &,
                                                            const TVec3f &, bool, TVec3f *);

static const TBGCheckData *mapCollisionIntersectLine(const TVec3f &start, const TVec3f &end,
                                                     TVec3f *hitPos) {
    // BSE declares intersectLine as u32, but retail returns const TBGCheckData* in r3.
    const auto fn = reinterpret_cast<MapCollisionIntersectLineFn>(
        intersectLine__17TMapCollisionDataCFRCQ29JGeometry8TVec3_f);
    return fn(gpMapCollisionData, start, end, false, hitPos);
}

static bool isNameTagOcclusionTriangleBlocking(const TBGCheckData *tri) {
    if (!isValidCollisionHitPointer(tri))
        return false;

    if (tri->isIllegalData() || tri->isMarioThrough() || tri->isWaterSurface())
        return false;

    const TVec3f *normal = tri->getNormal();
    if (!normal)
        return false;

    // doldecomp bgIntersectLine + CameraBGCheck: floors (up-facing) do not block sight lines.
    if (normal->y > 0.55f)
        return false;

    return true;
}

// doldecomp TMapCollisionData::intersectLine — ground/wall/roof grid raycast.
static bool isNameTagAnchorOccluded(f32 anchorX, f32 anchorY, f32 anchorZ) {
    if (!isHideSeekNameTagMode() || !gpCamera || !isMapCollisionReadyForOcclusion())
        return false;

    Vec camera{};
    if (!getCameraPosition(camera))
        return false;

    TVec3f segStart = {camera.x, camera.y, camera.z};
    const TVec3f anchor = {anchorX, anchorY, anchorZ};

    const f32 fullDx = anchor.x - segStart.x;
    const f32 fullDy = anchor.y - segStart.y;
    const f32 fullDz = anchor.z - segStart.z;
    const f32 fullLenSq = fullDx * fullDx + fullDy * fullDy + fullDz * fullDz;
    if (fullLenSq < kOcclusionMinRayLength * kOcclusionMinRayLength)
        return false;

    const f32 anchorGuard = fullLenSq - kOcclusionAnchorMargin * kOcclusionAnchorMargin;

    for (int attempt = 0; attempt < 4; ++attempt) {
        TVec3f hitPos{};
        const TBGCheckData *hit = mapCollisionIntersectLine(segStart, anchor, &hitPos);
        if (!isValidCollisionHitPointer(hit))
            return false;

        if (!isNameTagOcclusionTriangleBlocking(hit)) {
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

static f32 measureCameraDistance(f32 wx, f32 wy, f32 wz) {
    Vec camera{};
    if (!getCameraPosition(camera))
        return kCullDistance;

    const f32 dx = wx - camera.x;
    const f32 dy = wy - camera.y;
    const f32 dz = wz - camera.z;
    return sqrtf(dx * dx + dy * dy + dz * dz);
}

static bool projectWorldToScreen(f32 wx, f32 wy, f32 wz, f32 &outX, f32 &outY) {
    if (!gpCamera)
        return false;

    const Vec world = {wx, wy, wz};
    Vec view{};
    MTXMultVec(gpCamera->mTRSMatrix, &world, &view);

    if (view.z >= -1.0f)
        return false;

    f32 fovy = gpCamera->mProjectionFovy;
    if (fovy < 1.0f || fovy > 179.0f)
        fovy = 60.0f;

    f32 aspect = gpCamera->mProjectionAspect;
    if (aspect < 0.5f || aspect > 4.0f)
        aspect = 4.0f / 3.0f;

    const f32 halfFov = fovy * 0.5f * 0.017453293f;
    const f32 tanHalf = sinf(halfFov) / cosf(halfFov);
    const f32 invZ = -1.0f / view.z;
    const f32 ndcY = view.y * invZ / tanHalf;
    const f32 ndcX = view.x * invZ / (tanHalf * aspect);

    const f32 screenWidth = static_cast<f32>(BetterSMS::getScreenRenderWidth());
    const f32 adjustX = BetterSMS::getScreenRatioAdjustX();

    outX = (ndcX + 1.0f) * 0.5f * screenWidth - adjustX;
    outY = (1.0f - ndcY) * 0.5f * kGameScreenHeight;

    const f32 margin = 140.0f;
    return outX >= -adjustX - margin && outX <= screenWidth - adjustX + margin &&
           outY >= -margin && outY <= kGameScreenHeight + margin;
}

static f32 measurePerspectiveFontSize(f32 anchorX, f32 anchorY, f32 anchorZ) {
    f32 baseScreenX, baseScreenY;
    f32 topScreenX, topScreenY;
    if (!projectWorldToScreen(anchorX, anchorY, anchorZ, baseScreenX, baseScreenY))
        return kMinFontSize;
    if (!projectWorldToScreen(anchorX, anchorY + kPerspectiveMeasureWorldHeight, anchorZ, topScreenX,
                              topScreenY))
        return kMinFontSize;

    const f32 pixelSpan = baseScreenY - topScreenY;
    if (pixelSpan <= 0.0f)
        return kMinFontSize;

    return clampf(pixelSpan * kPerspectiveMeasureFill, kMinFontSize, kMaxFontSize);
}

static f32 evaluateDistanceCurveSize(f32 cameraDistance) {
    if (cameraDistance >= kCullDistance)
        return 0.0f;
    if (cameraDistance <= kFullScaleDistance)
        return kMaxFontSize;
    if (cameraDistance >= kMinScaleDistance)
        return kMinFontSize;

    const f32 span = kMinScaleDistance - kFullScaleDistance;
    if (span <= 1.0f)
        return kMinFontSize;

    const f32 t = (cameraDistance - kFullScaleDistance) / span;
    const f32 eased = smoothstep01(t);
    return kMaxFontSize + (kMinFontSize - kMaxFontSize) * eased;
}

static f32 evaluateTargetFontSize(f32 cameraDistance, f32 anchorX, f32 anchorY, f32 anchorZ) {
    const f32 curveSize = evaluateDistanceCurveSize(cameraDistance);
    if (curveSize <= 0.0f)
        return 0.0f;

    const f32 perspectiveSize = measurePerspectiveFontSize(anchorX, anchorY, anchorZ);
    // Blend curve readability with true perspective so scaling tracks camera FOV/aspect.
    const f32 blended = curveSize * 0.4f + perspectiveSize * 0.6f;
    return clampf(blended, kMinFontSize, kMaxFontSize);
}

static f32 evaluateHideSeekFontSize(f32 cameraDistance, f32 anchorX, f32 anchorY, f32 anchorZ) {
    f32 size = evaluateTargetFontSize(cameraDistance, anchorX, anchorY, anchorZ);
    if (size <= 0.0f)
        return kMinFontSize;
    return clampf(size - kHideSeekFontSizeReduce, kMinFontSize, kMaxFontSize);
}

static f32 evaluateTargetAlpha(f32 cameraDistance) {
    if (cameraDistance >= kCullDistance)
        return 0.0f;
    if (cameraDistance <= kFadeStartDistance)
        return 1.0f;

    const f32 fadeSpan = kCullDistance - kFadeStartDistance;
    if (fadeSpan <= 1.0f)
        return 0.0f;

    const f32 t = (cameraDistance - kFadeStartDistance) / fadeSpan;
    return 1.0f - smoothstep01(t);
}

static f32 measureTextWidth(const char *text, int fontSize, JUtility::TColor color) {
    if (!gpSystemFont || !text || text[0] == '\0' || fontSize <= 0)
        return 0.0f;

    J2DPrint measure(gpSystemFont, 1);
    measure.private_initiate(gpSystemFont, 1, kJ2DPrintDefaultLeading, color, color);
    measure.initiate();
    measure.setFontSize(fontSize, fontSize);

    // J2DPrint::getWidth() parses without initchar(), so unk34/unk38 stay at the
    // font's base cell size. print() calls initchar() first and renders scaled.
    // Scale measured width manually so centering matches the drawn glyphs.
    f32 width = measure.getWidth("%s", text);
    const int baseFontWidth = gpSystemFont->getWidth();
    if (baseFontWidth > 0)
        width *= static_cast<f32>(fontSize) / static_cast<f32>(baseFontWidth);
    return width;
}

static f32 measureTextHeight(int fontSize) {
    return static_cast<f32>(fontSize);
}

static void computeDrawRect(f32 centerX, f32 centerY, f32 textWidth, f32 textHeight, int outlineOffsetPx,
                            int &outX, int &outY) {
    const f32 pad = static_cast<f32>(outlineOffsetPx);
    const f32 boxW = textWidth + pad * 2.0f;
    const f32 boxH = textHeight + pad * 2.0f;
    outX = static_cast<int>(centerX - boxW * 0.5f + pad + 0.5f);
    outY = static_cast<int>(centerY - boxH * 0.5f + pad + 0.5f);
}

struct OutlineMetrics {
    int offsetPx;
};

static OutlineMetrics calcOutlineMetrics(f32 fontSize) {
    OutlineMetrics metrics{};
    if (fontSize < 4.0f)
        return metrics;

    // ~11% of glyph height keeps outline readable from close-up through distance taper.
    f32 offsetF = fontSize * 0.11f + 0.35f;
    if (offsetF < 1.0f)
        offsetF = 1.0f;
    if (offsetF > 3.0f)
        offsetF = 3.0f;

    metrics.offsetPx = static_cast<int>(offsetF + 0.5f);
    return metrics;
}

static f32 scaledGapAboveHead(f32 fontSize) {
    const f32 scale = clampf(fontSize / kNominalFontSize, 0.35f, 1.0f);
    return kGapAboveAnchorPx * scale;
}

static f32 screenAnchorCenterY(f32 headScreenY, f32 fontSize) {
    return headScreenY - scaledGapAboveHead(fontSize) - fontSize * 0.5f;
}

static JUtility::TColor applyAlpha(JUtility::TColor color, f32 alpha) {
    const int alphaByte = static_cast<int>(clampf(alpha, 0.0f, 1.0f) * 255.0f + 0.5f);
    return JUtility::TColor(color.r, color.g, color.b, static_cast<u8>(alphaByte));
}

static void printLayer(int x, int y, int fontSize, const char *text, JUtility::TColor topColor,
                       JUtility::TColor bottomColor, bool useGradient) {
    if (!gpSystemFont || !text || text[0] == '\0')
        return;

    J2DPrint printer(gpSystemFont, 1);
    const JUtility::TColor bottom = useGradient ? bottomColor : topColor;
    printer.private_initiate(gpSystemFont, 1, kJ2DPrintDefaultLeading, topColor, bottom);
    printer.initiate();
    printer.setFontSize(fontSize, fontSize);
    printer.print(x, y, "%s", text);
}

static void drawOutline(int x, int y, int fontSize, const OutlineMetrics &metrics, const char *text,
                        JUtility::TColor outlineColor) {
    if (metrics.offsetPx <= 0)
        return;

    // Filled Chebyshev disk — solid halo at every distance, not just the outer ring.
    for (int dy = -metrics.offsetPx; dy <= metrics.offsetPx; ++dy) {
        for (int dx = -metrics.offsetPx; dx <= metrics.offsetPx; ++dx) {
            if (dx == 0 && dy == 0)
                continue;

            const int adx = dx < 0 ? -dx : dx;
            const int ady = dy < 0 ? -dy : dy;
            const int cheb = adx > ady ? adx : ady;
            if (cheb < 1 || cheb > metrics.offsetPx)
                continue;

            printLayer(x + dx, y + dy, fontSize, text, outlineColor, outlineColor, false);
        }
    }
}

static void drawNameTag(int x, int y, int fontSize, const OutlineMetrics &outlineMetrics,
                        const char *text, const Appearance &appearance, f32 alpha) {
    if (!text || text[0] == '\0' || alpha <= 0.01f)
        return;

    const JUtility::TColor top = applyAlpha(appearance.textTopColor, alpha);
    const JUtility::TColor bottom = applyAlpha(appearance.textBottomColor, alpha);
    const JUtility::TColor outline = applyAlpha(appearance.outlineColor, alpha);

    if (outlineMetrics.offsetPx > 0)
        drawOutline(x, y, fontSize, outlineMetrics, text, outline);

    printLayer(x, y, fontSize, text, top, bottom, appearance.gradientEnabled);
}

static void resetSlotRuntime(SlotRuntime &slot) {
    slot.active = false;
    slot.initialized = false;
    slot.smoothedFontSize = kNominalFontSize;
    slot.anchorWorldX = 0.0f;
    slot.anchorWorldY = 0.0f;
    slot.anchorWorldZ = 0.0f;
    slot.smoothedAlpha = 0.0f;
    slot.cameraDistance = 0.0f;
    slot.targetFontSize = 0.0f;
    slot.targetAlpha = 0.0f;
    slot.targetOcclusion = 1.0f;
    slot.smoothedOcclusion = 1.0f;
    slot.drawVisible = false;
    slot.name[0] = '\0';
}

static bool shouldSnapMotion(const SlotRuntime &slot, f32 bodyX, f32 bodyY, f32 bodyZ, f32 targetFont) {
    if (!slot.initialized)
        return true;

    const f32 dx = bodyX - slot.lastBodyX;
    const f32 dy = bodyY - slot.lastBodyY;
    const f32 dz = bodyZ - slot.lastBodyZ;
    const f32 bodyJump = sqrtf(dx * dx + dy * dy + dz * dz);
    if (bodyJump >= kTeleportDistance)
        return true;

    if (fabsf(targetFont - slot.smoothedFontSize) >= kSnapFontDelta)
        return true;

    return false;
}

} // namespace

void initSystem() {
    memset(gSlots, 0, sizeof(gSlots));
    for (auto &slot : gSlots)
        resetSlotRuntime(slot);
    gDebugOverlay = false;
}

void clearSystem() {
    initSystem();
}

void setDebugOverlayEnabled(bool enabled) {
    gDebugOverlay = enabled;
}

bool isDebugOverlayEnabled() {
    return gDebugOverlay;
}

void updateSlot(u8 slot, bool active, f32 anchorX, f32 anchorY, f32 anchorZ, f32 bodyX, f32 bodyY,
                f32 bodyZ, const Appearance &appearance, const char *name) {
    if (slot >= MAX_REMOTE_SLOTS)
        return;

    SlotRuntime &state = gSlots[slot];
    if (!active) {
        resetSlotRuntime(state);
        return;
    }

    state.active = true;
    state.appearance = appearance;
    if (name && name[0] != '\0')
        strncpy(state.name, name, MAX_PLAYER_NAME - 1);
    else
        state.name[0] = '\0';
    state.name[MAX_PLAYER_NAME - 1] = '\0';

    f32 rawScreenX = 0.0f;
    f32 rawScreenY = 0.0f;
    const bool onScreen = projectWorldToScreen(anchorX, anchorY, anchorZ, rawScreenX, rawScreenY);
    const f32 distance = measureCameraDistance(anchorX, anchorY, anchorZ);

    state.cameraDistance = distance;
    if (isHideSeekNameTagMode()) {
        state.targetFontSize =
            onScreen ? evaluateHideSeekFontSize(distance, anchorX, anchorY, anchorZ) : 0.0f;
        state.targetAlpha = onScreen ? 1.0f : 0.0f;
        state.targetOcclusion =
            onScreen && isNameTagAnchorOccluded(anchorX, anchorY, anchorZ) ? 0.0f : 1.0f;
        state.drawVisible = onScreen && state.targetFontSize >= kMinFontSize;
    } else {
        state.targetFontSize = onScreen ? evaluateTargetFontSize(distance, anchorX, anchorY, anchorZ) : 0.0f;
        state.targetAlpha = onScreen ? evaluateTargetAlpha(distance) : 0.0f;
        state.targetOcclusion = 1.0f;
        state.drawVisible = onScreen && state.targetAlpha > 0.02f && state.targetFontSize > 0.5f;
    }

    const f32 combinedAlphaTarget = state.targetAlpha * state.targetOcclusion;

    state.anchorWorldX = anchorX;
    state.anchorWorldY = anchorY;
    state.anchorWorldZ = anchorZ;

    const bool snap = shouldSnapMotion(state, bodyX, bodyY, bodyZ, state.targetFontSize) ||
                      fabsf(state.targetOcclusion - state.smoothedOcclusion) >= 0.45f;
    const f32 dt = getFrameDelta();

    if (snap || !state.initialized) {
        state.smoothedFontSize = state.targetFontSize;
        state.smoothedOcclusion = state.targetOcclusion;
        state.smoothedAlpha = combinedAlphaTarget;
        state.initialized = true;
    } else {
        state.smoothedFontSize =
            exponentialSmooth(state.smoothedFontSize, state.targetFontSize, kScaleSmoothRate, dt);
        state.smoothedOcclusion =
            exponentialSmooth(state.smoothedOcclusion, state.targetOcclusion, kOcclusionSmoothRate, dt);
        state.smoothedAlpha =
            exponentialSmooth(state.smoothedAlpha, combinedAlphaTarget, kAlphaSmoothRate, dt);
    }

    if (isHideSeekNameTagMode())
        state.drawVisible = onScreen && state.targetFontSize >= kMinFontSize && state.smoothedAlpha > 0.03f;

    state.lastBodyX = bodyX;
    state.lastBodyY = bodyY;
    state.lastBodyZ = bodyZ;
}

void drawAll(const J2DOrthoGraph *graph) {
    if (!graph || !gpSystemFont)
        return;

    auto *ctx = const_cast<J2DOrthoGraph *>(graph);
    ctx->setup2D();

    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        const SlotRuntime &state = gSlots[slot];
        if (!state.active || !state.drawVisible || state.smoothedAlpha <= 0.03f)
            continue;

        const int fontSize = static_cast<int>(state.smoothedFontSize + 0.5f);
        if (fontSize < static_cast<int>(kMinFontSize))
            continue;

        const OutlineMetrics outlineMetrics = calcOutlineMetrics(state.smoothedFontSize);

        f32 screenX = 0.0f;
        f32 screenY = 0.0f;
        if (!projectWorldToScreen(state.anchorWorldX, state.anchorWorldY, state.anchorWorldZ, screenX,
                                  screenY))
            continue;

        const f32 centerX = screenX;
        const f32 centerY = screenAnchorCenterY(screenY, state.smoothedFontSize);

        const f32 textWidth =
            measureTextWidth(state.name, fontSize, state.appearance.textTopColor);
        const f32 textHeight = measureTextHeight(fontSize);

        int x = 0;
        int y = 0;
        computeDrawRect(centerX, centerY, textWidth, textHeight, outlineMetrics.offsetPx, x, y);

        drawNameTag(x, y, fontSize, outlineMetrics, state.name, state.appearance, state.smoothedAlpha);
    }

    ctx->setScissor();
}

bool getSlotDebugState(u8 slot, DebugState &out) {
    if (slot >= MAX_REMOTE_SLOTS)
        return false;

    const SlotRuntime &state = gSlots[slot];
    if (!state.active)
        return false;

    out.cameraDistance = state.cameraDistance;
    out.targetFontSize = state.targetFontSize;
    out.smoothedFontSize = state.smoothedFontSize;
    out.targetAlpha = state.targetAlpha;
    out.smoothedAlpha = state.smoothedAlpha;
    out.visible = state.drawVisible;
    return true;
}

} // namespace smso::nametag
