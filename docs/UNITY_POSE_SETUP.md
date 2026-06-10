# Unity controller-pose capture — detailed setup runbook

Goal: a Unity APK on the PICO 4 Ultra that records a controller's live **6-DoF pose** into an
egogrip episode the pipeline ingests. Roadmap **P2** — **not** gated by the enterprise-camera
authorization, so build it now in parallel with that application
([PICO_ENTERPRISE_NOTES.md](PICO_ENTERPRISE_NOTES.md)).

Ready-to-use scripts: [`app/Assets/Egogrip/`](../app/Assets/Egogrip/) — `EgogripClock.cs`
(shared monotonic clock) and `EgogripPoseRecorder.cs` (pose → `gripper_pose.csv` + `manifest.json`).

Conventions in this doc: `▸` = a click path; **✓ Check** = how to confirm a step worked before
moving on. `adb` = `~/Android/Sdk/platform-tools/adb`.

---

## Stage A — controller pose only (today)

### 0. Unity install prerequisites
- Unity **2022.3 LTS**. In **Unity Hub ▸ Installs ▸ your editor ▸ gear ▸ Add Modules**, make sure
  these are ticked: **Android Build Support**, **Android SDK & NDK Tools**, **OpenJDK**.
- **✓ Check:** in the editor, `Edit ▸ Preferences ▸ External Tools` shows JDK / Android SDK / NDK
  paths filled in (use the Unity-bundled ones).

### 1. Create the project
- **Unity Hub ▸ New Project ▸ 3D (Built-In Render Pipeline) ▸** name it `egogrip-pico`.
- **Recommended:** set its location to **`egogrip/app/`** so the scripts already in
  `app/Assets/Egogrip/` are picked up automatically. (If you put the project elsewhere, you'll copy
  that folder in at step 7.)

### 2. Install XR packages (Package Manager)
- `Window ▸ Package Manager ▸` (top-left dropdown) **Unity Registry**.
- Install **XR Plugin Management** and **XR Interaction Toolkit** (2.5+). For XRI, expand its
  **Samples** and import **Starter Assets** (optional, only if you later want controller visuals).
- **✓ Check:** both appear under Package Manager ▸ *In Project*.

> **Note on the SDK in git:** the PICO Unity Integration SDK (~300 MB, proprietary) is **not**
> committed. After cloning, re-add it by copying the downloaded SDK folder into
> `app/Packages/com.unity.xr.picoxr/` — Unity auto-detects it as an embedded package.

### 3. Import the PICO Unity Integration SDK
- `Assets ▸ Import Package ▸ Custom Package…` ▸ select the `.unitypackage` you downloaded ▸
  **Import** (leave everything ticked).
  - If yours shipped as a tarball instead: `Package Manager ▸ + ▸ Add package from tarball…`.
- **✓ Check:** a **PICO** (or **PXR**) menu appears in the top menu bar and there are no red
  console errors.

### 4. Enable the PICO XR provider
- `Edit ▸ Project Settings ▸ XR Plug-in Management`. If it shows an Install button, click it.
- Select the **Android** tab (the little robot icon) ▸ tick **PICO**.
- Tick **Initialize XR on Startup** (top of the same page).
- **✓ Check:** under `XR Plug-in Management` you now see a **PICO** sub-section in the left tree.

### 5. Run Project Validation (let it fix the Android settings)
- `Project Settings ▸ XR Plug-in Management ▸ Project Validation` ▸ Android tab ▸ **Fix All**.
  This auto-sets graphics API, multithreaded rendering, min API, etc. for PICO.
- **✓ Check:** the list shows all green checks (or only optional warnings).

### 6. Player settings (set the rest by hand)
`Project Settings ▸ Player ▸` (Android tab) ▸ expand the sections:
- **Identification:**
  - **Package Name = `org.egogrip.capture`** ← critical: must match the package PICO authorizes
    for the camera later and the `adb pull` path below.
  - **Minimum API Level = Android 10.0 (API level 29)**; Target API Level = Automatic (or 32+).
- **Configuration:**
  - **Scripting Backend = IL2CPP**
  - **Target Architectures = ARM64** only (untick ARMv7).
  - **Active Input Handling = Both** (safe; avoids input-system surprises).
- **Resolution and Presentation:** Default Orientation = **Landscape Left**.
- **Other Settings ▸ Rendering:** **Color Space = Linear** (Project Validation usually sets this).
- **✓ Check:** Package Name reads `org.egogrip.capture` exactly.

### 7. Add the egogrip scripts
- If your project is **at `egogrip/app/`**: they're already in `Assets/Egogrip/` — confirm Unity
  compiled them (no console errors).
- If your project is **elsewhere**: in a file browser, copy
  `egogrip/app/Assets/Egogrip/EgogripClock.cs` and `EgogripPoseRecorder.cs` into your project's
  `Assets/Egogrip/` folder, then return to Unity and let it recompile.
- **✓ Check:** the **Project** window shows `Assets/Egogrip/EgogripPoseRecorder` with a C# icon and
  no error badge.

### 8. Build the scene
- `File ▸ New Scene ▸ Basic (Built-in)` ▸ save as `Assets/Scenes/Capture.unity`.
- Add an XR rig: `GameObject ▸ XR ▸ XR Origin (VR)`. This creates an `XR Origin` with a `Main
  Camera` child — **delete the old standalone `Main Camera`** that was in the scene so there's only
  one.
