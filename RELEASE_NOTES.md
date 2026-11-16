# Release notes

## 1.6.0 — 2026-11-16

- When the VPN gateway drops the secure connection before the login form, the app now
  tries the gateway's other servers on its own instead of failing. The gateway name points
  at several servers, and one of them refusing connections no longer stops you from
  connecting. Your one-time code is not used up by these retries.

## 1.5.1 — 2026-11-16

- Fix: when the VPN gateway drops the secure connection before the login form, the app
  now says the gateway could not be reached instead of asking you to check your password,
  and keeps reconnecting automatically. In 1.5.0 this was mistaken for a rejected login,
  which also switched automatic reconnects off.

## 1.5.0 — 2026-11-16

- New "One-time code" setting for gateways that want the 6-digit code typed straight
  after the password in the same prompt (password `Abc#2026` + code `123456` →
  `Abc#2026123456`). This is now the default; choose "Separate code prompt" in
  Settings for a gateway that still asks for the code in a second prompt.
- The app never sends the same one-time code twice: a reconnect within the same
  30-second window, or with a code about to expire, waits for the next code instead.
- After the gateway rejects a login, the app stops reconnecting on its own (at logon
  and when the network returns) until you connect again, so repeated failures do not
  lock your account.

## 1.4.0 — 2026-08-26

- Connect-at-login now waits for the network instead of failing into it. At logon the
  app waits (up to two minutes) for the network to come up before dialling, retries a
  few times with backoff once it is up, and — if the network still is not there — dials
  automatically as soon as it appears. Previously a slow Wi-Fi/DHCP at logon made the
  automatic connection fail once and give up for the session.
- A connection attempt defeated by a missing network is now retried automatically when
  the network returns. Manually started connections keep the old behavior: a failure
  stays put until you act.
