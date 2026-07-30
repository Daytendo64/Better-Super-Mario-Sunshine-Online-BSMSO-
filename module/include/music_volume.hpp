#pragma once

namespace smso {

/// Apply launcher music-volume setting to retail MSBgm (slot 3) and BSE AudioStreamer.
/// Safe to call every frame; independent of SFX / Mario voice.
void updateMusicVolume();

} // namespace smso
