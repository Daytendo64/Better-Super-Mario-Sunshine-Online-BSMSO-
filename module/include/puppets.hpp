#pragma once

#include <Dolphin/types.h>

class TMarDirector;
class TMario;

namespace smso {

void initPuppets();
void exportLocalPlayer(TMario *mario, TMarDirector *director);
// Retail-accurate FLUDD pack visibility on Mario's model (not Yoshi's back).
bool shouldShowFluddPackOnMario(const TMario *mario);
void consumeWarpIntent();
void applyHotelWarpMissionOverride(TMarDirector *director);
void syncHotelWarpMissionEpisode(TMarDirector *director);
void applyPendingWarpPoint(TMarDirector *director);
void respawnLocalMarioAtStageSpawn(TMarDirector *director, TMario *mario);
void reloadLocalStage(TMarDirector *director, u8 areaId, u8 episodeId);
void updatePuppets(TMarDirector *director);
void clearPuppets();

} // namespace smso
