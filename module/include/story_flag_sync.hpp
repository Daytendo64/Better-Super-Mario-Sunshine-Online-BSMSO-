#pragma once

#include "comm_buffer.hpp"

namespace smso {

void initStoryFlagSync();
void resetStoryFlagTrackers();
void updateStoryFlagSyncConnectionState(bool connected, bool syncEnabled);
void notifyStoryFlagStageEnter(u8 courseId, u8 episodeId);
void captureLocalStoryFlagProgress();
void scrubEphemeralSpawnDirectorFlagsOnStageExit();
bool applyStoryFlagWorldEvent(const CommWorldEvent &event);
bool applyTriggerFlagWorldEvent(const CommWorldEvent &event);
bool applySecretCompleteWorldEvent(const CommWorldEvent &event);

} // namespace smso
