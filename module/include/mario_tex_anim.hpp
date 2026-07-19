#pragma once

#include <Dolphin/types.h>

class TMario;
class MActor;

namespace smso {

// Optional J3D texture-matrix (BTK) animations for custom Mario packs.
// Retail never loads body/hand/cap BTKs; Shadow Mario / Shadow Luigi ship
// ma_mdl1.btk (and related) under custom/ which we inject into /mario/btk/
// and /mario/custom/. Soft-fails when the mounted archive has no matching BTK.
//
// Shadow packs use deferred BetterSMS-style MActor BTK (not TMultiBtk).
// Bind exact-name BTKs only — never alias right-hand clips onto left hands
// (Shadow ma_hnd2l/ma_hnd3l are TEX1=0; aliased setBtk + entryIn aborts).
// TScreenTexture::replace for H_kagemario_dummy runs on the LOCAL player only —
// applying it to remote Shadow while local is also Shadow crashes Dolphin.
// Remotes still get MActor BTK for UV scroll. Never create during BSE initMario.
//
// Local MActors allocate on the stage heap (not sSystemHeap). System-heap
// parking leaked across warps and hung Gelato→plaza on the 4th Shadow bind
// (mid ma_cap1 after packs=2 remount). Force-rebind / disconnect free them;
// connected stage exit drops tracking and lets the stage heap reclaim.
//
// archiveSlot: CommBuffer network slot whose pack was mounted at bind time.
// Used to remount that pack when retrying deferred MActor create (local pack
// is usually remounted after remote spawn, so getGlbResource would miss).

// Drop all TexAnim bindings. When keepRemoteBindings is true (connected stage
// exit with body pool kept), local bindings are destroyed but remote Shadow
// MActor/BTK bindings stay — they live on the remote heap with the pool bodies.
// Clearing remotes while models survive leaves dangling MaterialAnm pointers and
// crashes when more remotes draw after the next stage enter.
void clearMarioTexAnims(bool keepRemoteBindings = false);
// Drop tracks/MActors for one TMario (body rebuild / abandon). Compacts table.
void releaseMarioTexAnims(TMario *mario);
// True when this TMario has any TexAnim binding (Shadow MActors or TMultiBtk).
bool marioHasTexAnimBinding(TMario *mario);
// Bind once per TMario (safe to call every frame). Soft-fails when no BTKs.
void bindMarioTexAnims(TMario *mario);
void bindMarioTexAnimsForSlot(TMario *mario, u8 archiveSlot);
// Force rebind after initModel rebuild (clears prior tracks for this mario).
void rebindMarioTexAnims(TMario *mario);
void rebindMarioTexAnimsForSlot(TMario *mario, u8 archiveSlot);
// A globally prewarmed body already has complete BTK/MActor state. Retarget only
// the retry/remount slot when assigning it to a player; do not rebuild tracks.
void retargetMarioTexAnimsForSlot(TMario *mario, u8 archiveSlot);
void updateMarioTexAnims(TMario *mario);
void updateAllMarioTexAnims(TMario *localMario);

// Shadow MActor helpers for remote draw (local uses BSE playerDrawHandler).
bool marioHasShadowMActors(TMario *mario);
void entryInMarioShadowMActors(TMario *mario);
void entryOutMarioShadowMActors(TMario *mario);

} // namespace smso
