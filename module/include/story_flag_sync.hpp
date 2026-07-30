#pragma once

#include "comm_buffer.hpp"

namespace smso {

void initStoryFlagSync();
void resetStoryFlagTrackers();
/// Clear authority overlay + bootstrap + trackers after a host session progress reset
/// so plaza Type5 / card bits cannot be re-applied from stale session caches.
void clearStoryFlagSessionProgress();
void updateStoryFlagSyncConnectionState(bool connected, bool syncEnabled);

// Called from BSE stageInit AFTER resetStage and BEFORE setupObjects/loadAfter.
// Must write pending Type5 overlay into FlagManager here so MareGate / MapEvent
// loadAfter see authoritative bits on the same enter (not one visit late).
void notifyStoryFlagStageEnter(u8 courseId, u8 episodeId);

void captureLocalStoryFlagProgress();
void scrubEphemeralSpawnDirectorFlagsOnStageExit();
bool applyStoryFlagWorldEvent(const CommWorldEvent &event);
bool applyTriggerFlagWorldEvent(const CommWorldEvent &event);
bool applySecretCompleteWorldEvent(const CommWorldEvent &event);

} // namespace smso
