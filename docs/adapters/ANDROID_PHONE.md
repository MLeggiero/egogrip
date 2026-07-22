# Adapter: Android phone

The hardest portability case — no VR controllers, no headset passthrough — and therefore the
best stress test of the contract. **Rig (as built): the phone is worn on the chest/head as the
egocentric camera** (not on the gripper); wrist camera(s) + the RP2040 connect via a USB-C hub to
the phone. ARCore VIO tracks the *wearer* (ego/head pose + ego video); the **gripper's 6-DoF pose
comes from an AprilTag/ArUco marker on the gripper, detected in the ego image** (PnP) and lifted to
world by the phone's VIO. Native Kotlin app in `app-native/` (`ArCoreEgoSource` + `GripperMarkerTracker`).

## Capability profile (manifest `device`)
```json
{
  "platform": "android",
  "model": "Pixel / Galaxy ...",
  "capabilities": {
    "ego_rgb": true,            // rear Camera2
    "ego_depth": false,         // mono phones; (Pro/ToF phones could set true)
    "head_pose": true,          // ARCore VIO (the wearer)
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
| `gripper_pose` (pose6dof) | **AprilTag/ArUco marker on the gripper**, detected in the ego camera → PnP `T_egocam_marker` → `camWorldPose · T_egocam_marker · T_marker_TCP`. `tracking_state` = 0 when the marker is occluded. |
| `head_pose` (pose6dof) | **ARCore** device 6-DoF world pose (the wearer). |
| `ego` (video_rgb) | phone **rear camera via ARCore** (`acquireCameraImage` → H.264) |
| `wrist0…` (video_rgb) | UVC over the USB-C hub (`EgogripCamera`, multi-device) |
| `gripper_state`, `tactile` | RP2040 over USB-serial via the hub (`EgogripSerial`, same firmware) |
| hands | **absent** — capabilities say so; exporter omits those columns |

## What changes vs PICO
- Replace the XR `PoseSource` with an **ARCore** `PoseSource` (device pose, not controller).
- Replace the enterprise camera with **Camera2** for `ego`.
- Pure native Android app (no Unity needed) — this adapter doubles as the simplest reference
  implementation of the capture core on bare Android.
- Calibration gains `T_marker_TCP` (marker→gripper jaw) + the printed **marker size**, in place of
  `T_ctrl_gripper`. Camera intrinsics come from ARCore at runtime.

## Why it still "just works" downstream
`observation.state` is still TCP pose ⊕ width; `action` is unchanged. Missing modalities
(head/hands, maybe wrist cam) are simply not present in the manifest, and the exporter masks
them — so phone episodes and PICO episodes can live in the **same LeRobot dataset**.

## iOS note
Same shape with **ARKit** (device world pose, also right-handed +Y up) + **AVFoundation**
(camera) + **LiDAR** depth on Pro models (`ego_depth:true`). USB cameras/serial are restricted
on iOS → prefer BLE for the MCU.
