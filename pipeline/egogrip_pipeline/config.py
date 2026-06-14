"""Load + validate the capture config (schema/capture_config.schema.json).

The capture config is the INPUT that drives recording: a typed list of sensor instances the
on-device app opens. The OUTPUT manifest (schema/capture_manifest.schema.json) describes what was
actually recorded. This module loads the config, validates it (full jsonschema if available, else
structural checks), and exposes helpers used by tests/tooling — notably pose-offset resolution and
a config<->manifest consistency check. See docs/CAPTURE_CONFIG.md.
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from . import geometry as G

SENSOR_TYPES = ("xr_pose", "ego_camera", "uvc_camera", "rp2040_serial")
CAMERA_TYPES = ("ego_camera", "uvc_camera")


def _schema_path() -> Path:
    # repo_root/schema/... ; package lives at repo_root/pipeline/egogrip_pipeline
    return Path(__file__).resolve().parents[2] / "schema" / "capture_config.schema.json"


@dataclass
class Sensor:
    id: str
    type: str
    enabled: bool = True
    rate_hz: float | None = None
    stream_id: str | None = None
    params: dict = field(default_factory=dict)

    @property
    def out_stream_id(self) -> str:
        """Output stream id, defaulting to the sensor id."""
        return self.stream_id or self.id

    @classmethod
    def from_dict(cls, d: dict) -> "Sensor":
        known = {"id", "type", "enabled", "rate_hz", "stream_id"}
        params = {k: v for k, v in d.items() if k not in known}
        return cls(id=d["id"], type=d["type"], enabled=d.get("enabled", True),
                   rate_hz=d.get("rate_hz"), stream_id=d.get("stream_id"), params=params)


@dataclass
class CaptureConfig:
    format_version: str
    sensors: list[Sensor]
    operator: str | None = None
    task_label: str | None = None
    calibration_ref: str | None = None
    raw: dict = field(default_factory=dict)

    @classmethod
    def load(cls, path: str | Path) -> "CaptureConfig":
        raw = json.loads(Path(path).read_text())
        return cls(
            format_version=raw["format_version"],
            sensors=[Sensor.from_dict(s) for s in raw["sensors"]],
            operator=raw.get("operator"),
            task_label=raw.get("task_label"),
            calibration_ref=raw.get("calibration_ref"),
            raw=raw,
        )

    def enabled_sensors(self) -> list[Sensor]:
        return [s for s in self.sensors if s.enabled]

    def sensor(self, sensor_id: str) -> Sensor | None:
        return next((s for s in self.sensors if s.id == sensor_id), None)

    def of_type(self, t: str) -> list[Sensor]:
        return [s for s in self.sensors if s.type == t]


def validate_config(path: str | Path) -> list[str]:
    """Return a list of problems; empty == valid. Full schema check if jsonschema is installed,
    plus structural cross-checks (duplicate ids, known types) the schema can't easily express."""
    path = Path(path)
    if not path.exists():
        return [f"missing config: {path}"]
    try:
        raw = json.loads(path.read_text())
    except json.JSONDecodeError as e:
        return [f"invalid JSON: {e}"]

    problems: list[str] = []
    schema_file = _schema_path()
    try:
        import jsonschema

        if schema_file.exists():
            schema = json.loads(schema_file.read_text())
            for err in sorted(jsonschema.Draft202012Validator(schema).iter_errors(raw),
                              key=lambda e: list(e.path)):
                loc = "/".join(str(p) for p in err.path) or "<root>"
                problems.append(f"schema: {loc}: {err.message}")
        else:
            problems.append(f"schema not found at {schema_file}")
    except ImportError:
        for key in ("format_version", "sensors"):
            if key not in raw:
                problems.append(f"missing required key: {key}")

    ids = [s.get("id") for s in raw.get("sensors", [])]
    dupes = sorted({i for i in ids if ids.count(i) > 1})
    if dupes:
        problems.append(f"duplicate sensor ids: {dupes}")
    for s in raw.get("sensors", []):
        if s.get("type") not in SENSOR_TYPES:
            problems.append(f"sensor {s.get('id')}: unknown type {s.get('type')!r}")
    return problems


def resolve_pose_offset(offset: dict | None) -> tuple[np.ndarray, np.ndarray]:
    """A pose_offset dict -> (translation(3,), quat(4,) xyzw). Defaults to identity.

    Rotation comes from exactly one of rotation_quat_xyzw / rotation_euler_deg. `from_calibration`
    is a contract for the app to fill T_ctrl_gripper from calibration.json; here it yields identity.
    """
    t = np.zeros(3)
    q = np.array([0.0, 0.0, 0.0, 1.0])
    if not offset:
        return t, q
    if "translation_m" in offset:
        t = np.asarray(offset["translation_m"], dtype=np.float64).reshape(3)
    if "rotation_quat_xyzw" in offset:
        q = G.normalize(np.asarray(offset["rotation_quat_xyzw"], dtype=np.float64)).reshape(4)
    elif "rotation_euler_deg" in offset:
        q = G.euler_deg_to_quat(offset["rotation_euler_deg"])
    return t, q


def apply_pose_offset(pos, quat, offset: dict | None) -> tuple[np.ndarray, np.ndarray]:
    """Compose controller pose(s) with a config pose_offset -> gripper TCP pose(s). The Unity
    recorder applies the same transform at capture time (docs/CAPTURE_CONFIG.md)."""
    t, q = resolve_pose_offset(offset)
    return G.compose_pose_offset(pos, quat, t, q)


def check_against_manifest(config: CaptureConfig, manifest) -> list[str]:
    """Each enabled, stream-producing sensor's stream id should appear in the manifest streams."""
    problems: list[str] = []
    have = {s.id for s in manifest.streams}
    for s in config.enabled_sensors():
        if s.type == "rp2040_serial":
            wanted = [st.get("stream_id") for st in s.params.get("streams", [])]
        else:
            wanted = [s.out_stream_id]
        for w in wanted:
            if w and w not in have:
                problems.append(f"sensor {s.id}: stream '{w}' not found in manifest")
    return problems
