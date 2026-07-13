#include "voice_sync.hpp"

#include "comm_buffer.hpp"
#include "remote_actor.hpp"
#include "remote_mario_audio.hpp"

#include <Dolphin/MTX.h>
#include <Dolphin/mem.h>
#include <SMS/MSound/MSound.hxx>
#include <SMS/MSound/MSoundSESystem.hxx>
#include <SMS/Player/Mario.hxx>
#include <SMS/System/MarDirector.hxx>

extern TMario *gpMarioAddress;
extern TMarDirector *gpMarDirector;

namespace smso {

namespace {

static constexpr u32 kInvalidVoiceId = 0xFFFFFFFF;
static constexpr u8 kLocalVoiceChannel = 0;
static constexpr u8 kMarioVoiceLowHealthFlag = 1 << 0;
static constexpr u16 kLowHealthThreshold = 2;

u16 gLocalVoiceSequence = 0;
u32 gLastLocalVoiceId = kInvalidVoiceId;
u32 gLastLocalVoiceHandle = 0;
u16 gLastRemoteVoiceSequence[MAX_REMOTE_SLOTS];

static bool isMarioVoiceSoundId(u32 soundId) {
    // BSE's MSound.hxx voice audit:
    // - 0x7800..0x7917 are MA_VO / MV Mario voice clips.
    // - 0x7094 is MV16_EXERT_CONT_01 (same Mario voice family, different category bits).
    // - 0x792c..0x792f are late MV clips; skip 0x7918..0x792b Yoshi voices.
    // FLUDD voice lines are NPC_VM_NOZ_* in the 0x88b0 range and never pass this filter.
    return soundId == 0x00007094 || (soundId >= 0x00007800 && soundId <= 0x00007917) ||
           (soundId >= 0x0000792C && soundId <= 0x0000792F);
}

// MSRandPlay ambient clips (idle cry / continuous exert). These fire randomly
// via setPlayerInfo on the local player and must NOT be networked — holding them
// until a remote enters hearing range makes a Mario voice play on approach.
static bool isAmbientMarioVoiceSoundId(u32 soundId) {
    switch (soundId) {
    case MSD_SE_MV10A_CRY_SHORT_01:
    case MSD_SE_MV10A_CRY_SHORT_02:
    case MSD_SE_MV10A_CRY_SHORT_03:
    case MSD_SE_MV16_EXERT_CONT_01:
        return true;
    default:
        return false;
    }
}

static u8 currentMarioHealthByte() {
    if (!gpMarioAddress)
        return 0;
    return gpMarioAddress->mHealth > 255 ? 255 : static_cast<u8>(gpMarioAddress->mHealth);
}

static void publishLocalVoiceEvent(u32 soundId, TMarDirector *director) {
    CommBuffer *buf = getCommBuffer();
    if (!buf || !director)
        return;

    MarioVoiceEvent &event = buf->localMarioVoiceEvent;
    event.soundId = soundId;
    event.sequence = ++gLocalVoiceSequence;
    event.health = currentMarioHealthByte();
    event.flags = event.health <= kLowHealthThreshold ? kMarioVoiceLowHealthFlag : 0;
    event.stageId = buf->localSnapshot.stageId;
    event.episodeId = buf->localSnapshot.episodeId;
    event.reserved0 = 0;
    event.reserved1 = 0;
}

static bool isLocalMarioOwningChannel0Voice() {
    if (!gpMarioAddress || !gpMarDirector)
        return false;
    if (gpMarDirector->mCurState < TMarDirector::STATE_NORMAL)
        return false;
    if (gpMarioAddress->mHolder != nullptr)
        return false;
    return true;
}

static void captureLocalMarioVoice(TMarDirector *director) {
    if (!gpMSound)
        return;

    CommBuffer *buf = getCommBuffer();
    if (!buf || (buf->bridgeFlags & BF_CONNECTED) == 0)
        return;

    const u32 handle = gpMSound->checkMarioVoicePlaying(kLocalVoiceChannel);
    if (!handle) {
        gLastLocalVoiceHandle = 0;
        gLastLocalVoiceId = kInvalidVoiceId;
        return;
    }

    const u32 soundId = gpMSound->getMarioVoiceID(kLocalVoiceChannel);
    if (soundId == kInvalidVoiceId || !isMarioVoiceSoundId(soundId))
        return;
    if (isAmbientMarioVoiceSoundId(soundId))
        return;

    if (!isLocalMarioOwningChannel0Voice())
        return;

    if (handle == gLastLocalVoiceHandle && soundId == gLastLocalVoiceId)
        return;

    gLastLocalVoiceHandle = handle;
    gLastLocalVoiceId = soundId;
    publishLocalVoiceEvent(soundId, director);
}

static bool voiceEventMatchesLocalStage(const MarioVoiceEvent &event, const CommBuffer *buf) {
    return event.stageId == buf->localSnapshot.stageId && event.episodeId == buf->localSnapshot.episodeId;
}

static bool playRemoteMarioVoice(u8 slot, const MarioVoiceEvent &event) {
    if (!isMarioVoiceSoundId(event.soundId) || isAmbientMarioVoiceSoundId(event.soundId))
        return false;

    TMario *body = getRemoteBodyForSlot(slot);
    if (!body)
        return false;

    return playRemoteMarioVoiceSound(event.soundId, body, slot, event.flags);
}

static void consumeRemoteMarioVoices() {
    CommBuffer *buf = getCommBuffer();
    if (!buf || !gpMSound)
        return;

    for (u32 slot = 0; slot < MAX_REMOTE_SLOTS; ++slot) {
        if (buf->remoteSnapshots[slot].connected == 0) {
            gLastRemoteVoiceSequence[slot] = 0;
            continue;
        }

        const MarioVoiceEvent &event = buf->remoteMarioVoiceEvents[slot];
        if (event.sequence == 0 || event.sequence == gLastRemoteVoiceSequence[slot])
            continue;

        // Permanent skips: wrong stage, ambient MSRandPlay clips, or unknown IDs.
        if (!voiceEventMatchesLocalStage(event, buf) || !isMarioVoiceSoundId(event.soundId) ||
            isAmbientMarioVoiceSoundId(event.soundId)) {
            gLastRemoteVoiceSequence[slot] = event.sequence;
            continue;
        }

        TMario *body = getRemoteBodyForSlot(static_cast<u8>(slot));
        if (!body) {
            // Puppet not ready yet — keep pending for a later frame.
            continue;
        }

        // Out of hearing range: drop, do NOT hold until the player walks in.
        // Holding made idle/action voices fire the moment a remote entered the
        // 3500uu audible radius (felt like a Mario SE on approach).
        if (!isRemoteMarioSoundAudible(body->mTranslation)) {
            gLastRemoteVoiceSequence[slot] = event.sequence;
            continue;
        }

        if (playRemoteMarioVoice(static_cast<u8>(slot), event))
            gLastRemoteVoiceSequence[slot] = event.sequence;
    }
}

} // namespace

void initMarioVoiceSync() {
    gLocalVoiceSequence = 0;
    gLastLocalVoiceId = kInvalidVoiceId;
    gLastLocalVoiceHandle = 0;
    memset(gLastRemoteVoiceSequence, 0, sizeof(gLastRemoteVoiceSequence));
}

void updateMarioVoiceSync(TMarDirector *director) {
    // Do not call setPlayerInfo / rebindLocalMarioPlayerInfo every frame.
    // setPlayerInfo recreates MSRandPlay entries and interrupts continuous
    // local movement SE (rollout / exert-cont / water-wait).
    captureLocalMarioVoice(director);
    consumeRemoteMarioVoices();
}

void clearMarioVoiceSync() {
    initMarioVoiceSync();
}

void resetRemoteMarioVoiceSlot(u8 slot) {
    if (slot < MAX_REMOTE_SLOTS)
        gLastRemoteVoiceSequence[slot] = 0;
}

} // namespace smso
