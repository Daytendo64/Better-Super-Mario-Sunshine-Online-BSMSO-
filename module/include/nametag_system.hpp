#pragma once

#include <Dolphin/types.h>
#include <JSystem/JUtility/JUTColor.hxx>

class J2DOrthoGraph;

namespace smso::nametag {

struct Appearance {
    JUtility::TColor textTopColor;
    JUtility::TColor textBottomColor;
    JUtility::TColor outlineColor;
    bool hasOutlineColor;
    bool gradientEnabled;
};

struct DebugState {
    f32 cameraDistance;
    f32 targetFontSize;
    f32 smoothedFontSize;
    f32 targetAlpha;
    f32 smoothedAlpha;
    bool visible;
};

void initSystem();
void clearSystem();

void setDebugOverlayEnabled(bool enabled);
bool isDebugOverlayEnabled();

// anchor* = animated head crown in world space; body* = root position for teleport detection.
void updateSlot(u8 slot, bool active, f32 anchorX, f32 anchorY, f32 anchorZ, f32 bodyX, f32 bodyY,
                f32 bodyZ, const Appearance &appearance, const char *name);
void drawAll(const J2DOrthoGraph *graph);

bool getSlotDebugState(u8 slot, DebugState &out);

} // namespace smso::nametag
