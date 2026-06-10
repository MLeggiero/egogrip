"""Time alignment: per-stream clock correction + resampling onto one uniform timeline.

Strategy (docs/SYNC.md):
  Tier-0  one monotonic device clock (already in the raw files)
  Tier-1  fit device ≈ a*mcu_micros + b for serial streams (implemented here); latency offsets
  Tier-2  LED/serial anchors pin camera time to serial time (detector stubbed for Phase 5)

Device-agnostic: operates purely on the manifest + standard stream files.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from . import geometry as G
from . import io
from .schema import GRIPPER_STATE, POSE6DOF, TACTILE, VIDEO_RGB, Manifest, Stream

NS_PER_S = 1_000_000_000.0


@dataclass
class AlignedEpisode:
    """Every stream resampled onto a single uniform grid (device-clock ns)."""
    fps: float
    t_ns: np.ndarray                              # (T,) grid on the device clock
    t_seconds: np.ndarray                         # (T,) seconds from episode start
    gripper_pos: np.ndarray                       # (T,3) meters, canonical world frame
    gripper_quat: np.ndarray                      # (T,4) xyzw
    gripper_track: np.ndarray                     # (T,) tracking state
    width_m: np.ndarray                           # (T,)
    tactile: np.ndarray | None = None             # (T,C)
    tactile_channels: list[str] = field(default_factory=list)
    video_frame_idx: dict[str, np.ndarray] = field(default_factory=dict)  # stream_id -> (T,) idx or -1
    max_pairwise_skew_ms: float = 0.0


# --------------------------------------------------------------------------- Tier-1


def fit_clock(mcu_micros: np.ndarray, device_ns: np.ndarray) -> tuple[float, float, float]:
    """Least-squares fit device_ns ≈ a*mcu_micros + b. Returns (a, b, residual_ms)."""
    x = np.asarray(mcu_micros, dtype=np.float64)
    y = np.asarray(device_ns, dtype=np.float64)
    a, b = np.polyfit(x, y, 1)
    residual_ms = float(np.sqrt(np.mean((y - (a * x + b)) ** 2)) / 1e6)
    return float(a), float(b), residual_ms


def _corrected_serial_times(episode_dir: Path, stream: Stream) -> np.ndarray:
    """Device-clock timestamps for a serial stream, MCU-clock-corrected when possible."""
    pair = io.mcu_clock_columns(episode_dir, stream)
    raw_t = io.read_csv(episode_dir / stream.file)[stream.timestamp_field]
    if pair is None:
        return raw_t - float(stream.latency_offset_ns)
    device_ns, mcu = pair
    a, b, res = fit_clock(mcu, device_ns)
    if stream.clock_fit is not None:
        stream.clock_fit.a, stream.clock_fit.b, stream.clock_fit.residual_ms = a, b, res
    return (a * mcu + b) - float(stream.latency_offset_ns)


# --------------------------------------------------------------------------- Tier-2 (stubs)


def detect_led_anchors(video_path: str, roi: tuple[int, int, int, int]) -> np.ndarray:
    """Find LED-flash frame times (brightness spikes in an ROI). Phase 5."""
    raise NotImplementedError("LED anchor detection — Phase 5")


def fit_clock_from_anchors(video_flash_ns: np.ndarray, serial_event_ns: np.ndarray) -> tuple[float, float]:
    """Fit camera_time -> serial_time from matched anchors. Phase 5."""
    raise NotImplementedError("anchor clock fit — Phase 5")


# --------------------------------------------------------------------------- helpers


def _nearest(src_t: np.ndarray, query_t: np.ndarray) -> np.ndarray:
    """Index in src_t nearest to each query_t."""
    idx = np.searchsorted(src_t, query_t)
    idx = np.clip(idx, 1, len(src_t) - 1)
    left = src_t[idx - 1]
    right = src_t[idx]
    idx -= (np.abs(query_t - left) <= np.abs(query_t - right)).astype(int)
    return np.clip(idx, 0, len(src_t) - 1)


def _find(manifest: Manifest, kind: str, frame: str | None = None) -> Stream | None:
    for s in manifest.streams_of_kind(kind):
        if frame is None or s.frame == frame:
            return s
    return None


# --------------------------------------------------------------------------- main


def build_timeline(manifest: Manifest, episode_dir: str | Path, fps: float = 30.0) -> AlignedEpisode:
    """Resample every stream onto a uniform `fps` grid over the overlapping span."""
    episode_dir = Path(episode_dir)
    step = NS_PER_S / fps

    pose = _find(manifest, POSE6DOF, frame="world") or _find(manifest, POSE6DOF)
    if pose is None:
        raise ValueError("No gripper pose (pose6dof) stream — cannot build action timeline.")
    state = _find(manifest, GRIPPER_STATE)
    tac = _find(manifest, TACTILE)
    videos = manifest.streams_of_kind(VIDEO_RGB)

    # span over pose + videos (device clock)
    span_kinds = [POSE6DOF] + ([VIDEO_RGB] if videos else [])
    start, stop = io.episode_span_ns(manifest, episode_dir, span_kinds)
    grid = np.arange(start, stop + 1e-6, step)
    if len(grid) < 2:
        raise ValueError("Episode too short for the requested fps.")

    # gripper pose -> normalize the device's declared frame to canonical, then resample
    pt, pos, quat, track = io.load_pose(episode_dir, pose)
    src_frame = (manifest.device.get("capabilities") or {}).get("world_frame", "openxr_y_up_rh")
    convert = G.FRAME_CONVERTERS.get(src_frame, G.from_openxr)
    pos, quat = convert(pos, quat)
    g_pos = G.resample_linear(pt, pos, grid)
    g_quat = G.resample_quat(pt, quat, grid)
    g_track = track[_nearest(pt, grid)]

    # gripper width (serial -> clock-corrected)
    if state is not None:
        wt = _corrected_serial_times(episode_dir, state)
        w = io.read_csv(episode_dir / state.file)["width_m"]
        width = G.resample_linear(wt, w, grid)
    else:
        width = np.zeros(len(grid))

    # tactile (serial -> clock-corrected), optional
    tactile = None
    tac_names: list[str] = []
    if tac is not None:
        tt = _corrected_serial_times(episode_dir, tac)
        _, tvals, tac_names = io.load_tactile(episode_dir, tac)
        tactile = G.resample_linear(tt, tvals, grid)

    # videos: nearest frame index + skew bookkeeping
    video_idx: dict[str, np.ndarray] = {}
    max_skew_ms = 0.0
    for v in videos:
        vt = io.load_video_index(episode_dir, v)["monotonic_ns"]
        ni = _nearest(vt, grid)
        chosen = vt[ni]
        gap_ms = np.abs(chosen - grid) / 1e6
        tol_ms = 1000.0 / fps  # one grid step
        ni = np.where(gap_ms <= tol_ms, ni, -1)
        video_idx[v.id] = ni
        max_skew_ms = max(max_skew_ms, float(gap_ms[gap_ms <= tol_ms].max(initial=0.0)))

    return AlignedEpisode(
        fps=fps,
        t_ns=grid,
        t_seconds=(grid - grid[0]) / NS_PER_S,
        gripper_pos=g_pos,
        gripper_quat=g_quat,
        gripper_track=g_track,
        width_m=width,
        tactile=tactile,
        tactile_channels=tac_names,
        video_frame_idx=video_idx,
        max_pairwise_skew_ms=max_skew_ms,
    )
