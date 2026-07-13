#pragma once

#include "comm_buffer.hpp"

namespace smso {

void initRedCoinSync();
/// Force per-stage tracker reset on every stage enter (including same course/episode reload).
void notifyRedCoinStageEnter();
void captureLocalRedCoinProgress();
bool applyRedCoinWorldEvent(const CommWorldEvent &event);
void flushDeferredRedCoinEvents();

} // namespace smso
