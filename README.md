# BSMSO (Better Super Mario Sunshine Online)

Online multiplayer mod for Super Mario Sunshine via Dolphin Emulator and [Better Sunshine Engine (BSE)](https://github.com/DotKuribo/BetterSunshineEngine).

Each player runs their own Dolphin instance. A dedicated server relays player state; the BSMSO launcher bridges Dolphin memory to the network via a RAM mailbox.

## Quick Start

1. Install prerequisites: .NET 7+ SDK, CMake + Ninja, Git + Git LFS, Dolphin x64, a BSE-patched NTSC-U ISO, and the BSE PowerPC toolchain (from Better Sunshine Engine).
2. Build:
   ```powershell
   git submodule update --init --recursive
   cd module/lib/BetterSunshineEngine; git lfs pull; cd ../../..
   .\tools\build.ps1
   dotnet build launcher/SMSO.sln -c Release
   .\tools\publish.ps1
   ```
3. Copy `dist/_BSMSO.kxe` into your ISO at `files/Kuribo!/Mods/` (after `BetterSunshineEngine.kxe`).
4. Run `dist/launcher/BSMSO.Launcher.exe`, configure paths, then **Launch Dolphin** before **Host Server** or **Connect**.

## Components

| Component | Description |
|-----------|-------------|
| `_BSMSO.kxe` | BSMSO game module — exports Mario state, renders remote players |
| `BSMSO.Launcher.exe` | WPF launcher with settings, warp, roster, help |
| `BSMSO.ServerHost.exe` | Optional headless dedicated server |

Default port: **27015** (TCP + UDP).

See `docs/install-guide.md` and `docs/network-setup.md` for full setup.

## AI

The only thing that ai is used for is help with code, none of the custom assets, such as the custom models, stages. and future assets will be made by others who help contribute to this mod and NOT by ai.
