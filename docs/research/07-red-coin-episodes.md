# Red Coin Episodes

SMS red-coin shines use two layouts in vanilla: **switch** missions (ground-pound `TRedCoinSwitch`, coins spawn from `TCoinEmpty`) and **pre-placed** missions (coins already in the map). A third pattern is **mixed / deferred spawn**: only some coins exist at stage settle; the rest appear later from enemies, NPCs, or props (notably plaza **Red Coin Field**).

**Red-coin switch arming is not networked.** Each player presses their own switch locally. There is no hip-drop publish for `TRedCoinSwitch`, no remote switch apply, and no collection-driven mission arming. `Type5Flag.mRedCoinSwitchPressed` (`0x50009`) is also excluded from durable `TriggerFlag` sync so one stage’s press cannot re-arm other missions.

Only **collection** is networked as durable `RedCoinCollected` (course+episode scoped). On switch missions, remotes only see collection progress for coins that have already appeared on their client (after they press the switch themselves).

## Authority model: stable index + 8-bit collected mask

Canonical identity is a **stable index 0–7** derived from a **sorted initialPos cohort**, with **packed initialPos** as a hide fingerprint for deferred spawns.

1. **Settle snapshot** (~90 frames): gather live `TCoinRed` **and** `TCoinEmpty` placeholders, sort by `mInitialPosition`, assign indices 0–N. Switch secrets typically lock all **8 empties** before the switch fires (`final=1`).
2. **Switch cohort**: when `mRedCoinSwitchPressed` rises, do **not** append-only as coins trickle in. Wait until **8 live reds** exist, then lock **one** sorted snapshot (or rebind an empty-based snapshot). Publishing is deferred until the cohort is final.
3. **Enemy-drop / partial stages** (Red Coin Field): settle may find fewer than 8. New reds expand by **position fingerprint** into matching slots or append. If the snapshot is already full of empties/dead junk, **claim** an uncollected placeholder slot. Never FIFO-bind remote bits to “next unclaimed live coin.” Adopt dead reds at most **one per Type6 tick**, and only true `TCoinRed` (never `TCoinEmpty`).
4. **Authority state** = course+episode **8-bit collected mask** + optional packed pos per index. Server `RedCoinAuthority`: `payload1` = mask, `payload2` = packed `initialPos`.
5. **Local collect**: adopt/expand, find dead/taken bound actor; publish `reserved=index`, `payload1=mask`, `payload2=pos`. Never invent “first unset bit.”
6. **Remote apply**: remember mask + pos; `hideRedCoinByStableIndex` and/or **exact pos match** (`kRebindPosEpsilon`) only on first apply. Collect SFX/particles only when a live coin was actually hidden. Pending slots bind only when a live coin’s `initialPos` matches the stored fingerprint.
7. **HUD / switch arming / never call taken()** — unchanged from prior rules. Mission-live requires switch / red-coin card / **live `TCoinRed`** — never empty-only settle snapshots.
8. **Sirena casino / hotel**: `sameStage` treats casino episodes **0↔3** and **1↔4** as equivalent. Server authority + occupancy also coalesce via `LevelCatalog.NormalizeEpisodeFromGame` so mission ids and catalog ids share one red-coin / npc-clean / graffiti key (prevents solo-death wipe and missing catch-up when roster stores catalog 0 while the module publishes mission 3).

### Why FIFO pending-bind was wrong (2026-07-18)

After switch arm, locals that append-assigned indices as coins appeared (1→2→…→8) disagreed with remotes that sorted the full cohort by `initialPos`. FIFO then hid the “next unclaimed” live coin for remote mask bit N — often an **uncollected** neighbor. Fix: switch-cohort lock + position fingerprint; remove FIFO.

### Why Pianta reward was sound-only (2026-07-18)

