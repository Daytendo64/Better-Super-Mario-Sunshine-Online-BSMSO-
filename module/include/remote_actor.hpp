#pragma once

#include <Dolphin/types.h>

class JKRHeap;
class TMarDirector;
class TMario;

namespace smso {

void initRemoteActors();
void updateRemoteActors(TMarDirector *director);
void clearRemoteActors();
bool hasRemoteBodyForSlot(u8 slot);
TMario *getRemoteBodyForSlot(u8 slot);
bool getRemoteBodyPosition(u8 slot, f32 &x, f32 &y, f32 &z);
bool getRemoteHeadAnchorPosition(u8 slot, f32 &x, f32 &y, f32 &z);
JKRHeap *borrowRemoteActorHeap();

} // namespace smso
