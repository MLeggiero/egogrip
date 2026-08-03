/*
 * egogrip tactile bench test — ESP32 or Arduino Mega 2560 + MPR121 capacitive breakout
 * (sold as "HW-017" / "GY-MPR121"; the chip is an NXP/Freescale MPR121).
 *
 * Sensor under test: 12 copper-tape strips (ELE0 = leftmost) under a GROUNDED copper-tape
 * plate, separated by ~1 mm of silicone. Pressing squeezes the dielectric gap, capacitance to
 * the grounded plate rises, and the MPR121's filtered count FALLS. So the pressure signal is
 *
 *     delta[i] = baseline[i] - filtered[i]        (counts, positive under load)
 *     norm[i]  = clamp((delta - gate) / (span - gate), 0, 1)
 *
 * This is a BENCH sketch, not flight firmware: it talks human-readable serial, not the framed
 * protocol in ../rp2040-gripper/README.md. It exists to answer "does my sensor actually work,
 * and what dynamic range does each channel have?" before that data is worth streaming.
 *
 * !! The MPR121 is a 3.3 V PART — check which side of this line your board is on !!
 *   3.3 V logic, wire straight through: Pico/RP2040, ESP32, Nano ESP32, Nano 33, Nano RP2040.
 *   5 V logic, needs care: Mega 2560, Nano (classic ATmega328P), Nano Every.
 *       Power the breakout from 3V3 (never 5V) and either fit a level shifter on SDA/SCL or
 *       leave DISABLE_INTERNAL_PULLUPS set (see README.md §1).
 *   A classic Arduino Nano is a 5 V board. Its "3V3" pin is an output from the USB-serial
 *   chip, not a sign that the I/O pins are 3.3 V — they are not.
 *
 * Wiring: VCC->3V3, GND->GND, ground plate -> the same GND. I2C is SDA/SCL for your board
 * (GP4/GP5 on a Pico, A4/A5 on a Nano, 20/21 on a Mega) and is printed at boot, so you can
 * confirm it there. IRQ unused (this sketch polls). Send 'h' over serial for the commands.
 *
 * On ESP32, leave WiFi and Bluetooth off. Both radios inject noise straight into a
 * high-impedance capacitive front end; neither is started unless you ask for it.
 */
#include <Wire.h>
#include <EEPROM.h>

// ---------------------------------------------------------------- board differences

#if defined(ARDUINO_ARCH_RP2040)
// I2C0 on GP4/GP5 — deliberately the same bus and pins as the AS5600 gripper firmware
// (../../rp2040-gripper/arduino/). The MPR121 sits at 0x5A and the encoder at 0x36, so both
// share the bus with no conflict and this bench wiring is already the gripper wiring.
// Requires the Arduino-Pico core (earlephilhower), which the rest of the repo standardizes on.
#ifndef I2C_SDA
#define I2C_SDA 4
#endif
#ifndef I2C_SCL
#define I2C_SCL 5
#endif
#define EE_SIZE 256         // flash-emulated EEPROM: must be sized up front and committed
#define EE_COMMIT() EEPROM.commit()

#elif defined(ARDUINO_ARCH_ESP32)
// The core's default I2C pins vary by variant: 21/22 on classic ESP32, 8/9 on S2/S3/C3.
// Override here if your board breaks them out elsewhere; the pins in use are printed at boot.
#ifndef I2C_SDA
#define I2C_SDA SDA
#endif
#ifndef I2C_SCL
#define I2C_SCL SCL
#endif
#define EE_SIZE 64          // ESP32 "EEPROM" is emulated in flash and must be sized + committed
#define EE_COMMIT() EEPROM.commit()

#else
#define I2C_SDA SDA         // AVR: Mega 20/21, Nano A4/A5 — fixed by the TWI peripheral
#define I2C_SCL SCL
#define EE_COMMIT() ((void)0)
#endif

// ---------------------------------------------------------------- user config

