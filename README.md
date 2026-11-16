<p align="center"><img src="agent/PhoneBox.Agent/phonebox.ico" width="72" alt=""></p>

<h1 align="center">Couchpad</h1>

<p align="center"><b>Turn your Android phone into a TV remote for your laptop: touchpad, keyboard and motion mouse, over USB or Wi-Fi.</b><br>
Built on top of PhoneBox: **116 widgets** and **31 templates** for PC monitoring, AI coding sessions, media controls, webhooks and laptop remote control — no cloud.</p>

> **Fork & Credit Notice**
>
> **Couchpad** is a fork of [PhoneBox](https://github.com/ersinkoc/phonebox) by **Ersin KOÇ (`ersinkoc`)** under the [MIT License](LICENSE), with a new **Remote** mode added.
> Original project: <https://github.com/ersinkoc/phonebox>
> All credit for the desk panel architecture, widget/template catalog, Android shell, pairing system, and desktop agent goes to the original author.

<p align="center">
  <img src="https://img.shields.io/badge/Windows%2010%2F11%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-9%2B-3ddc84" alt="Android 9+">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT license"></a>
</p>

<!-- TODO: Replace with real screenshots of the Couchpad Remote page (Touchpad & Motion modes), e.g.:
     <p align="center"><img src="docs/screenshots/remote-touchpad.png" width="780" alt="Couchpad Remote Touchpad and Motion modes"></p> -->

**Contents**: [What's different from PhoneBox](#whats-different-from-phonebox) · [What it does](#what-it-does) · [Requirements](#requirements) · [Setup](#setup) · [Wi-Fi setup](#wi-fi-setup) · [Using the Remote page](#using-the-remote-page) · [Low-latency Wi-Fi tips](#low-latency-wi-fi-tips) · [Security](#security) · [Troubleshooting](#troubleshooting) · [Development](#development) · [Credits & License](#credits--license)

## What's different from PhoneBox

Couchpad keeps every existing PhoneBox desk panel feature intact (and leaves internal package IDs, namespaces, config paths, and pairing compatibility untouched), while adding:

- **Agent input injection (`POST /api/remote`)**: Windows `SendInput` (and best-effort Linux `xdotool`) mouse movement, clicks, double-clicks, drag hold/release with a 5-second stuck-button timeout, vertical/horizontal scrolling, key combos, and Unicode typing.
- **Dedicated Remote page template & `remote` widget (`12 × 6`, min `4 × 3`, `idleSec: 0`)**: touchpad gestures, soft keyboard forwarding, quick-key strip, media & volume controls, app shortcut pills, and an instant **Disable remote** kill switch.
- **Gyroscope Motion Mode**: TV-remote-style air mouse using Android `Sensor.TYPE_GYROSCOPE` (`SENSOR_DELAY_GAME`), gated behind a big **Hold to move** button with configurable axes, X/Y inversion, sensitivity, EMA smoothing, and dead zone.
- **Wi-Fi Latency Optimizations**: pinned-TLS connection reuse with eager response draining, `TCP_NODELAY`, skipped redundant USB probes while on Wi-Fi, a low-latency `WifiLock` while the Remote page is active, a `1000 ms` read timeout, and a native Kotlin coalescing sender thread.
- **Fork Auto-Update Safety**: `updates.auto` defaults to `false`, background auto-updates never overwrite non-single-file local source builds, and the default update repository points to `skander26/Couchpad` (configurable in **Settings › General › Updates**).

## What it does

### Remote mode (new in Couchpad)

| Gesture / Control | Action on your computer |
|---|---|
| **1-finger drag** on touchpad | Move cursor (with mild velocity curve) |
| **1-finger tap** on touchpad | Left click |
| **2-finger tap** on touchpad | Right click |
| **2-finger drag** on touchpad | Vertical and horizontal scroll |
| **Double-tap & drag** on touchpad | Hold left mouse button to drag/select, release on lift |
| **Left click / Right click** buttons | Direct mouse click buttons (available in both Touchpad and Motion modes) |
| **⌨ Keyboard** button | Opens the Android soft keyboard and sends text (`op=text`) plus `Backspace`/`Enter`/`Esc`/`Tab`/arrows (`op=key`) |
| **Quick-key strip** | One-tap `Esc`, `Tab`, `Win`, `Alt+Tab`, `←`, `↑`, `↓`, `→`, `⌫`, `↵` |
| **Media & volume rows** | `⏮`, `⏯`, `⏭`, `🔇`, `🔉 −`, `🔊 +` with live mute/percentage status |
| **App shortcut pills** | Launches your configured PC shortcuts from **Settings › Shortcuts** using their IDs only |
| **Motion mode (`Motion` tab)** | Hold **✋ Hold to move** to steer the cursor by aiming/tilting the phone; **Scroll ▲ / ▼** buttons alongside; **⚙ Tuning** (`X`/`Y` axis `Yaw`/`Pitch`/`Roll`, `Invert X`/`Y`, `Sens`, `Smooth` EMA, `Dead` zone) saved in `localStorage` |
| **🛑 Disable remote** | Immediately turns off **Allow remote input** on the PC and releases any held mouse buttons |

### Desk panel features (from PhoneBox)

Couchpad still includes the full PhoneBox visual editor and **116 widgets** across **31 templates**:

- **PC at a glance**: CPU, GPU, RAM, disks, network, ping, heaviest processes with termination controls, battery care, device batteries.
- **AI coding & Dev**: Claude and Codex quotas/sessions, native Antigravity quotas, Claude Code **Allow / Deny** permission prompts, GitHub review requests & PRs, PowerShell/bash/zsh command widgets.
- **Control Deck & companion**: Now Playing, application audio mixer, OBS Studio, Home Assistant toggles, Smart Stack cards, clocks, weather, calendars, pomodoro sequences, checklists, notes, OLED burn-in protection, webhooks, and rules.

## Requirements

| Platform / Component | Support & Notes |
|---|---|
| **PC: Windows 10 / 11** | Full Remote support (`SendInput` for cursor, buttons, scroll, shortcuts, Unicode text), tray menu toggle, audio mixer & media |
| **PC: Linux** | Best-effort mouse/scroll/key injection on X11 via `xdotool` (Wayland text/keys via `wtype`); `.NET 10` agent (`-f net10.0`) |
| **PC: macOS** | Desk panel and shortcuts work; **mouse injection via `/api/remote` is not supported on macOS** (returns an error without crashing) |
| **Phone** | **Android 9+** (`minSdk = 28`). Hardware gyroscope required for Motion mode (Touchpad works on any device) |
| **Tools for setup / build** | [.NET 10 SDK](https://dotnet.microsoft.com/download), `adb` (Android Platform Tools); **JDK 17** and **Android SDK Platform 35** to rebuild the APK from source; **USB debugging** enabled on the phone for initial USB install/pairing |

## Setup

### 1. Install prerequisites (Windows PowerShell)

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Google.PlatformTools
```

If you also want to rebuild the Android APK from source (`-BuildApk`), install JDK 17 and point `JAVA_HOME` and `ANDROID_HOME` at your JDK and Android SDK (for example from Android Studio):

```powershell
winget install EclipseAdoptium.Temurin.17.JDK
$env:JAVA_HOME = (Get-ChildItem 'C:\Program Files\Eclipse Adoptium\jdk-17*' | Select-Object -First 1).FullName
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
```

### 2. Clone and install agent + Android app

1. Turn on **USB debugging** on your Android phone (*Settings › About phone › tap Build number 7 times › Developer options › USB debugging*; on Xiaomi/Redmi/POCO also enable *Install via USB*), plug it into your laptop with a data USB cable, and accept the debugging prompt.
2. Clone the repo and run the installer:

```powershell
git clone https://github.com/skander26/Couchpad.git
cd Couchpad
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Apk -Autostart
```

*(Pass `-BuildApk` if you changed Java/Kotlin/WebView sources and want Gradle to rebuild `app-release.apk` first.)*

If updating over an existing upstream PhoneBox APK with a different signing key, uninstall the existing package once before running `install.ps1 -Apk` (all PC settings in `%APPDATA%\PhoneBox` are preserved):

```powershell
adb uninstall net.oxog.phonebox.deskpanel
```

### 3. Enable Remote input and add the Remote page

1. Open **Settings** on your PC at <http://127.0.0.1:8765> (or double-click the PhoneBox tray icon).
2. Under **General**, turn on **Allow remote input (mouse and keyboard control from the phone)**.
3. Under **Pages**, click **＋ New page**, select **🖱️ Remote**, and click **Save**.
4. Tap the page dots at the bottom of the phone screen (or swipe across pages) and open **🖱️ Remote**.

## Wi-Fi setup

Once paired, unplug the USB cable and use Couchpad across the room:

1. In <http://127.0.0.1:8765> › **General › Wi-Fi & Tailscale**, turn on **Let the phone connect without the USB cable** and click **Save**.
2. Restart the PC agent (click **Restart** in Settings or **Quit** from the tray menu and start it again) so the HTTPS listener on port `8766` starts.
3. **Allow Windows Firewall** when prompted (enable **Private** networks; on Linux open TCP port `8766` on your LAN).
4. Pair your phone using either method:
   - **Over USB**: plug the phone in once for a couple of seconds (`AgentLink` automatically fetches the PC's LAN/Tailscale IPs, paired bearer token, and TLS certificate SHA-256 fingerprint via `/api/pair`), then unplug the cable.
   - **Without a cable (QR code)**: click **Pair a phone over Wi-Fi** in Settings, scan the pairing QR code with your phone camera, open in PhoneBox, verify that the 8-character check code matches on both screens, and tap **Pair**.

## Using the Remote page

1. **Choose a mode**: tap **Touchpad** (classic laptop trackpad) or **Motion** (TV-remote air mouse) at the top of the widget.
2. **Touchpad mode**: slide one finger to move the cursor, tap once to left-click, tap with two fingers to right-click, drag two fingers to scroll, or double-tap-and-drag to move windows or select text.
3. **Motion mode**: press and hold **✋ Hold to move**, aim/tilt your wrist to move the cursor, and release to pause ("clutch") while repositioning your hand. Open **⚙ Tuning** to pick `X`/`Y` axes (`Yaw`/`Pitch`/`Roll`), flip **Invert X** / **Invert Y**, and tune **Sens**, **Smooth**, and **Dead** zone. Use **Scroll ▲ / ▼**, **Left click**, and **Right click** around it.
4. **Type & trigger shortcuts**: tap **⌨ Keyboard** for full Android soft-keyboard typing, use the quick-key bar (`Win`, `Alt+Tab`, `Esc`, arrows), control playback/volume on the right, or tap your shortcut pills.
5. **Quick shutoff**: tap **🛑 Disable remote** on the phone (or right-click the tray icon › **Allow remote input**) to disable PC input immediately.

## Low-latency Wi-Fi tips

Couchpad includes several transport optimizations for Wi-Fi remote responsiveness:
- **Pinned-TLS keep-alive pooling**: singleton `HostnameVerifier` reference and eager draining/closing of `/api/remote` response bodies inside `AgentLink.send()` so Android's `HttpURLConnection` reuses the existing TLS socket instead of performing a new TLS handshake per move.
- **`TCP_NODELAY` & socket keep-alive**: enabled on wrapped Android `SSLSocket` connections (and Kestrel's default `NoDelay = true`).
- **Low-latency `WifiLock`**: acquires `WifiManager.WIFI_MODE_FULL_LOW_LATENCY` (API 29+) only while the Remote page is visible in the foreground, preventing aggressive phone Wi-Fi radio power-save sleep spikes (`100–185 ms`), and releases it on `onPause()` or page switch.
- **No wasted USB timeouts on Wi-Fi**: skips the `usb()` loopback connect probe for `/api/remote` while `route == Route.WIFI`, with automatic fallback if Wi-Fi drops, plus a `1000 ms` read timeout.
- **Native coalescing sender**: Android's WebView pushes move/scroll deltas via `Android.sendRemoteDelta()` into atomic accumulators drained on a background daemon thread (`phonebox-remote`) every `~12 ms`, so network RTT jitter does not cap cursor responsiveness.
- **Agent hot-path bypass**: `/api/remote` skips `PhoneRegistry` and app-version header updates on every tick.

**Environment tips for lowest jitter:**
- Connect both the laptop and phone to **5 GHz Wi-Fi** (avoid crowded 2.4 GHz Channel 1/6/11 when possible), or plug the **laptop into Ethernet**.
- Stay within clean router range without walls/appliances blocking 5 GHz.
- Disable **Power Saving Mode** on your laptop's Wi-Fi adapter (*Device Manager › Network adapters › your Wi-Fi adapter › Power Management › uncheck "Allow the computer to turn off this device to save power"* and set Wireless Adapter Settings to *Maximum Performance*).
- Make sure **AP/Client Isolation** is disabled on your router (otherwise the phone cannot reach port `8766`).
- On the same local network, prefer your router LAN IP over a **relayed Tailscale** hop.
- Turn on **Show latency stats** in the Remote widget's options temporarily to inspect `route · min/avg/p95/max ms · reuse X/Y` directly on the Remote page.

## Security

- **Opt-in only (OFF by default)**: Remote input gives full control over your keyboard and mouse, so `AllowRemoteInput` defaults to `false`. Until enabled, `POST /api/remote` returns HTTP `403`.
- **Wi-Fi authentication**: Port `8766` uses TLS with certificate pinning and requires the **paired phone bearer token**. The separate webhook token (`webhookToken`) is explicitly rejected on `/api/remote`, and missing/wrong tokens or any browser `Origin` header return `403`.
- **Localhost CSRF & rewrite protection**: On `127.0.0.1:8765`, `/agent/api/remote` is excluded from the settings-preview path rewrite (`404`), and direct requests to `/api/remote` carrying browser `Origin` or `Sec-Fetch-Site` headers are refused (`403`).
- **Stuck-button protection**: Held mouse buttons (`op=down`) are automatically released after **5 seconds** if no `op=up` arrives (and immediately if Remote is disabled).
- **Quick disable & unpairing**: Disable remote input anytime via the phone's **🛑 Disable remote** button or the Windows tray menu. If a phone is lost or stolen, click **New key (unpair phones)** in **Settings › General › Wi-Fi & Tailscale** to invalidate its bearer token immediately.
- **Auto-update safety**: Automatic updates (`updates.auto`) default to `false`, the update repository defaults to `skander26/Couchpad` (configurable under **Settings › General › Updates**), and local source builds (`!SingleFile`) never overwrite themselves in the background — protecting your custom build from being replaced by an upstream PhoneBox release.
- **Windows UIPI limitation**: Because the agent runs unelevated as your standard user, Windows User Interface Privilege Isolation (UIPI) blocks synthetic `SendInput` events whenever an **Administrator** window or **UAC prompt** is focused, returning HTTP `409` cleanly instead of crashing.

## Troubleshooting

<details><summary><b>Cursor moves in the wrong direction in Motion mode</b></summary>

Hold **✋ Hold to move** and open **⚙ Tuning**. Depending on whether you hold your phone in landscape, portrait, or flat like a TV remote, swap **X** and **Y** between `Yaw`, `Pitch`, and `Roll` and toggle **Invert X** or **Invert Y**. Tuning values save immediately in `localStorage`.

</details>

<details><summary><b>Cursor drifts or jitters in Motion mode</b></summary>

Always release **✋ Hold to move** when repositioning your hand — Motion mode only sends cursor deltas while held. If holding still still creeps slightly or jitters, increase **Dead** (`0.03–0.06`) to filter sensor bias/micro-tremor and raise **Smooth** (`45–65%`) to apply stronger exponential moving average smoothing.

</details>

<details><summary><b>Soft keyboard does not appear on the phone</b></summary>

Check that **Allow remote input** is enabled on the PC (controls and keyboard input are disabled when off) and tap **⌨ Keyboard** again. If you recently edited layout settings, wait a second for config sync and verify your Android keyboard isn't hidden by a floating keyboard mode.

</details>

<details><summary><b>Delay or stuttering over Wi-Fi</b></summary>

Enable **Show latency stats** on the Remote widget in **Settings › Pages** to see live `route · min/avg/p95/max ms · reuse X/Y` latency:
- Verify it says `wifi` and `reuse` matches total requests (`reuse X/X`).
- Switch both devices from 2.4 GHz to **5 GHz Wi-Fi**, disable laptop Wi-Fi power management, and check `ping <phone-ip>` from PowerShell (`> 30 ms` or sporadic `> 100 ms` spikes indicate radio/AP congestion or interference).

</details>

<details><summary><b>Panel says "PC not connected" or remote actions return an error</b></summary>

- Confirm `PhoneBoxAgent.exe` is running (tray icon or `http://127.0.0.1:8765`).
- Over USB: check `adb devices` lists `device` (not `unauthorized` or missing). Try another data cable or direct PC USB port.
- Over Wi-Fi: ensure **Let the phone connect without the USB cable** is enabled in Settings, the agent was restarted after turning it on, and Windows Firewall allows `PhoneBoxAgent.exe` on Private networks (port `8766`).
- Elevated windows: clicking inside Task Manager (as Admin), Registry Editor, or UAC prompts on Windows returns HTTP `409` due to UIPI.

</details>

<details><summary><b>"The update was not installed on the phone" / signature mismatch</b></summary>

Android refuses to overwrite an upstream release-signed PhoneBox APK with a locally signed build (or downgrade `versionCode`). Run `adb uninstall net.oxog.phonebox.deskpanel` once before `.\scripts\install.ps1 -Apk`.

</details>

## Development

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), JDK 17 + Android SDK Platform 35 (`JAVA_HOME` and `ANDROID_HOME` set), Python 3, and Node.js 22.

```powershell
# Run browser demo locally from source (http://127.0.0.1:8790/panel/ and /settings/)
python tools/demo-server.py

# Validate release metadata, widget count (116), template count (31) & catalog consistency
python tools/check-release.py

# Run all .NET agent unit & security tests
dotnet test agent/PhoneBox.Agent.Tests

# Run Playwright browser UI tests (first install dependencies)
cd tools/ui-tests
npm ci
npx playwright install chromium
npx playwright test --workers=1
cd ../..

# Lint & build the Android APK
cd android
.\gradlew.bat lintDebug assembleDebug
cd ..

# Build agent release binaries + APK into dist/
.\scripts\build.ps1

# Run Windows agent directly from source
dotnet run --project agent/PhoneBox.Agent -f net10.0-windows10.0.19041.0
```

## Credits & License

- **Original project**: [PhoneBox](https://github.com/ersinkoc/phonebox) by **Ersin KOÇ (`ersinkoc`)** — original desk panel, widget system, desktop agent, and pairing protocol.
- **Couchpad additions**: **Remote** mode (input injection endpoint, touchpad/keyboard/media UI, gyroscope motion mouse, Wi-Fi latency improvements, and fork updater safeguards) by **skander26**.
- Licensed under the [MIT License](LICENSE). See [THIRD-PARTY-NOTICES.txt](agent/PhoneBox.Agent/THIRD-PARTY-NOTICES.txt) for bundled third-party library notices and [PRIVACY.md](PRIVACY.md) for connection details.
