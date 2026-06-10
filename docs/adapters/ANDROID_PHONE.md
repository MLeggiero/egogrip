# Adapter: Android phone

The hardest portability case — no VR controllers, no headset passthrough — and therefore the
best stress test of the contract. A phone is clamped to (or is) the gripper rig.

## Capability profile (manifest `device`)
```json
{
  "platform": "android",
  "model": "Pixel / Galaxy ...",
  "capabilities": {
    "ego_rgb": true,            // rear Camera2
    "ego_depth": false,         // mono phones; (Pro/ToF phones could set true)
    "head_pose": false,         // no HMD
    "hand_tracking": false,     // no headset hand tracking
    "controller_pose": false,   // no VR controller
    "world_frame": "openxr_y_up_rh"
  }
}
```

## Frame
ARCore world tracking is right-handed, +Y up → declare `openxr_y_up_rh` (identity convert;
verify forward axis on first calibration).

## Per-stream sourcing
| Stream | Android source |
|---|---|
| `gripper_pose` (pose6dof) | **ARCore** device 6-DoF world pose × `T_phone_gripper` (phone rigidly mounted to the rig). No controller, so the *device itself* is the tracked body. |
| `ego` (video_rgb) | rear **Camera2** stream |
| `wrist0` (video_rgb) | UVC over USB-C host (OTG), if a second camera is attached |
| `gripper_state`, `tactile` | RP2040 over USB-serial (same firmware) — or BLE if the USB port is taken by a camera |
| head / hands | **absent** — capabilities say so; exporter omits those columns |

## What changes vs PICO
- Replace the XR `PoseSource` with an **ARCore** `PoseSource` (device pose, not controller).
- Replace the enterprise camera with **Camera2** for `ego`.
- Pure native Android app (no Unity needed) — this adapter doubles as the simplest reference
  implementation of the capture core on bare Android.
- Calibration gains `T_phone_gripper` (how the phone is clamped) in place of `T_ctrl_gripper`.

## Why it still "just works" downstream
`observation.state` is still TCP pose ⊕ width; `action` is unchanged. Missing modalities
(head/hands, maybe wrist cam) are simply not present in the manifest, and the exporter masks
them — so phone episodes and PICO episodes can live in the **same LeRobot dataset**.

## iOS note
Same shape with **ARKit** (device world pose, also right-handed +Y up) + **AVFoundation**
(camera) + **LiDAR** depth on Pro models (`ego_depth:true`). USB cameras/serial are restricted
on iOS → prefer BLE for the MCU.
