# Remote Visuals

**Current default build:** full remote Mario bodies from `remote_actor.cpp`, with `remote_mario.cpp` drawing name tags anchored to those bodies.

## Implementation

- **Bodies:** full `TMario` puppets are spawned and updated from launcher-written snapshots
- **Name tags:** `J2DPrint` + `gpSystemFont` in `stageDraw2D`, positioned via world-to-screen using the camera TRS matrix and perspective divide
- **Mid-HUD GX fence (`gx_hud_fence`):** BSE `addDraw2DCallback` injects into `TGCConsole2::perform`. Overlays (nametags, Hide & Seek grace, Connected PostDraw) must `beginOverlay`/`endOverlay` so retail counter panes resume with clean TEV/TexObj **and** an untouched `J2DOrthoGraph` (`mBounds`/`mScissor`/`mOrtho`). Writing logical widescreen rects into `mBounds` without restoring `mOrtho` desyncs `scissorBounds` power / later `setPort` and shears shine / blue / gold digit rows (ModBuildId 44 TEV, 48 scissor/ortho GX, **54 full graf-field restore**). See also shine HUD refresh notes in `06-flags-and-sync.md`.
- **Stage filter:** remotes only render when `stageId` and `episodeId` match the local player
- **Interpolation:** C# `RemoteInterpolation` with ~33 ms render delay writes smoothed snapshots into Dolphin memory

## Snapshot fields used

- Position, velocity, name, stage/episode (filtering)
- `nozzleId`, `water`, and `vfxFlags` drive synced FLUDD state, spray pressure, and water VFX

## FLUDD water material parity

Retail `TModelWaterManager` uses one global `mWaterCardType` (`unk5D5F`) for all droplet draw tints (`waterColor[0]`=water, `[1..3]`=Yoshi juice). Remotes must not leave juice tint armed while anyone sprays FLUDD — that turns streams into opaque muddy ribbons and recolors the local WATER HUD. See `12-graffiti-clean-sync.md` §6b.

## FLUDD ModelWater droplets vs body LOD

Body anim uses distance LOD intervals 1/2/3/4 (60/30/20/15 Hz) plus a nearest-4 crowd budget (ModBuildId 56). ModelWater droplet emit is **fully exempt**: a dedicated per-frame spray tick runs `bindRemoteFludd` + `emitRequest` whenever `VFX_WATER_SPRAY` / dry-pump is set and the remote body exists (near or far, including offscreen root tracking for emit mtx). Do not gate droplet emit on `visualUpdateThisFrame` / stagger / nearby-only heuristics.

## Crowd / on-screen LOD (ModBuildId 56)

Shipped in `remote_actor.cpp` — see full research note [`14-remote-crowd-lod.md`](14-remote-crowd-lod.md).

When many remotes share the near field (e.g. 10 players packed in Delfino Plaza):

- **Distance tiers:** full-rate enter/exit ~1600/2200; mid 30 Hz; far-visible 20 Hz beyond ~4600–5200 (hysteresis); off-screen 15 Hz.
- **Crowd budget:** at most **4** on-screen remotes keep 60 Hz `remoteCalcAnim`; farther near remotes demote to 30 Hz pose samples (spin-jump exempt). At most **3** remotes cast shadows (within ~3200).
- **Light LOD re-root:** on budgeted skip frames, joint re-root still rebuilds weight envelopes (no rubber-hose) but skips CPU soft-skin `deform` until the next pose sample.
- **Far particles:** continuous slide/swim/blur emits are skipped at far-visible interval (audio + water-enter edges kept).
- Full TMario accessories (FLUDD / cap / hands / Yoshi) remain; this is not a bare J3D puppet path.

## Off-screen draw skip (ModBuildId 56)

Remotes remain registered on `gRemotePerformGroup` while off-screen (nametags, spray emit mtx, appear timers). `TMario_perform_remote` skips `calcView` / Yoshi viewCalc / FLUDD viewCalc / `entryModels` when `isRemoteBodyDrawVisible` is false. On-screen fidelity and network sync are unchanged; this only drops pure draw work for frustum-culled remotes.

## Fallback build

Full bodies are enabled by default via `SMSO_REMOTE_ENEMY_MARIO`. Use `.\tools\build.ps1 -ParticleOnly` only as a fallback while debugging body crashes. See `06-full-mario-bodies.md`.

**Avoid:** shared J3DModelData MAP spam, gpMarioPos swap, PerformListGXPost injection.
