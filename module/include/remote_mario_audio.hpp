#pragma once

#include <Dolphin/types.h>
#include <JSystem/JGeometry/JGMVec.hxx>

namespace smso {

constexpr f32 kRemoteMarioSoundMaxDistance = 3500.0f;

bool isRemoteMarioSoundAudible(const TVec3f &sourcePos);
bool playRemoteMarioPositionalSound(u32 soundId, const Vec &pos);

} // namespace smso
