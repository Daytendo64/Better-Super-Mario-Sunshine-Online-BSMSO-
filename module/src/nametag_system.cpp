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
#include <SMS/System/Application.hxx>
#include <SMS/System/MarDirector.hxx>

#include "comm_buffer.hpp"
#include "collision_los.hpp"
#include "gx_hud_fence.hpp"
#include "hide_seek.hpp"

extern CPolarSubCamera *gpCamera;

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
static constexpr f32 kAnchorSmoothRate = 30.0f;

// doldecomp TMapCollisionData::intersectLine — margin before the head anchor so floor
// collision under the target does not false-positive as a wall block.
// (Raycast itself lives in collision_los; these drive sample hysteresis only.)
static constexpr u8 kOcclusionRefreshFrames = 6; // 10 Hz at 60 fps, staggered per slot
static constexpr u8 kOcclusionHideSamples = 2;
static constexpr u8 kNearbyOcclusionHideSamples = 3;
static constexpr u8 kOcclusionShowSamples = 2;
static constexpr f32 kNearbyOcclusionDistance = 1800.0f;
static constexpr u8 kProjectionMissGraceFrames = 3;

// Snap thresholds for teleports and large scale discontinuities.
static constexpr f32 kTeleportDistance = 700.0f;
static constexpr f32 kSnapFontDelta = 8.0f;
// Only treat a screen jump as a LOD/anchor correction when the body barely moved.
// A small absolute screen threshold falsely snapped during ordinary camera/player
// motion and looked like nametag flicker, especially on clients.
static constexpr f32 kSnapScreenDelta = 48.0f;
static constexpr f32 kSnapScreenBodyMoveMax = 40.0f;

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
    f32 screenX;
    f32 screenY;

    f32 smoothedFontSize;
    f32 smoothedAlpha;

    f32 cameraDistance;
    f32 targetFontSize;
    f32 targetAlpha;
    f32 targetOcclusion;
    f32 smoothedOcclusion;
    bool drawVisible;
    u8 occlusionRefresh;
    u8 occludedSamples;
    u8 visibleSamples;
    u8 projectionMissFrames;
    int measuredFontSize;
    f32 measuredTextWidth;
};

static SlotRuntime gSlots[MAX_REMOTE_SLOTS];
static bool gDebugOverlay = false;

struct ProjectionCache {
    f32 fovy;
    f32 aspect;
    f32 tanHalf;
    f32 screenWidth;
    f32 adjustX;
    bool valid;
};

static ProjectionCache gProjectionCache = {};

static f32 clampf(f32 value, f32 minValue, f32 maxValue) {
    if (value < minValue)
        return minValue;
    if (value > maxValue)
        return maxValue;
    return value;
}

