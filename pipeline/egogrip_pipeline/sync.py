"""Time alignment: per-stream clock correction + resampling onto one timeline.

Strategy is documented in docs/SYNC.md:
  Tier-0  one monotonic device clock (already in the raw files)
  Tier-1  fit device ≈ a*source + b per stream (default); apply latency offsets
  Tier-2  use LED/serial sync anchors to pin camera time to serial time

This module is a scaffold: interfaces + outline, math TODO.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from .schema import Manifest, Stream


@dataclass
class AlignedTimeline:
    """A common time grid plus, per stream, the indices/values sampled onto it."""
    fps: float
    t_seconds: np.ndarray            # shape (T,), seconds from episode start
    # per stream id -> resampled values (videos store frame indices; signals store arrays)
    streams: dict[str, np.ndarray]


def apply_clock_fit(source_ns: np.ndarray, stream: Stream) -> np.ndarray:
    """Map a stream's native timestamps onto the shared device clock.

    Uses stream.clock_fit (a, b) if present, else identity, then subtracts the measured
    fixed latency offset. Returns device-time nanoseconds.
    """
    if stream.clock_fit is not None:
        device_ns = stream.clock_fit.a * source_ns.astype(np.float64) + stream.clock_fit.b
    else:
        device_ns = source_ns.astype(np.float64)
    return device_ns - float(stream.latency_offset_ns)


def detect_led_anchors(video_path: str, roi: tuple[int, int, int, int]) -> np.ndarray:
    """Tier-2: find LED-flash frame times (brightness spikes in an ROI).

    Returns device-time ns of detected flashes, to be matched against SYNC serial events.
    TODO: implement (decode frames, mean brightness over ROI, peak-pick).
    """
    raise NotImplementedError("LED anchor detection — Phase 5")


def fit_clock_from_anchors(video_flash_ns: np.ndarray, serial_event_ns: np.ndarray) -> tuple[float, float]:
    """Least-squares fit camera_time -> serial_time from matched anchor pairs.

    Returns (a, b). TODO: pair anchors by id/order, np.polyfit degree 1.
    """
    raise NotImplementedError("anchor clock fit — Phase 5")


def build_timeline(manifest: Manifest, fps: float = 30.0) -> AlignedTimeline:
    """Resample every stream onto a uniform `fps` grid spanning the episode.

    Outline:
      1. Per stream, load native timestamps; map to device time via apply_clock_fit.
      2. Episode span = [max(first), min(last)] across required streams.
      3. Grid = arange(span, step=1/fps).
      4. Videos: nearest-frame index per grid point (within tolerance).
         Signals (pose/width/tactile): interpolate (slerp for quats, linear for the rest).
      5. Record max_pairwise_skew_ms into the manifest's sync summary.
    TODO: implement loaders + interpolation.
    """
    raise NotImplementedError("timeline construction — Phase 5/6")
