# Hardware (mock gripper rig)

CAD, BOM, and build notes for the UMI-style hand-held mock gripper. Design rationale and
calibration live in [../docs/HARDWARE.md](../docs/HARDWARE.md).

> Status: scaffold. [bom.csv](bom.csv) is the starting BOM; CAD (STEP/STL + printable plates)
> to be added in Phase 3.

## Contents (planned)
- `bom.csv` — bill of materials (here now).
- `cad/` — source CAD (STEP) for the gripper body, controller mount, wrist-cam mount, finger
  tactile pads, MCU tray.
- `stl/` — printable meshes.
- `assembly.md` — build + wiring + mount-seating instructions.

## Design constraints (summary)
- Parallel-jaw, spring-return, human-squeezable; width → robot gripper open/close.
- **Repeatable** controller seat so `T_ctrl_gripper` survives re-mounts.
- Wrist camera looks down the jaws, inside the gripper envelope (avoid EgoKit desk-collision).
- One captive cable to the powered hub; MCU on the gripper.
- Mirror the body for a second hand later (N-extensible).
