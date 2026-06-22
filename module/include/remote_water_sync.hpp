#pragma once

class TWaterEmitInfo;

namespace smso {

void initRemoteWaterSync();
void updateRemoteWaterSync();
void emitRemoteWaterRequest(TWaterEmitInfo *emitInfo);

} // namespace smso
