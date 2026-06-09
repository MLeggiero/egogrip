# Time synchronization

EgoKit's documented weakness is timing: "frame rate synchronization between cameras remains
imprecise without custom hardware," and it relies on per-camera timestamp logs only. For
manipulation policies, timing errors between the **action** (gripper pose/width) and the
**observation** (images/tactile) directly corrupt what the policy learns. egogrip takes a
tiered approach.

## Tier 0 — one clock (always on)

Every sample, on every stream, is stamped with `SystemClock.elapsedRealtimeNanos()` at the
moment it is received by the capture process. This is a single monotonic clock shared by:

- XR sources (controller/hand/head pose) — already on this clock via the OS.
- UVC frames — stamped in the native plugin on the camera callback (we record both the
  arrival `monotonic_ns` and the codec `pts_ns`).
- Serial samples — stamped on the byte-frame boundary in the plugin read loop.

This alone is better than independent per-device clocks because there is no inter-clock skew
to estimate. The residual error is **latency jitter**: the delay between an event happening
and our timestamping it (USB transfer + buffering + callback scheduling).

### Bounding latency jitter
- **Camera:** prefer drivers that expose a hardware/SOF timestamp; otherwise model a fixed
  per-camera latency offset (measured once, stored in manifest) and only the jitter remains.
- **Serial:** the MCU stamps each packet with its **own** microsecond counter; the plugin
  records arrival time too. The MCU↔device clock relationship is fit by linear regression
  over the session (offset + drift), so serial samples get a corrected device-time estimate
  rather than just arrival time.

## Tier 1 — shared monotonic clock + soft sync  *(default for egogrip)*

Tier 0 plus **explicit offset/drift correction** at conversion time:

1. The pipeline fits, per stream, `device_time ≈ a · source_time + b` using anchor events
   (below) and/or the MCU counter regression.
2. Resampling onto the common timeline uses these corrected times.
3. The manifest records the fitted parameters and the residual so dataset quality is
   auditable.

This is the level we ship. No extra wiring required beyond the MCU's own timestamp field.

## Tier 2 — hardware sync pulse  *(optional, designed-in)*

To bound drift tightly and cross-check Tier 1, the MCU can drive a **sync LED** mounted in
view of the wrist (and, if accessible, ego) camera while simultaneously writing a
`sync_events` row over serial. The LED flash appears in the video frames; the serial event
appears in the MCU stream. The pipeline detects the flash (brightness spike on an ROI) and
the serial marker and pins them to the same instant — anchoring camera time to serial time
directly.

- Cheap (one GPIO + LED), no special camera support needed.
- Run a burst at episode start/end and periodically; gives multiple anchors for the
  regression and a hard check on accumulated drift.
- `sync_events.csv` + the detector live in the pipeline.

## Tier 3 — hardware-triggered capture  *(future, not MVP)*

True frame-level genlock: the MCU triggers exposures. Most consumer **UVC cameras do not
support external trigger**, so this requires a triggerable global-shutter camera (e.g.
machine-vision/Arducam-style) and replaces UVC for that stream. Documented as the ceiling,
not on the MVP path.

## What gets stored for auditing

- `*_frames.csv`: `monotonic_ns` (arrival) and `pts_ns` (codec) per frame.
- serial rows: device arrival `monotonic_ns` **and** MCU `micros` (in the packet).
- `sync_events.csv`: every LED pulse / marker.
- `manifest.json`: per-stream fitted offset/drift + residual after alignment, and a global
  `max_pairwise_skew_ms` estimate so bad episodes can be filtered automatically.

## Acceptance target (MVP)

After Tier-1 alignment, the **action↔ego-image** skew should be **< 1 frame (≈33 ms)** at
30 Hz, verified with the Tier-2 LED test on a sample of episodes. The LED test is the
acceptance gate; Tier-0/1 is what runs in normal operation.