static const uint8_t N_CH = 12;             // ELE0..ELE11, index 0 = leftmost strip
static const float PITCH_MM = 5.0f;         // strip centre-to-centre spacing, for the centroid
// 115200 because it is the one rate every monitor defaults to — a mismatch here looks exactly
// like a broken sensor. Ignored entirely on Pico/ESP32-S3 native USB-CDC, which run at USB
// speed regardless. On an AVR, raise this to 500000 if you want CSV above ~100 Hz.
static const long SERIAL_BAUD = 115200;
static const uint16_t DEFAULT_SPAN = 80;    // counts of delta treated as "full scale" pre-calibration
static const uint16_t SAMPLE_HZ_DEFAULT = 200;
static const uint8_t PRINT_HZ = 20;         // heat-mode redraw rate (terminals hate 200 Hz)

// The MPR121 charges one electrode at a time and its internal baseline filter would quietly
// absorb a sustained press, so baseline tracking is disabled on-chip (ECR CL=00) and done here
// instead: we only re-learn the baseline while a channel is idle.
static const bool BASE_TRACK_DEFAULT = true;
static const float BASE_TRACK_ALPHA = 0.0005f;  // ~10 s time constant at 200 Hz
static const float BASE_DRIFT_FRACTION = 0.1f;  // ~100 s for signal below the contact threshold
static const float NOISE_GATE_SIGMA = 3.0f;     // gate = max(GATE_MIN, 3 sigma)
static const float NOISE_GATE_MIN = 2.0f;

// Contact hysteresis (normalized units).
static const float ON_THRESH = 0.15f;
static const float OFF_THRESH = 0.07f;
static const uint16_t RELEASE_MS = 40;
static const uint16_t TAP_MS = 200;
static const float SLIDE_MM = 3.0f;

// AVR only. The Mega's TWI pins idle high through the AVR's internal pull-ups, which sit on
// the 5 V rail. Disabling them leaves the breakout's own 3.3 V pull-ups in charge, so the bus
// never swings above 3.3 V. Set to 0 only if you fitted a bidirectional level shifter.
// Irrelevant on ESP32, whose pull-ups are already on 3.3 V.
#define DISABLE_INTERNAL_PULLUPS 1

// ---------------------------------------------------------------- MPR121 registers

static const uint8_t REG_TOUCHSTATUS = 0x00;
static const uint8_t REG_OORSTATUS = 0x02;
static const uint8_t REG_FILTDATA = 0x04;   // 2 bytes/electrode, LSB first, 10-bit
static const uint8_t REG_BASELINE = 0x1E;   // 1 byte/electrode, value << 2
static const uint8_t REG_MHDR = 0x2B;       // rising/falling/touched baseline filter block
static const uint8_t REG_TTH0 = 0x41;       // touch/release threshold pairs
static const uint8_t REG_DEBOUNCE = 0x5B;
static const uint8_t REG_CONFIG1 = 0x5C;    // FFI | CDC
static const uint8_t REG_CONFIG2 = 0x5D;    // CDT | SFI | ESI
static const uint8_t REG_ECR = 0x5E;        // CL | ELEPROX_EN | ELE_EN
static const uint8_t REG_CDC0 = 0x5F;       // per-electrode charge current (autoconfig writes these)
static const uint8_t REG_AUTOCFG0 = 0x7B;
static const uint8_t REG_AUTOCFG1 = 0x7C;
static const uint8_t REG_USL = 0x7D;
static const uint8_t REG_LSL = 0x7E;
static const uint8_t REG_TL = 0x7F;
static const uint8_t REG_SOFTRESET = 0x80;

static const uint8_t ECR_RUN = 0x0C;        // CL=00 (no on-chip baseline tracking), 12 electrodes

// Autoconfig targets for VDD = 3.3 V: USL = 256*(Vdd-0.7)/Vdd, TL = 0.9*USL, LSL = 0.65*USL.
static const uint8_t AC_USL = 202, AC_TL = 181, AC_LSL = 131;

// ---------------------------------------------------------------- runtime state

enum Mode { M_QUIET = 0, M_HEAT = 1, M_PLOT = 2, M_CSV = 3, M_RAW = 4 };

