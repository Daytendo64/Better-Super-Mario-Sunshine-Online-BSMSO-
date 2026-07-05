#pragma once

#include <Dolphin/types.h>

class TMarDirector;

namespace smso {

void initStageGuard();

bool isSmsoAuthorizedStageTransition();
bool isNonGameplayStage(u8 areaId);
void authorizeLauncherStageMove();
void clearAuthorizedStageMovePending();
void performSmsoMoveStage(TMarDirector *director);

} // namespace smso
