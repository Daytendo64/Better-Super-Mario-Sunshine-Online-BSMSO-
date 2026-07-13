#pragma once

#include <Dolphin/types.h>

class TMarDirector;
class TMario;
class J2DOrthoGraph;

namespace JDrama {
class TGraphics;
}

namespace smso {

void applyHideSeekPlayerCosmetics(TMario *mario, bool isSeeker, bool isRemote = false);
void playHideSeekSeekerCosmeticVfx(TMario *mario);
void maintainLocalHideSeekSeekerDraw(TMario *mario, JDrama::TGraphics *graphics);

bool isHideSeekSeekerSlot(u8 slot);

// True while the server-authoritative Start Tag hide-grace is active.
bool isHideSeekGraceActive();
// Seekers must not see or hear remote hiders during grace.
bool shouldSuppressRemoteHiderFromSeekerGrace(u8 remoteSlot);

void initHideSeek();
void bootHideSeek();
void onHideSeekStageExit();
void guardHideSeekDeathBeforeWarp(TMarDirector *director);
void updateHideSeek(TMarDirector *director);
void drawHideSeekGrace(const J2DOrthoGraph *graph);
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
