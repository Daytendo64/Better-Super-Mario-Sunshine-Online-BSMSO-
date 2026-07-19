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
2. **Bind gesso** — per-remote clone via retail `SMS_MakeMActorFromSDLModelData(sdlModelData, anm, 3)` on the remote actor heap. Stage `mRed/Yellow/GreenGesso` templates are singletons; sharing them across riders crashes. Clones must be **`SDLModel` (0xAC)**, not plain `J3DModel(modelData, 0, 0)` — the latter crashes in `MActor::perform` on other clients.
3. **Color** — `TMapObjBase::initPacketMatColor` with MapObjManager `unkA8/unkB0/unkB8` so red/yellow/green match.
4. **Retry bind** — if templates are not loaded yet (`bindPending`), retry every perform frame.
5. **Water context** — set `mWaterHeight`, `mIsWater` for FX and retail anim helpers.
6. **Per-frame** — speed-based gesso BCK rate, base-matrix copy, `MActor::perform(2)` from `remoteCalcAnim`.
7. **Draw** — keep surf draw flag when `mSurfGesso` is bound so retail `calcView`/`entryModels` call `perform(4/0x200)`. Strip the flag only when surfing with a null gesso (bind pending) to avoid null-deref.
8. **10 players** — one `BlooperSurfSlot` per `RemoteActorSlot` (`MAX_REMOTE_SLOTS == 10`); up to 9 simultaneous remote clones plus the local rider on the stage templates.

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
5. Three players same color — no crash (per-slot SDLModel clones).
6. Stress: as many remotes as practical (up to 9) surfing at once — no remote-client crash; heap bind failures only skip mesh (draw flag stripped when `mSurfGesso` is null).
