# Tactile bench test — ESP32 or Arduino Mega + MPR121 ("HW-017")

A standalone bench rig for bringing up the **12-strip capacitive tactile array** before it
goes anywhere near the gripper. One sketch, both boards. It answers three questions, in order:

1. Is the MPR121 alive and is every strip electrically sane?
2. Does *pressure* actually move the signal, and by how much per channel?
3. Can we get normalized, per-channel pressure and discrete tactile events out of it?

This is deliberately **not** the framed USB-CDC protocol from
[../rp2040-gripper/README.md](../rp2040-gripper/README.md) — it speaks human-readable serial
so you can debug with nothing but the Arduino IDE. Once the array is proven, the same maths
moves into the RP2040 firmware as `T_TACTILE` packets (§7).

---

## 0. Which board — use the ESP32

Both work and the sketch builds for both, but the ESP32 is the better bench host and the
better long-term answer:

| | ESP32 | Mega 2560 |
|---|---|---|
| Logic level | **3.3 V — wire straight to the MPR121** | 5 V; needs the pull-up workaround or a level shifter |
| Clock / FPU | 240 MHz, hardware float | 16 MHz, software float |
| RAM | 520 KB | 8 KB |
| Serial throughput | 921600+ comfortably | 500000 is the practical ceiling |
| Radio | WiFi/BLE for untethered logging | none |
| Fits the roadmap | **yes** — ESP32-S3 is the documented wireless upgrade path ([D8](../../docs/DESIGN_DECISIONS.md)) | bench only, never goes on the gripper |

The level shifting is the deciding factor. On the Mega the MPR121 sits one wiring slip away
from 5 V on a 3.6 V-absolute-max pin; on the ESP32 that failure mode does not exist. The
speed and RAM are a bonus — this workload is I²C-bound, and the MPR121's own 1 ms conversion
period caps useful sampling around 500 Hz on either board.

**One ESP32-specific caution:** leave **WiFi and Bluetooth off**. Both radios inject noise
directly into a high-impedance capacitive front end, and this sensor is already resolving
sub-picofarad changes. Neither starts unless you call `WiFi.begin()`, so simply don't. If you
later want untethered logging, re-run the `d` diagnostics with the radio up and check whether
`sigma` moved before trusting the data.

## 1. Wiring — the MPR121 is a 3.3 V part on either board

| MPR121 breakout | ESP32 | Mega 2560 |
|---|---|---|
| `VCC` / `3V3`  | **3V3** | **3V3** (never 5V) |
| `GND`          | GND | GND |
| `SDA`          | core default (**21** classic, **8** on S2/S3/C3) | **20** |
| `SCL`          | core default (**22** classic, **9** on S2/S3/C3) | **21** |
| `IRQ`          | not connected (this sketch polls) | — |
| `ELE0…ELE11`   | copper strips, **ELE0 = leftmost** | — |
| — | **ground plate → the same GND** | — |

The sketch prints the I²C pins it is actually using at boot, and if it finds no MPR121 it
sweeps the whole bus and lists whatever did respond — that distinguishes "nothing powered" from
"the `ADDR` strap put it somewhere unexpected". ESP32 boards vary; if yours breaks the I²C pins
out elsewhere, define `I2C_SDA` / `I2C_SCL` at the top of the sketch.

**On the Mega only**, the MPR121's 3.6 V absolute maximum meets a 5 V board:

- **`Wire.begin()` enables the AVR's internal pull-ups, which sit on the 5 V rail.** The
  sketch switches them off immediately (`DISABLE_INTERNAL_PULLUPS`), leaving the breakout's
  own 3.3 V pull-ups to hold the bus. That keeps SDA/SCL at 3.3 V and is fine for bench work.
- **Best practice is still a bidirectional level shifter** (BSS138 / TXS0102 module) between
  the Mega's 20/21 and the breakout. If you fit one, set `DISABLE_INTERNAL_PULLUPS` to `0`.

On either board, do not power the breakout from `5V` "because it has a regulator" — most
HW-017 clones do not, and the ones that do still expose the I²C lines at 3.3 V logic.

**The ground plate must be tied to the MPR121's ground**, not left floating. A floating plate
turns the array into a proximity sensor (it will react to your hand hovering) instead of a
pressure sensor. §6 has a test for exactly this.

## 2. Flash it

```
Sketch: egogrip_tactile_bench/egogrip_tactile_bench.ino
Serial Monitor: 500000 baud

ESP32:  Board = your variant (e.g. "ESP32 Dev Module" / "ESP32S3 Dev Module")
Mega:   Board = "Arduino Mega or Mega 2560",  Processor = ATmega2560
```

No libraries to install — the sketch drives the MPR121 register-level over `Wire.h`, and
handles the board differences (I²C pins, flash-emulated EEPROM) itself. If your serial
monitor does not offer 500000, change `SERIAL_BAUD` at the top to `115200` and keep CSV
streaming at or below 100 Hz.

Also set `PITCH_MM` to your real strip centre-to-centre spacing; it only scales the reported
contact centroid.

## 3. Bring-up, in order

**Step 1 — does it talk?** On reset the sketch prints the I²C pins, `# MPR121 at 0x5A`, and a
diagnostics table. If it prints `! no MPR121 found on 0x5A-0x5D` it follows with a full bus
scan — stop there and fix power or wiring before anything else. An address outside 0x5A-0x5D
in that scan means the `ADDR` strap is not on GND.

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

The port is typically `/dev/ttyUSB0` for a classic ESP32 (CP2102/CH340 bridge),
`/dev/ttyACM0` for a Mega or a native-USB ESP32-S3, and `COMn` on Windows.

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
- CSV at 200 Hz is close to the practical serial limit on the Mega (comfortable on the
  ESP32). Every row carries `micros()`, so if the rate does sag the timing is still
  recoverable — but drop to 100 Hz if you see jitter.
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

That MCU is the RP2040 today ([D8](../../docs/DESIGN_DECISIONS.md)). If you bench this on an
ESP32 and want to keep it, an **ESP32-S3** is already the documented upgrade path — it has
native USB-CDC (which the classic ESP32's CP2102/CH340 bridge does not) and that is what the
headset needs on the other end of the hub. A classic ESP32 is a fine bench host but is not a
drop-in for the gripper.
