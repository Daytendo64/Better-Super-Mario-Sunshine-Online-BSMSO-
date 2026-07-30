#pragma once

#include <Dolphin/types.h>

namespace smso::collision_los {

/// True when map collision is loaded and MarDirector is in a playable state.
bool isReady();

/// Camera → world-point wall/roof raycast (floors / water / Mario-through skipped).
/// Used to hide remotes when GPU Z fails (camera jammed inside one-sided geometry).
bool isPointOccludedFromCamera(f32 worldX, f32 worldY, f32 worldZ);

} // namespace smso::collision_los
