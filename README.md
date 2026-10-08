<div align="center">

# 📱 ControlPhone

**Free, open-source control center for many Android phones at once — view every screen live, control one phone or a whole group in sync.**

**Unlimited devices · No ads · No hidden background tasks · Optimized to be as light as possible**

[Tiếng Việt](README.vi.md) · English

UI languages: English · Tiếng Việt · 简体中文 · 繁體中文 · Deutsch · Français

![ControlPhone](docs/screenshot-grid.jpg)

<sub>Phone screens in the screenshots are placeholders.</sub>

</div>

---

## ✨ Why ControlPhone

| | |
|---|---|
| ♾️ **Unlimited devices** | No device cap, no license key, no "pro" version. Designed for 100+ phones on one PC (tested live with 20 phones, benchmarked at 100 streams). |
| 🚫 **No ads, no tracking** | No ads, no telemetry, no account. The app talks only to your phones and your own PC (Internet is used once, to download scrcpy during setup). |
| 💤 **No hidden background tasks** | Nothing is installed as a service, nothing starts with Windows. Close the app window (or the server window) and everything stops — the server exits by itself and also stops the ADB server if ControlPhone started it (an ADB server already used by another tool is left running; set `CP_KEEP_ADB=1` to always keep ADB, `CP_KEEP_RUNNING=1` to keep the server running after the window closes). No app is installed on the phones either — the screen server (one small file in `/data/local/tmp`) runs only while connected. |
| 🪶 **Lightweight by design** | Hardware H.264 encoding on the phone, decoding in your browser (WebCodecs). Idle phones cost ~6 kbps each; phones that are off-screen or paused send nothing. |
| 🆓 **Free & open source** | MIT license. Use it, modify it, share it. |

## 🚀 Features

**Live screen grid**
- See every phone live in a resizable grid, "fit to screen" in one click.
- Light grid streams (360p–720p, adjustable) + a sharp large view (up to 1280px / 30 fps) for the phone you are working on.
- Only visible tiles are decoded; minimizing the window pauses everything.
- **Pause viewing** for one phone or a group (the phone stops streaming but stays controllable).
- **Show only these phones**: hide and pause every other phone, restore with one click.

**Control — directly on the small screens**
- Mouse = finger on any tile: tap, swipe, long-press. Middle click = Home, side button = Back, Shift + wheel = scroll, Alt + drag = pinch zoom.
- Type on the keyboard while hovering a phone; **Ctrl+V pastes your PC clipboard into all selected phones** (any language, emoji).
- **Click** = select one phone · **Ctrl+click** = add/remove to the group · drag a box to select many · Ctrl+A = all.
- **Group sync is automatic**: when 2+ phones are selected, anything you do on one of them is repeated on the whole group (F3 to pause sync).
- Turn the **physical screen off** while you keep viewing and controlling (saves battery; restored when you exit). Enabled by default.

**Right-click menu on any phone**

![Right-click menu](docs/screenshot-menu.jpg)

1. Zoom (large view of that phone; group sync still applies)
2. Install APK
3. Import file to the phone
4. Export file to the PC
5. ADB commands ▸ (device info, battery, storage, RAM, IP/Wi-Fi, current app, installed apps, show/hide navigation bar, Wi-Fi/data on/off, clear cache, close background apps, set battery to 100% / restore real battery, custom command, reboot)
6. Open network settings
7. Open Settings

**Batch tools (for all selected phones)**
- Back / Home / Recents / Power / Volume / unlock / screen off-on
- Send text, paste, open URL, launch / stop / clear / uninstall apps
- Install APK (or drag & drop `.apk` files onto the window), push files, file browser (download / upload / delete)
- ADB shell (optionally as root) with per-phone results, batch screenshots
- Zoom view: one click copies a full-resolution screenshot to the PC clipboard — just paste it anywhere with Ctrl+V
- Rename, renumber, groups, filters, search, battery & temperature at a glance

**Connections**
- **USB** — detected instantly.
- **Wi-Fi** — USB → Wi-Fi in one click (and Wi-Fi → USB to switch back), connect by IP, LAN scan, Android 11+ pairing code. A phone connected by both USB and Wi-Fi appears once and fails over automatically; Wi-Fi phones reconnect automatically.
- **Root** — detected automatically; root shell, permanent ADB over Wi-Fi, and auto-installs this PC's ADB key into `/data/misc/adb/adb_keys` so rooted phones never ask "Allow USB debugging?" again (ideal for screenless box phones).
- **OTG** — launches `scrcpy --otg` (keyboard/mouse over USB without USB debugging).
- Native scrcpy window for any phone as a fallback.

## 📥 Install

