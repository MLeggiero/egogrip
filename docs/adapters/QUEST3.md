# Adapter: Meta Quest 3

Proves the contract on a second VR headset. Quest 3 is very close to PICO (Android XR, Unity,
controllers, USB-C host), so most of the capture core is shared; only the SDK bindings differ.

## Capability profile (manifest `device`)
```json
{
  "platform": "quest",
  "model": "Meta Quest 3",
  "capabilities": {
    "ego_rgb": true,            // Passthrough Camera API (gated, like PICO enterprise)
    "ego_depth": false,         // depth API limited; treat as stretch
    "head_pose": true,
    "hand_tracking": true,
    "controller_pose": true,
    "world_frame": "unity_y_up_lh"
  }
}
```

## Frame
Unity left-handed, +Y up → declare `unity_y_up_lh`. The pipeline's `from_unity` converter
normalizes to canonical (negate Z, negate qx/qy). No on-device handedness math needed.

## Per-stream sourcing
| Stream | Quest source |
|---|---|
| `gripper_pose` (pose6dof) | OVR controller pose (controller strapped to gripper) × `T_ctrl_gripper` |
| head / hands | OVRCameraRig + Meta Hand Tracking (OVRSkeleton, 26-joint) |
| `ego` (video_rgb) | Passthrough Camera API — **gated**, request access like PICO's enterprise camera |
| `wrist0` (video_rgb) | UVC over USB-C host (same native plugin as PICO) |
| `gripper_state`, `tactile` | RP2040 over USB-serial (identical firmware + protocol) |

## What changes vs PICO
- Pose/hand SDK: **OVR/Meta XR SDK** instead of PICO Integration SDK (swap the `PoseSource`).
- Ego camera: **Passthrough Camera API** instead of PICO enterprise camera (swap `CameraSource`).
- Everything else — clock, `EpisodeWriter`, manifest, UVC plugin, serial, pipeline — is reused.

## Degradation
If passthrough-camera access isn't granted, set `ego_rgb:false` and record wrist-only; the
exporter masks the missing ego camera. Same fallback as PICO.
