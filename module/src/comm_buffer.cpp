#include "comm_buffer.hpp"

#include <Dolphin/OS.h>
#include <Dolphin/mem.h>
#include <Dolphin/string.h>

namespace smso {

static CommBuffer s_commBuffer;

CommBuffer *getCommBuffer() {
    return &s_commBuffer;
}

void publishMailboxAnchor() {
    CommBuffer *buf = getCommBuffer();
    buf->magic = COMM_MAGIC;
    buf->version = COMM_VERSION;

    volatile CommMailboxAnchor *anchor =
        reinterpret_cast<volatile CommMailboxAnchor *>(COMM_GUEST_ADDRESS);
    anchor->magic = COMM_MAGIC;
    anchor->version = COMM_VERSION;
    anchor->reserved = 0;
    anchor->bufferGuest = reinterpret_cast<u32>(buf);
}

void initCommBuffer() {
    CommBuffer *buf = getCommBuffer();
    memset(buf, 0, sizeof(CommBuffer));
    buf->magic = COMM_MAGIC;
    buf->version = COMM_VERSION;
    buf->warpTargetSlot = WARP_NO_TARGET;
    publishMailboxAnchor();
    OSReport("[SMSO] Comm buffer @ %p anchor @ 0x%08X\n", buf, COMM_GUEST_ADDRESS);
}

void resetCommBuffer() {
    CommBuffer *buf = getCommBuffer();
    u8 slot = buf->localSlot;
    char name[MAX_PLAYER_NAME] = {};
    memcpy(name, buf->localPlayerName, sizeof(buf->localPlayerName));
    initCommBuffer();
    buf->localSlot = slot;
    memcpy(buf->localPlayerName, name, sizeof(buf->localPlayerName));
    publishMailboxAnchor();
}

} // namespace smso
