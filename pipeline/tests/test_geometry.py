"""Rotation math tests (require numpy)."""
import pytest

np = pytest.importorskip("numpy")

from egogrip_pipeline import geometry as G  # noqa: E402


def test_slerp_endpoints():
    q0 = G.normalize(np.array([[0.0, 0.0, 0.0, 1.0]]))
    q1 = G.normalize(np.array([[0.0, 1.0, 0.0, 1.0]]))
    np.testing.assert_allclose(G.slerp(q0, q1, np.array([0.0]))[0], q0[0], atol=1e-6)
    np.testing.assert_allclose(G.slerp(q0, q1, np.array([1.0]))[0], q1[0], atol=1e-6)


def test_rot6d_identity():
    q = np.array([[0.0, 0.0, 0.0, 1.0]])
    np.testing.assert_allclose(G.quat_to_rot6d(q)[0], [1, 0, 0, 0, 1, 0], atol=1e-6)


def test_resample_quat_at_source_times():
    t = np.array([0.0, 1.0, 2.0])
    q = G.normalize(np.array([[0, 0, 0, 1.0], [0, 0.3, 0, 1.0], [0, 0.7, 0, 1.0]]))
    out = G.resample_quat(t, q, t)
    # signs may flip on the shorter arc; compare absolute dot ~ 1
    dots = np.abs(np.sum(out * q, axis=1))
    np.testing.assert_allclose(dots, 1.0, atol=1e-6)


def test_unity_handedness_flip():
    pos = np.array([[1.0, 2.0, 3.0]])
    q = G.normalize(np.array([[0.1, 0.2, 0.3, 0.9]]))
    p2, q2 = G.from_unity(pos, q)
    assert p2[0, 2] == -3.0
    np.testing.assert_allclose(q2[0], G.normalize(np.array([[-0.1, -0.2, 0.3, 0.9]]))[0], atol=1e-6)
