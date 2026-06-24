#pragma once

#include <Dolphin/types.h>

class TMarDirector;
class TMario;

namespace smso {

void applyHideSeekPlayerCosmetics(TMario *mario, bool isSeeker, bool isRemote = false);
void playHideSeekSeekerCosmeticVfx(TMario *mario);

bool isHideSeekSeekerSlot(u8 slot);

void initHideSeek();
void onHideSeekStageExit();
void updateHideSeek(TMarDirector *director);
void clearHideSeek();

bool isHideSeekActive();
bool isHideSeekTaggedDeathActive();
bool isHideSeekNameTagMode();
bool shouldDrawHideSeekNameTag(u8 remoteSlot);

void setHideSeekAllowStageTransition(bool allow);

} // namespace smso
