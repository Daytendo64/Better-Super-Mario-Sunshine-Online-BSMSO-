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
| `stageId` / `episodeId` | Host real area + scenario (never tongue progress) |
| `velocity` | tongue tip offset from Mario while tongue active, else Mario speed |
| `vfxFlags` | `VFX_YOSHI_FRUIT_MOUTH` + bits 11–13 fruit encode (`unpackYoshiFruitEnc`) |
| `pingMs` low byte | exact tongue `mProgress` (0–255) while tongue active, else BCK rate×64 |
| `pingMs` high byte | Yoshi BCK frame×8 while riding and not spraying, else spray pressure / aux |
| `VFX_NO_FLUDD` | FLUDD pack hidden on Mario's back |

Fruit actor encodes (`doldecomp` `TYoshiTongue::mActorTypeInMouth`):

| Encode | Actor type |
|--------|------------|
| 1 | `0x40000390` |
| 2 | `0x40000391` |
| … | … |
| 7 | `0x40000396` |

## Tongue sync (`TYoshiTongue`, doldecomp `Tongue.cpp`)

Retail fields replicated on remotes:

- `mState` @ `0x7C` — IDLE / EXTENDING / GRABBED / RETRACTING / …
- `mProgress` @ `0x7E` — extension/retract animation timer (exact value in `pingMs` low byte)
- `mActorTypeInMouth` @ `0xD0` — grabbed fruit (or other actor) type
- `mHeadPos` / `mHeadDir` / `mTipPos` — tip via `velocity` offset; head dir derived from offset

### Safe vs unsafe retail calls on puppet tongues

| Function | Safe on remote? | Notes |
|----------|-----------------|-------|
| `calcAnim(mtx)` | **Yes** | Matrix math from synced head/tip/state |
| `viewCalc()` | **Yes** | Only `mModel`/`mTipModel->viewCalc()` — does **not** scan stage |
| `entry()` | **Yes** | Draws tongue mesh when state ≠ IDLE |
| `movement()` | **No** | Calls `findTarget()`, `canGo()`, mutates stage actors |
| `findTarget()` | **No** | Scans `mCollisions[]` for grabbables |
| `thinkUpper()` / `emitTongue()` | **No** | Local-input tongue emit loop |

After `TYoshi::initInLoadAfter()`, remote puppet tongues are **removed from 敵グループ** so retail collision/movement paths never run on network bodies. The group name must be the retail **Shift-JIS** byte sequence (`\x93\x47\x83\x4F\x83\x8B\x81\x5B\x83\x76`) — a UTF-8 source literal silently fails the name lookup, leaves tongues on the enemy perform list, and crashes inconsistently once **two or more** remotes are mounted.

## Fruit eating

1. **Continuous sync** — snapshot fields above keep tongue pose and mouth actor aligned.
2. **Eat animation** — remotes switch Yoshi BCK to eat anims (10 / 12 / 15) on transition; **never** call retail `doEat()` (mutates stage fruit).
3. **World event `WE_YOSHI_FRUIT_TAKEN` (10)** — published once per fruit grab (payload0 = encode, payload1 = packed tip position, reserved = eater slot). Remotes hide the nearest matching `TMapObjBase` fruit within 450 units.

## Remote apply (`yoshi_sync.cpp` + `remote_actor.cpp`)

1. **Before** `syncRemoteAnimation`: mount puppet via `TYoshi::MOUNTED` + `setBckFromIndex`/`thinkBtp` (no `ride()` — avoids BGM/voice side effects).
2. `initInLoadAfter()` is **never** called on network puppets (see multi-Yoshi invisible fix below). Remotes settle `stageInitDone` after a one-shot tongue enemy-group scrub; riding meshes already exist from `TYoshi::init`.
3. Each frame: sync juice, color (`thinkBtp` matches current BCK), translation, host Yoshi BCK frame (`pingMs >> 8`).
4. **Mounted calc subset** of `TYoshi::calcAnim`: mirror rig + tongue `calcAnim`; staged `thinkUpper` only while spray/tongue needs mouth open (plus one closing frame). LOD-skip body frames still run this Yoshi subset so crowd demotion cannot detach riders.
5. **Draw**: retail `TYoshi::entry` for tev/color + tongue when the mirror+tongue rig is ready; tongue `viewCalc` in view pass. (BindShadow circle requests from entry are acceptable once remotes stop allocating `TMirrorActor` heaps.)
6. While host sprays juice: staged FLUDD nozzle pressure from `pingMs` high byte.
7. Dismount: reset companion to `STATE_EGG`.
8. `syncRemoteAnimAux` uses `unpackYoshiTongueHand` while `onYoshi()`.
9. `syncRemoteAnimation` / `syncRemoteHeadWaist` must **not** decode `pingMs` as Mario BCK rate/head angles while host rides Yoshi.

## Crash fix (2026-06-27)

Remote clients crashed because `syncRemoteAnimation` ran **before** Yoshi mount and fed yoshi-riding `animId` values into retail `TMario::setAnimation` while `onYoshi()` was still false.

Fix: reorder apply so Yoshi mount precedes animation sync; fallback to `ANIMATION_IDLE` if mount is not ready.

## Crash fix (2026-07-24) — multi-mounted remotes

Having **two or more** remotes on real Yoshi crashed some clients inconsistently:

1. **Enemy-group tongue remove used a UTF-8 `敵グループ` literal** while retail name refs are Shift-JIS. `initInLoadAfter` (DOL) still pushed tongues onto the group; our remove never found it. One puppet tongue on retail `movement`/`findTarget` sometimes survived; two+ amplified stage mutation / UAF.
2. **`HoldRemotePublishMaxDuration` could release on the first Active tick** after a long load (timer armed at stage exit, Loading skipped the ceiling check, then MaxDuration fired without Active grace) — remounting Connected remotes into a half-settled stage while mounted Yoshi called `initInLoadAfter`.

Fixes: Shift-JIS enemy-group bytes (same pattern as `kPlayerGroupName`); remove-all duplicate tongue entries; settle `stageInitDone` only after a successful remove; never re-call `initInLoadAfter` on remove retry; MaxDuration only stops Loading from wiping Active grace (never skip grace); draw/`entry` gated on a complete mirror+tongue rig.

## Invisible fix (2026-07-26) — 5+ remotes on Yoshi

**Symptom:** Around five concurrent Yoshi riders, Mario and Yoshi meshes vanished (often noticed first on the host).

**Root cause:** Remote mount called retail `TYoshi::initInLoadAfter()`, which allocates **five `TMirrorActor`s per Yoshi** (body + 2 hands + tongue + tip), each creating a second `J3DModel` and pushing into `鏡シーン`. Five riders ≈ 25 mirror models — stage/mirror heap pressure corrupts or stalls the shared J3D draw path so `entryModels` / Yoshi packets drop for everyone on that client (host has local + all remotes, so it hits the cliff first).

**Fix:**
1. Remotes **never** call `initInLoadAfter` (meshes already from `TYoshi::init`; no mirror reflections for puppets; tongue never joins `敵グループ`).
2. LOD-skip frames still run `calcRemoteYoshiAnim` while mounted so crowd budget demotion cannot detach riders.
3. Juice draw-gate hardened so packed juice cannot land in retail's blink-hide windows.

Sources: doldecomp `Yoshi.cpp`, `Tongue.cpp`, `MirrorActor.cpp`, BSE `stage.cpp`, SMSO `remote_actor.cpp`, `yoshi_sync.cpp`, `world_sync.cpp`.
