# Native binaries (user-supplied)

VPN connections will not work until **you** place the following four files in this
`Resources/` folder. They are intentionally **not** checked into the repository — you
must supply them from an official OpenConnect Windows build.

| File | Source / notes |
| ---- | -------------- |
| `openconnect.exe` | From an official OpenConnect **Windows build, v9.x or newer**. v9.x bundles Wintun, so no separate driver install is needed. |
| `wintun.dll` | Ships alongside `openconnect.exe` in the same OpenConnect v9.x build. |
| `vpnc-script-win.js` | The default VPN-configuration script bundled with that OpenConnect build. |
| `vpnc-script-win--no-dns.js` | A copy of `vpnc-script-win.js` with its **DNS-configuration section removed** (commented out / deleted). Used when "Skip DNS modification" is enabled so VPNTray does not touch the system DNS settings. |

## How to obtain them

1. Download an official OpenConnect Windows build (v9.x or newer — for example from the
   OpenConnect project's published Windows binaries).
2. Copy `openconnect.exe`, `wintun.dll`, and `vpnc-script-win.js` from that build into
   this folder.
3. Make a copy of `vpnc-script-win.js` named `vpnc-script-win--no-dns.js`, then open it
   and remove (or comment out) the DNS-configuration block so the script skips all DNS
   changes.

## Bundling

`VPNTray.csproj` uses wildcard copy items (`Resources\*.exe`, `Resources\*.dll`,
`Resources\*.js`, `Resources\README.md`). Whatever you drop into this folder is copied
next to the built `VPNTray.exe` under a `Resources\` subfolder, matching
`BundledBinaries.BaseDir`. The build still succeeds when these files are absent — only
runtime VPN connectivity is affected.
