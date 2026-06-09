"""End-to-end alignment + export tests (require numpy)."""
from pathlib import Path

import pytest

np = pytest.importorskip("numpy")

from egogrip_pipeline.export_lerobot import export_episode  # noqa: E402
from egogrip_pipeline.schema import Manifest  # noqa: E402
from egogrip_pipeline.synthetic import generate_episode  # noqa: E402
from egogrip_pipeline.sync import build_timeline  # noqa: E402


def test_build_timeline_shapes(tmp_path: Path):
    ep = generate_episode(tmp_path, seed=0, duration_s=2.0)
    m = Manifest.load(ep)
    al = build_timeline(m, ep, fps=30.0)
    T = len(al.t_seconds)
    assert T > 0
    assert al.gripper_pos.shape == (T, 3)
    assert al.gripper_quat.shape == (T, 4)
    assert al.width_m.shape == (T,)
    assert al.tactile is not None and al.tactile.shape == (T, 4)
    assert set(al.video_frame_idx) == {"ego", "wrist0"}
    # uniform grid
    dt = np.diff(al.t_seconds)
    np.testing.assert_allclose(dt, dt[0], rtol=1e-3)


def test_clock_fit_recovered(tmp_path: Path):
    ep = generate_episode(tmp_path, seed=0, duration_s=2.0)
    m = Manifest.load(ep)
    build_timeline(m, ep, fps=30.0)  # fills clock_fit on serial streams
    fit = m.stream("gripper_state").clock_fit
    assert fit is not None
    assert abs(fit.a - 1000.0) < 1.0          # ns-per-microsecond, ~1000
    assert fit.residual_ms is not None and fit.residual_ms < 1.0


def test_export_neutral(tmp_path: Path):
    ep = generate_episode(tmp_path / "raw", seed=0, duration_s=2.0)
    out = export_episode(ep, tmp_path / "ds", fps=30.0, action="absolute", rotation="rot6d")
    assert (out / "meta.json").exists()
    data = np.load(out / "arrays.npz")
    T = data["timestamp"].shape[0]
    assert data["observation.state"].shape == (T, 10)   # xyz(3)+rot6d(6)+width(1)
    assert data["action"].shape == (T, 10)
    assert data["observation.tactile"].shape == (T, 4)
    assert "frame_idx.ego" in data and data["frame_idx.ego"].shape == (T,)


def test_action_delta_runs(tmp_path: Path):
    ep = generate_episode(tmp_path / "raw", seed=3, duration_s=1.5)
    out = export_episode(ep, tmp_path / "ds", fps=30.0, action="delta", rotation="rot6d")
    data = np.load(out / "arrays.npz")
    assert data["action"].shape[1] == 10
