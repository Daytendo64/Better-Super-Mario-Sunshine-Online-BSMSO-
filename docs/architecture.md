# SMSO Architecture

## Overview

Each player runs an independent Dolphin instance with BSE + `_SMSO.kxe`. This is **not** Dolphin Netplay.

```
Launcher (WPF) ←→ TCP/UDP Server ←→ Other Launchers
     ↕ RAM mailbox (5493 bytes @ 0x817FC000)
  Dolphin + _SMSO.kxe
```

## Components

| Component | Role |
|-----------|------|
| `SMSO.Launcher` | WPF UI, SessionCoordinator, config |
| `SMSO.Server` | Authoritative session host (embedded or standalone) |
| `SMSO.Net` | Protocol, CommBuffer, interpolation |
| `SMSO.Bridge` | Win32 ReadProcessMemory mailbox I/O |
| `_SMSO.kxe` | BSE module: export Mario state, remote visuals, warp |

## Authority

- **Server:** slots, usernames, warp validation, sync toggles
- **Each client:** local Mario physics
- **Receiving client:** remote player interpolation

## Comm Buffer

5493-byte packed struct shared between C++ module and C# bridge. Magic `0x534D534F`, CommVersion 14 (dual outbound `localPendingOwnership` / `localPendingMission` + dual inbound + progress snapshot mailbox). ModBuildId 53.

## Custom Mario model preparation

Installed uncompressed `/data/bsmso_models/<id>.arc` packs are loaded by one
demand-driven `DVDReadAsyncPrio` job. DVD callbacks only record completion;
validation and cache publication occur on the main thread, and body construction
waits for loading screens or eight consecutive safe idle frames. A model commit
only swaps a fully initialized body pointer, so the previous model stays visible
while preparation is pending.

The expanded MEM1 arena uses separate JKR heaps:

- 23.875 MiB arena: 16.5 MiB pack heap + 7.375 MiB body/J3D heap (targets 9 remote
  retail TMario puppets ≈ 5.4 MiB plus mid-stage custom arena headroom).
- 7.5 MiB fallback arena: 2.25 MiB pack heap + 5.25 MiB body/J3D heap (honest soft
  bound ≈ 7–8 remotes; prewarm soft-completes and remaining remotes lazy-spawn).

Baseline body-pool prewarm fills all `MAX_PLAYERS - 1` remote slots during load /
idle (staggered one construction per window). Heap exhaustion soft-completes the
prewarm so custom ready-body work is not starved.

The full split fits ten typical 1.6 MiB packs but only eight worst-case 2 MiB
packs; excess identities remain on the old/retail visual until a safe stage
boundary recycle. The fallback intentionally admits one worst-case pack.
Published packs and active body graphs remain pinned. Custom mid-stage
replacements are always born into 768 KiB child ExpHeap arenas (preferred
2-arena ping-pong for first staging, plus overflow children while the body
heap has room). Activation is pointer-only so the previous model stays
visible. Mid-stage never runs `teardownRemoteBodyGraph` / `~TMario` /
`freeAll` on a constructed remote body graph — SMS engine subsystems UAF when
those arenas are recycled before a stage boundary. Parked graphs remain
module-owned until stage-boundary body-heap recycle.

Main-heap prewarm / first-residency bodies (`arena == nullptr`) are never
scrub-and-forgotten mid-stage. Demotion parks them in a permanent spare table
(still module-owned so perform never falls through to retail) until
stage-boundary heap recycle. Ready/variant capacity pressure never frees
graphs mid-stage; when the body heap cannot allocate another arena the live
model stays visible and prepare soft-defers until RAM frees via warp recycle.

Sequential body churn therefore never crashes via address reuse; the honest
same-stage soft bound is body-heap RAM (~7 MiB ≈ ~9 arenas). Absolute
exhaustion keeps the current visible model and retries later.
`gRemoteHeapRecycleOnStageExit` remains a last-resort hint when RAM is
exhausted. The remaining same-stage distinct-identity soft bound is the
immutable pack heap/cache, which may LRU-unpin packs that are no longer
referenced by live/ready bodies.
