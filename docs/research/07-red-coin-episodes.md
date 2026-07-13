# Red Coin Episodes

SMS red-coin shines use two layouts in vanilla: **switch** missions (ground-pound `TRedCoinSwitch`, coins spawn from `TCoinEmpty`) and **pre-placed** missions (coins already in the map).

**Red-coin switch arming is not networked.** Each player presses their own switch locally. There is no hip-drop publish for `TRedCoinSwitch`, no remote switch apply, and no collection-driven mission arming. `Type5Flag.mRedCoinSwitchPressed` (`0x50009`) is also excluded from durable `TriggerFlag` sync so one stage’s press cannot re-arm other missions.

Only **collection** is networked as durable `RedCoinCollected` (course+episode scoped). On switch missions, remotes only see collection progress for coins that have already appeared on their client (after they press the switch themselves).

## Stage reload / HUD rule

After a stage reload, vanilla clears the switch bit and red-coin count. Durable `RedCoinCollected` history (and the ~45s progress resync) may still re-apply. Apply must **not** write `Type6Flag.mRedCoinCount` or call `processDownCoin` until the local mission is live:

- `mRedCoinSwitchPressed`, or
- live `TCoinRed` actors (pre-placed), or
- the red-coin HUD card already shown (`mIsRedCoinCard`)

Until then, apply only remembers collected indices/positions for later hide/reconcile. When the local player arms the mission, a one-shot HUD catch-up fills slots and count. Trackers also reset on every `stageInit` (not only course/episode ID changes) so same-episode reloads do not keep stale state.

## Identity

At stage settle (~90 frames, sync enabled), each client snapshots live `TCoinRed` actors sorted by `mInitialPosition` into stable indices 0–7.

Collection events carry:
- `reserved` = stable index (0–7)
- `payload0` = `(authoritativeCount << 4) | hudSlot` (server authoritative)
- `payload1` = packed world XYZ via `packCollectibleWorldPos` (scale 16, bias 256)

## Safe remote apply

Never call or patch `taken__8TCoinRed` @ `0x801BE428`. Remote hide path (only while mission is live locally):

1. `makeObjDead()`
2. taken byte @ `0x152` = 1
3. clip flags (no vtable swap)
4. `Type6Flag.mRedCoinCount` = authoritative count
5. `processDownCoin` @ `0x801466F0` for HUD slot anim

Implementation: `module/src/red_coin_sync.cpp`. Server deduplicates collections by stable index in `launcher/SMSO.Server/RedCoinAuthority.cs`.

Sources: doldecomp `FlagManager.hxx`, `Coin.hxx`, `MapObjBase.hxx`, BSE `us.map`.

---

# Pianta Village Ep. 6 � Piantas in Need (Monte clean)

Mission progress is **not** a FlagManager counter. Stage scripts call SPC `checkMonteClear(N)` which returns true when named NPC `???N` has:

1. `LIVE_FLAG_UNK400000` clear (not sunk in goop), and
2. `TBaseNPC::isClean()` (`mPollutionAmount == 0`)

Vanilla flow: spray ground goop ? NPC recovers from sink ? spray NPC until `mPollutionAmount` hits 0 ? script increments the rescued HUD.

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

Do **not** make wet `NpcReact` durable � that would flood TCP history.
