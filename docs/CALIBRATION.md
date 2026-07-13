# Calibration (`calibration.json`)

Holds the values the pipeline needs to turn raw sensor readings into physical units. Referenced by
each episode's `manifest.json` (`calibration_ref`) and loaded by
[`egogrip_pipeline/calibration.py`](../pipeline/egogrip_pipeline/calibration.py). Example:
[`configs/calibration.example.json`](../configs/calibration.example.json).

Because the firmware logs **raw counts**, width is recomputed offline from this file — so fixing a
calibration value re-derives every past episode **without re-recording**.

## Gripper width (AS5600)

```jsonc
"gripper_width": {
  "count_zero": 0,        // AS5600 start-closed tare (reported by the firmware ZERO command)
  "counts_per_mm": 50.0,  // linear scale from a 2-point measurement
  "width_closed_m": 0.0,  // jaw opening at the closed reference
  "direction": 1          // +1 / -1 for the encoder's count sense as the jaw opens
}
```

Conversion (`WidthCalibration.counts_to_m`):

```
width_m = width_closed_m + (delta_counts * direction) / counts_per_mm / 1000
```

`delta_counts` is the firmware's **multi-turn accumulated** count relative to the start-closed tare
(the gripper's travel can span several encoder turns; the AS5600 is absolute only within one turn,
so the firmware accumulates and you re-zero closed each session — see
[firmware/rp2040-gripper](../firmware/rp2040-gripper/)).

### Measuring `counts_per_mm` (2-point)

1. Release the gripper closed (spring rest) and send `ZERO` → `delta_counts ≈ 0`.
2. Open the jaws to a known gauge (e.g. an 80 mm block) and read `delta_counts`.
3. `counts_per_mm = delta_counts / 80`. In code:

```python
from egogrip_pipeline.calibration import calibrate_width
wc = calibrate_width(closed_delta=0, open_delta=4000, open_width_mm=80.0)  # -> 50 counts/mm
```

Use more points and average if the linkage isn't perfectly linear (the schema leaves room for a
polynomial later).

## Cameras & pose (future)

`calibration.json` is also where camera intrinsics and the `T_ctrl_gripper` controller→TCP transform
go once the Phase-4 hand-eye calibration exists; `capture_config.json`'s `pose_offset` can reference
it with `{ "from_calibration": true }`.
