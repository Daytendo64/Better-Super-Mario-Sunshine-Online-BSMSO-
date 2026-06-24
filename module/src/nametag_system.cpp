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

#include "comm_buffer.hpp"
#include "hide_seek.hpp"

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
static constexpr f32 kMinScaleDistance = 7500.0f;
static constexpr f32 kFadeStartDistance = 9500.0f;
static constexpr f32 kCullDistance = 13000.0f;

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
    bool useDiagonals;
};

static OutlineMetrics calcOutlineMetrics(f32 fontSize) {
    OutlineMetrics metrics{};
    if (fontSize < 7.0f)
        return metrics;

    // Scale outline offset with text size: ~1px at medium, up to 2px at max, none when tiny.
    int offset = static_cast<int>(fontSize / 11.0f + 0.35f);
    if (offset < 1)
        offset = 1;
    if (offset > 2)
        offset = 2;

    metrics.offsetPx = offset;
    metrics.useDiagonals = fontSize >= 12.0f;
    return metrics;
}

static f32 screenAnchorCenterY(f32 headScreenY) {
    return headScreenY - kGapAboveAnchorPx - kNominalFontSize * 0.5f;
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

    for (int layer = 1; layer <= metrics.offsetPx; ++layer) {
        for (int dy = -layer; dy <= layer; ++dy) {
            for (int dx = -layer; dx <= layer; ++dx) {
                if (dx == 0 && dy == 0)
                    continue;

                const int adx = dx < 0 ? -dx : dx;
                const int ady = dy < 0 ? -dy : dy;
                const int cheb = adx > ady ? adx : ady;
                if (cheb != layer)
                    continue;

                if (!metrics.useDiagonals && dx != 0 && dy != 0)
                    continue;

                printLayer(x + dx, y + dy, fontSize, text, outlineColor, outlineColor, false);
            }
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

    if (appearance.hasOutlineColor && outlineMetrics.offsetPx > 0)
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
        state.drawVisible = onScreen && state.targetFontSize >= kMinFontSize;
    } else {
        state.targetFontSize = onScreen ? evaluateTargetFontSize(distance, anchorX, anchorY, anchorZ) : 0.0f;
        state.targetAlpha = onScreen ? evaluateTargetAlpha(distance) : 0.0f;
        state.drawVisible = onScreen && state.targetAlpha > 0.02f && state.targetFontSize > 0.5f;
    }

    state.anchorWorldX = anchorX;
    state.anchorWorldY = anchorY;
    state.anchorWorldZ = anchorZ;

    const bool snap = shouldSnapMotion(state, bodyX, bodyY, bodyZ, state.targetFontSize);
    const f32 dt = getFrameDelta();

    if (snap || !state.initialized) {
        state.smoothedFontSize = state.targetFontSize;
        state.smoothedAlpha = state.targetAlpha;
        state.initialized = true;
    } else {
        state.smoothedFontSize =
            exponentialSmooth(state.smoothedFontSize, state.targetFontSize, kScaleSmoothRate, dt);
        state.smoothedAlpha =
            exponentialSmooth(state.smoothedAlpha, state.targetAlpha, kAlphaSmoothRate, dt);
    }

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
        const f32 centerY = screenAnchorCenterY(screenY);

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
