"""Generic, device-agnostic loaders for the raw stream files.

These read ONLY what the manifest declares (file path + kind + columns). Nothing here is
PICO-specific — that is what keeps the pipeline portable (docs/PORTABILITY.md).
"""
from __future__ import annotations

import csv
import json
from pathlib import Path

import numpy as np

from .schema import Manifest, Stream


def read_csv(path: str | Path) -> dict[str, np.ndarray]:
    """Read a headered CSV into {column_name: ndarray}; numeric columns become float64."""
    path = Path(path)
    with path.open(newline="") as f:
        reader = csv.reader(f)
        header = next(reader)
        rows = [r for r in reader if r]
    cols: dict[str, list[str]] = {h: [] for h in header}
    for row in rows:
        for h, v in zip(header, row):
            cols[h].append(v)
    out: dict[str, np.ndarray] = {}
    for h, vals in cols.items():
        try:
            out[h] = np.asarray(vals, dtype=np.float64)
        except ValueError:
            out[h] = np.asarray(vals, dtype=object)
    return out


def read_jsonl(path: str | Path) -> list[dict]:
    path = Path(path)
    with path.open() as f:
        return [json.loads(line) for line in f if line.strip()]


def load_pose(episode_dir: Path, stream: Stream) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """Load a pose6dof stream -> (t_ns, pos(N,3), quat(N,4) xyzw, tracking_state(N,))."""
    c = read_csv(episode_dir / stream.file)
    t = c[stream.timestamp_field]
    pos = np.stack([c["x"], c["y"], c["z"]], axis=1)
    quat = np.stack([c["qx"], c["qy"], c["qz"], c["qw"]], axis=1)
    track = c.get("tracking_state", np.ones_like(t))
    return t, pos, quat, track


def load_scalar(episode_dir: Path, stream: Stream, value_cols: list[str]) -> tuple[np.ndarray, np.ndarray]:
    """Load selected numeric columns of a tabular stream -> (t_ns, values(N, len(cols)))."""
    c = read_csv(episode_dir / stream.file)
    t = c[stream.timestamp_field]
    vals = np.stack([c[col] for col in value_cols], axis=1)
    return t, vals


def load_tactile(episode_dir: Path, stream: Stream) -> tuple[np.ndarray, np.ndarray, list[str]]:
    """Load a tactile stream -> (t_ns, values(N,C), channel_names). Channels from manifest."""
    c = read_csv(episode_dir / stream.file)
    t = c[stream.timestamp_field]
    if stream.channels:
        names = [ch.name for ch in stream.channels]
    else:  # fall back to any ch* columns in file order
        names = [k for k in c if k.startswith("ch")]
    vals = np.stack([c[n] for n in names], axis=1)
    return t, vals, names


def load_video_index(episode_dir: Path, stream: Stream) -> dict[str, np.ndarray]:
    """Load a video's frame index (frame_idx, monotonic_ns, pts_ns)."""
    assert stream.index_file, f"video stream {stream.id} has no index_file"
    return read_csv(episode_dir / stream.index_file)


def mcu_clock_columns(episode_dir: Path, stream: Stream) -> tuple[np.ndarray, np.ndarray] | None:
    """If a serial stream carries its MCU micros counter, return (device_ns, mcu_micros).

    Used by sync.fit_clock for Tier-1 source->device clock correction. None if absent.
    """
    c = read_csv(episode_dir / stream.file)
    if "mcu_micros" in c and stream.timestamp_field in c:
        return c[stream.timestamp_field], c["mcu_micros"]
    return None


def episode_span_ns(manifest: Manifest, episode_dir: Path, required_kinds: list[str]) -> tuple[float, float]:
    """Overlapping [start, stop] across the required stream kinds, in device-clock ns."""
    starts, stops = [], []
    for s in manifest.streams:
        if s.kind not in required_kinds:
            continue
        if s.index_file:
            t = load_video_index(episode_dir, s)["monotonic_ns"]
        else:
            t = read_csv(episode_dir / s.file)[s.timestamp_field]
        if len(t):
            starts.append(float(t[0]))
            stops.append(float(t[-1]))
    if not starts:
        raise ValueError(f"No streams of kinds {required_kinds} found.")
    return max(starts), min(stops)
