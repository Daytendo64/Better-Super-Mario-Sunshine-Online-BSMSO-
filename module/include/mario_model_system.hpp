#pragma once

#include <Dolphin/types.h>

class TMarDirector;
class TMario;

namespace smso {

// Per-slot Mario archive remount (SMSCoop-inspired).
// Adopts the game-mounted retail "mario" volume; loads custom packs from
// /data/bsmso_models/<id>.arc via SMSLoadArchive into expanded-MEM1 remote heap
// only when that heap has pack+body headroom — never sSystemHeap / stage heap
// (OOM there aborts). Soft-fails to retail when the pack is missing or the heap
// is too full. Remounts with unmountFixed/mountFixed before local/remote TMario
// model init.
//
// Pack-cache / heap lifetime:
// - Pack buffers live on the remote actor heap. While that heap is kept alive
//   across stage exits (connected sessions), pack cache pointers stay valid and
//   clearMarioModelSystem(keepPackCache=true) must NOT drop them.
// - On disconnect / offline stage exit the heap is destroyed; clear with
//   keepPackCache=false so dangling cache entries are discarded before free.
//
// Local mid-stage CommBuffer model-id changes are deferred to the next stage
// init (same as a warp/reload). Remote bodies apply the CommBuffer id on first
// stage residency (late-join safe, including rebuild of a retail-prewarmed pool
// body). If a remote pack soft-fails or an id arrives/changes after apply, the
// remote path retries same-stage (visible retail kept until rebuild succeeds).
//
// Hitch avoidance: SMSLoadArchive is sync (~1.4–1.9 MiB + DVD seek). Prefer
// prefetchRemoteMarioPacks (1 load/call) on hub / stageInit / idle frames —
// never pack-load + initValues on the same gameplay visibility frame.

void initMarioModelSystem();
// Remount retail and clear per-slot bindings. When keepPackCache is true the
// pack cache (and its remote-heap buffers) survive for the next stage.
void clearMarioModelSystem(bool keepPackCache = false);
void updateMarioModelSystem(TMarDirector *director);

// Budgeted remote pack prefetch: at most one SMSLoadArchive per call.
// Walks CommBuffer remote model ids and resolve/cache misses. Returns true when
// more known ids still need work (caller should keep ticking).
bool prefetchRemoteMarioPacks();

// True when id is empty (retail) or the pack buffer is already in the cache.
// Does not touch the DVD.
bool isMarioModelPackCached(const char id[8]);

// Bind optional /mario/btk/*.btk UV anims after TMario visuals are ready.
void ensureMarioTexAnimsBound(TMario *mario);

// Call before TMario::initValues / initModel for the given network slot.
// Slot MAX_REMOTE_SLOTS (or localSlot from CommBuffer) selects the local pack.
bool setActiveMarioArchive(u32 slot);

// Re-read CommBuffer and resolve/cache the pack for a remote slot (never local).
// Used before remote body spawn / first-residency apply so late-join ids bind.
// Prefer pack-cache remount; SMSLoadArchive only on cache miss.
void syncRemoteMarioArchiveSlot(u32 slot);

// Restore the local player's archive after spawning a remote body.
bool restoreLocalMarioArchive();

// Like restoreLocalMarioArchive, but if local remount fails mount retail so the
// global "mario" volume is never left on a remote/custom pack after a temporary
// remount (Shadow BTK setup / remote spawn). Returns true if local or retail
// is mounted afterward.
bool restoreLocalMarioArchiveGuarded();

// Mount the retail mario volume (no CommBuffer id lookup). Used by remote body
// pool prewarm so unique lobby packs cannot starve later body allocations.
bool mountRetailMarioArchive();

// Rebuild local Mario visuals from the currently mounted "mario" volume.
// Used only from controlled stage-init refresh paths — not mid-stage hot-swap.
bool refreshLocalMarioModel(TMario *mario);

// Copy the CommBuffer model id for a network slot (local or remote).
// out must be at least MARIO_MODEL_ID_SIZE (8) bytes.
void readMarioModelIdForSlot(u32 slot, char out[8]);

// True when this slot has a non-retail pack buffer bound (loaded or cache hit).
// Used to skip first-residency body rebuilds that would only re-spawn retail.
bool marioSlotHasCustomPack(u32 slot);

// Birdo / Yoshi — packs that keep retail caps for init but must not draw them.
bool marioModelIdWantsHiddenCaps(const char id[8]);

// Zero-scale hat J3DModel instances + mtxEffectHide. Safe for local and remote
// (per-instance only — never touches shared J3DModelData shape flags).
void squashHiddenCapDrawInstance(TMario *mario);

// Squash local cap draw matrices around calc/draw perform passes (Yoshi/Birdo).
void maintainLocalHiddenCaps(TMario *mario, u32 performFlags);

} // namespace smso
