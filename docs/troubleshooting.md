# SMSO Troubleshooting

## Cannot connect

- Verify host forwarded **TCP and UDP** port 27015
- Check firewall allows SMSO.Launcher.exe
- Confirm server is running (host clicked Host Server, or ServerHost.exe is running)
- After updating BSMSO: replace **launcher + disc modules (`_BSMSO.kxe`) + dedicated `BSMSO.ServerHost.exe`** together. A VersionMismatch reject means builds differ — the log shows client vs server ModBuildId
- Launcher update banner: at startup the app reads bundled `latest-build.json` from the release zip (beside `BSMSO.Launcher.exe`, or under `assets/`). If that file’s `modBuildId` is higher than the running launcher, Launch / Host / Connect are disabled until you install the latest zip. No GitHub fetch by default. Optional HTTP override: env `BSMSO_UPDATE_MANIFEST_URL` or config `UpdateManifestUrl`. Zip packaging rewrites `assets/latest-build.json` from the current ModBuildId.
- Split product labels (same build): `.\tools\package-split.ps1` produces `BSMSO_1.1.zip` and `BSMSO_2.0.zip` from one publish (same ModBuildId). The launcher UI version string comes from that zip’s `versionLabel`.
- First connect right after Host / rehost can briefly fail while the listener binds; the launcher retries automatically — wait a second and try again if needed
- "Port already in use": stop any leftover `BSMSO.ServerHost.exe` or old launcher host, then Host again. BSMSO binds exclusively — two hosts cannot silently share the same port. Steam and other apps may also use 27015 — change the port in Settings if needed.
- "Could not finish hosting … local join failed": the port bound, but the host's own local join hiccuped — wait a second and Host again (this is not a Radmin/VPN bind problem).
- Over Radmin: if Host succeeds but friends get Connect failed, they are usually on the wrong IP (use Radmin `26.x`) or blocked by firewall — see `docs/network-setup.md`.
- After Disconnect / rehost, join heals force a full progress snapshot (client seq 0) so reconnects do not soft-skip ownership

## Username rejected

- Names must be 3–16 characters, alphanumeric + underscore
- Duplicate names are rejected with "NameTaken"
- After a crash/disconnect, wait up to ~30s (reconnect window) or pick a different name if NameTaken persists

## Black screen after Install / Launch

- Prefer Fast Disc Speed (`FastDiscSpeed=True` / Emulate Disc Speed **OFF**). With **Apply recommended Dolphin settings** on, Launch enables it; with that toggle off, your Dolphin disc-speed choice is left alone. Emulate Disc Speed ON can hang BSE CARD + DVD init on many PCs after Kuribo finishes loading.
- Confirm dolphin.log shows `Loaded module` for BetterSunshineEngine (~583744), optional Moveset (~46976), and `_BSMSO.kxe` with no `Unknown instruction`.
- Wrong-size DEBUG BSE (~603424) or broken Moveset (~44992) black-screens immediately — re-run **Install / patch modules** from build **81+** (size checks block bad payloads).
- After updating the launcher, Launch Dolphin once so GMSE90.ini is rewritten, then restart Dolphin if it was already open.

## Bridge not attached

- Launch Dolphin before connecting
- Run game until main menu or in-game (mailbox magic must appear)
- Try running launcher as Administrator if ReadProcessMemory fails

## Warp does nothing

- Warps blocked during loading — wait until in-game
- Verify course/episode exists in level database

## Dolphin crash on connect

- Do not load SMSCoop with SMSO
- Ensure `_SMSO.kxe` / `_BSMSO.kxe` is built for your BSE version (re-run module install after zip update)

## Windows SmartScreen / antivirus warnings

BSMSO reads and writes Dolphin's memory to sync multiplayer state. That pattern is common in game trainers and can trigger **false positives** from Defender or SmartScreen on unsigned builds.

- Prefer the published build from `dist/launcher` after running `tools/publish.ps1`
- Verify SHA-256 hashes in `dist/CHECKSUMS.txt` match your download
- SmartScreen **"Unknown publisher"** is expected until the project is code-signed; set `CODESIGN_PFX` when publishing to sign releases
- If attach fails, try **Run as administrator** — the launcher does not require admin by default
- You may add a Defender exclusion for the BSMSO install folder; do not disable antivirus globally

## Logs

Open **Help** tab → **Open Logs Folder**, or `%AppData%\SMSO\logs\`
