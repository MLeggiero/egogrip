# Roadmap / build plan

Build order is chosen so each phase produces something testable and the **long-lead item
(PICO enterprise camera enrollment)** is unblocked first. Phases overlap where the work is
independent (firmware vs app vs pipeline).

Legend: 🔴 blocking / long-lead · 🟢 can start immediately · ⚪ depends on earlier phase.

## Phase 0 — Foundations & access  (week 0)
- 🔴 Apply for **PICO enterprise camera access** (testing program + authorized package name).
  This gates ego-RGB; start day one. See [PICO_ENTERPRISE_NOTES.md](PICO_ENTERPRISE_NOTES.md).
- 🟢 Acquire BOM ([HARDWARE.md](HARDWARE.md)); confirm a **powered PD** USB-C hub works for
  data + charge on the headset simultaneously.
- 🟢 Lock the **raw capture schema** ([schema/](../schema/)) so all three workstreams agree.
- **Exit criteria:** hub validated (camera + serial + charging at once); schema frozen v0.1.

## Phase 1 — Capture spine on device  (weeks 1–2)
Goal: record a real episode folder with *most* streams, no calibration polish yet.
- ⚪ Unity project + PICO Integration SDK; controller/hand/head pose logging to `poses.jsonl`
  / `gripper_pose.csv` on one monotonic clock.
- ⚪ Native AAR: open one **UVC** camera → MediaCodec H.264 → `wrist0.mp4` + frame CSV.
- ⚪ Native AAR: **USB-serial** read loop → `gripper_state.csv` / `tactile.csv`.
- ⚪ `CaptureManager`: arm → record → finalize `manifest.json`; storage/battery pre-flight.
- **Exit criteria:** Start/Stop produces a valid, schema-conformant episode with wrist video
  + poses + serial, all on one clock.

## Phase 2 — Ego camera + GUI  (weeks 2–3)
- ⚪ Integrate **enterprise camera API** → `ego.mp4` + frame CSV (once Phase-0 access lands).
- ⚪ **In-VR GUI**: wrist-anchored panel — Start/Stop, timer, per-stream health (fps + alive
  dot), storage/battery, errors.
- **Exit criteria:** full multimodal episode (ego + wrist + poses + width + tactile) captured
  hands-busy via the GUI; degraded streams visibly flagged.

## Phase 3 — Firmware & gripper rig  (weeks 2–4, parallel)
- 🟢 RP2040 firmware: encoder (PIO) width, tactile ADC, USB-CDC framed protocol w/ `micros`.
- 🟢 Print/build the **UMI-style gripper**; mount controller, wrist cam, tactile, MCU.
- ⚪ Sync LED on a GPIO + `sync_events` emission.
- **Exit criteria:** physical rig streams width + tactile + sync events; mounts repeatable.

## Phase 4 — Calibration  (week 4)
- ⚪ Wrist intrinsics; verify ego intrinsics from API.
- ⚪ Hand-eye for `T_ctrl_gripper` and `T_gripper_wristcam`; width `counts→m` fit.
- ⚪ `calibration.json` + in-app **calibration check** mode.
- **Exit criteria:** TCP pose in world frame is accurate; reprojection sanity passes.

## Phase 5 — Sync verification  (week 5)
- ⚪ Pipeline LED-flash detector + serial-marker matcher; per-stream offset/drift fit.
- ⚪ Report `max_pairwise_skew_ms`; gate episodes on it.
- **Exit criteria:** action↔ego-image skew **< 1 frame (33 ms)** on sampled episodes
  ([SYNC.md acceptance target](SYNC.md#acceptance-target-mvp)).

## Phase 6 — LeRobot export  (weeks 5–6)
- 🟢/⚪ Pipeline: episode → aligned timeline → **LeRobot v2** dataset (state = TCP pose ⊕
  width, action convention configurable, optional tactile/hand columns).
- ⚪ Round-trip test: load with `LeRobotDataset`, visualize, sanity-check action/obs lag.
- **Exit criteria:** a multi-episode LeRobot dataset loads and visualizes correctly.

## Phase 7 — Validation  (weeks 6–8)
- ⚪ Collect a small task dataset (e.g. pick-place) and **train a baseline policy** (ACT or
  Diffusion Policy / a LeRobot-supported VLA) to prove the data is learnable — the real test,
  and EgoKit's own stated "future work."
- ⚪ On-device episode **review/verify** UX pass (scrub last episode, delete bad takes).
- **Exit criteria:** a policy trains and shows non-trivial behavior from egogrip data.

## Stretch / post-MVP
- Dual / bimanual (second gripper + camera + controller) — schema already supports it.
- Camera-based tactile (GelSight/DIGIT) plugin.
- Depth: revisit PICO scene-mesh/iToF access, or add a rig depth sensor.
- Tier-3 hardware-triggered capture for frame-locked sync.
- Auto-offload (Wi-Fi/ADB) and optional cloud conversion.
- RLDS and HDF5 exporters.

## Top risks (track these)
1. **Enterprise camera access** denied/slow → fallback: rig-mounted USB camera for "ego-ish"
   view, or ship wrist-only first.
2. **USB bandwidth** for 2 cameras → mitigate with MJPEG/global-shutter + powered hub; keep
   MVP single-camera.
3. **Pose accuracy** of controller-on-gripper under fast motion / tracking loss → record
   `tracking_state`, validate against AprilTag ground truth in calibration.
4. **Sync** worse than target → escalate Tier-1 → Tier-2 LED → (last resort) Tier-3 HW.
5. **Thermals/battery** during long captures → powered hub + duty-cycle guidance.
