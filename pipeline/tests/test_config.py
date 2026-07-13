"""Tests for the capture config (schema + loader + pose-offset math)."""
from pathlib import Path

import numpy as np
import pytest

from egogrip_pipeline import geometry as G
from egogrip_pipeline.config import (
    CaptureConfig,
    apply_pose_offset,
    resolve_pose_offset,
    validate_config,
)

CONFIGS = Path(__file__).resolve().parents[2] / "configs"


def test_example_configs_validate():
    files = sorted(CONFIGS.glob("*.json"))
    assert files, "no example configs found"
    for f in files:
        assert validate_config(f) == [], f"{f.name} failed validation"


def test_load_single_gripper():
    cfg = CaptureConfig.load(CONFIGS / "single_gripper.json")
    assert {"ego", "wrist0", "gripper_pose", "gripper"} <= {s.id for s in cfg.sensors}
    pose = cfg.sensor("gripper_pose")
    assert pose.type == "xr_pose" and "pose_offset" in pose.params
    gripper = cfg.sensor("gripper")
    assert gripper.params["streams"][0]["stream_id"] == "gripper_state"


def test_stream_id_defaults_to_id():
    cfg = CaptureConfig.load(CONFIGS / "single_gripper.json")
    assert cfg.sensor("ego").out_stream_id == "ego"


def test_duplicate_ids_flagged(tmp_path):
    p = tmp_path / "dup.json"
    p.write_text(
        '{"format_version":"0.1.0","sensors":['
        '{"id":"a","type":"xr_pose","node":"right_hand"},'
        '{"id":"a","type":"uvc_camera","width":640,"height":480}]}'
    )
    assert any("duplicate" in x for x in validate_config(p))


def test_schema_rejects_bad_type(tmp_path):
    p = tmp_path / "bad.json"
    p.write_text('{"format_version":"0.1.0","sensors":[{"id":"x","type":"lidar"}]}')
    assert validate_config(p), "unknown sensor type should be flagged"


def test_xr_pose_requires_node(tmp_path):
    pytest.importorskip("jsonschema")
    p = tmp_path / "nonode.json"
    p.write_text('{"format_version":"0.1.0","sensors":[{"id":"g","type":"xr_pose"}]}')
    assert validate_config(p), "xr_pose without node should fail schema"


def test_quat_mul_matches_matrix_product():
    rng = np.random.default_rng(0)
    a = G.normalize(rng.standard_normal(4))
    b = G.normalize(rng.standard_normal(4))
    lhs = G.quat_to_matrix(G.quat_mul(a, b))[0]
    rhs = G.quat_to_matrix(a)[0] @ G.quat_to_matrix(b)[0]
    assert np.allclose(lhs, rhs, atol=1e-9)


def test_euler_about_x():
    q = G.euler_deg_to_quat([90, 0, 0])
    s = np.sin(np.pi / 4)
    assert np.allclose(q, [s, 0, 0, s], atol=1e-9)


def test_resolve_offset_defaults_identity():
    t, q = resolve_pose_offset(None)
    assert np.allclose(t, [0, 0, 0]) and np.allclose(q, [0, 0, 0, 1])


def test_pose_offset_pure_translation():
    pos = np.zeros((1, 3))
    quat = np.array([[0.0, 0.0, 0.0, 1.0]])
    tp, tq = apply_pose_offset(pos, quat, {"translation_m": [0, 0, -0.10]})
    assert np.allclose(tp[0], [0, 0, -0.10], atol=1e-9)
    assert np.allclose(tq[0], [0, 0, 0, 1], atol=1e-9)


def test_pose_offset_rotates_local_translation():
    # controller yawed +90 deg about +Y: a +X local offset maps to world -Z (right-handed).
    quat = G.euler_deg_to_quat([0, 90, 0]).reshape(1, 4)
    tp, _ = apply_pose_offset(np.zeros((1, 3)), quat, {"translation_m": [0.1, 0, 0]})
    assert np.allclose(tp[0], [0, 0, -0.1], atol=1e-6)


def test_pose_offset_is_invertible():
    # raw controller pose must be recoverable from the TCP pose + offset (rigid transform).
    rng = np.random.default_rng(1)
    pos = rng.standard_normal((5, 3))
    quat = G.normalize(rng.standard_normal((5, 4)))
    offset = {"translation_m": [0.03, -0.01, -0.09], "rotation_euler_deg": [10, -5, 90]}
    tp, tq = apply_pose_offset(pos, quat, offset)
    t, q = resolve_pose_offset(offset)
    q_inv = q * np.array([-1, -1, -1, 1])  # conjugate of a unit quaternion
    back_q = G.quat_mul(tq, np.repeat(q_inv[None], 5, axis=0))
    R = G.quat_to_matrix(back_q)
    back_p = tp - np.einsum("nij,j->ni", R, t)
    assert np.allclose(back_p, pos, atol=1e-9)
    assert np.allclose(G.quat_to_matrix(back_q), G.quat_to_matrix(quat), atol=1e-9)
