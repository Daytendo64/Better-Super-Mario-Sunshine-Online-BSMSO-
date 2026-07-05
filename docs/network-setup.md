# BSMSO Network Setup

## Default Port

- **TCP 27015** — control (join, warp, roster, sync settings)
- **UDP 27015** — player snapshots (60 Hz)

Both must be reachable between host and clients.

## Radmin VPN (recommended — no port forwarding)

Radmin VPN gives every player a virtual LAN address so the host does not need to open router ports.

1. Download and install [Radmin VPN](https://www.radmin-vpn.com/) on every PC.
2. **Host:** Radmin VPN → **Network** → **Create network** (name + password). Note your Radmin IP (usually `26.x.x.x`).
3. **Clients:** **Join network** with the host's name and password.
4. **Host:** In BSMSO, **Launch Dolphin** → **Host Server**. Share your **Radmin IP** and port `27015`.
5. **Clients:** Settings → Server IP = host's Radmin IP, port `27015` → **Launch Dolphin** → **Connect**.
6. Allow **BSMSO.Launcher.exe** and **Dolphin.exe** through Windows Firewall when prompted.

Do not use the host's public/Wi‑Fi IP when playing over Radmin — use the Radmin VPN IP only.

## Port forwarding (alternative)

For WAN play without VPN software:

1. Forward **TCP and UDP 27015** on the host's router to the host PC's local IP.
2. Share the host's **public IP** and port `27015`.
3. Clients enter that address in Settings.

## Hosting

### Embedded (launcher)

1. Configure max players (2–10) in Settings
2. Click **Host Server**
3. Ensure port 27015 is reachable (Radmin VPN or router forwarding)
4. Share the correct IP with friends (Radmin IP or public IP)

### Dedicated server

```powershell
dist\server\BSMSO.ServerHost.exe 27015
```

Forward the same ports to the machine running ServerHost (or run it on the same Radmin VPN network).

## Connecting

1. Enter host IP and port in Settings
2. Choose a unique username (3–16 alphanumeric characters + underscore)
3. Launch Dolphin manually, then click **Connect**

## Firewall

Allow `BSMSO.Launcher.exe` and `Dolphin.exe` through Windows Firewall for private and public networks when hosting.
