# Release notes

## 1.4.0 — 2026-08-26

- Connect-at-login now waits for the network instead of failing into it. At logon the
  app waits (up to two minutes) for the network to come up before dialling, retries a
  few times with backoff once it is up, and — if the network still is not there — dials
  automatically as soon as it appears. Previously a slow Wi-Fi/DHCP at logon made the
  automatic connection fail once and give up for the session.
- A connection attempt defeated by a missing network is now retried automatically when
  the network returns. Manually started connections keep the old behavior: a failure
  stays put until you act.
