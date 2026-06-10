# Portability: one format, many devices

egogrip is designed so a PICO 4 Ultra, a Meta Quest 3, an Android phone, or an iPhone/iPad can
all produce **the same dataset** and feed **the same offline pipeline** — exactly EgoKit's
"unified workflow across heterogeneous devices" idea, extended to gripper + tactile + state.

## The one rule

> **The raw episode format is the contract.** Anything that can (1) keep a single monotonic
> clock, (2) write its streams in the canonical units/frames below, and (3) emit a conformant
> `manifest.json`, is a supported capture device. **The pipeline reads only the manifest — it
> contains zero device-specific code.**

This is why the pipeline is built first: it pins the contract every device must satisfy.

## Canonical conventions (pin these once, convert per device)

| Thing | Canonical choice | Notes |
|---|---|---|
| Length | **meters** | |
| Time | **nanoseconds**, monotonic, one clock/episode | `monotonic_ns` in every row |
| World frame | **OpenXR**: right-handed, **+Y up, −Z forward** | each adapter converts into this |
| Orientation | **unit quaternion (x, y, z, w)** | exported as 6-D rotation by default |
| Pose meaning | `gripper_pose` is the **TCP** (jaw midpoint) in **world** | controller→TCP applied on-device |
| Images | encoded video + per-frame `monotonic_ns` index | codec in manifest |

An adapter may store poses in its **native** frame as long as it **declares** that frame in
`device.capabilities.world_frame`; the **pipeline normalizes to canonical on load** (so the
handedness math lives in one tested place, not re-implemented per device):
- **Unity** (PICO/Quest): `unity_y_up_lh` — left-handed, +Y up. Pipeline flips handedness.
- **ARKit** (iOS): `openxr_y_up_rh` — right-handed, +Y up, already canonical (verify forward axis).
- **ARCore** (Android): `openxr_y_up_rh` — same.

[geometry.py](../pipeline/egogrip_pipeline/geometry.py) holds the converters
(`FRAME_CONVERTERS`); [sync.build_timeline](../pipeline/egogrip_pipeline/sync.py) applies the
one named by the manifest. This is verified by `tests/test_portability.py`, which proves a
Unity-frame episode and an OpenXR-frame episode of the same motion produce identical canonical
poses through the unchanged pipeline. Concrete per-device writeups live in
[adapters/](adapters/).

## Capability profile

Devices differ in what they can sense. The manifest's `device.capabilities` declares it, and
the exporter degrades gracefully (masks absent modalities — LeRobot supports mixed datasets).

```json
"device": {
  "platform": "pico|quest|android|ios",
  "model": "PICO 4 Ultra Enterprise",
  "capabilities": {
    "ego_rgb": true, "ego_depth": false,
    "head_pose": true, "hand_tracking": true, "controller_pose": true,
    "world_frame": "openxr_y_up_rh"
  }
}
```

### What each platform realistically provides

| Modality | PICO 4U Ent | Quest 3 | Android phone | iPhone Pro / iPad |
|---|---|---|---|---|
| Ego RGB | enterprise cam API | passthrough cam API (gated) | rear Camera2 | rear AVFoundation |
| Ego depth | ⚠️ stretch (iToF not exposed) | ⚠️ depth API limited | ❌ (mono) | ✅ **LiDAR** (Pro) |
| Gripper 6-DoF | **controller on gripper** | controller on gripper | device world-track + marker | ARKit world-track + marker |
| Hand / head pose | ✅ OpenXR | ✅ OpenXR | ARCore (device pose) | ARKit (device pose) |
| Wrist cam (USB) | ✅ UVC | ✅ UVC | ✅ UVC (USB host) | ⚠️ limited USB cam support |
| Tactile / width (MCU) | ✅ USB-serial | ✅ USB-serial | ✅ USB-serial | ⚠️ MFi / BLE instead |

A phone has no VR controller, so the gripper pose comes from the device's own world tracking
plus a rigid offset (phone clamped to the rig) or a tracked marker — same `gripper_pose`
stream, different source. The format doesn't change.

## Capture-side portability: Platform Abstraction Layer (PAL)

On-device code splits into **shared core** (identical everywhere) and **platform adapters**
(the only per-device code):

```
        ┌──────────────────────── shared capture core ───────────────────────┐
        │  Clock (one monotonic source)                                       │
        │  EpisodeWriter  → writes the format + manifest (this is the contract)│
        │  Stream registry / health / pre-flight                              │
        └──────────────▲───────────────▲───────────────▲────────────────────┘
                       │               │               │   implement per device
            ┌──────────┴───┐ ┌─────────┴────┐ ┌────────┴─────────┐
            │ PoseSource   │ │ CameraSource │ │ SensorSource     │
            │ head/ctrl/hand│ │ ego/wrist    │ │ tactile/width    │
            └──────────────┘ └──────────────┘ └──────────────────┘
   PICO/Quest: Unity+PXR/OVR   Android: Camera2+ARCore   iOS: ARKit+AVFoundation
```

The `EpisodeWriter`, clock, and manifest are written once and reused; porting a device means
implementing the three `*Source` interfaces with that platform's SDK and supplying its frame
conversion. Everything downstream is identical.

## How to add a new device (checklist)

1. Implement the `PoseSource` / `CameraSource` / `SensorSource` you can on that platform.
2. Provide the native→canonical frame transform (add it to `geometry.py`).
3. Use the shared `EpisodeWriter` to emit streams + a conformant `manifest.json` with a
   correct `device.capabilities` block.
4. Validate an episode against [schema/capture_manifest.schema.json](../schema/capture_manifest.schema.json)
   (`egogrip-validate <episode>`).
5. Run the pipeline (`egogrip-export …`) — **no pipeline changes required.**

If steps 4–5 pass, the device is supported. That's the whole bar.