// Keep this ABOVE every function that names it. The Arduino IDE auto-generates prototypes and
// injects them just below the last preprocessor directive, so a type declared further down the
// .ino is not visible to its own generated prototype: "'Frame' does not name a type".
struct Frame {
  float total;      // sum of normalized channels ~ total force proxy
  float peak;       // strongest single channel
  uint8_t peak_ch;
  uint8_t active;   // channels above their gate
  float centroid;   // mm from the leftmost strip, valid only when active > 0
};

static uint8_t i2c_addr = 0;                // 0 = not found yet
static Mode mode = M_HEAT;
static uint16_t sample_hz = SAMPLE_HZ_DEFAULT;
static bool base_track = BASE_TRACK_DEFAULT;
static bool autocfg_enabled = true;
static uint16_t oor_flags = 0;              // autoconfig out-of-range, bit per electrode

static float base[N_CH];                    // software baseline, counts
static float sigma[N_CH];                   // idle noise, counts
static float gate[N_CH];                    // deadband, counts
static uint16_t span[N_CH];                 // counts of delta == norm 1.0
static uint16_t filt[N_CH];                 // last filtered read
static float norm[N_CH];

// calibration capture
static bool calibrating = false;
static uint32_t calib_until = 0;
static uint16_t calib_max[N_CH];

// contact / event tracking
static bool contact = false;
static uint32_t contact_t0 = 0, below_since = 0;
static float ev_peak = 0, ev_cen_min = 0, ev_cen_max = 0, ev_cen_first = 0;
static uint8_t ev_peak_ch = 0;

static const uint32_t EE_MAGIC = 0x45475431UL;  // "EGT1"
static const int EE_ADDR = 0;

// ---------------------------------------------------------------- I2C helpers

static bool mpr_write(uint8_t reg, uint8_t val) {
  Wire.beginTransmission(i2c_addr);
  Wire.write(reg);
  Wire.write(val);
  return Wire.endTransmission() == 0;
}

static bool mpr_read(uint8_t reg, uint8_t *buf, uint8_t n) {
  Wire.beginTransmission(i2c_addr);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom((int)i2c_addr, (int)n) != n) return false;
  for (uint8_t i = 0; i < n; i++) buf[i] = Wire.read();
  return true;
}

static bool mpr_read8(uint8_t reg, uint8_t &out) { return mpr_read(reg, &out, 1); }

// ---------------------------------------------------------------- MPR121 bring-up

// ADDR pin -> GND/VDD/SDA/SCL. Cheap breakouts usually hard-wire GND (0x5A) but not always.
static bool find_device() {
  for (uint8_t a = 0x5A; a <= 0x5D; a++) {
    Wire.beginTransmission(a);
    if (Wire.endTransmission() == 0) {
      i2c_addr = a;
      return true;
    }
  }
  i2c_addr = 0;
  return false;
}

// Full bus sweep, printed when the MPR121 is missing — distinguishes "nothing on the bus at
// all" (power/wiring) from "something is there but not where we expect" (ADDR strap).
static void bus_scan() {
  Serial.print(F("# I2C scan:"));
  uint8_t found = 0;
  for (uint8_t a = 0x08; a < 0x78; a++) {
    Wire.beginTransmission(a);
    if (Wire.endTransmission() == 0) {
      Serial.print(F(" 0x"));
      Serial.print(a, HEX);
      found++;
    }
  }
  if (!found) Serial.print(F(" nothing responding — check 3V3, GND, SDA/SCL and pull-ups"));
  Serial.println();
}

