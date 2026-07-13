# Disc Speed, BSE Settings CARD I/O, and Moveset

## Summary

Dolphin **Emulate Disc Speed** (`FastDiscSpeed=False`) stresses Better Sunshine Engine’s memory-card settings path and DVD usage. BSMSO forces `FastDiscSpeed=True` for GMSE90; that remains required for reliable settings save/load. This note documents root causes and what BSMSO can fix in-repo.

## Boot / settings / CARD path (BSE)

1. Kuribo loads `BetterSunshineEngine.kxe`, then Moveset / `_BSMSO.kxe`.
2. Modules register `SettingsGroup`s (`GMSB` banner metadata; `mSaveGlobal=false` → files keyed to the **disc** ID, i.e. `GMSE90`).
3. `BetterApplicationProcess` → `initAllSettings`:
   - `CARDInit` / `Settings::mountCard`
   - Per-module `loadSettingsGroup` (`CARDOpen` / `CARDRead`)
   - On success only (upstream): `setting->emit()` for each setting
4. Settings menu / autosave: unmount game `TCardManager`, remount via BSE `Settings::mountCard`, write module saves, remount game card.

### Failure modes under real disc timing

| Mechanism | Effect |
|-----------|--------|
| Busy-wait on `CARD_ERROR_UNLOCKED` / `CARD_ERROR_BUSY` without `OSYieldThread` | Starves EXI unlock while AudioStreamer DVD thread + sync `DVDReadPrio` (stage params) hold the DI bus |
| Slot B unlock bug (`sChannel = CARD_SLOTA`) | Wrong channel while waiting for Slot B unlock |
| `loadSettingsGroup` returns on `NOFILE` / open failure **without** `emit()` | Constructor defaults stay in RAM, but Moveset `valueChanged` patches never run |
| Sync `DVDReadPrio` in `Stage::TStageParams::load` | Long blocking DVD reads under emulated disc speed |

Moveset settings that only check `getBool()` (Back Flip, Pound Jump, …) still “work” from defaults. Settings that need `emit()` / `valueChanged` do **not**:

- Long Jump / Crouch button mapping (PowerPC patches)
- Hover Slide (animation registration)
- Fast Turbo (memory constant write)

That matches “some moves unavailable” when CARD load fails under disc pressure.

## Moveset init + `better_movement.prm`

- `initModule` registers settings (`mGameCode = 'GMSB'`, `mSaveGlobal = false`).
- `onPlayerInit` loads `/better_movement.prm` from the mounted `"mario"` JKR volume (also `PlayerMovementParams` ctor path `/mario/better_movement.prm`).
- BSMSO CharacterPack remounts replace the mario archive. Merged custom packs historically **omitted** `better_movement.prm` (retail SMS archive also lacks it unless a Moveset release patched mario). Missing prm → Moveset keeps code defaults (`mMaxJumps=1`, multipliers `1.0`) instead of official Moveset retune.

## What BSMSO ships

1. **BSE submodule patch** (`settings.cpp`): yield on CARD busy/unlock waits; emit defaults when load fails; fix Slot B unlock channel.
   - Source is in `module/lib/BetterSunshineEngine`. Do **not** deploy DEBUG/dev `.kxe` until boot-verified.
   - Installer uses official v4.0.0 `BetterSunshineEngine.kxe` (583744). Local copies are used only if they match that size.
2. **Moveset source** (`tools/_research-moveset`): keep official init path (no early `emit` at module load). Known-good binary is 46976 bytes.
3. **CharacterPack**: inject official-default `better_movement.prm` when missing; `MarioPackInstaller` / `ModelLibrary.TryGetPackBytes` repair retail `mario.arc` and library packs. Sample at `assets/better_movement.prm`.
4. **Launcher**: keep forcing `FastDiscSpeed=True` + MEM1/MEM2 on every launch (already present).

### Rebuild commands (dev)

```powershell
# Patched BSE.kxe — MUST use Release toolchain AND set CMAKE_BUILD_TYPE=Release
# (empty build type previously produced a DEBUG 603424 kxe that black-screened).
cmake -S module\lib\BetterSunshineEngine -B module\lib\BetterSunshineEngine\build-bsmso-patch -G Ninja `
  "-DCMAKE_TOOLCHAIN_FILE=module\lib\BetterSunshineEngine\targets\GCNKuriboClangRelease.cmake" `
  -DCMAKE_BUILD_TYPE=Release -DSMS_REGION=us
cmake --build module\lib\BetterSunshineEngine\build-bsmso-patch --config Release

# Patched Moveset.kxe
cmake -S tools\_research-moveset -B tools\_research-moveset\build-bsmso -G Ninja `
  "-DCMAKE_TOOLCHAIN_FILE=tools\_research-moveset\targets\GCNKuriboClangRelease.cmake" `
  -DCMAKE_BUILD_TYPE=Release -DSMS_REGION=us
cmake --build tools\_research-moveset\build-bsmso --config Release
```

**Before deploying any rebuilt `.kxe`:** confirm size (BSE ≈ 583744 release; Moveset ≈ 46976), boot Dolphin to title screen, and check `dolphin.log` for `Loaded module` on Engine, Moveset, and `_BSMSO.kxe` with no `Unknown instruction`.

## Black-screen regression (2026-07-11)

Unvalidated rebuilds deployed to `Kuribo!/Mods/` caused an immediate black screen:

| Module | Bad size | Known-good |
|--------|----------|------------|
| `BetterSunshineEngine.kxe` | 603424 (DEBUG, built Jul 11 2026) | 583744 (official v4.0.0 release) |
| `BetterSunshineMoveset.kxe` | 44992 | 46976 |

**Dolphin log evidence:** BSE finished Kuribo load (`FINISHED`), then `FILE: BetterSunshineMoveset.kxe` with **no** `Loaded module` — CPU immediately hit `Unknown instruction 00000000` at `PC=80004038`, `last_PC=81200d60`, `LR=804ce5f8` (Kuribo/BSE loader path). Game never reached title.

**Mitigation:** Mods restored to release BSE + known-good Moveset. Launcher now ignores local BSE `.kxe` unless size is exactly `583744`, and warns if Moveset size ≠ `46976`. Do not ship DEBUG/dev BSE or incomplete Moveset rebuilds until boot is verified.

## Honest limits

- **Emulate Disc Speed ON** remains unsafe for BSE AudioStreamer + sync DVD stage loads even with CARD yields. Prefer Fast Disc Speed for BSMSO.
- Settings **persistence** still depends on successful CARD write; yields reduce races but cannot make EXI+DVD contention disappear under full disc emulation.
- Shipping the patched `.kxe` files requires re-running **Install / patch modules** (or copying into `Kuribo!/Mods/`) so the game tree picks them up.
- Patched BSE/Moveset source remains in-tree for a future **verified** Release rebuild; until then install sources must stay at official/known-good sizes.
