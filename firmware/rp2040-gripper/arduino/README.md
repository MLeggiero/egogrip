# RP2040 gripper firmware — Arduino-Pico (primary)

AS5600 magnetic-absolute gripper encoder → framed USB-CDC. This is the **production** firmware;
the CircuitPython sketch in `../circuitpython/` is the no-toolchain alternate.

## Wiring (AS5600 → Pico)

| AS5600 | Pico |
|---|---|
| VCC | 3V3 |
| GND | GND |
| SDA | GP4 (I2C0 SDA) |
| SCL | GP5 (I2C0 SCL) |

Mount a diametric magnet over the chip; the firmware reports magnet health (`ok`/`weak`/`strong`/
`missing`) in the `INFO` packet — seat it until it reads `ok`, then tare. Pins are `#define`d at the
top of [egogrip_gripper/egogrip_gripper.ino](egogrip_gripper/egogrip_gripper.ino).

## Build & flash (arduino-cli)

```bash
arduino-cli config init --overwrite
arduino-cli config add board_manager.additional_urls \
  https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
arduino-cli core update-index
arduino-cli core install rp2040:rp2040

# compile -> .uf2
arduino-cli compile -b rp2040:rp2040:rpipico --output-dir build egogrip_gripper

# flash: hold BOOTSEL, plug in, then either:
arduino-cli upload -b rp2040:rp2040:rpipico -p /dev/ttyACM0 egogrip_gripper
# ...or just copy build/egogrip_gripper.ino.uf2 onto the RPI-RP2 drive.
```

## Behaviour

- Streams `STATE` at 200 Hz (configurable via the `SET_RATE` command): `raw_counts` (within-turn),
  `delta_counts` (multi-turn accumulated, relative to the start-closed tare), `trigger`.
- `ZERO` sets the tare at the closed reference; do it each session (the multi-turn count can't
  survive a power cycle). The app's "Zero gripper" action sends this.
- No AS5600 attached → a synthetic sweep streams so the host pipeline can be exercised on a bare Pico.
- Tier-1 timing only: each packet carries `micros()`; there is no sync LED.

Counts → millimetres/metres happens off-device from `calibration.json`
([docs/CALIBRATION.md](../../../docs/CALIBRATION.md)).