On Red Coin Field, settle finds ~2 pre-placed reds. The first Type6 tick called `adoptDeadUntrackedRedCoins` which mass-filled slots to **8** with dead/empty junk (`adopt-dead count=8 (+5) mask=0x00`). Later NPC drops (flaming Pianta, bird, pods) could not append; apply still played collect FX / hide-by-pos could kill the new spawn. Fix: adopt only `TCoinRed`, max 1 per tick; claim placeholder slots for late live reds; skip collect FX unless a live actor was hidden; never hide `TCoinEmpty`.

## Wire format (compatible)

| Field | Meaning |
|-------|---------|
| `reserved` | Stable index 0–7 (identity) |
| `payload0` | `(authoritativeCount << 4) \| hudSlot` |
| `payload1` | Authoritative collected **mask** (low 8 bits). Legacy packed XYZ in payload1 is ignored for hide (`looksLikePackedCollectibleWorldPos`). |
| `payload2` | Packed `initialPos` fingerprint for remote hide / pending-bind (0 = unknown). |

No protocol version bump: older modules ignore `payload2`; mask in `payload1` still drives catch-up.

## Stage reload / HUD rule

After a stage reload, vanilla clears the switch bit and red-coin count. Durable `RedCoinCollected` history (and the ~45s progress resync) may still re-apply. Apply must **not** write `Type6Flag.mRedCoinCount` or call `processDownCoin` until the local mission is live:

- `mRedCoinSwitchPressed`, or
- live `TCoinRed` actors (pre-placed), or
- the red-coin HUD card already shown (`mIsRedCoinCard`)

Until then, apply only remembers collected indices for later hide/reconcile. When the local player arms the mission, a one-shot HUD catch-up fills slots and count. Trackers also reset on every `stageInit` (not only course/episode ID changes) so same-episode reloads do not keep stale state; durable mask events then re-apply and rebind to the new actors **only when co-op persistence applies** (see death-reset rule below).

## Death / solo reset rule (2026-07-18)

**Vanilla:** dying (or otherwise reloading the stage) clears red-coin progress for that attempt.

**BSMSO rule:**

| Situation | Behavior |
|-----------|----------|
| Solo (no other peer on same course+episode) dies / stage reloads | Progress **resets** — matches vanilla |
| Solo completes the shine | Normal (local + authority until stage empty) |
| 2+ players same course+episode, one dies | Collected mask **persists** for remaining / reloading players |
| Players on different stages | Independent — each stage’s mask is keyed by course+episode |

### Why solo death previously kept coins

1. Module correctly zeroed `sCollectedMask` on `stageInit` / same-episode reload.
2. Server `RedCoinAuthority` only cleared when stage occupancy hit **0**. Solo death keeps occupancy **1**, so authority survived.
3. ~45s `BuildAuthoritySnapshotReplay` (and any queued durable apply) re-injected `RedCoinCollected` with `already=0` after settle — log pattern: `snapshot ready … mask=0x00` then immediately `apply … already=0` with the pre-death mask.

### Fix (module + server)

1. **Module** (`red_coin_sync.cpp`): on **same-stage reload only** (death / soft reload — not joiner or first visit), if no same-stage peer → publish sentinel `RedCoinCollected` with `reserved=0xFF`. Clear same-stage deferred events. Do **not** blanket-skip applies while solo (that broke co-op join and local collect echoes). Deferred-drop stages snapshot **live `TCoinRed` only** (empties are switch-mission seats).
2. **Server** (`GameServer` / `RedCoinAuthority` / `WorldEventRelay`): on `reserved=0xFF`, `ResetStage` + purge durable red-coin history **only when occupancy ≤ 1 and no other session is already on that stage**. Progress snapshots omit red-coin stages with occupancy &lt; 2. When occupancy hits **2**, force an immediate progress resync so joiners receive the mask.

Diagnostics: `[SMSOBB] red-coin solo-mission-reset … (same-stage reload)`, `red-coin stage-enter co-op`, `red-coin co-op peer joined`.

## Live co-op red-coin apply (2026-07-18)

