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

## Logs

Open **Help** tab → **Open Logs Folder**, or `%AppData%\SMSO\logs\`
