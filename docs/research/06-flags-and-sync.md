# Flags and Sync

NTSC-U save region base: `0x578940` (RAScript).

Host toggles sync categories via launcher **Sync Flags** (default on). Server relays `WorldEvent` TCP packets with monotonic `eventId` and stores full history for **late join replay** (`WorldStateReplay` sent to joining clients after `JoinAccepted`).

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
| BlueCoinCollected | `setBlueCoinFlag` + actor hide | courseId + payload0 = position-sorted coin index |
| GoldCoinCollected | polling `0x40002` | payload1 = stage coin total; same episode only |
| RedCoinCollected | polling `Type6Flag.mRedCoinCount` | payload0=HUD slot; reserved=stableIndex; payload1=packed position; authoritative count in payload0 high nibble |
| Yoshi (riding) | UDP `PlayerSnapshot` | `nozzleId` low=Yoshi nozzle + high=color; `movementState` high 5 bits=juice; requires `VFX_NO_FLUDD` + Yoshi nozzle — see `08-yoshi-sync.md` |
| StoryFlag / TriggerFlag / SecretComplete | `setFlag` / `setBool` | payload1 = flag id, payload0 = value |

Module applies idempotent flag writes when sync toggles enabled. Remote shine/blue/red coin apply also hides the world actor (same safe pattern as red coins: `makeObjDead` + vtable→`TCoinEmpty`; never calls `taken()`). Episode-scoped events received while on another stage are deferred until the matching course/episode loads. Hooks skip re-broadcast while applying remote events. HUD counters refreshed via `countShine` / `countBlueCoin` after each apply.
