# egogrip

**A self-contained, headset-only data-collection system for robot learning.**

`egogrip` turns a **PICO 4 Ultra Enterprise** headset plus a hand-held **UMI-style mock
gripper** into a portable, multimodal demonstration recorder. An operator wears the headset,
holds the mock gripper, and performs a task. The system records **egocentric RGB**, a
**wrist camera**, **tactile**, **gripper width**, and the **6-DoF pose of the gripper** —
all time-aligned — and writes episodes that convert to a
[LeRobot](https://github.com/huggingface/lerobot) dataset for imitation / VLA training.

It is inspired by [EgoKit](https://www.chuange.org/papers/EgoKit.html) (the egocentric
capture workflow) and [UMI](https://umi-gripper.github.io/) (the hand-held gripper as an
action interface), and tries to fix their biggest gaps: **no tactile, no depth, no gripper
state, and loose timing**.

> Status: **scaffolding / planning**. This repo currently contains the architecture, the
> data-format spec, the hardware BOM, and skeletons for each component. See
> [docs/ROADMAP.md](docs/ROADMAP.md) for the phased build plan.

---

## Why this exists

Egocentric human demos are the cheapest way to get manipulation data at scale, but existing
kits stop short of being directly trainable:

| Capability | EgoKit | UMI | **egogrip** |
|---|---|---|---|
| Ego RGB | ✅ | (head GoPro) | ✅ (PICO enterprise camera API) |
| Wrist camera | ✅ (2×) | ✅ | ✅ (1×, schema supports N) |
| Hand/head pose | ✅ | — | ✅ |
| **Gripper 6-DoF pose** | ❌ | ✅ (SLAM, post-hoc) | ✅ (**controller on gripper, live**) |
| **Gripper width (action)** | ❌ | ✅ | ✅ (encoder → MCU) |
| **Tactile** | ❌ | optional | ✅ (**pluggable**, MCU array reference) |
| Depth | ❌ | mirror-stereo trick | ⚠️ investigating (PICO scene/iToF access is limited) |
| Time sync | timestamps only | hardware-ish | shared monotonic clock + optional HW pulse |
| Trainable output | raw MP4 + logs | UMI pipeline | **LeRobot exporter** |
| Needs a PC to operate | no | yes (post) | **no — fully on-headset** |

## Key design decisions

These were chosen deliberately; rationale lives in
[docs/DESIGN_DECISIONS.md](docs/DESIGN_DECISIONS.md).

- **Device:** PICO 4 Ultra **Enterprise** — required for app-level passthrough RGB via the
  enterprise camera API. (Consumer PICO does not expose it.)
- **Gripper pose:** a PICO **controller strapped to the gripper** gives accurate live 6-DoF
  with zero custom CV.
- **Tactile:** a **pluggable sensor interface**; the reference implementation is a low-cost
  pressure/force array on the MCU.
- **App stack:** **Unity + a native Android AAR plugin** — Unity for the PICO XR SDK
  (controller/hand/head pose, enterprise passthrough) and the in-VR GUI; the AAR for USB
  UVC cameras and USB-serial where Unity is weak.
- **Output:** a clean **raw capture format** on-device, converted **after the fact** to
  **LeRobot v2**.
- **Sync:** all streams stamped against **one monotonic device clock**; hooks for an
  optional hardware sync pulse (LED + MCU) to bound drift.
- **Operation:** **self-contained on the headset** — capture, store, and verify need no
  companion computer. Conversion/training happen offline.
- **MCU:** **RP2040 (Raspberry Pi Pico)** reference firmware; ESP32-S3 is the wireless
  upgrade path.
- **MVP:** **single gripper**, but the schema and config are **N-sensor** so bimanual is a
  config change.

## Repository layout

```
egogrip/
├── docs/              architecture, data format, sync, hardware, roadmap, decisions,
│                      PORTABILITY, PICO_TOMORROW (setup runbook), NATIVE_APP_PLAN, adapters/
├── app-native/        native Android (Kotlin) capture app — serial + episodes  [WORKING]
├── app/               Unity APK (in-VR GUI + pose + enterprise camera)  [scaffold]
├── native-plugin/     Android AAR: UVC cameras + USB-serial bridge      [scaffold]
├── firmware/          RP2040 firmware — CircuitPython (flashable now) + C  [WORKING/scaffold]
├── pipeline/          Python: raw capture → LeRobot dataset             [WORKING]
├── hardware/          mock-gripper CAD plan + BOM
└── schema/            on-device raw capture format (JSON Schema)
```

## How it fits together (one paragraph)

The **gripper MCU** reads gripper width + tactile and streams them over **USB-serial** into a
**powered USB-C hub**. A **wrist camera** (UVC) plugs into the same hub. The hub connects to
the headset's single USB-C port (with PD pass-through so the headset charges while
recording). The **APK** on the headset reads the **enterprise passthrough RGB** + **controller
6-DoF pose** + **hand/head pose** internally, and the **wrist camera + serial** via the
native plugin. Every sample is stamped against one monotonic clock and written to an
**episode folder** on the headset. Later, the **pipeline** pulls episodes and exports a
**LeRobot** dataset. Full detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Getting started
Download the latest .apk app file from app/Assets/Builds to the PICO 4 Ultra headset. Run the .apk application on your headset to install, and enjoy!
   camera access (the long-lead item).

## License

MIT — see [LICENSE](LICENSE).
