#pragma once

#include "comm_buffer.hpp"

#include <Dolphin/types.h>

class TMario;
struct J3DModel;

namespace JDrama {
class TGraphics;
}

namespace smso {

// doldecomp MarioStatus.hpp status type+id mask (low 9 bits of mState).
constexpr u32 kBlooperSurfStatusMask = 0x1FFu;
constexpr u32 kBlooperSurfStatusId = 0x046u;
constexpr u32 kBlooperSurfJumpStatusId = 0x09Au;
constexpr u32 kBlooperSurfDrawFlag = 0x10000u;
constexpr u32 kBlooperSurfMarioState = 0x810446u;
constexpr u32 kBlooperSurfJumpMarioState = 0x281089Au;
constexpr u16 kBlooperSurfRideShellAnim = 0x6Du;

constexpr u8 kSurfGessoTypeCount = 3u;
constexpr u8 kSurfGessoTypeMask = 0x03u;

struct BlooperSurfSlot {
    u8 gessoType;
    void *cloneActor;
    J3DModel *cloneModel;
    u8 cloneType;
    bool bindPending;
};

inline u32 snapshotMarioState(const PlayerSnapshot &snap) {
    return static_cast<u32>(snap.actionId) | (static_cast<u32>(snap.actionIdHi) << 16);
}

inline u32 blooperSurfStatusId(u32 state) {
    return state & kBlooperSurfStatusMask;
}

inline bool isBlooperSurfState(u32 state) {
    if ((state & kBlooperSurfDrawFlag) == 0)
        return false;
    const u32 id = blooperSurfStatusId(state);
    return id == kBlooperSurfStatusId || id == kBlooperSurfJumpStatusId;
}

inline bool isBlooperSurfRideState(u32 state) {
    return isBlooperSurfState(state) && blooperSurfStatusId(state) == kBlooperSurfStatusId;
}

inline bool snapshotIsBlooperSurfing(const PlayerSnapshot &snap) {
    if ((snap.vfxFlags & VFX_DEAD) != 0)
        return false;
    return isBlooperSurfState(snapshotMarioState(snap));
}

inline u8 snapshotSurfGessoType(const PlayerSnapshot &snap) {
    return static_cast<u8>(snap.water & kSurfGessoTypeMask);
}

bool isLocalBlooperSurf(const TMario *mario);

u8 exportBlooperSurfWaterByte(const TMario *mario);
void exportBlooperSurfSnapshotFields(const TMario *mario, PlayerSnapshot &snap);

void initBlooperSurfSync();
void releaseBlooperSurfClone(BlooperSurfSlot &slot);
void applyRemoteBlooperSurfSnapshot(TMario *body, BlooperSurfSlot &slot, const PlayerSnapshot &snap);
void updateRemoteBlooperSurfFrame(TMario *body, BlooperSurfSlot *slot, JDrama::TGraphics *graphics);
void resetBlooperSurfSlot(BlooperSurfSlot &slot);
bool remoteBlooperSurfUsesVfx(u32 state);

} // namespace smso
