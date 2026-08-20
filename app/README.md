# egogrip APK (Unity)

The on-headset application: the **in-VR GUI** and the **capture orchestrator**. Unity owns the
XR session (controller / hand / head pose via the PICO Integration SDK, ego RGB via the
enterprise camera API) and calls the [native AAR plugin](../app-native/capture/) for USB cameras
and serial.

> Status: the Unity project **is** committed (scene, scripts, ProjectSettings, Packages). One thing
> is **not** in git: the PICO Unity Integration SDK (~300 MB, proprietary) — you re-add it after
> cloning (see **Open the cloned project** below). The egogrip C#/Kotlin is reviewed but built only
> on a machine with Unity + the Android SDK ([../docs/UNITY_STEPS.md](../docs/UNITY_STEPS.md)).

## Open the cloned project

Unity Hub gates on the exact editor version and the project needs the PICO SDK re-added, so a fresh
clone won't open until you do these three things:

1. **Install Unity `6000.4.10f1`** (the version in `ProjectSettings/ProjectVersion.txt`) via Unity
   Hub, with **Android Build Support** (SDK & NDK + OpenJDK). Hub won't open the project on a
   different version without an upgrade prompt.
2. **Re-add the PICO Unity Integration SDK (v3.4.0).** Download it from PICO and unzip it into
   **`app/Packages/com.unity.xr.picoxr/`** so that `app/Packages/com.unity.xr.picoxr/package.json`
   exists. It's gitignored, so it stays out of commits. Without it Unity opens in **Safe Mode** with
   PXR compile errors.
3. **Open the `app/` folder** in Unity Hub (▸ *Add* ▸ select `egogrip/app`) — **not** the repo root.

✓ Check: Package Manager ▸ *In Project* lists `com.unity.xr.picoxr`, the **PICO** menu appears, and
the Console has no red errors. Full build/sideload steps: [../docs/UNITY_POSE_SETUP.md](../docs/UNITY_POSE_SETUP.md).

## What the app does
- **Capture orchestration** (`CaptureManager`): establishes the single monotonic clock origin,
  opens all sources, fans samples into per-stream writers, finalizes `manifest.json`. Pose
  streams (controller→TCP, hands, head) are written directly from Unity; USB/serial go through
  the AAR. See [../docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md).
- **In-VR GUI** (`EgogripHud`) — a code-generated, **world-anchored** **uGUI Canvas + TextMeshPro**
  HUD in **fixed zones** (nothing reflows when you toggle): status bar (REC/idle + timer + sample
  count), **video zone** (ego + wrist camera feeds as wide, full-width `RawImage` tiles), a **sensor
  roster**, a signals strip, and a device/warnings row. Warn-only (never blocks recording).
  - **Controls:** `A/X` = record; **thumbstick** moves a highlight over the roster; **grip** toggles
    the selected sensor (idle-only); **B/Y** = *tap* to recenter the panel in front of you, *hold* to
    drag it with your controller (world-locked, movable). The panel auto-places in front on launch.
  - **Pre-flight sensor roster:** input mode (controllers ↔ hands), controllers L/R, head, each wrist
    camera, **ego**, and each **Pico** — all individually on/off before a run, locked mid-run; the
    finalized `manifest.json` reflects exactly the enabled set.
  - **Multiple cameras:** set `EgogripPoseRecorder.numWristCameras` (default **2**); the app spawns
    that many capture instances (`wrist0`, `wrist1`, …), each auto-claiming a distinct USB device (the
    native backend de-dupes). No hard cap — **USB bandwidth** is the real limit (~2 full-rate UVC
    streams; more at lower res/fps). Unused instances show "no signal".
  - **Serial (RP2040 Picos):** set `numSerialDevices` (default **2**); each `EgogripSerial` claims a
    distinct CDC device and records `gripper_state<idx>.csv` / `tactile<idx>.csv` /
    `sync_events<idx>.csv`. Live gripper width + tactile show in the signals strip. Needs the serial
    code in the AAR — **rebuild `egogrip-capture.aar`** (see below).
  - **Ego camera:** `EgogripEgoCamera.simulate` (default **on**) generates a synthetic feed that shows
    in the ego slot AND records `ego.mp4` — so you can exercise the whole path *as if* PICO enterprise
    access existed, without breaking anything. When access lands: implement `PollReal()`, add the
    `EGOGRIP_PICO_EGO` define, and flip `simulate` off — same record seam.
  - Requires **`com.unity.ugui`** (in the manifest) and a one-time *Window ▸ TextMeshPro ▸ Import TMP
    Essential Resources* — without the import TMP renders nothing (layout is still correct).
  - **Rebuild the AAR** for serial/ego recording: Android Studio → build `:capture` in `app-native/`
    → drop `egogrip-capture.aar` into `Assets/Plugins/Android/`. The serial lib
    (`com.github.mik3y:usb-serial-for-android`) is jitpack-only, which Unity's Gradle doesn't search,
    so it's **vendored** as `Plugins/Android/usb-serial-for-android-3.8.1.aar` (Unity auto-includes it)
    rather than declared as a coordinate — no Custom Gradle Settings Template needed.
  - Requires **`com.unity.ugui`** (in the manifest) and a one-time *Window ▸ TextMeshPro ▸ Import TMP
    Essential Resources* — without the import TMP renders nothing (layout is still correct).
- **Calibration mode**: run/refresh `calibration.json`; quick "calibration check" before a
  session (see [../docs/HARDWARE.md](../docs/HARDWARE.md#calibration)).

## Stack / packages (planned)
- Unity LTS + **PICO Integration SDK** (XR, hand tracking, motion tracking; enterprise camera).
- The native [`egogrip-capture.aar`](../app-native/capture/) in `Assets/Plugins/Android/`.
- Build target: Android APK, sideloaded to the PICO 4 Ultra Enterprise (the registered
  authorized package name — see [../docs/PICO_ENTERPRISE_NOTES.md](../docs/PICO_ENTERPRISE_NOTES.md)).

## Key implementation notes
- **One clock:** capture `SystemClock.elapsedRealtimeNanos()` once at arm; pass the origin to
  the AAR so XR poses and device I/O share a timeline.
- **Crash safety:** episodes are append-only; on launch, finalize/repair any episode left in
  `recording` state.
- **N-extensible:** the capture config enumerates streams; a second gripper/camera/controller
  is config, not code (decision D9).
- **Controller vs. hand tracking:** the HUD toggles the pose source (grip on the *INPUT* row, idle
  only). *Controllers* records the 6-DoF `gripper_pose`; *Hands* records PICO 26-joint hand tracking
  (`poses.jsonl`, `skeleton`) plus a derived wrist `gripper_pose` so the action stream is identical
  either way. To activate hands on-device: set `handTracking: 1` in `PXR_ProjectSetting` (done) **and**
  add the `EGOGRIP_PICO_HANDS` scripting define (Player ▸ Scripting Define Symbols), then confirm the
  `PXR_HandTracking` calls in `EgogripHandTracker.cs` against your PICO SDK version. Without the
  define the app still builds; Hands mode shows "build flag off" in the HUD.

## Not on the headset
Dataset conversion (LeRobot) and training run **offline** in [../pipeline/](../pipeline/). The
headset only captures, stores, and verifies (decision D7 — self-contained operation).
