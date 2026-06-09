"""Rotation / frame math, kept in ONE place so device portability is auditable.

Canonical frame (see docs/PORTABILITY.md): right-handed, +Y up, -Z forward (OpenXR).
Quaternions are (x, y, z, w), unit length. Per-device adapters convert their native frame
into canonical; those converters live here too.

Only hard dependency of the pipeline: numpy.
"""
from __future__ import annotations

import numpy as np

# --------------------------------------------------------------------------- core


def normalize(q: np.ndarray) -> np.ndarray:
    """Normalize quaternion(s) of shape (..., 4)."""
    q = np.asarray(q, dtype=np.float64)
    n = np.linalg.norm(q, axis=-1, keepdims=True)
    n = np.where(n == 0.0, 1.0, n)
    return q / n


def quat_to_matrix(q: np.ndarray) -> np.ndarray:
    """(N,4) xyzw unit quaternions -> (N,3,3) rotation matrices."""
    q = normalize(np.atleast_2d(q))
    x, y, z, w = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
    R = np.empty((q.shape[0], 3, 3), dtype=np.float64)
    R[:, 0, 0] = 1 - 2 * (y * y + z * z)
    R[:, 0, 1] = 2 * (x * y - w * z)
    R[:, 0, 2] = 2 * (x * z + w * y)
    R[:, 1, 0] = 2 * (x * y + w * z)
    R[:, 1, 1] = 1 - 2 * (x * x + z * z)
    R[:, 1, 2] = 2 * (y * z - w * x)
    R[:, 2, 0] = 2 * (x * z - w * y)
    R[:, 2, 1] = 2 * (y * z + w * x)
    R[:, 2, 2] = 1 - 2 * (x * x + y * y)
    return R


def matrix_to_rot6d(R: np.ndarray) -> np.ndarray:
    """(N,3,3) -> (N,6): first two columns (Zhou et al. 2019 continuous rotation)."""
    R = np.atleast_3d(R)
    return np.concatenate([R[:, :, 0], R[:, :, 1]], axis=-1)


def quat_to_rot6d(q: np.ndarray) -> np.ndarray:
    """(N,4) xyzw -> (N,6) continuous 6-D rotation (first two cols of R; Zhou et al. 2019)."""
    return matrix_to_rot6d(quat_to_matrix(q))


# --------------------------------------------------------------------------- slerp


def slerp(q0: np.ndarray, q1: np.ndarray, frac: np.ndarray) -> np.ndarray:
    """Vectorized spherical linear interpolation. q0,q1: (N,4); frac: (N,)."""
    q0 = normalize(q0)
    q1 = normalize(q1).copy()
    frac = np.asarray(frac, dtype=np.float64)

    dot = np.sum(q0 * q1, axis=-1)
    flip = dot < 0.0  # take the shorter arc
    q1[flip] *= -1.0
    dot = np.abs(dot)
    dot = np.clip(dot, -1.0, 1.0)

    theta = np.arccos(dot)
    sin_theta = np.sin(theta)
    near = sin_theta < 1e-6  # ~parallel -> fall back to lerp

    w0 = np.where(near, 1.0 - frac, np.sin((1.0 - frac) * theta) / np.where(near, 1.0, sin_theta))
    w1 = np.where(near, frac, np.sin(frac * theta) / np.where(near, 1.0, sin_theta))
    out = w0[:, None] * q0 + w1[:, None] * q1
    return normalize(out)


def resample_quat(src_t: np.ndarray, src_q: np.ndarray, query_t: np.ndarray) -> np.ndarray:
    """Resample quaternions sampled at src_t onto query_t via slerp (clamped at ends)."""
    src_t = np.asarray(src_t, dtype=np.float64)
    src_q = normalize(src_q)
    n = src_t.shape[0]
    if n == 1:
        return np.repeat(src_q, query_t.shape[0], axis=0)
    idx = np.searchsorted(src_t, query_t, side="right") - 1
    idx = np.clip(idx, 0, n - 2)
    t0, t1 = src_t[idx], src_t[idx + 1]
    denom = np.where(t1 == t0, 1.0, t1 - t0)
    frac = np.clip((query_t - t0) / denom, 0.0, 1.0)
    return slerp(src_q[idx], src_q[idx + 1], frac)


def resample_linear(src_t: np.ndarray, src_v: np.ndarray, query_t: np.ndarray) -> np.ndarray:
    """Per-column linear interpolation. src_v: (N,) or (N,D). Clamped at the ends."""
    src_t = np.asarray(src_t, dtype=np.float64)
    src_v = np.asarray(src_v, dtype=np.float64)
    if src_v.ndim == 1:
        return np.interp(query_t, src_t, src_v)
    return np.stack([np.interp(query_t, src_t, src_v[:, c]) for c in range(src_v.shape[1])], axis=1)


# ------------------------------------------------------------------ frame converters
# Each per-device adapter records its native frame; these map a (pos, quat) pair INTO
# the canonical OpenXR frame. Add one function per platform as devices are ported.


def from_unity(pos: np.ndarray, quat: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Unity (left-handed, +Y up) -> canonical (right-handed, +Y up).

    Handedness flip about Z: negate the Z position component and the quaternion's z & w-cross
    terms. Concretely: p' = (x, y, -z); q' = (-x, -y, z, w).
    """
    pos = np.atleast_2d(np.asarray(pos, dtype=np.float64)).copy()
    quat = normalize(np.atleast_2d(quat)).copy()
    pos[:, 2] *= -1.0
    quat[:, 0] *= -1.0
    quat[:, 1] *= -1.0
    return pos, quat


def from_openxr(pos: np.ndarray, quat: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """ARKit/ARCore/OpenXR are already canonical (right-handed, +Y up). Identity."""
    return np.atleast_2d(np.asarray(pos, dtype=np.float64)), normalize(np.atleast_2d(quat))


FRAME_CONVERTERS = {
    "unity_y_up_lh": from_unity,
    "openxr_y_up_rh": from_openxr,
}
