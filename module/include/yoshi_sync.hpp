#pragma once

#include "comm_buffer.hpp"

#include <Dolphin/MTX.h>

class TMario;

struct RemoteYoshiSlot {
    bool mounted;
    bool hatched;
    bool stageInitDone;
    u8 type;
    bool hostSpraying;
    u8 sprayPressureEnc;
    u8 lastTongueState;
    u8 lastMouthActorEnc;
    u8 lastYoshiBck;
    u32 lastFruitEatEventId;
};

namespace JDrama {
class TGraphics;
}

namespace smso {

// Host riding Yoshi: FLUDD pack hidden on Mario (VFX_NO_FLUDD) but current nozzle is Yoshi.
bool snapshotHostOnYoshi(u8 packedNozzle, u16 vfxFlags);

bool remoteBodyRidingYoshi(const RemoteYoshiSlot &slot);
bool remoteBodyRidingYoshi(const TMario *body);

void exportYoshiSnapshotFields(TMario *mario, PlayerSnapshot &snap);
void syncRemoteYoshiFromSnapshot(TMario *body, RemoteYoshiSlot &slot, const PlayerSnapshot &snap);
void performRemoteYoshiDraw(TMario *body, u32 flags, JDrama::TGraphics *graphics, bool drawBody);
void calcRemoteYoshiAnim(TMario *body, const RemoteYoshiSlot *slot);

// Tongue emit matrix after mounted calc + mTongue->calcAnim (doldecomp getTongueMtx).
Mtx *getRemoteYoshiSprayEmitMtx(TMario *body);

bool applyRemoteYoshiFruitWorldEvent(u8 actorTypeEnc, u32 packedPos);

void resetLocalYoshiFruitSync();

} // namespace smso
