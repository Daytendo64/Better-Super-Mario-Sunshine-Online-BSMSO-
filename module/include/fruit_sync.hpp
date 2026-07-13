#pragma once

#include <Dolphin/types.h>

class TMario;
class TMarDirector;
class TMapObjBase;
class TTakeActor;

namespace smso {

void initFruitSync();
void ensureMarioFruitHooks();
void updateLocalMarioFruitCapture(TMario *mario);
void updateRemoteCarriedFruit();
void retryPendingRemoteFruitEvents();
void deferRemoteMarioFruitWorldEvent(u8 eventType, u8 fruitEnc, u8 actorSlot, u32 packedPos,
                                     u32 packedVel = 0);
void resetFruitSyncForStage();

// Active synced fruit held by a remote puppet (for draw-time mHeldObject restore).
TTakeActor *getRemoteCarriedFruitActor(u8 slot);
void clearRemoteCarriedFruit(u8 slot);

bool applyRemoteMarioFruitWorldEvent(u8 eventType, u8 fruitEnc, u8 actorSlot, u32 packedPos,
                                       u32 packedVel = 0);
bool applyRemoteMarioFruitSync(u8 fruitEnc, u8 actorSlot, u32 packedPos, u32 packedVel);

} // namespace smso
