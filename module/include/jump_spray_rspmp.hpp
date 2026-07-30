#pragma once

#include <Dolphin/types.h>

class TMario;

namespace smso {

/// Register ma_rspmp once (BetterSMS anim table). Safe to call repeatedly.
void ensureJumpSprayRspmpAnimRegistered();

/// Sonic / Shadow: while spraying during ma_jump, switch body BCK to ma_rspmp.
void updateJumpSprayRspmpAnim(TMario *mario);

} // namespace smso
