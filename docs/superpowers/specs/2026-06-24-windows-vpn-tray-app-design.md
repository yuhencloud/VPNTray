# Windows VPN Tray App — Design Spec

**Date:** 2026-06-24
**Status:** Approved (design), pending implementation plan
**Working name:** VPNTray
**Reference:** `../../../VPNMenuBar` (macOS Swift app this ports)

## 1. Purpose

A Windows 10/11 system-tray app that wraps `openconnect` to connect to a Cisco
AnyConnect-compatible VPN. It generates a TOTP one-time code (RFC 6238) from a
user-supplied Base32 secret, prepends a fixed password prefix, and feeds
`prefix + TOTP` to `openconnect` over stdin. It lives in the notification area,
manages the long-running VPN subprocess, and auto-reconnects on network changes.

This is a functional port of the macOS `VPNMenuBar` app. It is a **generic
OpenConnect front-end**: `gateway`, `serverCertPin`, `passwordPrefix`, and
`totpSecret` are all user-supplied (the user's own VPN values), never hardcoded.

## 2. Decisions (locked)

| Decision | Choice | Rationale |
|---|---|---|
| Tech stack | **C# / .NET 8** | Native tray support, small footprint, single-folder distribution, natural UAC handling |
| UI framework | **WPF** + `Hardcodet.NotifyIcon.Wpf` for the tray | Modern windows for Settings/Onboarding; Hardcodet is the de-facto tray library |
| Privilege model | **Whole app runs elevated** (`requireAdministrator` in `app.manifest`) | One UAC prompt at launch; connect/disconnect/route ops need no further elevation. Closest to mac's one-time NOPASSWD authorization |
| openconnect provisioning | **Bundled** (`openconnect.exe` + `wintun.dll` + `vpnc-script-win.js` in app dir) | Windows allows redistribution; sidesteps the entire dependency-installer problem. Simpler than mac's Homebrew flow |
| Scope | **Core + common enhancements** | Tray, connect/disconnect, TOTP, settings, config persistence, onboarding, dependency check, auto-reconnect, startup-at-login, status notifications |

## 3. Architecture

Strict one-way dependency graph, mirroring the mac app:

```
 UI  →  Core  →  Config / Dependencies
  \
   → Util
```

- App runs as a single elevated process (single-instance mutex prevents a second copy).
- `VpnController` is the state machine; all UI binds to its `INotifyPropertyChanged` state.
- The long-running `openconnect.exe` subprocess is abstracted behind
  `IOpenConnectProcessRunning` so it is testable in isolation.

### 3.1 Component map (mac → Windows)

| macOS (Swift) | Windows (C#) | Responsibility |
|---|---|---|
| `VPNConfig` + `ConfigStore` | `VpnConfig` + `ConfigStore` | JSON persistence to `%APPDATA%\VPNTray\config.json`, ACL-restricted to current user (0600 equivalent), atomic write, broken-file backup on decode error |
| `VPNState` | `VpnState` | 4-state: `Disconnected`, `Connecting`, `Connected(since)`, `Failed(reason)`; `IsSetupIncomplete` helper |
| `TOTPGenerator` (CryptoKit) | `TotpGenerator` (`HMACSHA1` + own Base32 decoder) | RFC 6238 / RFC 4226, 30s step, 6 digits, dynamic truncation |
| `ProcessRunning` + `SystemProcessRunner` | `IProcessRunner` + `SystemProcessRunner` | One-shot subprocess wrapper (exit code + stdout + stderr) |
| `OpenConnectProcess(Running)` | `OpenConnectProcess` + `IOpenConnectProcessRunning` | Long-running `openconnect.exe` wrapper; drains stderr; `WaitForHandshakeAsync(timeout)` judges success |
| `NetworkMonitoring` + `NetworkMonitor` | `INetworkMonitor` + `NetworkMonitor` (`NetworkChange.NetworkAvailabilityChanged` / `NetworkAddressChanged`) | Network reachability changes |
| `VPNController` (@MainActor ObservableObject) | `VpnController` (`INotifyPropertyChanged`) | State machine; owns `ShouldAutoReconnect` intent flag; auto-disconnect/reconnect; post-connect watchdog |
| `ArchDetector` / `DependencyChecker` / `DependencyInstaller` | `BundledBinaries` + `DependencyChecker` | Resolve bundled binary paths; verify `openconnect.exe` exists and runs `--version`; verify wintun present |
| UI views | `TrayIconController`, `SettingsWindow`, `OnboardingWindow`, `StatusIconFactory` | Tray + windows |
| `LoginItemManager` (SMAppService) | `StartupManager` (Task Scheduler, highest privileges) | Launch-at-login that starts elevated silently |
| `AppLogger` | `AppLogger` | File log; **never logs secrets/passwords** |
| `VPNMenuBarApp` + `AppCoordinator` + `AppDelegate` | `App.xaml(.cs)` + `AppCoordinator` | `@main` equivalent; owns singleton controller, hosts windows, disconnects VPN on every quit path |

### 3.2 Project layout

```
VPNTray/
  src/VPNTray/
    App/            App.xaml, App.xaml.cs, AppCoordinator.cs, app.manifest
    Config/         VpnConfig.cs, ConfigStore.cs
    Core/           VpnState.cs, TotpGenerator.cs, IProcessRunner.cs,
                    SystemProcessRunner.cs, IOpenConnectProcessRunning.cs,
                    OpenConnectProcess.cs, INetworkMonitor.cs, NetworkMonitor.cs,
                    VpnController.cs
    Dependencies/   BundledBinaries.cs, DependencyChecker.cs
    UI/             TrayIconController.cs, SettingsWindow.xaml(.cs),
                    OnboardingWindow.xaml(.cs), StatusIconFactory.cs
    Util/           AppLogger.cs, StartupManager.cs, SingleInstance.cs
    Resources/      openconnect.exe, wintun.dll, vpnc-script-win.js,
                    vpnc-script-win--no-dns.js, app icons
  src/VPNTray.Tests/  (TotpGenerator, VpnController via fakes)
  docs/superpowers/specs/  this file
```

## 4. Data flow — connect

1. `Connect()` guards on `config.IsConfigured` (all 5 required fields non-empty).
2. Runs `DependencyChecker.Check()` on a background thread (probes block briefly,
   must not freeze UI thread).
3. (Best-effort) stale host-route cleanup for the gateway — see §6.
4. Generates `password = passwordPrefix + TotpGenerator.Code(totpSecret)`.
5. Spawns:
   ```
   openconnect.exe --script <vpncScriptPath> --user <username>
                   --passwd-on-stdin --servercert <serverCertPin> <gateway>
   ```
   `wintun.dll` sits next to `openconnect.exe` and is auto-loaded. Password is
   written to stdin then stdin closed. Because the whole app is elevated, no
   `sudo`/`runas` wrapper is needed.
6. `WaitForHandshakeAsync(timeout: 5s)` polls captured stderr:
   - **success markers**: `Connected as`, `CSTP connected`, `Established DTLS`,
     `ESP session established` (same set as mac), plus "process still alive after
     grace period".
   - **failure markers**: `Login failed`, `authentication failure`,
     `Certificate verification failure`, etc. → mapped to friendly reasons.
   - process exited during handshake → failure (tail of stderr).
7. On success: `state = Connected`, `ShouldAutoReconnect = true`. On failure:
   state = `Failed(reason)`, flag untouched (no spin-loop retry on bad config).
8. `StartMonitoring()` watchdog polls `IsRunning()` every 2s; if the child
   disappears, transitions to `Disconnected` and posts a tray balloon notification.

### 4.1 Disconnect / quit

- `Disconnect()` clears `ShouldAutoReconnect` (user intent = stay disconnected),
  then `Process.Kill(entireProcessTree: true)` on the owned child.
- `AppCoordinator` disconnects the VPN on **every** quit path (tray Quit,
  process exit) before terminating — no orphaned `openconnect.exe`.

### 4.2 Network-change handling

- unreachable + `Connected` → `AutoDisconnect()` (preserves `ShouldAutoReconnect`
  so recovery can retry).
- reachable + `Disconnected` + `ShouldAutoReconnect` → `Connect()`.
- Auto-reconnect only flips on a **successful** handshake, so wrong creds /
  missing deps never trigger a retry loop.

## 5. Windows-specific behavior

- **Bundled binaries**: `BundledBinaries` resolves `openconnect.exe`,
  `wintun.dll`, `vpnc-script-win.js` relative to the app base directory.
  `DependencyChecker` verifies the exe exists and `--version` succeeds, and that
  `wintun.dll` is present next to it.
- **Skip-DNS-modification toggle**: bundle a modified `vpnc-script-win--no-dns.js`
  that does not alter system DNS. When `config.SkipDNSModification == true`, the
  `--script` arg points at the no-DNS copy (mirrors mac's bundled
  `vpnc-script--no-dns`). Default on.
- **Startup at login**: `StartupManager` registers a Scheduled Task with "run
  with highest privileges" so the app starts elevated silently at logon (a plain
  `HKCU\...\Run` entry cannot auto-elevate and would hit UAC each boot).
  The on/off preference is stored **outside** `VpnConfig` (a dedicated registry
  value under `HKCU\Software\VPNTray`), default-on — mirroring mac's choice to
  keep it in UserDefaults to avoid a config schema migration. Source of truth is
  the actual Scheduled Task; the registry value is the user's intent.
- **Single instance**: named `Mutex` at startup; second launch surfaces the
  existing instance and exits.

## 6. Stale host-route cleanup (enhancement, best-effort)

On macOS a leftover host route to the gateway (pinned to a previous WiFi gateway)
breaks reconnects after roaming. Windows + wintun has a different routing model,
so this is **lower priority**: implement a best-effort `route delete` of a stale
host route to the gateway IP before connect, gated on the same safety check the
mac app uses (only delete a concrete host route, never a default route). If it
proves unreliable on Windows it degrades to a no-op and openconnect surfaces its
native error. Failures are logged only, connect proceeds.

## 7. Error handling

stderr markers map to friendly messages (same taxonomy as mac):

| Marker | User-facing reason |
|---|---|
| `Login failed` / `authentication failure` | Authentication failed — check your TOTP secret or password prefix. |
| `Certificate verification failure` | Server certificate verification failed — the gateway cert may have rotated; update the pin in Settings. |
| process exited / timeout | Tail of stderr (last ~200 chars) or "Handshake timeout". |

Full stderr is dumped to the log file for diagnosis; the UI reason stays short.

## 8. Config & security

- Location: `%APPDATA%\VPNTray\config.json`, ACL-restricted to the current user
  (Windows equivalent of 0600). Atomic write (temp + move). On decode error,
  back up the broken file and start fresh.
- Schema (`VpnConfig`): `Username`, `PasswordPrefix`, `TotpSecret`, `Gateway`,
  `ServerCertPin`, `OpenconnectPath`, `VpncScriptPath`, `SkipDNSModification`
  (default true), `SchemaVersion`.
- `IsConfigured` requires the 5 user fields non-empty.
- Fields are sanitized on save (strip zero-width / control / NBSP / ideographic
  space chars that break openconnect args) — port of mac's `sanitized()`.
- **Secrets never logged.** TOTP secret and assembled password are kept out of
  all log output and never echoed back into source/docs.

## 9. UI

- **Tray icon**: status badge with a colored dot — green (connected), orange
  (connecting), red (failed), grey (disconnected). Rendered by `StatusIconFactory`.
- **Tray menu**: Connect / Connecting… (disabled) / Disconnect / Reconnect,
  Open Settings…, Check Dependencies…, Quit. When setup is incomplete, only
  Status + Check Dependencies + Quit show (mac's lock-down behavior).
- **Onboarding wizard**: Welcome → Dependency Check → Credentials
  (username / password prefix / TOTP secret) + VPN server (gateway / cert pin) → Done.
- **Settings window**: edit all fields incl. Advanced (paths, skip-DNS toggle,
  launch-at-login). Save disabled until something changes; on save, reconnect if
  currently connected.

## 10. Out of scope for v1

- Sparkle / auto-update (macOS-specific) — deferred.
- QR-code image import for the TOTP secret — deferred (paste Base32 in Settings).
- Homebrew / sudoers in-app installers — not applicable (replaced by bundling).

## 11. Testing

- `TotpGenerator` — known-answer vectors (RFC 6238 test vectors), Base32 decode
  edge cases.
- `VpnController` — state transitions driven by a fake `IOpenConnectProcessRunning`
  and fake `INetworkMonitor` (connect success/failure, auto-disconnect/reconnect,
  no-retry-on-bad-config invariant).
- `ConfigStore` — round-trip, atomic write, broken-file recovery.
- Manual smoke test against a real VPN for the actual openconnect handshake.
