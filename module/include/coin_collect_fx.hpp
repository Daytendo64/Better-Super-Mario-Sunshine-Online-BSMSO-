#pragma once

#include <SMS/MapObj/MapObjBase.hxx>

namespace smso {

// Particles + distance-local pickup SFX for remote coin collections (vanilla taken() is never called on apply).
void playRemoteCoinCollectParticles(const TVec3f &pos, bool blueCoin);

} // namespace smso
