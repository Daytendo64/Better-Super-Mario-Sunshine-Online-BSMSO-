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
void normalizeSirenaSecretMissionEpisode(TMarDirector *director);
/// Remap hotel/casino next-scene load ids before moveStage (natural doors + warps).
void normalizeSirenaNextSceneForLoad();
/// Remap Pinna beach→park 0xFF doors to the correct pinnaParco archive (not raw beach ep).
void normalizePinnaParkNextSceneForLoad();
/// Remap Noki bay→mareUndersea 0xFF doors to load ep0 while keeping bay mission (Ep4/Ep8).
void normalizeMareUnderseaNextSceneForLoad();
void syncHotelWarpMissionEpisode(TMarDirector *director);
void applyPendingWarpPoint(TMarDirector *director);
void respawnLocalMarioAtStageSpawn(TMarDirector *director, TMario *mario);
void reloadLocalStage(TMarDirector *director, u8 areaId, u8 episodeId);
void updatePuppets(TMarDirector *director);
void clearPuppets();

} // namespace smso
