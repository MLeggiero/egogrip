# egogrip — multimodal capture upgrade plan

> Approved design doc for the current build. Diagrams: [diagrams/system_dataflow.svg](diagrams/system_dataflow.svg),
> [diagrams/sync_timeline.svg](diagrams/sync_timeline.svg).

## Context
egogrip records headset-based human demonstrations (ego RGB, wrist RGB, controller 6-DoF pose,
gripper width, tactile) on a PICO 4 Ultra Enterprise and exports LeRobot datasets for robot
learning. This plan adds four capabilities: (1) lightweight ego-camera video, (2) lightweight
wrist-camera video, (3) an easily-edited JSON that defines which sensors are recorded, and
(4) improved RP2040 ("Raspberry Pi Pico") firmware for an AS5600 gripper encoder (tactile later)
streaming over USB-serial to the headset.

Today the repo has **two parallel apps** (a Unity app recording XR pose + wrist video, and a
standalone native Android app recording serial + camera + IMU), a working offline pipeline, and
scaffold firmware. There is **no input config** — sensors are hard-coded and `manifest.json` is
built by string concatenation; the only JSON is the *output* manifest. The Unity host (the real
product) does **not** record gripper width yet (serial lives only in the standalone app). This
plan unifies on a Unity-host + native-AAR design, adds a typed sensor-registry config, rewrites
firmware for Arduino-Pico + AS5600, and wires ego video — reusing the machinery that already works.

## Decisions (confirmed with owner)
- One **Unity-hosted APK + native AAR**. Unity owns XR pose, in-VR GUI, enterprise ego camera,
  orchestration; AAR owns USB cameras + USB-serial + on-device H.264. Standalone `app-native/app`
  becomes a bench/test harness.
- Ego camera: build against the PICO **Enterprise Main Camera Access API** assuming OS15 grants it
  (authorized package name in Unity Player settings); styly `EnterpriseCameraAccessPlugin` is the
  integration path. Wrist cam is the graceful fallback.
- Config: **typed sensor registry** (`capture_config.json`).
- Firmware: **Arduino-Pico**, **AS5600 magnetic-absolute** width over I²C; tactile kept
  plug-in-ready but unwired; **Tier-1 timing only** (no sync LED).

## Reuse (already works — do not rebuild)
- On-device H.264 MP4 + per-frame timestamp CSV is the lightweight-video mechanism
  (`app-native/capture/.../Camera2Client.kt`, `UvcCameraBackend` via the `EgogripCamera` facade).
- Offline multi-rate alignment: `pipeline/egogrip_pipeline/sync.py::build_timeline` resamples all
  streams to a uniform grid (linear scalars, slerp rotation, nearest-frame video).
- Export maps any video stream id → `observation.images.<id>` (`export_lerobot.py`), so an `ego`
  stream needs no exporter change.
- Manifest schema is already N-sensor/self-describing (`schema/capture_manifest.schema.json`).
- Tier-1 MCU↔device clock fit: `sync.py::fit_clock` + `io.mcu_clock_columns` (per-packet `micros`).

## Capability 3 — typed sensor-registry JSON (build FIRST; greenfield, no hardware)
- New `schema/capture_config.schema.json` (the *input* config). Per sensor instance: `id`,
  `type` ∈ {`xr_pose`,`ego_camera`,`uvc_camera`,`rp2040_serial`}, `enabled`, `rate_hz`
  (nominal/health only), + type params (camera: w/h/fps/codec/bitrate; uvc: VID:PID or index;
  rp2040: VID:PID/baud, packet-type→stream-id map, channel layout, calibration ref; xr_pose:
  XRNode, stream id, frame). Top-level: `format_version`, operator/task defaults, `calibration_ref`.
- New `configs/single_gripper.json` (ego+wrist+xr_pose+rp2040), `configs/wrist_only.json`,
  `configs/dual_gripper.json` (stretch, proves N-sensor).
- New `pipeline/egogrip_pipeline/config.py` + `tests/test_config.py`: load/validate; assert
  config↔manifest consistency.
