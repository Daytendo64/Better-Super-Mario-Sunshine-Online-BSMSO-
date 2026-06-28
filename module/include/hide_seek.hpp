#pragma once

#include <Dolphin/types.h>

class TMarDirector;
class TMario;

namespace JDrama {
class TGraphics;
}

namespace smso {

void applyHideSeekPlayerCosmetics(TMario *mario, bool isSeeker, bool isRemote = false);
void playHideSeekSeekerCosmeticVfx(TMario *mario);
void maintainLocalHideSeekSeekerDraw(TMario *mario, JDrama::TGraphics *graphics);

bool isHideSeekSeekerSlot(u8 slot);

void initHideSeek();
void bootHideSeek();
void onHideSeekStageExit();
void guardHideSeekDeathBeforeWarp(TMarDirector *director);
void updateHideSeek(TMarDirector *director);
void clearHideSeek();

bool isHideSeekActive();
bool isHideSeekTaggedDeathActive();
bool shouldForceHideSeekDeadSnapshot();
bool isHideSeekNameTagMode();
bool shouldDrawHideSeekNameTag(u8 remoteSlot);

void setHideSeekAllowStageTransition(bool allow);
void setHideSeekAllowDeathStageReload(bool allow);

bool isHideSeekAuthorizedStageTransition();

} // namespace smso
