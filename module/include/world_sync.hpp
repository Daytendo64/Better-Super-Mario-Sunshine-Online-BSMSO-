#pragma once

#include <Dolphin/types.h>

namespace smso {

void initWorldSync();
void processWorldEvents();

// True while a remote shine-get animation is driven locally for this network slot.
bool isRemoteShineCollectActive(u8 slot);

u32 packCollectibleWorldPos(f32 x, f32 y, f32 z);
void unpackCollectibleWorldPos(u32 packed, f32 &x, f32 &y, f32 &z);
bool isValidPackedWorldPos(u32 packed);
bool looksLikePackedCollectibleWorldPos(u32 packed);

} // namespace smso
