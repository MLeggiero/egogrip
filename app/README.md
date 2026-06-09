# egogrip APK (Unity)

The on-headset application: the **in-VR GUI** and the **capture orchestrator**. Unity owns the
XR session (controller / hand / head pose via the PICO Integration SDK, ego RGB via the
enterprise camera API) and calls the [native AAR plugin](../native-plugin/) for USB cameras
and serial.

> Status: scaffold (this README is the build spec). The Unity project files are intentionally
> not committed yet — see [../docs/ROADMAP.md](../docs/ROADMAP.md) Phase 1–2.

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
