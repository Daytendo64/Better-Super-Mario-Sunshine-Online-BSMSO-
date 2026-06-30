#include "remote_mario_audio.hpp"

#include <SMS/MSound/MSound.hxx>
#include <SMS/MSound/MSoundSESystem.hxx>
#include <SMS/Player/Mario.hxx>

extern TMario *gpMarioAddress;
extern MSound *gpMSound;

namespace smso {

namespace {

constexpr f32 kRemoteMarioSoundMaxDistSq =
    kRemoteMarioSoundMaxDistance * kRemoteMarioSoundMaxDistance;

static f32 vecSquareDistance(const TVec3f &a, const TVec3f &b) {
    const f32 dx = a.x - b.x;
    const f32 dy = a.y - b.y;
    const f32 dz = a.z - b.z;
    return dx * dx + dy * dy + dz * dz;
}

} // namespace

bool isRemoteMarioSoundAudible(const TVec3f &sourcePos) {
    if (!gpMarioAddress)
        return false;
    return vecSquareDistance(gpMarioAddress->mTranslation, sourcePos) <= kRemoteMarioSoundMaxDistSq;
}

bool playRemoteMarioPositionalSound(u32 soundId, const Vec &pos) {
    if (!gpMSound || !gpMSound->gateCheck(soundId))
        return false;

    const TVec3f sourcePos = {pos.x, pos.y, pos.z};
    if (!isRemoteMarioSoundAudible(sourcePos))
        return false;

    MSoundSESystem::MSoundSE::startSoundActor(soundId, &pos, 0, nullptr, 0, 4);
    return true;
}

} // namespace smso
