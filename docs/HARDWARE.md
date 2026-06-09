# Hardware

The physical system: the **PICO 4 Ultra Enterprise** headset plus a hand-held **UMI-style
mock gripper** that carries a controller, a wrist camera, tactile pads, and an MCU, all
feeding the headset through a **powered USB-C hub**.

## 1. Bill of materials (MVP, single gripper)

See [hardware/bom.csv](../hardware/bom.csv) for the line-item version. Summary:

| Group | Item | Notes |
|---|---|---|
| Headset | PICO 4 Ultra **Enterprise** | enterprise edition required for camera API |
| | PICO controller | strapped to gripper for 6-DoF pose (comes with headset) |
| Connectivity | **Powered** USB-C hub with PD pass-through | fixes EgoKit battery-drain; charges headset while recording |
| | USB-C cable (data + PD) | host → hub |
| Wrist camera | UVC camera module + wide/fisheye lens | global shutter preferred; UVC mandatory |
| Tactile (reference) | FSR / capacitive / magnetic (AnySkin) pad array | read by MCU ADC; pluggable |
| MCU | **RP2040 (Raspberry Pi Pico)** | USB-CDC to hub; ADC + PIO encoder |
| Gripper width | quadrature encoder (or hall / linear pot) | jaw opening → counts |
| Sync (optional) | LED + resistor on a GPIO | Tier-2 hardware sync (see SYNC.md) |
| Gripper body | 3D-printed parallel-jaw gripper, soft fingers, springs/return | UMI-style |
| Mounts | controller mount, wrist-cam mount, MCU/cable management | printed |
| Fasteners | M2/M3 machine screws, heat-set inserts | |

**Long-lead / blocking item:** enrollment for PICO enterprise camera access — start this
first (see [PICO_ENTERPRISE_NOTES.md](PICO_ENTERPRISE_NOTES.md)).

## 2. Mock gripper rig

Design goals, borrowed from UMI and adapted:

- **Parallel-jaw, spring-return**, human-squeezable; jaw width maps to a robot gripper's
  open/close. Soft fingertips host tactile pads.
- **Rigid controller mount** with a *repeatable* seat (kinematic-ish) so `T_ctrl_gripper`
  stays valid across re-mounts.
- **Wrist camera** mounted looking down the jaws (UMI's wide-FOV-at-the-tool idea), clear of
  finger occlusion. Avoid EgoKit's "mount collides with desk" problem — keep the camera
  inside the gripper envelope.
- **Cable strain relief**: one captive cable from the gripper to the hub; the MCU lives on
  the gripper; the wrist camera's USB joins at the hub.
- **N-extensible:** the same body design is mirrored for a second hand later.

CAD lives in [hardware/](../hardware/) (to be added — STEP + STL + printable plates). The BOM
and mount interfaces are defined first so firmware/app work can proceed in parallel.

## 3. Electronics (RP2040)

- **Gripper width:** quadrature encoder on the jaw linkage via RP2040 **PIO** (clean,
  hardware-decoded counts) → calibrated to meters. Fallbacks: hall-effect or linear pot on
  ADC.
- **Tactile:** reference is an analog pad array on the **ADC** (mux if >3 channels) or a
  digital sensor over I²C/SPI. The firmware exposes channels generically; the *meaning* is
  declared in the manifest by the active sensor plugin.
- **Sync LED:** one GPIO drives an LED in camera view; the same event is emitted on serial.
- **Transport:** USB-CDC framed packets, each carrying the MCU `micros()` counter (for the
  Tier-1 clock regression). Protocol in
  [firmware/rp2040-gripper/README.md](../firmware/rp2040-gripper/README.md).

## 4. USB / bandwidth budget

The headset has **one** USB-C port. Everything shares it through the hub:

- 1× UVC wrist camera @ 1280×720/30 (MJPEG on the wire, decode→re-encode on device).
- 1× USB-CDC serial (tiny: width + tactile + sync, ≪ 1 Mbps).
- PD power **in** to charge the headset (separate lane on a proper PD hub).

Headroom check before adding a second camera: two simultaneous UVC streams over USB
high-speed is the practical ceiling EgoKit hit; a global-shutter/MJPEG camera and a powered
hub help. The ego camera does **not** go through USB — it's internal to the headset via the
enterprise API — which frees bandwidth versus a head-mounted USB camera.

## 5. Calibration

A one-time (and re-checkable) procedure produces the transforms in
[ARCHITECTURE.md §5](ARCHITECTURE.md#5-coordinate-frames):

1. **Camera intrinsics** — wrist camera via a checkerboard/Charuco; ego camera intrinsics
   come from the enterprise API (verify against a board).
2. **`T_ctrl_gripper`** — controller mount → TCP. Touch a known fixture / move the TCP around
   a fixed AprilTag and solve hand-eye.
3. **`T_gripper_wristcam`** — AprilTag board viewed by the wrist camera while the tracked
   controller gives ground-truth pose; solve hand-eye (AX=XB).
4. **Width calibration** — open/close against gauge blocks to fit `counts → meters`.
5. **Sync offset** — LED test (SYNC.md Tier 2) to measure per-camera latency offset.

Calibration outputs a `calibration.json` referenced by every episode manifest. A quick
"calibration check" mode in the app flags drift before a recording session.
