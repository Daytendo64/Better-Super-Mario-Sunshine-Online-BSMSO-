#pragma once

#include "comm_buffer.hpp"

namespace smso {

void initRedCoinSync();
void captureLocalRedCoinProgress();
bool applyRedCoinWorldEvent(const CommWorldEvent &event);
void flushDeferredRedCoinEvents();

} // namespace smso
