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
- Current body cap is 4 remote bodies; additional remotes are hidden until this cap is raised safely
