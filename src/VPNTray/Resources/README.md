# Resources — bundled native binaries

These files are loaded at runtime from the app's `Resources/` folder (see
`Dependencies/BundledBinaries.cs`) and copied next to the built exe via the
wildcard `<None Include="Resources\*.exe|*.dll|*.js" CopyToOutputDirectory>`
items in `VPNTray.csproj`.

## What's here and where it came from

| File(s) | Source | Version | License |
|---|---|---|---|
| `openconnect.exe` + the `lib*.dll` / `iconv.dll` / `zlib1.dll` set | Official OpenConnect MinGW64/GnuTLS Windows build (the `openconnect-installer-MinGW64-GnuTLS` payload, installed via the Chocolatey `openconnect` package) | OpenConnect **v9.12** (GnuTLS 3.8.6; anyconnect/DTLS/ESP) | LGPL 2.1 |
| `wintun.dll` | The Wintun driver bundled inside that same OpenConnect v9.12 build (Wintun 0.14, WireGuard LLC) | 0.14 | Prebuilt redistributable |
| `vpnc-script-win.js` | The script bundled inside the v9.12 build | matches v9.12 | GPL/heuristic script |
| `vpnc-script-win--no-dns.js` | a copy of `vpnc-script-win.js` with the DNS-add block disabled (its `if (env("INTERNAL_IP4_DNS"))` guard changed to `if (false)`) — used when **Skip DNS modification** is on (the default) | — | — |

> ⚠️ Wintun support in openconnect is marked **experimental** (openconnect prints
> a warning on connect). It works in practice; if you hit instability, the
> alternative is to install the TAP-Windows driver. The v9.12 build here uses
> Wintun and connects successfully.

> ⚠️ All `lib*.dll`, `iconv.dll`, and `zlib1.dll` are **runtime dependencies of
> `openconnect.exe`** and must stay in this folder next to it, or openconnect
> won't launch. The DLL set is version-matched to this openconnect build — do
> not mix with another build's DLLs.

### History / note

An earlier attempt bundled a community **v8.10** MinGW build (horar/openconnect);
that build's Wintun support did not work on Windows 11 ("Set up tun device
failed"). The official **v9.12** build above creates the Wintun adapter and
connects, so it replaced it.

## Architecture / platform

The app targets **x64**; `openconnect.exe`, its DLLs, and `wintun.dll` are all
**amd64**. They run on 64-bit Windows 10/11.

## Updating openconnect

Replace `openconnect.exe` + its DLL set with a newer MinGW64 build (e.g. from a
newer `choco install openconnect` / the official installer), refresh
`vpnc-script-win.js` from that build, and regenerate `vpnc-script-win--no-dns.js`
by disabling its DNS-add block the same way. Keep the exact filenames —
`BundledBinaries.cs` resolves them by name.
