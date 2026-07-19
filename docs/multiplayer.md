# SMSO Multiplayer Guide

## Getting Started

1. Install the mod (see `install-guide.md`)
2. All players launch SMSO Launcher and configure paths
3. Host clicks **Host Server**; clients click **Connect**
4. Everyone clicks **Launch Dolphin** separately (not automatic)

## Warping

- **Client Actions tab:** warp yourself to a level/episode
- **Server Actions tab (host only):** warp everyone, one player, or multiple selected players

Warps are blocked while a player is in a loading state.

## Remote Players

Other players appear as full remote Mario bodies with synced movement, animation, FLUDD nozzle state, spray visuals, sounds, and a name tag. You only see remotes on the **same course and episode** as you.

The launcher sends player snapshots at 60 Hz and smooths remote movement with a short render delay (~33 ms) to reduce rubber-banding.

The default module build enables full remote bodies. Use `.\tools\build.ps1 -ParticleOnly` only as a fallback build if you need to disable full bodies while debugging.

## World Sync (Host)

Server Actions tab toggles:

- **Flag Sync** — shines, blue coins, story/trigger/secret flags (Delfino Plaza gates, boats, nozzle rights, etc.)
- **Object Sync** — collectibles (framework)
- **Progress Sync** — episode completion

Flag Sync uses a grow-only server authority set plus snapshots (join / ~45s / stage-enter healing). **Shine and blue-coin ownership apply live on any stage** (FlagManager + HUD immediately; actor hide/FX only on the matching course). Persistent card progression is merged from connected players; verified Type5 MapEvent latches (`0x50001`, `0x50002`, `0x50004`) are plaza-only and coalesced to hub episode 255. Vanilla clears never erase shared progress, and same-episode reloads restore the last authoritative stage bits locally. Runtime Type3 flags, red-coin switch `0x50009`, graffiti/session Type5 bits, and one-shot spawn directors `0x30001`/`0x30004` are excluded. **Surface graffiti/goop cleaning** syncs as durable `GraffitiCleaned` cell stamps (32u **XYZ** grid, not full pollution bitmaps) — see `docs/research/12-graffiti-clean-sync.md`. Delfino Plaza does **not** soft-reload when `decideNextScenario` advances — archive/`loadAfter` props update on the next natural leave/re-enter. See `docs/research/06-flags-and-sync.md`.

## Hide & Seek

- Host assigns seekers/hiders, then **Start Tag**.
- **Start Tag grace (30s):** server-authoritative hide window. Seekers get a blue screen wash; everyone sees the `HIDE N` countdown, then a brief flash with **GO** when grace ends. Seekers cannot move; hiders can. Proximity tags are blocked until grace ends. Death still promotes hiders to seekers (new seekers stay frozen until grace ends).
- **Mid-round warp:** re-arms a short **4s proximity-only** immunity (no blue wash / no freeze) so spawn clustering does not mass-tag.
- **Stop Tag / Reset** clears grace immediately.

## Limits

- Up to 10 players (9 remote slots per client)
- NTSC-U only
- No host migration
