#pragma once

#include <Dolphin/types.h>

class J2DOrthoGraph;
class TApplication;

namespace smso::connection_hud {

void initSystem();
void updateSystem(TApplication *app);
void drawSystem(TApplication *app, const J2DOrthoGraph *ortho);

} // namespace smso::connection_hud
