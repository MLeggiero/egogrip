# Tactile bench test — MPR121 ("HW-017") on ESP32 / Nano / Mega

A standalone bench rig for bringing up the **12-strip capacitive tactile array** before it
goes anywhere near the gripper. One sketch, any of the boards in §0. It answers three
questions, in order:

1. Is the MPR121 alive and is every strip electrically sane?
2. Does *pressure* actually move the signal, and by how much per channel?
3. Can we get normalized, per-channel pressure and discrete tactile events out of it?

This is deliberately **not** the framed USB-CDC protocol from
[../rp2040-gripper/README.md](../rp2040-gripper/README.md) — it speaks human-readable serial
so you can debug with nothing but the Arduino IDE. Once the array is proven, the same maths
moves into the RP2040 firmware as `T_TACTILE` packets (§7).

---

## 0. Which board

The sketch runs unchanged on any of these — it picks up the right I²C pins from the core and
prints them at boot. What differs is the **logic level**, and that is the only thing that can
cost you a chip:

| Board | Logic | I²C pins | Notes |
|---|---|---|---|
| **Raspberry Pi Pico (RP2040)** | **3.3 V** | **GP4 / GP5** | **use this one** — see below |
| ESP32 DevKit (classic) | **3.3 V** | 21 / 22 | wire straight through |
| ESP32-S2 / S3 / C3 | **3.3 V** | 8 / 9 | ditto; S3 is the documented wireless upgrade path ([D8](../../docs/DESIGN_DECISIONS.md)) |
| Nano ESP32 | **3.3 V** | A4 / A5 | an ESP32-S3 in a Nano footprint |
| Nano 33 IoT / RP2040 Connect | **3.3 V** | A4 / A5 | wire straight through |
| Nano 33 BLE / BLE Sense | **3.3 V** | A4 / A5 | **not 5 V tolerant** — never feed it 5 V |
| **Nano (classic, ATmega328P)** | **5 V** | **A4 / A5** | works fine, but see the 5 V caution in §1 |
| Nano Every (ATmega4809) | **5 V** | A4 / A5 | same caution |
| Mega 2560 | **5 V** | 20 / 21 | same caution |

**If you have a Pico, use the Pico.** It is not just another 3.3 V board — it is the project's
production MCU ([D8](../../docs/DESIGN_DECISIONS.md)), and the sketch puts the MPR121 on
**I²C0, GP4/GP5: the exact bus and pins the AS5600 gripper firmware already uses**
([../rp2040-gripper/arduino/](../rp2040-gripper/arduino/)). The encoder answers at `0x36` and
the MPR121 at `0x5A`, so they share the bus with no conflict and no rewiring. That makes this
bench setup the real gripper wiring rather than a throwaway rig, and the tactile front end
merges into `egogrip_gripper.ino` as `T_TACTILE` frames without moving a single wire.

It also has native USB-CDC — which is what the headset needs on the other end of the hub, and
what a classic ESP32's CP2102/CH340 bridge is not.

**A classic Arduino Nano is a 5 V board.** Its `3V3` pin is an *output* from the USB-serial
chip's regulator — handy for powering the breakout, but it says nothing about the I/O pins,
which swing to 5 V. If you want a genuinely 3.3 V Nano, it has to be a Nano 33, a Nano RP2040
Connect, or a Nano ESP32.

For a pure bench test the classic Nano is perfectly adequate: this workload is I²C-bound, the
MPR121's own 1 ms conversion caps useful sampling near 500 Hz on any of these boards, and the
sketch keeps every string in flash (`F()`), so it uses roughly a third of the Nano's 2 KB of
RAM. You just have to respect §1.

**If you are on an ESP32, leave WiFi and Bluetooth off.** Both radios inject noise directly
into a high-impedance capacitive front end, and this sensor is resolving sub-picofarad
changes. Neither starts unless you call `WiFi.begin()`, so simply don't. If you later want
untethered logging, re-run the `d` diagnostics with the radio up and check whether `sigma`
moved before trusting the data.

## 1. Wiring — the MPR121 is a 3.3 V part whatever you plug it into

| MPR121 breakout | connect to |
|---|---|
| `VCC` / `3V3`  | your board's **3V3** pin — never `5V` |
| `GND`          | GND |
| `SDA`          | §0 table: **GP4** on a Pico, **A4** on a Nano, **20** on a Mega, **21** on classic ESP32 |
| `SCL`          | **GP5** / **A5** / **21** / **22** |
| `IRQ`          | not connected — this sketch polls |
| `ELE0…ELE11`   | the copper strips, **ELE0 = leftmost** |
| — | **ground plate → the same GND** |

