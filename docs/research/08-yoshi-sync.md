# Yoshi Multiplayer Sync

SMS attaches one `TYoshi` companion per `TMario` (`mYoshi`). World `TEggYoshi` eggs and fruit props remain **local-only**; only per-player mount state is networked.

## Snapshot encoding (64-byte `PlayerSnapshot`, no protocol bump)

Host riding Yoshi: **`VFX_NO_FLUDD`** + current nozzle **`TWaterGun::Yoshi`**.

| Field | On-Yoshi payload |
|-------|------------------|
| `nozzleId` | `(mType & 0xF) << 4 \| Yoshi` |
| `movementState` | `(juiceRatio * 31) << 3 \| upperBodyState` |
| `animId` | Yoshi-riding Mario BCK indices (0xB6..0xC6) — **only valid when `TMario::onYoshi()`** |
| `water` | Host Yoshi BCK index while riding |
| `health` high nibble | `TYoshiTongue` state/progress while riding (low nibble = hand) |
| `pingMs` high byte | Yoshi spray pressure while `VFX_WATER_SPRAY` |
| `VFX_NO_FLUDD` | FLUDD pack hidden on Mario's back |

## Remote apply (`yoshi_sync.cpp` + `remote_water_sync.cpp`)

1. **Before** `syncRemoteAnimation`: mount puppet via `TYoshi::MOUNTED` + `setBckFromIndex`/`thinkBtp` (no `changeAnimation` — avoids `mBodyAnmSound` init).
2. `initInLoadAfter()` at puppet spawn and once per slot on first mount (tongue/mirror rig).
3. Each frame: sync juice, color, translation, host Yoshi BCK; **safe mounted calc** (mirror rig + tongue `calcAnim` for eat/spray matrix).
4. While host sprays: retail `thinkUpper` for mouth BCK only — puppet FLUDD nozzle/pressure is **staged and restored** inside calc (never `mHasFludd`, never `movement()`/`perform()`).
5. **Juice spray VFX** (`emitRemoteYoshiJuiceSpray`): spray cone + model-water droplets from `getRemoteYoshiSprayEmitMtx()`; droplet emits use **scoped** `mWaterCardType` save/restore; draw uses a **ModelWaterManager perform hook** when local Mario is not on Yoshi so juice color never overwrites FLUDD blue / the juice HUD.
6. Dismount: reset companion to `STATE_EGG`.

## Crash fix (2026-06-27)

Remote clients crashed because `syncRemoteAnimation` ran **before** Yoshi mount and fed yoshi-riding `animId` values into retail `TMario::setAnimation` while `onYoshi()` was still false (`MarioDraw.cpp`).

Fix: reorder apply so Yoshi mount precedes animation sync; fallback to `ANIMATION_IDLE` if mount is not ready.

Sources: doldecomp `Yoshi.cpp`, `ModelWaterManager.cpp`, BSE `stage.cpp` (`waterColor[]`), SMSO `remote_actor.cpp`.
