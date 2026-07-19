# Flags and Sync

NTSC-U save region base: `0x578940` (RAScript).

Host toggles sync categories via launcher **Sync Flags** (default on). Server relays `WorldEvent` TCP packets with monotonic `eventId`. **Durable** events (shine / blue coin / red coin / NPC cleaned / graffiti cleaned / story-class flags) are retained in history; ephemeral object events (NPC react, fruit, hip-drop) are broadcast live only and excluded from history so late-join replay cannot exceed the TCP payload cap.

## Live-first ownership pattern (reusable)

Durable **ownership** flags use a two-layer apply. This is the future-proof model for shine, blue coin, and story/secret bits:

1. **Immediate live apply** — write the `TFlagManager` bit and refresh HUD (`countShine` / `countBlueCoin`) as soon as the event reaches the module, **regardless of current course/episode**. Never defer the flag write itself to plaza load or stage match.
2. **Visual reconcile** — actor hide / particles / get-star anim only when the relevant actors exist on the current stage; otherwise log `defer-visual` and remember ids/masks for hide-on-settle. Visuals may wait; **flags never wait**.

| Layer | Shine | Blue coin | Story / secret |
|-------|-------|-----------|----------------|
| Ownership (always) | `setShineFlag` + HUD | `setBlueCoinFlag(course, idx)` + HUD | `setBool` on card/game banks |
| Visual (stage-gated) | remote get-star / hide actor | hide + FX on **matching course** | MapEvent `watch()` / geometry wake when actors exist |

Episode-scoped **mission** events (red coin, NPC cleaned, gold coin, hip-drop, fruit) still defer at the launcher when off-stage. Their module apply must also **free the durable mailbox** after recording ownership/mask state — never hold the single `incoming` slot waiting for actors/settle. Holding that slot previously blocked shine/blue until the next plaza `WorldProgressRequest`.

Bridge prioritizes shine / blue / story / secret ahead of ephemeral traffic in the pending incoming queue. Authority `WorldStateReplay` clears both the C# queue **and** the Dolphin incoming slot before re-pushing, so a stuck visual retry cannot block healing.

## Recovery (full-run durability)

Event-only delivery is lossy under 9-player load (single mailbox slot + ephemeral flood). Recovery paths:

1. **Authority snapshot replay** — server rebuilds shine / per-course blue / per-stage red bitmasks / per-stage NPC-cleaned masks / per-stage graffiti clean cells **and grow-only story/trigger/secret sets** into a `WorldStateReplay` for join, periodic rebroadcast (~45s), and client `WorldProgressRequest` on initial stage and stage enter / warp. Snapshot serialization order is **ownership first** (shine → blue → story → trigger → secret → red → npc → graffiti). Graffiti fills remaining TCP budget only — never truncates shine/blue/story catch-up under a graffiti-heavy 10-player run. This is a **healing** path, not the primary live path.
2. **Idempotent re-apply** — module re-applies durable collectibles even when `eventId <= lastAppliedEventId`, and retries (keeps incoming slot) only if FlagManager itself is missing.
3. **Red-coin switch arming** — local-only (`mRedCoinSwitchPressed` / Type5 `0x50009` excluded from durable TriggerFlag sync). Collection uses an index+mask model; remotes never press the switch for you — see `07-red-coin-episodes.md`.
4. **Monotonic flag merge** — clients baseline-publish existing durable set bits at one event per frame. The server accepts only `0→1`; local `resetStage`/`resetGame` clears never enter authority. Remote writes update local trackers, so they do not echo through normal polling.
5. **Same-episode reload recovery** — the stage-init callback runs even when course/episode IDs do not change. It preserves and reapplies authoritative Type5 bits after vanilla reset, then resets polling baselines.

## Comm buffer v5 (`WorldSyncState` @ offset 933)

| Field | Size | Direction |
|-------|------|-----------|
| `localPending` | 15 | module → bridge |
| `incoming` | 15 | bridge → module |
| `lastAppliedEventId` | 4 | module |

## Event types

