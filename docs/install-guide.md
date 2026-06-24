# BSMSO Installation Guide

## Prerequisites

- Windows 10/11 x64
- [.NET 7 SDK](https://dotnet.microsoft.com/download) or newer
- CMake 3.16+ and Ninja
- Git + Git LFS
- Dolphin Emulator x64
- [Better Sunshine Engine](https://github.com/DotKuribo/BetterSunshineEngine) patched Super Mario Sunshine NTSC-U ISO

## Build from Source

```powershell
cd BSMSO
git submodule update --init --recursive
cd module\lib\BetterSunshineEngine
git lfs pull
cd ..\..\..
.\tools\build.ps1
dotnet build launcher\SMSO.sln -c Release
.\tools\publish.ps1
```

## Install Game Module

1. Download and install **BetterSunshineEngine.kxe** from the [BSE releases page](https://github.com/DotKuribo/BetterSunshineEngine/releases).
2. Copy `dist\_BSMSO.kxe` into your ISO folder at `files\Kuribo!\Mods\`
3. Load order: `BetterSunshineEngine.kxe` → `_BSMSO.kxe` (underscore prefix = child module)

Or use:

```powershell
.\tools\install-module.ps1 "C:\path\to\extracted\iso"
```

## Launcher Setup

1. Run the BSMSO launcher
2. Settings tab: set username, Dolphin path, ISO path
3. Click **Launch Dolphin** manually
4. Click **Host Server** or **Connect**

If the launcher reports an outdated module, rebuild with `tools\build.ps1` and reinstall `_BSMSO.kxe`.

**Do not** use SMSCoop alongside BSMSO.
