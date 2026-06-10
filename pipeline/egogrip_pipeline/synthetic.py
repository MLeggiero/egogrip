"""Generate a synthetic episode in the canonical raw format — pure stdlib (no numpy).

Lets you exercise the whole pipeline (validate -> align -> export) with no hardware and no
dependencies. The motion is smooth and deterministic given a seed. Video files are tiny
placeholders (their frame-index CSVs carry real timestamps, which is what alignment needs).
"""
from __future__ import annotations

import csv
import json
import math
import random
from pathlib import Path

NS = 1_000_000_000
FORMAT_VERSION = "0.1.0"


def _axis_angle_quat(ax: float, ay: float, az: float, theta: float) -> tuple[float, float, float, float]:
    n = math.sqrt(ax * ax + ay * ay + az * az) or 1.0
    s = math.sin(theta / 2.0)
    return (ax / n * s, ay / n * s, az / n * s, math.cos(theta / 2.0))  # xyzw


def _write_csv(path: Path, header: list[str], rows: list[list]) -> None:
    with path.open("w", newline="") as f:
        w = csv.writer(f)
        w.writerow(header)
        w.writerows(rows)


def generate_episode(
    out_dir: str | Path,
    *,
    seed: int = 0,
    duration_s: float = 3.0,
    fps_video: float = 30.0,
    hz_pose: float = 72.0,
    hz_state: float = 200.0,
    hz_tactile: float = 200.0,
    n_tactile: int = 4,
    episode_id: str = "2026-06-08T00-00-00_synthetic",
    platform: str = "pico",
    world_frame: str = "openxr_y_up_rh",
    real_video: bool = False,
    video_size: tuple[int, int] = (64, 48),
) -> Path:
    """Write a complete, schema-conformant episode folder. Returns its path.

    `world_frame` controls the frame the pose data is *stored* in (canonical OpenXR by default,
    or "unity_y_up_lh" to emulate a Unity/Quest device); the pipeline normalizes either to
    canonical. `real_video=True` encodes index-coded mp4 frames (needs PyAV) so the export
    transcode is testable; otherwise tiny placeholders are written.
    """
    rng = random.Random(seed)
    out = Path(out_dir) / episode_id
    out.mkdir(parents=True, exist_ok=True)

    start_ns = 10 * NS  # pretend the device booted 10s ago

    # ---- gripper TCP pose, generated in canonical frame, stored in `world_frame` ----
    pose_rows = []
    n_pose = int(duration_s * hz_pose)
    for i in range(n_pose):
        t = i / hz_pose
        ts = int(start_ns + t * NS)
        # smooth Lissajous path, ~30cm in front, slowly rotating about Y (canonical)
        x = 0.10 * math.sin(2 * math.pi * 0.5 * t)
        y = 1.10 + 0.05 * math.sin(2 * math.pi * 0.7 * t)
        z = -0.30 + 0.08 * math.cos(2 * math.pi * 0.5 * t)
        qx, qy, qz, qw = _axis_angle_quat(0, 1, 0, 0.6 * math.sin(2 * math.pi * 0.3 * t))
        if world_frame == "unity_y_up_lh":
            # inverse of geometry.from_unity (an involution): negate z, qx, qy
            z, qx, qy = -z, -qx, -qy
        pose_rows.append([ts, f"{x:.6f}", f"{y:.6f}", f"{z:.6f}",
                          f"{qx:.6f}", f"{qy:.6f}", f"{qz:.6f}", f"{qw:.6f}", 1])
    _write_csv(out / "gripper_pose.csv",
               ["monotonic_ns", "x", "y", "z", "qx", "qy", "qz", "qw", "tracking_state"], pose_rows)

    # ---- serial streams carry a clean MCU micros counter + a jittered device arrival ns ----
    # device_ns ≈ a*mcu_us + b  (tiny drift); sync.fit_clock recovers (a,b).
    mcu_start_us = 5_000_000
    a_true = 1000.0 * (1 + 1e-5)            # ns per microsecond, ~0.001% fast
    b_true = start_ns - a_true * mcu_start_us

    def serial_times(hz: float):
        n = int(duration_s * hz)
        period_us = 1_000_000.0 / hz
        out_rows = []
        for i in range(n):
            mcu = int(mcu_start_us + i * period_us)
            arrival = int(a_true * mcu + b_true + rng.gauss(0, 150_000))  # ~0.15ms jitter
            out_rows.append((arrival, mcu, i / hz))
        return out_rows

    # gripper width: opens/closes 0.00..0.08 m
    width_rows = []
    for arrival, mcu, t in serial_times(hz_state):
        wn = 0.5 * (1 + math.sin(2 * math.pi * 0.8 * t))  # 0..1
        width = 0.08 * wn
        counts = int(width / 0.08 * 4096)
        trigger = 1 if wn < 0.2 else 0
        width_rows.append([arrival, mcu, f"{width:.6f}", counts, trigger])
    _write_csv(out / "gripper_state.csv",
               ["monotonic_ns", "mcu_micros", "width_m", "raw_counts", "trigger"], width_rows)

    # tactile: force rises as the gripper closes, per-channel offsets + noise
    tac_rows = []
    ch_offset = [rng.uniform(0.0, 0.1) for _ in range(n_tactile)]
    for arrival, mcu, t in serial_times(hz_tactile):
        wn = 0.5 * (1 + math.sin(2 * math.pi * 0.8 * t))
        force = (1 - wn)
        vals = [f"{max(0.0, force + ch_offset[c] + rng.gauss(0, 0.02)):.4f}" for c in range(n_tactile)]
        tac_rows.append([arrival, mcu] + vals)
    _write_csv(out / "tactile.csv",
               ["monotonic_ns", "mcu_micros"] + [f"ch{c}" for c in range(n_tactile)], tac_rows)

    # ---- video frame indices (+ mp4s: real index-coded frames, or placeholders) ----
    def write_video(stream_id: str):
        n = int(duration_s * fps_video)
        rows = []
        for i in range(n):
            ts = int(start_ns + (i / fps_video) * NS)
            rows.append([i, ts, ts])  # monotonic_ns == pts_ns in this fake
        _write_csv(out / f"{stream_id}_frames.csv", ["frame_idx", "monotonic_ns", "pts_ns"], rows)
        if real_video:
            import numpy as np  # lazy: only the real-video path needs numpy/PyAV

            from .video import encode
            W, H = video_size
            # each frame a solid color that encodes its index (survives h264 within tolerance)
            frames = np.empty((n, H, W, 3), dtype=np.uint8)
            for i in range(n):
                frames[i, :, :, 0] = (i * 7) % 256
                frames[i, :, :, 1] = (i * 5) % 256
                frames[i, :, :, 2] = (i * 3) % 256
            encode(out / f"{stream_id}.mp4", frames, fps_video)
        else:
            (out / f"{stream_id}.mp4").write_bytes(b"\x00PLACEHOLDER_MP4")  # not real frames
        return n

    n_ego = write_video("ego")
    n_wrist = write_video("wrist0")

    # ---- a couple of LED sync events ----
    _write_csv(out / "sync_events.csv", ["monotonic_ns", "kind", "id"],
               [[start_ns, "LED_PULSE", 0], [int(start_ns + duration_s * NS) - 1, "LED_PULSE", 1]])

    # ---- head/hands (simplified 26-joint hands) ----
    with (out / "poses.jsonl").open("w") as f:
        for i in range(n_pose):
            t = i / hz_pose
            ts = int(start_ns + t * NS)
            head = {"p": [0.0, 1.6, 0.0], "q": [0.0, 0.0, 0.0, 1.0]}
            hand = [{"p": [0.0, 1.1, -0.3], "q": [0.0, 0.0, 0.0, 1.0]} for _ in range(26)]
            f.write(json.dumps({"monotonic_ns": ts, "head": head, "hand_l": hand, "hand_r": hand}) + "\n")

    # ---- manifest ----
    manifest = {
        "format_version": FORMAT_VERSION,
        "episode_id": episode_id,
        "task_label": "synthetic pick-place demo",
        "conventions": {"length_unit": "m", "time_unit": "ns",
                        "world_frame": "openxr_y_up_rh", "quaternion_order": "xyzw"},
        "device": {
            "model": f"synthetic-{platform}", "platform": platform, "app_version": "0.0.0",
            "capabilities": {"ego_rgb": True, "ego_depth": False, "head_pose": True,
                             "hand_tracking": True, "controller_pose": platform in ("pico", "quest"),
                             "world_frame": world_frame},
        },
        "clock": {"source": "synthetic", "unit": "ns",
                  "start_monotonic_ns": start_ns,
                  "stop_monotonic_ns": int(start_ns + duration_s * NS)},
        "calibration_ref": "synthetic-calibration",
        "streams": [
            {"id": "gripper_pose", "kind": "pose6dof", "file": "gripper_pose.csv",
             "timestamp_field": "monotonic_ns", "rate_hz_nominal": hz_pose,
             "sample_count": n_pose, "frame": "world", "units": "m"},
            {"id": "gripper_state", "kind": "gripper_state", "file": "gripper_state.csv",
             "timestamp_field": "monotonic_ns", "rate_hz_nominal": hz_state,
             "sample_count": len(width_rows), "units": "m",
             "clock_fit": {"a": 1.0, "b": 0.0}, "plugin": "rp2040.encoder"},
            {"id": "tactile0", "kind": "tactile", "file": "tactile.csv",
             "timestamp_field": "monotonic_ns", "rate_hz_nominal": hz_tactile,
             "sample_count": len(tac_rows), "plugin": "rp2040.fsr_array",
             "clock_fit": {"a": 1.0, "b": 0.0},
             "channels": [{"name": f"ch{c}", "unit": "norm", "location": f"pad_{c}"}
                          for c in range(n_tactile)]},
            {"id": "ego", "kind": "video_rgb", "file": "ego.mp4", "index_file": "ego_frames.csv",
             "timestamp_field": "monotonic_ns", "rate_hz_nominal": fps_video,
             "sample_count": n_ego, "codec": "h264",
             "frame_size": {"width": 1280, "height": 960}},
            {"id": "wrist0", "kind": "video_rgb", "file": "wrist0.mp4", "index_file": "wrist0_frames.csv",
             "timestamp_field": "monotonic_ns", "rate_hz_nominal": fps_video,
             "sample_count": n_wrist, "codec": "h264",
             "frame_size": {"width": 1280, "height": 720}},
            {"id": "sync", "kind": "sync_events", "file": "sync_events.csv",
             "timestamp_field": "monotonic_ns", "sample_count": 2},
        ],
        "status": "finalized",
    }
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2))
    return out
