# PICO 4 Ultra Enterprise — camera & depth access notes

> ⚠️ **Verify everything here against current PICO docs before committing budget.** PICO's
> enterprise program, API names, and what's exposed change between SDK releases. The points
> below are the working assumptions egogrip is designed around; treat them as a checklist to
> confirm, not gospel.

## Why Enterprise at all

- The consumer **PICO 4 Ultra** does **not** give third-party apps the passthrough camera
  feed. App-level RGB requires the **PICO 4 Ultra Enterprise** device + the **Enterprise
  SDK / Main Camera Access API** + an **authorized package name** (you register your app's
  Android package id with PICO and join their testing/enterprise program).
- EgoKit demonstrated ego capture on PICO at ~1280×960 / ~89 fps via a passthrough-camera
  path — so app-level ego RGB on PICO is feasible *with the right access*.

## What to confirm during Phase 0

1. **Enrollment path & lead time** — how to register the package name and get the enterprise
   camera entitlement; how long it takes. This is the project's critical path.
2. **RGB access** — resolution(s), frame rate, format (likely YUV), and how frames are
   delivered (Texture2D / native buffer). Confirm both passthrough cameras vs one.
3. **Intrinsics / extrinsics** — does the API expose camera intrinsics and the
   `head → ego_cam` transform? We need these for projecting/aligning ego frames. If not,
   calibrate against an AprilTag board.
4. **Depth** — **likely not available as raw iToF frames** to apps. Check whether any of:
   - spatial **mesh** / scene reconstruction (geometry, not per-pixel depth), or
   - a **depth/environment** API
   is exposed. If only mesh is available, that is *not* equivalent to per-pixel ego depth.
5. **Simultaneous use** — can the enterprise camera run **while** two USB UVC cameras stream
   and the device charges over PD? (Vision Pro notably *cannot* do 2 USB cams + scene cam;
   confirm PICO can.)
6. **Permissions/MDM** — whether camera access needs device provisioning (ArborXR-style MDM)
   or just the signed entitlement; affects deployment.

## Implications for egogrip

- **Ego RGB:** plan A. Gate it behind Phase-0 access; have a fallback (rig-mounted USB camera
  as an "ego-ish" view, or wrist-only dataset) if access slips.
- **Ego depth:** **do not promise it for MVP.** Tracked as a stretch — either revisit PICO
  depth/mesh access, or get depth from a rig sensor (RealSense/OAK) over USB if depth becomes
  a hard requirement (costs USB bandwidth — re-budget §HARDWARE.md if so).
- **Pose:** controller + hand + head pose are standard SDK features (not enterprise-gated),
  so the pose spine works regardless of camera-access timing — build it first (Phase 1).

## Useful references (verify current versions)

- PICO Business — Enterprise API intro / Main Camera Access API.
- PICO Developer — PICO 4 Ultra resources, Integration SDK (Sense Pack / MR, Hand
  Interaction, Motion Tracking).
- `picoxr/support` (GitHub) — technical support / samples.
- `styly-dev/EnterpriseCameraAccessPlugin` (GitHub) — community Unity wrapper for PICO/Vision
  Pro enterprise camera access; useful reference for the access pattern (requires enterprise
  device + authorized package).