Same-stage partners must see collects **live** (hide + HUD) without reloading. Failures came from:

1. `MatchesEpisodeScopedApply` requiring exact episode equality while casino uses catalog↔mission aliases — live reds were queued until stage-enter flush.
2. Bridge incoming queue prioritizing only shine/blue/story — **graffiti flooded** the single Dolphin incoming slot so red-coin applies lagged until reload/resync.
3. Module local world-event queue could **drop** red-coin publishes to protect graffiti.

Fixes: casino aliases on live apply; prioritize `RedCoinCollected`/`NpcCleaned` ahead of graffiti; never drop red/shine/blue for graffiti; server pushes `live-red-coin-peer` progress snapshots to same-stage peers on each accept.

**Pianta:** Live deferred drops were rebound into **collected** fingerprint seats, then `hideRedCoinByStableIndex` immediately `makeObjDead`'d the reward coin (sound/talk without a visible coin). Fix: never attach a live drop to a collected seat; if only a collected seat matched the pos, **append/claim** a new uncollected slot instead. Field snapshots no longer lock `final` at 8 coins so late NPC drops keep expanding.

**Co-op death lag:** Same-stage reload does not change course/episode, so the launcher skipped progress resync (~45s wait). Fix: preserve co-op mask across reload when a same-stage peer was sticky; detect dead→alive revive; module asserts `BF_REQUEST_PROGRESS` so the launcher requests an immediate authority snapshot.

### Co-op death catch-up too slow (2026-07-18 night)

**Symptom:** Die while a peer shares the stage → red coins stay missing for ~20–45s until periodic progress resync.

**Root cause (logs):** `stageInit` zeroes `sCollectedMask`. `SessionCoordinator` only requested progress on course/episode **change**, so same-stage death never asked. Module could also publish `solo-mission-reset` during a brief peer-snapshot gap; when the server correctly **ignored** it, nothing pushed a catch-up snapshot (`32:28` reset ignored → `32:50` periodic apply of `mask=0x0E`).

**Fix:**
1. Module: sticky same-stage peer + delayed solo-reset confirm; on co-op same-stage reload **preserve** collected mask/fingerprints and keep deferred events (`co-op mask preserved`).
2. Server: when ignoring mission-reset due to co-op, `EnqueueProgressSnapshot(…, "co-op-death-catchup")`.
3. Launcher: on Dead-vfx clear with unchanged course/episode, `RequestWorldProgressResync("same-stage-revive …")`.

### Deferred Pianta / Pokey / pod / bird drop blocked (2026-07-18 night)

**Symptom:** After ~3 red coins on Red Coin Field, talking to the saved flaming Pianta does not yield a usable red coin.

**Root cause (logs):** `snapshot expand` then `adopt-dead` both grew the seat table for the **same** spawn (`expand count=3` → `adopt-dead count=4`, … up to `expand count=8 mask=0x1C`). Eight seats filled with duplicates/junk before the Pianta reward could claim a live `TCoinRed` slot. Zero `claim-slot` lines in `dolphin.log` confirmed late drops never reclaimed placeholders.

**Fix:** Expand appends only **unique** `initialPos` (else rebind/claim). Adopt refuses to append when the fingerprint is already owned. Collect path expands/finds first and adopts only if the dead actor was never tracked. Rebuild+deploy required — prior `dist/_BSMSO.kxe` still logged the old expand-only path.

## Flag sync interaction (future-proof pattern)

| Flag / state | Sync role |
|--------------|-----------|
| `Type5Flag.mRedCoinSwitchPressed` (`0x50009`) | **Local only** — excluded from durable TriggerFlag sync |
| `Type6Flag.mRedCoinCount` | **Derived** from mask / server count when mission live — do not treat as an independent durable stream |
| Collected mask | **Authoritative** durable mission progress (course+episode) |

### Reusable pattern: mission bitset + stable index

For other mission collectibles (red coins, Monte cleans, future bitsets):

