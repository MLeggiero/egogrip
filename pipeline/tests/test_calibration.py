"""Tests for gripper width calibration (raw counts -> metres) and its use in alignment."""
import json
from pathlib import Path

import numpy as np

from egogrip_pipeline.calibration import Calibration, WidthCalibration, calibrate_width
from egogrip_pipeline.schema import Manifest
from egogrip_pipeline.synthetic import generate_episode
from egogrip_pipeline.sync import build_timeline


def test_counts_to_m_linear():
    wc = WidthCalibration(counts_per_mm=50.0)
    assert np.isclose(wc.counts_to_m(4000), 0.08)        # 4000/50 = 80 mm = 0.08 m
    assert np.allclose(wc.counts_to_m([0, 2000, 4000]), [0.0, 0.04, 0.08])


def test_width_closed_offset():
    wc = WidthCalibration(counts_per_mm=50.0, width_closed_m=0.005)
    assert np.isclose(wc.counts_to_m(0), 0.005)


def test_calibrate_width_two_point():
    wc = calibrate_width(closed_delta=0, open_delta=4000, open_width_mm=80.0)
    assert np.isclose(wc.counts_per_mm, 50.0)
    assert np.isclose(wc.counts_to_m(2000), 0.04)


def test_calibrate_width_negative_direction():
    wc = calibrate_width(closed_delta=0, open_delta=-4000, open_width_mm=80.0)
    assert wc.direction == -1
    assert np.isclose(wc.counts_to_m(-4000), 0.08)


def test_recalibration_changes_width_without_rerecording():
    a = WidthCalibration(counts_per_mm=50.0)
    b = WidthCalibration(counts_per_mm=40.0)
    assert not np.isclose(a.counts_to_m(4000), b.counts_to_m(4000))


def test_for_episode_resolves_ref(tmp_path):
    d = tmp_path / "ep"
    d.mkdir()
    (d / "calibration.json").write_text(json.dumps({"gripper_width": {"counts_per_mm": 50.0}}))

    class M:
        calibration_ref = "calibration.json"

    cal = Calibration.for_episode(d, M())
    assert cal is not None and cal.width is not None
    assert cal.width.counts_per_mm == 50.0


def test_for_episode_missing_returns_none(tmp_path):
    class M:
        calibration_ref = "nope.json"

    assert Calibration.for_episode(tmp_path, M()) is None


def test_build_timeline_uses_calibration(tmp_path):
    ep = generate_episode(tmp_path, seed=0, duration_s=1.0)
    m = Manifest.load(ep)
    cal = Calibration(width=WidthCalibration(counts_per_mm=50.0))  # matches the synthetic generator
    al = build_timeline(m, ep, fps=30.0, calibration=cal)
    # synthetic sweeps 0..0.08 m; recomputed width from delta_counts must land in that range
    assert al.width_m.min() >= -1e-6
    assert al.width_m.max() <= 0.08 + 2e-3


def test_build_timeline_falls_back_to_preview(tmp_path):
    # no calibration -> uses the on-device width_preview_m column; still produces a width series
    ep = generate_episode(tmp_path, seed=1, duration_s=1.0)
    m = Manifest.load(ep)
    al = build_timeline(m, ep, fps=30.0)
    assert al.width_m.shape[0] > 0
    assert al.width_m.max() <= 0.08 + 2e-3