The sketch prints the I²C pins it is actually using at boot, and if it finds no MPR121 it
sweeps the whole bus and lists whatever did respond — that distinguishes "nothing powered"
from "the `ADDR` strap put it somewhere unexpected". If your board breaks the I²C pins out
elsewhere, define `I2C_SDA` / `I2C_SCL` at the top of the sketch.

Never power the breakout from `5V` "because it has a regulator" — most HW-017 clones do not,
and the ones that do still expose the I²C lines at 3.3 V logic.

### On a 5 V board (classic Nano, Nano Every, Mega)

The MPR121's 3.6 V absolute maximum meets a 5 V microcontroller, in two places:

- **`Wire.begin()` enables the AVR's internal pull-ups, which sit on the 5 V rail.** The
  sketch switches them off immediately (`DISABLE_INTERNAL_PULLUPS`), leaving the breakout's
  own 3.3 V pull-ups to hold the bus. That keeps SDA/SCL at 3.3 V and is fine for bench work.
- **Best practice is still a bidirectional level shifter** (BSS138 / TXS0102 module) on
  SDA/SCL. If you fit one, set `DISABLE_INTERNAL_PULLUPS` to `0`.

**Check the Nano's 3V3 pin before you trust it.** It is fed by the USB-serial chip, not a
proper onboard regulator. On genuine FTDI boards it supplies ~50 mA, which is ample — the
MPR121 draws well under a milliamp. On CH340 clones that pin is sometimes weak, and on a few
it is not connected at all. Put a multimeter on it first; if it does not read ~3.3 V, feed
the breakout from a small external 3.3 V LDO with its ground tied to the Nano's.

**The ground plate must be tied to the MPR121's ground**, not left floating. A floating plate
turns the array into a proximity sensor (it will react to your hand hovering) instead of a
pressure sensor. §6 has a test for exactly this.

## 2. Flash it

```
Sketch: egogrip_tactile_bench/egogrip_tactile_bench.ino
Serial Monitor: 500000 baud   (ignored on the Pico — native USB-CDC has no baud rate)

Pico:   Board = "Raspberry Pi Pico"  — Arduino-Pico core by earlephilhower
ESP32:  Board = your variant (e.g. "ESP32 Dev Module" / "ESP32S3 Dev Module" / "Nano ESP32")
Nano:   Board = "Arduino Nano",  Processor = ATmega328P
        (older clones need "ATmega328P (Old Bootloader)" to upload)
Mega:   Board = "Arduino Mega or Mega 2560",  Processor = ATmega2560
```

No libraries to install — the sketch drives the MPR121 register-level over `Wire.h`, and
handles the board differences (I²C pins, flash-emulated EEPROM) itself. If your serial
monitor does not offer 500000, change `SERIAL_BAUD` at the top to `115200` and keep CSV
streaming at or below 100 Hz.

**On the Pico**, install the Arduino-Pico core by earlephilhower — the same core the gripper
firmware uses, and the one whose `Wire.setSDA()/setSCL()` API this sketch calls. In the IDE,
add this to **Preferences ▸ Additional Board Manager URLs** and install **rp2040**:

```
https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
```

Or build a `.uf2` headlessly, exactly as the gripper firmware does:

```bash
arduino-cli compile -b rp2040:rp2040:rpipico --output-dir build egogrip_tactile_bench
# hold BOOTSEL while plugging in, then copy build/*.uf2 onto the RPI-RP2 drive
```

The official Arduino Mbed RP2040 core will *not* build this — it lacks `Wire.setSDA()`.

Also set `PITCH_MM` to your real strip centre-to-centre spacing; it only scales the reported
contact centroid.

## 3. Bring-up, in order

**Step 1 — does it talk?** On reset the sketch prints the I²C pins, `# MPR121 at 0x5A`, and a
diagnostics table. If it prints `! no MPR121 found on 0x5A-0x5D` it follows with a full bus
scan — stop there and fix power or wiring before anything else. An address outside 0x5A-0x5D
in that scan means the `ADDR` strap is not on GND.

> On a native-USB board (Pico, ESP32-S3) opening the serial monitor does **not** reset the
> board the way it does on an AVR, so boot output can be gone before you attach. The sketch
> waits up to 3 s for the host to open the port to make that unlikely, but if you attach late
> you will land mid-stream with no banner — that is normal. Press **`d`** for the diagnostics
> table and **`h`** for the command list at any time.

**Step 2 — is every strip sane?** Look at the boot diagnostics (or press `d`):

```
ch  base  sigma  gate  span   cdc  flags
 0   612   0.41   2.0    80     16
 1   598   0.55   2.0    80     16
 2   205   4.90   14.7   80      6   NOISY
 ...
```

