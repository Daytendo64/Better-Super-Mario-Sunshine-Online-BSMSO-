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

- **Flag Sync** — shines, blue coins, story/mission flags
- **Object Sync** — collectibles (framework)
- **Progress Sync** — episode completion

These toggles are protocol/UI plumbing for world-event sync. Module-side world-event application is still experimental and should not be treated as complete progression sync yet.

## Limits

- Up to 10 players (9 remote slots per client)
- NTSC-U only
- No host migration
