# E2E Test Plan

## Automated (CI)

```powershell
dotnet test launcher\SMSO.Tests
python tools\verify_levels.py
```

Integration tests cover: two-client join, duplicate username rejection, server start/stop, CommBuffer layout.

## Manual LAN Checklist

| Test | Steps | Pass |
|------|-------|------|
| 2P connect | Two PCs, host + client | Both see roster |
| Position sync | Move Mario in-game | Remote proxy moves |
| Remote column | 2P same stage, walk around | Particle column visible at remote player |
| Name tag | 2P same stage | Username floats above remote column |
| Trail | 2P same stage, sprint/jump | Trail particles behind moving remote |
| Stage filter | Players on different courses | No remote proxy for wrong-stage player |
| FLUDD export | Spray/hover/rocket in-game | Roster still updates; nozzle/water in snapshot |
| Self warp | Client Actions warp | Correct stage loads |
| Host warp all | Server Actions | All players same stage |
| Duplicate name | Same username twice | Second rejected |
| Dolphin crash | Kill Dolphin | Launcher disconnects, notifies user |
| 10P stress | 10 clients 15 min | Server stable |

## WAN

Forward TCP+UDP 27015; external client connects via public IP.
