#pragma once

#include "comm_buffer.hpp"

namespace smso {

/// Graffiti / goop sync is permanently disabled. APIs remain as no-ops so
/// module call sites still compile/link. Do not re-enable without a non-durable
/// design — cell spray previously flooded durable history and starved progress sync.
void initGraffitiCleanSync();
void notifyGraffitiCleanStageEnter();
void updateGraffitiCleanSync();
bool applyGraffitiCleanWorldEvent(const CommWorldEvent &event);

/// No-op (pollution clean trampoline removed).
void onLocalPollutionClean(f32 x, f32 y, f32 z, f32 size);

/// No-op (remote spray assist disabled).
void notifyRemoteSprayEmit(f32 ox, f32 oy, f32 oz, f32 dx, f32 dy, f32 dz);

} // namespace smso