static void configure() {
  mpr_write(REG_SOFTRESET, 0x63);
  delay(2);
  mpr_write(REG_ECR, 0x00);  // stop mode — every other register is write-protected in run mode

  // Baseline filter block. Inert while CL=00, but left at sane values so that flipping the ECR
  // back to on-chip tracking (for a touch-style test) behaves.
  static const uint8_t bf[] = {0x01, 0x01, 0x00, 0x00,   // rising:  MHD NHD NCL FDL
                               0x01, 0x01, 0xFF, 0x02,   // falling: MHD NHD NCL FDL
                               0x00, 0x00, 0x00};        // touched: NHD NCL FDL
  for (uint8_t i = 0; i < sizeof(bf); i++) mpr_write(REG_MHDR + i, bf[i]);

  // On-chip touch/release thresholds are only a cross-check here; detection is in software.
  for (uint8_t i = 0; i < N_CH; i++) {
    mpr_write(REG_TTH0 + 2 * i, 12);
    mpr_write(REG_TTH0 + 2 * i + 1, 6);
  }
  mpr_write(REG_DEBOUNCE, 0x00);

  // CONFIG1 = FFI 6 samples | CDC 16 uA. CONFIG2 = CDT 0.5 us | SFI 4 samples | ESI 1 ms,
  // i.e. the fastest sample period the part offers — pressure transients are the point.
  mpr_write(REG_CONFIG1, 0x10);
  mpr_write(REG_CONFIG2, 0x20);

  // Autoconfig picks a per-electrode charge current/time so wildly different strip areas all
  // land mid-scale. It runs on entry to run mode, so it must be armed first.
  if (autocfg_enabled) {
    mpr_write(REG_USL, AC_USL);
    mpr_write(REG_TL, AC_TL);
    mpr_write(REG_LSL, AC_LSL);
    mpr_write(REG_AUTOCFG1, 0x00);
    mpr_write(REG_AUTOCFG0, 0x1B);  // FFI 6 | 2 retries | BVA=2 | ARE | ACE
  } else {
    mpr_write(REG_AUTOCFG0, 0x00);
  }

  mpr_write(REG_ECR, ECR_RUN);
  delay(30);  // let the filters fill before anyone reads them

  uint8_t lo = 0, hi = 0;
  mpr_read8(REG_OORSTATUS, lo);
  mpr_read8(REG_OORSTATUS + 1, hi);
  oor_flags = (uint16_t)lo | ((uint16_t)hi << 8);
}

static bool sample_filtered() {
  uint8_t b[N_CH * 2];
  if (!mpr_read(REG_FILTDATA, b, N_CH * 2)) return false;
  for (uint8_t i = 0; i < N_CH; i++)
    filt[i] = (uint16_t)b[2 * i] | ((uint16_t)(b[2 * i + 1] & 0x03) << 8);
  return true;
}

// ---------------------------------------------------------------- baseline / calibration

// Averages a quiet window into base[] and measures per-channel noise, which sets the deadband.
// Nothing may touch the pad while this runs.
static void capture_baseline(uint16_t n) {
  float sum[N_CH], sq[N_CH];
  for (uint8_t i = 0; i < N_CH; i++) { sum[i] = 0; sq[i] = 0; }

  uint16_t got = 0;
  for (uint16_t s = 0; s < n; s++) {
    if (!sample_filtered()) continue;
    for (uint8_t i = 0; i < N_CH; i++) {
      float v = filt[i];
      sum[i] += v;
      sq[i] += v * v;
    }
    got++;
    delay(5);
  }
  if (got == 0) {
    Serial.println(F("! baseline failed: no I2C reads"));
    return;
  }
  for (uint8_t i = 0; i < N_CH; i++) {
    base[i] = sum[i] / got;
    float var = sq[i] / got - base[i] * base[i];
    sigma[i] = var > 0 ? sqrt(var) : 0;
    gate[i] = max(NOISE_GATE_MIN, NOISE_GATE_SIGMA * sigma[i]);
  }
  Serial.print(F("# baseline over "));
  Serial.print(got);
  Serial.println(F(" samples"));
}

static void reset_spans() {
  for (uint8_t i = 0; i < N_CH; i++) span[i] = DEFAULT_SPAN;
}

static uint8_t ee_checksum(const uint16_t *v) {
  uint8_t c = 0;
  for (uint8_t i = 0; i < N_CH; i++) c += (uint8_t)(v[i] & 0xFF) + (uint8_t)(v[i] >> 8);
  return c;
}

