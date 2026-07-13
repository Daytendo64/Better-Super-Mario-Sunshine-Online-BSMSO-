# Flags and Sync

NTSC-U save region base: `0x578940` (RAScript).

Host toggles sync categories via launcher **Sync Flags** (default on). Server relays `WorldEvent` TCP packets with monotonic `eventId`. **Durable** events (shine / blue coin / red coin / NPC cleaned / story-class flags) are retained in history; ephemeral object events (NPC react, fruit, hip-drop) are broadcast live only and excluded from history so late-join replay cannot exceed the TCP payload cap.

## Recovery (full-run durability)

Event-only delivery is lossy under 9-player load (single mailbox slot + ephemeral flood). Recovery paths:

1. **Authority snapshot replay** — server rebuilds shine / per-course blue / per-stage red bitmasks / per-stage NPC-cleaned masks **and grow-only story/trigger/secret sets** into a `WorldStateReplay` for join, periodic rebroadcast (~45s), and client `WorldProgressRequest` on initial stage and stage enter / warp.
2. **Idempotent re-apply** — module re-applies durable collectibles even when `eventId <= lastAppliedEventId`, and retries (keeps incoming slot) if FlagManager is not ready.
3. **Red-coin switch arming** — remote red-coin collection apply appears empty→red slots and presses the switch if present, so a missed hip-drop switch broadcast cannot leave the mission with no coins.
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
| ShineCollected | `setShineFlag` + remote get-star anim on collector's Mario | payload0 = global shine id; reserved = collector slot; payload1 = packed world position. All shine types: episode, secret, blue-coin reward, 100-coin, etc. Position captured from `mCollectedShine` during collection and stage snapshot at settle. |
| BlueCoinCollected | `setBlueCoinFlag` + actor hide | courseId + payload0 = `TMapObjBase::mMapObjID` (vanilla blue-coin flag index 0..49). Hide matches **only** that ID — never `TCoin::_154` (red-coin HUD slot / unused on TCoinBlue; often 0 and formerly false-matched every graffiti spawn after index-0 collect). |
| GoldCoinCollected | polling `0x40002` | payload1 = stage coin total; same episode only |
| RedCoinCollected | polling `Type6Flag.mRedCoinCount` | payload0=HUD slot; reserved=stableIndex; payload1=packed position; authoritative count in payload0 high nibble |
| NpcCleaned | pollution Monte clear detect (`monte_clean_sync`) | reserved=stableIndex; payload0=(count<<4)|index; payload1=packed position. Forces clean+unsunk so `checkMonteClear` / Pianta 6 HUD catch up on late join — see `07-red-coin-episodes.md` |
| Yoshi (riding) | UDP `PlayerSnapshot` | `nozzleId` low=Yoshi nozzle + high=color; `movementState` high 5 bits=juice; requires `VFX_NO_FLUDD` + Yoshi nozzle — see `08-yoshi-sync.md` |
| StoryFlag / TriggerFlag / SecretComplete | polling `TFlagManager` durable bools | payload1 = flag id; payload0 is always 1. Persistent card story bits exclude shine/blue ownership. Type5 is resetStage scratch and is keyed by course/episode; only verified durable MapEvent latches `0x50001`, `0x50002`, `0x50004` are admitted. Type3 runtime flags and all other Type5 graffiti/session/one-shot bits are non-durable. Server `StoryFlagAuthority` is a grow-only set. Flag bits apply live so watched MapEvents update mid-visit. **No plaza soft-reload**. |

Module applies idempotent flag writes when sync toggles enabled. Remote shine/blue/red coin apply also hides the world actor (same safe pattern as red coins: `makeObjDead` + vtable→`TCoinEmpty`; never calls `taken()`). Remote red/blue coin apply replays pickup particles for all clients **on the same course** (blue) / **same stage** (red/gold); pickup SFX (`MSD_SE_SY_RED_COIN_GET` / `MSD_SE_SY_BLUE_COIN_GET`) only when the local camera is within JAI `distanceMax` (`checkSoundArea` + `getDistPowFromCamera`) so distant players do not hear a global jingle. Blue-coin FX additionally requires `event.courseId == local area` — otherwise resolving a local coin by the same flag index would play SFX on a different stage. Local collectors still get audio from vanilla `taken()`. Episode-scoped events received while on another stage are deferred until the matching course/episode loads. Hooks skip re-broadcast while applying remote events. HUD counters refreshed via `countShine` / `countBlueCoin` after each apply.

## Delfino Plaza (dolpic) story state

Plaza visuals are driven by:

1. **Which `dolpic` scenario loads** (shine count + story progress — see `DelfinoPlazaMapping`).
2. **Card bools** such as `0x10384` (Bianco king gate) and nozzle rights `0x10366+`.
3. **Stage bools** such as `0x50001` (Ricco tanuki house), `0x50002` (lighthouse), `0x50004` (MareGate / boat).

   **Durable Type5 allowlist:** `0x50001`, `0x50002`, `0x50004`, keyed by course/episode. All other Type5 bits are resetStage scratch (including graffiti/timers/session switches); `0x50009` (`mRedCoinSwitchPressed`) remains local-only.

   **Excluded from durable story/game sync:** `0x30001` / `0x30004` — one-shot spawn directors consumed by `decideMarioPosIdx`. Pinna unlock FMV sets `0x30004` so the post-cutscene plaza entry uses the cannon spawn; vanilla clears it after use. Durable sync used to re-apply `0x30004` on every plaza enter (authority snapshot after stageInit), so returns from Ricco/Bianco/etc. always spawned at the Pinna cannon. Durable Pinna unlock progress is card bool `0x10389` (`decideNextScenario` → `dolpic8`), which still syncs.

Shine sync alone is not enough: remotes can share shine counts while gates/boats/flood props stay stale. BSMSO polls persistent card progression plus the verified stage-trigger allowlist, emits set-only `StoryFlag` / `TriggerFlag`, and includes them in sparse authority snapshots. Runtime Type3 is never durable: decomp confirms `resetGame` clears the whole bank, while stable outcomes live in card flags. Remote geometry flags are written into `TFlagManager` immediately.

### Snapshot semantics

Story snapshots are authoritative **sparse set snapshots**, not full bitmaps. Presence means set; absence does not command a clear. This is intentional because clearing an absent bit could erase local save progress or let a vanilla stage reset roll back the session. Connected clients merge their initial durable card/stage set into authority gradually (one event per frame), so differing local saves converge to their union. Server snapshots then deterministically heal missed packets, reconnects, and late joins. A new session clears the module's cached authority overlay after the disconnected mailbox state is observed.

### Live mid-visit (no stage reload)

MapEvents / scripts that actively `watch()` FlagManager bits update as soon as the bit is applied:

| Example | Flag | Behavior |
|---------|------|----------|
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

Shine count and story bits still sync and are honored on the **next** plaza load; only the currently loaded MapObj set stays on the archive that was entered.
