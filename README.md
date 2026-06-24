# VPNTray

A Windows 10/11 system-tray app that wraps [OpenConnect](https://www.infradead.org/openconnect/) for Cisco AnyConnect-compatible VPNs, generates the TOTP one-time code in-app, and lives quietly in the notification area. Built with .NET 8 (WPF). This is the Windows port of the macOS [VPN MenuBar](https://github.com/CoderZCC/VPNMenuBar) app.

> ⚠️ **This is a generic OpenConnect front-end.** The gateway, server certificate pin, password prefix, and TOTP secret are **placeholders / blanks** — there are no real defaults baked in. You provide your own VPN's hostname, certificate pin, password prefix, and TOTP secret during the Onboarding wizard (or later in Settings). They are usually given to you by your VPN administrator.

> 📖 For a detailed step-by-step walkthrough (UAC, onboarding, extracting the TOTP secret, daily use, FAQ), see **[INSTALL.md](INSTALL.md)** (中文 / Chinese).

## What it is

- **Tray app** — sits in the Windows notification area. A status dot shows the connection state at a glance.
- **TOTP built in** — generates the RFC 6238 / HMAC-SHA1 6-digit code from your Base32 secret. The VPN password is a fixed prefix + the current 6-digit code, fed to `openconnect` over stdin. You never type the code by hand.
- **Runs elevated** — the whole app is marked `requireAdministrator`. Launching shows **one** UAC prompt; after that, connecting, disconnecting, and route changes need no further elevation. (This is the Windows analog of the mac app's `sudo NOPASSWD` rule — you approve elevation once at launch instead of per action.)
- **Automatic behaviors** — launch at login, auto-disconnect when the network drops and auto-reconnect when it returns, and disconnect-on-quit so no orphaned `openconnect` process is ever left behind.

## Requirements

- **Windows 10 or 11, x64**
- **.NET 8 Desktop Runtime** (to run a framework-dependent build) — or build a self-contained release.
- The four **native binaries** below, placed in the app's `Resources/` folder (see [Native binaries](#native-binaries) — these are **not** shipped in this repo).
- Administrator rights on the machine (the app requests elevation at launch).

## Native binaries

VPNTray loads four native files from the `Resources/` folder next to `VPNTray.exe`:

| File | Where it comes from |
| ---- | ------------------- |
| `openconnect.exe` | An official **OpenConnect Windows build, v9.x or newer**. v9.x bundles the Wintun driver, so no separate driver install is needed. |
| `wintun.dll` | Ships alongside `openconnect.exe` in the same OpenConnect v9.x build. |
| `vpnc-script-win.js` | The default VPN-configuration script bundled with that OpenConnect build. |
| `vpnc-script-win--no-dns.js` | A copy of `vpnc-script-win.js` with its **DNS-configuration section removed**. Used when "Skip DNS modification" is enabled so VPNTray does not touch your system DNS settings. |

> ⚠️ **These four files are intentionally NOT committed to this repository.** Until all four are present in `Resources/`, the in-app dependency check shows red and connections will not work.

**To obtain them:**

1. Download an official OpenConnect Windows build (v9.x or newer).
2. Copy `openconnect.exe`, `wintun.dll`, and `vpnc-script-win.js` into `Resources/`.
3. Make a copy of `vpnc-script-win.js` named `vpnc-script-win--no-dns.js`, open it, and remove (or comment out) the DNS-configuration block.

See [`src/VPNTray/Resources/README.md`](src/VPNTray/Resources/README.md) for the full description. The build still succeeds when these files are absent — only runtime VPN connectivity is affected.

## Build from source

```powershell
git clone <this-repo-url>
cd VPNTray
dotnet build -c Release
```

The built app lands in `src/VPNTray/bin/Release/net8.0-windows/`. Drop your four native binaries into the `Resources/` subfolder next to `VPNTray.exe` (the `.csproj` copies whatever is in `src/VPNTray/Resources/` to the output on build, so you can also place them there before building).

Run `VPNTray.exe`. Because the app is marked `requireAdministrator`, Windows will prompt for elevation (UAC) on every launch — accept it.

## First launch

1. Double-click `VPNTray.exe`. **Accept the UAC prompt.** The app starts elevated, and a tray icon appears in the notification area.
2. The **Onboarding wizard** opens automatically on first run:
   1. **Welcome**
   2. **Dependency Check** — verifies the four native binaries are present in `Resources/`. Fix any red rows before continuing.
   3. **Credentials** — username, password prefix, TOTP secret (Base32), gateway, and server certificate pin.
   4. **Done**
3. Once setup is complete, the full tray menu becomes available.

Until setup is complete, the tray menu only shows **Check Dependencies…** and **Quit**.

## Extracting your TOTP Base32 secret

Your VPN administrator typically gives you a QR-code image. VPNTray needs the **Base32 secret text** inside that QR code (not a registered Authenticator app). Extract it locally — do **not** use an online QR decoder, which would upload your VPN credential to a stranger.

- **zbar (recommended, fully local):** install [zbar](https://github.com/mchehab/zbar), then run `zbarimg vpn-qr.png`. The output is an `otpauth://totp/...?secret=ABC123...&issuer=...` URI — the value between `secret=` and `&` is your Base32 secret.
- **Phone camera:** point your phone camera at the QR code; it surfaces the full `otpauth://` URL. Copy the `secret=` portion and send it to your PC.
- **Password managers:** if you already store the TOTP in 1Password (one-time password field → reveal `otpauth://` URI) or Bitwarden (the "Authenticator key" field is the Base32 secret), copy it from there.

> ⚠️ **Google Authenticator and Microsoft Authenticator do NOT show the secret** — they deliberately hide it. If your secret only lives there with no backup, ask your admin to re-issue a QR code and decode it with zbar.

A valid Base32 secret is uppercase letters and digits 2–7, usually 16–32 characters. **Use your own — never someone else's.**

## Daily use

Click the tray icon for the menu:

| Menu item | Action |
| --------- | ------ |
| **Connect** | Generate the TOTP and bring up the VPN tunnel. |
| **Disconnect** | Tear down the tunnel (and clear the auto-reconnect intent). |
| **Reconnect** | Disconnect then connect again. |
| **Open Settings…** | Edit all fields, including Advanced (paths, Skip DNS modification, Launch at login). |
| **Check Dependencies…** | Re-run the native-binary dependency check. |
| **Quit** | Exit the app (disconnecting first if connected). |

The status dot on the tray icon shows state:

- 🟢 **green** — connected
- 🟠 **orange** — connecting
- 🔴 **red** — failed
- ⚫ **grey** — disconnected

## Automated behaviors

- **Launch at login** — registers a **Scheduled Task** that runs with highest privileges, so the app starts silently and already elevated at logon (no UAC prompt at startup). Toggle it in Settings → Advanced.
- **Auto-disconnect / auto-reconnect** — if the network drops while connected, the VPN is dropped cleanly; when the network returns, it reconnects automatically **only if you connected manually**. Failed connects do not loop-retry.
- **Disconnect on quit** — every exit path disconnects the VPN first, so no orphaned `openconnect.exe` is left running.

## Configuration & privacy

- **Location:** `%APPDATA%\VPNTray\config.json`
- **Permissions:** the file is ACL-locked to the current user (Windows equivalent of `chmod 0600`).
- **Contents:** plain JSON — username, password prefix, TOTP Base32 secret, gateway, certificate pin, and the openconnect / vpnc-script paths.
- **Never uploaded** — it stays entirely on your machine.

> ⚠️ **Never commit `config.json` to git or share it.** The TOTP secret is the seed for your one-time codes and is account-specific. Each user should obtain their own secret from the VPN admin and run Onboarding themselves rather than copying someone else's config.

## Uninstall

1. **Quit** the app from the tray menu (this disconnects any active VPN).
2. If you enabled Launch at login, remove the Scheduled Task (Settings → Advanced toggle off, or delete it from Task Scheduler).
3. Delete the app folder (the `bin\Release\net8.0-windows` directory you ran from).
4. Delete your config and secrets: remove `%APPDATA%\VPNTray`.

## FAQ

**Connection immediately fails / the dependency check is red.**
Make sure all four native binaries are present in `Resources/` (see [Native binaries](#native-binaries)). Use **Check Dependencies…** to re-verify.

**Authentication fails.**
Re-check your TOTP secret and password prefix in Settings. The 6-digit code is derived from the secret, so a wrong secret means a wrong code.

**Server certificate verification fails.**
Your admin probably rotated the certificate. Update the **server cert pin** in Settings.

**Do I get a UAC prompt every time?**
You get one at manual launch. When the app starts via the Launch-at-login Scheduled Task it starts elevated silently with no prompt.

**Can I connect two VPNs at once?**
No — VPNTray manages a single `openconnect` connection.

## Not yet (out of scope for v1)

- Auto-update.
- QR-code **image** import of the TOTP secret (for now, extract the Base32 text and paste it).

## License / contributions

This is a Windows port of [VPN MenuBar](https://github.com/CoderZCC/VPNMenuBar). Issues and pull requests welcome.
