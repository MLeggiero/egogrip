# Architecture

## 1. System overview

```
                          PICO 4 Ultra Enterprise (headset)
        ┌───────────────────────────────────────────────────────────────┐
        │  egogrip APK  (Unity + native AAR plugin)                       │
        │                                                                 │
        │  ┌──────────────┐   ┌───────────────┐   ┌────────────────────┐ │
        │  │ XR sources   │   │ Native plugin │   │ In-VR GUI (Unity)  │ │
        │  │ (Unity/PXR)  │   │ (AAR)         │   │ start/stop/verify  │ │
        │  │              │   │               │   └────────────────────┘ │
        │  │ • ego RGB    │   │ • UVC wrist   │            ▲              │
        │  │   (ent. cam) │   │   camera      │            │              │
        │  │ • controller │   │ • USB-serial  │   ┌────────┴──────────┐   │
        │  │   6-DoF pose │   │   (MCU)       │   │ Capture Manager   │   │
        │  │ • hand/head  │   │ • H.264 enc   │──▶│ • 1 monotonic clk │   │
        │  │   pose       │   │   (MediaCodec)│   │ • per-stream queue│   │
        │  └──────┬───────┘   └───────┬───────┘   │ • episode writer  │   │
        │         └───────────────────┴──────────▶│                   │   │
        │                                          └─────────┬─────────┘   │
        │                                                    ▼             │
        │                                       /sdcard/egogrip/<episode>/ │
        └───────────────────────────────────────────────────────────────┘
                                  ▲ USB-C (data + PD charge)
                                  │
                    ┌─────────────┴──────────────┐
                    │  Powered USB-C hub (PD in)  │
                    └───┬──────────────────┬──────┘
                        │ UVC              │ USB-serial (CDC)
                  ┌─────┴─────┐      ┌─────┴───────────────────┐
                  │ wrist cam │      │ RP2040 gripper MCU      │
                  └───────────┘      │ • gripper width encoder │
                                     │ • tactile array (ADC)   │
                                     │ • sync LED driver (opt) │
                                     └─────────────────────────┘
        ┌───────────────────────────────────────────────────────────────┐
        │  OFFLINE (any PC, after the fact)                               │
        │  pipeline/  episode folder ──▶ sync/align ──▶ LeRobot dataset   │
        └───────────────────────────────────────────────────────────────┘
```

The headset is **self-contained**: it captures, stores, and lets the operator verify
episodes with no computer attached. A PC is only involved *after* recording, to convert raw
episodes into a training dataset.

## 2. Components

### 2.1 egogrip APK (on headset)
Single Android app, built as **Unity + a native AAR plugin**.

- **Unity layer** owns the XR session and the GUI:
  - PICO XR / PXR SDK for **controller 6-DoF pose**, **hand tracking** (OpenXR 26-joint),
    **head pose**.
  - PICO **Enterprise Camera API** for **egocentric passthrough RGB** (see
    [PICO_ENTERPRISE_NOTES.md](PICO_ENTERPRISE_NOTES.md)).
  - The **in-VR GUI**: a wrist-anchored panel — Start/Stop, recording timer, per-stream
    health indicators (fps + "alive" dot), free storage/battery, last-episode review.
- **Native AAR plugin** owns the things Unity is bad at:
  - **UVC wrist camera(s)** via an `libuvc`/AndroidUSBCamera-style path → **MediaCodec**
    H.264 → **MediaMuxer** MP4.
  - **USB-serial** (CDC/FTDI) read loop for the MCU stream.
  - Hands frames/poses back to Unity (or writes directly to the episode dir) with native
    timestamps.

`CaptureManager` (in the Unity layer, calling into the plugin) is the orchestrator: opens
all sources, applies one clock, fans samples into per-stream writers, and finalizes the
episode `manifest.json`.

### 2.2 Gripper MCU (RP2040)
Reads gripper jaw **width** (quadrature encoder / hall / pot), the **tactile array** (ADC or
I²C/SPI sensor), and optionally drives a **sync LED**. Streams framed packets over USB-CDC.
See [firmware/rp2040-gripper/](../firmware/rp2040-gripper/).

### 2.3 Mock gripper rig
UMI-style 3D-printed parallel-jaw gripper. Mounts: PICO controller, wrist camera, tactile
pads on the fingers, MCU + cabling to the hub. See [HARDWARE.md](HARDWARE.md).

