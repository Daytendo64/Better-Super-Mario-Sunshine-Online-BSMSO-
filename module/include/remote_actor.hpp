#pragma once

#include <Dolphin/types.h>

class JKRHeap;
class TMarDirector;
class TMario;

namespace smso {

void initRemoteActors();
void updateRemoteActors(TMarDirector *director);
// Tear down stage-local remote actor state. When keepHeapAndPool is true (still
// connected), the expanded-MEM1 remote heap, pack buffers on that heap, and
// prewarmed body pool survive for the next stage — only perform-group membership
// and per-stage residency flags are cleared. When false, destroy the heap (which
// frees packs + bodies) as on disconnect / offline exit.
void clearRemoteActors(bool keepHeapAndPool = false);

// Lightweight hub / loading preload: ensure remote heap, prefetch one pack, and
// stagger at most one body prewarm per call. Safe on area 15 (file select) and
// during stage load before STATE_NORMAL — does not sync/draw remotes.
void updateRemoteModelPreload(TMarDirector *director);

bool hasRemoteBodyForSlot(u8 slot);
bool hasRemoteBodyForSlotLoose(u8 slot);
TMario *getRemoteBodyForSlot(u8 slot);
TMario *getRemoteBodyForSlotLoose(u8 slot);
bool getRemoteBodyPosition(u8 slot, f32 &x, f32 &y, f32 &z);
bool getRemoteHeadAnchorPosition(u8 slot, f32 &x, f32 &y, f32 &z);
bool isRemoteMarioBody(const TMario *mario);
JKRHeap *borrowRemoteActorHeap();

// True after the remote body's model has been applied once this stage residency
// (late-join first-apply complete). Soft-fail / pack-pending slots stay unfrozen
// so a later successful load can still rebuild. Mid-stage id changes that need a
// rebuild call requestRemoteMarioModelReapply instead of waiting for a warp.
bool isRemoteMarioModelFrozen(u8 slot);

// Clear the apply-once freeze for a remote slot so first-residency can rebuild
// under a newly available pack or a changed CommBuffer model id (same stage).
// Keeps the current body visible until the rebuild succeeds.
void requestRemoteMarioModelReapply(u8 slot);

} // namespace smso
