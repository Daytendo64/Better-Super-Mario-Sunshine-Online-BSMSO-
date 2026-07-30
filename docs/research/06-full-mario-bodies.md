# Full Mario Bodies (Phase 2b)

## Goal

Render networked players as real Mario models (animation + FLUDD), not only particle proxies.

## Reference implementations

| Project | Approach | Notes |
|---------|----------|-------|
| [SMSCoop](https://github.com/TheAzack9/SMSCoop) | `new TMario()` + duplicate `load()` stream; hook `TViewObjPtrListT` | Local split-screen; patches Mario `perform` vtable |
| SMSO (default build) | `TMario` + `initValues` / `initModel` + view-list + perform-list registration | Display-only via `TMario` perform hook |
| SMSO (`-ParticleOnly`) | No full remote bodies | Fallback if bodies crash on a stage |

## SMSO architecture

1. Launcher interpolates snapshots (`RemoteInterpolation.cs`) → comm buffer `remoteSnapshots[]`
2. `updateRemoteActors()` spawns/updates `TMario` bodies (default build)
3. `updateRemoteMarioVisuals()` anchors 2D name tags to active bodies
4. `drawRemoteMarioOverlays()` draws username labels in 2D

## Safe constraints (from BSE research)

- Do **not** patch Mario draw or swap `gpMarioPos`
- Do **not** share writable `J3DModelData` between local and remote actors
- Do **not** inject into `mPerformListGXPost`
- Register remote bodies on the stage view-object list so the engine calls `perform()` / draw

## Build

```powershell
.\tools\build.ps1                 # full Mario bodies (default)
.\tools\build.ps1 -ParticleOnly   # particle proxies only
.\tools\install-module.ps1
```

## Known risks

- Remote bodies skip movement `perform` flags; positions are driven from network snapshots each frame
- Spawn uses game `initModel` / `initValues` — may fail on title screen or during load
- Body pool targets **9 remotes** (`MAX_PLAYERS - 1`) with staggered load-time prewarm on the expanded MEM1 body heap (~7.375 MiB). Fallback 7.5 MiB arena (~5.25 MiB body) may soft-complete below 9; remaining remotes lazy-spawn when RAM frees
- Unique custom packs soft-fail to retail when the pack heap is full (full arena admits ~10 typical / ~8 worst-case packs)

## Body-heap budget rule (ModBuildId 56)

Mid-stage policy never frees a `TMario` graph, so **every graph built under the
wrong archive is lost for the rest of the stage**. At ~612 KiB per graph the
7.375 MiB body heap holds roughly twelve graphs — enough for one correct body
per remote, but not for a wasted retail body *plus* a real body for nine
remotes. Build 51 raised baseline prewarm to all nine puppets while pack
prefetch was still running, so all nine were built retail; after the two 768 KiB
ping-pong staging arenas only about two remotes could ever be upgraded to their
pack and the rest rendered as retail Mario.

`prewarmRemoteBodyPoolStep` therefore builds a graph only when it can build the
right one:

- an unoccupied slot (no connected snapshot, no announced model id) gets nothing;
- a slot announcing a custom model is skipped and revisited until
  `isMarioModelPackReadyForBodyInit` is true, bounded by `kPrewarmPackWaitFrames`
  (900 frames) before a retail fallback so a missing pack never leaves a player
  bodyless;
- prewarm reopens when the roster grows instead of waiting for the next stage.

`acquirePoolBodyForSlot` follows the same rule: it refuses to hand another
slot's retail spare to a slot whose pack is already resident, so the lazy spawn
path builds the correct body once instead of paying a replacement graph plus a
staging arena. Demoted main-heap graphs record the identity they were built
under (`gMainHeapParkedSpareIds`) and are re-adopted by a later slot that wants
the same model — the only mid-stage way to recover that RAM.
