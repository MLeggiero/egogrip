# egogrip pipeline (offline)

Converts raw on-device **episode folders** into a trainable **LeRobot v2** dataset. Runs on
any PC after recording — the headset never needs it to operate (decision D7).

> Status: scaffold. Modules define the interfaces and the conversion outline; the heavy
> lifting (video remux, LeRobot writing) is stubbed with `TODO`s.

## Pipeline stages
1. **Load** — read `manifest.json`, discover streams (only the manifest is trusted).
2. **Align** ([egogrip_pipeline/sync.py](egogrip_pipeline/sync.py)) — apply per-stream
   latency offsets + the Tier-1 clock fit; optionally use Tier-2 LED anchors; resample all
   streams onto one timeline (default 30 Hz = ego rate). See [../docs/SYNC.md](../docs/SYNC.md).
3. **Export** ([egogrip_pipeline/export_lerobot.py](egogrip_pipeline/export_lerobot.py)) —
   write `observation.images.*`, `observation.state` (TCP pose ⊕ width), optional
   `observation.tactile`, and `action`, per [../docs/DATA_FORMAT.md](../docs/DATA_FORMAT.md).

## Usage (planned)
```bash
pip install -e .
egogrip-export /path/to/episode_or_folder --out ./my_dataset --fps 30 --action absolute
```

## Layout
- `egogrip_pipeline/schema.py` — dataclasses mirroring the manifest JSON Schema.
- `egogrip_pipeline/sync.py` — clock fit + resampling.
- `egogrip_pipeline/export_lerobot.py` — LeRobot writer (RLDS/HDF5 exporters slot in here).

## Dependencies (planned)
`numpy`, `pandas`, `pyav`/`opencv` (video), `lerobot`. Pinned in `pyproject.toml` once the
LeRobot version is fixed.