### 2.4 Pipeline (offline)
Python package that ingests an episode folder, performs **timestamp alignment**, and emits a
**LeRobot v2** dataset. Pluggable exporters (RLDS/HDF5) can be added. See
[pipeline/](../pipeline/).

## 3. Data flow & threading (on device)

Each source runs on its own producer thread/callback and pushes
`(monotonic_ns, payload)` onto a bounded queue:

| Source | Origin | Rate (target) | Written as |
|---|---|---|---|
| ego RGB | enterprise cam API | ~30 fps (cap; sensor up to ~89) | `ego.mp4` + `ego_frames.csv` |
| wrist RGB | UVC via plugin | 30 fps | `wrist0.mp4` + `wrist0_frames.csv` |
| controller pose | PXR | 72–90 Hz | `gripper_pose.csv` |
| hand/head pose | PXR/OpenXR | 72–90 Hz | `poses.jsonl` |
| gripper width | MCU serial | 100–200 Hz | `gripper_state.csv` |
| tactile | MCU serial | 100–1000 Hz | `tactile.csv` / `tactile.npz` |

Video is encoded on-device (hardware codec); **no raw frames are stored** — only the encoded
stream plus a per-frame timestamp index. Low-rate signals are appended as CSV/JSONL so a
crash never loses more than the last buffered samples. Backpressure policy and frame-drop
accounting are explicit (a dropped frame is logged, never silently skipped) — see
[SYNC.md](SYNC.md).

## 4. On-device episode layout

```
/sdcard/egogrip/2026-06-08T14-03-12_ep0007/
├── manifest.json          # device, calibration ref, streams, clock, counts, status
├── ego.mp4                # egocentric H.264
├── ego_frames.csv         # frame_idx, monotonic_ns, pts_ns
├── wrist0.mp4
├── wrist0_frames.csv
├── gripper_pose.csv       # monotonic_ns, x,y,z, qx,qy,qz,qw, tracking_state
├── gripper_state.csv      # monotonic_ns, width_m, raw_counts, trigger
├── tactile.csv            # monotonic_ns, ch0..chN
├── poses.jsonl            # monotonic_ns, head{...}, hand_l[26], hand_r[26]
└── sync_events.csv        # monotonic_ns, kind (e.g. LED_PULSE), id
```

The format is specified in [DATA_FORMAT.md](DATA_FORMAT.md) and
[schema/capture_manifest.schema.json](../schema/capture_manifest.schema.json).

## 5. Coordinate frames

One transform tree, resolved at calibration time, all stored in the manifest:

```
world (PICO play space)
 └── head (HMD)
 └── controller            ──(T_ctrl_gripper, calibrated)──▶ gripper_tcp (jaw midpoint)
 └── ego_cam               ──(intrinsics + T_head_egocam from enterprise API)
gripper_tcp
 └── wrist_cam             ──(T_gripper_wristcam, calibrated)
 └── tactile pads          ──(fixed by CAD)
```

`gripper_tcp` (the tool-center point at the jaw midpoint) is the canonical action frame
exported to LeRobot. `T_ctrl_gripper` and `T_gripper_wristcam` come from a one-time
calibration routine (AprilTag board); see [HARDWARE.md](HARDWARE.md#calibration).

## 6. Failure handling

- **USB disconnect / hub brownout:** CaptureManager marks the stream `degraded`, keeps
  recording the rest, surfaces a red health dot in the GUI, and records the gap in the
  manifest. Powered hub mitigates the EgoKit battery-drain failure mode.
- **Storage full / low battery:** pre-flight check before arming; soft-stop with a clean
  manifest if thresholds are crossed mid-episode.
- **Tracking loss (controller):** `tracking_state` per pose sample so the exporter can drop
  or flag those frames.
- **App crash:** episodes are append-only; a `finalize` step repairs an unfinished manifest
  on next launch.

## 7. Extensibility (single → dual / N sensors)

Streams are declared in the manifest as a list, keyed by id (`wrist0`, `wrist1`,
`gripper0`, …). The capture config enumerates devices; adding a second gripper/camera is a
config + hub-bandwidth change, not a schema change. Tactile is a **plugin**: a sensor driver
implements `read() -> frame` and declares its channel layout, so GelSight/DIGIT (UVC),
FSR/capacitive arrays (serial), or an F/T sensor all land in the same `tactile.*` slot.