static bool boundedNameEquals(const char *a, const char *b) {
    if (!a || !b)
        return a == b;
    for (u32 i = 0; i < MAX_PLAYER_NAME; ++i) {
        if (a[i] != b[i])
            return false;
        if (a[i] == '\0')
            return true;
    }
    return true;
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

// doldecomp TMapCollisionData::intersectLine — ground/wall/roof grid raycast.
// All modes: hide tags when a wall blocks camera→anchor (matches remote body LOS).
static bool isNameTagAnchorOccluded(f32 anchorX, f32 anchorY, f32 anchorZ) {
    return collision_los::isPointOccludedFromCamera(anchorX, anchorY, anchorZ);
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

    const f32 screenWidth = static_cast<f32>(BetterSMS::getScreenRenderWidth());
    const f32 adjustX = BetterSMS::getScreenRatioAdjustX();
    if (!gProjectionCache.valid || gProjectionCache.fovy != fovy ||
        gProjectionCache.aspect != aspect || gProjectionCache.screenWidth != screenWidth ||
        gProjectionCache.adjustX != adjustX) {
        const f32 halfFov = fovy * 0.5f * 0.017453293f;
        gProjectionCache.fovy = fovy;
        gProjectionCache.aspect = aspect;
        gProjectionCache.tanHalf = sinf(halfFov) / cosf(halfFov);
        gProjectionCache.screenWidth = screenWidth;
        gProjectionCache.adjustX = adjustX;
        gProjectionCache.valid = true;
    }

    const f32 tanHalf = gProjectionCache.tanHalf;
    const f32 invZ = -1.0f / view.z;
    const f32 ndcY = view.y * invZ / tanHalf;
    const f32 ndcX = view.x * invZ / (tanHalf * aspect);

    outX = (ndcX + 1.0f) * 0.5f * screenWidth - adjustX;
    outY = (1.0f - ndcY) * 0.5f * kGameScreenHeight;

    const f32 margin = 140.0f;
    return outX >= -adjustX - margin && outX <= screenWidth - adjustX + margin &&
           outY >= -margin && outY <= kGameScreenHeight + margin;
}

static f32 measurePerspectiveFontSize(f32 anchorX, f32 anchorY, f32 anchorZ, f32 baseScreenY) {
    f32 topScreenX, topScreenY;
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

static f32 evaluateTargetFontSize(f32 cameraDistance, f32 anchorX, f32 anchorY, f32 anchorZ,
                                  f32 baseScreenY) {
    const f32 curveSize = evaluateDistanceCurveSize(cameraDistance);
    if (curveSize <= 0.0f)
        return 0.0f;

    const f32 perspectiveSize =
        measurePerspectiveFontSize(anchorX, anchorY, anchorZ, baseScreenY);
    // Blend curve readability with true perspective so scaling tracks camera FOV/aspect.
    const f32 blended = curveSize * 0.4f + perspectiveSize * 0.6f;
    return clampf(blended, kMinFontSize, kMaxFontSize);
}

static f32 evaluateHideSeekFontSize(f32 cameraDistance, f32 anchorX, f32 anchorY, f32 anchorZ,
                                    f32 baseScreenY) {
    f32 size = evaluateTargetFontSize(cameraDistance, anchorX, anchorY, anchorZ, baseScreenY);
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
    measure.syncCharMetrics();

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

static void configurePrinter(J2DPrint &printer, int fontSize, JUtility::TColor topColor,
                             JUtility::TColor bottomColor, bool useGradient) {
    const JUtility::TColor bottom = useGradient ? bottomColor : topColor;
    printer.private_initiate(gpSystemFont, 1, kJ2DPrintDefaultLeading, topColor, bottom);
    printer.initiate();
    printer.setFontSize(fontSize, fontSize);
    printer.syncCharMetrics();
}

static void drawOutline(J2DPrint &printer, int x, int y, const OutlineMetrics &metrics,
                        const char *text) {
    if (metrics.offsetPx <= 0)
        return;

    // Outer Chebyshev ring only — filled disks were O(r^2) J2DPrint calls per tag/frame
    // and spiked low-end CPU with multiple remotes on screen.
    for (int dy = -metrics.offsetPx; dy <= metrics.offsetPx; ++dy) {
        for (int dx = -metrics.offsetPx; dx <= metrics.offsetPx; ++dx) {
            if (dx == 0 && dy == 0)
                continue;

            const int adx = dx < 0 ? -dx : dx;
            const int ady = dy < 0 ? -dy : dy;
            const int cheb = adx > ady ? adx : ady;
            if (cheb != metrics.offsetPx)
                continue;

            printer.print(x + dx, y + dy, "%s", text);
        }
    }
}

// The printer is owned by drawAll and reused across every tag: private_initiate
// fully re-seeds font, colors and leading, so one stack instance per frame does
// the work that used to cost a construct/destruct pair per tag (and per outline
// color switch) with several remotes on screen.
static void drawNameTag(J2DPrint &printer, int x, int y, int fontSize,
                        const OutlineMetrics &outlineMetrics, const char *text,
                        const Appearance &appearance, f32 alpha) {
    if (!text || text[0] == '\0' || alpha <= 0.01f)
        return;

    const JUtility::TColor top = applyAlpha(appearance.textTopColor, alpha);
    const JUtility::TColor bottom = applyAlpha(appearance.textBottomColor, alpha);
    const JUtility::TColor outline = applyAlpha(appearance.outlineColor, alpha);

    if (outlineMetrics.offsetPx > 0) {
        configurePrinter(printer, fontSize, outline, outline, false);
        drawOutline(printer, x, y, outlineMetrics, text);
    }

    configurePrinter(printer, fontSize, top, bottom, appearance.gradientEnabled);
    printer.print(x, y, "%s", text);
}

static void resetSlotRuntime(SlotRuntime &slot) {
    slot.active = false;
    slot.initialized = false;
    slot.smoothedFontSize = kNominalFontSize;
    slot.anchorWorldX = 0.0f;
    slot.anchorWorldY = 0.0f;
    slot.anchorWorldZ = 0.0f;
    slot.screenX = 0.0f;
    slot.screenY = 0.0f;
    slot.smoothedAlpha = 0.0f;
    slot.cameraDistance = 0.0f;
    slot.targetFontSize = 0.0f;
    slot.targetAlpha = 0.0f;
    slot.targetOcclusion = 1.0f;
    slot.smoothedOcclusion = 1.0f;
    slot.drawVisible = false;
    slot.occlusionRefresh = 0;
    slot.occludedSamples = 0;
    slot.visibleSamples = 0;
    slot.projectionMissFrames = 0;
    slot.measuredFontSize = -1;
    slot.measuredTextWidth = 0.0f;
    slot.name[0] = '\0';
}

static bool shouldSnapMotion(const SlotRuntime &slot, f32 bodyX, f32 bodyY, f32 bodyZ, f32 targetFont,
                             bool projected, f32 rawScreenX, f32 rawScreenY) {
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

    // Large screen jumps without body motion usually mean the world head anchor
    // corrected after a stale pose sample — snap instead of easing through it.
    // Do not snap during ordinary movement; that caused client-side flicker.
    if (projected && bodyJump <= kSnapScreenBodyMoveMax) {
        const f32 sdx = rawScreenX - slot.screenX;
        const f32 sdy = rawScreenY - slot.screenY;
        if (sdx * sdx + sdy * sdy >= kSnapScreenDelta * kSnapScreenDelta)
            return true;
    }

    return false;
}

} // namespace

void initSystem() {
    memset(gSlots, 0, sizeof(gSlots));
    for (auto &slot : gSlots)
        resetSlotRuntime(slot);
    gProjectionCache = {};
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
    const bool nameChanged =
        name && name[0] != '\0'
            ? !boundedNameEquals(state.name, name)
            : state.name[0] != '\0';
    if (name && name[0] != '\0')
        strncpy(state.name, name, MAX_PLAYER_NAME - 1);
    else
        state.name[0] = '\0';
    state.name[MAX_PLAYER_NAME - 1] = '\0';
    if (nameChanged) {
        state.measuredFontSize = -1;
        state.measuredTextWidth = 0.0f;
    }

    f32 rawScreenX = 0.0f;
    f32 rawScreenY = 0.0f;
    const bool projected =
        projectWorldToScreen(anchorX, anchorY, anchorZ, rawScreenX, rawScreenY);
    if (projected) {
        state.projectionMissFrames = 0;
    } else if (state.projectionMissFrames < 0xFF) {
        ++state.projectionMissFrames;
    }
    // Temporal LOD and camera matrices can disagree for one update at the edge.
    // Keep the last valid anchor briefly instead of alternating drawVisible.
    const bool onScreen =
        projected || (state.initialized && state.projectionMissFrames < kProjectionMissGraceFrames);
    if (!projected) {
        rawScreenX = state.screenX;
        rawScreenY = state.screenY;
    }
    const f32 distance = measureCameraDistance(anchorX, anchorY, anchorZ);

    state.cameraDistance = distance;
    const f32 moveDx = bodyX - state.lastBodyX;
    const f32 moveDy = bodyY - state.lastBodyY;
    const f32 moveDz = bodyZ - state.lastBodyZ;
    const bool largeMove = !state.initialized ||
                           moveDx * moveDx + moveDy * moveDy + moveDz * moveDz >=
                               kTeleportDistance * kTeleportDistance;
    if (isHideSeekNameTagMode()) {
        // Distance/perspective scaling stays for everyone. Local seekers skip the
        // far-distance fade/cull so teammate tags do not disappear at range.
        state.targetFontSize =
            onScreen ? evaluateHideSeekFontSize(distance, anchorX, anchorY, anchorZ, rawScreenY)
                     : 0.0f;
        if (isLocalHideSeekSeeker())
            state.targetAlpha = onScreen ? 1.0f : 0.0f;
        else
            state.targetAlpha = onScreen ? evaluateTargetAlpha(distance) : 0.0f;
        if (!onScreen) {
            // Preserve the last stable occlusion decision while off-screen.
        } else if (largeMove || state.occlusionRefresh == 0) {
            const bool occluded = isNameTagAnchorOccluded(anchorX, anchorY, anchorZ);
            if (occluded) {
                state.visibleSamples = 0;
                if (state.occludedSamples < 0xFF)
                    ++state.occludedSamples;
                const u8 hideSamples = distance <= kNearbyOcclusionDistance
                                           ? kNearbyOcclusionHideSamples
                                           : kOcclusionHideSamples;
                if (state.occludedSamples >= hideSamples)
                    state.targetOcclusion = 0.0f;
            } else {
                state.occludedSamples = 0;
                if (state.visibleSamples < 0xFF)
                    ++state.visibleSamples;
                if (state.visibleSamples >= kOcclusionShowSamples)
                    state.targetOcclusion = 1.0f;
            }
            state.occlusionRefresh =
                static_cast<u8>(kOcclusionRefreshFrames + (slot % 3));
        } else {
            --state.occlusionRefresh;
        }
        state.drawVisible = onScreen && state.targetFontSize >= kMinFontSize;
    } else {
        state.targetFontSize =
            onScreen ? evaluateTargetFontSize(distance, anchorX, anchorY, anchorZ, rawScreenY)
                     : 0.0f;
        state.targetAlpha = onScreen ? evaluateTargetAlpha(distance) : 0.0f;
        state.targetOcclusion = 1.0f;
        state.occlusionRefresh = 0;
        state.occludedSamples = 0;
        state.visibleSamples = 0;
        // Hysteresis: once visible, keep drawing while the smoothed alpha is still
        // readable. Toggling drawVisible on the raw 0.02 target threshold flickered
        // at fade distances as players moved.
        if (state.drawVisible)
            state.drawVisible = onScreen && state.smoothedAlpha > 0.02f &&
                                state.targetFontSize > 0.25f;
        else
            state.drawVisible = onScreen && state.targetAlpha > 0.05f &&
                                state.targetFontSize > 0.5f;
    }

    const f32 combinedAlphaTarget = state.targetAlpha * state.targetOcclusion;

    state.anchorWorldX = anchorX;
    state.anchorWorldY = anchorY;
    state.anchorWorldZ = anchorZ;

    const bool snap =
        shouldSnapMotion(state, bodyX, bodyY, bodyZ, state.targetFontSize, projected, rawScreenX,
                         rawScreenY);
    const f32 dt = getFrameDelta();

    if (snap || !state.initialized) {
        state.smoothedFontSize = state.targetFontSize;
        state.smoothedOcclusion = state.targetOcclusion;
        state.smoothedAlpha = combinedAlphaTarget;
        state.screenX = rawScreenX;
        state.screenY = rawScreenY;
        state.initialized = true;
    } else {
        state.smoothedFontSize =
            exponentialSmooth(state.smoothedFontSize, state.targetFontSize, kScaleSmoothRate, dt);
        state.smoothedOcclusion =
            exponentialSmooth(state.smoothedOcclusion, state.targetOcclusion, kOcclusionSmoothRate, dt);
        state.smoothedAlpha =
            exponentialSmooth(state.smoothedAlpha, combinedAlphaTarget, kAlphaSmoothRate, dt);
        if (projected) {
            state.screenX = exponentialSmooth(state.screenX, rawScreenX, kAnchorSmoothRate, dt);
            state.screenY = exponentialSmooth(state.screenY, rawScreenY, kAnchorSmoothRate, dt);
        }
    }

    if (isHideSeekNameTagMode())
        state.drawVisible =
            onScreen && state.targetFontSize >= kMinFontSize && state.smoothedAlpha > 0.03f;

    state.lastBodyX = bodyX;
    state.lastBodyY = bodyY;
    state.lastBodyZ = bodyZ;
}

void drawAll(const J2DOrthoGraph *graph) {
#if defined(SMSO_HIDE_NAMETAGS)
    (void)graph;
    return;
#else
    if (!graph || !gpSystemFont)
        return;

    // Skip GX work entirely when nothing is visible — setup2D alone mid-console
    // can still disturb later TGCConsole2 panes (Z-menu digits / radar).
    bool anyVisible = false;
    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        const SlotRuntime &state = gSlots[slot];
        if (!state.active || !state.drawVisible || state.smoothedAlpha <= 0.03f)
            continue;
        if (static_cast<int>(state.smoothedFontSize + 0.5f) < static_cast<int>(kMinFontSize))
            continue;
        anyVisible = true;
        break;
    }
    if (!anyVisible)
        return;

    auto *ctx = const_cast<J2DOrthoGraph *>(graph);
    gx_hud_fence::beginOverlay(ctx);

    J2DPrint printer(gpSystemFont, 1);

    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        SlotRuntime &state = gSlots[slot];
        if (!state.active || !state.drawVisible || state.smoothedAlpha <= 0.03f)
            continue;

        const int fontSize = static_cast<int>(state.smoothedFontSize + 0.5f);
        if (fontSize < static_cast<int>(kMinFontSize))
            continue;

        const OutlineMetrics outlineMetrics = calcOutlineMetrics(state.smoothedFontSize);

        const f32 centerX = state.screenX;
        const f32 centerY = screenAnchorCenterY(state.screenY, state.smoothedFontSize);

        if (state.measuredFontSize != fontSize) {
            state.measuredTextWidth =
                measureTextWidth(state.name, fontSize, state.appearance.textTopColor);
            state.measuredFontSize = fontSize;
        }
        const f32 textWidth = state.measuredTextWidth;
        const f32 textHeight = measureTextHeight(fontSize);

        int x = 0;
        int y = 0;
        computeDrawRect(centerX, centerY, textWidth, textHeight, outlineMetrics.offsetPx, x, y);

        drawNameTag(printer, x, y, fontSize, outlineMetrics, state.name, state.appearance,
                    state.smoothedAlpha);
    }

    // Restore retail HUD GX — do not leave J2DPrint TexObj/TEV for later panes.
    gx_hud_fence::endOverlay(ctx);
#endif
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
