#include "world_sync.hpp"
#include "comm_buffer.hpp"

namespace smso {

void initWorldSync() {}

void processWorldEvents() {
    CommBuffer *buf = getCommBuffer();
    if (!(buf->bridgeFlags & (BF_SYNC_SHINE | BF_SYNC_BLUE_COIN | BF_SYNC_EVENT | BF_SYNC_STORY |
                              BF_SYNC_MISSION | BF_SYNC_SECRET)))
        return;
}

} // namespace smso
