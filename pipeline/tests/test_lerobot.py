"""LeRobot v2 export test — runs only if `lerobot` (and PyAV) are installed."""
import pytest

pytest.importorskip("numpy")
pytest.importorskip("av")
pytest.importorskip("lerobot")

from egogrip_pipeline.export_lerobot import export_episode  # noqa: E402
from egogrip_pipeline.synthetic import generate_episode  # noqa: E402


def test_lerobot_export_creates_dataset(tmp_path):
    ep = generate_episode(tmp_path / "raw", seed=0, duration_s=1.0, real_video=True)
    root = export_episode(ep, tmp_path / "lerobot_ds", fps=30.0,
                          target="lerobot", video=True, repo_id="egogrip/test")
    assert (root / "meta" / "info.json").exists()
