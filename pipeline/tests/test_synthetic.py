"""Stdlib-only tests: generation + format validation work with zero dependencies."""
from pathlib import Path

from egogrip_pipeline.synthetic import generate_episode
from egogrip_pipeline.validate import validate_episode


def test_generate_and_validate(tmp_path: Path):
    ep = generate_episode(tmp_path, seed=1, duration_s=2.0)
    assert (ep / "manifest.json").exists()
    for f in ("gripper_pose.csv", "gripper_state.csv", "tactile.csv",
              "ego_frames.csv", "wrist0_frames.csv", "poses.jsonl"):
        assert (ep / f).exists(), f"missing {f}"
    problems = validate_episode(ep)
    assert problems == [], f"validation problems: {problems}"


def test_serial_streams_have_mcu_micros(tmp_path: Path):
    import csv

    ep = generate_episode(tmp_path, seed=2, duration_s=1.0)
    for name in ("gripper_state.csv", "tactile.csv"):
        with (ep / name).open() as f:
            header = next(csv.reader(f))
        assert "mcu_micros" in header, f"{name} should carry the MCU clock column"
