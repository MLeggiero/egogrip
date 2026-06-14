# egogrip RP2040 gripper firmware — CircuitPython reference
# Streams the framed serial protocol (see ../README.md) over USB-CDC data.
# No toolchain: flash CircuitPython UF2, drop this + boot.py onto CIRCUITPY.
#
# Frame: 0xAA 0x55 | type:u8 | seq:u8 | micros:u32(LE) | len:u8 | payload | crc8(type..payload)
#   STATE(0x01):   raw_counts:u16, delta_counts:i32, trigger:u8
#   TACTILE(0x02): n:u8, n x i16
#   SYNC(0x03):    event_id:u32
#
# Wiring (optional — works with nothing connected, values just float/oscillate):
#   A0 (GP26) = gripper width pot/hall ; A1 (GP27), A2 (GP28) = tactile pads.

import struct
import time

import usb_cdc

# ---- config ----
STATE_HZ = 200
TACTILE_HZ = 200
WIDTH_COUNTS_FULL = 4096
TRIGGER_COUNTS = int(0.2 * WIDTH_COUNTS_FULL)
DEBUG_ASCII = False  # True -> human-readable lines instead of binary (for a serial monitor)

serial = usb_cdc.data

# ---- optional analog inputs (degrade gracefully if a pin is unavailable) ----
_width_in = None
_tactile_in = []
try:
    import analogio
    import board

    _width_in = analogio.AnalogIn(board.A0)
    for name in ("A1", "A2"):
        if hasattr(board, name):
            _tactile_in.append(analogio.AnalogIn(getattr(board, name)))
except Exception:
    pass

N_TACTILE = max(2, len(_tactile_in))  # always report >=2 channels


def crc8(data):
    crc = 0
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = ((crc << 1) ^ 0x07) & 0xFF if (crc & 0x80) else (crc << 1) & 0xFF
    return crc


_seq = 0


def micros():
    return (time.monotonic_ns() // 1000) & 0xFFFFFFFF


def send(ptype, payload):
    global _seq
    body = bytes([ptype, _seq]) + struct.pack("<I", micros()) + bytes([len(payload)]) + payload
    serial.write(bytes([0xAA, 0x55]) + body + bytes([crc8(body)]))
    _seq = (_seq + 1) & 0xFF


def read_width_counts(t):
    if _width_in is not None:
        return int(_width_in.value / 65535 * WIDTH_COUNTS_FULL)
    # no sensor: smooth synthetic open/close so the host sees moving data
    import math
    return int((0.5 + 0.5 * math.sin(t * 2 * math.pi * 0.8)) * WIDTH_COUNTS_FULL)


def read_tactile(t):
    if _tactile_in:
        return [min(32767, ti.value >> 1) for ti in _tactile_in]
    import math
    base = int((0.5 + 0.5 * math.cos(t * 2 * math.pi * 0.8)) * 20000)
    return [base, base // 2]


def main():
    next_state = 0
    next_tactile = 0
    state_dt = 1.0 / STATE_HZ
    tactile_dt = 1.0 / TACTILE_HZ
    while True:
        now = time.monotonic()
        if now >= next_state:
            counts = read_width_counts(now)
            trig = 1 if counts < TRIGGER_COUNTS else 0
            if DEBUG_ASCII:
                serial.write(("STATE counts=%d trig=%d us=%d\r\n" % (counts, trig, micros())).encode())
            else:
                raw = counts % 4096  # within-turn; pot is single-turn so delta == counts
                send(0x01, struct.pack("<Hi", raw, counts) + bytes([trig]))
            next_state = now + state_dt
        if now >= next_tactile:
            ch = read_tactile(now)[:N_TACTILE]
            while len(ch) < N_TACTILE:
                ch.append(0)
            if DEBUG_ASCII:
                serial.write(("TACTILE %s us=%d\r\n" % (ch, micros())).encode())
            else:
                send(0x02, bytes([len(ch)]) + struct.pack("<%dh" % len(ch), *ch))
            next_tactile = now + tactile_dt


main()
