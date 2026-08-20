"""Visualize a raw egogrip episode in Rerun (https://rerun.io) for QA — before export.

Everything is logged on ONE `monotonic` timeline (the same clock every stream is stamped with), so
the Rerun viewer scrubs poses, hands, camera cadence, tactile and gripper width perfectly aligned —
the visual proof of egogrip's sync design, and a fast way to reject bad episodes (dropouts, lost
tracking, misalignment) before spending time on export/training.

    pip install 'egogrip-pipeline[viz]'
    egogrip-viz path/to/raw_episode                 # spawn the viewer
    egogrip-viz path/to/raw_episode --save ep.rrd   # write a shareable recording (headless)

This reuses the pipeline loaders (`io.py`) and `geometry.from_unity`; it does not touch capture,
export, or the format contract. Rerun is an optional dependency — imported lazily so the core
pipeline runs without it. Exported LeRobot datasets are viewable directly via Rerun's built-in
LeRobot importer, so this module intentionally targets the RAW episode.
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np

from . import io
from .geometry import FRAME_CONVERTERS, from_unity
from .schema import (
    GRIPPER_STATE,
    POSE6DOF,
    SKELETON,
    SYNC_EVENTS,
    TACTILE,
    VIDEO_RGB,
    Manifest,
)

# OpenXR 26-joint hand: finger chains from the wrist (index 1) for LineStrips3D bones.
_HAND_BONES = [
    [1, 2, 3, 4, 5],            # thumb
    [1, 6, 7, 8, 9, 10],        # index
    [1, 11, 12, 13, 14, 15],    # middle
    [1, 16, 17, 18, 19, 20],    # ring
    [1, 21, 22, 23, 24, 25],    # little
]


def _rr():
    """Lazy import so the pipeline works without rerun installed."""
    try:
        import rerun as rr
    except ImportError as e:  # pragma: no cover
        raise ImportError("rerun not installed — install with: pip install 'egogrip-pipeline[viz]'") from e
    return rr


def _set_time_ns(rr, t_ns) -> None:
    """Set the shared `monotonic` timeline, robust across Rerun versions."""
    t_ns = int(t_ns)
    if hasattr(rr, "set_time_nanos"):          # rerun <= 0.23
        rr.set_time_nanos("monotonic", t_ns)
    else:                                       # rerun >= 0.24
        rr.set_time("monotonic", duration=t_ns * 1e-9)


def _log_scalar(rr, entity: str, value: float) -> None:
    """Log one time-series scalar, robust to the Scalar/Scalars rename."""
    if hasattr(rr, "Scalars"):
        rr.log(entity, rr.Scalars(value))
    elif hasattr(rr, "Scalar"):
        rr.log(entity, rr.Scalar(value))
    else:  # pragma: no cover
        rr.log(entity, rr.TimeSeriesScalar(value))


def _frame_converter(manifest: Manifest):
    """Pick the raw->right-handed converter from the manifest's world_frame (default unity)."""
    wf = manifest.raw.get("conventions", {}).get("world_frame", "unity_y_up_lh")
    return FRAME_CONVERTERS.get(wf, from_unity)


def visualize_episode(episode_dir, *, spawn: bool = True, save=None, video: bool = True, stride: int = 1):
    """Log a raw episode to Rerun. Spawns the viewer unless `save` is given (then writes an .rrd)."""
    rr = _rr()
    episode_dir = Path(episode_dir)
    m = Manifest.load(episode_dir)
    conv = _frame_converter(m)
    stride = max(1, int(stride))

    rr.init("egogrip", spawn=spawn and not save)
    if save:
        rr.save(str(save))
    rr.log("world", rr.ViewCoordinates.RIGHT_HAND_Y_UP, static=True)

    _log_poses(rr, episode_dir, m, conv, stride)
    _log_skeletons(rr, episode_dir, m, conv, stride)
    _log_scalars(rr, episode_dir, m, stride)
    _log_events(rr, episode_dir, m)
    if video:
        _log_videos(rr, episode_dir, m, stride)
    return save


def _log_poses(rr, episode_dir, m, conv, stride) -> None:
    for s in m.streams_of_kind(POSE6DOF):
        t, pos, quat, _track = io.load_pose(episode_dir, s)
        pos, quat = conv(pos, quat)
        ent = f"world/{s.id}"
        for i in range(0, len(t), stride):
            _set_time_ns(rr, t[i])
            rr.log(ent, rr.Transform3D(translation=pos[i], quaternion=rr.Quaternion(xyzw=quat[i])))
        if len(pos):
            rr.log(f"{ent}/trail", rr.Points3D(pos[::stride], radii=0.004), static=True)


