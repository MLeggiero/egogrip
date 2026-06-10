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
- **Start** captures, on one shared monotonic clock, and writes an episode under
  `/sdcard/Android/data/org.egogrip.capture/files/episodes/<id>/`:
  - **serial** — RP2040 (CDC), framed protocol → `gripper_state.csv` + `tactile.csv`
  - **camera** — USB/external UVC via **Camera2** (no dependency) → `wrist0.mp4` +
    `wrist0_frames.csv`; skips gracefully if the PICO doesn't expose the cam to Camera2
  - **IMU** — headset orientation (game rotation vector) → `imu.csv` (3-DoF, free)
- **Stop** finalizes `manifest.json`. Pull with the `adb pull` line the app prints.

Episodes have serial + camera + IMU but **no 6-DoF gripper pose yet** (that needs the
controller/Unity phase), so they validate *capture*; `egogrip-validate` passes, `egogrip-export`
(needs a pose stream) does not — expected.

## Files
- `Protocol.kt` — parses the RP2040 frames (mirrors the firmware).
- `SerialClient.kt` — usb-serial-for-android open + permission + read loop.
- `Camera2Client.kt` — USB/external camera → mp4 via Camera2 (framework only, no dependency).
- `ImuClient.kt` — headset orientation → `imu.csv` (framework only).
- `EpisodeWriter.kt` — writes the egogrip raw format (manifest + CSVs).
- `CaptureClock.kt` — the one shared monotonic clock.
- `MainActivity.kt` — device list + Start/Stop + pre-flight + log (programmatic UI, no AppCompat).

## Camera notes
Camera capture is **built in via Camera2** — zero dependencies, so it never blocks the build.
It works when the PICO surfaces the USB camera as a Camera2 device (`LENS_FACING_EXTERNAL`),
common on Android 12+ with UVC kernel support. If it doesn't appear at runtime, the app logs it
and keeps recording serial + IMU.

**Fallback (only if Camera2 can't see the UVC cam):** a libuvc-based path in
`../optional/UvcClient.kt` — uncomment `com.herohan:UVCAndroid` in `app/build.gradle.kts`, copy
that file in, and wire it per its comments.

## Troubleshooting
- **A dependency version won't resolve:** bump `usb-serial-for-android` to the latest, or check
  that `jitpack.io` is reachable (it's declared in `settings.gradle.kts`).
- **Gradle/AGP mismatch:** this project pins AGP 8.5.2 / Gradle 8.7 / Kotlin 1.9.24 / JDK 17.
  If Android Studio insists on a newer AGP, accept its upgrade or set the matching Gradle.
- **No serial device listed:** confirm the RP2040 is flashed and on the hub; some hubs need
  external power for the headset to enumerate downstream devices.
- **Install fails over Wi-Fi:** re-run `adb connect <PICO_IP>:5555`; ensure PC + PICO share Wi-Fi.
