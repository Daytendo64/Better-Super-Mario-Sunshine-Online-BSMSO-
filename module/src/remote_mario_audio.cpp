#include "remote_mario_audio.hpp"

#include <SMS/MSound/MSound.hxx>
#include <SMS/MSound/MSoundSESystem.hxx>
#include <SMS/Player/Mario.hxx>

#include "hide_seek.hpp"
#include "remote_actor.hpp"

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

bool rebindLocalMarioPlayerInfo() {
    // MSound::setPlayerInfo stores Vec*/Mtx permanently AND recreates MSRandPlay
    // registrations (cry / exert-cont / water-wait). Only call after local
    // initModel rebuild (new joint matrices) — never every frame, and never
    // after archive remount alone (remount does not change those pointers).
    // Spurious calls reset MSRandPlay timers and can audibly restart Mario SE.
    if (!gpMSound || !gpMarioAddress)
        return false;
    if (!gpMarioAddress->mModelData || !gpMarioAddress->mModelData->mModel ||
        !gpMarioAddress->mModelData->mModel->mJointArray)
        return false;

    gpMSound->setPlayerInfo(reinterpret_cast<Vec *>(&gpMarioAddress->mTranslation),
                            reinterpret_cast<Vec *>(&gpMarioAddress->mSpeed),
                            gpMarioAddress->mModelData->mModel->mJointArray[1], true);
    return true;
}

bool playRemoteMarioVoiceSound(u32 soundId, TMario *body, u8 slot, u8 voiceFlags) {
    // Do NOT use MSound::startMarioVoice on channel 2: that path is Shadow Mario
    // (pitch 1.2 / port 11). Do NOT call setPlayerInfo for remotes: it rebinds
    // MSRandPlay and mutes local continuous movement SE (rollout / exert).
    // Play the same voice ID as a positional actor SE at the remote body.
    (void)voiceFlags;
    if (shouldSuppressRemoteHiderFromSeekerGrace(slot))
        return false;
    if (!body || !isRemoteMarioBody(body))
        return false;

    const Vec *pos = reinterpret_cast<const Vec *>(&body->mTranslation);
    return playRemoteMarioPositionalSound(soundId, *pos);
}

} // namespace smso
