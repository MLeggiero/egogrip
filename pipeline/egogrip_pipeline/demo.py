"""End-to-end demo: synthesize an episode -> validate -> align -> export. No hardware needed.

    python -m egogrip_pipeline.demo            # writes to ./_demo_out
    egogrip-demo --out /tmp/egogrip_demo
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from .export_lerobot import export_episode
from .synthetic import generate_episode
from .validate import validate_episode


def run(out_dir: str | Path = "_demo_out", fps: float = 30.0) -> Path:
    out_dir = Path(out_dir)
    raw_root = out_dir / "raw"
    ds_root = out_dir / "dataset"

    ep = generate_episode(raw_root, seed=0)
    print(f"[1/3] generated synthetic episode: {ep}")

    problems = validate_episode(ep)
    if problems:
        print("[2/3] VALIDATION FAILED:")
        for p in problems:
            print(f"      {p}")
        raise SystemExit(1)
    print("[2/3] validation: ✓ conforms to the format contract")

    written = export_episode(ep, ds_root, fps=fps, action="absolute", rotation="rot6d")
    meta = json.loads((written / "meta.json").read_text())
    print(f"[3/3] exported neutral dataset: {written}")
    print(f"      frames={meta['num_frames']} @ {meta['fps']}Hz | "
          f"state_dim={meta['features']['observation.state']['shape'][0]} | "
          f"action={meta['features']['action']['mode']} | "
          f"skew={meta['max_pairwise_skew_ms']}ms")
    fits = meta.get("source_clock_fits", {})
    for sid, fit in fits.items():
        print(f"      clock-fit[{sid}]: a={fit['a']:.6f} residual={fit.get('residual_ms')}ms")
    return written


def main() -> None:
    ap = argparse.ArgumentParser(description="egogrip end-to-end pipeline demo.")
    ap.add_argument("--out", default="_demo_out")
    ap.add_argument("--fps", type=float, default=30.0)
    args = ap.parse_args()
    run(args.out, args.fps)


if __name__ == "__main__":
    main()