| Type | Hook | Payload |
|------|------|---------|
| ShineCollected | **live** `setShineFlag` + HUD; get-star anim when actor exists | payload0 = global shine id; reserved = collector slot; payload1 = packed world position. All shine types: episode, secret, blue-coin reward, 100-coin, etc. Position captured from `mCollectedShine` during collection and stage snapshot at settle. |
| BlueCoinCollected | **live** `setBlueCoinFlag` + HUD; actor hide/FX on matching course | courseId + payload0 = `TMapObjBase::mMapObjID` (vanilla blue-coin flag index 0..49). Hide matches **only** that ID — never `TCoin::_154` (red-coin HUD slot / unused on TCoinBlue; often 0 and formerly false-matched every graffiti spawn after index-0 collect). |
| GoldCoinCollected | polling `0x40002` | payload1 = stage coin total; same episode only |
| RedCoinCollected | polling `Type6Flag.mRedCoinCount` | reserved=stableIndex; payload0=(count<<4)\|hudSlot; payload1=authoritative 8-bit collected mask. Hide by bound actor pointer only — see `07-red-coin-episodes.md` |
| NpcCleaned | pollution Monte clear detect (`monte_clean_sync`) | reserved=stableIndex; payload0=(count<<4)|index; payload1=packed position. Mask recorded immediately; actor unsink reconciled when NPCs exist — mailbox never waits on settle. |
| GraffitiCleaned | trampoline on `TPollutionManager::clean` (`graffiti_clean_sync`) | payload0=radius/8; payload1=packed XYZ; payload2=32u **XYZ** cell (10\|10\|10 + valid bit). Grow-only per stage (max 384). Plaza/casino episode aliases on apply. See `12-graffiti-clean-sync.md`. |
| Yoshi (riding) | UDP `PlayerSnapshot` | `nozzleId` low=Yoshi nozzle + high=color; `movementState` high 5 bits=juice; requires `VFX_NO_FLUDD` + Yoshi nozzle — see `08-yoshi-sync.md` |
| StoryFlag / TriggerFlag / SecretComplete | polling `TFlagManager` durable bools | payload1 = flag id; payload0 is always 1. Persistent card story bits exclude shine/blue ownership. Type5 is resetStage scratch; only verified durable MapEvent latches `0x50001`, `0x50002`, `0x50004` are admitted (plaza-only). Server `StoryFlagAuthority` coalesces those plaza Type5 bits to hub episode **`0xFF` (255)** so every dolpic scenario shares one key. Flag bits apply live (launcher does **not** episode-defer plaza hub TriggerFlags — same as StoryFlag/SecretComplete); module admits overlay off-plaza and writes FlagManager on plaza. **No plaza soft-reload**. |

Module applies idempotent flag writes when sync toggles enabled. Remote shine/blue/red coin apply also hides the world actor when present (same safe pattern as red coins: `makeObjDead` + vtable→`TCoinEmpty`; never calls `taken()`). Remote red/blue coin apply replays pickup particles for all clients **on the same course** (blue) / **same stage** (red/gold); pickup SFX (`MSD_SE_SY_RED_COIN_GET` / `MSD_SE_SY_BLUE_COIN_GET`) only when the local camera is within JAI `distanceMax` (`checkSoundArea` + `getDistPowFromCamera`) so distant players do not hear a global jingle. Blue-coin FX additionally requires `event.courseId == local area` — otherwise resolving a local coin by the same flag index would play SFX on a different stage. Local collectors still get audio from vanilla `taken()`.

**Episode-scoped** events (gold / red / NPC cleaned / graffiti cleaned / hip-drop / fruit / NPC react / non-hub Type5) received while on another stage are deferred in the launcher until the matching course/episode loads. **Shine / blue / story / secret / plaza hub TriggerFlag (episode 255)** are not episode-scoped — ownership applies immediately on any stage. Hooks skip re-broadcast while applying remote events. Blue HUD refreshes via `countBlueCoin` after ownership apply. Shine HUD: on each newly set shine flag, re-arm `startAppearStar` and spin `countShine` until the console displayed total (`+0x64`) catches FlagManager `0x40000` (one-shot appear + single `countShine` only fixed the first shine per stage).

OSReport diagnostics (shine/blue): `shine publish` / `shine apply-flag` / `shine hud-refresh` / `shine defer-visual`; `blue publish` / `blue apply-flag` / `blue defer-visual` / `blue defer-fx`.

### Shine HUD cross-stage / multi-shine (2026-07-18)

**Symptom (1):** Remote shine collect sets the flag (`shine apply-flag … changed=1`) but the on-screen shine counter stays stale until returning to Delfino Plaza.

**Symptom (2):** First synced shine bumps the HUD; a **second** shine in the same stage logs `shine hud-refresh count=2` but the digits stay at **1** until plaza refresh.

**Root cause:** `TGCConsole2::startAppearStar` is one-shot (`console+0x34`). While armed, `perform` drives `countShine` across ~250 frames; `countShine` commits the shown total to `console+0x64` and advances timer `+0x8A`. Calling `startAppearStar` + one `countShine` per apply arms the card for the first shine (later frames finish digits) but the second shine early-returns from `startAppearStar` (flag already set / pane settled), so only an orphan `countShine` runs and digits stall.

