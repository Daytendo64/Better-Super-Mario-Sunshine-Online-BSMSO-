#pragma once

#include <Dolphin/types.h>

class TMarDirector;

namespace smso {

void initStageGuard();

bool isSmsoAuthorizedStageTransition();
/// Launcher / warp-all intent only — excludes Hide&Seek death-stage authorization.
bool isLauncherAuthorizedStageMove();
bool isNonGameplayStage(u8 areaId);
void authorizeLauncherStageMove();
void clearAuthorizedStageMovePending();
void performSmsoMoveStage(TMarDirector *director);

} // namespace smso
