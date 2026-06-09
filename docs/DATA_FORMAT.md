# Data format

Two formats: the **raw on-device capture** (optimized for safe, fast, lossless writing on the
headset) and the **LeRobot dataset** (the trainable output). The pipeline converts the first
into the second.

## 1. Raw on-device capture

One folder per episode (see [ARCHITECTURE.md §4](ARCHITECTURE.md#4-on-device-episode-layout)).
Design rules:

- **One clock.** Every row carries `monotonic_ns` from the same source clock
  (`SystemClock.elapsedRealtimeNanos()` on Android). Video also stores codec `pts_ns`.
- **Append-only.** Low-rate signals are CSV/JSONL flushed periodically; a crash loses ≤ the
  last buffer, and the manifest is repaired on next launch.
- **No raw video frames.** Video is hardware-encoded H.264 in an `.mp4`; a sidecar CSV maps
  `frame_idx → monotonic_ns` so frames align to everything else.
- **Self-describing.** `manifest.json` lists every stream, its rate, units, channel layout,
  calibration reference, and final sample counts. The pipeline reads only the manifest to
  discover an episode.

### Per-stream files

| File | Columns / shape | Units | Notes |
|---|---|---|---|
| `ego_frames.csv` | `frame_idx, monotonic_ns, pts_ns` | ns | index into `ego.mp4` |
| `wrist0_frames.csv` | `frame_idx, monotonic_ns, pts_ns` | ns | index into `wrist0.mp4` |
| `gripper_pose.csv` | `monotonic_ns, x, y, z, qx, qy, qz, qw, tracking_state` | m, unit quat | controller→TCP already applied; world frame |
| `gripper_state.csv` | `monotonic_ns, width_m, raw_counts, trigger` | m, counts, 0/1 | jaw opening |
| `tactile.csv` | `monotonic_ns, ch0 … chN` | sensor-defined | layout in manifest; `.npz` if high-rate |
| `poses.jsonl` | `{monotonic_ns, head:{p,q}, hand_l:[26×{p,q}], hand_r:[26×{p,q}]}` | m, quat | OpenXR joint order |
| `sync_events.csv` | `monotonic_ns, kind, id` | — | e.g. `LED_PULSE` markers |

The canonical `manifest.json` shape is enforced by
[schema/capture_manifest.schema.json](../schema/capture_manifest.schema.json).

## 2. LeRobot dataset (output)

Target: **LeRobot v2** (`LeRobotDataset`). The exporter resamples everything onto a single
timeline (default 30 Hz, matching the ego camera) and writes the standard layout
(parquet for tabular features + an `mp4` per camera + `info.json` / `stats`).

### Default feature mapping

| LeRobot key | dtype / shape | Source | Notes |
|---|---|---|---|
| `observation.images.ego` | video (H,W,3) | `ego.mp4` | re-muxed/transcoded as needed |
| `observation.images.wrist` | video (H,W,3) | `wrist0.mp4` | |
| `observation.state` | float32 (7+1) | `gripper_pose` + `gripper_state` | TCP pose (xyz + 6d-rot) ⊕ width |
| `observation.tactile` | float32 (N,) | `tactile.csv` | optional; channel count from manifest |
| `action` | float32 (7+1) | next-step TCP pose ⊕ width | relative or absolute (configurable) |
| `timestamp` | float32 | aligned timeline | seconds from episode start |
| `episode_index`, `frame_index`, `index` | int | bookkeeping | LeRobot standard |

Notes:
- **Rotation representation:** stored as quaternion in raw; exported as **6-D rotation**
  (continuous, preferred for learning) by default — selectable.
- **Action convention:** absolute TCP target by default; delta-action exporter provided. UMI
  uses relative trajectories — both are supported via a flag.
- **Tactile** only appears if present in the episode; mixed datasets are handled by the
  exporter (pads/masks missing modality per LeRobot conventions).
- **Hand/head pose** are exported to optional extra columns (`observation.head_pose`,
  `observation.hands`) — off by default to keep the policy state compact, on for research.

### Alternate exporters
- **RLDS/TFDS** (Open X-Embodiment) and **HDF5 (robomimic)** are planned as additional
  exporters behind the same intermediate representation. See
  [pipeline/egogrip_pipeline/export_lerobot.py](../pipeline/egogrip_pipeline/export_lerobot.py).

## 3. Versioning

`manifest.json` carries `format_version`. The pipeline refuses unknown major versions and
migrates known minor ones. Bump rules live in [DESIGN_DECISIONS.md](DESIGN_DECISIONS.md).