1. Snapshot actors at settle; sort by a deterministic key (`mInitialPosition`); bind **pointers**. **Keep appending** when more actors of the same kind appear later — do not freeze a partial set.
2. Authority = bitset keyed by course/episode; events carry **index** (+ optional full mask).
3. Apply/hide **only** by index → bound actor; rebind / pending-bind on reload/spawn without changing indices already published.
4. Keep session arming flags (switches, etc.) out of durable flag sync when they must stay local.
5. Derive HUD counters from the bitset when the local mission is armed.
6. Never invent an identity key from “first free slot” when the collected actor is unknown.

Implementation: `module/src/red_coin_sync.cpp`. Server: `launcher/SMSO.Server/RedCoinAuthority.cs`.

### Diagnostics

OSReport tags in `dolphin.log`:

- `[SMSOBB] red-coin snapshot ready count=… mask=… final=…`
- `[SMSOBB] red-coin snapshot switch-cohort count=…`
- `[SMSOBB] red-coin switch-arm reset partial snap`
- `[SMSOBB] red-coin snapshot expand count=… (+…) mask=…`
- `[SMSOBB] red-coin snapshot rebind-live count=… (+…) mask=…`
- `[SMSOBB] red-coin snapshot claim-slot i=… (late drop)`
- `[SMSOBB] red-coin snapshot adopt-dead count=… (+…) mask=…`
- `[SMSOBB] red-coin pending-bind-pos i=… mask=…`
- `[SMSOBB] red-coin hide-by-pos mask=…`
- `[SMSOBB] red-coin collect i=… mask=… count=…`
- `[SMSOBB] red-coin collect-defer …` / `collect-defer switch-cohort …`
- `[SMSOBB] red-coin apply i=… mask=… count=… live=… already=… pos=…`
- `[SMSOBB] red-coin apply-skip solo-reset i=… mask=…`
- `[SMSOBB] red-coin solo-mission-reset course=…/…`
- `[SMSOBB] red-coin stage-enter co-op`
- `[SMSOBB] red-coin co-op peer joined — resume persist course=…/…`
- `[SMSOBB] red-coin co-op mask preserved 0x… (same-stage reload)`
- `[SMSOBB] red-coin hide-by-index i=… mask=… count=…`

## All red-coin missions / secrets (NTSC-U audit)

| Course | Ep (0-based) | Area notes | Layout |
|--------|--------------|------------|--------|
| 2 Bianco | 3 | Windmill Village | Pre-placed / switch episode |
| 2 Bianco | 7 | Lake | Pre-placed / switch episode |
| 47 | 0 | Hillside Cave secret | Switch + timer |
| 46 | 0 | Dirty Lake secret | Switch + timer |
| 3 Ricco | 5 | Red Coins on the Water | Pre-placed |
| 48 | 0 | Ricco Tower secret | Switch + timer |
| 4 Gelato | 5 | Coral Reef | Pre-placed (clustered — spatial hide regression) |
| 32 | 0 | Sand Castle secret | Switch + timer |
| 5 Pinna | 2 | Pirate Ships | Pre-placed |
| 50 | 0 | Beach Cannon secret | Switch + timer |
| 41 | 0 | Yoshi-Go-Round secret | Switch + timer |
| 6/7 Sirena | 7 (logical; hotel may load area 7 / scenario 4) | Hotel red coins | Pre-placed |
| 51 | 0 | Hotel Lobby secret | Switch + timer |
| 40 / 14 | 0 | Casino secret | Switch + timer |
| 8 Pianta | 7 | Fluff Festival Coin Hunt | Pre-placed |
| 9 Noki | 2 | Red Coins in a Bottle | Special / bottle |
| 9 Noki | 7 | Red Coin Fish | Special |
| 31 | 0 | Shell's Secret | Switch + timer |
| 23 | 0 | **Red Coin Field** (plaza pipe) | **Mixed: ~2 pre-placed + 6 deferred drops** |
| 22 | 0 | Pachinko Game | Red-coin shine |
| 24 | 0 | Lily Pad Ride | Red-coin shine |
| 0 / 20 | — | Airstrip Red Coin Waterworks | Timed red coins |
| 42 | 0 | Red Coin Chucksters | Red-coin shine |

