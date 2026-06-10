"""Guard: episodes the native Android app produces (serial + camera + IMU, NO pose) must always
validate against the format contract. Mirrors EpisodeWriter's manifest shape."""
import json
from pathlib import Path

from egogrip_pipeline.validate import validate_episode


def _write_native_episode(d: Path) -> Path:
    d.mkdir(parents=True, exist_ok=True)
    (d / "gripper_state.csv").write_text("monotonic_ns,mcu_micros,width_m,raw_counts,trigger\n1,5,0.0,0,1\n")
    (d / "tactile.csv").write_text("monotonic_ns,mcu_micros,ch0,ch1\n1,5,10,20\n")
    (d / "imu.csv").write_text("monotonic_ns,sensor_ns,qx,qy,qz,qw\n1,2,0,0,0,1\n")
    (d / "wrist0.mp4").write_bytes(b"\x00")
    (d / "wrist0_frames.csv").write_text("frame_idx,monotonic_ns,pts_ns\n0,1,1\n")
    manifest = {
        "format_version": "0.1.0", "episode_id": d.name, "task_label": "native test",
        "conventions": {"length_unit": "m", "time_unit": "ns",
                        "world_frame": "openxr_y_up_rh", "quaternion_order": "xyzw"},
        "device": {"model": "PICO", "platform": "pico", "os": "Android 14", "app_version": "0.1.0",
                   "capabilities": {"ego_rgb": False, "ego_depth": False, "head_pose": False,
                                    "hand_tracking": False, "controller_pose": False,
                                    "world_frame": "openxr_y_up_rh"}},
        "clock": {"source": "SystemClock.elapsedRealtimeNanos", "unit": "ns",
                  "start_monotonic_ns": 1, "stop_monotonic_ns": 2},
        "streams": [
            {"id": "gripper_state", "kind": "gripper_state", "file": "gripper_state.csv",
             "timestamp_field": "monotonic_ns", "sample_count": 1, "units": "m"},
            {"id": "tactile0", "kind": "tactile", "file": "tactile.csv",
             "timestamp_field": "monotonic_ns", "sample_count": 1,
             "channels": [{"name": "ch0"}, {"name": "ch1"}]},
            {"id": "wrist0", "kind": "video_rgb", "file": "wrist0.mp4", "index_file": "wrist0_frames.csv",
             "timestamp_field": "monotonic_ns", "sample_count": 1, "codec": "h264",
             "frame_size": {"width": 1280, "height": 720}},
            {"id": "imu", "kind": "imu", "file": "imu.csv",
             "timestamp_field": "monotonic_ns", "sample_count": 1, "frame": "head"},
        ],
        "status": "finalized",
    }
    (d / "manifest.json").write_text(json.dumps(manifest))
    return d


def test_native_episode_validates(tmp_path):
    ep = _write_native_episode(tmp_path / "t_native")
    problems = validate_episode(ep)
    assert problems == [], f"native episode should validate: {problems}"
