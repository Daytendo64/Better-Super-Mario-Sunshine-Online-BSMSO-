#pragma once

#include <Dolphin/types.h>

namespace smso {

void initNpcSync();
void ensureNpcReactHooks();
void updateNpcReactSync();
void resetNpcSyncForStage();
void retryPendingRemoteNpcEvents();
void deferRemoteNpcReact(u8 reactionKind, u8 actorSlot, u32 packedPos, u32 payload2 = 0);

bool applyRemoteNpcReact(u8 reactionKind, u8 actorSlot, u32 packedPos, u32 payload2 = 0);

} // namespace smso
