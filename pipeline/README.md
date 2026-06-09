# egogrip pipeline (offline)

Converts raw on-device **episode folders** into a trainable dataset. Runs on any PC after
recording — the headset never needs it to operate (decision D7). It is **device-agnostic**:
it reads only the manifest, so PICO / Quest / Android / iOS episodes all flow through unchanged
(decision D11, [../docs/PORTABILITY.md](../docs/PORTABILITY.md)).

> Status: **working end-to-end** for the neutral output (synth → validate → align → export).
> numpy is the only hard dependency. The **LeRobot** writer's video transcode is the remaining
> Phase-6 `TODO`; everything else (clock-fit, slerp/linear resampling, state/action build,
> tactile, frame indexing) is implemented and tested.

## Quick start (no hardware)

```bash
pip install -e .                 # only needs numpy; add [validate] for jsonschema
egogrip-demo --out _demo_out     # synth episode -> validate -> align -> neutral dataset
python -m pytest                 # 10 tests (geometry, clock-fit, e2e export)
```

The demo prints the recovered serial clock-fit + residual and the aligned frame count, e.g.
`frames=89 @ 30Hz | state_dim=10 | action=absolute`.

## Pipeline stages
1. **Load** — read `manifest.json`, discover streams (only the manifest is trusted).
2. **Align** ([egogrip_pipeline/sync.py](egogrip_pipeline/sync.py)) — apply per-stream
   latency offsets + the Tier-1 clock fit; optionally use Tier-2 LED anchors; resample all
   streams onto one timeline (default 30 Hz = ego rate). See [../docs/SYNC.md](../docs/SYNC.md).
3. **Export** ([egogrip_pipeline/export_lerobot.py](egogrip_pipeline/export_lerobot.py)) —
   write `observation.images.*`, `observation.state` (TCP pose ⊕ width), optional
   `observation.tactile`, and `action`, per [../docs/DATA_FORMAT.md](../docs/DATA_FORMAT.md).

## Real episodes
```bash
egogrip-validate /path/to/episode_or_folder         # gate against the format contract
egogrip-export   /path/to/episode_or_folder --out ./ds --fps 30 --action absolute
egogrip-export   /path/to/ep --out ./ds --target lerobot   # Phase 6 (needs lerobot+video)
```

## Layout
- `egogrip_pipeline/schema.py` — dataclasses mirroring the manifest JSON Schema.
- `egogrip_pipeline/geometry.py` — quaternion slerp, 6-D rotation, per-device frame converters.
- `egogrip_pipeline/io.py` — generic stream loaders (no device-specific code).
- `egogrip_pipeline/sync.py` — Tier-1 clock fit + resampling onto one timeline.
- `egogrip_pipeline/export_lerobot.py` — state/action build; neutral + LeRobot writers.
- `egogrip_pipeline/synthetic.py` — stdlib episode generator (test data, no hardware).
- `egogrip_pipeline/validate.py` — format-contract validator (the "is this device supported?" gate).
- `egogrip_pipeline/demo.py` — end-to-end demo.

## Dependencies
`numpy` (required). Optional extras: `[validate]` (jsonschema), `[lerobot]`, `[video]` (PyAV).
RLDS/HDF5 exporters slot in alongside the LeRobot writer behind the same aligned representation.
