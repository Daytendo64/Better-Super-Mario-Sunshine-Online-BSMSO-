#pragma once

class TMarDirector;
class TMario;

namespace smso {

void initPuppets();
void exportLocalPlayer(TMario *mario, TMarDirector *director);
// Retail-accurate FLUDD pack visibility on Mario's model (not Yoshi's back).
bool shouldShowFluddPackOnMario(const TMario *mario);
void consumeWarpIntent();
void applyPendingWarpPoint(TMarDirector *director);
void skipEntryDemoIfPending(TMarDirector *director);
void skipCutscenesIfConnected(TMarDirector *director);
void respawnLocalMarioAtStageSpawn(TMarDirector *director, TMario *mario);
void updatePuppets(TMarDirector *director);
void clearPuppets();

} // namespace smso
