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

    if (handle == gLastLocalVoiceHandle && soundId == gLastLocalVoiceId)
        return;

    gLastLocalVoiceHandle = handle;
    gLastLocalVoiceId = soundId;
    publishLocalVoiceEvent(soundId, director);
}

static bool voiceEventMatchesLocalStage(const MarioVoiceEvent &event, const CommBuffer *buf) {
    return event.stageId == buf->localSnapshot.stageId && event.episodeId == buf->localSnapshot.episodeId;
}

static void playRemoteMarioVoice(u8 slot, const MarioVoiceEvent &event) {
    if (!isMarioVoiceSoundId(event.soundId))
        return;

    TMario *body = getRemoteBodyForSlot(slot);
    if (!body)
        return;

    const Vec *pos = reinterpret_cast<const Vec *>(&body->mTranslation);
    playRemoteMarioPositionalSound(event.soundId, *pos);
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

        gLastRemoteVoiceSequence[slot] = event.sequence;
        if (!voiceEventMatchesLocalStage(event, buf))
            continue;

        playRemoteMarioVoice(static_cast<u8>(slot), event);
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
