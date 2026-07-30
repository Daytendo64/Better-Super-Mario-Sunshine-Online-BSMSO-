#pragma once

#include <Dolphin/types.h>

namespace smso {

void initWorldSync();
void ensureHipDropObjectHooks();
void processWorldEvents();

/// Movie-context / stage-exit retry for Bowser epilogue shine 0x77 (119).
/// Vanilla latches it in TMovieDirector::decideNextMode while stage callbacks are
/// inactive — must force-publish so peers receive it (same class as 0x103AE).
void tryPublishBowserEpilogueShine();

/// One-shot OSReport of live THipDropHideObj / casinorulet counts after setupObjects.
void reportCasinoHipDropSpawnDiag(u8 areaId);

// Enqueue an outbound world event into the shared local queue. Validates sync flags for
// the event type and drops it if the relevant sync category is disabled or the queue is
// full. Actual hand-off to the bridge mailbox happens from processWorldEvents().
// Returns false when the event was not queued (caller must not mark local progress as sent).
bool enqueueLocalWorldEvent(u8 type, u8 courseId, u8 episodeId, u8 payload0, u8 reserved,
                            u32 payload1, u32 payload2 = 0);

// True while a remote shine-get animation is driven locally for this network slot.
bool isRemoteShineCollectActive(u8 slot);

u32 packCollectibleWorldPos(f32 x, f32 y, f32 z);
void unpackCollectibleWorldPos(u32 packed, f32 &x, f32 &y, f32 &z);
bool isValidPackedWorldPos(u32 packed);
bool looksLikePackedCollectibleWorldPos(u32 packed);

// True once the stage director is in normal gameplay and settle frames have elapsed.
bool objectSyncGameplayReady();

} // namespace smso
