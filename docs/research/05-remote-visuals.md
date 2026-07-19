# Remote Visuals

**Current default build:** full remote Mario bodies from `remote_actor.cpp`, with `remote_mario.cpp` drawing name tags anchored to those bodies.

## Implementation

- **Bodies:** full `TMario` puppets are spawned and updated from launcher-written snapshots
- **Name tags:** `J2DPrint` + `gpSystemFont` in `stageDraw2D`, positioned via world-to-screen using the camera TRS matrix and perspective divide
- **Stage filter:** remotes only render when `stageId` and `episodeId` match the local player
- **Interpolation:** C# `RemoteInterpolation` with ~33 ms render delay writes smoothed snapshots into Dolphin memory

## Snapshot fields used

- Position, velocity, name, stage/episode (filtering)
- `nozzleId`, `water`, and `vfxFlags` drive synced FLUDD state, spray pressure, and water VFX

## FLUDD water material parity

Retail `TModelWaterManager` uses one global `mWaterCardType` (`unk5D5F`) for all droplet draw tints (`waterColor[0]`=water, `[1..3]`=Yoshi juice). Remotes must not leave juice tint armed while anyone sprays FLUDD — that turns streams into opaque muddy ribbons and recolors the local WATER HUD. See `12-graffiti-clean-sync.md` §6b.

## FLUDD ModelWater droplets vs body LOD

Body anim uses distance LOD intervals 1/2/4 (60/30/15 Hz). ModelWater droplet emit is **fully exempt**: a dedicated per-frame spray tick runs `bindRemoteFludd` + `emitRequest` whenever `VFX_WATER_SPRAY` / dry-pump is set and the remote body exists (near or far, including offscreen root tracking for emit mtx). Do not gate droplet emit on `visualUpdateThisFrame` / stagger / nearby-only heuristics.

## Fallback build

Full bodies are enabled by default via `SMSO_REMOTE_ENEMY_MARIO`. Use `.\tools\build.ps1 -ParticleOnly` only as a fallback while debugging body crashes. See `06-full-mario-bodies.md`.

**Avoid:** shared J3DModelData MAP spam, gpMarioPos swap, PerformListGXPost injection.
