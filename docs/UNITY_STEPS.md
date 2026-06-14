# Unity / on-device steps (what can't be built in CI)

The headset app is a **Unity** project (`app/`) plus a native **AAR** (`app-native/capture`). Neither
Unity nor the Android SDK runs in the automation environment, so the C#/Kotlin app changes are
written and reviewed but **not compiled or run here** — they need you on a machine with Unity + the
PICO Integration SDK and a headset. This page lists the manual steps; the Python pipeline and the
firmware logic are covered by their own tests/docs.

For the base "build the APK + sideload it" flow, follow the existing runbooks
[UNITY_POSE_SETUP.md](UNITY_POSE_SETUP.md) and [PICO_TOMORROW.md](PICO_TOMORROW.md). The steps below
are the **new** bits for the capture config + gripper pose offset.

## 1. Build & install the APK

1. Open `app/` in Unity (version in `app/ProjectSettings/ProjectVersion.txt`).
2. Player Settings → **Package Name** = your PICO-**authorized** package id (required later for the
   enterprise ego camera; see [PICO_ENTERPRISE_NOTES.md](PICO_ENTERPRISE_NOTES.md)).
3. XR Plug-in Management → enable **PICO**. Confirm the `Capture.unity` scene has the XR rig +
   `EgogripPoseRecorder`.
4. Build an Android APK and install: `adb install -r egogrip.apk`.

## 2. Put a capture config on the device

The app reads `capture_config.json` from its files dir (then StreamingAssets). Edit one of the
[`configs/`](../configs/) files and push it:

```bash
adb push configs/single_gripper.json \
  /sdcard/Android/data/<your.package.name>/files/capture_config.json
```

No file present → the recorder falls back to its Inspector fields (identity offset). See
[CAPTURE_CONFIG.md](CAPTURE_CONFIG.md).

## 3. Set & tune the gripper pose offset

`gripper_pose` is the controller pose with the `xr_pose.pose_offset` applied, so it records the real
TCP. Set it in the config:

```jsonc
"pose_offset": {
  "translation_m": [0.0, -0.02, -0.10],   // controller -> jaw midpoint, metres (controller-local)
  "rotation_euler_deg": [0.0, 0.0, 0.0]    // or rotation_quat_xyzw
}
```

Tune it empirically: start translation-only (measure controller mount → jaw midpoint with a ruler),
record, and adjust until the TCP sits at the jaws. Euler uses `Rz·Ry·Rx`; for a precise value use
`rotation_quat_xyzw`. The offset is rigid, so raw controller pose stays recoverable.

## 4. Verify on device

1. Record a short take (press the controller primary button, or your GUI Start).
2. Pull it: `adb pull /sdcard/Android/data/<pkg>/files/episodes/<id> ./ep`.
3. Check `manifest.json` → the `gripper_pose` stream carries a `pose_offset` block.
4. Check `gripper_pose.csv` reflects the offset (with a non-zero translation, the logged xyz differ
   from the bare controller position by the rotated offset).
5. Run the pipeline: `python -m egogrip_pipeline.validate ./ep` then export — the TCP/action stream
   should look right.

## Status: wired vs pending in Unity

| Piece | State |
|---|---|
| Controller/head pose → `gripper_pose.csv` | ✅ working (`EgogripPoseRecorder`) |
| **`pose_offset` from `capture_config.json` applied at capture** | ✅ wired (`EgogripCaptureConfig` + recorder) — needs on-device verification |
| Wrist UVC camera → mp4 | ✅ working (`EgogripWristCamera` + AAR) |
| Cameras + serial **driven by `capture_config.json`** | ⬜ pending — the config-driven `CaptureManager` |
| Gripper width/tactile **serial in the Unity app** | ⬜ pending — move serial into the AAR (today it's only in the standalone `app-native/app`) |
| Enterprise **ego camera** → `ego.mp4` | ⬜ pending — `EgogripEgoCamera` + MediaCodec surface |

The pending rows are the next implementation steps in [UPGRADE_PLAN.md](UPGRADE_PLAN.md); they all
require Unity/Android builds, so they'll come with manual build+verify steps like the above.
