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

## Fallback build

Full bodies are enabled by default via `SMSO_REMOTE_ENEMY_MARIO`. Use `.\tools\build.ps1 -ParticleOnly` only as a fallback while debugging body crashes. See `06-full-mario-bodies.md`.

**Avoid:** shared J3DModelData MAP spam, gpMarioPos swap, PerformListGXPost injection.
