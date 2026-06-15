# egogrip APK (Unity)

The on-headset application: the **in-VR GUI** and the **capture orchestrator**. Unity owns the
XR session (controller / hand / head pose via the PICO Integration SDK, ego RGB via the
enterprise camera API) and calls the [native AAR plugin](../native-plugin/) for USB cameras
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
- **In-VR GUI** (wrist-anchored panel, hands-busy friendly):
  - Big **Start/Stop** (also bindable to a controller button / volume key like EgoKit).
  - Recording **timer** + free **storage** + **battery**.
  - **Per-stream health**: fps + green/red alive dot per stream; degraded streams turn red.
  - **Single ego preview** + **last-episode review** (scrub, keep/delete). No full multi-cam
    preview grid — too expensive in VR (see decision D7).
  - **Pre-flight**: blocks arming if storage/battery low or a required stream is missing.
- **Calibration mode**: run/refresh `calibration.json`; quick "calibration check" before a
  session (see [../docs/HARDWARE.md](../docs/HARDWARE.md#calibration)).

## Stack / packages (planned)
- Unity LTS + **PICO Integration SDK** (XR, hand tracking, motion tracking; enterprise camera).
- The native [`egogrip-capture.aar`](../native-plugin/) in `Assets/Plugins/Android/`.
- Build target: Android APK, sideloaded to the PICO 4 Ultra Enterprise (the registered
  authorized package name — see [../docs/PICO_ENTERPRISE_NOTES.md](../docs/PICO_ENTERPRISE_NOTES.md)).

## Key implementation notes
- **One clock:** capture `SystemClock.elapsedRealtimeNanos()` once at arm; pass the origin to
  the AAR so XR poses and device I/O share a timeline.
- **Crash safety:** episodes are append-only; on launch, finalize/repair any episode left in
  `recording` state.
- **N-extensible:** the capture config enumerates streams; a second gripper/camera/controller
  is config, not code (decision D9).

## Not on the headset
Dataset conversion (LeRobot) and training run **offline** in [../pipeline/](../pipeline/). The
headset only captures, stores, and verifies (decision D7 — self-contained operation).
