#pragma once

#include "comm_buffer.hpp"

#include <Dolphin/MTX.h>

class TMario;

struct RemoteYoshiSlot {
    bool mounted;
    bool hatched;
    bool stageInitDone;
    /// True after one remote stage-settle attempt this mount cycle.
    /// Remotes never call TYoshi::initInLoadAfter (duplicate TMirrorActor /
    /// mirror-heap exhaustion with 5+ concurrent riders).
    bool stageInitAttempted;
    u8 type;
    bool hostSpraying;
    u8 sprayPressureEnc;
    u8 lastTongueState;
    u8 lastMouthActorEnc;
    u8 lastYoshiBck;
    u32 lastFruitEatEventId;
    /// Last thinkUpper pass left the mouth eat mtx armed — need one closing frame.
    bool thinkUpperMouthWasOpen;
};

namespace JDrama {
class TGraphics;
}

namespace smso {

// Host riding Yoshi: FLUDD pack hidden on Mario (VFX_NO_FLUDD) but current nozzle is Yoshi.
bool snapshotHostOnYoshi(u8 packedNozzle, u16 vfxFlags);

inline bool snapshotYoshiFruitMouthActive(const PlayerSnapshot &snap) {
    return snapshotHostOnYoshi(snap.nozzleId, snap.vfxFlags) &&
           (snap.vfxFlags & VFX_YOSHI_FRUIT_MOUTH) != 0;
}

inline u8 snapshotLogicalEpisodeId(const PlayerSnapshot &snap, u8 /*fallbackEpisodeId*/) {
    return snap.episodeId;
}

inline u8 snapshotYoshiTongueProgressByte(const PlayerSnapshot &snap) {
    return static_cast<u8>(snap.pingMs & 0xFFu);
}

bool remoteBodyRidingYoshi(const RemoteYoshiSlot &slot);
bool remoteBodyRidingYoshi(const TMario *body);

void exportYoshiSnapshotFields(TMario *mario, PlayerSnapshot &snap);
// pingMs low byte: exact TYoshiTongue::mProgress while host tongue is active.
void exportYoshiTongueProgressPingLow(TMario *mario, PlayerSnapshot &snap);
// pingMs high byte: Yoshi BCK frame*8 while host rides and is not spraying juice.
void exportYoshiBckFramePingHigh(TMario *mario, PlayerSnapshot &snap);
void syncRemoteYoshiFromSnapshot(TMario *body, RemoteYoshiSlot &slot, const PlayerSnapshot &snap);
void performRemoteYoshiDraw(TMario *body, u32 flags, JDrama::TGraphics *graphics, bool drawBody);
void calcRemoteYoshiAnim(TMario *body, RemoteYoshiSlot *slot);

// Tongue emit matrix after mounted calc + mTongue->calcAnim (doldecomp getTongueMtx).
Mtx *getRemoteYoshiSprayEmitMtx(TMario *body);

bool applyRemoteYoshiFruitWorldEvent(u8 actorTypeEnc, u32 packedPos);

void resetLocalYoshiFruitSync();

} // namespace smso
