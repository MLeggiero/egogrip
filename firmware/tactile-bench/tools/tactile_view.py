#!/usr/bin/env python3
"""Live viewer for the egogrip tactile bench sketch (ESP32 or Arduino Mega + MPR121).

Reads the sketch's CSV mode (`t_us,ch0..ch11`, normalized 0..1) and draws a colour bar
graph in the terminal, one column per copper strip, left to right. Optionally tees the
stream to a file so a session can be replayed or plotted later.

    python3 tactile_view.py --port /dev/ttyACM0 --save press_test.csv
    python3 tactile_view.py --replay press_test.csv          # no hardware needed

Only dependency is pyserial, and only for --port.
"""

from __future__ import annotations

import argparse
import sys
import time

N_CH = 12
RAMP = " .:-=+*x#%@"
# 24-bit colour ramp, cool (light) -> hot (heavy).
COLORS = [(60, 60, 80), (60, 90, 140), (50, 130, 170), (60, 170, 150),
          (120, 200, 90), (210, 210, 70), (240, 170, 50), (240, 110, 40),
          (230, 60, 40), (210, 30, 60), (200, 20, 110)]


def level(v: float) -> int:
    return max(0, min(10, int(round(v * 10))))


def cell(v: float, color: bool) -> str:
    idx = level(v)
    ch = RAMP[idx]
    if not color:
        return ch
    r, g, b = COLORS[idx]
    return f"\033[38;2;{r};{g};{b}m{ch}\033[0m"


def parse(line: str):
    """Return the 12 normalized channels, or None for anything that isn't a data row."""
    line = line.strip()
    if not line or line[0] in "#!" or line.startswith("EVT") or line.startswith("t_us"):
        return None
    parts = line.split(",")
    if len(parts) != N_CH + 1:
        return None
    try:
        return [float(p) for p in parts[1:]]
    except ValueError:
        return None


def render(vals, pitch_mm: float, rate: float, color: bool) -> str:
    total = sum(vals)
    active = [i for i, v in enumerate(vals) if v > 0.02]
    centroid = (sum(v * i * pitch_mm for i, v in enumerate(vals)) / total) if total > 0 else None

    rows = []
    # Vertical bars: 8 rows tall, so pressure differences are visible at a glance.
    for r in range(8, 0, -1):
        cells = []
        for v in vals:
            filled = v * 8
            cells.append(cell(v, color) if filled >= r - 0.5 else " ")
        rows.append("  |" + " ".join(cells) + "|")
    rows.append("  +" + "-" * (2 * N_CH - 1) + "+")
    rows.append("   " + " ".join(f"{i:X}" for i in range(N_CH)) + "   (0 = leftmost strip)")
    rows.append("")
    cen = f"{centroid:6.1f} mm" if centroid is not None else "     -- "
    rows.append(f"  total {total:5.2f}   peak {max(vals):4.2f}   centroid {cen}"
                f"   contacts {len(active):2d}   {rate:5.1f} Hz")
    return "\n".join(rows)


def run(source, pitch_mm: float, color: bool, save, realtime: bool) -> None:
    frames, t0, rate = 0, time.time(), 0.0
    height = 0
    try:
        for raw in source:
            if save:
                save.write(raw if raw.endswith("\n") else raw + "\n")
            vals = parse(raw)
            if vals is None:
                text = raw.strip()
                if text.startswith("EVT") or text.startswith("!"):
                    sys.stdout.write("\r\033[K" + text + "\n")
                    height = 0
                continue

            frames += 1
            dt = time.time() - t0
            if dt >= 0.5:
                rate, frames, t0 = frames / dt, 0, time.time()

            out = render(vals, pitch_mm, rate, color)
            if height:
                sys.stdout.write(f"\033[{height}A")
            sys.stdout.write("\033[J" + out + "\n")
            sys.stdout.flush()
            height = out.count("\n") + 1
            if realtime:
                time.sleep(0.03)
    except KeyboardInterrupt:
        sys.stdout.write("\n")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--port", help="serial port, e.g. /dev/ttyACM0 or COM3")
    src.add_argument("--replay", help="replay a saved CSV instead of reading serial")
    ap.add_argument("--baud", type=int, default=500000, help="must match SERIAL_BAUD in the sketch")
    ap.add_argument("--pitch", type=float, default=5.0, help="strip pitch in mm (centroid scale)")
    ap.add_argument("--save", help="tee the stream to this file")
    ap.add_argument("--no-color", action="store_true")
    args = ap.parse_args()

    save = open(args.save, "w") if args.save else None
    color = not args.no_color and sys.stdout.isatty()

    try:
        if args.replay:
            with open(args.replay) as fh:
                run(fh, args.pitch, color, save, realtime=True)
            return 0

        try:
            import serial  # type: ignore
        except ImportError:
            print("pyserial is required for --port:  pip install pyserial", file=sys.stderr)
            return 1

        with serial.Serial(args.port, args.baud, timeout=1) as ser:
            time.sleep(2.0)          # the Mega resets when the port opens
            ser.reset_input_buffer()
            ser.write(b"3\n")        # switch the sketch into CSV mode

            def lines():
                while True:
                    raw = ser.readline()
                    if raw:
                        yield raw.decode("ascii", "replace")

            run(lines(), args.pitch, color, save, realtime=False)
        return 0
    finally:
        if save:
            save.close()


if __name__ == "__main__":
    sys.exit(main())
