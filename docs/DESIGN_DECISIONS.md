# Design decisions (ADR-style log)

Records *why* the system is built this way. Update when a decision changes; don't delete
history — supersede.

## D1 — Device: PICO 4 Ultra **Enterprise**
**Decision:** target the Enterprise edition.
**Why:** app-level passthrough **RGB** requires the PICO **Enterprise Camera API**; the
consumer PICO 4 Ultra does not expose it. Raw **iToF depth** frames are generally *not*
exposed to apps even on Enterprise (depth feeds the spatial-mesh internally) — so depth is
treated as **investigate / stretch**, not an MVP guarantee.
**Implications:** enterprise enrollment + authorized package name is a **long-lead, blocking**
task (Phase 0). See [PICO_ENTERPRISE_NOTES.md](PICO_ENTERPRISE_NOTES.md).

## D2 — Gripper pose: **PICO controller strapped to the gripper**
**Decision:** recover gripper 6-DoF from a mounted PICO controller.
**Why:** accurate **live** 6-DoF tracking already provided by the headset, zero custom CV,
no post-hoc SLAM drift (UMI's approach), robust vs hand-tracking occlusion.
**Trade-offs:** needs a rigid, repeatable controller mount (`T_ctrl_gripper` calibration);
controller can lose tracking under fast motion → we log `tracking_state`.
**Alternatives considered:** hand-tracking + offset (noisier), AprilTag-by-headset (needs
camera access + CV), wrist-cam SLAM (UMI; heavy offline, drift).

## D3 — Tactile: **pluggable interface**, MCU array as reference
**Decision:** define a sensor-plugin abstraction; ship a low-cost MCU pressure/force array
as the reference; keep GelSight/DIGIT and F/T as drop-ins.
**Why:** tactile choice is unsettled and modality-dependent (camera tactile = huge USB
bandwidth; arrays = tiny serial). Don't hard-couple the format to one sensor.
**Implications:** `tactile.*` slot is generic; channel layout/units come from the manifest,
written by the active plugin.

## D4 — App stack: **Unity + native Android AAR plugin**
**Decision:** Unity owns XR + GUI; a native AAR owns UVC cameras + USB-serial + encoding.
**Why:** Unity has first-class PICO XR/enterprise-camera/hand-tracking support and makes the
in-VR GUI easy; USB host, UVC, and MediaCodec are far more reliable in native Android. This
is the split EgoKit used successfully.
**Alternatives:** pure native (must hand-roll XR + 3D GUI); all-in-Unity USB (fragile).

## D5 — Output: **raw on-device format → LeRobot v2 (offline)**
**Decision:** write a clean, crash-safe raw episode on device; convert to **LeRobot v2** in
an offline pipeline; keep exporter pluggable (RLDS/HDF5 later).
**Why:** LeRobot is the current de-facto standard for imitation/VLA training; doing heavy
conversion on the headset wastes battery/thermal and risks data loss. Raw-first keeps capture
fast and lossless and decouples capture from training-format churn.

## D6 — Sync: **shared monotonic clock + soft correction**, HW pulse designed-in
**Decision:** Tier-1 (one clock + offset/drift regression using the MCU's own counter) is the
default; a Tier-2 **LED + serial** hardware pulse is built in for anchoring/verification;
Tier-3 genlock is the documented ceiling, not MVP.
**Why:** directly attacks EgoKit's stated weak point without mandating exotic hardware.
**Acceptance:** action↔ego-image skew < 1 frame (33 ms) at 30 Hz, verified by the LED test.
See [SYNC.md](SYNC.md).

## D7 — Topology: **self-contained on the headset**
**Decision:** capture + storage + verification require **no companion computer**; only
offline conversion/training use a PC.
**Why:** portability and field use; explicit user requirement. The in-VR GUI must therefore
cover start/stop, health, and review without external tooling.
**Implications:** keep on-device CPU/GPU/storage budgets modest; no live multi-stream preview
grid (expensive in VR) — show health indicators + single preview + last-episode review.

## D8 — MCU: **RP2040 (Raspberry Pi Pico)**
**Decision:** RP2040 reference firmware; ESP32-S3 documented as the wireless upgrade path;
Teensy 4.x if very high tactile sample rates are needed.
**Why:** excellent/cheap USB-CDC reliability on Android, PIO for hardware quadrature decode,
enough ADC for a tactile array, low cost.

## D9 — Scope: **single gripper, N-extensible schema**
**Decision:** MVP is one gripper / one wrist camera / one controller; the manifest declares
streams as a keyed list so dual/bimanual is a config + bandwidth change, not a rewrite.
**Why:** fastest path to a learnable dataset while keeping the door open; bimanual ~doubles
bandwidth, mechanics, and sync complexity.

## D10 — License: **MIT**
**Decision:** MIT (matches UMI's permissive stance; simplest for adoption).
**Why:** maximize reuse by other robot-learning researchers. Revisit if patent concerns on
hardware arise (would move to Apache-2.0).

## D11 — Device-agnostic: format is the contract; pipeline has no device code
**Decision:** the raw episode format (manifest + streams) is a **cross-device contract** in
canonical units/frames; the offline pipeline depends only on the manifest and contains **no
PICO-specific code**. New devices (Quest 3, Android, iOS) are added via small capture adapters
behind a Platform Abstraction Layer — not pipeline changes.
**Why:** explicit user requirement and the core of EgoKit's value (one workflow across
heterogeneous devices). Decouples capture hardware from the trainable output.
**Implications:** pin canonical conventions (meters, ns, OpenXR right-handed +Y-up, quat xyzw);
manifest carries `device.platform` + `device.capabilities`; exporter degrades gracefully for
absent modalities; build the pipeline **first** to lock the contract. See
[PORTABILITY.md](PORTABILITY.md).