- Select the **XR Origin** object ▸ Inspector ▸ on the **XR Origin** component set
  **Tracking Origin Mode = Floor** (stable world frame for the position values).
- `GameObject ▸ Create Empty` ▸ rename to **`EgogripRecorder`** ▸ Inspector ▸ **Add Component** ▸
  type **Egogrip Pose Recorder** ▸ add it. Set **Hand = Right Hand** (the controller you'll strap
  to the gripper). Leave Log Hz = 5.
- `File ▸ Build Settings ▸ Add Open Scenes` so `Capture` is in the build list (ticked).
- **✓ Check:** pressing **Play** in the editor logs `PoseRecorder ready…` in the Console (pose will
  be zeros in-editor — that's fine; real values come on-device).

### 9. Build the APK
- `File ▸ Build Settings ▸ Android` (already switched) ▸ **Build** ▸ choose an output folder/name,
  e.g. `egogrip-pico.apk`. (Use **Build**, not Build And Run, since the headset isn't on USB for
  the editor.)
- **✓ Check:** build finishes with no errors and the `.apk` exists.

### 10. Install + run
```bash
~/Android/Sdk/platform-tools/adb install -r /path/to/egogrip-pico.apk
~/Android/Sdk/platform-tools/adb shell monkey -p org.egogrip.capture 1   # or launch from library
```
- Put the headset on. The app opens into VR (you'll see a plain scene).
- **Press the controller primary button (A on the right / X on the left)** to **start** recording;
  move the controller around; press again to **stop**. No gripper rig needed to test pose.

### 11. Watch it live
```bash
~/Android/Sdk/platform-tools/adb logcat -s egogrip
# expect: "REC → …", "pose[RightHand] t=… p=(x,y,z) q=(…) tracked=1 n=…" at ~5 Hz, then "Saved N…"
# (if nothing under egogrip, try: adb logcat -s Unity)
```

### 12. Pull + validate
```bash
~/Android/Sdk/platform-tools/adb pull \
  /sdcard/Android/data/org.egogrip.capture/files/episodes ./pulled
cd egogrip/pipeline && pip install -e .            # if not already
egogrip-validate ../pulled/<episode_id>            # → ✓ should pass
head -3 ../pulled/<episode_id>/gripper_pose.csv    # eyeball the numbers move
```
`egogrip-export` needs a gripper-width (`gripper_state`) stream too, so a pose-only episode won't
fully export yet — expected until Stage B.

---

## Coordinate frame & TCP (handled for you)
- Recorder logs **raw Unity pose** (left-handed, +Y up) and declares
  `capabilities.world_frame = "unity_y_up_lh"`. The pipeline's `geometry.from_unity` normalizes it
  to canonical OpenXR on import (`p'=(x,y,-z); q'=(-x,-y,z,w)`) — no handedness math in C#.
- `gripper_pose` should be **controller→TCP** (`T_ctrl_gripper`). Until the gripper is built +
  calibrated, that transform is **identity**, so this logs the raw controller pose as the TCP
  stand-in — correct for proving capture; swap in the offset after calibration (docs/HARDWARE.md).

## Stage B — one episode with everything (later)
Package the native capture core (`Protocol.kt`, `SerialClient.kt`, `EpisodeWriter.kt`,
`CaptureClock.kt`) as **`egogrip-capture.aar`** ([native-plugin/](../native-plugin/)) and call it
from Unity, passing the **same `SystemClock.elapsedRealtimeNanos()` origin** (`EgogripClock` already
reads it) so serial/camera/pose share one episode + clock. Then `egogrip-export` runs end-to-end.

## Stage C — ego camera (after PICO authorizes the package)
With `org.egogrip.capture` + this device SN authorized (PICO Integration SDK ≥ 2.5.0), add the
enterprise camera (`AcquireVSTCameraFrameAntiDistortion`) as the `ego` `video_rgb` stream and set
`capabilities.ego_rgb = true`. See [PICO_ENTERPRISE_NOTES.md](PICO_ENTERPRISE_NOTES.md).

---

## Troubleshooting
- **Pose is all zeros / `tracked=0`:** the XR session isn't running. Confirm XR Plug-in Management ▸
  Android ▸ **PICO** ticked, **Initialize XR on Startup** on, and the scene has the **XR Origin**
  camera (not a deleted/extra Main Camera).
- **Primary-button toggle never fires:** the app may not have input focus. Fallback — open
  `EgogripPoseRecorder.cs` and call `StartRecording();` at the end of `Start()` to auto-record on
  launch, rebuild.
- **No `egogrip` logs:** Unity sometimes tags logs `Unity`; the script sets the tag to `egogrip`,
  but try `adb logcat -s Unity egogrip` to be safe.
- **`adb pull` path empty:** confirm the package name is exactly `org.egogrip.capture` (wrong
  package = different `files/` dir).
- **Build error about Graphics API / Vulkan:** re-run Project Validation ▸ **Fix All**.
- **Wrong controller recorded:** set the `Hand` field on the EgogripRecorder component to the
  controller mounted on the gripper.