- `base` is the untouched filtered count. Healthy is roughly **150–950**. All twelve should
  be in the same ballpark; one wildly different strip is a wiring fault, not a feature.
- `sigma` is idle noise. Under ~1 count is good, over ~6 flags `NOISY` — usually a long
  unshielded lead, a floating ground plate, or a laptop charger.
- `gate` is the deadband, derived as `3σ`. Anything below it reads zero.
- Flags worth acting on: `BASE_LOW(shorted?)`, `BASE_HIGH(open?)`, `AUTOCFG_OOR`
  (the chip could not find a charge current for that strip — its capacitance is out of
  range, see §6), and `OVCF` (overcurrent — a strip is shorted to a rail).

**Step 3 — re-zero with hands off.** Press `z`. It averages 64 quiet samples into the
software baseline and re-measures noise. Do this whenever the rig has been moved or the
temperature has changed.

**Step 4 — does pressure register?** Press `4` for raw counts and push on one strip:

```
ch  filt  base   d   gate  span   norm
 3   551   612   61.0  2.0    80   0.756
```

`d` (= `base - filt`) is the pressure signal in counts, and it must be **positive and
comfortably above `gate`**. Twenty or more counts under a firm press is a workable sensor;
two or three counts is a mechanical problem, not a settings problem (§6).

**Step 5 — calibrate full scale.** Press `c`, then within 8 seconds press *every* pad about
as hard as you ever intend to. The sketch records the peak `d` per channel and makes it that
channel's `span`, i.e. `norm = 1.0`. It flags any strip that never responded. Press `w` to
save the spans to EEPROM — they reload automatically at the next boot.

**Step 6 — watch it work.** Press `1` for the live heat map:

```
     0 1 2 3 4 5 6 7 8 9 A B      total   centroid  n
    |. : = # % * .           |  2.31     14.7mm     6
EVT PRESS  ch4  peak 0.71  dur 412ms  cen 20.1->21.4mm
```

## 4. Output modes and commands

Single keystrokes in the serial monitor:

| key | mode | use |
|---|---|---|
| `1` | heat map + events | eyeballing the array (default, 20 Hz redraw) |
| `2` | Serial Plotter | 12 tab-separated normalized values — **this is the spectrum view**, open Tools ▸ Serial Plotter |
| `3` | CSV stream | `t_us,ch0…ch11` at the sample rate, for logging or `tools/tactile_view.py` |
| `4` | raw counts | per-channel `filt / base / delta / gate / span` |
| `0` | quiet | events only — the cleanest demo |

| key | action |
|---|---|
| `z` | re-zero baseline (hands off) |
| `c` | calibrate spans, 8 s |
| `w` / `l` / `x` | save spans to EEPROM / load / reset to default |
| `a` | re-run the MPR121 autoconfig, then re-baseline |
| `t` | toggle idle baseline tracking |
| `d` | diagnostics table |
| `+` / `-` | sample rate, 50–500 Hz (default 200) |
| `h` | help |

### Events

Contact is tracked with hysteresis (on at `norm > 0.15`, off below `0.07` for 40 ms) and
classified on release:

- `TAP` — under 200 ms, centroid stayed put
- `PRESS` — sustained; reports peak pressure and duration
- `SLIDE` — the contact centroid moved ≥ 3 mm across the strip, reported as `start->end`

Each event also carries the strongest channel and a `[SATURATED]` note if it pinned at 1.0,
which means the calibrated span is too small.

## 5. Host-side viewer

`tools/tactile_view.py` renders the CSV stream as a live colour bar graph — taller bar =
harder press — with running total, peak, contact centroid and sample rate:

```bash
pip install pyserial
python3 tools/tactile_view.py --port /dev/ttyUSB0 --save press_test.csv
python3 tools/tactile_view.py --replay press_test.csv     # review it later, no hardware
```

The port is typically `/dev/ttyUSB0` for a classic ESP32 or a Nano (CP2102/CH340/FTDI
bridge), `/dev/ttyACM0` for a Mega or a native-USB ESP32-S3, and `COMn` on Windows.

It sends `3` on connect to put the sketch into CSV mode, and `--save` tees the raw stream so
a session can be replayed or loaded into anything that reads CSV.

## 6. When the signal is weak — it is almost always mechanical

Each strip forms a parallel-plate capacitor with the grounded plate:

```
C = ε0 · εr · A / d        εr(silicone) ≈ 3
```

A 10 × 40 mm strip at d = 1 mm is about **10 pF**. Pressing compresses `d`, and since
`ΔC/C ≈ -Δd/d`, squeezing that 1 mm gap by 10 µm is a **1 % change** — a fraction of a pF.
That is right at the edge of what the MPR121 resolves, which is why the sketch's job is
mostly to tell you whether your *mechanics* give it anything to measure.