def _log_skeletons(rr, episode_dir, m, conv, stride) -> None:
    ident = np.array([[0.0, 0.0, 0.0, 1.0]])
    for s in m.streams_of_kind(SKELETON):
        rows = io.read_jsonl(episode_dir / s.file)
        for r in rows[::stride]:
            _set_time_ns(rr, r["monotonic_ns"])
            head = r.get("head")
            if head:
                hp, hq = conv(np.array([head["p"]], dtype=np.float64), np.array([head["q"]], dtype=np.float64))
                rr.log("world/head", rr.Transform3D(translation=hp[0], quaternion=rr.Quaternion(xyzw=hq[0])))
            for side in ("l", "r"):
                joints = r.get(f"hand_{side}")
                ent = f"world/hand_{side}"
                if not joints:
                    continue  # hand untracked this frame → leave prior
                pts = np.array([j["p"] for j in joints], dtype=np.float64)
                pts, _ = conv(pts, np.repeat(ident, len(pts), axis=0))
                rr.log(ent, rr.Points3D(pts, radii=0.006))
                strips = [pts[c] for c in _HAND_BONES if max(c) < len(pts)]
                if strips:
                    rr.log(f"{ent}/bones", rr.LineStrips3D(strips))


def _log_scalars(rr, episode_dir, m, stride) -> None:
    for s in m.streams_of_kind(GRIPPER_STATE):
        col = next((c for c in ("width_preview_m", "width_m") if c in io.read_csv(episode_dir / s.file)), None)
        if col is None:
            continue
        t, vals = io.load_scalar(episode_dir, s, [col])
        for i in range(0, len(t), stride):
            _set_time_ns(rr, t[i])
            _log_scalar(rr, "signals/gripper_width", float(vals[i, 0]))

    for s in m.streams_of_kind(TACTILE):
        t, vals, names = io.load_tactile(episode_dir, s)
        for i in range(0, len(t), stride):
            _set_time_ns(rr, t[i])
            for c, name in enumerate(names):
                _log_scalar(rr, f"signals/tactile/{name}", float(vals[i, c]))


def _log_events(rr, episode_dir, m) -> None:
    for s in m.streams_of_kind(SYNC_EVENTS):
        c = io.read_csv(episode_dir / s.file)
        t = c.get(s.timestamp_field)
        if t is None:
            continue
        kinds, ids = c.get("kind"), c.get("id")
        for i in range(len(t)):
            _set_time_ns(rr, t[i])
            label = f"{kinds[i] if kinds is not None else 'event'} {ids[i] if ids is not None else ''}".strip()
            rr.log("events/led", rr.TextLog(label))


def _log_videos(rr, episode_dir, m, stride) -> None:
    """Best-effort video: attach the encoded mp4 (if Rerun supports AssetVideo) and always log a
    per-frame cadence tick from the index so frame timing is visible on the timeline. Wrapped
    defensively because the video/columnar APIs differ across Rerun versions."""
    for s in m.streams_of_kind(VIDEO_RGB):
        mp4 = episode_dir / s.file
        ent = f"cameras/{s.id}"
        if mp4.exists():
            try:
                rr.log(ent, rr.AssetVideo(path=str(mp4)), static=True)
            except Exception:  # AssetVideo unavailable in this Rerun version — cadence ticks still work
                pass
        if s.index_file and (episode_dir / s.index_file).exists():
            mono = io.load_video_index(episode_dir, s).get("monotonic_ns")
            if mono is not None:
                for tt in mono[::stride]:
                    _set_time_ns(rr, tt)
                    _log_scalar(rr, f"{ent}/frames", 1.0)


def main(argv=None) -> None:
    ap = argparse.ArgumentParser(description="Visualize a raw egogrip episode in Rerun (QA before export).")
    ap.add_argument("episode_dir", type=Path, help="Raw episode folder (contains manifest.json).")
    ap.add_argument("--save", type=Path, default=None, help="Write an .rrd recording (headless; no viewer).")
    ap.add_argument("--no-spawn", action="store_true", help="Don't spawn the viewer (use with a running one).")
    ap.add_argument("--no-video", action="store_true", help="Skip camera streams.")
    ap.add_argument("--stride", type=int, default=1, help="Subsample every Nth sample for a quick look.")
    a = ap.parse_args(argv)
    visualize_episode(a.episode_dir, spawn=not a.no_spawn, save=a.save, video=not a.no_video, stride=a.stride)


if __name__ == "__main__":  # pragma: no cover
    main()
