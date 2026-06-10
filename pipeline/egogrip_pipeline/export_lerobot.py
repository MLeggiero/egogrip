"""Export an aligned episode to a trainable dataset.

Default output is a dependency-free **neutral** dataset (meta.json + arrays.npz) so the whole
pipeline runs with just numpy. If `lerobot` is installed and --target lerobot is passed, a
LeRobot v2 dataset is written instead (video transcode is the remaining Phase-6 TODO).

Feature mapping: docs/DATA_FORMAT.md §2. Device-agnostic: reads only the manifest.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from . import geometry as G
from .schema import Manifest
from .sync import AlignedEpisode, build_timeline


def build_state(ep: AlignedEpisode, rotation: str = "rot6d") -> tuple[np.ndarray, list[str]]:
    """observation.state = TCP position ⊕ rotation ⊕ gripper width."""
    if rotation == "rot6d":
        rot = G.quat_to_rot6d(ep.gripper_quat)
        rot_names = [f"rot6d_{i}" for i in range(6)]
    elif rotation == "quat":
        rot = ep.gripper_quat
        rot_names = ["qx", "qy", "qz", "qw"]
    else:
        raise ValueError(f"unknown rotation repr: {rotation}")
    state = np.concatenate([ep.gripper_pos, rot, ep.width_m[:, None]], axis=1)
    names = ["x", "y", "z", *rot_names, "width"]
    return state.astype(np.float32), names


def build_action(ep: AlignedEpisode, mode: str = "absolute", rotation: str = "rot6d") -> np.ndarray:
    """action: where the TCP+width go on the next step (absolute target or delta)."""
    if mode == "absolute":
        state, _ = build_state(ep, rotation)
        act = np.roll(state, -1, axis=0)
        act[-1] = state[-1]  # last step holds
        return act
    if mode == "delta":
        pos = ep.gripper_pos
        dpos = np.zeros_like(pos)
        dpos[:-1] = pos[1:] - pos[:-1]
        R = G.quat_to_matrix(ep.gripper_quat)  # (T,3,3)
        Rrel = np.zeros_like(R)
        Rrel[:-1] = np.einsum("tij,tjk->tik", np.transpose(R[:-1], (0, 2, 1)), R[1:])
        Rrel[-1] = np.eye(3)
        rot6d = G.matrix_to_rot6d(Rrel)
        w = ep.width_m
        dw = np.zeros_like(w)
        dw[:-1] = w[1:] - w[:-1]
        return np.concatenate([dpos, rot6d, dw[:, None]], axis=1).astype(np.float32)
    raise ValueError(f"unknown action mode: {mode}")


def export_episode(
    episode_dir: str | Path,
    out_dir: str | Path,
    *,
    fps: float = 30.0,
    action: str = "absolute",
    rotation: str = "rot6d",
    include_tactile: bool = True,
    target: str = "neutral",
    video: bool = False,
    repo_id: str | None = None,
    task: str | None = None,
) -> Path:
    """Convert one raw episode into a dataset. Returns the output dir (per-episode for neutral,
    the shared dataset root for lerobot)."""
    episode_dir = Path(episode_dir)
    manifest = Manifest.load(episode_dir)
    _check_format(manifest)

    ep = build_timeline(manifest, episode_dir, fps=fps)
    state, state_names = build_state(ep, rotation)
    act = build_action(ep, action, rotation)
    tactile = ep.tactile if (include_tactile and ep.tactile is not None) else None
    task = task or manifest.task_label or "egogrip demonstration"

    if target == "lerobot":
        root = Path(out_dir)
        return _write_lerobot(root, manifest, episode_dir, ep, state, state_names, act,
                              tactile, task, repo_id)
    out = Path(out_dir) / manifest.episode_id
    return _write_neutral(out, manifest, episode_dir, ep, state, state_names, act, tactile,
                          action, rotation, video)


def _decode_camera_frames(episode_dir: Path, manifest: Manifest, ep) -> dict[str, np.ndarray]:
    """Decode each camera's source video at the aligned frame indices -> (T,H,W,3) per camera."""
    from .video import decode_indices

    frames = {}
    for vid, idx in ep.video_frame_idx.items():
        src = episode_dir / manifest.stream(vid).file
        frames[vid] = decode_indices(src, idx)
    return frames