Three things dominate, in order of impact:

1. **Solid silicone barely compresses.** Rubber is nearly incompressible in bulk (Poisson
   ratio ≈ 0.5); a fully constrained sheet between two plates has nowhere to go, so `d`
   hardly changes no matter how hard you press. **Give it room to bulge sideways**: cut the
   silicone into islands or strips *smaller* than the pads, emboss/dimple it, or switch to
   open-cell foam or a spacer-ring air gap. This one change is usually worth 5–20× the
   signal of every electrical tweak combined.
2. **Baseline capacitance too high.** Strips much bigger than ~10 × 40 mm, or a gap much
   under 1 mm, push the static load past what the MPR121 can bias — you get `AUTOCFG_OOR`
   and a compressed `base`. Fix with smaller strips or a thicker dielectric.
3. **Lead capacitance and noise.** Every centimetre of unshielded wire adds static
   capacitance and picks up mains hum. Keep leads short and equal-length; run them under the
   ground plate where you can.

**Shield check (do this once).** Press with a bare finger, then with an insulated object — a
pencil eraser or plastic stylus — at roughly the same force. The readings should be
*similar*. If the finger reads far higher, your grounded plate is not actually shielding:
check that it is continuous (copper tape overlaps conduct poorly — solder or bridge the
seams) and truly tied to the MPR121's GND. An unshielded array measures your hand, not your
force, and will read nothing at all through a glove or a gripper jaw.

Other things worth knowing:

- **Adjacent-channel crosstalk** is normal. The MPR121 charges one electrode at a time and
  leaves the others floating, so a press between two strips shows on both — that is what
  makes the sub-pitch `centroid` work, not a defect.
- **Drift** with temperature and humidity is real. Idle baseline tracking (`t`, on by
  default, ~10 s time constant) removes it *only while a channel is idle*, so a sustained
  press is never quietly zeroed out. On-chip baseline tracking is deliberately disabled
  (ECR `CL=00`) because it would do exactly that.
- **Hysteresis and creep** from the silicone mean release is slower than press. Expect it;
  it is a property of the rubber, not the electronics.
- CSV at 200 Hz is close to the practical serial limit on a 16 MHz AVR (comfortable on the
  ESP32). Every row carries `micros()`, so if the rate does sag the timing is still
  recoverable — but drop to 100 Hz if you see jitter. CH340-based Nano clones in particular
  can be unreliable above 115200; if the stream arrives as garbage, that is the first thing
  to change (`SERIAL_BAUD`), not the sensor.
- **On ESP32, an active radio is a noise source.** If `sigma` climbs or the array starts
  reacting to nothing, check WiFi/BLE before you go looking for a mechanical fault.

## 7. How this feeds the real system

The bench sketch is the reference implementation of the tactile front end; the parts that
graduate to the RP2040 gripper firmware are:

- **the normalization** — `norm = clamp((base - filt - gate) / (span - gate), 0, 1)`, with
  spans from the `c` calibration, so tactile arrives at the pipeline already unit-free;
- **the derived features** — total, peak channel and contact centroid in mm;
- **the calibration procedure**, which becomes part of [../../docs/CALIBRATION.md](../../docs/CALIBRATION.md).

On the gripper, the MPR121 shares the MCU's I²C bus with the AS5600 (0x5A vs 0x36, no
conflict) and channels are emitted as `T_TACTILE` frames — `n:u8` then `n × i16` — at the
rate set by `SET_RATE`. The CSV this sketch produces is already the column layout the
pipeline expects for `tactile.csv` (`monotonic_ns, ch0 … chN`, see
[../../docs/DATA_FORMAT.md](../../docs/DATA_FORMAT.md)); only the timestamp column changes,
from MCU `micros()` to the headset's monotonic clock.

That MCU is the RP2040 today ([D8](../../docs/DESIGN_DECISIONS.md)), so **if you benched on a
Pico there is nothing to port** — the MPR121 is already on I²C0/GP4/GP5 alongside the AS5600,
and merging means adding a `T_TACTILE` emitter to `egogrip_gripper.ino` with the normalization
above. Keep the framed protocol's `i16` channels as `norm × 10000` so the wire format stays
integer and unit-free.

If you benched on an ESP32 instead, an **ESP32-S3** is the documented upgrade path — it has
native USB-CDC (which the classic ESP32's CP2102/CH340 bridge does not), and that is what the
headset needs on the other end of the hub. A classic ESP32 is a fine bench host but is not a
drop-in for the gripper.
