"""Export an aligned episode to a LeRobot v2 dataset.

Feature mapping is documented in docs/DATA_FORMAT.md §2. This is a scaffold: the structure,
CLI, and feature plan are here; the LeRobot writes are TODO until the LeRobot version is
pinned (pyproject.toml).
"""
from __future__ import annotations

import argparse
from pathlib import Path

from .schema import Manifest
from .sync import build_timeline

# Default LeRobot feature plan (see docs/DATA_FORMAT.md).
#   observation.images.ego     <- ego.mp4
#   observation.images.wrist   <- wrist0.mp4
#   observation.state          <- TCP pose (xyz + 6d-rot) ⊕ gripper width
#   observation.tactile        <- tactile channels (optional)
#   action                     <- next-step TCP pose ⊕ width (absolute|delta)


def export_episode(
    episode_dir: str | Path,
    out_dir: str | Path,
    fps: float = 30.0,
    action: str = "absolute",          # or "delta"
    rotation: str = "rot6d",           # or "quat"
    include_tactile: bool = True,
    include_hands: bool = False,
) -> None:
    """Convert one raw episode folder into (or append to) a LeRobot dataset."""
    manifest = Manifest.load(episode_dir)
    _check_format(manifest)

    timeline = build_timeline(manifest, fps=fps)  # noqa: F841  (used once implemented)

    # TODO (Phase 6):
    #   1. Build observation.state: pose (apply rotation repr) ⊕ width, per grid point.
    #   2. Build action from the *next* state (absolute or delta).
    #   3. Attach observation.images.* by transcoding/remuxing the per-camera mp4 to the grid.
    #   4. Attach observation.tactile / observation.hands if requested + present.
    #   5. Write via LeRobotDataset (create or append), then consolidate stats.
    raise NotImplementedError(
        "LeRobot writing — Phase 6. Timeline + feature plan are in place; "
        "pin the lerobot dependency in pyproject.toml to implement."
    )


def _check_format(manifest: Manifest) -> None:
    major = manifest.format_version.split(".")[0]
    if major != "0":
        raise ValueError(f"Unsupported manifest major version: {manifest.format_version}")
    if manifest.status not in ("finalized", "repaired"):
        raise ValueError(f"Episode not finalized (status={manifest.status}); run repair first.")


def _iter_episodes(path: Path):
    if (path / "manifest.json").exists():
        yield path
    else:
        yield from (p.parent for p in path.glob("*/manifest.json"))


def main() -> None:
    ap = argparse.ArgumentParser(description="Export egogrip episodes to a LeRobot dataset.")
    ap.add_argument("path", help="An episode folder, or a folder of episodes.")
    ap.add_argument("--out", required=True, help="Output LeRobot dataset dir.")
    ap.add_argument("--fps", type=float, default=30.0)
    ap.add_argument("--action", choices=["absolute", "delta"], default="absolute")
    ap.add_argument("--rotation", choices=["rot6d", "quat"], default="rot6d")
    ap.add_argument("--no-tactile", action="store_true")
    ap.add_argument("--hands", action="store_true")
    args = ap.parse_args()

    for ep in _iter_episodes(Path(args.path)):
        print(f"[egogrip] exporting {ep} -> {args.out}")
        export_episode(
            ep, args.out, fps=args.fps, action=args.action, rotation=args.rotation,
            include_tactile=not args.no_tactile, include_hands=args.hands,
        )


if __name__ == "__main__":
    main()
