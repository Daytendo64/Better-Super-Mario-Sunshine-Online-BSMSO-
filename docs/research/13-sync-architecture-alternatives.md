# Sync Architecture Alternatives (SMS + Dolphin)

Short comparison for the Phase 1 authority-first rewrite. Context: each player runs an
independent Dolphin + `_BSMSO.kxe` with a 672–5 KiB RAM mailbox; there is no shared
emulator lockstep.

## (A) Authority-first snapshots + deltas — **chosen**

**Model:** Server holds durable bitsets / sparse maps (shine, blue, red, NPC, story). Live
collectibles publish as tiny TCP deltas. Clients recover from compact
`WorldProgressSnapshot` heals (mailbox bulk-apply), never from unbounded event history.

**Why it fits SMS + Dolphin:**
- GameCube FlagManager is already a bitset authority; mirroring that on the server is natural.
- Mailbox is tiny and single-slot per lane — O(progress) heals beat O(N) event drains.
- Independent Dolphin instances diverge constantly (physics, RNG, load timing); shared
  inputs cannot reconstruct FlagManager state.
- Late join / mid-run catch-up is a snapshot merge, not a replay of the whole session.

Phase 1 hardens this with an `AuthorityHealGovernor`: local authority cache restage so
force-full never clears the mailbox and waits forever on TCP.

## (B) Lockstep / input share

**Model:** All peers share controller inputs (or deterministic RNG seeds) and advance the
same simulation frame-by-frame (classic Dolphin Netplay).

**Why it does not fit BSMSO:**
- BSMSO is explicitly **not** Dolphin Netplay — each client owns local Mario physics.
- Sunshine is full of non-deterministic actor timing, DVD load, and BSE hooks; lockstep
  stalls everyone on one peer's hitch.
- Custom models, per-player Yoshi/FLUDD, and asymmetric stage occupancy break a single
  sim.
- Would require rewriting the product architecture, not just flag sync.

## (C) Full state mirror

**Model:** Continuously replicate large slices of MEM1 (actors, FlagManager banks, heap
graphs) so remotes are pixel-identical.

**Why it is a poor fit:**
- Bandwidth and mailbox size explode; 10 players × actor graphs is not viable on the
  current bridge.
- WriteProcessMemory races with the game thread → crashes / softlocks.
- Most mirrored bytes are ephemeral VFX; durable progress is a tiny fraction.
- Debugging and versioning become intractable compared to explicit authorities.

## Summary

| | Heal cost | Soft-death risk | Fits independent Dolphins |
|--|--|--|--|
| **A snapshots+deltas** | O(progress) | Low if cache+circuit | Yes |
| B lockstep | O(1)/frame but stalls | Desync = hard stop | No |
| C full mirror | O(RAM) | Write races | Poorly |

Stay on (A). Phase 2 should finish pruning live mission queues and make ownership /
mission / ephemeral completely separate end-to-end (including module outbound).
