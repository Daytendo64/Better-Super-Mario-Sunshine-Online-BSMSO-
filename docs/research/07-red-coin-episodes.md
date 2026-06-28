# Red Coin Episodes

SMS red-coin shines use two layouts in vanilla: **switch** missions (ground-pound `TRedCoinSwitch`, coins spawn from `TCoinEmpty`) and **pre-placed** missions (coins already in the map). Switch activation, timer, HUD card, and empty→red spawning remain **local-only** per client — only collection is networked.

## Identity

At stage settle (~90 frames, sync enabled), each client snapshots live `TCoinRed` actors sorted by `mInitialPosition` into stable indices 0–7.

Collection events carry:
- `reserved` = stable index (0–7)
- `payload0` = `(authoritativeCount << 4) | hudSlot` (server authoritative)
- `payload1` = packed world XYZ via `packCollectibleWorldPos` (scale 16, bias 256)

## Safe remote apply

Never call or patch `taken__8TCoinRed` @ `0x801BE428`. Remote hide path:

1. `makeObjDead()`
2. taken byte @ `0x152` = 1
3. clip flags (no vtable swap)
4. `Type6Flag.mRedCoinCount` = authoritative count
5. `processDownCoin` @ `0x801466F0` for HUD slot anim

Implementation: `module/src/red_coin_sync.cpp`. Server deduplicates collections by stable index in `launcher/SMSO.Server/RedCoinAuthority.cs`.

Sources: doldecomp `FlagManager.hxx`, `Coin.hxx`, `MapObjBase.hxx`, BSE `us.map`.
