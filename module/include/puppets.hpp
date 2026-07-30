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
/// Arm hotel mission override for the next hotel stageInit (death reload / soft remount).
void armHotelMissionEpisodeSync(u8 missionEpisode);
void applyHotelWarpMissionOverride(TMarDirector *director);
void normalizeSirenaSecretMissionEpisode(TMarDirector *director);
/// Remap hotel/casino next-scene load ids before moveStage (natural doors + warps).
void normalizeSirenaNextSceneForLoad();
/// Remap Pinna beach→park 0xFF doors to the correct pinnaParco archive (not raw beach ep).
void normalizePinnaParkNextSceneForLoad();
/// Remap Noki bay→mareUndersea 0xFF doors to load ep0 while keeping bay mission (Ep4/Ep8).
void normalizeMareUnderseaNextSceneForLoad();
void syncHotelWarpMissionEpisode(TMarDirector *director);
/// Hotel Ep7 (load delfino3 / mission 6): proximity chase BGM crossfade.
void updateHotelShadowMarioBgm(TMarDirector *director);
void applyPendingWarpPoint(TMarDirector *director);
/// Restore spawn control after a same-stage reload without TMarDirector::setMario
/// (setMario re-enters waitingStart/demo and softlocks Hide & Seek death recovery).
void respawnLocalMarioAtStageSpawn(TMarDirector *director, TMario *mario);
/// Clear BSE mIsDisableInput (+ stuck wipe-warp) and re-arm retail pad read.
/// Setting mReadInput alone is not enough — BSE collisionContext / processWarpCallback
/// can re-disable every Mario perform; callers that race BSE must re-assert after it.
void restoreLocalMarioControl(TMarDirector *director, TMario *mario);
/// True while MarDirector / Mario / demo camera are still in a stage-entry intro.
bool isStageEntryDemoActive(const TMarDirector *director, const TMario *mario);
/// True when intro is done and local pad/BSE input is actually enabled.
bool isLocalMarioControlPlayable(const TMarDirector *director, const TMario *mario);
/// End stage-entry intro (demo camera + entrance demo + waitingStart) without setMario.
/// Returns true when the stage is playable after the attempt.
bool forceSkipStageEntryDemo(TMarDirector *director, TMario *mario);
void reloadLocalStage(TMarDirector *director, u8 areaId, u8 episodeId);
/// Same-stage reload with distinct archive load id vs mission episode (Sirena hotel).
void reloadLocalStage(TMarDirector *director, u8 areaId, u8 loadEpisode, u8 missionEpisode);
void updatePuppets(TMarDirector *director);
void clearPuppets();

} // namespace smso