static void spans_save() {
  int a = EE_ADDR;
  EEPROM.put(a, EE_MAGIC);
  a += sizeof(EE_MAGIC);
  for (uint8_t i = 0; i < N_CH; i++, a += 2) EEPROM.put(a, span[i]);
  EEPROM.put(a, ee_checksum(span));
  EE_COMMIT();
  Serial.println(F("# spans saved to EEPROM"));
}

static bool spans_load() {
  int a = EE_ADDR;
  uint32_t magic = 0;
  EEPROM.get(a, magic);
  if (magic != EE_MAGIC) return false;
  a += sizeof(EE_MAGIC);
  uint16_t tmp[N_CH];
  for (uint8_t i = 0; i < N_CH; i++, a += 2) EEPROM.get(a, tmp[i]);
  uint8_t crc = 0;
  EEPROM.get(a, crc);
  if (crc != ee_checksum(tmp)) return false;
  for (uint8_t i = 0; i < N_CH; i++) span[i] = tmp[i] ? tmp[i] : DEFAULT_SPAN;
  return true;
}

static void calib_start(uint16_t seconds) {
  for (uint8_t i = 0; i < N_CH; i++) calib_max[i] = 0;
  calibrating = true;
  calib_until = millis() + (uint32_t)seconds * 1000UL;
  Serial.print(F("# calibrating "));
  Serial.print(seconds);
  Serial.println(F("s — press EVERY pad as hard as you ever will, one after another"));
}

static void calib_finish() {
  calibrating = false;
  Serial.println(F("# calibration result (counts of full-scale delta):"));
  for (uint8_t i = 0; i < N_CH; i++) {
    span[i] = max((uint16_t)(gate[i] + 5.0f), calib_max[i]);
    Serial.print(F("#  ch"));
    Serial.print(i);
    Serial.print(F(" span "));
    Serial.print(span[i]);
    if (calib_max[i] < gate[i] + 5.0f) Serial.print(F("   <-- NO RESPONSE, check this strip"));
    Serial.println();
  }
  Serial.println(F("# 'w' saves these to EEPROM"));
}

// ---------------------------------------------------------------- per-frame derived values

static Frame compute() {
  Frame f = {0, 0, 0, 0, 0};
  float wsum = 0, wpos = 0;
  for (uint8_t i = 0; i < N_CH; i++) {
    float delta = base[i] - (float)filt[i];
    float n = 0;
    if (delta > gate[i]) {
      float denom = (float)span[i] - gate[i];
      n = denom > 1.0f ? (delta - gate[i]) / denom : 0;
      if (n > 1.0f) n = 1.0f;
      f.active++;
      wsum += n;
      wpos += n * (i * PITCH_MM);
    }

    // Baseline re-learning, in three bands. Tracking only while strictly idle was a mistake:
    // once slow drift (temperature, humidity, silicone creep) pushed delta past the gate,
    // tracking stopped and the channel sat on a phantom pressure that only grew.
    //   idle           -> re-learn at the normal rate
    //   below contact  -> still drift, not a press: leak it out, an order slower
    //   at/above ON    -> a real press, never tracked out
    if (base_track && !calibrating) {
      if (delta <= gate[i] && delta >= -gate[i])
        base[i] += ((float)filt[i] - base[i]) * BASE_TRACK_ALPHA;
      else if (n > 0 && n < ON_THRESH)
        base[i] += ((float)filt[i] - base[i]) * (BASE_TRACK_ALPHA * BASE_DRIFT_FRACTION);
    }
    norm[i] = n;
    f.total += n;
    if (n > f.peak) { f.peak = n; f.peak_ch = i; }
    if (calibrating && delta > calib_max[i]) calib_max[i] = (uint16_t)delta;
  }
  f.centroid = wsum > 0 ? wpos / wsum : 0;
  return f;
}

// ---------------------------------------------------------------- event classifier

