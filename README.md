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
- Administrator rights on the machine (the app requests elevation at launch).

The native VPN binaries (openconnect + Wintun + scripts) are **already bundled** — see [Native binaries](#native-binaries). Nothing extra to install.

## Native binaries

The native files VPNTray needs at runtime are **committed in `src/VPNTray/Resources/`** and copied next to `VPNTray.exe` on build — the app is runnable out of the box:

| File | What it is |
| ---- | ---------- |
| `openconnect.exe` + its `lib*.dll` / `zlib1.dll` set | OpenConnect **v8.10** (anyconnect/DTLS/ESP), from the [horar/openconnect](https://github.com/horar/openconnect) prebuilt MinGW64 build, SHA-512 verified. |
| `wintun.dll` | Wintun **0.14.1** (amd64), official build from [wintun.net](https://www.wintun.net/). |
| `vpnc-script-win.js` | The VPN-configuration script, version-matched to the openconnect build. |
| `vpnc-script-win--no-dns.js` | The same script with its **DNS-configuration block disabled** — used when "Skip DNS modification" is enabled (the default) so VPNTray does not touch your system DNS settings. |

See [`src/VPNTray/Resources/README.md`](src/VPNTray/Resources/README.md) for sources, versions, licenses, and how to swap in a newer openconnect. The `.csproj` bundles whatever is in `Resources/` next to the built exe.

## Build from source

```powershell
git clone <this-repo-url>
cd VPNTray
dotnet build -c Release
```

The built app lands in `src/VPNTray/bin/Release/net8.0-windows/`, with the bundled native binaries already copied into its `Resources/` subfolder next to `VPNTray.exe`.

Run `VPNTray.exe`. Because the app is marked `requireAdministrator`, Windows will prompt for elevation (UAC) on every launch — accept it.

## First launch

1. Double-click `VPNTray.exe`. **Accept the UAC prompt.** The app starts elevated, and a tray icon appears in the notification area.
2. A single **Credentials window** opens automatically on first run. Fill in all five fields — username, password prefix, TOTP secret (Base32), gateway, and server certificate pin — then click **Save**.
3. Once setup is complete, the full tray menu becomes available.

Reopen the window any time with a **single left-click** on the tray icon, or **right-click → Open Settings…**. Until setup is complete, the tray menu only shows **Open Settings…** and **Quit**.

The window also has a **Check tools** button that verifies the bundled native tools (openconnect / wintun / vpnc script) — green checks mean all good. The tools ship with the app, so this is only for troubleshooting a failed connection.

## Importing your TOTP Base32 secret

Your VPN administrator typically gives you an `otpauth://` QR-code image. Two buttons below the TOTP field import it directly — **decoded entirely on your machine, never uploaded anywhere**:

- **Import QR image…** — pick the QR-code image file; the secret is recognized and filled in automatically.
- **Scan screen for QR** — if the QR code is visible on screen (email, chat, browser), the window hides itself, scans all monitors, and fills in the secret.

Both accept a full `otpauth://totp/...?secret=...` QR code as well as a QR code containing just a bare Base32 secret. You can also paste the secret manually.

> ⚠️ **Google Authenticator and Microsoft Authenticator do NOT show the secret** — they deliberately hide it. If your secret only lives there with no backup, ask your admin to re-issue a QR code and import it here.

A valid Base32 secret is uppercase letters and digits 2–7, usually 16–32 characters. **Use your own — never someone else's.**

## Daily use

**Left-click** the tray icon to open the main window; **right-click** for the menu:

| Menu item | Action |
| --------- | ------ |
| **Connect** | Generate the TOTP and bring up the VPN tunnel. |
| **Disconnect** | Tear down the tunnel (and clear the auto-reconnect intent). |
| **Reconnect** | Disconnect then connect again. |
| **Open Settings…** | Open the main window to edit the five credential fields. |
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

**Connection immediately fails.**
The native binaries are bundled, so this normally just works. If a file under `Resources/` is missing or corrupt (see [Native binaries](#native-binaries)), reinstall or rebuild the app.

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
