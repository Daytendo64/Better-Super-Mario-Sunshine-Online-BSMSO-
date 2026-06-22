# SMSO Architecture

## Overview

Each player runs an independent Dolphin instance with BSE + `_SMSO.kxe`. This is **not** Dolphin Netplay.

```
Launcher (WPF) ←→ TCP/UDP Server ←→ Other Launchers
     ↕ RAM mailbox (672 bytes @ 0x817FC000)
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

672-byte packed struct shared between C++ module and C# bridge. Magic `0x534D534F`, version 1.
