# VPNTray — project instructions

## Before every commit

Check the staged diff for sensitive information before committing — real usernames,
passwords, TOTP secrets, gateway hostnames/IPs of the actual corporate VPN, certificate
pins, tokens, or anything copied out of a real config. Test fixtures must use obviously
fake values (`u`, `pw`, example pins). If something sensitive already slipped into
history, rotate the secret — deleting the line is not enough.

## Versioning and release notes

The version lives in one place: `<Version>` in `src/VPNTray/VPNTray.csproj`.
`scripts/build-installer.ps1` reads it and stamps the assembly, the MSI ProductVersion,
and the output file names — never edit the version anywhere else.

Bump the version and write the release note **only when packaging** (running
`scripts/build-installer.ps1`), not on every change. Work accumulates on `master`
between releases; the packaging step is what turns it into a versioned release.

### How to bump

- **Bug fixes only** since the last release → increment the **patch** (last) digit:
  `1.3.0` → `1.3.1`.
- **Any new feature** since the last release → increment the **minor** (second) digit
  and reset the patch to 0: `1.3.0` → `1.4.0`. Features win over fixes when a release
  contains both.

Decide from the commits since the previous `build: X.Y.Z` commit: `feat:` commits mean
a minor bump, only `fix:`/`refactor:`/`chore:`-style commits mean a patch bump.

### Release notes

Every package gets a release note entry in `RELEASE_NOTES.md` at the repo root,
newest release first. Format:

```markdown
## 1.4.0 — 2026-08-26

- Connect-at-login now waits for the network instead of failing into it, and
  retries with backoff once the network is up.
- Fix: …
```

Write the entries for users, not for git archaeologists: say what changed in behavior,
one bullet per user-visible change, derived from the commits since the last release.
Internal-only refactors don't need a bullet.

### Packaging checklist

1. Run the tests: `dotnet test VPNTray.sln`.
2. Decide the bump (feature → minor, fixes only → patch) and update `<Version>` in
   `src/VPNTray/VPNTray.csproj`.
3. Add the release entry to `RELEASE_NOTES.md`.
4. Commit both together as `build: X.Y.Z`.
5. Run `scripts/build-installer.ps1` — output lands in `dist/`.
