#include "coin_collect_fx.hpp"

#include "particle_ids.hpp"

#include <SMS/MSound/MSound.hxx>
#include <SMS/MSound/MSoundSESystem.hxx>
#include <SMS/Manager/MarioParticleManager.hxx>
#include <SMS/macros.h>

extern TMarioParticleManager *gpMarioParticleManager;
extern MSound *gpMSound;

namespace smso {

namespace {

static f32 engineSoundDistanceMax() {
    auto *distanceMax =
        reinterpret_cast<f32 *>(SMS_PORT_REGION(0x8040CD50, 0x804044b0, 0, 0));
    if (!distanceMax || *distanceMax <= 0.0f)
        return 3000.0f;
    return *distanceMax;
}

static bool isRemoteCoinCollectSoundAudible(u32 soundId, const Vec &pos) {
    if (!gpMSound || !gpMSound->gateCheck(soundId))
        return false;

    if (!MSoundSESystem::MSoundSE::checkSoundArea(soundId, pos))
        return false;

    const f32 maxDist = engineSoundDistanceMax();
    return gpMSound->getDistPowFromCamera(pos) <= maxDist * maxDist;
}

} // namespace

void playRemoteCoinCollectParticles(const TVec3f &pos, bool blueCoin) {
    if (gpMarioParticleManager) {
        TVec3f emitPos = pos;
        emitPos.y += 25.0f;

        gpMarioParticleManager->emit(particles::kCoinGetA, &emitPos, 0, nullptr);
        gpMarioParticleManager->emit(particles::kCoinGetB, &emitPos, 0, nullptr);
        if (blueCoin)
            gpMarioParticleManager->emit(particles::kBlueCoinKira, &emitPos, 0, nullptr);
    }

    const u32 soundId = blueCoin ? MSD_SE_SY_BLUE_COIN_GET : MSD_SE_SY_RED_COIN_GET;
    const Vec soundPos = {pos.x, pos.y, pos.z};
    if (!isRemoteCoinCollectSoundAudible(soundId, soundPos))
        return;

    MSoundSESystem::MSoundSE::startSoundActor(soundId, &soundPos, 0, nullptr, 0, 4);
}

} // namespace smso
