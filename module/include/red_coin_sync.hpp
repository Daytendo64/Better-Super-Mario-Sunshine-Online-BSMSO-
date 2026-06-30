#pragma once

#include "comm_buffer.hpp"

class TMapObjBase;

namespace smso {

void initRedCoinSync();
void captureLocalRedCoinProgress();
bool applyRedCoinWorldEvent(const CommWorldEvent &event);
void flushDeferredRedCoinEvents();
u32 redCoinSwitchVtable();
void applyRemoteRedCoinSwitchHit(TMapObjBase *switchObj);

} // namespace smso
