# Resources — bundled native binaries

These files are loaded at runtime from the app's `Resources/` folder (see
`Dependencies/BundledBinaries.cs`) and copied next to the built exe via the
wildcard `<None Include="Resources\*.exe|*.dll|*.js" CopyToOutputDirectory>`
items in `VPNTray.csproj`.

## What's here and where it came from

| File(s) | Source | Version | License |
|---|---|---|---|
| `openconnect.exe` + the `lib*.dll` / `zlib1.dll` set | [horar/openconnect](https://github.com/horar/openconnect) prebuilt MinGW64 build (`openconnect-v8.10_MINGW64.zip`, SHA-512 verified) | OpenConnect **v8.10** (GnuTLS 3.6.13; anyconnect/DTLS/ESP) | LGPL 2.1 |
| `wintun.dll` | [wintun.net](https://www.wintun.net/) official build `wintun-0.14.1` (amd64) | Wintun 0.14.1 | Prebuilt redistributable (WireGuard LLC) |
| `vpnc-script-win.js` | the version-matched copy shipped inside the openconnect build above | matches v8.10 | GPL/heuristic script |
| `vpnc-script-win--no-dns.js` | a copy of `vpnc-script-win.js` with the DNS-configuration block disabled (its `if (env("INTERNAL_IP4_DNS"))` guard changed to `if (false)`) — used when **Skip DNS modification** is on (the default) | — | — |

The official OpenConnect project does not publish a standalone Windows CLI
binary — only the GUI installer — so `openconnect.exe` comes from the
well-known `horar/openconnect` community MinGW mirror. The download was
integrity-checked against the publisher's SHA-512.

> ⚠️ All `lib*.dll` and `zlib1.dll` are **runtime dependencies of
> `openconnect.exe`** and must stay in this folder next to it, or openconnect
> won't launch.

## Architecture / platform

The app targets **x64**; the bundled `openconnect.exe`, its DLLs, and
`wintun.dll` are all **amd64**. They run on 64-bit Windows 10/11.

## Updating openconnect

To move to a newer openconnect: replace `openconnect.exe` + the `lib*.dll`/
`zlib1.dll` set with a newer MinGW64 build (and refresh `vpnc-script-win.js`
from that build, then regenerate `vpnc-script-win--no-dns.js` by disabling its
DNS block the same way). Keep the exact filenames — `BundledBinaries.cs`
resolves them by name.
