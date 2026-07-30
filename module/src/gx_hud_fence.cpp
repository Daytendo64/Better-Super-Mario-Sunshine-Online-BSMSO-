#include "gx_hud_fence.hpp"

#include <BetterSMS/module.hxx>
#include <Dolphin/GX.h>
#include <Dolphin/MTX.h>
#include <JSystem/J2D/J2DOrthoGraph.hxx>
#include <JSystem/JUtility/JUTRect.hxx>
#include <SMS/MarioUtil/gd-reinit-gx.hxx>

namespace smso::gx_hud_fence {

namespace {

constexpr f32 kHudLogicalWidth = 600.0f;
constexpr f32 kHudOrthoTop = 16.0f;
constexpr f32 kHudOrthoBottom = 496.0f;

// doldecomp J2DGrafContext / J2DOrthoGraph field offsets (NTSC-U layout).
// Do not use J2DOrthoGraph::_E8/_EC from the SMS interface header — those names
// do not land on mNear/mFar after the overlapping _D8 JUTRect declaration.
constexpr u32 kOffBounds = 0x08u;
constexpr u32 kOffScissor = 0x18u;
constexpr u32 kOffOrtho = 0xD8u;
constexpr u32 kOffNear = 0xE8u;
constexpr u32 kOffFar = 0xECu;

struct GrafSnapshot {
    JUTRect bounds{};
    JUTRect scissor{};
    JUTRect ortho{};
    f32 nearZ = -1.0f;
    f32 farZ = 1.0f;
    bool valid = false;
};

GrafSnapshot gSaved{};

JUTRect &boundsRef(J2DOrthoGraph *ortho) {
    return *reinterpret_cast<JUTRect *>(reinterpret_cast<u8 *>(ortho) + kOffBounds);
}

JUTRect &scissorRef(J2DOrthoGraph *ortho) {
    return *reinterpret_cast<JUTRect *>(reinterpret_cast<u8 *>(ortho) + kOffScissor);
}

JUTRect &orthoRef(J2DOrthoGraph *ortho) {
    return *reinterpret_cast<JUTRect *>(reinterpret_cast<u8 *>(ortho) + kOffOrtho);
}

f32 &nearRef(J2DOrthoGraph *ortho) {
    return *reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(ortho) + kOffNear);
}

f32 &farRef(J2DOrthoGraph *ortho) {
    return *reinterpret_cast<f32 *>(reinterpret_cast<u8 *>(ortho) + kOffFar);
}

void captureGraf(J2DOrthoGraph *ortho) {
    if (!ortho) {
        gSaved.valid = false;
        return;
    }
    gSaved.bounds = boundsRef(ortho);
    gSaved.scissor = scissorRef(ortho);
    gSaved.ortho = orthoRef(ortho);
    gSaved.nearZ = nearRef(ortho);
    gSaved.farZ = farRef(ortho);
    gSaved.valid = true;
}

void restoreGrafFields(J2DOrthoGraph *ortho) {
    if (!ortho || !gSaved.valid)
        return;
    boundsRef(ortho) = gSaved.bounds;
    scissorRef(ortho) = gSaved.scissor;
    orthoRef(ortho) = gSaved.ortho;
    nearRef(ortho) = gSaved.nearZ;
    farRef(ortho) = gSaved.farZ;
}

/// Pixel viewport + BSE widescreen HUD projection + identity J2D pos mtx.
/// Intentionally does NOT call setPort() — that treats mBounds as a GX viewport
/// and permanently desyncs scissorBounds power when mBounds/mOrtho diverge.
void applyHudSafeGx(J2DOrthoGraph *ortho) {
    ReInitializeGX();
    if (ortho) {
        ortho->setup2D();
        ortho->setLookat();
    }

    const f32 adjustX = BetterSMS::getScreenRatioAdjustX();
    GXSetViewport(0.0f, 0.0f, 640.0f, 480.0f, 0.0f, 1.0f);
    GXSetScissor(0, 0, 640, 480);

    Mtx44 mtx;
    C_MTXOrtho(mtx, kHudOrthoTop, kHudOrthoBottom, -adjustX, kHudLogicalWidth + adjustX, -1.0f,
               1.0f);
    GXSetProjection(mtx, GX_ORTHOGRAPHIC);
}

} // namespace

void beginOverlay(J2DOrthoGraph *ortho) {
    // Snapshot retail mid-TGCConsole2 graf fields BEFORE any overlay draw.
    // Build 48 wrote widescreen logical rects into mBounds and left mOrtho
    // stale; later pane setPort/scissorBounds then sheared shine/blue/gold
    // digit rows (visible mainly during startAppearStar redraws).
    captureGraf(ortho);
    applyHudSafeGx(ortho);
}

void endOverlay(J2DOrthoGraph *ortho) {
    // Put mBounds / mScissor / mOrtho / near / far back exactly so subsequent
    // TGCConsole2 panes keep matching scissorBounds power and setPort inputs.
    restoreGrafFields(ortho);
    applyHudSafeGx(ortho);
    if (ortho)
        ortho->setScissor();
}

} // namespace smso::gx_hud_fence
