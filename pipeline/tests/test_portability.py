"""Portability proof: a Unity/Quest-frame episode and an OpenXR episode of the SAME motion
must yield identical canonical poses through the unchanged pipeline (decision D11)."""
import pytest

np = pytest.importorskip("numpy")

from egogrip_pipeline.schema import Manifest  # noqa: E402
from egogrip_pipeline.sync import build_timeline  # noqa: E402
from egogrip_pipeline.synthetic import generate_episode  # noqa: E402


def test_unity_and_openxr_episodes_agree(tmp_path):
    oxr = generate_episode(tmp_path / "oxr", seed=0, duration_s=2.0,
                           platform="pico", world_frame="openxr_y_up_rh", episode_id="oxr_ep")
    uni = generate_episode(tmp_path / "uni", seed=0, duration_s=2.0,
                           platform="quest", world_frame="unity_y_up_lh", episode_id="uni_ep")

    a = build_timeline(Manifest.load(oxr), oxr, fps=30.0)
    b = build_timeline(Manifest.load(uni), uni, fps=30.0)
    n = min(len(a.t_seconds), len(b.t_seconds))

    # the pipeline must have converted Unity (left-handed) back to canonical
    np.testing.assert_allclose(a.gripper_pos[:n], b.gripper_pos[:n], atol=1e-5)
    dots = np.abs(np.sum(a.gripper_quat[:n] * b.gripper_quat[:n], axis=1))  # equal up to sign
    np.testing.assert_allclose(dots, 1.0, atol=1e-5)


def test_manifest_declares_platform(tmp_path):
    ep = generate_episode(tmp_path, seed=0, duration_s=0.5, platform="android",
                          world_frame="openxr_y_up_rh")
    m = Manifest.load(ep)
    assert m.device["platform"] == "android"
    assert m.device["capabilities"]["world_frame"] == "openxr_y_up_rh"