### Option 1 — Release ZIP (recommended, nothing else to install)
1. Download `ControlPhone-vX.Y.Z-win64.zip` from [Releases](https://github.com/mahiepit/ControlPhone/releases).
2. Extract it anywhere.
3. Double-click **`ControlPhone.bat`**.

The ZIP already contains a portable Node.js and the official scrcpy binaries.

### Option 2 — From source
Requirements: Windows 10/11, [Node.js 18+](https://nodejs.org), Microsoft Edge or Google Chrome.
```bash
git clone https://github.com/mahiepit/ControlPhone.git
cd ControlPhone
npm install
npm run setup
npm start
```
`npm run setup` downloads scrcpy v5.0 from the official GitHub release and verifies its SHA-256. `ControlPhone.bat` does all of this automatically.

### On each phone
1. Enable **Developer options** → **USB debugging**.
2. Plug it in and tap **Allow** on the phone.

That's it — the phone appears in the grid within a few seconds. No app is installed on the phone.

## 🖱️ Quick reference

| Action | How |
|---|---|
| Select one phone | Click its tile header (or tap a phone outside the current group) |
| Build a group | Ctrl+click phones, drag a box, or Ctrl+A |
| Control the group | Just operate any phone of the group (sync is automatic, F3 to pause) |
| Large view | Right-click → Zoom, or double-click the tile header |
| Paste to all selected | Ctrl+V |
| Phone menu | Right-click the phone |
| Pause one phone / group | Right-click → Pause viewing |
| Focus on a few phones | Right-click → Show only these phones (purple button at the top to restore) |
| Change language | Language selector at the top right |
| Quality & options | ⚙ Settings |

## ⚡ Performance

Measured on a Windows PC (22 threads) with 20 phones running a 3D game (every phone at its 15 fps cap):

| | 20 phones (Balanced) | 20 phones (Ultra-light) | Estimated 100 phones (Ultra-light) |
|---|---|---|---|
| Total video bandwidth | 12 Mbps | 6.3 Mbps | ~32 Mbps |
| Browser (decode + draw) | 0.8 core | 0.6 core | ~3 cores |
| ControlPhone server | 0.1 core, ~100 MB RAM | 0.07 core | ~0.35 core |
| On the phone (scrcpy) | 1.5–3 % CPU | — | — |

Decoding benchmark: 100 streams × 15 fps (1,500 frames/s) decoded with the UI still at 60 fps.
Idle phones (static screen) cost about 6 kbps each.

**Tips for 100 phones:** use the *Ultra-light* preset, spread the phones over several USB controllers (a single USB controller usually handles only a few dozen phones), and pause viewing for phones you don't need to watch.

## ❓ Troubleshooting

- **Tile stuck on "Connecting…"** — right-click → Reconnect stream, or *Restart ADB* in the sidebar.
- **Black screen** — the phone's screen is asleep: use *Unlock* or *Screen on*.
- **Keyboard input not reaching the phone** — hover the phone (or click the large view) first; tap a text field on the phone.
- **Wi-Fi connection refused** — some ROMs block ADB over TCP (common on Samsung Android 11+); use *Pairing code (Android 11+)* instead.
- **Navigation bar visible in games on Android 10** — select the phones → right-click → ADB commands → *Hide 3-button bar for the current app*.
- **No video in the browser** — use a recent Microsoft Edge or Google Chrome (WebCodecs required).

## ❤️ Support the project

ControlPhone is free and always will be. If it saves you time or money, a donation keeps it maintained and improving. Thank you!

<table>
<tr>
<td align="center"><b>PayPal</b><br><img src="public/img/donate-paypal.svg" width="180" alt="PayPal QR"><br><a href="https://paypal.me/thaogia">paypal.me/thaogia</a></td>
<td align="center"><b>BNB (BEP-20) / ETH (ERC-20)</b><br><img src="public/img/donate-crypto.svg" width="180" alt="BNB / ETH QR"><br><code>0xd09c2E60cbC8526976C436e316630FA64296E824</code></td>
</tr>
</table>

Please double-check the network (BNB Smart Chain or Ethereum) before sending crypto.

## 🧩 How it works

```
Phone (scrcpy-server, H.264 hardware encoder) ──adb──► ControlPhone server (Node.js) ──WebSocket──► Browser UI (WebCodecs decode)
                         ▲                                                                              │
                         └──────────────── touch / keys / text / commands ◄────────────────────────────┘
```

```
ControlPhone.bat        launcher (portable Node if present, auto setup)
server/                 HTTP + WebSocket server, ADB client, scrcpy sessions, batch actions
public/                 UI (plain HTML/CSS/JS, no build step), translations in public/js/lang/
tools/setup.js          downloads & verifies scrcpy      tools/pack.js   builds the release ZIP
```

Want to add a language? Copy `public/js/lang/en.js`, translate the values, add it to `LANGS` in `public/js/i18n.js`, and run `npm run i18n:check`.

## 📄 License

[MIT](LICENSE) © Thảo Gia. Third-party components (scrcpy, adb, ws, Node.js) are listed in [NOTICE.md](NOTICE.md).
ControlPhone is an independent project, not affiliated with Genymobile or Google.
Use it responsibly and in accordance with the terms of the apps and services you use.