**Fix:** On each newly set shine flag: clear the appear one-shot + appear frame, call `startAppearStar`, force displayed cache behind the FlagManager total (`0x40000`), zero the count timer, then **spin `countShine` until `displayed >= count`** (cap 320) so every shine bumps digits immediately on any stage. Log: `shine hud-refresh count=%d displayed=%d timer=%u armed=%u`.

Visual reconcile (`defer-visual` / get-star anim) stays stage-local.

## Delfino Plaza (dolpic) story state

Plaza visuals are driven by:

1. **Which `dolpic` scenario loads** (shine count + story progress — see `DelfinoPlazaMapping`).
2. **Card bools** such as `0x10384` (Bianco king gate) and nozzle rights `0x10366+`.
3. **Stage bools** such as `0x50001` (Ricco tanuki house), `0x50002` (lighthouse), `0x50004` (MareGate / boat).

   **Durable Type5 allowlist:** `0x50001`, `0x50002`, `0x50004`, plaza-only, coalesced to episode **255 (`PlazaHubEpisode`)**. All other Type5 bits are resetStage scratch (including graffiti/timers/session switches); `0x50009` (`mRedCoinSwitchPressed`) remains local-only. Launcher applies hub TriggerFlags immediately (any stage); flushing queued episode events on plaza also drains episode-255 hub triggers.

   **Excluded from durable story/game sync:** `0x30001` / `0x30004` — one-shot spawn directors consumed by `decideMarioPosIdx`. Pinna unlock FMV sets `0x30004` so the post-cutscene plaza entry uses the cannon spawn; vanilla clears it after use. Durable sync used to re-apply `0x30004` on every plaza enter (authority snapshot after stageInit), so returns from Ricco/Bianco/etc. always spawned at the Pinna cannon. Durable Pinna unlock progress is card bool `0x10389` (`decideNextScenario` → `dolpic8`), which still syncs.

Shine sync alone is not enough: remotes can share shine counts while gates/boats/flood props stay stale. BSMSO polls persistent card progression plus the verified stage-trigger allowlist, emits set-only `StoryFlag` / `TriggerFlag`, and includes them in sparse authority snapshots. Runtime Type3 is never durable: decomp confirms `resetGame` clears the whole bank, while stable outcomes live in card flags. Remote geometry flags are written into `TFlagManager` immediately.

### Snapshot semantics

Story snapshots are authoritative **sparse set snapshots**, not full bitmaps. Presence means set; absence does not command a clear. This is intentional because clearing an absent bit could erase local save progress or let a vanilla stage reset roll back the session. Connected clients merge their initial durable card/stage set into authority gradually (one event per frame), so differing local saves converge to their union. Server snapshots then deterministically heal missed packets, reconnects, and late joins. A new session clears the module's cached authority overlay after the disconnected mailbox state is observed.

### Live mid-visit (no stage reload)

MapEvents / scripts that actively `watch()` FlagManager bits update as soon as the bit is applied:

| Example | Flag | Behavior |
|---------|------|----------|
| Shine count / ownership | shine id 0..127 | HUD + `getShineFlag` update on any stage |
| Blue coin ownership | course + index 0..49 | HUD + flag update on any stage; actor hide only on matching course |
| Bianco king gate | `0x10384` | Opens/updates when the card bool is set remotely |
| Ricco tanuki house | `0x50001` | Watched MapEvent reacts live |
| Lighthouse / Gelato | `0x50002` | Watched MapEvent reacts live |
| Other watched story/trigger bits | card/stage/game banks | Same — flag write is enough |

### Needs natural leave / re-enter

Vanilla binds some plaza layout to the **loaded `dolpicN` archive** and to `loadAfter` (not to a continuous watch). BSMSO does **not** soft-reload the hub to force these — players stay in place; the next natural exit/re-entry (pipe, warp, episode change) loads the correct archive via `decideNextScenario`:

| Visual / prop | Why stale until re-enter |
|---------------|--------------------------|
| Hub archive switch (e.g. Pinna unlock `dolpic6` → `dolpic7`) | Scenario archive encodes MapObj sets, NPC placements, Pinna cannon layout |
| MareGate / boat (`0x50004`) | `TMareGate::loadAfter` only; MapEvent watch cannot spawn it mid-visit |
| Flood / post-flood layout | Bound to `dolpic9` / `dolpic10` archives — never force-reload (old soft-reload looped the flood cutscene) |
| Shine / blue **actors** on another course | Ownership + HUD already live; world actor hide waits until that course is loaded |

Shine count, blue ownership, and story bits sync live mid-visit; only archive-bound MapObj sets stay on the scenario that was entered.