static void update_events(const Frame &f) {
  uint32_t now = millis();

  if (!contact) {
    if (f.peak >= ON_THRESH) {
      contact = true;
      contact_t0 = now;
      below_since = 0;
      ev_peak = f.peak;
      ev_peak_ch = f.peak_ch;
      ev_cen_first = ev_cen_min = ev_cen_max = f.centroid;
    }
    return;
  }

  if (f.peak > ev_peak) { ev_peak = f.peak; ev_peak_ch = f.peak_ch; }
  if (f.active > 0) {
    if (f.centroid < ev_cen_min) ev_cen_min = f.centroid;
    if (f.centroid > ev_cen_max) ev_cen_max = f.centroid;
  }

  if (f.peak > OFF_THRESH) { below_since = 0; return; }
  if (below_since == 0) { below_since = now; return; }
  if (now - below_since < RELEASE_MS) return;

  uint32_t dur = below_since - contact_t0;
  float travel = ev_cen_max - ev_cen_min;
  contact = false;

  if (mode == M_PLOT || mode == M_CSV) return;  // keep machine-readable streams clean

  Serial.print(F("EVT "));
  if (travel >= SLIDE_MM) Serial.print(F("SLIDE"));
  else if (dur < TAP_MS) Serial.print(F("TAP  "));
  else Serial.print(F("PRESS"));
  Serial.print(F("  ch"));
  Serial.print(ev_peak_ch);
  Serial.print(F("  peak "));
  Serial.print(ev_peak, 2);
  Serial.print(F("  dur "));
  Serial.print(dur);
  Serial.print(F("ms  cen "));
  Serial.print(ev_cen_first, 1);
  Serial.print(F("->"));
  Serial.print(ev_cen_max >= ev_cen_first ? ev_cen_max : ev_cen_min, 1);
  Serial.print(F("mm"));
  if (ev_peak > 0.98f) Serial.print(F("  [SATURATED — raise span with 'c']"));
  Serial.println();
}

// ---------------------------------------------------------------- output

static const char RAMP[] = " .:-=+*x#%@";  // 11 levels, index = round(norm * 10)

static void print_heat(const Frame &f) {
  static uint8_t row = 0;
  if (row == 0) {
    Serial.println(F("     0 1 2 3 4 5 6 7 8 9 A B      total   centroid  n"));
  }
  row = (row + 1) % 20;

  Serial.print(F("    |"));
  for (uint8_t i = 0; i < N_CH; i++) {
    uint8_t idx = (uint8_t)(norm[i] * 10.0f + 0.5f);
    if (idx > 10) idx = 10;
    if (idx == 0 && norm[i] > 0) idx = 1;  // any signal above the gate gets a mark, not a blank
    Serial.print(RAMP[idx]);
    Serial.print(' ');
  }
  Serial.print(F("|  "));
  Serial.print(f.total, 2);
  Serial.print(F("     "));
  if (f.active) { Serial.print(f.centroid, 1); Serial.print(F("mm")); }
  else Serial.print(F("  -  "));
  Serial.print(F("    "));
  Serial.println(f.active);
}

static void print_plot() {
  for (uint8_t i = 0; i < N_CH; i++) {
    Serial.print(norm[i], 3);
    Serial.print(i + 1 < N_CH ? '\t' : '\n');
  }
}

static void print_csv() {
  Serial.print(micros());
  for (uint8_t i = 0; i < N_CH; i++) {
    Serial.print(',');
    Serial.print(norm[i], 3);
  }
  Serial.println();
}

static void print_raw(const Frame &f) {
  Serial.println(F("ch  filt  base   d   gate  span   norm"));
  for (uint8_t i = 0; i < N_CH; i++) {
    Serial.print(i < 10 ? F(" ") : F(""));
    Serial.print(i);
    Serial.print(F("  "));
    Serial.print(filt[i]);
    Serial.print(F("  "));
    Serial.print(base[i], 0);
    Serial.print(F("  "));
    Serial.print(base[i] - filt[i], 1);
    Serial.print(F("  "));
    Serial.print(gate[i], 1);
    Serial.print(F("   "));
    Serial.print(span[i]);
    Serial.print(F("   "));
    Serial.println(norm[i], 3);
  }
  Serial.print(F("total "));
  Serial.print(f.total, 2);
  Serial.print(F("  peak ch"));
  Serial.print(f.peak_ch);
  Serial.print(' ');
  Serial.println(f.peak, 2);
}