- New parsers: Unity `app/Assets/Egogrip/EgogripCaptureConfig.cs`, Kotlin
  `app-native/capture/.../CaptureConfig.kt`.
- `manifest.json` gains `config_ref`.

## Capability 4 — Arduino-Pico firmware + AS5600 calibration
New `firmware/rp2040-gripper/arduino/egogrip_gripper/egogrip_gripper.ino` (+ README, arduino-cli
build → uf2). Keep CircuitPython as the no-toolchain alternate; keep the framed protocol.

Read path: AS5600 over `Wire`, polled at ≥ the state rate. Emit **raw 12-bit counts** (within-turn,
health/debug) + **multi-turn accumulated `delta_counts`** relative to the start-closed tare +
magnet-health. Conversion to mm/m is **not** done in firmware.

Calibration model (revised for multi-turn travel):
- The gripper's full open/close likely spans **more than one encoder turn**, and the AS5600 is
  absolute only *within* one turn. So firmware tracks **multi-turn accumulation**: each tick
  `step = wrap12(raw - raw_prev)` (signed, [-2048,2047]); `accum += step`. This yields a continuous
  `delta_counts = accum - accum_at_zero` that grows monotonically across turns. Valid while motion
  between samples < half a turn — trivially true polling at the 200 Hz state rate vs a human hand.
- **Reference = start closed, per session.** Multi-turn accumulation can't survive a power cycle
  (on boot the chip knows the within-turn angle but not which turn), so zero is re-established each
  session. Ergonomic with a spring gripper: it rests closed, so the operator releases it and the app
  sends `ZERO` (GUI "Zero gripper", or auto-zero on arm when width is stable at the low end);
  `accum_at_zero` is captured there. (If travel is later confirmed < 1 turn, a persisted one-time
  absolute tare becomes possible — but the multi-turn path is the safe default and costs nothing.)
- Scale = a single linear `counts_per_mm`: `width_mm = width_closed_mm + delta_counts * mm_per_count`,
  from a 2-point measurement (closed = 0, one gauge block). Linear confirmed; the N-point polynomial
  path is deferred but the schema leaves room.
- Magnet health: read AS5600 `STATUS` (MD/ML/MH) + `AGC`; report ok/weak/strong/missing in `INFO`
  + GUI. You never need to know the magnet's pole orientation — seat it so the chip detects it, then tare.
- Authority: log **both**. Firmware/app write `raw_counts` (within-turn, health/debug),
  `delta_counts` (multi-turn accumulated — the authoritative signal), and an on-device
  `width_preview_m` to `gripper_state.csv`; the **pipeline recomputes authoritative `width_m` from
  `delta_counts × counts_per_mm` in calibration.json**, so a scale fix recalibrates old episodes
  without re-recording. The tare and `counts_per_mm` are recorded into the episode for provenance.

Protocol: `INFO` on connect (fw version, sensors, channels, rates, magnet status); `SET_RATE`;
`ZERO` (set the start-closed tare `accum_at_zero`; report it); STATE payload = `raw_counts:u16`
(within-turn) + `delta_counts:i32` (multi-turn accumulated) + `trigger:u8` (configurable threshold).
`TACTILE` kept as 0-channel stub. No LED. Synthetic sweep when no AS5600 attached. Update Kotlin
`Protocol.kt` to match (bump protocol minor version).

New `calibration.json` (loaded by pipeline; referenced by manifest `calibration_ref`): width block
`{count_zero, counts_per_mm, direction, width_closed_mm}` + existing camera intrinsics/extrinsics
slots. New `pipeline/egogrip_pipeline/calibration.py` + a 2-point `calibrate_width` helper +
`docs/CALIBRATION.md`. `gripper_state.csv` columns become
`monotonic_ns, mcu_micros, raw_counts, delta_counts, width_preview_m, trigger`. `sync.py` (currently
reads `width_m` directly at `sync.py:132`) recomputes authoritative width from
`delta_counts × counts_per_mm` (calibration.json), falling back to a `width_*` column when no
calibration is present. The hardcoded `4096/0.08` in `EpisodeWriter.kt` + `synthetic.py` becomes the
calibrated preview, not the source of truth.

