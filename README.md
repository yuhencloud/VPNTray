# VPNTray

A Windows 10/11 system-tray app that wraps [OpenConnect](https://www.infradead.org/openconnect/) for Cisco AnyConnect-compatible VPNs, generates the TOTP one-time code in-app, and lives quietly in the notification area. Built with .NET 8 (WPF). This is the Windows port of the macOS [VPN MenuBar](https://github.com/CoderZCC/VPNMenuBar) app.

> ⚠️ **This is a generic OpenConnect front-end.** The gateway, server certificate pin, password, and TOTP secret are **placeholders / blanks** — there are no real defaults baked in. You provide your own VPN's hostname, certificate pin, password, and TOTP secret during the Onboarding wizard (or later in Settings). They are usually given to you by your VPN administrator.

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
| `vpnc-script-win--no-dns.js` | The same script with its **DNS-configuration block disabled**. Used in the default DNS mode, *Send every name to public DNS*: the VPN never touches system DNS, so downloads and public hosts keep working out of the box. The trade-off is that internal names (`*.corp…`) do not resolve until you switch to internal DNS under Settings → DNS. |

### DNS and the split-DNS rules

`vpnc-script-win.js` ignores the gateway's `CISCO_SPLIT_DNS` list: it pins the internal
resolvers onto the tunnel adapter, whose metric beats Wi-Fi's, so *every* lookup goes to the
internal DNS. On a split tunnel that breaks public names — the internal resolver answers them
with in-cloud addresses (100.64.0.0/10) that the default route then sends out the physical NIC,
where they black-hole.

VPNTray keeps the internal DNS (so `*.corp…` resolves) and can carve individual public suffixes
back out with NRPT rules pointing at a public resolver, which is what AnyConnect does natively.
The list lives under **Settings → DNS → Advanced — public-DNS exceptions** and **ships empty**:
which names a given gateway's resolver gets wrong is a property of that deployment, and an NRPT
rule is machine-wide, so a shipped default would redirect somebody's lookups to a third party
for a problem they may not have.

Add a suffix only after confirming the breakage, by comparing the two answers:

```powershell
Resolve-DnsName name.example.com -Server <internal resolver>   # from the tunnel adapter
Resolve-DnsName name.example.com -Server 8.8.8.8
```

An internal answer in a range the tunnel does not route (e.g. `100.64.0.0/10`) is the signal.
Matching answers mean no exception is needed.

Rules are tagged `vpntray_split_dns` and removed on disconnect, on app exit, and whenever
openconnect dies — inspect them with `Get-DnsClientNrptRule`.

See [`src/VPNTray/Resources/README.md`](src/VPNTray/Resources/README.md) for sources, versions, licenses, and how to swap in a newer openconnect. The `.csproj` bundles whatever is in `Resources/` next to the built exe.

## Build from source

```powershell
git clone <this-repo-url>
cd VPNTray
dotnet build -c Release
```

The built app lands in `src/VPNTray/bin/Release/net8.0-windows/`, with the bundled native binaries already copied into its `Resources/` subfolder next to `VPNTray.exe`.

Run `VPNTray.exe`. Because the app is marked `requireAdministrator`, Windows will prompt for elevation (UAC) on every launch — accept it.

### Building the installer

```powershell
.\scripts\build-installer.ps1
```

Publishes self-contained (no .NET needed on the target machine) and packages it with WiX 5 into
two outputs:

| Output | Use |
| ------ | --- |
| `dist/VPNTray-Setup-<version>-x64.exe` | **Ship this.** A Burn bundle that elevates and then runs the MSI from the elevated process, in the user's own session — the only context that can ask a running, `requireAdministrator` VPNTray to close (see the comment block in `installer/bundle.wxs`). It shows the MSI's own wizard, not a second one. |
| `dist/VPNTray-Setup-<version>-x64.msi` | The package itself, for deployment tooling that wants an MSI. Installing it directly cannot close a running VPNTray unless msiexec is launched from an already-elevated prompt. |

Needs the WiX CLI and three extensions:

```powershell
dotnet tool install --global wix
wix extension add --global WixToolset.UI.wixext/5.0.2
wix extension add --global WixToolset.Util.wixext/5.0.2
wix extension add --global WixToolset.BootstrapperApplications.wixext/5.0.2
```

The bootstrapper extension is the one WiX 4 called `WixToolset.Bal.wixext`; asking for that
name on WiX 5 installs an assembly whose name no longer matches and `wix extension list`
reports it as `(damaged)`.

Pin the extension versions to the `wix` CLI's own major version — asking for them unpinned
resolves to the newest release, which for a v5 CLI fails with `WIX6101: Could not find expected
package root folder wixext5`.

The version comes from `<Version>` in `src/VPNTray/VPNTray.csproj` and is the only place to
bump it — the script feeds it to the MSI's `ProductVersion` and to the output file name, and
the app reads the same stamp for its title bar.

## First launch

1. Double-click `VPNTray.exe`. **Accept the UAC prompt.** The app starts elevated, and a tray icon appears in the notification area.
2. A single **Credentials window** opens automatically on first run. Fill in all five fields — username, password, TOTP secret (Base32), gateway, and server certificate pin — then click **Save** (the window stays open).
3. Use the **Connect** button at the top of the window to bring up the VPN right there — it shows **Connecting…**, then **Disconnect** once connected. No need to go to the tray. The tray menu's Connect / Disconnect / Reconnect still work too.

Reopen the window any time with a **single left-click** on the tray icon, or **right-click → Open Settings…**. Until setup is complete, the tray menu only shows **Open Settings…** and **Quit**.

A **Tools** section at the bottom checks the bundled native tools (openconnect / wintun / vpnc script) as soon as the window opens; its header carries the verdict, red when something failed. Expand it for the per-check detail and a **Re-check** button. The tools ship with the app, so this only matters when a connection fails.

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

- **Launch at login** — registers a **Scheduled Task** that runs with highest privileges, so the app starts silently and already elevated at logon (no UAC prompt at startup). Toggle it under **Settings → Startup**. That task launches the app with `--background`, which is the one route in that stays tray-only; every other route — the installer's *Launch VPNTray*, the Start menu, the exe itself — opens the window, because answering a deliberate launch with nothing but a tray icon reads as a failure. A nested **Connect automatically** switch (default off) makes that logon start also dial the VPN; manual launches never auto-connect.
- **Auto-disconnect / auto-reconnect** — if the network drops while connected, the VPN is dropped cleanly; when the network returns, it reconnects automatically **only if you connected manually**. Failed connects do not loop-retry.
- **Disconnect on quit** — every exit path disconnects the VPN first, so no orphaned `openconnect.exe` is left running.

## Configuration & privacy

- **Location:** `%APPDATA%\VPNTray\config.json`
- **Permissions:** the file is ACL-locked to the current user (Windows equivalent of `chmod 0600`).
- **Contents:** plain JSON — username, password, TOTP Base32 secret, gateway, certificate pin, and the openconnect / vpnc-script paths.
- **Never uploaded** — it stays entirely on your machine.

> ⚠️ **Never commit `config.json` to git or share it.** The TOTP secret is the seed for your one-time codes and is account-specific. Each user should obtain their own secret from the VPN admin and run Onboarding themselves rather than copying someone else's config.

## Uninstall

1. **Quit** the app from the tray menu (this disconnects any active VPN).
2. If you enabled Launch at login, remove the Scheduled Task (switch it off under Settings → Startup, or delete it from Task Scheduler).
3. Delete the app folder (the `bin\Release\net8.0-windows` directory you ran from).
4. Delete your config and secrets: remove `%APPDATA%\VPNTray`.

## FAQ

**Connection immediately fails.**
The native binaries are bundled, so this normally just works. If a file under `Resources/` is missing or corrupt (see [Native binaries](#native-binaries)), reinstall or rebuild the app.

**Authentication fails.**
Re-check your password and TOTP secret in Settings. The gateway authenticates in two stages — password first, then a separate one-time code — and either one being wrong reports the same failure. The 6-digit code is derived from the secret, so a wrong secret means a wrong code.

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
