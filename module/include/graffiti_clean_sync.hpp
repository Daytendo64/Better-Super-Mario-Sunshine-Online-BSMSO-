#pragma once

#include "comm_buffer.hpp"

namespace smso {

void initGraffitiCleanSync();
void notifyGraffitiCleanStageEnter();
void updateGraffitiCleanSync();
bool applyGraffitiCleanWorldEvent(const CommWorldEvent &event);

/// Called from the TPollutionManager::clean trampoline after the retail stamp.
/// Publishes a durable cell-deduped graffiti clean when the local player authored it.
void onLocalPollutionClean(f32 x, f32 y, f32 z, f32 size);

/// Called from remote FLUDD droplet emit with the same emit ray used for VFX.
/// Immediately raycasts and retail-cleans (no publish) so viewer wall graffiti
/// tracks visible spray instead of a fragile later-frame emit-mtx probe.
void notifyRemoteSprayEmit(f32 ox, f32 oy, f32 oz, f32 dx, f32 dy, f32 dz);

} // namespace smso