## Capability 2 — wrist camera (refactor working code)
Make `EgogripCamera`/`UvcCameraBackend`/`Camera2Client` take id/size/fps/bitrate from config;
derive manifest entry from config. Verify coexistence with ego + serial on the powered hub.

## Capability 1 — ego camera (main new Unity work)
New `app/Assets/Egogrip/EgogripEgoCamera.cs`: enterprise frames (styly plugin) → render onto a
**MediaCodec input Surface** in new AAR `SurfaceEncoder.kt` → `ego.mp4` + `ego_frames.csv` on the
shared clock; 720p/30 default, configurable bitrate; frames stamped `elapsedRealtimeNanos`; pixels
never copied into C#. Document authorized-package setup in `docs/PICO_ENTERPRISE_NOTES.md`.

## Glue (load-bearing)
- Move `Protocol.kt`/`SerialClient.kt` from `app-native/app` into the AAR (`app-native/capture`) +
  new `EgogripSerial.kt` facade + `app/Assets/Egogrip/EgogripSerial.cs` so Unity records width.
- Replace `EgogripPoseRecorder.BuildManifest` string-building with config-driven
  `app/Assets/Egogrip/EgogripManifestWriter.cs`.
- New `app/Assets/Egogrip/EgogripCaptureManager.cs`: read config → start xr_pose + ego + uvc +
  serial on one clock → finalize manifest; per-stream GUI health dot (extend `EgogripHud.cs`); a
  **"Zero gripper"** action that sends `ZERO` (or auto-zeros on arm when width is stable at the
  closed end), since the multi-turn encoder needs a start-closed tare each session.
- Rebuild + re-commit `app/Assets/Plugins/Android/egogrip-capture.aar`.
- Verify `EgogripClock` (C#) and `CaptureClock` (Kotlin) both use `SystemClock.elapsedRealtimeNanos`.

## Frequency mismatch
Don't sync at capture. Each sensor runs native-rate; every sample is stamped with one monotonic
clock on arrival; the offline pipeline resamples onto a uniform grid (default 30 Hz = ego) —
already implemented. `rate_hz` in config is health metadata only; timestamps are authoritative, so
30/50/60 Hz mixes need no special handling. Per-camera fixed latency stored as `latency_offset_ns`
(manifest/config), subtracted at alignment; short doc on measuring it (Tier-1, no LED).

## Build order
1. Config schema + example configs + `config.py` validator + tests (no hardware).
2. Arduino firmware (AS5600) + `calibration.py` + pipeline width recalibration + tests.
3. Serial-into-AAR + config-driven `EgogripManifestWriter` (Unity records width end-to-end).
4. Wrist cam config-driven.
5. Ego enterprise camera → MediaCodec → `ego.mp4`.
6. GUI health + docs (`CAPTURE_CONFIG.md`, `CALIBRATION.md`, frequency doc) + pipeline ego id-map.

## Verification
- Pipeline: `cd pipeline && pip install -e . && pytest`; `egogrip-demo` runs synth→validate→align
  →export. Add `test_config.py`, a width-recalibration test, an ego-stream export test.
- Firmware: `arduino-cli compile`; flash; confirm framed packets + `INFO`; bare-Pico synthetic
  sweep; with AS5600, `ZERO` then a 2-point width check.
- Config: toggle a sensor in `capture_config.json`; confirm `manifest.json` streams change and the
  pipeline still exports.
- End-to-end (hardware): record a short episode (ego+wrist+pose+width), `adb pull`, run pipeline,
  load the LeRobot dataset, eyeball action↔image lag.

## Risks / assumptions
- Enterprise camera frame format / Surface path unverified on hardware; built behind the
  `ego_camera` config type so a take degrades to wrist-only.
- Three USB endpoints + PD charge on one hub assumed OK per Phase-0 hub validation; MVP single wrist cam.
- AAR rebuild needs the Android/Gradle toolchain (already in `app-native`).
- Everything stays N-sensor so bimanual is a config change, not a code change.