def _write_neutral(out, manifest, episode_dir, ep, state, state_names, act, tactile,
                   action, rotation, video) -> Path:
    out.mkdir(parents=True, exist_ok=True)
    features = {
        "observation.state": {"shape": [state.shape[1]], "names": state_names},
        "action": {"shape": [act.shape[1]], "mode": action, "rotation": rotation},
        "timestamp": {"shape": [1]},
    }
    arrays = {
        "timestamp": ep.t_seconds.astype(np.float32),
        "observation.state": state,
        "action": act,
        "gripper_track": ep.gripper_track.astype(np.float32),
    }
    if tactile is not None:
        arrays["observation.tactile"] = tactile.astype(np.float32)
        features["observation.tactile"] = {"shape": [tactile.shape[1]], "names": ep.tactile_channels}

    if video:  # transcode each camera onto the aligned grid -> real mp4s
        from .video import encode

        cam = _decode_camera_frames(episode_dir, manifest, ep)
        for vid, fr in cam.items():
            encode(out / f"{vid}.mp4", fr, ep.fps)
            features[f"observation.images.{vid}"] = {"video": f"{vid}.mp4", "shape": list(fr.shape[1:])}
    else:       # keep frame-index references only (no transcode)
        for vid, idx in ep.video_frame_idx.items():
            arrays[f"frame_idx.{vid}"] = idx.astype(np.int64)
            features[f"observation.images.{vid}"] = {
                "source_video": manifest.stream(vid).file, "frame_index_ref": True}

    np.savez_compressed(out / "arrays.npz", **arrays)
    meta = {
        "episode_id": manifest.episode_id,
        "fps": ep.fps,
        "num_frames": int(len(ep.t_seconds)),
        "max_pairwise_skew_ms": round(ep.max_pairwise_skew_ms, 3),
        "device": manifest.device,
        "features": features,
        "source_clock_fits": {
            s.id: vars(s.clock_fit) for s in manifest.streams if s.clock_fit is not None},
    }
    (out / "meta.json").write_text(json.dumps(meta, indent=2))
    return out


def _import_lerobot():
    for mod in ("lerobot.datasets.lerobot_dataset", "lerobot.common.datasets.lerobot_dataset"):
        try:
            return __import__(mod, fromlist=["LeRobotDataset"]).LeRobotDataset
        except ImportError:
            continue
    raise SystemExit("lerobot not installed. `pip install lerobot`, or use --target neutral.")


def _write_lerobot(root: Path, manifest, episode_dir, ep, state, state_names, act, tactile,
                   task, repo_id) -> Path:
    """Append this episode to a LeRobot v2 dataset at `root` (create on first episode)."""
    LeRobotDataset = _import_lerobot()
    repo_id = repo_id or f"egogrip/{root.name}"
    cam = _decode_camera_frames(episode_dir, manifest, ep)
    T = len(ep.t_seconds)

    features = {
        "observation.state": {"dtype": "float32", "shape": (state.shape[1],), "names": state_names},
        "action": {"dtype": "float32", "shape": (act.shape[1],), "names": state_names},
    }
    if tactile is not None:
        features["observation.tactile"] = {"dtype": "float32", "shape": (tactile.shape[1],),
                                           "names": ep.tactile_channels}
    for vid, fr in cam.items():
        features[f"observation.images.{vid}"] = {
            "dtype": "video", "shape": tuple(fr.shape[1:]), "names": ["height", "width", "channel"]}

    if (root / "meta" / "info.json").exists():
        ds = LeRobotDataset(repo_id, root=root)
    else:
        ds = LeRobotDataset.create(repo_id, fps=int(round(ep.fps)), features=features,
                                   root=root, use_videos=True, robot_type="egogrip_umi")
    for t in range(T):
        frame = {"observation.state": state[t].astype(np.float32),
                 "action": act[t].astype(np.float32), "task": task}
        if tactile is not None:
            frame["observation.tactile"] = tactile[t].astype(np.float32)
        for vid, fr in cam.items():
            frame[f"observation.images.{vid}"] = fr[t]
        ds.add_frame(frame)
    ds.save_episode()
    ds.finalize()
    return root


def _check_format(manifest: Manifest) -> None:
    if manifest.format_version.split(".")[0] != "0":
        raise ValueError(f"Unsupported manifest major version: {manifest.format_version}")
    if manifest.status not in ("finalized", "repaired"):
        raise ValueError(f"Episode not finalized (status={manifest.status}); repair first.")


def _iter_episodes(path: Path):
    if (path / "manifest.json").exists():
        yield path
    else:
        yield from sorted(p.parent for p in path.glob("*/manifest.json"))


def main() -> None:
    ap = argparse.ArgumentParser(description="Export egogrip episodes to a dataset.")
    ap.add_argument("path", help="An episode folder, or a folder of episodes.")
    ap.add_argument("--out", required=True, help="Output dataset dir.")
    ap.add_argument("--fps", type=float, default=30.0)
    ap.add_argument("--action", choices=["absolute", "delta"], default="absolute")
    ap.add_argument("--rotation", choices=["rot6d", "quat"], default="rot6d")
    ap.add_argument("--target", choices=["neutral", "lerobot"], default="neutral")
    ap.add_argument("--no-tactile", action="store_true")
    args = ap.parse_args()

    for ep in _iter_episodes(Path(args.path)):
        out = export_episode(
            ep, args.out, fps=args.fps, action=args.action, rotation=args.rotation,
            include_tactile=not args.no_tactile, target=args.target,
        )
        print(f"[egogrip] {ep.name} -> {out}")


if __name__ == "__main__":
    main()