Plaza single-ep secrets use `episodeId` 0 on their area id. Sirena hotel red coins may run inside area 7 with episode remapping — collection sync remains course+episode from `TMarDirector`.

### Gelato Beach notes

- Course 4. 1-based episode 6 = `episodeId` 5 = **Red Coins in the Coral Reef** (pre-placed underwater coins, no switch).
- 1-based episode 4 = Sand Bird (not a red-coin shine). Clustered coral-reef coins are the regression target for “collect 1 → only that coin vanishes.”

Sources: doldecomp `FlagManager.hxx`, `Coin.hxx`, `MapObjBase.hxx`, BSE `us.map`, MarioWiki Red Coin / Red Coin Field.

---

# Pianta Village Ep. 6 — Piantas in Need (Monte clean)

Mission progress is **not** a FlagManager counter. Stage scripts call SPC `checkMonteClear(N)` which returns true when named NPC `???N` has:

1. `LIVE_FLAG_UNK400000` clear (not sunk in goop), and
2. `TBaseNPC::isClean()` (`mPollutionAmount == 0`)

Vanilla flow: spray ground goop → NPC recovers from sink → spray NPC until `mPollutionAmount` hits 0 → script increments the rescued HUD.

`WE_NPC_REACT` wet events are **ephemeral** (spray spam). Late join / stage-enter progress replay therefore never restores cleaned state, so `checkMonteClear` stays false and the rescued counter desyncs for late joiners.

## Durable `NpcCleaned` (WE_NPC_CLEANED = 17)

When a pollution Monte locally transitions to clear, the module publishes durable `NpcCleaned` (course+episode, stable index by sorted initial position). Server `NpcCleanAuthority` dedupes by index; authority snapshots include cleans for join / ~45s / stage-enter resync.

Remote apply (`monte_clean_sync.cpp`):

1. `TPollutionManager::clean` at NPC (+ initial) position so they cannot re-sink
2. `mPollutionAmount = 0`
3. Clear `LIVE_FLAG_UNK400000 | LIVE_FLAG_SINK_BOTTOM | LIVE_FLAG_UNK10`
4. Clear `HIT_FLAG_NO_COLLISION`; raise buried NPCs toward load position

The stage script then sees `checkMonteClear` true for those indices and the HUD catch-up is **script-driven** (no silent bookkeeping vs HUD split like red coins).

Gated by `BF_SYNC_EVENT | BF_SYNC_MISSION`. Stage-empty occupancy reset clears authority like red coins.

Do **not** make wet `NpcReact` durable — that would flood TCP history.

### Pianta sink must not auto-wipe goop (2026-07-18)

**Symptom:** A pollution Pianta walks into goop / gets stuck → that goop patch disappears → the Pianta frees itself without player spray.

**Root cause:** Stage snapshot seeded `sCleanedMask` from every already-clear Monte (`alreadyClear=0xFFFF`). When a previously-clear Pianta later sank, `scanLocalMonteCleans` cleared `wasClear` but left the ownership bit set; `reconcilePendingMonteCleans` then called `forceMonteNpcCleared` (pollution `clean` radius 220 at NPC + initial pos + unsink flags).

**Fix:**
1. Snapshot records `wasClear` for transition detection only — does **not** seed `sCleanedMask`.
2. On re-sink / re-dirty, drop that NPC’s ownership bit so reconcile cannot force-unsink.
3. Mask bits are set only by real clear transitions (`WE_NPC_CLEANED` publish) or remote apply.

Legitimate player cleaning still publishes on clear transition and remotes still force-clean on durable apply. Log: `monte-clean snapshot count=%u alreadyClear=0x%X mask=0x%X`.
