# Windows VPN Tray App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows system-tray app (C#/.NET 8, WPF) that wraps a bundled `openconnect.exe` to connect to a Cisco AnyConnect-compatible VPN, generating a TOTP one-time code and managing the connection — a functional port of the macOS `VPNMenuBar` app.

**Architecture:** Single elevated WPF process. `VpnController` (INotifyPropertyChanged) is the state machine; the UI binds to its `State`. The long-running `openconnect.exe` subprocess is hidden behind `IOpenConnectProcessRunning` for testability. Strict one-way dependency graph: `UI → Core → Config/Dependencies`, plus `UI → Util`.

**Tech Stack:** .NET 8, WPF, `Hardcodet.NotifyIcon.Wpf` (tray), `System.Text.Json` (config), `System.Security.Cryptography.HMACSHA1` (TOTP), xUnit (tests). Bundled native deps: `openconnect.exe`, `wintun.dll`, `vpnc-script-win.js`.

## Global Constraints

- Target framework: **.NET 8** (`net8.0-windows`), `<UseWPF>true</UseWPF>`.
- Platform: **Windows 10/11, x64**. App runs **elevated** (`app.manifest` → `requireAdministrator`).
- App name / namespace: **`VPNTray`**. Config dir: `%APPDATA%\VPNTray\`.
- Single instance only (named mutex).
- **Secrets (TOTP secret, assembled password) must never appear in logs** or be echoed into source/docs.
- TOTP: RFC 6238, HMAC-SHA1, 30-second step, 6 digits.
- openconnect success markers: `Connected as`, `CSTP connected`, `Established DTLS`, `ESP session established`. Failure markers: `Login failed`, `authentication failure`, `Certificate verification failure`.
- Config file is ACL-restricted to the current user, written atomically (temp + replace).
- Tests use fakes for `IOpenConnectProcessRunning`, `IProcessRunner`, `INetworkMonitor` — no real subprocess/network in unit tests.
- Commit after every task. Author commits as `demo <demo@example.com>` (repo-local git identity already set).

---

### Task 1: Solution scaffolding

**Files:**
- Create: `src/VPNTray/VPNTray.csproj`
- Create: `src/VPNTray/App/app.manifest`
- Create: `src/VPNTray.Tests/VPNTray.Tests.csproj`
- Create: `VPNTray.sln`
- Create: `src/VPNTray.Tests/SmokeTest.cs`

**Interfaces:**
- Produces: a buildable solution with an app project and a test project that references it.

- [ ] **Step 1: Create the solution and projects**

Run:
```bash
cd C:/work/tools/VPNTray
dotnet new sln -n VPNTray
dotnet new wpf -n VPNTray -o src/VPNTray -f net8.0
dotnet new xunit -n VPNTray.Tests -o src/VPNTray.Tests -f net8.0
dotnet sln add src/VPNTray/VPNTray.csproj src/VPNTray.Tests/VPNTray.Tests.csproj
dotnet add src/VPNTray.Tests/VPNTray.Tests.csproj reference src/VPNTray/VPNTray.csproj
dotnet add src/VPNTray/VPNTray.csproj package Hardcodet.NotifyIcon.Wpf
```

- [ ] **Step 2: Configure the app project for elevation and a stable assembly name**

Edit `src/VPNTray/VPNTray.csproj` so the `<PropertyGroup>` contains:
```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <RootNamespace>VPNTray</RootNamespace>
  <AssemblyName>VPNTray</AssemblyName>
  <Platforms>x64</Platforms>
  <PlatformTarget>x64</PlatformTarget>
  <ApplicationManifest>App/app.manifest</ApplicationManifest>
</PropertyGroup>
```

- [ ] **Step 3: Create the elevation manifest**

Create `src/VPNTray/App/app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" /> <!-- Windows 10/11 -->
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 4: Add the test project namespace and a smoke test**

Replace `src/VPNTray.Tests/SmokeTest.cs` (delete the generated `UnitTest1.cs`):
```csharp
namespace VPNTray.Tests;

public class SmokeTest
{
    [Fact]
    public void Solution_Builds_And_Tests_Run()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 5: Build and test**

Run: `dotnet build VPNTray.sln && dotnet test VPNTray.sln`
Expected: build succeeds, 1 test passes.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: scaffold VPNTray solution (app + tests, elevation manifest)"
```

---

### Task 2: TotpGenerator

**Files:**
- Create: `src/VPNTray/Core/TotpGenerator.cs`
- Test: `src/VPNTray.Tests/Core/TotpGeneratorTests.cs`

**Interfaces:**
- Produces:
  - `static class TotpGenerator`
  - `static string Code(string secret, DateTimeOffset at = default, int digits = 6)` — `at == default` means "now". Throws `TotpException`.
  - `static byte[] Base32Decode(string input)` — throws `TotpException` on invalid input.
  - `class TotpException : Exception`

- [ ] **Step 1: Write the failing tests (RFC 6238 known-answer vectors)**

Create `src/VPNTray.Tests/Core/TotpGeneratorTests.cs`:
```csharp
using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class TotpGeneratorTests
{
    // RFC 6238 Appendix B uses secret "12345678901234567890" (ASCII).
    // Base32 of that ASCII is "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ".
    private const string Rfc6238Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    public void Code_MatchesRfc6238Vectors(long unixSeconds, string expected)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(expected, TotpGenerator.Code(Rfc6238Secret, at, digits: 6));
    }

    [Fact]
    public void Base32Decode_DecodesAsciiSecret()
    {
        var bytes = TotpGenerator.Base32Decode("GEZDGNBVGY3TQOJQ");
        Assert.Equal("1234567890", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Base32Decode_IgnoresPaddingSpacesAndCase()
    {
        var a = TotpGenerator.Base32Decode("gezd gnbv gy3t qojq=");
        var b = TotpGenerator.Base32Decode("GEZDGNBVGY3TQOJQ");
        Assert.Equal(b, a);
    }

    [Fact]
    public void Base32Decode_ThrowsOnInvalidChar()
    {
        Assert.Throws<TotpException>(() => TotpGenerator.Base32Decode("0189!"));
    }

    [Fact]
    public void Code_ThrowsOnInvalidDigits()
    {
        Assert.Throws<TotpException>(() => TotpGenerator.Code(Rfc6238Secret, DateTimeOffset.UnixEpoch, digits: 0));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/VPNTray.Tests --filter TotpGeneratorTests`
Expected: FAIL (TotpGenerator does not exist).

- [ ] **Step 3: Implement TotpGenerator**

Create `src/VPNTray/Core/TotpGenerator.cs`:
```csharp
using System.Security.Cryptography;

namespace VPNTray.Core;

public class TotpException : Exception
{
    public TotpException(string message) : base(message) { }
}

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30s step) with an RFC 4648 Base32 decoder.</summary>
public static class TotpGenerator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Code(string secret, DateTimeOffset at = default, int digits = 6)
    {
        if (digits is < 1 or > 9) throw new TotpException("digits must be 1..9");
        if (at == default) at = DateTimeOffset.UtcNow;

        var key = Base32Decode(secret);
        long seconds = Math.Max(0, at.ToUnixTimeSeconds());
        ulong counter = (ulong)(seconds / 30);

        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);

        byte[] mac = HMACSHA1.HashData(key, counterBytes.ToArray());

        int offset = mac[^1] & 0x0f;
        int binary =
            ((mac[offset] & 0x7f) << 24) |
            ((mac[offset + 1] & 0xff) << 16) |
            ((mac[offset + 2] & 0xff) << 8) |
            (mac[offset + 3] & 0xff);

        int modulus = (int)Math.Pow(10, digits);
        int truncated = binary % modulus;
        return truncated.ToString().PadLeft(digits, '0');
    }

    public static byte[] Base32Decode(string input)
    {
        var cleaned = input.ToUpperInvariant()
            .Replace("=", "")
            .Replace(" ", "");

        int bits = 0;
        uint buffer = 0;
        var output = new List<byte>(cleaned.Length * 5 / 8 + 1);

        foreach (char c in cleaned)
        {
            int value = Alphabet.IndexOf(c);
            if (value < 0) throw new TotpException($"invalid Base32 character '{c}'");
            buffer = (buffer << 5) | (uint)value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xff));
            }
        }
        return output.ToArray();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/VPNTray.Tests --filter TotpGeneratorTests`
Expected: PASS (all vectors).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add TotpGenerator (RFC 6238 + Base32 decode)"
```

---

### Task 3: BundledBinaries (path resolution)

**Files:**
- Create: `src/VPNTray/Dependencies/BundledBinaries.cs`
- Test: `src/VPNTray.Tests/Dependencies/BundledBinariesTests.cs`

**Interfaces:**
- Produces:
  - `static class BundledBinaries`
  - `static string BaseDir { get; }` — the app's `Resources` folder under `AppContext.BaseDirectory`.
  - `static string OpenconnectExe { get; }`, `WintunDll`, `VpncScript`, `VpncScriptNoDns` — full paths.

- [ ] **Step 1: Write the failing test**

Create `src/VPNTray.Tests/Dependencies/BundledBinariesTests.cs`:
```csharp
using VPNTray.Dependencies;

namespace VPNTray.Tests.Dependencies;

