# Third-party software

ControlPhone itself is released under the MIT License (see `LICENSE`).
It uses or redistributes the following third-party components:

| Component | Use | License |
|---|---|---|
| [scrcpy](https://github.com/Genymobile/scrcpy) v5.0 — © Genymobile and Romain Vimont | `scrcpy-server` runs temporarily on the phone to capture the screen and inject input; `scrcpy.exe` is used for the optional native window and OTG mode | Apache License 2.0 (`vendor/scrcpy-win64-v5.0/LICENSE.txt`) |
| [Android Debug Bridge (adb)](https://developer.android.com/tools/adb) — The Android Open Source Project | communication with the phones (shipped inside the official scrcpy release; the `adb` already on your PATH is preferred) | Apache License 2.0 |
| FFmpeg / SDL / libusb DLLs | shipped inside the official scrcpy Windows release, used only by `scrcpy.exe` | see the scrcpy release |
| [ws](https://github.com/websockets/ws) | WebSocket server | MIT |
| [Node.js](https://nodejs.org) (portable `node.exe`, release package only) | runtime | MIT + bundled licenses (`node/LICENSE`) |

The scrcpy binaries are downloaded from the official GitHub release
(`Genymobile/scrcpy`, SHA-256 verified) by `tools/setup.js`, or included unmodified in the release ZIP.

ControlPhone is an independent project and is not affiliated with or endorsed by Genymobile or Google.
