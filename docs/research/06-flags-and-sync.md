# Flags and Sync

NTSC-U save region base: `0x578940` (RAScript).

Host toggles sync categories; server relays `WorldEvent` packets with monotonic `eventId`.

Module applies idempotent flag writes when sync toggles enabled. Prevents double-apply via `lastAppliedEventId`.
