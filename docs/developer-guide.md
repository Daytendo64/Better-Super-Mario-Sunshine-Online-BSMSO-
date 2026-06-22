# SMSO Developer Guide

## Repository Layout

```
SMSO/
├── launcher/     # C# solution
├── module/       # BSE C++ module
├── assets/       # levels.ntsc-u.json
├── tools/        # build scripts
└── docs/
```

## Building

```powershell
.\tools\build.ps1                # _SMSO.kxe with full remote Mario bodies
.\tools\build.ps1 -ParticleOnly  # fallback build without full remote bodies
dotnet test launcher\SMSO.Tests
python tools\verify_levels.py
.\tools\publish.ps1
```

## Comm Buffer Contract

`module/include/comm_buffer.hpp` and `launcher/SMSO.Net/CommBuffer.cs` must stay byte-identical. Run `CommBufferTests` after any layout change.

## BSE Callbacks

Register only safe APIs in `module/src/module.cpp`:

- `Stage::addInitCallback`, `addUpdateCallback`, `addDraw2DCallback`, `addExitCallback`
- `Player::addUpdateCallback`, `addLoadAfterCallback` for warp/demo handling

## Protocol Versioning

Bump `COMM_VERSION` and `ProtocolConstants.ProtocolVersion` together.

## Submodule

```powershell
git submodule update --init module/lib/BetterSunshineEngine
cd module/lib/BetterSunshineEngine && git lfs pull
```
