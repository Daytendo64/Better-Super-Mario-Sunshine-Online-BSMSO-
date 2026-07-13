#pragma once

#include "comm_buffer.hpp"

namespace smso {

void initMonteCleanSync();
void notifyMonteCleanStageEnter();
void updateMonteCleanSync();
bool applyMonteCleanWorldEvent(const CommWorldEvent &event);

} // namespace smso