static void print_diag() {
  Serial.println(F("--- diagnostics ---"));
  Serial.print(F("addr 0x"));
  Serial.print(i2c_addr, HEX);
  Serial.print(F("  sample_hz "));
  Serial.print(sample_hz);
  Serial.print(F("  autoconfig "));
  Serial.print(autocfg_enabled ? F("on") : F("off"));
  Serial.print(F("  baseline_track "));
  Serial.println(base_track ? F("on") : F("off"));

  uint8_t cdc[N_CH];
  bool have_cdc = mpr_read(REG_CDC0, cdc, N_CH);
  uint8_t tl = 0, th = 0;
  mpr_read8(REG_TOUCHSTATUS, tl);
  mpr_read8(REG_TOUCHSTATUS + 1, th);

  Serial.println(F("ch  base  sigma  gate  span   cdc  flags"));
  for (uint8_t i = 0; i < N_CH; i++) {
    Serial.print(i < 10 ? F(" ") : F(""));
    Serial.print(i);
    Serial.print(F("  "));
    Serial.print(base[i], 0);
    Serial.print(F("   "));
    Serial.print(sigma[i], 2);
    Serial.print(F("   "));
    Serial.print(gate[i], 1);
    Serial.print(F("   "));
    Serial.print(span[i]);
    Serial.print(F("    "));
    Serial.print(have_cdc ? (cdc[i] & 0x3F) : 0);
    Serial.print(F("   "));
    if (oor_flags & (1 << i)) Serial.print(F("AUTOCFG_OOR "));
    if (base[i] < 60) Serial.print(F("BASE_LOW(shorted?) "));
    if (base[i] > 1000) Serial.print(F("BASE_HIGH(open?) "));
    if (sigma[i] > 6) Serial.print(F("NOISY "));
    if ((((uint16_t)tl | ((uint16_t)th << 8)) >> i) & 1) Serial.print(F("touched "));
    Serial.println();
  }
  if (th & 0x80) Serial.println(F("! OVCF set: overcurrent on the electrode pins"));
  Serial.println(F("-------------------"));
}

static void print_help() {
  Serial.println(F("--- egogrip tactile bench / MPR121 12-ch array ---"));
  Serial.println(F(" 1  heat map (default)   2  serial-plotter   3  csv stream"));
  Serial.println(F(" 4  raw counts           0  quiet (events only)"));
  Serial.println(F(" z  re-zero baseline (hands off!)   c  calibrate spans (8 s)"));
  Serial.println(F(" w  save spans to EEPROM  l  load  x  reset spans to default"));
  Serial.println(F(" a  re-run MPR121 autoconfig        t  toggle idle baseline tracking"));
  Serial.println(F(" d  diagnostics          +/-  sample rate         h  this help"));
}

// ---------------------------------------------------------------- commands

static void handle_serial() {
  while (Serial.available()) {
    char c = Serial.read();
    switch (c) {
      case '0': mode = M_QUIET; Serial.println(F("# quiet — events only")); break;
      case '1': mode = M_HEAT; break;
      case '2': mode = M_PLOT; break;
      case '3': mode = M_CSV; Serial.println(F("t_us,ch0,ch1,ch2,ch3,ch4,ch5,ch6,ch7,ch8,ch9,ch10,ch11")); break;
      case '4': mode = M_RAW; break;
      case 'z': Serial.println(F("# hands off...")); delay(500); capture_baseline(64); break;
      case 'c': calib_start(8); break;
      case 'w': spans_save(); break;
      case 'l': Serial.println(spans_load() ? F("# spans loaded") : F("# no valid EEPROM spans")); break;
      case 'x': reset_spans(); Serial.println(F("# spans reset to default")); break;
      case 'a': autocfg_enabled = true; configure(); capture_baseline(64); print_diag(); break;
      case 't': base_track = !base_track; Serial.print(F("# baseline tracking ")); Serial.println(base_track ? F("on") : F("off")); break;
      case 'd': print_diag(); break;
      case '+': if (sample_hz < 500) sample_hz += 50; Serial.println(sample_hz); break;
      case '-': if (sample_hz > 50) sample_hz -= 50; Serial.println(sample_hz); break;
      case 'h': case '?': print_help(); break;
      default: break;
    }
  }
}

