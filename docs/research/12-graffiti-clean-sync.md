# Graffiti / Pollution Clean Sync

> **STATUS: PERMANENTLY DISABLED (2026-07-19)**
>
> Durable `GraffitiCleaned` cell spray flooded long co-op sessions (tens of thousands of
> events, mailbox queue full, progress starve). Module APIs are no-ops; server rejects
> the event type; it is not durable and not in authority snapshots. Local spray still
> works via retail `TPollutionManager::clean` (hook removed). Do not re-enable without a
> non-durable design.
>
> Historical design notes below are retained for reference only.

## How SMS graffiti works

Surface goop/graffiti is **not** a FlagManager bit. It lives in `TPollutionManager` (`gpPollution` @ NTSC-U `0x8040DED0`), which owns one or more `TPollutionLayer` canvases.

- Layers keep runtime bitmaps (`mPollutionBmp` / `mPollutionMap`) stamped with pollute/clean textures (`/common/map/pollute.bti`, `clean.bti`).
- FLUDD water particles hit ground/wall → `TModelWaterManager::splashGround/Wall` → `TPollutionManager::clean(x,y,z,size)`.
- `clean` (NTSC-U `0x8019DDB4`) skips a Bianco deep-water case (`map==1 && y < -10`) then calls `stamp(0, …)` with the clean brush.
- `TPollutionManager::stamp` loops **all** layers and calls each layer's `stamp` (which gates via `isInAreaSize`).
- `TPollutionManager::stampGround` only stamps layers with `planeType == 0` (ground). **Wall layers never receive stampGround.**
- Wall layers (`TPollutionLayerWallPlusX/Z`, etc.) use different tex axes (`getTexPosS/T` on Y/Z or Y/X) than ground.
- Discrete `TPollutionObj` subregions track cleaned degree for buildings / MapEvent sinks; full bitmap sync is too large for multiplayer.
- When a graffiti patch finishes, layer `cleaned()` can call `appearItem` (blue coin). Collection ownership is separate (`WE_BLUE_COIN_COLLECTED`).

Splash sizes (doldecomp `ModelWaterManager`):

| Hit | Radius |
|-----|--------|
| `splashGround` | `mCleanSize * 10` (typical ~40–120) |
| `splashWall` | `mCleanSize * 32` (often **~400–576**) |

