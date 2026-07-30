#include "music_volume.hpp"
#include "comm_buffer.hpp"

#include <BetterSMS/music.hxx>
#include <SMS/MSound/MSBGM.hxx>

namespace smso {
namespace {

// Decomp MSBgm::setAllTracksVolume uses move-parameter slot 3 so gameplay fades
// on slot 0 (e.g. hotel Shadow Mario DistFade) still multiply correctly.
constexpr u8 kBgmMainVolumeSlot = 3;
constexpr u8 kBgmTrackCount = 3;

u8 readMusicVolumePercent() {
    CommBuffer *buf = getCommBuffer();
    if (!buf)
        return COMM_MUSIC_VOLUME_DEFAULT;
    return buf->musicVolume > 100 ? 100 : buf->musicVolume;
}

void applyRetailBgmVolume(f32 scale) {
    for (u8 track = 0; track < kBgmTrackCount; ++track) {
        if (!MSBgm::getHandle(track))
            continue;
        // fadeFrames=0: snap; slot 3 multiplies with DistFade/xFade on other slots.
        MSBgm::setTrackVolume(track, scale, 0, kBgmMainVolumeSlot);
    }
}

void applyStreamerVolume(u8 percent) {
    using namespace BetterSMS::Music;
    const u8 streamVol =
        static_cast<u8>((static_cast<u32>(percent) * AudioVolumeDefault) / 100u);
    setMaxVolume(streamVol);
    // Stage init paths reset streamer to AudioVolumeDefault — reassert while active.
    if (isPlaying() || isPaused())
        setVolume(streamVol, streamVol);
}

} // namespace

void updateMusicVolume() {
    const u8 percent = readMusicVolumePercent();
    const f32 scale = static_cast<f32>(percent) / 100.0f;
    applyRetailBgmVolume(scale);
    applyStreamerVolume(percent);
}

} // namespace smso
