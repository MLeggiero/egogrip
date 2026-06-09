# egogrip native Android plugin (AAR)

The native half of the APK. Owns what Unity is bad at on Android XR: **USB UVC cameras**,
**USB-serial** (the gripper MCU), and **hardware video encoding**. Exposes a small API the
Unity `CaptureManager` calls.

> Status: scaffold (interface defined; no implementation yet). Built as an `.aar` consumed by
> the Unity project in [../app/](../app/).

## Responsibilities
1. **UVC camera capture** — enumerate USB cameras via the Android USB host API; stream with a
   libuvc/AndroidUSBCamera-style path; feed frames to **MediaCodec** (H.264) → **MediaMuxer**
   (`wrist0.mp4`); write `wrist0_frames.csv` (`frame_idx, monotonic_ns, pts_ns`). Timestamp
   on the camera callback using `SystemClock.elapsedRealtimeNanos()`.
2. **USB-serial** — open the RP2040 CDC device; run a read loop that parses the framed
   protocol ([../firmware/rp2040-gripper/README.md](../firmware/rp2040-gripper/README.md)),
   validates CRC, and appends `gripper_state.csv` / `tactile.csv` / `sync_events.csv` with
   both device arrival `monotonic_ns` and the MCU `micros`.
3. **Lifecycle/health** — report per-stream fps + alive/degraded + dropped-frame counts back
   to Unity for the GUI; handle hot (un)plug cleanly.

## Why native (not Unity)
USB host enumeration, UVC, MediaCodec, and serial are mature in Android Java/Kotlin/NDK and
fragile when bridged through Unity. EgoKit used exactly this split. Unity keeps the XR session
and GUI; the AAR keeps the device I/O.

## Proposed interface (Unity ⇄ AAR)

```kotlin
interface EgogripCapture {
    fun listDevices(): List<DeviceInfo>                 // UVC cams + CDC serial found on the hub
    fun openEpisode(dir: String, config: CaptureConfig) // create files, arm encoders
    fun start(monotonicStartNs: Long)                   // begin writing on the shared clock
    fun stop(): EpisodeSummary                           // flush, finalize streams, return counts
    fun health(): List<StreamHealth>                     // fps, alive, dropped, degraded
    fun pulseSync(eventId: Int)                          // forward PULSE to MCU (Tier-2)
    fun lastEgoPreviewFrame(): ByteArray?                // optional single frame for GUI review
}
```

The clock is **shared**: Unity passes the same `elapsedRealtimeNanos()` origin used for XR
poses so every file is on one timeline (see [../docs/SYNC.md](../docs/SYNC.md)).

## Build (planned)
- Android library module (Gradle) → `egogrip-capture.aar`, dropped into the Unity project's
  `Assets/Plugins/Android/`.
- minSdk targeting the PICO OS Android level; USB host + camera permissions in the manifest.

## Open questions
- Two simultaneous UVC streams over USB high-speed (bandwidth ceiling EgoKit hit) — keep MVP
  single-camera; benchmark before enabling dual.
- Whether the **ego** enterprise-camera frames are encoded here or in Unity/PXR — depends on
  the enterprise API's delivery format (see [../docs/PICO_ENTERPRISE_NOTES.md](../docs/PICO_ENTERPRISE_NOTES.md)).
