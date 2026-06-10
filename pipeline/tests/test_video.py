"""Video decode/transcode tests (require numpy + PyAV)."""
import pytest

np = pytest.importorskip("numpy")
pytest.importorskip("av")

from egogrip_pipeline.export_lerobot import export_episode  # noqa: E402
from egogrip_pipeline.synthetic import generate_episode  # noqa: E402
from egogrip_pipeline.video import decode_indices, probe  # noqa: E402


def _expected_color(i: int) -> np.ndarray:
    return np.array([(7 * i) % 256, (5 * i) % 256, (3 * i) % 256])


def test_decode_recovers_frame_index(tmp_path):
    ep = generate_episode(tmp_path, seed=0, duration_s=1.0, real_video=True)
    idx = np.array([0, 3, 7, 10])
    frames = decode_indices(ep / "ego.mp4", idx)
    assert frames.shape[0] == 4
    for k, i in enumerate(idx):
        mean = frames[k].reshape(-1, 3).mean(0)
        assert np.abs(mean - _expected_color(int(i))).max() < 18, (mean, _expected_color(int(i)))


def test_gap_index_is_black(tmp_path):
    ep = generate_episode(tmp_path, seed=0, duration_s=1.0, real_video=True)
    frames = decode_indices(ep / "ego.mp4", np.array([-1]))
    assert frames[0].max() == 0


def test_neutral_video_transcode(tmp_path):
    ep = generate_episode(tmp_path / "raw", seed=0, duration_s=1.0, real_video=True)
    out = export_episode(ep, tmp_path / "ds", fps=30.0, video=True)
    assert (out / "ego.mp4").exists() and (out / "wrist0.mp4").exists()
    assert probe(out / "ego.mp4")["width"] > 0
