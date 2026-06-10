# egogrip-capture (native Android)

The fast-path on-device capture app for the PICO: lists USB devices on the hub, streams the
RP2040 over **USB-serial**, and writes a real egogrip **episode** (`manifest.json` +
`gripper_state.csv` + `tactile.csv`) — the same format the [pipeline](../pipeline/) ingests.

This is the **tomorrow-on-the-PICO** deliverable (decision: native beats Unity for a brief
window; pose + enterprise ego camera come later in Unity). Full runbook:
[../docs/PICO_TOMORROW.md](../docs/PICO_TOMORROW.md).

## Build (do this tonight — first sync downloads ~1 GB)
1. Android Studio → **Open** → this `app-native/` folder.
2. Let Gradle sync (downloads Gradle 8.7 + AGP + the serial lib).
3. **Build → Make Project** (or `./gradlew assembleDebug`). Aim for a green build tonight.
4. Install over **wireless adb** (the hub will occupy the USB-C port tomorrow):
   ```bash
   adb install -r app/build/outputs/apk/debug/app-debug.apk
   ```

## What it does
- **Refresh** lists every USB device on the hub with VID/PID and tags (`SERIAL`, `UVC-CAMERA`)
  — this alone proves the hub + OTG + camera enumerate on the PICO.
- **Start** opens the RP2040 (CDC), parses the framed protocol, and writes an episode under
  `/sdcard/Android/data/org.egogrip.capture/files/episodes/<id>/`.
- **Stop** finalizes `manifest.json`. Pull with the `adb pull` line the app prints.

Episodes have serial streams but **no gripper pose yet** (pose needs the controller/Unity
phase), so they validate *capture*; `egogrip-validate` passes, `egogrip-export` (needs pose)
does not — expected.

## Files
- `Protocol.kt` — parses the RP2040 frames (mirrors the firmware).
- `SerialClient.kt` — usb-serial-for-android open + permission + read loop.
- `EpisodeWriter.kt` — writes the egogrip raw format (manifest + CSVs).
- `CaptureClock.kt` — the one shared monotonic clock.
- `MainActivity.kt` — device list + Start/Stop + log (programmatic UI, no XML, no AppCompat).

## Enable the camera (optional, stretch)
The default build has **no camera dependency** so a flaky UVC lib can't block the serial path.
To add live UVC preview + frame capture:
1. In `app/build.gradle.kts`, uncomment the `com.herohan:UVCAndroid` line.
2. Copy `../optional/UvcClient.kt` into `app/src/main/java/org/egogrip/capture/`.
3. Wire it in `MainActivity.startCapture()` per the comments in that file.
Note: synchronized **UVC → mp4** capture is the next coding task (see
[../docs/NATIVE_APP_PLAN.md](../docs/NATIVE_APP_PLAN.md)); the optional module currently proves
the camera streams and logs frame timestamps.

## Troubleshooting
- **A dependency version won't resolve:** bump `usb-serial-for-android` to the latest, or check
  that `jitpack.io` is reachable (it's declared in `settings.gradle.kts`).
- **Gradle/AGP mismatch:** this project pins AGP 8.5.2 / Gradle 8.7 / Kotlin 1.9.24 / JDK 17.
  If Android Studio insists on a newer AGP, accept its upgrade or set the matching Gradle.
- **No serial device listed:** confirm the RP2040 is flashed and on the hub; some hubs need
  external power for the headset to enumerate downstream devices.
- **Install fails over Wi-Fi:** re-run `adb connect <PICO_IP>:5555`; ensure PC + PICO share Wi-Fi.
