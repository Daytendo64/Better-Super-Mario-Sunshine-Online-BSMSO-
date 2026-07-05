# Blooper surf multiplayer sync

Research-backed design for syncing Ricco Harbor (and global BSE) blooper surfing to remote Mario puppets.

## Retail behavior (doldecomp)

| Item | Value |
|------|-------|
| Ride status | `MARIO_STATUS_SURF` (`0x810446`) — status id `0x046` + draw flag `0x10000` |
| Jump-off substate | `0x281089A` — status id `0x09A` + draw flag |
| Mario BCK | `ANIM_RIDE_SHELL` (`0x6D`) |
| Blooper mesh | `TMario::mSurfGesso` / `mSurfGessoID` (0=red, 1=yellow, 2=green) |
| Mesh actor | `MActor` clone of stage `TSurfGesso` templates in `TMapObjManager` |
| Waist lean | `_3DC` pitch + `_3D8` roll via `considerWaist()` / ride-shell callback |
| Surface FX | `TMario::surfingEffect()` — expects plausible `mWaterHeight` |
| Water context | Surf is **not** `STATE_WATERBORN`; it is its own status machine |

BSE enables bloopers on every stage via `generic.cpp` (“Global surfing bloopies”) and loads shared gesso templates into `TMapObjManager`.

## Wire format (no comm version bump)

| Field | While surfing |
|-------|----------------|
| `actionId` / `actionIdHi` | Full `TMario::mState` (includes surf draw flag) |
| `animId` | `0x6D` ride shell (forced on export if host drifted) |
| `water` | `mSurfGessoID & 0x03` (gesso color) |
| `pingMs` high byte | Waist pitch (`_3DC`) |
| `vfxFlags` bits 10–15 | Waist roll (`_3D8`) |

`snap.water` is overloaded elsewhere (tank, spray pressure, Yoshi); blooper surf takes priority only when `isBlooperSurfState(mState)`.

## Remote reconstruction (`blooper_surf_sync.cpp`)

1. **Detect** surfing from snapshot `mState`, not puppet-local collision.
2. **Bind gesso** — per-remote `MActor`+`J3DModel` clone (templates are singletons; sharing crashes with multiple riders).
3. **Retry bind** — if templates are not loaded yet (`bindPending`), retry every perform frame.
4. **Water context** — set `mWaterHeight`, `mIsWater` for FX and retail anim helpers.
5. **Per-frame** — speed-based gesso BCK rate, base-matrix copy, `MActor::perform` from `remoteCalcAnim`.
6. **Draw safety** — strip surf draw flag before `calcView`/`entryModels` (clone is not a real `TSurfGesso`).

## Files

- `module/include/blooper_surf_sync.hpp` — constants + API
- `module/src/blooper_surf_sync.cpp` — export/apply/frame + clone heap
- `module/src/puppets.cpp` — host export
- `module/src/remote_actor.cpp` — puppet perform + particles integration

## Verification

1. Rebuild module (`tools/build.ps1`) and restart Dolphin.
2. Two clients, Ricco Ep 8 or any stage with bloopers.
3. Host mounts red/yellow/green blooper — remotes show matching color mesh, ride-shell pose, lean, spray trail.
4. Jump off (X) — remotes drop mesh within one snapshot.
5. Three players same color — no crash (per-slot clones).
