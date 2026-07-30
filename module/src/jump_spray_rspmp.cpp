#include "jump_spray_rspmp.hpp"

#include "comm_buffer.hpp"
#include "mario_model_system.hpp"

#include <BetterSMS/player.hxx>
#include <Dolphin/string.h>
#include <SMS/Camera/PolarSubCamera.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/Player/Watergun.hxx>
#include <SMS/macros.h>

extern CPolarSubCamera *gpCamera;

namespace smso {
namespace {

// Retail gMarioAnimeData: animId 0x4D → info "jump" → ma_jump.bck.
constexpr u16 kAnimJump = 0x4Du;

// Pack ids (SHA-256 prefix) — Sonic / Shadow hedgehog (not Shadow Mario).
static const char kSonicId[MARIO_MODEL_ID_SIZE] = {'8', '4', '1', '1', '9', '2', 'a', '3'};
static const char kShadowId[MARIO_MODEL_ID_SIZE] = {'2', '3', '7', '0', '4', '0', '6', '8'};

static u16 sRspmpAnimId = 0xFFFF;
static bool sRspmpRegistered = false;

static bool idsEqual8(const char a[MARIO_MODEL_ID_SIZE], const char b[MARIO_MODEL_ID_SIZE]) {
    return memcmp(a, b, MARIO_MODEL_ID_SIZE) == 0;
}

static bool isSonicOrShadowPack(const char id[MARIO_MODEL_ID_SIZE]) {
    if (!id || marioModelIdIsEmpty(id))
        return false;
    return idsEqual8(id, kSonicId) || idsEqual8(id, kShadowId);
}

static bool hostIsSprayingWater(const TMario *mario) {
    if (!mario || !mario->mFludd || !mario->mAttributes.mHasFludd)
        return false;
    if (mario->mFludd->mCurrentWater <= 0)
        return false;

    const bool yCamActive =
        gpCamera && gpCamera->isLButtonCameraSpecifyMode(static_cast<int>(gpCamera->mMode));

    if (mario->mFludd->mIsEmitWater)
        return true;
    if (!yCamActive && mario->mAttributes.mIsFluddEmitting && mario->mFluddUsageState <= 1)
        return true;

    TNozzleBase *nozzle = mario->mFludd->mNozzleList[mario->mFludd->mCurrentNozzle];
    if (!nozzle)
        return false;
    const u8 nozzleType = mario->mFludd->mCurrentNozzle;
    f32 pressure = 0.0f;
    if (nozzleType == TWaterGun::Hover || nozzleType == TWaterGun::Rocket ||
        nozzleType == TWaterGun::Turbo) {
        const auto *trigger = static_cast<const TNozzleTrigger *>(nozzle);
        const f32 maxPressure = trigger->mEmitParams.mInsidePressureMax.get();
        if (maxPressure > 0.0f)
            pressure = trigger->mTriggerFill / maxPressure;
    } else {
        pressure = nozzle->_378;
    }
    return mario->mFluddUsageState <= 1 && pressure > 0.01f;
}

} // namespace

void ensureJumpSprayRspmpAnimRegistered() {
    if (sRspmpRegistered)
        return;
    // Appends ma_rspmp.bck (table name "rspmp") with FLUDD upper = retail pump (68).
    sRspmpAnimId = BetterSMS::Player::addAnimationData("rspmp", true);
    sRspmpRegistered = true;
}

void updateJumpSprayRspmpAnim(TMario *mario) {
    if (!mario)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!buf)
        return;

    char modelId[MARIO_MODEL_ID_SIZE] = {};
    readMarioModelIdForSlot(buf->localSlot, modelId);
    if (!isSonicOrShadowPack(modelId))
        return;

    ensureJumpSprayRspmpAnimRegistered();
    if (sRspmpAnimId == 0xFFFF)
        return;

    const bool spraying = hostIsSprayingWater(mario);
    const u16 anim = mario->mAnimationID;

    if (spraying && anim == kAnimJump) {
        mario->setAnimation(static_cast<int>(sRspmpAnimId), 1.0f);
        return;
    }

    // Keep rspmp while spray continues (retail may try to snap back to jump).
    if (spraying && anim == sRspmpAnimId)
        return;

    // Spray released while still on our override — restore jump if airborne.
    if (!spraying && anim == sRspmpAnimId) {
        const u32 st = mario->mState;
        if (st == TMario::STATE_JUMP || st == TMario::STATE_D_JUMP || st == TMario::STATE_SLIP_JUMP ||
            (st & TMario::STATE_DOJUMP) != 0)
            mario->setAnimation(static_cast<int>(kAnimJump), 1.0f);
    }
}

} // namespace smso