Sources: [doldecomp PollutionManager.hpp](https://github.com/doldecomp/sms/blob/main/include/Map/PollutionManager.hpp), [PollutionManager.cpp](https://raw.githubusercontent.com/doldecomp/sms/master/src/Map/PollutionManager.cpp), [PollutionLayer.cpp](https://raw.githubusercontent.com/doldecomp/sms/master/src/Map/PollutionLayer.cpp), [TCRF unused pollution maps](https://tcrf.net/Super_Mario_Sunshine/Unused_Pollution_Maps).

Monte NPC rescue (`monte_clean_sync`) already calls `clean__17TPollutionManagerFffff` to clear goop under rescued Piantas — that path is separate mission sync (`WE_NPC_CLEANED = 17`).

## Sync design

Mirror red-coin / `NpcCleaned` durable events — **not** continuous bitmap replication.

| Piece | Behavior |
|-------|----------|
| Detect | `SMS_PATCH_B` trampoline on `clean@0x8019DDB4`. After retail stamp, publish only when the **local** Mario is spraying (`mIsEmitWater` / `mIsFluddEmitting`) and not applying a remote event. |
| Live assist | Viewer: remote droplet emit calls `notifyRemoteSprayEmit` → pullback raycast → `retailClean` (ground ~100 / wall ~480 + Y satellites). Backup uses stored emit ray / body aim. `sApplyingRemote` — **never publishes**. Primary live clear path. |
| Event | `WE_GRAFFITI_CLEANED = 18` / `WorldEventType.GraffitiCleaned` |
| Payload | `courseId`+`episodeId` (plaza → **255**); `payload0` = radius quantized `/8` (clamped 96–128); `payload1` = **spray XYZ** (cell-center/clamp fallback if pack OOR); `payload2` = **3D cell** pack for server dedupe |
| Dedupe | Grow-only **32u XYZ** grid cells; max **384** cells per course/episode (plaza hub episode 255) |
| Authority | `GraffitiCleanAuthority` — reject duplicates / over-cap; plaza coalesced to `PlazaHubEpisode`; included in join / ~45s / stage-enter `WorldStateReplay` |
| Apply | Remote **`stamp(0,…)` / retailClean (all planes)** with `sApplyingRemote` (no echo). Radius clamped 96–128 (wall up to **384** + dense **Y±48…±192** satellites). Pending stamps only for `!gpPollution` deferrals — **one-shot** reapply after settle, then marked inactive. Duplicate cell events skip re-stamp. |
| Episode | Apply when **course matches** and episodes are equivalent: exact, **plaza any** (course 1, incl. hub 255), or **casino** catalog↔mission (course 14). Never consume-and-skip on equivalent plaza/casino episodes. |
| Gates | `BF_SYNC_EVENT \| BF_SYNC_MISSION`; episode-deferred in launcher with the same equivalence rules |
| Stage empty | Occupancy reset clears graffiti authority (plaza hub only when **all** plaza episodes empty) |
| Outbound queue | Cap **256**; prefer evict ephemeral over dropping `WE_GRAFFITI_CLEANED`; **never** drop shine/blue/story/trigger/secret/red/npc-clean to make room for graffiti (a dropped story publish is permanently lost from session authority); only drop a new graffiti cell as last resort if the queue is entirely graffiti / progress-protected. Bridge drains up to 9 local events/poll |

### payload2 3D cell packing

```
bits  0–9 : cellX  (signed 10-bit two's complement)
bits 10–19: cellY  (signed 10-bit)
bits 20–29: cellZ  (signed 10-bit)
bit  30   : valid marker (always set when packed)
bit  31   : unused (0)
```

Signed 10-bit range `[-512, 511]` → world ±16k at 32u — enough for SMS stages. Bit 30 distinguishes a real pack from `payload2 == 0` (derive cell from `payload1`).

Bandwidth: one TCP event per newly cleaned **32u XYZ** cell (not per water particle). Concurrent sprayers converge via server cell dedupe. Vertical wall spray publishes many Y cells so remotes clear like local.

## Root-cause fixes (2026-07)

### 0. Outbound queue drops under spray spam (CRITICAL — 2026-07-18)

**Symptom:** dolphin.log shows only ~7 `graffiti apply cell=(…)` for a full plaza dock red M spray (one vertical strip). Older XZ-only sessions had hundreds of applies. Module reports `cell=32 max=384 3D` — cells exist, events never leave the cleaner.

**Root cause:** `kLocalWorldEventQueueCap = 32`. Wall spray enters many new 32u XYZ cells faster than the bridge drains the single `localPending` slot (one event per poll handshake). `publishLocalWorldEvent` **silently dropped** when full → most `WE_GRAFFITI_CLEANED` never reached the server/viewer.

**Compounding:** Remote FLUDD is visual-only (`mIsEmitWater=false`, manual `emitRemoteWaterRequest` with retail `mFlag=0x40`). Sparse/misaimed remote droplets may miss `splashWall` → `clean()`. Durable stamps were supposed to compensate but queue drops left remotes with almost nothing.

**Fix:**

- Raise outbound queue cap to **256**.
- When full: **evict oldest ephemeral** (NPC react / fruit / hip-drop) first; never drop graffiti in favor of ephemeral; only drop a new graffiti cell as last resort if the queue is entirely graffiti.
- Flush-on-enqueue + loop flush while `localPending` is free.
- Bridge: after clear, re-read `localPending` up to **8** more times per poll (`DrainLocalWorldEventBacklog`) so a mid-frame module flush can drain faster than 1/tick.

### 0b. Remote spray vs local clean asymmetry

| Path | Mechanism |
|------|-----------|
| **Local spray** | `ModelWaterManager::move` → wall hit → `splashWall` → `gpPollution->clean(..., mCleanSize*32)` every hit |
| **Remote visual** | `emitRemoteWaterRequest` → same manager particles; **should** splashWall/Ground → clean on the viewer. Cull radius expanded so distant remotes are not stripped before hit checks. Retail `mFlag=0x40` kept (WaterGun also sets it). |
| **Durable stamps** | `WE_GRAFFITI_CLEANED` — late-join / catch-up / sparse-droplet compensation. Not the only live clear path. |

Live progressive clear = remote particle splash **and/or** dense stamps. Blue coin still needs enough local `appearItem` coverage on each client.

### 1. Goop stretch / vertex explosion (HIGH)

**Symptom:** After graffiti sync, pollution meshes stretch into long spikes into the sky (Sirena electric/teal zigzag, some Delfino Plaza states).

**Root cause:** `reconcilePendingApplies` re-called `clean`/`stamp` for **every pending stamp every frame after settle, forever**. Nothing cleared `active`. Repeated `pushStampTask` entries corrupt pollution layer geometry. Compounded by applying wall splash radii (~400–576 from dolphin.log) via `stamp()` (all planes) at cell centers.

**Fix:**

- Mark pending stamps `active = false` immediately after one successful settle apply.
- Live applies (when `gpPollution` exists) never enter the pending list.
- Cap sync radius to **96–128** (drop wall `*32` sizes).
- Keep plaza deep-water skip (`map==1 && y < -10`).

### 2. "M" graffiti incomplete sync / cell-center miss (MEDIUM)

**Symptom:** Spraying Mario's M does not fully clear for remotes (partial / missed edges).

**Root cause (first pass):** Publish snapped to **cell center** and remotes stamped only there. First spray point in a large cell can be far from the letter edge.

**Fix:**

- Publish/apply at **actual spray XYZ** (`payload1`); cell remains dedupe-only.
- Clamp apply radius to min **96** / max **128** so one stamp per cell covers typical M segments.
- Still goes through layer `stamp` → `cleaned()` → `appearItem` for local blue-coin spawn; collect ownership stays `WE_BLUE_COIN_COLLECTED`.

### 3. Wall graffiti never cleans for remotes (CRITICAL — 2026-07-18)

**Symptom:** Ground goop cleans for remotes, but wall M / wall graffiti barely moves or looks like it never starts. dolphin.log shows many `graffiti apply … size=128 pos=(…,48,…)` — events arrive.

**Root cause:** After the stretch fix, remote apply used `stampGround__17TPollutionManagerFUsffff`, which **only** stamps `planeType == 0` ground layers. Wall M graffiti lives on `TPollutionLayerWallPlusX` / `PlusZ` (plane types 2–5). Remotes never stamped walls.

Separately, grow-only **128u** cells meant one remote stamp per large XZ cell while local `clean()` keeps hitting every particle every frame — walls especially looked "not starting."

**Fix:**

- Remote apply uses **full retail** `stamp(0,…)` / `retailClean` so every layer that passes `isInAreaSize` receives the clean brush (walls + ground). Radius stays clamped 96–128 so wall tex-axis stamps cannot revive the stretch bug.
- Live spray path uses **32u** cells and raised max cells so progressive spray fills in densely.
- Duplicate apply of the same cell (live + durable mailbox ~7ms apart) is skipped after the first remember.

### 4. Wall M XZ-only collapse (CRITICAL — 2026-07-18, screenshots)

**Symptom:** Client A clears plaza dock red M + blue coin spawns; Client B still sees the full red M. dolphin.log shows newer module (`cell=32 max=…`) and wall-height applies (`y=288`) — events arrive but M remains for remote.

**Root cause:** Dedupe cells were **XZ only** (`cellCoord(x)`, `cellCoord(z)`). Wall M spray moves primarily in **Y** at nearly the same XZ. The entire letter collapsed to **one grow-only cell** → one remote stamp at the first-hit Y → rest of the M stayed. Local kept cleaning every particle → full clear + `appearItem`. Remote stuck with red M.

**Fix:**

- Cell key is **(cellX, cellY, cellZ)** at 32u; `payload2` packs 10\|10\|10 + valid bit.
- Max cells raised to **384** (wall M uses many Y slices).
- Progressive spray up an M publishes many Y cells → remotes clear like local and can reach `appearItem` / blue-coin spawn via enough bitmap coverage.

### 5. Episode byte-mismatch consume-and-skip (CRITICAL — Bugbot)

**Symptom:** Events reach the module but `applyGraffitiCleanWorldEvent` returns true without stamping when `episodeId` is not byte-equal to local `mEpisodeID`.

**Root cause:** Plaza hub episodes diverge via `decideNextScenario` without soft-reload. Casino already had `sameCasinoEpisode` / `SirenaCasinoMapping.EpisodesEquivalent` for other sync — graffiti did **not**. Remotes on the same physical stage with different load/mission episode IDs silently skipped all stamps while the cleaner cleared + `appearItem` locally.

**Fix:**

- Module apply when **course matches** and episodes are equivalent: exact **or** plaza (course 1, any episode) **or** casino (course 14, catalog↔mission).
- Launcher live-apply defer and pending flush use the same graffiti episode equivalence.
- Stage trackers do not clear on plaza/casino equivalent episode drift alone.

### 6. Remote FLUDD water-droplet stream (HIGH/MEDIUM)

**Symptom:** Remote Mario FLUDD droplets look throttled / sparse vs local continuous stream (not 1:1), especially mid-plaza distance or C-up aiming.

**Root causes:**

1. While host had `VFX_Y_CAM`, snapshot packing preferred upper-BCK in `snap.water`, and the apply path skipped updating `syncedSprayPressure` under Y-cam → `nozzle->_378` stayed 0 → `visualEmitNozzleDeform` early-out.
2. 60 Hz FLUDD promotion was distance-gated (~3400uu); farther remotes stayed at 30/15 Hz → half/quarter droplet accumulation.
3. Viewer pressure ascent lerp (`*0.65`) lagged pump-up vs local live `_378`.
4. `emitRemoteWaterRequest` clamped `mNum` to 48 (local does not).
5. Yoshi juice still used the 30 Hz `shouldEmitRemoteSprayThisFrame` gate.

**Fix:**

- Pack/apply spray pressure whenever `VFX_WATER_SPRAY` is set, independent of Y-cam (Y-cam pitch still rides pingMs / VFX aux).
- Force **60 Hz** visual interval for **any** spraying remote (no distance gate); keep updates alive during offscreen grace while spraying.
- Apply decoded spray pressure immediately (no ascent smoothing).
- Soft-cap remote `mNum` at 12 (pathological `_37C` spikes only); Yoshi juice emits every visual frame like FLUDD droplets.
- **2026-07-18 (LOD-exempt spray):** Forcing body `visualUpdateThisFrame` while spraying still left droplets coupled to renderVisible / stagger. Dedicated spray tick now runs bindRemoteFludd + ModelWater emit every game frame whenever `VFX_WATER_SPRAY`/`VFX_FLUDD_EMPTY` and the body exists — body anim stays on distance LOD (1/2/4). Offscreen spraying remotes still refresh root transform for emit mtx. Juice tint heal unchanged.

### 6b. Remote FLUDD muddy ribbon / juice tint + LOD (CRITICAL — 2026-07-18)

**Symptom:** Remote FLUDD spray draws as a thick opaque reddish-brown ribbon (not translucent blue droplets). Local WATER HUD tank also turns red.

**Root causes:**

1. `mWaterCardType` indexes `waterColor[]` (0=water α=0x14; 1–3=juice α=0x6E opaque). The juice perform/emit paths **restored** the prior juice card after draw/emit, immediately re-arming the muddy ribbon / red HUD.
2. Spray emit was gated by body `visualUpdateThisFrame`. On temporal-LOD skip frames emit matrices went stale and mtx-bound JPA mist stretched into a ribbon.

**Fix:**

- Resolve draw tint and **leave** it (do not restore prior juice). FLUDD `emitRemoteWaterRequest` forces card `0` and leaves it.
- Heal HUD tint each tick when not on local Yoshi.
- Soft-cap remote `mNum` at 12.
- Decouple FLUDD spray from body LOD: dedicated 60 Hz spray tick (see §6) — do not force body interval=1.
- Keep juice card heal (never restore prior juice after FLUDD emit/draw).

### 7. Catch-up stamp strength (2026-07-18)

**Fix:**

- `reserved` bit0 = wall splash (original clean size ≥ 200); bit1 = finishing (legacy relay only — **no mid-stage re-stamp**).
- Wall apply: radius up to **160** + modest **Y-axis satellites** (±64, ±128) once per event; satellites use ground clamp (≤128).
- Periodic OSReport: `graffiti diag publish=… apply=… assist=… try=… missHit=… packFb=…` ~3s during spray.

### 7b. Remaining goop stretch / sky-spike (CRITICAL — 2026-07-18)

**Symptom:** Some goop still stretches into sky spikes after clean sync (plaza walls, Sirena, etc.).

**Root cause:** Viewer spray **assist** called `retailClean` every frame at wall size **480** with multiple Y satellites while the remote kept spraying the same face. Durable one-shot fixes were correct; perpetual assist re-stamp revived `pushStampTask` vertex corruption. Finishing reserved also re-applied known cells.

**Fix:**

- Assist wall/ground sizes capped to **128 / 96**; one-shot per 32u XYZ cell (`sAssistApplied`).
- Wall sync max **160**; Y satellites reduced to ±64/±128 at ≤128 radius.
- Finishing no longer re-stamps an already-applied cell.
- Pending settle reapply remains one-shot (`active=false` after apply).

### 8. Pack-range publish stall (CRITICAL — 2026-07-18)

**Symptom:** dolphin.log `publish=205 … cellsLocal=351` — cells keep growing, publish frozen. Viewer apply freezes at the same count. Queue drops = 0.

**Root cause:** `packCollectibleWorldPos` (scale 16, bias 256) only encodes world ≈ **−4096…12272**. When any axis is out of range, pack returns 0 → `publishLocalGraffitiClean` returns early **after** `rememberStamp` already recorded the cell → `cellsLocal` advances without a wire event.

**Fix:** `packGraffitiWorldPos` falls back to cell-center, then axis-clamped coords, so publish keeps pace with cells. Diag reports `packFb=`.

### 9. Live remote spray does not clear for viewers (CRITICAL — 2026-07-18)

**Symptom:** User sees **no cleaning at all** when remotes spray. Applies that do arrive are mid-height only (`cellY=9`, `pos Y≈288`) with size≤192 while local `splashWall` uses ~400–576.

**Root causes:**

1. Remote FLUDD is visual `emitRequest` (`mIsEmitWater=false`) — particle splashWall→clean is unreliable.
2. Sync stamps clamp to 192 and often land at mid-height only → little/no visible wall clear.
3. Periodic finishing (`flags=0x03` every 45 frames) burned bandwidth without helping vertical coverage.

**Fix:**

- **Viewer spray clean assist (primary live path):** remote droplet emit notifies with emit origin/dir → pullback raycast → `retailClean` (ground **100** / wall **480** + Y sats). Uses `sApplyingRemote` — **does not publish**.
- Wall sync stamps: dense Y±48…±192 satellites; wall radius up to **384**.
- Removed periodic finishing spam; wall bit on first cell publish is enough with assist + Y satellites.

### 10. Plaza episode authority split (2026-07-18)

**Symptom:** Stamps split across plaza episode buckets as `decideNextScenario` advances `mEpisodeID`.

**Fix:** Module publishes plaza under episode **255**; `GraffitiCleanAuthority` coalesces course 1 → `PlazaHubEpisode` on accept + ResetStage; GameServer only resets plaza graffiti hub when **all** plaza occupancy is empty.

### 11. Plaza wall X: assist≈0 / stamps too weak (CRITICAL — 2026-07-18 video)

**Symptom (OBS replay + dolphin.log):** Remote Mario sprays the Delfino Plaza dock pedestal red **X**. Viewer sees droplets + yellow impact sparks on the X, but the graffiti never fades. Diag alternates:

- Viewer: `publish≈4 apply=223+ assist=0`
- Sprayer: `publish=203–289 apply≈24 assist=5` (frozen)

**Root causes:**

1. **Assist never fired on the viewer.** Prior assist only probed `getEmitMtx` later in `updateGraffitiCleanSync`, with no ray pullback. Close-range plaza wall spray starts the ray *inside* the monument face → `intersectLine` miss. `|ny|<0.55` wall test also rejected the slightly slanted pedestal. `isMarioThrough` rejected some pollution faces. Result: `assist=0` while VFX clearly hit the X.
2. **Sync stamps arrived but were too weak for walls.** Applies clustered at `Y≈288` (`cellY=9`) with size≤**192** vs retail `splashWall` ~**400–576**. Y±64/±128 satellites were not enough vertical coverage for the letter.

**Fix:**

- Remote droplet emit calls `notifyRemoteSprayEmit` with the **same emit ray** as visible spray → immediate pullback raycast (`-dir * 250`) → `retailClean` wall **128** / ground **96**, **one-shot per cell** + modest Y sats. Never publishes.
- Backup: stored emit ray between emit ticks; body yaw/gun-aim if mtx missing.
- Looser wall normal (`|ny|<0.85`); do not reject `isMarioThrough`.
- Wall sync radius **160** (one-shot only); Y satellites ±64/±128 at ≤128.
- Diag: `assist= try= missHit=` — viewer `assist` must rise while remotes spray (unique cells, not every frame).

**Verify boot:** `graffiti-clean sync ready (… wall 3D emitAssist+packFb)`  
**Verify spray:** viewer `assist` climbs as new cells are hit; plaza wall X fades without sky spikes.

## Edge cases

| Case | Handling |
|------|----------|
| Late join / reconnect | Authority snapshot replays accepted cells → module stamps at packed XYZ once |
| Stage / episode change | Module clears local trackers on real stage enter; plaza/casino equivalent episode drift does not |
| Partial cleans | Mid-spray syncs each newly entered 32u **XYZ** cell; remotes stamp clamped radius at first-spray XYZ in that cell |
| Multi-player concurrent spray | Both publish; first accepted cell wins; later duplicates rejected |
| Remote FLUDD VFX droplets | May call `clean` via particles; spray **assist** also cleans without publish |
| Monte force-clean | Goes through same trampoline; only publishes if local Mario is spraying (Monte durable path remains `WE_NPC_CLEANED`). Do **not** seed Monte ownership from already-clear snapshot — that falsely wiped goop under sunk Piantas (see `07-red-coin-episodes.md`). |
| Ghost graffiti after warp | Stage-enter settle re-applies **deferred** stamps once (marked inactive after) |
| Wall graffiti / M / paired / electric / plaza gate | Emit-tied assist (retail wall size + pullback) + sync `stamp(0,…)` size≤384 with dense Y satellites; 3D cells for catch-up |
| Duplicate mailbox apply | Second event for same XYZ cell is consumed without re-stamping |
| Plaza episode alias | Course 1: all episodes equivalent; authority + publish use hub episode **255** |
| Casino episode alias | Course 14: 0↔3, 1↔4 |
| Pack out-of-range XYZ | Publish falls back to cell-center / clamped pack so `publish≈cellsLocal` |

## Limitations / next steps

- Cap of 384 cells/stage: very large free-clean areas may still stop syncing new cells (OSReport reject on server).
- Quantized radius + spray-point stamp is an approximation; pixel-perfect goop edges will not match.
- Spreading / revival pollution (`stamp(1,…)`) is not synced (goop does not re-spread from remotes).
- Blue-coin **spawn** from fully cleaned graffiti still depends on each client's local `appearItem` / counters after enough stamps; ownership of collected blues remains `WE_BLUE_COIN_COLLECTED`.
- Outbound mailbox is still one `localPending` slot; backlog drain relies on queue depth + bridge multi-read. Do not lower queue cap below ~128.
- Spray assist is emit-tied (same ray as droplets) with pullback; thin graffiti edges may still need sync stamps / late-join replay.

## Lesson: pack range vs cellsLocal

`rememberStamp` before a fallible pack → silent publish skip freezes `publish` while `cellsLocal` grows. Always pack with a fallback (cell center / clamp) or only remember after a successful enqueue.

## Lesson: remote spray ≠ local clean

Visual remote FLUDD cannot be trusted for progressive graffiti clear. Viewer-side assist must use the **same emit ray as droplet VFX** (not a later stale mtx probe), pull the ray origin back out of close-range wall geometry, and use splashWall-sized brushes. Durable stamps are catch-up / late-join and need near-retail wall radius.

## Lesson: stampGround vs stamp

| API | Layers hit | Use |
|-----|------------|-----|
| `stampGround(type,x,y,z,size)` | `planeType == 0` only | Ground-only helpers; **wrong for multiplayer clean sync** |
| `stamp(type,x,y,z,size)` | All layers; each calls `isInAreaSize` | Retail `clean()` path; **required for wall graffiti** |

Never use `stampGround` for remote graffiti apply. Keep radius clamped when using `stamp()` so huge wall splash sizes cannot corrupt mesh verts.

## Lesson: XZ-only cells vs wall graffiti

Wall M / paired / vertical graffiti spray along **Y**. An XZ-only grow-only grid collapses the whole letter to one cell. Always include quantized Y in the dedupe key.

## Lesson: outbound queue under spray

Graffiti is high-rate durable traffic. A tiny ring (32) + single mailbox slot + drop-on-full guarantees remotes see a handful of stamps. Prefer a deep queue, never starve graffiti for ephemeral events, and drain localPending as fast as the bridge can clear it.

## Files

- Module: `graffiti_clean_sync.{hpp,cpp}`, `remote_water_sync.cpp`, `remote_actor.cpp` (spray LOD + pressure), `puppets.cpp` (pressure pack), `world_sync.cpp`, `comm_buffer.hpp`, `module.cpp`
- Net/Server: `ProtocolConstants.cs`, `GraffitiCleanAuthority.cs`, `WorldEventRelay.cs`, `GameServer.cs`
- Launcher/Bridge: `SessionCoordinator.cs`, `BridgeWorker.cs` (multi-drain), `DolphinBridge.cs` (`TryReadLocalPendingWorldEvent`)
- Tests: `GraffitiCleanAuthorityTests.cs`, `CollectibleAuthorityTests.cs`
