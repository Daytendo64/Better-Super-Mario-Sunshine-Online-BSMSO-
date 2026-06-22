#pragma once

#include <Dolphin/types.h>

class J2DOrthoGraph;
class TMarDirector;

namespace smso {

void initRemoteMarioVisuals();
void updateRemoteMarioVisuals(TMarDirector *director);
void drawRemoteMarioOverlays(const J2DOrthoGraph *graph);
void clearRemoteMarioVisuals();
bool shouldUseParticleProxy(u8 slot);

} // namespace smso