public class BundledBinariesTests
{
    [Fact]
    public void Paths_AreUnderResources_AndNamedCorrectly()
    {
        Assert.EndsWith(Path.Combine("Resources", "openconnect.exe"), BundledBinaries.OpenconnectExe);
        Assert.EndsWith(Path.Combine("Resources", "wintun.dll"), BundledBinaries.WintunDll);
        Assert.EndsWith(Path.Combine("Resources", "vpnc-script-win.js"), BundledBinaries.VpncScript);
        Assert.EndsWith(Path.Combine("Resources", "vpnc-script-win--no-dns.js"), BundledBinaries.VpncScriptNoDns);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/VPNTray.Tests --filter BundledBinariesTests`
Expected: FAIL (BundledBinaries not found).

- [ ] **Step 3: Implement BundledBinaries**

Create `src/VPNTray/Dependencies/BundledBinaries.cs`:
```csharp
namespace VPNTray.Dependencies;

/// <summary>Resolves paths to the native binaries bundled in the app's Resources folder.</summary>
public static class BundledBinaries
{
    public static string BaseDir => Path.Combine(AppContext.BaseDirectory, "Resources");

    public static string OpenconnectExe => Path.Combine(BaseDir, "openconnect.exe");
    public static string WintunDll => Path.Combine(BaseDir, "wintun.dll");
    public static string VpncScript => Path.Combine(BaseDir, "vpnc-script-win.js");
    public static string VpncScriptNoDns => Path.Combine(BaseDir, "vpnc-script-win--no-dns.js");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/VPNTray.Tests --filter BundledBinariesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add BundledBinaries path resolver"
```

---

### Task 4: VpnConfig

**Files:**
- Create: `src/VPNTray/Config/VpnConfig.cs`
- Test: `src/VPNTray.Tests/Config/VpnConfigTests.cs`

**Interfaces:**
- Consumes: `BundledBinaries` (for default paths).
- Produces:
  - `class VpnConfig` with mutable properties: `Username`, `PasswordPrefix`, `TotpSecret`, `Gateway`, `ServerCertPin` (all `string = ""`), `OpenconnectPath` (default `BundledBinaries.OpenconnectExe`), `VpncScriptPath` (default `BundledBinaries.VpncScript`), `SkipDnsModification` (`bool = true`), `SchemaVersion` (`int = 1`).
  - `bool IsConfigured { get; }`
  - `VpnConfig Sanitized()`

- [ ] **Step 1: Write the failing tests**

Create `src/VPNTray.Tests/Config/VpnConfigTests.cs`:
```csharp
using VPNTray.Config;

namespace VPNTray.Tests.Config;

public class VpnConfigTests
{
    private static VpnConfig FullyConfigured() => new()
    {
        Username = "first.last",
        PasswordPrefix = "Prefix2021",
        TotpSecret = "GEZDGNBVGY3TQOJQ",
        Gateway = "vpn.example.com",
        ServerCertPin = "pin-sha256:AAAA",
    };

    [Fact]
    public void IsConfigured_TrueWhenAllFiveRequiredFieldsPresent()
        => Assert.True(FullyConfigured().IsConfigured);

    [Theory]
    [InlineData("Username")]
    [InlineData("PasswordPrefix")]
    [InlineData("TotpSecret")]
    [InlineData("Gateway")]
    [InlineData("ServerCertPin")]
    public void IsConfigured_FalseWhenAnyRequiredFieldEmpty(string field)
    {
        var c = FullyConfigured();
        typeof(VpnConfig).GetProperty(field)!.SetValue(c, "");
        Assert.False(c.IsConfigured);
    }

    [Fact]
    public void Sanitized_StripsInvisibleAndControlChars()
    {
        var c = new VpnConfig { Username = "  ab​cd   ", TotpSecret = "EF\tGH" };
        var s = c.Sanitized();
        Assert.Equal("abcd", s.Username);
        Assert.Equal("EFGH", s.TotpSecret);
    }

    [Fact]
    public void Defaults_UseBundledPaths_AndSkipDnsOn()
    {
        var c = new VpnConfig();
        Assert.EndsWith("openconnect.exe", c.OpenconnectPath);
        Assert.True(c.SkipDnsModification);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/VPNTray.Tests --filter VpnConfigTests`
Expected: FAIL (VpnConfig not found).

- [ ] **Step 3: Implement VpnConfig**

Create `src/VPNTray/Config/VpnConfig.cs`:
```csharp
using VPNTray.Dependencies;

namespace VPNTray.Config;

public class VpnConfig
{
    public string Username { get; set; } = "";
    public string PasswordPrefix { get; set; } = "";
    public string TotpSecret { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string ServerCertPin { get; set; } = "";
    public string OpenconnectPath { get; set; } = BundledBinaries.OpenconnectExe;
    public string VpncScriptPath { get; set; } = BundledBinaries.VpncScript;
    public bool SkipDnsModification { get; set; } = true;
    public int SchemaVersion { get; set; } = 1;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Username) &&
        !string.IsNullOrEmpty(PasswordPrefix) &&
        !string.IsNullOrEmpty(TotpSecret) &&
        !string.IsNullOrEmpty(Gateway) &&
        !string.IsNullOrEmpty(ServerCertPin);

    public VpnConfig Sanitized() => new()
    {
        Username = Clean(Username),
        PasswordPrefix = Clean(PasswordPrefix),
        TotpSecret = Clean(TotpSecret),
        Gateway = Clean(Gateway),
        ServerCertPin = Clean(ServerCertPin),
        OpenconnectPath = Clean(OpenconnectPath),
        VpncScriptPath = Clean(VpncScriptPath),
        SkipDnsModification = SkipDnsModification,
        SchemaVersion = SchemaVersion,
    };

    private static readonly HashSet<char> Invisible = new()
    {
        '​', '‌', '‍', '﻿',
        ' ', ' ', ' ', '　',
    };

    private static string Clean(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (Invisible.Contains(ch)) continue;
            if (ch < 0x20 || ch == 0x7F) continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/VPNTray.Tests --filter VpnConfigTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add VpnConfig with IsConfigured + Sanitized"
```

---

### Task 5: ConfigStore

**Files:**
- Create: `src/VPNTray/Config/ConfigStore.cs`
- Test: `src/VPNTray.Tests/Config/ConfigStoreTests.cs`

**Interfaces:**
- Consumes: `VpnConfig`.
- Produces:
  - `class ConfigStore` with `ConfigStore(string? directory = null)` (default `%APPDATA%\VPNTray`).
  - `VpnConfig? Load()` — `null` when file missing; backs up + returns `null` on decode error.
  - `void Save(VpnConfig config)` — atomic write, ACL-restricts the file to the current user.
  - `string FilePath { get; }`

- [ ] **Step 1: Write the failing tests**

Create `src/VPNTray.Tests/Config/ConfigStoreTests.cs`:
```csharp
using VPNTray.Config;

namespace VPNTray.Tests.Config;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpntray-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Load_ReturnsNull_WhenFileMissing()
        => Assert.Null(new ConfigStore(_dir).Load());

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var store = new ConfigStore(_dir);
        store.Save(new VpnConfig { Username = "u", Gateway = "g", TotpSecret = "s" });
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("u", loaded!.Username);
        Assert.Equal("g", loaded.Gateway);
    }

    [Fact]
    public void Load_BacksUpAndReturnsNull_OnCorruptFile()
    {
        var store = new ConfigStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "{ this is not valid json");
        Assert.Null(store.Load());
        Assert.True(File.Exists(store.FilePath + ".broken"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/VPNTray.Tests --filter ConfigStoreTests`
Expected: FAIL (ConfigStore not found).

- [ ] **Step 3: Implement ConfigStore**

Create `src/VPNTray/Config/ConfigStore.cs`:
```csharp
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace VPNTray.Config;

public class ConfigStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ConfigStore(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPNTray");
    }

    public string FilePath => Path.Combine(_dir, "config.json");

    public VpnConfig? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<VpnConfig>(json, Options);
        }
        catch (Exception)
        {
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { /* best effort */ }
            return null;
        }
    }

    public void Save(VpnConfig config)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(config, Options);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        RestrictToCurrentUser(tmp);
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Windows equivalent of chmod 0600 — owner full control, no inheritance.</summary>
    private static void RestrictToCurrentUser(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var security = new FileSecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception)
        {
            // ACL hardening is best-effort; never block a save on it.
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/VPNTray.Tests --filter ConfigStoreTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add ConfigStore (atomic write, ACL, broken-file recovery)"
```

---

### Task 6: VpnState

**Files:**
- Create: `src/VPNTray/Core/VpnState.cs`
- Test: `src/VPNTray.Tests/Core/VpnStateTests.cs`

**Interfaces:**
- Produces:
  - `enum VpnStatus { Disconnected, Connecting, Connected, Failed }`
  - `sealed record VpnState(VpnStatus Status, DateTimeOffset? Since = null, string? Reason = null)`
  - static factory props/methods: `VpnState.Disconnected`, `VpnState.Connecting`, `VpnState.Connected(DateTimeOffset since)`, `VpnState.Failed(string reason)`.
  - `const string SetupIncompletePrefix = "Setup incomplete"`.
  - `bool IsSetupIncomplete { get; }` — true when `Status==Failed && Reason startswith SetupIncompletePrefix`.

- [ ] **Step 1: Write the failing tests**

Create `src/VPNTray.Tests/Core/VpnStateTests.cs`:
```csharp
using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class VpnStateTests
{
    [Fact]
    public void Disconnected_HasDisconnectedStatus()
        => Assert.Equal(VpnStatus.Disconnected, VpnState.Disconnected.Status);

    [Fact]
    public void Failed_WithSetupIncompletePrefix_IsSetupIncomplete()
        => Assert.True(VpnState.Failed("Setup incomplete — finish onboarding").IsSetupIncomplete);

    [Fact]
    public void Failed_WithOtherReason_IsNotSetupIncomplete()
        => Assert.False(VpnState.Failed("Authentication failed").IsSetupIncomplete);

    [Fact]
    public void Connected_CarriesSince()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Equal(now, VpnState.Connected(now).Since);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/VPNTray.Tests --filter VpnStateTests`
Expected: FAIL.

- [ ] **Step 3: Implement VpnState**

Create `src/VPNTray/Core/VpnState.cs`:
```csharp
namespace VPNTray.Core;

public enum VpnStatus { Disconnected, Connecting, Connected, Failed }

public sealed record VpnState(VpnStatus Status, DateTimeOffset? Since = null, string? Reason = null)
{
    public const string SetupIncompletePrefix = "Setup incomplete";

    public static VpnState Disconnected { get; } = new(VpnStatus.Disconnected);
    public static VpnState Connecting { get; } = new(VpnStatus.Connecting);
    public static VpnState Connected(DateTimeOffset since) => new(VpnStatus.Connected, Since: since);
    public static VpnState Failed(string reason) => new(VpnStatus.Failed, Reason: reason);

    public bool IsSetupIncomplete =>
        Status == VpnStatus.Failed && (Reason?.StartsWith(SetupIncompletePrefix) ?? false);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/VPNTray.Tests --filter VpnStateTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add VpnState state model"
```

---

### Task 7: IProcessRunner + SystemProcessRunner

**Files:**
- Create: `src/VPNTray/Core/IProcessRunner.cs`
- Create: `src/VPNTray/Core/SystemProcessRunner.cs`
- Test: `src/VPNTray.Tests/Core/SystemProcessRunnerTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ProcessResult(int ExitCode, string Stdout, string Stderr) { bool Succeeded => ExitCode == 0; }`
  - `interface IProcessRunner { ProcessResult Run(string executable, IReadOnlyList<string> arguments, int timeoutSeconds); }`
  - `sealed class SystemProcessRunner : IProcessRunner`

- [ ] **Step 1: Write the failing test (uses a real, always-present Windows exe)**

Create `src/VPNTray.Tests/Core/SystemProcessRunnerTests.cs`:
```csharp
using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class SystemProcessRunnerTests
{
    [Fact]
    public void Run_CapturesStdout_AndZeroExit()
    {
        var runner = new SystemProcessRunner();
        var result = runner.Run("cmd.exe", new[] { "/c", "echo", "hello" }, timeoutSeconds: 5);
        Assert.True(result.Succeeded);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public void Run_NonZeroExit_NotSucceeded()
    {
        var runner = new SystemProcessRunner();
        var result = runner.Run("cmd.exe", new[] { "/c", "exit", "3" }, timeoutSeconds: 5);
        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/VPNTray.Tests --filter SystemProcessRunnerTests`
Expected: FAIL.

- [ ] **Step 3: Implement the interface and runner**

Create `src/VPNTray/Core/IProcessRunner.cs`:
```csharp
namespace VPNTray.Core;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    ProcessResult Run(string executable, IReadOnlyList<string> arguments, int timeoutSeconds);
}
```

Create `src/VPNTray/Core/SystemProcessRunner.cs`:
```csharp
using System.Diagnostics;

namespace VPNTray.Core;

public sealed class SystemProcessRunner : IProcessRunner
{
    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return new ProcessResult(-1, "", "timed out");
        }
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/VPNTray.Tests --filter SystemProcessRunnerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add IProcessRunner + SystemProcessRunner"
```

---

### Task 8: Handshake parsing (pure logic, extracted for testability)

**Files:**
- Create: `src/VPNTray/Core/HandshakeAnalyzer.cs`
- Test: `src/VPNTray.Tests/Core/HandshakeAnalyzerTests.cs`

**Interfaces:**
- Produces:
  - `sealed record OpenConnectHandshake(bool Connected, string? Reason)`
  - `static class HandshakeAnalyzer`
  - `static OpenConnectHandshake? Evaluate(string stderr, bool processAlive, bool gracePassed, bool childConfirmedAlive)` — returns a decided handshake, or `null` meaning "keep waiting".
  - `static string FriendlyReason(string fullStderr)` — maps known markers / tails stderr.

- [ ] **Step 1: Write the failing tests**

Create `src/VPNTray.Tests/Core/HandshakeAnalyzerTests.cs`:
```csharp
using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class HandshakeAnalyzerTests
{
    [Fact]
    public void Evaluate_SuccessMarker_WhenAliveAndChildConfirmed_ReturnsConnected()
    {
        var h = HandshakeAnalyzer.Evaluate("... CSTP connected ...", processAlive: true, gracePassed: false, childConfirmedAlive: true);
        Assert.NotNull(h);
        Assert.True(h!.Connected);
    }

    [Fact]
    public void Evaluate_AuthFailureMarker_ReturnsFailedWithFriendlyReason()
    {
        var h = HandshakeAnalyzer.Evaluate("Login failed: bad creds", processAlive: true, gracePassed: false, childConfirmedAlive: true);
        Assert.NotNull(h);
        Assert.False(h!.Connected);
        Assert.Contains("Authentication failed", h.Reason);
    }

    [Fact]
    public void Evaluate_CertFailureMarker_MentionsCertPin()
    {
        var h = HandshakeAnalyzer.Evaluate("Certificate verification failure", true, false, true);
        Assert.Contains("certificate", h!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ProcessExitedWithNoMarker_ReturnsFailed()
    {
        var h = HandshakeAnalyzer.Evaluate("some random output", processAlive: false, gracePassed: false, childConfirmedAlive: false);
        Assert.NotNull(h);
        Assert.False(h!.Connected);
    }

    [Fact]
    public void Evaluate_GracePassedAndAlive_ReturnsConnected()
    {
        var h = HandshakeAnalyzer.Evaluate("", processAlive: true, gracePassed: true, childConfirmedAlive: true);
        Assert.True(h!.Connected);
    }

    [Fact]
    public void Evaluate_StillConnecting_ReturnsNull()
    {
        var h = HandshakeAnalyzer.Evaluate("negotiating...", processAlive: true, gracePassed: false, childConfirmedAlive: true);
        Assert.Null(h);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/VPNTray.Tests --filter HandshakeAnalyzerTests`
Expected: FAIL.

- [ ] **Step 3: Implement HandshakeAnalyzer**

Create `src/VPNTray/Core/HandshakeAnalyzer.cs`:
```csharp
namespace VPNTray.Core;

public sealed record OpenConnectHandshake(bool Connected, string? Reason);

public static class HandshakeAnalyzer
{
    private static readonly string[] SuccessMarkers =
        { "Connected as", "CSTP connected", "Established DTLS", "ESP session established" };

    private static readonly string[] FailureMarkers =
        { "Login failed", "authentication failure", "Certificate verification failure" };

    /// <summary>
    /// Decide the handshake outcome from the current stderr snapshot, or return
    /// null to keep waiting. Caller supplies liveness facts it alone can observe.
    /// </summary>
    public static OpenConnectHandshake? Evaluate(string stderr, bool processAlive, bool gracePassed, bool childConfirmedAlive)
    {
        foreach (var marker in FailureMarkers)
            if (stderr.Contains(marker, StringComparison.Ordinal))
                return new OpenConnectHandshake(false, MapMarker(marker, stderr));

        if (!processAlive)
        {
            var tail = Tail(stderr, 200);
            return new OpenConnectHandshake(false, tail.Length == 0 ? "openconnect exited before handshake" : tail);
        }

        bool keywordHit = SuccessMarkers.Any(m => stderr.Contains(m, StringComparison.Ordinal));
        if ((keywordHit || gracePassed) && childConfirmedAlive)
            return new OpenConnectHandshake(true, null);

        return null; // keep waiting
    }

    public static string FriendlyReason(string fullStderr)
    {
        foreach (var marker in FailureMarkers)
            if (fullStderr.Contains(marker, StringComparison.Ordinal))
                return MapMarker(marker, fullStderr);
        var tail = Tail(fullStderr, 200);
        return tail.Length == 0 ? "Handshake timeout" : tail;
    }

    private static string MapMarker(string marker, string fullStderr) => marker switch
    {
        "Login failed" or "authentication failure" =>
            "Authentication failed — please check your TOTP secret or password prefix.",
        "Certificate verification failure" =>
            "Server certificate verification failed. The gateway cert may have rotated — update the pin in Settings.",
        _ => Tail(fullStderr, 200),
    };

    private static string Tail(string text, int chars)
    {
        text = text.Trim();
        return text.Length <= chars ? text : text[^chars..].Trim();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/VPNTray.Tests --filter HandshakeAnalyzerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add HandshakeAnalyzer (stderr → handshake outcome)"
```

---

### Task 9: IOpenConnectProcessRunning + OpenConnectProcess

**Files:**
- Create: `src/VPNTray/Core/IOpenConnectProcessRunning.cs`
- Create: `src/VPNTray/Core/OpenConnectProcess.cs`
- Test: `src/VPNTray.Tests/Core/OpenConnectArgsTests.cs`

**Interfaces:**
- Consumes: `VpnConfig`, `HandshakeAnalyzer`, `OpenConnectHandshake`, `BundledBinaries`.
- Produces:
  - `interface IOpenConnectProcessRunning { void Start(VpnConfig config, string password); Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout); bool IsRunning(); void Stop(); }`
  - `sealed class OpenConnectProcess : IOpenConnectProcessRunning`
  - `static class OpenConnectArgs { static IReadOnlyList<string> Build(VpnConfig config); }` (pure, testable arg builder)

- [ ] **Step 1: Write the failing test for the arg builder**

Create `src/VPNTray.Tests/Core/OpenConnectArgsTests.cs`:
```csharp
using VPNTray.Config;
using VPNTray.Core;

namespace VPNTray.Tests.Core;

public class OpenConnectArgsTests
{
    private static VpnConfig Cfg() => new()
    {
        Username = "first.last",
        Gateway = "vpn.example.com",
        ServerCertPin = "pin-sha256:AAAA",
        VpncScriptPath = @"C:\app\Resources\vpnc-script-win.js",
        SkipDnsModification = false,
    };

    [Fact]
    public void Build_IncludesUserPinGatewayAndStdinFlag()
    {
        var args = OpenConnectArgs.Build(Cfg());
        Assert.Contains("--user", args);
        Assert.Contains("first.last", args);
        Assert.Contains("--passwd-on-stdin", args);
        Assert.Contains("--servercert", args);
        Assert.Contains("pin-sha256:AAAA", args);
        Assert.Equal("vpn.example.com", args[^1]); // gateway is last positional
    }

    [Fact]
    public void Build_UsesConfiguredScript_WhenSkipDnsOff()
    {
        var args = OpenConnectArgs.Build(Cfg());
        int i = args.ToList().IndexOf("--script");
        Assert.Equal(@"C:\app\Resources\vpnc-script-win.js", args[i + 1]);
    }

    [Fact]
    public void Build_UsesNoDnsScript_WhenSkipDnsOn()
    {
        var cfg = Cfg();
        cfg.SkipDnsModification = true;
        var args = OpenConnectArgs.Build(cfg);
        int i = args.ToList().IndexOf("--script");
        Assert.EndsWith("vpnc-script-win--no-dns.js", args[i + 1]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/VPNTray.Tests --filter OpenConnectArgsTests`
Expected: FAIL.

- [ ] **Step 3: Implement interface, arg builder, and process wrapper**

Create `src/VPNTray/Core/IOpenConnectProcessRunning.cs`:
```csharp
using VPNTray.Config;

namespace VPNTray.Core;

public interface IOpenConnectProcessRunning
{
    /// <summary>Spawn openconnect and write "password\n" to its stdin. Returns after spawn, not handshake.</summary>
    void Start(VpnConfig config, string password);

    /// <summary>Resolve within `timeout`; returns connected or failed(reason).</summary>
    Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout);

    bool IsRunning();

    /// <summary>Terminate the spawned openconnect. Idempotent.</summary>
    void Stop();
}
```

Create `src/VPNTray/Core/OpenConnectProcess.cs`:
```csharp
using System.Diagnostics;
using System.Text;
using VPNTray.Config;
using VPNTray.Dependencies;
using VPNTray.Util;

namespace VPNTray.Core;

public static class OpenConnectArgs
{
    public static IReadOnlyList<string> Build(VpnConfig config)
    {
        string script = config.SkipDnsModification
            ? BundledBinaries.VpncScriptNoDns
            : config.VpncScriptPath;

        return new List<string>
        {
            "--script", script,
            "--user", config.Username,
            "--passwd-on-stdin",
            "--servercert", config.ServerCertPin,
            config.Gateway,
        };
    }
}

public sealed class OpenConnectProcess : IOpenConnectProcessRunning
{
    private Process? _process;
    private readonly StringBuilder _stderr = new();
    private readonly object _lock = new();

    public void Start(VpnConfig config, string password)
    {
        Stop(); // clean up any prior invocation
        lock (_lock) { _stderr.Clear(); }

        var psi = new ProcessStartInfo
        {
            FileName = config.OpenconnectPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = BundledBinaries.BaseDir, // so wintun.dll resolves
        };
        foreach (var arg in OpenConnectArgs.Build(config)) psi.ArgumentList.Add(arg);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_lock) { _stderr.AppendLine(e.Data); }
        };
        p.Start();
        p.BeginErrorReadLine();
        // Do NOT log the password.
        AppLogger.Shared.Info("openconnect spawned");
        p.StandardInput.WriteLine(password);
        p.StandardInput.Close();
        _process = p;
    }

    public async Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        var grace = TimeSpan.FromSeconds(1.5);

        while (DateTimeOffset.UtcNow - start < timeout)
        {
            var p = _process;
            if (p is null) return new OpenConnectHandshake(false, "openconnect process not started");

            string stderr;
            lock (_lock) { stderr = _stderr.ToString(); }

            bool gracePassed = DateTimeOffset.UtcNow - start >= grace;
            var decided = HandshakeAnalyzer.Evaluate(
                stderr, processAlive: !p.HasExited, gracePassed: gracePassed, childConfirmedAlive: IsRunning());
            if (decided is not null)
            {
                if (!decided.Connected)
                    AppLogger.Shared.Error("openconnect handshake failed. Full stderr:\n" + stderr.Trim());
                return decided;
            }
            await Task.Delay(250);
        }

        string finalErr;
        lock (_lock) { finalErr = _stderr.ToString(); }
        AppLogger.Shared.Error("openconnect handshake timeout. Full stderr:\n" + finalErr.Trim());
        return new OpenConnectHandshake(false, HandshakeAnalyzer.FriendlyReason(finalErr));
    }

    public bool IsRunning()
    {
        var p = _process;
        try { return p is not null && !p.HasExited; }
        catch { return false; }
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { p.Dispose(); } catch { /* ignore */ }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/VPNTray.Tests --filter OpenConnectArgsTests`
Expected: PASS. (The process wrapper itself is exercised via manual smoke test in Task 17; its decision logic is covered by `HandshakeAnalyzerTests`.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add OpenConnectProcess + testable arg builder"
```

---

### Task 10: Logger + Util scaffolding

**Files:**
- Create: `src/VPNTray/Util/AppLogger.cs`
- Create: `src/VPNTray/Util/SingleInstance.cs`
- Test: `src/VPNTray.Tests/Util/AppLoggerTests.cs`

**Interfaces:**
- Produces:
  - `sealed class AppLogger` with `static AppLogger Shared`, `void Info(string)`, `void Error(string)`, `string LogPath`. Writes to `%APPDATA%\VPNTray\vpntray.log`.
  - `static class SingleInstance { static bool TryAcquire(out Mutex? handle); }`

- [ ] **Step 1: Write the failing test**

Create `src/VPNTray.Tests/Util/AppLoggerTests.cs`:
```csharp
using VPNTray.Util;

namespace VPNTray.Tests.Util;

public class AppLoggerTests
{
    [Fact]
    public void Info_WritesLineToFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpntray-log-" + Guid.NewGuid().ToString("N"));
        var logger = new AppLogger(dir);
        logger.Info("hello world");
        Assert.Contains("hello world", File.ReadAllText(logger.LogPath));
        Directory.Delete(dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/VPNTray.Tests --filter AppLoggerTests`
Expected: FAIL.

- [ ] **Step 3: Implement AppLogger and SingleInstance**

Create `src/VPNTray/Util/AppLogger.cs`:
```csharp
namespace VPNTray.Util;

public sealed class AppLogger
{
    private readonly string _dir;
    private readonly object _lock = new();

    public AppLogger(string? directory = null)
    {
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VPNTray");
    }

    public static AppLogger Shared { get; } = new();

    public string LogPath => Path.Combine(_dir, "vpntray.log");

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            lock (_lock) { File.AppendAllText(LogPath, line); }
        }
        catch { /* logging must never throw */ }
    }
}
```

Create `src/VPNTray/Util/SingleInstance.cs`:
```csharp
namespace VPNTray.Util;

public static class SingleInstance
{
    private const string MutexName = "Global\\VPNTray_SingleInstance";

    /// <summary>Returns true if this is the first instance. Keep `handle` alive for the app lifetime.</summary>
    public static bool TryAcquire(out Mutex? handle)
    {
        handle = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            handle.Dispose();
            handle = null;
        }
        return createdNew;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/VPNTray.Tests --filter AppLoggerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add AppLogger and SingleInstance"
```

---

### Task 11: INetworkMonitor + NetworkMonitor

**Files:**
- Create: `src/VPNTray/Core/INetworkMonitor.cs`
- Create: `src/VPNTray/Core/NetworkMonitor.cs`
- Test: `src/VPNTray.Tests/Core/FakeNetworkMonitorTests.cs`

**Interfaces:**
- Produces:
  - `interface INetworkMonitor { event Action<bool>? ReachabilityChanged; void Start(); void Stop(); }`
  - `sealed class NetworkMonitor : INetworkMonitor` (wraps `System.Net.NetworkInformation.NetworkChange`).
  - For tests: a `FakeNetworkMonitor` in the test project that lets tests raise the event.

- [ ] **Step 1: Write the failing test (validates the interface contract via a fake)**

Create `src/VPNTray.Tests/Core/FakeNetworkMonitorTests.cs`:
```csharp
using VPNTray.Core;

namespace VPNTray.Tests.Core;

public sealed class FakeNetworkMonitor : INetworkMonitor
{
    public event Action<bool>? ReachabilityChanged;
    public bool Started { get; private set; }
    public void Start() => Started = true;
    public void Stop() => Started = false;
    public void Raise(bool reachable) => ReachabilityChanged?.Invoke(reachable);
}

public class FakeNetworkMonitorTests
{
    [Fact]
    public void Raise_InvokesSubscribers()
    {
        var m = new FakeNetworkMonitor();
        bool? got = null;
        m.ReachabilityChanged += r => got = r;
        m.Start();
        m.Raise(true);
        Assert.True(m.Started);
        Assert.Equal(true, got);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/VPNTray.Tests --filter FakeNetworkMonitorTests`
Expected: FAIL (INetworkMonitor not found).

- [ ] **Step 3: Implement the interface and the real monitor**

Create `src/VPNTray/Core/INetworkMonitor.cs`:
```csharp
namespace VPNTray.Core;

public interface INetworkMonitor
{
    /// <summary>Fires with `true` when the network becomes reachable, `false` when it is lost.</summary>
    event Action<bool>? ReachabilityChanged;
    void Start();
    void Stop();
}
```

Create `src/VPNTray/Core/NetworkMonitor.cs`:
```csharp
using System.Net.NetworkInformation;

namespace VPNTray.Core;

public sealed class NetworkMonitor : INetworkMonitor
{
    public event Action<bool>? ReachabilityChanged;

    public void Start()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
    }

    public void Stop()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => ReachabilityChanged?.Invoke(e.IsAvailable);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/VPNTray.Tests --filter FakeNetworkMonitorTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add INetworkMonitor + NetworkMonitor"
```

---

### Task 12: VpnController state machine

**Files:**
- Create: `src/VPNTray/Core/VpnController.cs`
- Test: `src/VPNTray.Tests/Core/VpnControllerTests.cs`

**Interfaces:**
- Consumes: `IOpenConnectProcessRunning`, `INetworkMonitor`, `VpnConfig`, `ConfigStore`, `VpnState`, `TotpGenerator`.
- Produces:
  - `sealed class VpnController : INotifyPropertyChanged`
  - ctor `VpnController(IOpenConnectProcessRunning openConnect, INetworkMonitor network, Func<VpnConfig?> loadConfig)`
  - `VpnState State { get; }` (raises `PropertyChanged`)
  - `bool ShouldAutoReconnect { get; }`
  - `Task ConnectAsync()`, `Task DisconnectAsync()`, `void MarkSetupIncomplete()`
  - `void StartNetworkMonitoring()`

- [ ] **Step 1: Write the failing tests with fakes**

Create `src/VPNTray.Tests/Core/VpnControllerTests.cs`:
```csharp
using VPNTray.Config;
using VPNTray.Core;

namespace VPNTray.Tests.Core;

internal sealed class FakeOpenConnect : IOpenConnectProcessRunning
{
    public OpenConnectHandshake NextHandshake = new(true, null);
    public bool Running;
    public int StartCount;
    public int StopCount;
    public string? LastPassword;

    public void Start(VpnConfig config, string password) { StartCount++; LastPassword = password; Running = true; }
    public Task<OpenConnectHandshake> WaitForHandshakeAsync(TimeSpan timeout)
    {
        Running = NextHandshake.Connected;
        return Task.FromResult(NextHandshake);
    }
    public bool IsRunning() => Running;
    public void Stop() { StopCount++; Running = false; }
}

public class VpnControllerTests
{
    private static VpnConfig Good() => new()
    {
        Username = "u", PasswordPrefix = "PFX", TotpSecret = "GEZDGNBVGY3TQOJQ",
        Gateway = "g", ServerCertPin = "pin-sha256:AAAA",
    };

    [Fact]
    public async Task Connect_Success_SetsConnectedAndAutoReconnect()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var net = new FakeNetworkMonitor();
        var c = new VpnController(oc, net, () => Good());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Connected, c.State.Status);
        Assert.True(c.ShouldAutoReconnect);
        Assert.StartsWith("PFX", oc.LastPassword); // prefix + TOTP
    }

    [Fact]
    public async Task Connect_Failure_SetsFailed_AndDoesNotArmAutoReconnect()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(false, "Authentication failed") };
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => Good());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.False(c.ShouldAutoReconnect);
    }

    [Fact]
    public async Task Connect_WhenNotConfigured_FailsWithoutSpawning()
    {
        var oc = new FakeOpenConnect();
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => new VpnConfig());
        await c.ConnectAsync();
        Assert.Equal(VpnStatus.Failed, c.State.Status);
        Assert.Equal(0, oc.StartCount);
    }

    [Fact]
    public async Task Disconnect_ClearsAutoReconnect_AndStops()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var c = new VpnController(oc, new FakeNetworkMonitor(), () => Good());
        await c.ConnectAsync();
        await c.DisconnectAsync();
        Assert.Equal(VpnStatus.Disconnected, c.State.Status);
        Assert.False(c.ShouldAutoReconnect);
        Assert.True(oc.StopCount >= 1);
    }

    [Fact]
    public async Task NetworkLost_WhileConnected_AutoDisconnects_ButKeepsIntent()
    {
        var oc = new FakeOpenConnect { NextHandshake = new(true, null) };
        var net = new FakeNetworkMonitor();
        var c = new VpnController(oc, net, () => Good());
        c.StartNetworkMonitoring();
        await c.ConnectAsync();
        net.Raise(false);
        await Task.Delay(50);
        Assert.NotEqual(VpnStatus.Connected, c.State.Status);
        Assert.True(c.ShouldAutoReconnect); // intent preserved for recovery
    }

    [Fact]
    public void MarkSetupIncomplete_SetsSetupIncompleteState()
    {
        var c = new VpnController(new FakeOpenConnect(), new FakeNetworkMonitor(), () => new VpnConfig());
        c.MarkSetupIncomplete();
        Assert.True(c.State.IsSetupIncomplete);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/VPNTray.Tests --filter VpnControllerTests`
Expected: FAIL (VpnController not found).

- [ ] **Step 3: Implement VpnController**

Create `src/VPNTray/Core/VpnController.cs`:
```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VPNTray.Config;
using VPNTray.Util;

namespace VPNTray.Core;

public sealed class VpnController : INotifyPropertyChanged
{
    private readonly IOpenConnectProcessRunning _openConnect;
    private readonly INetworkMonitor _network;
    private readonly Func<VpnConfig?> _loadConfig;

    private VpnState _state = VpnState.Disconnected;
    private CancellationTokenSource? _watchdog;

    public VpnController(IOpenConnectProcessRunning openConnect, INetworkMonitor network, Func<VpnConfig?> loadConfig)
    {
        _openConnect = openConnect;
        _network = network;
        _loadConfig = loadConfig;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VpnState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
        }
    }

    public bool ShouldAutoReconnect { get; private set; }

    public void StartNetworkMonitoring()
    {
        _network.ReachabilityChanged += OnReachabilityChanged;
        _network.Start();
    }

    public async Task ConnectAsync()
    {
        var config = (_loadConfig() ?? new VpnConfig()).Sanitized();
        if (!config.IsConfigured)
        {
            State = VpnState.Failed("Authentication required — configuration is incomplete. Open Settings.");
            return;
        }

        State = VpnState.Connecting;
        try
        {
            string code = TotpGenerator.Code(config.TotpSecret);
            string password = config.PasswordPrefix + code;
            _openConnect.Start(config, password);
            var handshake = await _openConnect.WaitForHandshakeAsync(TimeSpan.FromSeconds(5));
            if (handshake.Connected)
            {
                State = VpnState.Connected(DateTimeOffset.Now);
                ShouldAutoReconnect = true;
                StartWatchdog();
            }
            else
            {
                _openConnect.Stop();
                State = VpnState.Failed(handshake.Reason ?? "Connection failed");
            }
        }
        catch (Exception ex)
        {
            _openConnect.Stop();
            AppLogger.Shared.Error("connect error: " + ex.Message);
            State = VpnState.Failed("Connection error: " + ex.Message);
        }
    }

    public async Task DisconnectAsync()
    {
        ShouldAutoReconnect = false;
        await StopAndClearAsync();
        State = VpnState.Disconnected;
    }

    /// <summary>Network-loss disconnect — preserves ShouldAutoReconnect so recovery can retry.</summary>
    private async Task AutoDisconnectAsync()
    {
        await StopAndClearAsync();
        State = VpnState.Disconnected;
    }

    private Task StopAndClearAsync()
    {
        _watchdog?.Cancel();
        _watchdog = null;
        try { _openConnect.Stop(); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    public void MarkSetupIncomplete()
        => State = VpnState.Failed(VpnState.SetupIncompletePrefix + " — finish onboarding to connect.");

    private void OnReachabilityChanged(bool reachable)
    {
        if (!reachable && State.Status == VpnStatus.Connected)
        {
            _ = AutoDisconnectAsync();
        }
        else if (reachable && State.Status == VpnStatus.Disconnected && ShouldAutoReconnect)
        {
            _ = ConnectAsync();
        }
    }

    private void StartWatchdog()
    {
        _watchdog?.Cancel();
        _watchdog = new CancellationTokenSource();
        var token = _watchdog.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token).ContinueWith(_ => { });
                if (token.IsCancellationRequested) return;
                if (!_openConnect.IsRunning() && State.Status == VpnStatus.Connected)
                {
                    State = VpnState.Disconnected;
                    AppLogger.Shared.Info("watchdog: openconnect vanished, marking disconnected");
                    return;
                }
            }
        }, token);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/VPNTray.Tests --filter VpnControllerTests`
Expected: PASS (all 6).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add VpnController state machine"
```

---

### Task 13: DependencyChecker

**Files:**
- Create: `src/VPNTray/Dependencies/DependencyChecker.cs`
- Test: `src/VPNTray.Tests/Dependencies/DependencyCheckerTests.cs`

**Interfaces:**
- Consumes: `BundledBinaries`, `IProcessRunner`, `VpnConfig`.
- Produces:
  - `sealed record DependencyStatus(string Name, bool Passed, string? FixHint)`
  - `sealed class DependencyChecker(IProcessRunner runner)` with `IReadOnlyList<DependencyStatus> Check(VpnConfig config)`.
  - Checks: (1) openconnect.exe exists, (2) openconnect `--version` runs (exit 0), (3) wintun.dll present next to it, (4) vpnc script file exists.

- [ ] **Step 1: Write the failing tests with a fake runner**

Create `src/VPNTray.Tests/Dependencies/DependencyCheckerTests.cs`:
```csharp
using VPNTray.Config;
using VPNTray.Core;
using VPNTray.Dependencies;

namespace VPNTray.Tests.Dependencies;

internal sealed class FakeRunner : IProcessRunner
{
    public int ExitToReturn = 0;
    public ProcessResult Run(string executable, IReadOnlyList<string> arguments, int timeoutSeconds)
        => new(ExitToReturn, "OpenConnect version v9.12", "");
}

public class DependencyCheckerTests
{
    [Fact]
    public void Check_ReturnsOneStatusPerDependency()
    {
        var checker = new DependencyChecker(new FakeRunner());
        var statuses = checker.Check(new VpnConfig());
        Assert.Equal(4, statuses.Count);
        Assert.Contains(statuses, s => s.Name.Contains("openconnect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Check_VersionProbeFails_WhenRunnerReturnsNonZero()
    {
        var checker = new DependencyChecker(new FakeRunner { ExitToReturn = 1 });
        var statuses = checker.Check(new VpnConfig());
        var versionRow = statuses.First(s => s.Name.Contains("runs", StringComparison.OrdinalIgnoreCase));
        Assert.False(versionRow.Passed);
        Assert.NotNull(versionRow.FixHint);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/VPNTray.Tests --filter DependencyCheckerTests`
Expected: FAIL.

- [ ] **Step 3: Implement DependencyChecker**

Create `src/VPNTray/Dependencies/DependencyChecker.cs`:
```csharp
using VPNTray.Config;
using VPNTray.Core;

namespace VPNTray.Dependencies;

public sealed record DependencyStatus(string Name, bool Passed, string? FixHint);

public sealed class DependencyChecker
{
    private readonly IProcessRunner _runner;

    public DependencyChecker(IProcessRunner runner) => _runner = runner;

    public IReadOnlyList<DependencyStatus> Check(VpnConfig config)
    {
        var results = new List<DependencyStatus>();

        bool exeExists = File.Exists(config.OpenconnectPath);
        results.Add(new DependencyStatus(
            "openconnect.exe present", exeExists,
            exeExists ? null : "Bundled openconnect.exe is missing — reinstall VPNTray."));

        bool versionOk = false;
        if (exeExists)
        {
            try
            {
                var r = _runner.Run(config.OpenconnectPath, new[] { "--version" }, timeoutSeconds: 5);
                versionOk = r.Succeeded;
            }
            catch { versionOk = false; }
        }
        results.Add(new DependencyStatus(
            "openconnect runs", versionOk,
            versionOk ? null : "openconnect.exe failed to run — the bundle may be corrupt or blocked by SmartScreen."));

        bool wintunOk = File.Exists(BundledBinaries.WintunDll);
        results.Add(new DependencyStatus(
            "wintun.dll present", wintunOk,
            wintunOk ? null : "wintun.dll is missing next to openconnect.exe — reinstall VPNTray."));

        string script = config.SkipDnsModification ? BundledBinaries.VpncScriptNoDns : config.VpncScriptPath;
        bool scriptOk = File.Exists(script);
        results.Add(new DependencyStatus(
            "vpnc script present", scriptOk,
            scriptOk ? null : $"VPN config script not found at {script}."));

        return results;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/VPNTray.Tests --filter DependencyCheckerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add DependencyChecker (verifies bundled binaries)"
```

---

### Task 14: StartupManager (launch at login)

**Files:**
- Create: `src/VPNTray/Util/StartupManager.cs`
- Test: `src/VPNTray.Tests/Util/StartupManagerTests.cs`

**Interfaces:**
- Produces:
  - `sealed class StartupManager` with `bool IsEnabled()`, `void Enable()`, `void Disable()`. Uses `schtasks.exe` to register a task with highest privileges so the app starts elevated silently at logon. Intent flag stored at `HKCU\Software\VPNTray\LaunchAtLogin`.
  - Internal pure helper `static string BuildCreateArgs(string taskName, string exePath)` — testable without touching the registry/scheduler.

- [ ] **Step 1: Write the failing test for the pure arg builder**

Create `src/VPNTray.Tests/Util/StartupManagerTests.cs`:
```csharp
using VPNTray.Util;

namespace VPNTray.Tests.Util;

public class StartupManagerTests
{
    [Fact]
    public void BuildCreateArgs_RequestsHighestPrivilegesAndLogonTrigger()
    {
        var args = StartupManager.BuildCreateArgs("VPNTray", @"C:\Apps\VPNTray.exe");
        Assert.Contains("/RL", args);
        Assert.Contains("HIGHEST", args);
        Assert.Contains("/SC", args);
        Assert.Contains("ONLOGON", args);
        Assert.Contains(@"C:\Apps\VPNTray.exe", string.Join(" ", args));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/VPNTray.Tests --filter StartupManagerTests`
Expected: FAIL.

- [ ] **Step 3: Implement StartupManager**

Create `src/VPNTray/Util/StartupManager.cs`:
```csharp
using System.Diagnostics;
using Microsoft.Win32;

namespace VPNTray.Util;

public sealed class StartupManager
{
    private const string TaskName = "VPNTray";
    private const string RegistryKey = @"Software\VPNTray";
    private const string RegistryValue = "LaunchAtLogin";

    public static IReadOnlyList<string> BuildCreateArgs(string taskName, string exePath) => new[]
    {
        "/Create", "/F",
        "/TN", taskName,
        "/SC", "ONLOGON",
        "/RL", "HIGHEST",
        "/TR", $"\"{exePath}\"",
    };

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
        var value = key?.GetValue(RegistryValue) as int?;
        // Default-on: absent value means enabled (mirrors mac LoginItemManager).
        return value is null || value != 0;
    }

    public void Enable()
    {
        var exe = Process.GetCurrentProcess().MainModule!.FileName;
        RunSchtasks(BuildCreateArgs(TaskName, exe));
        SetPreference(true);
    }

    public void Disable()
    {
        RunSchtasks(new[] { "/Delete", "/F", "/TN", TaskName });
        SetPreference(false);
    }

    /// <summary>Sync the registered task to the stored preference. Called once at launch.</summary>
    public void ApplyPreference()
    {
        if (IsEnabled()) Enable(); else Disable();
    }

    private void SetPreference(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
        key.SetValue(RegistryValue, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    private static void RunSchtasks(IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            AppLogger.Shared.Error("schtasks failed: " + ex.Message);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/VPNTray.Tests --filter StartupManagerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add StartupManager (Scheduled Task, highest privileges)"
```

---

### Task 15: StatusIconFactory + tray icon assets

**Files:**
- Create: `src/VPNTray/UI/StatusIconFactory.cs`
- Create: `src/VPNTray/Resources/icons/` (4 `.ico` files, see Step 1)
- Test: `src/VPNTray.Tests/UI/StatusIconFactoryTests.cs`

**Interfaces:**
- Consumes: `VpnStatus`.
- Produces:
  - `static class StatusIconFactory` with `static string IconResourcePath(VpnStatus status)` returning the pack URI / file path for the status icon, and `static string DotColorName(VpnStatus status)` returning `"green"`/`"orange"`/`"red"`/`"gray"` (testable mapping).

- [ ] **Step 1: Create four placeholder tray icons**

Generate four 32×32 `.ico` files (a shield with a colored status dot) at
`src/VPNTray/Resources/icons/`: `connected.ico` (green), `connecting.ico`
(orange), `failed.ico` (red), `disconnected.ico` (gray). Any icon editor or a
short ImageMagick command works:
```bash
# Example (requires ImageMagick); colors per state:
magick -size 32x32 xc:none -fill "#34C759" -draw "circle 16,16 16,28" src/VPNTray/Resources/icons/connected.ico
magick -size 32x32 xc:none -fill "#FF9500" -draw "circle 16,16 16,28" src/VPNTray/Resources/icons/connecting.ico
magick -size 32x32 xc:none -fill "#FF3B30" -draw "circle 16,16 16,28" src/VPNTray/Resources/icons/failed.ico
magick -size 32x32 xc:none -fill "#8E8E93" -draw "circle 16,16 16,28" src/VPNTray/Resources/icons/disconnected.ico
```
These are placeholders; visual polish can come later.

- [ ] **Step 2: Write the failing test for the mapping**

Create `src/VPNTray.Tests/UI/StatusIconFactoryTests.cs`:
```csharp
using VPNTray.Core;
using VPNTray.UI;

namespace VPNTray.Tests.UI;

public class StatusIconFactoryTests
{
    [Theory]
    [InlineData(VpnStatus.Connected, "green")]
    [InlineData(VpnStatus.Connecting, "orange")]
    [InlineData(VpnStatus.Failed, "red")]
    [InlineData(VpnStatus.Disconnected, "gray")]
    public void DotColorName_MapsEachStatus(VpnStatus status, string expected)
        => Assert.Equal(expected, StatusIconFactory.DotColorName(status));

    [Fact]
    public void IconResourcePath_ReturnsStatusSpecificFile()
    {
        Assert.Contains("connected", StatusIconFactory.IconResourcePath(VpnStatus.Connected));
        Assert.Contains("disconnected", StatusIconFactory.IconResourcePath(VpnStatus.Disconnected));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test src/VPNTray.Tests --filter StatusIconFactoryTests`
Expected: FAIL.

- [ ] **Step 4: Implement StatusIconFactory and register icons in the csproj**

Create `src/VPNTray/UI/StatusIconFactory.cs`:
```csharp
using VPNTray.Core;

namespace VPNTray.UI;

public static class StatusIconFactory
{
    public static string DotColorName(VpnStatus status) => status switch
    {
        VpnStatus.Connected => "green",
        VpnStatus.Connecting => "orange",
        VpnStatus.Failed => "red",
        _ => "gray",
    };

    private static string FileFor(VpnStatus status) => status switch
    {
        VpnStatus.Connected => "connected",
        VpnStatus.Connecting => "connecting",
        VpnStatus.Failed => "failed",
        _ => "disconnected",
    };

    /// <summary>Pack URI for the tray icon resource of the given status.</summary>
    public static string IconResourcePath(VpnStatus status)
        => $"pack://application:,,,/Resources/icons/{FileFor(status)}.ico";
}
```

Add to `src/VPNTray/VPNTray.csproj` (so the icons are embedded resources):
```xml
<ItemGroup>
  <Resource Include="Resources/icons/*.ico" />
</ItemGroup>
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test src/VPNTray.Tests --filter StatusIconFactoryTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add StatusIconFactory and tray icons"
```

---

### Task 16: Settings and Onboarding windows

**Files:**
- Create: `src/VPNTray/UI/SettingsWindow.xaml`
- Create: `src/VPNTray/UI/SettingsWindow.xaml.cs`
- Create: `src/VPNTray/UI/OnboardingWindow.xaml`
- Create: `src/VPNTray/UI/OnboardingWindow.xaml.cs`

**Interfaces:**
- Consumes: `VpnConfig`, `ConfigStore`, `DependencyChecker`, `SystemProcessRunner`, `StartupManager`.
- Produces:
  - `SettingsWindow(ConfigStore store, StartupManager startup)` — edits all fields; `event Action? Saved`.
  - `OnboardingWindow(ConfigStore store, DependencyChecker checker, VpnConfig config)` — wizard; `event Action? Completed`, `event Action? Cancelled`.

This is a UI task: drive correctness by manual verification (Step 4), since WPF
windows are not unit-tested here. Keep code-behind thin — load config into
fields, validate on save, write via `ConfigStore`.

- [ ] **Step 1: Create SettingsWindow XAML**

Create `src/VPNTray/UI/SettingsWindow.xaml`:
```xml
<Window x:Class="VPNTray.UI.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="VPNTray Settings" Width="460" SizeToContent="Height"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize">
    <StackPanel Margin="16">
        <TextBlock Text="Username" Margin="0,4"/>
        <TextBox x:Name="UsernameBox"/>
        <TextBlock Text="Password prefix" Margin="0,8,0,4"/>
        <PasswordBox x:Name="PrefixBox"/>
        <TextBlock Text="TOTP secret (Base32)" Margin="0,8,0,4"/>
        <PasswordBox x:Name="TotpBox"/>
        <TextBlock Text="Gateway" Margin="0,8,0,4"/>
        <TextBox x:Name="GatewayBox"/>
        <TextBlock Text="Server cert pin" Margin="0,8,0,4"/>
        <TextBox x:Name="PinBox"/>
        <Expander Header="Advanced" Margin="0,12,0,0">
            <StackPanel>
                <TextBlock Text="openconnect.exe path" Margin="0,8,0,4"/>
                <TextBox x:Name="OpenconnectBox"/>
                <TextBlock Text="vpnc script path" Margin="0,8,0,4"/>
                <TextBox x:Name="ScriptBox"/>
                <CheckBox x:Name="SkipDnsBox" Content="Skip DNS modification" Margin="0,8,0,0"/>
                <CheckBox x:Name="LaunchAtLoginBox" Content="Launch at login" Margin="0,8,0,0"/>
            </StackPanel>
        </Expander>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button x:Name="CancelButton" Content="Cancel" Width="80" Margin="0,0,8,0" Click="OnCancel"/>
            <Button x:Name="SaveButton" Content="Save" Width="80" IsEnabled="False" Click="OnSave"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Create SettingsWindow code-behind**

Create `src/VPNTray/UI/SettingsWindow.xaml.cs`:
```csharp
using System.Windows;
using VPNTray.Config;
using VPNTray.Util;

namespace VPNTray.UI;

public partial class SettingsWindow : Window
{
    private readonly ConfigStore _store;
    private readonly StartupManager _startup;

    public event Action? Saved;

    public SettingsWindow(ConfigStore store, StartupManager startup)
    {
        _store = store;
        _startup = startup;
        InitializeComponent();
        Load();
        WireChangeTracking();
    }

    private void Load()
    {
        var c = _store.Load() ?? new VpnConfig();
        UsernameBox.Text = c.Username;
        PrefixBox.Password = c.PasswordPrefix;
        TotpBox.Password = c.TotpSecret;
        GatewayBox.Text = c.Gateway;
        PinBox.Text = c.ServerCertPin;
        OpenconnectBox.Text = c.OpenconnectPath;
        ScriptBox.Text = c.VpncScriptPath;
        SkipDnsBox.IsChecked = c.SkipDnsModification;
        LaunchAtLoginBox.IsChecked = _startup.IsEnabled();
    }

    private void WireChangeTracking()
    {
        void Enable() => SaveButton.IsEnabled = true;
        UsernameBox.TextChanged += (_, _) => Enable();
        GatewayBox.TextChanged += (_, _) => Enable();
        PinBox.TextChanged += (_, _) => Enable();
        OpenconnectBox.TextChanged += (_, _) => Enable();
        ScriptBox.TextChanged += (_, _) => Enable();
        PrefixBox.PasswordChanged += (_, _) => Enable();
        TotpBox.PasswordChanged += (_, _) => Enable();
        SkipDnsBox.Click += (_, _) => Enable();
        LaunchAtLoginBox.Click += (_, _) => Enable();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var c = new VpnConfig
        {
            Username = UsernameBox.Text,
            PasswordPrefix = PrefixBox.Password,
            TotpSecret = TotpBox.Password,
            Gateway = GatewayBox.Text,
            ServerCertPin = PinBox.Text,
            OpenconnectPath = OpenconnectBox.Text,
            VpncScriptPath = ScriptBox.Text,
            SkipDnsModification = SkipDnsBox.IsChecked == true,
        }.Sanitized();

        _store.Save(c);
        if (LaunchAtLoginBox.IsChecked == true) _startup.Enable(); else _startup.Disable();
        Saved?.Invoke();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 3: Create OnboardingWindow XAML and code-behind**

Create `src/VPNTray/UI/OnboardingWindow.xaml`:
```xml
<Window x:Class="VPNTray.UI.OnboardingWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="VPNTray Setup" Width="480" Height="420"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Step 1: Welcome -->
        <StackPanel x:Name="WelcomePane" Grid.Row="0">
            <TextBlock Text="Welcome to VPNTray" FontSize="20" FontWeight="Bold"/>
            <TextBlock TextWrapping="Wrap" Margin="0,12,0,0"
                Text="This wizard checks the bundled VPN tools and collects your VPN credentials. Click Next to begin."/>
        </StackPanel>

        <!-- Step 2: Dependency check -->
        <StackPanel x:Name="DepsPane" Grid.Row="0" Visibility="Collapsed">
            <TextBlock Text="Dependency Check" FontSize="18" FontWeight="Bold"/>
            <ItemsControl x:Name="DepsList" Margin="0,12,0,0"/>
            <Button x:Name="RecheckButton" Content="Recheck" Width="100" HorizontalAlignment="Left"
                    Margin="0,12,0,0" Click="OnRecheck"/>
        </StackPanel>

        <!-- Step 3: Credentials -->
        <StackPanel x:Name="CredsPane" Grid.Row="0" Visibility="Collapsed">
            <TextBlock Text="Credentials" FontSize="18" FontWeight="Bold"/>
            <TextBlock Text="Username" Margin="0,8,0,2"/>
            <TextBox x:Name="OnbUsername"/>
            <TextBlock Text="Password prefix" Margin="0,8,0,2"/>
            <PasswordBox x:Name="OnbPrefix"/>
            <TextBlock Text="TOTP secret (Base32)" Margin="0,8,0,2"/>
            <PasswordBox x:Name="OnbTotp"/>
            <TextBlock Text="Gateway" Margin="0,8,0,2"/>
            <TextBox x:Name="OnbGateway"/>
            <TextBlock Text="Server cert pin" Margin="0,8,0,2"/>
            <TextBox x:Name="OnbPin"/>
        </StackPanel>

        <!-- Step 4: Done -->
        <StackPanel x:Name="DonePane" Grid.Row="0" Visibility="Collapsed">
            <TextBlock Text="All set" FontSize="18" FontWeight="Bold"/>
            <TextBlock TextWrapping="Wrap" Margin="0,12,0,0"
                Text="Setup complete. Click Done to return to the tray. Click the tray icon to connect."/>
        </StackPanel>

        <StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button x:Name="BackButton" Content="Back" Width="80" Margin="0,0,8,0" Click="OnBack"/>
            <Button x:Name="NextButton" Content="Next" Width="80" Click="OnNext"/>
        </StackPanel>
    </Grid>
</Window>
```

Create `src/VPNTray/UI/OnboardingWindow.xaml.cs`:
```csharp
using System.Windows;
using System.Windows.Controls;
using VPNTray.Config;
using VPNTray.Dependencies;

namespace VPNTray.UI;

public partial class OnboardingWindow : Window
{
    private readonly ConfigStore _store;
    private readonly DependencyChecker _checker;
    private int _step;
    private bool _completed;

    public event Action? Completed;
    public event Action? Cancelled;

    private readonly StackPanel[] _panes;

    public OnboardingWindow(ConfigStore store, DependencyChecker checker)
    {
        _store = store;
        _checker = checker;
        InitializeComponent();
        _panes = new[] { WelcomePane, DepsPane, CredsPane, DonePane };
        PrefillCredentials();
        ShowStep(0);
        Closing += (_, _) => { if (!_completed) Cancelled?.Invoke(); };
    }

    private void PrefillCredentials()
    {
        var c = _store.Load() ?? new VpnConfig();
        OnbUsername.Text = c.Username;
        OnbPrefix.Password = c.PasswordPrefix;
        OnbTotp.Password = c.TotpSecret;
        OnbGateway.Text = c.Gateway;
        OnbPin.Text = c.ServerCertPin;
    }

    private void ShowStep(int step)
    {
        _step = step;
        for (int i = 0; i < _panes.Length; i++)
            _panes[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = step > 0;
        NextButton.Content = step == _panes.Length - 1 ? "Done" : "Next";
        if (step == 1) RunDependencyCheck();
    }

    private void RunDependencyCheck()
    {
        var statuses = _checker.Check(_store.Load() ?? new VpnConfig());
        DepsList.ItemsSource = statuses.Select(s =>
            (s.Passed ? "OK  " : "X   ") + s.Name + (s.FixHint is null ? "" : "  — " + s.FixHint));
    }

    private void OnRecheck(object sender, RoutedEventArgs e) => RunDependencyCheck();

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 0) ShowStep(_step - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 2 && !SaveCredentials()) return; // validate before leaving creds
        if (_step == _panes.Length - 1)
        {
            _completed = true;
            Completed?.Invoke();
            Close();
            return;
        }
        ShowStep(_step + 1);
    }

    private bool SaveCredentials()
    {
        var c = new VpnConfig
        {
            Username = OnbUsername.Text,
            PasswordPrefix = OnbPrefix.Password,
            TotpSecret = OnbTotp.Password,
            Gateway = OnbGateway.Text,
            ServerCertPin = OnbPin.Text,
        }.Sanitized();

        if (!c.IsConfigured)
        {
            MessageBox.Show("Please fill in all five fields (username, password prefix, TOTP secret, gateway, cert pin).",
                "Incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        // Preserve advanced fields from any existing config.
        var existing = _store.Load() ?? new VpnConfig();
        c.OpenconnectPath = existing.OpenconnectPath;
        c.VpncScriptPath = existing.VpncScriptPath;
        c.SkipDnsModification = existing.SkipDnsModification;
        _store.Save(c);
        return true;
    }
}
```

- [ ] **Step 4: Build and manually verify both windows**

Run: `dotnet build VPNTray.sln`
Expected: build succeeds. (Manual visual verification happens in Task 17 once the
app can launch them from the tray.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add Settings and Onboarding windows"
```

---

### Task 17: App wiring — tray, coordinator, lifecycle, resource bundling

**Files:**
- Modify: `src/VPNTray/App.xaml`
- Create/Replace: `src/VPNTray/App.xaml.cs`
- Create: `src/VPNTray/App/AppCoordinator.cs`
- Modify: `src/VPNTray/VPNTray.csproj` (bundle native binaries)
- Delete: `src/VPNTray/MainWindow.xaml` + `.cs` (the WPF template's main window — unused)

**Interfaces:**
- Consumes: every component built above.
- Produces: a runnable elevated tray app.

- [ ] **Step 1: Place the native binaries and mark them for copy**

Obtain `openconnect.exe`, `wintun.dll`, and `vpnc-script-win.js` from an official
OpenConnect Windows build (v9.x). Copy a DNS-skipping variant of the script to
`vpnc-script-win--no-dns.js` (start from the bundled script; comment out the
DNS-configuration section). Place all four in `src/VPNTray/Resources/`.

Add to `src/VPNTray/VPNTray.csproj`:
```xml
<ItemGroup>
  <None Include="Resources/openconnect.exe" CopyToOutputDirectory="PreserveNewest" />
  <None Include="Resources/wintun.dll" CopyToOutputDirectory="PreserveNewest" />
  <None Include="Resources/vpnc-script-win.js" CopyToOutputDirectory="PreserveNewest" />
  <None Include="Resources/vpnc-script-win--no-dns.js" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```
(`CopyToOutputDirectory` keeps them next to the exe under `Resources/`, matching `BundledBinaries.BaseDir`.)

- [ ] **Step 2: Replace App.xaml to suppress the startup window**

Replace `src/VPNTray/App.xaml`:
```xml
<Application x:Class="VPNTray.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown"
             Startup="OnStartup">
    <Application.Resources/>
</Application>
```
Delete `src/VPNTray/MainWindow.xaml` and `src/VPNTray/MainWindow.xaml.cs`.

- [ ] **Step 3: Implement AppCoordinator (tray + windows + lifecycle)**

Create `src/VPNTray/App/AppCoordinator.cs`:
```csharp
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using VPNTray.Config;
using VPNTray.Core;
using VPNTray.Dependencies;
using VPNTray.UI;
using VPNTray.Util;

namespace VPNTray.App;

public sealed class AppCoordinator
{
    private readonly ConfigStore _store = new();
    private readonly StartupManager _startup = new();
    private readonly VpnController _controller;
    private readonly DependencyChecker _checker = new(new SystemProcessRunner());
    private TaskbarIcon _tray = null!;

    public AppCoordinator()
    {
        var openConnect = new OpenConnectProcess();
        var network = new NetworkMonitor();
        _controller = new VpnController(openConnect, network, () => _store.Load());
        _controller.PropertyChanged += (_, _) => Application.Current.Dispatcher.Invoke(UpdateTray);
    }

    public void Start()
    {
        _startup.ApplyPreference();
        _tray = new TaskbarIcon { ToolTipText = "VPNTray" };
        BuildMenu();
        _controller.StartNetworkMonitoring();

        var config = _store.Load();
        if (config is null || !config.IsConfigured)
        {
            _controller.MarkSetupIncomplete();
            ShowOnboarding();
        }
        UpdateTray();
    }

    private void BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        void Add(string header, RoutedEventHandler click, Func<bool>? visible = null)
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += click;
            if (visible is not null) item.Visibility = visible() ? Visibility.Visible : Visibility.Collapsed;
            menu.Items.Add(item);
        }

        Add("Connect", async (_, _) => await _controller.ConnectAsync());
        Add("Disconnect", async (_, _) => await _controller.DisconnectAsync());
        Add("Reconnect", async (_, _) => { await _controller.DisconnectAsync(); await _controller.ConnectAsync(); });
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Open Settings…", (_, _) => ShowSettings());
        Add("Check Dependencies…", (_, _) => ShowOnboardingAtDeps());
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add("Quit", (_, _) => Quit());

        _tray.ContextMenu = menu;
    }

    private void UpdateTray()
    {
        var status = _controller.State.Status;
        _tray.IconSource = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(StatusIconFactory.IconResourcePath(status)));
        _tray.ToolTipText = $"VPNTray — {status}";

        // Setup-incomplete lock-down: hide connect/disconnect/reconnect/settings.
        bool incomplete = _controller.State.IsSetupIncomplete;
        var items = _tray.ContextMenu.Items;
        foreach (var obj in items)
        {
            if (obj is System.Windows.Controls.MenuItem mi)
            {
                var header = mi.Header?.ToString() ?? "";
                if (header is "Connect" or "Disconnect" or "Reconnect" or "Open Settings…")
                    mi.Visibility = incomplete ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private void ShowSettings()
    {
        var win = new SettingsWindow(_store, _startup);
        win.Saved += async () =>
        {
            if (_controller.State.Status == VpnStatus.Connected) await _controller.DisconnectAsync();
            UpdateTray();
        };
        win.Show();
        win.Activate();
    }

    private void ShowOnboarding()
    {
        var win = new OnboardingWindow(_store, _checker);
        win.Completed += () => UpdateTray();
        win.Cancelled += () => { if (!( _store.Load()?.IsConfigured ?? false)) _controller.MarkSetupIncomplete(); };
        win.Show();
        win.Activate();
    }

    private void ShowOnboardingAtDeps() => ShowOnboarding();

    private void Quit()
    {
        try { _controller.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _tray.Dispose();
        Application.Current.Shutdown();
    }
}
```

- [ ] **Step 4: Implement App.xaml.cs (single-instance + start coordinator + disconnect on exit)**

Create `src/VPNTray/App.xaml.cs`:
```csharp
using System.Windows;
using VPNTray.App;
using VPNTray.Util;

namespace VPNTray;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private AppCoordinator? _coordinator;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (!SingleInstance.TryAcquire(out _instanceMutex))
        {
            MessageBox.Show("VPNTray is already running.", "VPNTray");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Shared.Error("unhandled: " + args.Exception);
            args.Handled = true;
        };

        _coordinator = new AppCoordinator();
        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 5: Build, run elevated, and smoke-test**

Run: `dotnet build VPNTray.sln -c Release`
Then launch the built `VPNTray.exe` (accept the UAC prompt). Verify:
- Tray icon appears (grey/disconnected).
- On first run with no config, the Onboarding wizard opens; completing it removes the setup-incomplete lock-down.
- Right-click → Open Settings shows the saved values.
- With a real VPN configured, Connect turns the icon green; Disconnect returns it to grey; Quit leaves no `openconnect.exe` running (check Task Manager).

Expected: all of the above hold. Record any failures and fix before committing.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test VPNTray.sln`
Expected: all tests pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: wire tray app (coordinator, lifecycle, bundled binaries)"
```

---

### Task 18: README / install docs

**Files:**
- Create: `README.md`
- Create: `INSTALL.md`

**Interfaces:**
- Produces: user-facing docs (English README + Chinese INSTALL, mirroring the mac repo's split).

- [ ] **Step 1: Write README.md**

Cover: what it is (generic OpenConnect front-end, placeholders are user-supplied),
requirements (Windows 10/11 x64, runs elevated), install (copy folder / run exe,
accept UAC), first-launch onboarding, the bundled-binaries note, config location
(`%APPDATA%\VPNTray\config.json`, ACL-locked, never commit), and the
disconnect-on-quit / auto-reconnect behaviors.

- [ ] **Step 2: Write INSTALL.md (Chinese)**

Mirror the mac `INSTALL.md` structure adapted for Windows: 安装、首次启动（UAC）、
Onboarding 三步、TOTP 密钥获取、日常使用（连接/断开/设置/退出）、自动化行为
（开机自启、断网自动重连、跳过 DNS）、常见问题、配置文件与隐私、卸载。
Note bundled openconnect (no Homebrew step), and that the whole app runs elevated.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "docs: add README and INSTALL"
```

---

## Self-Review Notes

- **Spec coverage:** Every spec §3.1 component maps to a task (Totp→T2, BundledBinaries→T3, VpnConfig→T4, ConfigStore→T5, VpnState→T6, ProcessRunner→T7, handshake/OpenConnect→T8/T9, Logger/SingleInstance→T10, NetworkMonitor→T11, VpnController→T12, DependencyChecker→T13, StartupManager→T14, StatusIcon→T15, Settings/Onboarding→T16, App/Coordinator/manifest/bundling→T17, docs→T18). Elevation manifest = T1/T17. Skip-DNS toggle = T9 arg builder + T17 no-dns script. Stale-route cleanup (spec §6, explicitly best-effort/deferred) is intentionally NOT a task in v1 — flagged here so it isn't mistaken for a gap.
- **Out-of-scope confirmed:** Sparkle auto-update, QR import — not in any task, per spec §10.
- **Type consistency:** `IOpenConnectProcessRunning`, `OpenConnectHandshake(bool Connected, string? Reason)`, `VpnState` factories, `DependencyStatus`, `ProcessResult` are used identically across producing and consuming tasks.
