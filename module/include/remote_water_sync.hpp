#pragma once

#include <Dolphin/types.h>

class TWaterEmitInfo;

namespace smso {

void initRemoteWaterSync();
void updateRemoteWaterSync();

void emitRemoteWaterRequest(TWaterEmitInfo *emitInfo);

// doldecomp TModelWaterManager::unk5D5F — global droplet draw tint; save/restore around emits.
void emitRemoteWaterRequestWithCardTint(TWaterEmitInfo *emitInfo, u8 waterCardType);

void resetRemoteYoshiJuiceDrawTint();
void notifyRemoteYoshiJuiceDrawTint(u8 yoshiType);

} // namespace smso
