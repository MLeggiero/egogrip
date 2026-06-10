# Native app — what needs to be coded

Plan for the on-device capture app, ordered so tomorrow's PICO session is productive and each
step is independently testable. "Native-first" gets data flowing without Unity/enterprise
approval; the same capture core later becomes the AAR plugin inside the Unity app.

## P0 — Serial + episode (DONE, in `app-native/`)
- USB device listing (proves hub/OTG/camera enumeration on the PICO).
- RP2040 USB-serial open + protocol parse → `gripper_state.csv` + `tactile.csv`.
- `EpisodeWriter` emits the egogrip raw format; one shared monotonic clock.
- **Feeds pipeline:** `egogrip-validate` passes on pulled episodes today.
- **Gap:** no pose, no video yet → `egogrip-export` (needs pose) won't run; that's expected.

## P1 — UVC camera → mp4 (tomorrow's stretch / next code task)
Goal: `wrist0.mp4` + `wrist0_frames.csv` time-aligned to serial.
- Open UVC via herohan/UVCAndroid (`optional/UvcClient.kt` is the starting point).
- Pipe frames to **MediaCodec** (H.264) → **MediaMuxer** mp4; write `frame_idx,monotonic_ns,pts_ns`
  on the shared clock in the frame callback.
- Register the stream via `EpisodeWriter.setVideo(...)`.
- **Test:** pull an episode, `egogrip-validate`; spot-check frame timestamps vs serial.
- Risk: UVC libs are finicky — keep it isolated so serial capture never depends on it.

## P2 — Gripper pose (the real action signal) — needs XR
Native Android can't easily get controller/head/hand pose; two paths:
- **(Recommended) Unity app** with the PICO Integration SDK: controller 6-DoF (controller
  strapped to gripper) × `T_ctrl_gripper` → `gripper_pose.csv` (kind `pose6dof`, frame `world`,
  declared `unity_y_up_lh` so the pipeline converts). Reuse P0/P1 as a **native AAR plugin**
  (`native-plugin/`) for USB/serial/camera; Unity owns pose + GUI.
- (Alt) Native OpenXR via the NDK — avoids Unity but you hand-roll XR + GUI; only worth it if
  staying out of Unity is a hard requirement.
- **Unlocks:** `egogrip-export` end-to-end (state = TCP pose ⊕ width, action = next-step) →
  LeRobot, which the pipeline already does.

## P3 — Ego RGB (enterprise camera) — needs enrollment
- After PICO grants camera access for `org.egogrip.capture`, add the enterprise camera as the
  `ego` `video_rgb` stream in the Unity app; set `capabilities.ego_rgb=true`.
- Investigate depth/scene access (likely limited) — keep `ego_depth` a stretch.

## P4 — In-VR GUI + calibration + health
- Wrist-anchored Start/Stop, per-stream health dots, storage/battery pre-flight, last-episode
  review (docs/ARCHITECTURE.md §2.1).
- Calibration mode → `calibration.json` (`T_ctrl_gripper`, `T_gripper_wristcam`, width fit,
  sync-LED offset). Pose export consumes it.

## The bridge: native core → Unity AAR
`Protocol.kt`, `SerialClient.kt`, and `EpisodeWriter.kt` are written to be **reused**: package
them as the [`native-plugin`](../native-plugin/) AAR and call them from Unity's `CaptureManager`,
passing Unity's `elapsedRealtimeNanos()` origin so XR poses and USB streams share one clock.
So nothing built for tomorrow is throwaway.

## Test ladder (what proves each step)
| Step | On-device check | Pipeline check |
|---|---|---|
| P0 | device list shows SERIAL; CSVs fill | `egogrip-validate` ✓ |
| P1 | `wrist0.mp4` plays; frame count sane | validate ✓; timestamps align |
| P2 | pose CSV tracks the gripper | `egogrip-export` → LeRobot ✓ |
| P3 | ego frames recorded | export with ego camera ✓ |
| P4 | hands-free Start/Stop in VR | full dataset trains a baseline |
