"""Video decode/encode for the export step (PyAV).

The hard part of LeRobot export is turning each source camera video into frames sampled on the
aligned timeline. That logic lives here and is independently testable; the LeRobot writer and
the neutral video output both consume it.

PyAV (`av`) is an optional dependency: `pip install egogrip-pipeline[video]`.
"""
from __future__ import annotations

from pathlib import Path

import numpy as np


def _require_av():
    try:
        import av  # noqa
        return av
    except ImportError as e:  # pragma: no cover
        raise SystemExit("PyAV not installed. `pip install av` (or extras [video]).") from e


def probe(path: str | Path) -> dict:
    """Return basic stream info: width, height, fps, count (decoded)."""
    av = _require_av()
    with av.open(str(path)) as c:
        s = c.streams.video[0]
        fps = float(s.average_rate) if s.average_rate else None
        return {"width": s.codec_context.width, "height": s.codec_context.height, "fps": fps}


def decode_indices(path: str | Path, indices: np.ndarray) -> np.ndarray:
    """Decode the frames at `indices` -> (T, H, W, 3) uint8 (RGB).

    `indices` may repeat (upsampling) and may contain -1 for gaps (no nearby frame); gaps and
    out-of-range indices become black frames. Decodes sequentially from the start (fine for the
    short clips egogrip records; add seeking if episodes get long).
    """
    av = _require_av()
    indices = np.asarray(indices)
    needed = sorted({int(i) for i in indices if i >= 0})
    frames: dict[int, np.ndarray] = {}
    H = W = None
    if needed:
        with av.open(str(path)) as c:
            stream = c.streams.video[0]
            target = set(needed)
            for i, frame in enumerate(c.decode(stream)):
                if i in target:
                    arr = frame.to_ndarray(format="rgb24")
                    frames[i] = arr
                    H, W = arr.shape[:2]
                    target.discard(i)
                    if not target:
                        break
    if H is None:  # nothing decoded; fall back to a probe for size
        info = probe(path)
        H, W = info["height"], info["width"]
    black = np.zeros((H, W, 3), dtype=np.uint8)
    return np.stack([frames.get(int(i), black) for i in indices], axis=0)


def encode(path: str | Path, frames: np.ndarray, fps: float, codec: str = "libx264") -> Path:
    """Encode (T,H,W,3) uint8 RGB frames to an mp4 at `fps`. Returns the path."""
    av = _require_av()
    path = Path(path)
    frames = np.asarray(frames, dtype=np.uint8)
    T, H, W, _ = frames.shape
    with av.open(str(path), mode="w") as container:
        try:
            stream = container.add_stream(codec, rate=int(round(fps)))
        except Exception:  # pragma: no cover - codec availability varies
            stream = container.add_stream("mpeg4", rate=int(round(fps)))
        stream.width, stream.height, stream.pix_fmt = W, H, "yuv420p"
        for t in range(T):
            vf = av.VideoFrame.from_ndarray(np.ascontiguousarray(frames[t]), format="rgb24")
            for pkt in stream.encode(vf):
                container.mux(pkt)
        for pkt in stream.encode():
            container.mux(pkt)
    return path
