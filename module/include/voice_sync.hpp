#pragma once

#include <Dolphin/types.h>

class TMarDirector;

namespace smso {

void initMarioVoiceSync();
void updateMarioVoiceSync(TMarDirector *director);
void clearMarioVoiceSync();
void resetRemoteMarioVoiceSlot(u8 slot);

} // namespace smso
