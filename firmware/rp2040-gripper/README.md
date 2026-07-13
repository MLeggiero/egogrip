# RP2040 gripper firmware

Reads **gripper width** + **tactile** and (optionally) drives a **sync LED**, streaming
framed packets over **USB-CDC** to the headset. Each packet carries the MCU `micros()`
counter so the pipeline can fit the MCU↔device clock (see [../../docs/SYNC.md](../../docs/SYNC.md)).

> Status: the **Arduino-Pico firmware** in [arduino/](arduino/) is the primary, buildable target
> (AS5600). [src/main.c](src/main.c) is a Pico-SDK skeleton; [circuitpython/](circuitpython/) is the
> no-toolchain alternate. All three speak the same protocol below.

## Responsibilities
- **Width:** AS5600 magnetic-absolute encoder over **I²C**; firmware tracks multi-turn accumulation
  and sends `raw_counts` + `delta_counts` (host converts to metres via calibration.json). Re-zero
  closed each session (`ZERO`), since accumulation can't survive a power cycle.
- **Tactile:** N analog channels via ADC (mux if >3) or a digital sensor over I²C/SPI. The
  firmware is sensor-agnostic; channel *meaning* is declared by the host-side plugin in the
  episode manifest.
- **Sync:** on command (or schedule), flash an LED on a GPIO and emit a `SYNC` packet at the
  same instant.
- **Timing:** every packet includes a monotonic `micros_u32` from the MCU.

## Serial protocol (v0.1)

USB-CDC, **little-endian**, line-oriented binary frames. Default 200 Hz state, tactile up to
1 kHz (configurable). Framing:

```
0xAA 0x55  | type:u8 | seq:u8 | micros_u32 | len:u8 | payload[len] | crc8
```

| type | name | payload |
|---|---|---|
| 0x01 | `STATE`   | `raw_counts:u16`, `delta_counts:i32`, `trigger:u8` |
| 0x02 | `TACTILE` | `n:u8`, then `n × i16` channel samples |
| 0x03 | `SYNC`    | `event_id:u32` (paired with an LED flash) |
| 0x10 | `INFO`    | ascii: firmware version, channel count, rates (sent on connect) |

Host→MCU commands (same framing, type ≥ 0x80):
| 0x80 | `SET_RATE` | `state_hz:u16`, `tactile_hz:u16` |
| 0x81 | `PULSE`    | `event_id:u32` (fire one LED sync pulse now) |
| 0x82 | `ZERO`     | tare tactile / set encoder zero |

CRC8 (poly 0x07) over `type..payload`. The host drops frames failing CRC and logs them.
On connect the MCU sends `INFO` so the host knows channel count and rates without guessing.

## Build
- **Arduino-Pico** (primary): [arduino/README.md](arduino/README.md) — `arduino-cli` → `.uf2`.
- Pico-SDK (CMake) skeleton in [src/main.c](src/main.c) is not yet buildable.
- Output flashed by holding BOOTSEL and copying the `.uf2` onto the RPI-RP2 drive.

## Why RP2040
USB-CDC reliability on Android, hardware quadrature via PIO, enough ADC for a tactile array,
cheap. ESP32-S3 is the wireless upgrade (BLE/Wi-Fi sync + offload); Teensy 4.x if tactile
needs very high sample rates. See [../../docs/DESIGN_DECISIONS.md](../../docs/DESIGN_DECISIONS.md) D8.
