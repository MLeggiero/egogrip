# Capture config (`capture_config.json`)

The **input** config that drives recording: a typed list of sensor instances the on-device app
opens. It is distinct from the **output** `manifest.json`, which *describes* an episode after the
fact. Editing this file — not code — changes what gets recorded. Schema:
[`schema/capture_config.schema.json`](../schema/capture_config.schema.json); examples in
[`configs/`](../configs/); loader/validator in
[`pipeline/egogrip_pipeline/config.py`](../pipeline/egogrip_pipeline/config.py).

## Shape

```jsonc
{
  "format_version": "0.1.0",
  "operator": "default",            // optional defaults written into episodes
  "task_label": "pick and place",
  "calibration_ref": "calibration.json",
  "export": { "fps": 30.0 },        // optional offline-export hints (recorder ignores)
  "sensors": [ /* sensor instances */ ]
}
```

Each sensor has a unique `id`, a `type`, `enabled` (default true), an optional nominal `rate_hz`
(health/metadata only — timestamps are authoritative), and an output `stream_id` (defaults to `id`).

## Sensor types

| `type` | records | key params |
|---|---|---|
| `ego_camera` | PICO enterprise front camera → `ego.mp4` | `width`, `height`, `fps`, `codec`, `bitrate` |
| `uvc_camera` | USB UVC camera → `<stream_id>.mp4` | `index` or `usb{vid,pid}`, `width`, `height`, `fps`, `codec`, `bitrate` |
| `xr_pose` | a controller/head 6-DoF pose stream | `node`, `frame`, `pose_offset` |
| `rp2040_serial` | gripper width / tactile over USB-serial | `usb{vid,pid}`, `baud`, `streams[]` |

`usb` ids are hex strings (e.g. `"vid": "0x2E8A"`). Adding a second gripper or camera is just
another entry — the schema and manifest are N-sensor (see [`configs/dual_gripper.json`](../configs/dual_gripper.json)).

## Gripper pose offset (`xr_pose.pose_offset`)

The mock gripper's TCP pose is the controller pose plus a **fixed rigid offset** (`T_ctrl_gripper`,
in the controller's local frame). Put it here so the recorded `gripper_pose` is the **real TCP pose
directly**:

```jsonc
"pose_offset": {
  "translation_m": [0.0, -0.02, -0.10],   // x,y,z metres, controller-local
  "rotation_euler_deg": [0.0, 0.0, 0.0]   // or rotation_quat_xyzw: [x,y,z,w]
}
```

- The capture app applies it at record time: `p_tcp = p + R(q)·t_off`, `q_tcp = q ⊗ q_off`.
  Reference implementation + tests: `geometry.compose_pose_offset` / `config.apply_pose_offset`.
- Euler is `[rx, ry, rz]` degrees with `R = Rz·Ry·Rx`; use `rotation_quat_xyzw` for a precise value.
- The transform is rigid and invertible, and the applied offset is recorded in the episode, so the
  **raw controller pose is always recoverable** — a wrong offset never forces a re-record.
- `"from_calibration": true` defers to `calibration.json`'s measured `T_ctrl_gripper` once the
  Phase-4 hand-eye calibration exists.

## On the headset

The app loads the config at record time via `EgogripCaptureConfig.Load()`
([app/Assets/Egogrip/EgogripCaptureConfig.cs](../app/Assets/Egogrip/EgogripCaptureConfig.cs)),
searching `Application.persistentDataPath` first, then StreamingAssets. Push your edited config to
the device's app files dir:

```bash
adb push configs/single_gripper.json \
  /sdcard/Android/data/<your.package.name>/files/capture_config.json
```

`EgogripPoseRecorder` reads the matching `xr_pose` sensor and applies its `pose_offset` to the live
controller pose, so `gripper_pose.csv` is the real TCP — and the applied offset is written into the
episode `manifest.json` (raw recoverable). With no config it falls back to the recorder's Inspector
fields (identity by default). Wiring the cameras + serial sensors through the same config is the
next step (the config-driven CaptureManager).

## Validate

```bash
python -c "from egogrip_pipeline.config import validate_config; \
print(validate_config('configs/single_gripper.json') or 'ok')"
```

Full schema validation needs `jsonschema` (`pip install -e 'pipeline[validate]'`); without it the
loader falls back to structural checks. `config.check_against_manifest(cfg, manifest)` asserts every
enabled sensor's stream id shows up in a recorded episode.
