"""Typed mirror of the episode manifest (schema/capture_manifest.schema.json).

Kept in sync with the JSON Schema by hand for now; a test should validate one against the
other once example episodes exist.
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

# stream kinds — mirror the JSON Schema enum
VIDEO_RGB = "video_rgb"
VIDEO_DEPTH = "video_depth"
POSE6DOF = "pose6dof"
SKELETON = "skeleton"
GRIPPER_STATE = "gripper_state"
TACTILE = "tactile"
SYNC_EVENTS = "sync_events"


@dataclass
class ClockFit:
    """device_time ≈ a * source_time + b (Tier-1 alignment)."""
    a: float = 1.0
    b: float = 0.0
    residual_ms: float | None = None
    method: str | None = None


@dataclass
class Channel:
    name: str
    unit: str | None = None
    location: str | None = None


@dataclass
class Stream:
    id: str
    kind: str
    file: str
    timestamp_field: str = "monotonic_ns"
    index_file: str | None = None
    rate_hz_nominal: float | None = None
    sample_count: int = 0
    dropped_count: int = 0
    units: str | None = None
    frame: str | None = None
    codec: str | None = None
    channels: list[Channel] = field(default_factory=list)
    latency_offset_ns: int = 0
    clock_fit: ClockFit | None = None
    plugin: str | None = None
    extra: dict = field(default_factory=dict)

    @classmethod
    def from_dict(cls, d: dict) -> "Stream":
        d = dict(d)
        chans = [Channel(**c) for c in d.pop("channels", [])]
        fit = d.pop("clock_fit", None)
        known = {f for f in cls.__dataclass_fields__ if f not in ("channels", "clock_fit", "extra")}
        extra = {k: v for k, v in d.items() if k not in known}
        kw = {k: v for k, v in d.items() if k in known}
        return cls(channels=chans, clock_fit=ClockFit(**fit) if fit else None, extra=extra, **kw)


@dataclass
class Manifest:
    format_version: str
    episode_id: str
    device: dict
    clock: dict
    streams: list[Stream]
    status: str
    calibration_ref: str | None = None
    task_label: str | None = None
    operator: str | None = None
    sync: dict | None = None
    raw: dict = field(default_factory=dict)

    @classmethod
    def load(cls, episode_dir: str | Path) -> "Manifest":
        episode_dir = Path(episode_dir)
        raw = json.loads((episode_dir / "manifest.json").read_text())
        streams = [Stream.from_dict(s) for s in raw["streams"]]
        return cls(
            format_version=raw["format_version"],
            episode_id=raw["episode_id"],
            device=raw["device"],
            clock=raw["clock"],
            streams=streams,
            status=raw["status"],
            calibration_ref=raw.get("calibration_ref"),
            task_label=raw.get("task_label"),
            operator=raw.get("operator"),
            sync=raw.get("sync"),
            raw=raw,
        )

    def stream(self, stream_id: str) -> Stream | None:
        return next((s for s in self.streams if s.id == stream_id), None)

    def streams_of_kind(self, kind: str) -> list[Stream]:
        return [s for s in self.streams if s.kind == kind]
