# Getting the page onto the headset

The Quest browser only exposes WebXR in a **secure context**. That single rule explains every
connection problem this project has had.

| Origin | Secure? | WebXR |
|---|---|---|
| `http://localhost:8765` | yes (localhost is special-cased) | works |
| `https://<lan-ip>:8766` | yes | works, after accepting the certificate once |
| `http://192.168.x.x:8765` | **no** | page loads, `navigator.xr` is undefined, Enter VR stays dead |

## Two ways in

**1. adb reverse — no certificate at all.** The plugin runs `adb reverse tcp:8765 tcp:8765`, which
makes this PC's server appear on the *headset's own* `localhost`. Browsers treat that as secure, so
WebXR is allowed and nothing has to be trusted. This is the one-click path:

```powershell
adb devices                                     # must list the headset as "device"
adb reverse tcp:8765 tcp:8765
adb shell am start -a android.intent.action.VIEW -d http://localhost:8765/
```

Needs Developer Mode on the headset (Meta Horizon phone app) and "Allow USB debugging" accepted.
Link and casting are not adb — they never appear as a device.

**2. HTTPS over Wi-Fi.** No cable needed. The server listens on `<port>+1` over TLS with a
self-signed certificate covering this machine's LAN address. Accept the warning once
(*Advanced ▸ Proceed*) and the browser remembers it.

## Ports

| Server | HTTP loopback | HTTPS LAN | Headset URL |
|---|---|---|---|
| Revit plugin (`RevitVr.Server`) | 8765 | 8766 | `https://<lan-ip>:8766/` |
| IngeTrazo plugin (embedded) | 8770 | 8771 | `https://<lan-ip>:8771/` |

Both transports run at the same time, so the PC and the headset never disagree about which page is
being served.

## Certificates

- **Revit plugin:** `%LOCALAPPDATA%\RevitVR\cert.pfx`, reused across runs and rebuilds so the
  headset only warns once. Regenerate with `--new-cert` if the machine's address changes; the
  certificate's SAN list includes the LAN address at creation time.
- **IngeTrazo plugin:** `cert.pem` / `key.pem` under `%LOCALAPPDATA%\IngeTrazo\VR`, regenerated
  automatically when the address changes.

## Firewall

Windows Firewall drops inbound connections to an unknown executable, so the headset cannot reach
the HTTPS address until the server exe is allowed. `plugins/revit/build.ps1` adds an inbound rule
for `RevitVr.Server.exe` on Private and Domain networks when run as Administrator. If the page
times out, that rule is the first thing to check.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Page loads, Enter VR greyed out | plain-http origin: use the https address, or the adb path |
| Nothing loads at all | server not running, wrong port, or firewall |
| Loads on the PC but not on the headset | you are on `localhost`; the headset needs the LAN address |
| `adb reverse` does nothing | headset not listed as `device` in `adb devices` |
| Certificate warning every time | the certificate was regenerated (address changed) |
| Library page says "Library unavailable" | the server predates the library; restart it from the current build |

The plugins print the addresses they are serving in their export dialogs; when in doubt, use those
rather than a bookmark.
