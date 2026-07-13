"""Smoke test for the Rerun QA viewer. Skips cleanly when rerun isn't installed."""
from __future__ import annotations

import pytest

from egogrip_pipeline.synthetic import generate_episode


def test_visualize_episode_runs(tmp_path):
    pytest.importorskip("rerun")
    from egogrip_pipeline.rerun_viz import visualize_episode

    ep = generate_episode(tmp_path / "raw", seed=0)
    # spawn=False + no save → in-memory sink (no viewer, no file): exercises every logging path
    # (poses, 26-joint hands, gripper/tactile scalars, sync events, video cadence) so a bad
    # archetype/shape raises here rather than silently.
    visualize_episode(ep, spawn=False, save=None, stride=5)


def test_visualize_episode_saves_rrd(tmp_path):
    pytest.importorskip("rerun")
    from egogrip_pipeline.rerun_viz import visualize_episode

    ep = generate_episode(tmp_path / "raw", seed=0)
    out = tmp_path / "ep.rrd"
    visualize_episode(ep, spawn=False, save=out, stride=5)
    assert out.exists()
