# RP2040 firmware — CircuitPython (no toolchain)

The fastest way to get the gripper MCU streaming for tomorrow: **drag-and-drop**, no IDE.

## Flash (one time)
1. Download the **CircuitPython UF2** for your RP2040 board from
   [circuitpython.org/downloads](https://circuitpython.org/downloads) (pick "Raspberry Pi Pico"
   or your exact board).
2. Hold **BOOTSEL**, plug the RP2040 into your PC → a drive **RPI-RP2** appears.
3. Drag the `.uf2` onto **RPI-RP2**. It reboots and remounts as **CIRCUITPY**.

## Install our firmware
Copy both files from this folder onto the **CIRCUITPY** drive:
- `boot.py`  (exposes a single USB-serial data port)
- `code.py`  (streams the protocol; auto-runs)

Eject/replug. It streams immediately — no sensors required (it emits smooth synthetic values
until you wire a pot to A0 and tactile pads to A1/A2).

## Sanity check on your PC (tonight)
- Set `DEBUG_ASCII = True` in `code.py`, save, and open the CIRCUITPY serial port at 115200 in
  any serial monitor — you'll see readable `STATE …` / `TACTILE …` lines.
- Set it back to `False` before tomorrow so the PICO app gets the binary protocol.

## Notes
- `boot.py` disables the REPL console so exactly one CDC port is exposed (clean for
  usb-serial-for-android). To get the REPL back for debugging, set `console=True` in `boot.py`.
- This mirrors the protocol in [../README.md](../README.md). The C/Pico-SDK version
  ([../src/main.c](../src/main.c)) is the eventual production firmware; CircuitPython is the
  zero-friction path for bring-up.
