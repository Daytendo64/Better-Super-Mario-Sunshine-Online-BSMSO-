# Super Mario Sunshine Online (SMSO)

Online multiplayer mod for Super Mario Sunshine via Dolphin Emulator and Better Sunshine Engine (BSE).

Each player runs their own Dolphin instance. A dedicated server relays player state; the launcher bridges Dolphin memory to the network via a 672-byte RAM mailbox.

## Quick Start

1. Install prerequisites: .NET 7+ SDK, CMake + Ninja, Git + Git LFS, Dolphin x64, BSE-patched NTSC-U ISO, and the BSE PowerPC toolchain (from BetterSunshineEngine).
2. Build:
   ```powershell
   git submodule update --init --recursive
   cd module/lib/BetterSunshineEngine; git lfs pull; cd ../../..
   .\tools\build.ps1
   dotnet build launcher/SMSO.sln -c Release
   .\tools\publish.ps1
   ```
3. Copy `dist/_SMSO.kxe` into your ISO at `files/Kuribo!/Mods/` (after `BetterSunshineEngine.kxe`).
4. Run `dist/launcher/SMSO.Launcher.exe`, configure paths, then **Launch Dolphin** manually before **Host Server** or **Connect**.

## Components

| Component | Description |
|-----------|-------------|
| `_SMSO.kxe` | BSE game module — exports Mario state, renders remote players |
| `SMSO.Launcher.exe` | WPF launcher with settings, warp, roster, help |
| `SMSO.ServerHost.exe` | Optional headless dedicated server |

Default port: **27015** (TCP + UDP).

See `docs/install-guide.md` and `docs/network-setup.md` for full setup.
