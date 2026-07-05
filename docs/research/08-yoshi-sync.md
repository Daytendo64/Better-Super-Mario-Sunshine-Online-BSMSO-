# Yoshi Multiplayer Sync

SMS attaches one `TYoshi` companion per `TMario` (`mYoshi`). World `TEggYoshi` eggs remain **local-only**; per-player mount state and tongue/fruit interactions are networked.

## Snapshot encoding (64-byte `PlayerSnapshot`, no protocol bump)

Host riding Yoshi: **`VFX_NO_FLUDD`** + current nozzle **`TWaterGun::Yoshi`**.

| Field | On-Yoshi payload |
|-------|------------------|
| `nozzleId` | `(mType & 0xF) << 4 \| Yoshi` |
| `movementState` | `(juiceRatio * 31) << 3 \| upperBodyState` |
| `animId` | Yoshi-riding Mario BCK indices (0xB6..0xC6) — **only valid when `TMario::onYoshi()`** |
| `water` | Host Yoshi BCK index while riding |
| `health` | bits 0–1 hand, 2–4 `TYoshiTongue` state, 5–7 coarse `mProgress/8` |
| `stageId` | exact tongue `mProgress` (0–255) while tongue active, else host area id |
| `episodeId` | encoded fruit actor type (1–7) while `VFX_YOSHI_FRUIT_MOUTH`, else host episode |
| `velocity` | tongue tip offset from Mario while tongue active, else Mario speed |
| `vfxFlags` | `VFX_YOSHI_FRUIT_MOUTH` when `episodeId` carries fruit encode |
| `pingMs` high byte | Yoshi spray pressure while `VFX_WATER_SPRAY` |
| `VFX_NO_FLUDD` | FLUDD pack hidden on Mario's back |

Fruit actor encodes (`doldecomp` `TYoshiTongue::mActorTypeInMouth`):

| Encode | Actor type |
|--------|------------|
| 1 | `0x40000390` |
| 2 | `0x40000391` |
| … | … |
| 7 | `0x40000396` |

## Tongue sync (`TYoshiTongue`, doldecomp `Tongue.hpp`)

Retail fields replicated on remotes:

- `mState` @ `0x7C` — IDLE / EXTENDING / GRABBED / RETRACTING / …
- `mProgress` @ `0x7E` — extension/retract animation timer
- `mActorTypeInMouth` @ `0xD0` — grabbed fruit (or other actor) type
- `mTipPos` @ `0xB8` — world-space tongue tip (via `velocity` offset from Mario)

Remote puppets run retail `calcAnim`, `viewCalc`, and `entry` on the tongue object so the mesh tracks the host.

## Fruit eating

1. **Continuous sync** — snapshot fields above keep tongue pose and mouth actor aligned.
2. **Eat animation** — remotes call retail `TYoshi::doEat(actorType)` when host Yoshi BCK transitions into eat anims (10 / 12 / 15).
3. **World event `WE_YOSHI_FRUIT_TAKEN` (10)** — published once per fruit grab (payload0 = encode, payload1 = packed tip position, reserved = eater slot). Remotes hide the nearest matching `TMapObjBase` fruit within 450 units.

## Remote apply (`yoshi_sync.cpp` + `remote_water_sync.cpp`)

1. **Before** `syncRemoteAnimation`: mount puppet via `TYoshi::MOUNTED` + `setBckFromIndex`/`thinkBtp` (no `changeAnimation` — avoids `mBodyAnmSound` init).
2. `initInLoadAfter()` at puppet spawn and once per slot on first mount (tongue/mirror rig).
3. Each frame: sync juice, color, translation, host Yoshi BCK; **safe mounted calc** (mirror rig + tongue `calcAnim` for eat/spray matrix).
4. While host sprays: retail `thinkUpper` for mouth BCK only — puppet FLUDD nozzle/pressure is **staged and restored** inside calc (never `mHasFludd`, never `movement()`/`perform()`).
5. **Juice spray VFX** (`emitRemoteYoshiJuiceSpray`): spray cone + model-water droplets from `getRemoteYoshiSprayEmitMtx()`.
6. Dismount: reset companion to `STATE_EGG`.
7. `syncRemoteAnimAux` uses `unpackYoshiTongueHand` (not FLUDD deploy) while `onYoshi()`.

## Crash fix (2026-06-27)

Remote clients crashed because `syncRemoteAnimation` ran **before** Yoshi mount and fed yoshi-riding `animId` values into retail `TMario::setAnimation` while `onYoshi()` was still false (`MarioDraw.cpp`).

Fix: reorder apply so Yoshi mount precedes animation sync; fallback to `ANIMATION_IDLE` if mount is not ready.

Sources: doldecomp `Yoshi.cpp`, `Tongue.cpp`, BSE `stage.cpp`, SMSO `remote_actor.cpp`, `world_sync.cpp`.
