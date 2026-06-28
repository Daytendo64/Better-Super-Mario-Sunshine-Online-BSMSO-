#pragma once

#include <Dolphin/types.h>

class TMarDirector;

namespace smso {

void initStageGuard();

bool isSmsoAuthorizedStageTransition();
bool isNonGameplayStage(u8 areaId);
bool shouldAllowMoveStage(TMarDirector *director);
void authorizeLauncherStageMove();
void clearAuthorizedStageMovePending();
void clearBlockedLoadingZoneTransition(TMarDirector *director);
void performSmsoMoveStage(TMarDirector *director);

} // namespace smso
