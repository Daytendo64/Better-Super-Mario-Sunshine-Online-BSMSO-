# SMSO Network Setup

## Default Port

- **TCP 27015** — control (join, warp, roster, sync settings)
- **UDP 27015** — player snapshots (60 Hz)

Both must be forwarded for WAN play.

## Hosting

### Embedded (launcher)

1. Configure max players (2–10) in Settings
2. Click **Host Server**
3. Forward TCP+UDP 27015 on your router to your PC
4. Share your public IP with friends

### Dedicated server

```powershell
dist\server\SMSO.ServerHost.exe 27015
```

Forward the same ports to the machine running ServerHost.

## Connecting

1. Enter host IP and port in Settings
2. Choose a unique username (3–16 alphanumeric + underscore)
3. Launch Dolphin manually, then click **Connect**

## Firewall

Allow `SMSO.Launcher.exe` and `Dolphin.exe` through Windows Firewall for private and public networks when hosting.
