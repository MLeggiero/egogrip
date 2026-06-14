"""Load calibration.json and convert raw gripper encoder counts -> width in metres.

Firmware/app log raw + multi-turn-accumulated AS5600 counts (`delta_counts`, relative to the
start-closed tare); this turns them into physical width with a linear `counts_per_mm`, so episodes
re-calibrate offline without re-recording. See docs/CALIBRATION.md.
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np


@dataclass
class WidthCalibration:
    counts_per_mm: float
    width_closed_m: float = 0.0
    direction: int = 1
    count_zero: int = 0

    def counts_to_m(self, delta_counts) -> np.ndarray:
        """delta_counts (relative to the closed tare) -> jaw width in metres."""
        d = np.asarray(delta_counts, dtype=np.float64)
        return self.width_closed_m + (d * self.direction) / self.counts_per_mm / 1000.0


@dataclass
class Calibration:
    width: WidthCalibration | None = None
    raw: dict = field(default_factory=dict)

    @classmethod
    def from_dict(cls, d: dict) -> "Calibration":
        w = d.get("gripper_width") or {}
        wc = None
        if "counts_per_mm" in w:
            wc = WidthCalibration(
                counts_per_mm=float(w["counts_per_mm"]),
                width_closed_m=float(w.get("width_closed_m", 0.0)),
                direction=int(w.get("direction", 1)),
                count_zero=int(w.get("count_zero", 0)),
            )
        return cls(width=wc, raw=d)

    @classmethod
    def load(cls, path: str | Path) -> "Calibration":
        return cls.from_dict(json.loads(Path(path).read_text()))

    @classmethod
    def for_episode(cls, episode_dir, manifest) -> "Calibration | None":
        """Resolve manifest.calibration_ref next to the episode, then as a literal path."""
        ref = getattr(manifest, "calibration_ref", None)
        if not ref:
            return None
        for cand in (Path(episode_dir) / ref, Path(ref)):
            if cand.is_file():
                return cls.load(cand)
        return None


def calibrate_width(closed_delta: float, open_delta: float, open_width_mm: float,
                    closed_width_mm: float = 0.0) -> WidthCalibration:
    """Two-point fit -> WidthCalibration. `closed_delta`/`open_delta` are delta_counts at two known
    jaw widths (mm); the encoder's count direction is captured in `direction`."""
    dc = open_delta - closed_delta
    dmm = open_width_mm - closed_width_mm
    if dmm == 0:
        raise ValueError("the two calibration widths must differ")
    return WidthCalibration(
        counts_per_mm=abs(dc / dmm),
        width_closed_m=closed_width_mm / 1000.0,
        direction=1 if dc / dmm >= 0 else -1,
    )
