# SMSO Troubleshooting

## Cannot connect

- Verify host forwarded **TCP and UDP** port 27015
- Check firewall allows SMSO.Launcher.exe
- Confirm server is running (host clicked Host Server, or ServerHost.exe is running)

## Username rejected

- Names must be 3–16 characters, alphanumeric + underscore
- Duplicate names are rejected with "NameTaken"

## Bridge not attached

- Launch Dolphin before connecting
- Run game until main menu or in-game (mailbox magic must appear)
- Try running launcher as Administrator if ReadProcessMemory fails

## Warp does nothing

- Warps blocked during loading — wait until in-game
- Verify course/episode exists in level database

## Dolphin crash on connect

- Do not load SMSCoop with SMSO
- Ensure `_SMSO.kxe` is built for your BSE version

## Windows SmartScreen / antivirus warnings

BSMSO reads and writes Dolphin's memory to sync multiplayer state. That pattern is common in game trainers and can trigger **false positives** from Defender or SmartScreen on unsigned builds.

- Prefer the published build from `dist/launcher` after running `tools/publish.ps1`
- Verify SHA-256 hashes in `dist/CHECKSUMS.txt` match your download
- SmartScreen **"Unknown publisher"** is expected until the project is code-signed; set `CODESIGN_PFX` when publishing to sign releases
- If attach fails, try **Run as administrator** — the launcher does not require admin by default
- You may add a Defender exclusion for the BSMSO install folder; do not disable antivirus globally

## Logs

Open **Help** tab → **Open Logs Folder**, or `%AppData%\SMSO\logs\`
