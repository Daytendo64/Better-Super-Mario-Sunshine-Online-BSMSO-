#pragma once

class J2DOrthoGraph;

namespace smso::gx_hud_fence {

/// BSE Stage::addDraw2DCallback injects into TGCConsole2::perform (mid-HUD).
/// Custom J2DPrint / fill draws leave TEV / TexObj / scissor / transform dirty,
/// which then paints garbage over later console panes (blue-coin digits, Z-map
/// radar, FLUDD water-meter outline) or shears counter digit panes when the
/// fence mutates J2DOrthoGraph mBounds without restoring mOrtho.
///
/// begin/end: snapshot + restore full graf fields (mBounds/mScissor/mOrtho),
/// and re-apply HUD-safe GX (ReInitializeGX, identity lookat, 640x480 viewport,
/// BSE widescreen ortho). Do not leave logical widescreen rects in mBounds.
void beginOverlay(J2DOrthoGraph *ortho);
void endOverlay(J2DOrthoGraph *ortho);

} // namespace smso::gx_hud_fence
