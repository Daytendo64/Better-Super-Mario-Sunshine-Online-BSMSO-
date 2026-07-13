#pragma once

#include <Dolphin/types.h>
#include <JSystem/JGeometry/JGMVec.hxx>
#include <SMS/Player/Mario.hxx>

namespace smso {

constexpr f32 kRemoteMarioSoundMaxDistance = 3500.0f;

bool isRemoteMarioSoundAudible(const TVec3f &sourcePos);
bool playRemoteMarioPositionalSound(u32 soundId, const Vec &pos);
bool playRemoteMarioVoiceSound(u32 soundId, TMario *body, u8 slot, u8 voiceFlags);

// Rebind MSound primary player-info to local Mario member addresses (loadAfter
// contract). NOT safe every frame — setPlayerInfo recreates MSRandPlay
// (cry / exert-cont / water-wait) and cuts continuous SE (rollout). Call only
// after local initModel rebuild (new anmMtx pointers), never after archive
// remount alone (remount does not invalidate member/joint pointers).
bool rebindLocalMarioPlayerInfo();

} // namespace smso
