#pragma once

#include <Dolphin/types.h>

class TMarDirector;
class TMario;

namespace smso {

// Per-slot Mario archive remount (SMSCoop-inspired).
// Adopts the game-mounted retail "mario" volume; loads custom packs from
// /data/bsmso_models/<id>.arc through a single demand-driven DVD async job into
// the dedicated expanded-MEM1 pack heap — never sSystemHeap / stage heap (OOM
// there aborts). Soft-fails to retail when the pack is missing or the pack heap
// is full. Remounts with unmountFixed/mountFixed before local/remote TMario init.
//
// Pack-cache / heap lifetime:
// - Pack buffers live on the remote actor heap and stay resident across connected
//   stage transitions. This avoids repeating DVD reads for every warp while the
//   same session is active; imports copied after Dolphin started become visible
//   after disconnect/relaunch, when the owning heap is safely destroyed.
// - Buffers are never replaced while live TMario/J3D graphs reference them.
// - On disconnect / offline stage exit the heap is destroyed; clear with
//   keepPackCache=false so dangling cache entries are discarded before free.
//
// Local mid-stage CommBuffer model-id changes are deferred to the next stage
// init (same as a warp/reload). Remote bodies apply the CommBuffer id on first
// stage residency (late-join safe, including rebuild of a retail-prewarmed pool
// body). If a remote pack soft-fails or an id arrives/changes after apply, the
// remote path retries same-stage (visible retail kept until rebuild succeeds).
//
// Gameplay archive reads use DVDReadAsyncPrio (exact FST length, never rounded
// past EOF — DirectoryBlob over-reads raise the retail disc-error fatal).
// DVD/FST/validation failures quarantine the pack id for the boot and soft-fail
// to retail with no retry stampede. Synchronous SMSLoadArchive is restricted to
// stage/loading initialization fallback. Body construction is a separate
// safe-window phase; activation is pointer-only.

void initMarioModelSystem();
// True when the previous stage never ran clearMarioModelSystem (soft reload /
// skipped exit). Callers may tear remotes before initMarioModelSystem.
bool marioModelSystemIsLive();
// Remount retail and clear per-slot bindings. When keepPackCache is true the
// pack cache (and its remote-heap buffers) survive for the next stage.
void clearMarioModelSystem(bool keepPackCache = false);
void updateMarioModelSystem(TMarDirector *director);

// Advance/start the one-in-flight archive state machine. Priority is active
// swap, newly connected remote, active roster, then next-stage local selection.
// Installed-library speculation is intentionally excluded. This never performs
// synchronous archive I/O during active gameplay.
bool prefetchRemoteMarioPacks(bool *outLoadStarted = nullptr);

// True when id is empty (retail) or the pack buffer is already in the cache.
// Does not touch the DVD.
bool isMarioModelPackCached(const char id[8]);

// True when the pack has spent at least one complete update frame resident.
// Body creation/rebuild uses this gate so SMSLoadArchive and initValues never
// compound on the same visible frame.
bool isMarioModelPackReadyForBodyInit(const char id[8]);

// Cache-only model access for bounded ready-body prewarm. These functions never
// touch DVD. Entries remain valid until the owning remote heap is destroyed.
u32 marioModelPackCacheCount();
bool readMarioModelPackCacheId(u32 index, char out[8]);
bool mountCachedMarioModelPack(const char id[8]);

// Requests a stage-exit heap recycle only for cache-specific reasons. Normal
// connected transitions retain the bounded cache; disconnect still clears it.
bool marioModelPackCacheNeedsRecycleOnStageExit();

// Bind optional /mario/btk/*.btk UV anims after TMario visuals are ready.
void ensureMarioTexAnimsBound(TMario *mario);

// TMario::TBodyAngleParams loads from the "params" volume (params.szs), not the
// remounted character pack. Tall packs inject BodyAngleFree.prm into the mario
// RARC; after local Mario exists, reload mBodyAngleFreeParams from that file so
// run lean (mWaistPitch) matches the pack instead of retail params.szs.
void ensureLocalBodyAngleFreeParams(TMario *mario);

// Call before TMario::initValues / initModel for the given network slot.
// Slot MAX_REMOTE_SLOTS (or localSlot from CommBuffer) selects the local pack.
// Returns true whenever a usable "mario" volume is live, which includes the
// soft-fail where the slot's pack was rejected and retail was mounted instead.
// In that case the slot is rebound to retail, so marioSlotHasCustomPack() —
// not this return value — tells callers what actually backs initValues.
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
