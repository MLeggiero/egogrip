"""Validate an episode against the format contract (schema/capture_manifest.schema.json).

If `jsonschema` is installed, does full schema validation; otherwise falls back to structural
checks. Either way, verifies the declared stream files actually exist. This is the gate a new
device must pass to be "supported" (docs/PORTABILITY.md).
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path


def _schema_path() -> Path:
    # repo_root/schema/... ; package lives at repo_root/pipeline/egogrip_pipeline
    return Path(__file__).resolve().parents[2] / "schema" / "capture_manifest.schema.json"


def validate_episode(episode_dir: str | Path) -> list[str]:
    """Return a list of problems; empty list == valid."""
    episode_dir = Path(episode_dir)
    problems: list[str] = []
    mpath = episode_dir / "manifest.json"
    if not mpath.exists():
        return [f"missing manifest.json in {episode_dir}"]
    manifest = json.loads(mpath.read_text())

    schema_file = _schema_path()
    try:
        import jsonschema

        if schema_file.exists():
            schema = json.loads(schema_file.read_text())
            for err in sorted(jsonschema.Draft202012Validator(schema).iter_errors(manifest),
                              key=lambda e: e.path):
                loc = "/".join(str(p) for p in err.path) or "<root>"
                problems.append(f"schema: {loc}: {err.message}")
        else:
            problems.append(f"schema file not found at {schema_file} (skipped schema check)")
    except ImportError:
        for key in ("format_version", "episode_id", "device", "clock", "streams", "status"):
            if key not in manifest:
                problems.append(f"missing required manifest key: {key}")

    # files referenced by streams must exist
    for s in manifest.get("streams", []):
        for ref in ("file", "index_file"):
            if ref in s and not (episode_dir / s[ref]).exists():
                problems.append(f"stream {s.get('id', '?')}: {ref} not found: {s[ref]}")
    return problems


def main() -> None:
    ap = argparse.ArgumentParser(description="Validate egogrip episode(s) against the format.")
    ap.add_argument("path", help="An episode folder, or a folder of episodes.")
    args = ap.parse_args()
    root = Path(args.path)
    episodes = [root] if (root / "manifest.json").exists() else \
        sorted(p.parent for p in root.glob("*/manifest.json"))
    if not episodes:
        raise SystemExit(f"no episodes found under {root}")
    bad = 0
    for ep in episodes:
        problems = validate_episode(ep)
        if problems:
            bad += 1
            print(f"✗ {ep.name}")
            for p in problems:
                print(f"    {p}")
        else:
            print(f"✓ {ep.name}")
    raise SystemExit(1 if bad else 0)


if __name__ == "__main__":
    main()
