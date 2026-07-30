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
/// True when the local player is assigned Seeker in Hide &amp; Seek mode.
bool isLocalHideSeekSeeker();

/// During tag/death recovery, rewrite plaza hub return (1/0xFF) and other mid-tag
/// stage leaves to the pinned death/tag stage. Returns true if next-scene was changed.
/// Called from performSmsoMoveStage so every moveStage path is covered.
bool redirectHideSeekDeathStageLeave(TMarDirector *director);

void setHideSeekAllowStageTransition(bool allow);
void setHideSeekAllowDeathStageReload(bool allow);

bool isHideSeekAuthorizedStageTransition();

/// Drop sticky death-recovery so a launcher / warp-all cannot be rewritten back onto
/// the death stage after Stop Tag / RoundComplete (dolphin.log: tag=0 death=1).
void clearHideSeekDeathForLauncherWarp();

} // namespace smso