// ---------------------------------------------------------------- Arduino entry

void setup() {
  Serial.begin(SERIAL_BAUD);
  delay(200);

  // Native USB-CDC (Pico, ESP32-S3) does not reset the board when the host opens the port, so
  // everything setup() prints — the address, the per-channel table — is gone before a monitor
  // can attach. Wait briefly for the host, but never block a headless boot.
#if defined(ARDUINO_ARCH_RP2040) || defined(ARDUINO_USB_CDC_ON_BOOT)
  for (uint32_t t0 = millis(); !Serial && millis() - t0 < 3000;) delay(10);
  delay(100);
#endif

#if defined(ARDUINO_ARCH_RP2040)
  EEPROM.begin(EE_SIZE);
  Wire.setSDA(I2C_SDA);   // must be set before begin() on the Arduino-Pico core
  Wire.setSCL(I2C_SCL);
  Wire.begin();
#elif defined(ARDUINO_ARCH_ESP32)
  EEPROM.begin(EE_SIZE);
  Wire.begin(I2C_SDA, I2C_SCL);
#else
  Wire.begin();
#if DISABLE_INTERNAL_PULLUPS
  // Wire.begin() enables the AVR's pull-ups to 5 V. Turn them off; the breakout pulls to 3.3 V.
  digitalWrite(SDA, LOW);
  digitalWrite(SCL, LOW);
#endif
#endif
  Wire.setClock(400000);

  reset_spans();
  for (uint8_t i = 0; i < N_CH; i++) { base[i] = 0; sigma[i] = 0; gate[i] = NOISE_GATE_MIN; }

  print_help();
  Serial.print(F("# I2C SDA="));
  Serial.print((int)I2C_SDA);
  Serial.print(F(" SCL="));
  Serial.println((int)I2C_SCL);

  if (!find_device()) {
    Serial.println(F("! no MPR121 found on 0x5A-0x5D"));
    bus_scan();
    return;
  }
  Serial.print(F("# MPR121 at 0x"));
  Serial.println(i2c_addr, HEX);

  configure();
  if (spans_load()) Serial.println(F("# spans restored from EEPROM"));
  Serial.println(F("# measuring baseline — do not touch the pad"));
  capture_baseline(64);
  print_diag();
}

void loop() {
  handle_serial();

  if (i2c_addr == 0) {  // keep hunting rather than pretending to have data
    static uint32_t retry = 0;
    if (millis() - retry > 2000) {
      retry = millis();
      if (find_device()) {
        Serial.print(F("# MPR121 found at 0x"));
        Serial.println(i2c_addr, HEX);
        configure();
        capture_baseline(64);
      }
    }
    return;
  }

  static uint32_t next_us = 0, next_print = 0;
  uint32_t now = micros();
  if ((int32_t)(now - next_us) < 0) return;
  next_us = now + 1000000UL / sample_hz;

  if (!sample_filtered()) {
    Serial.println(F("! I2C read failed"));
    i2c_addr = 0;
    return;
  }

  Frame f = compute();
  update_events(f);

  if (calibrating && (int32_t)(millis() - calib_until) >= 0) calib_finish();

  switch (mode) {
    case M_PLOT: print_plot(); break;
    case M_CSV: print_csv(); break;
    default:
      if ((int32_t)(now - next_print) >= 0) {
        next_print = now + 1000000UL / PRINT_HZ;
        if (mode == M_HEAT) print_heat(f);
        else if (mode == M_RAW) print_raw(f);
      }
      break;
  }
}
