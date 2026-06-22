# SMSO Installation Guide

## Prerequisites

- Windows 10/11 x64
- [.NET 7 SDK](https://dotnet.microsoft.com/download) or newer
- CMake 3.16+ and Ninja
- Git + Git LFS
- Dolphin Emulator x64
- BSE-patched Super Mario Sunshine NTSC-U ISO

## Build from Source

```powershell
cd SMSO
git submodule update --init --recursive
cd module\lib\BetterSunshineEngine
git lfs pull
cd ..\..\..
.\tools\build.ps1
dotnet build launcher\SMSO.sln -c Release
.\tools\publish.ps1
```

## Install Game Module

1. Copy `dist\_SMSO.kxe` into your ISO folder at `files\Kuribo!\Mods\`
2. Ensure `BetterSunshineEngine.kxe` is in the same folder and loads first
3. Load order: `BetterSunshineEngine.kxe` → `_SMSO.kxe` (underscore prefix = child module)

Or use:

```powershell
.\tools\install-module.ps1 "C:\path\to\extracted\iso"
```

## Launcher Setup

1. Run `dist\launcher\SMSO.Launcher.exe`
2. Settings tab: set username, Dolphin path, ISO path
3. Click **Launch Dolphin** manually
4. Click **Host Server** or **Connect**

**Do not** use SMSCoop alongside SMSO.
