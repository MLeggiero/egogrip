/*
 * egogrip gripper firmware — RP2040 (Arduino-Pico core) + AS5600 magnetic encoder.
 *
 * Streams gripper width over USB-CDC using the framed protocol (../../README.md). The AS5600 is
 * absolute only WITHIN one turn, and the jaw travel can span several turns, so we accumulate a
 * multi-turn `delta_counts` relative to a start-closed tare (the ZERO command). The host/pipeline
 * convert counts -> metres from calibration.json (docs/CALIBRATION.md) — firmware stays unit-free.
 *
 * Tier-1 timing: every packet carries micros(); there is no sync LED.
 * With no AS5600 attached it streams a synthetic sweep so a bare Pico still shows moving data.
 *
 * Build: see README.md (arduino-cli, board rp2040:rp2040:rpipico).
 */
#include <Wire.h>
#include <math.h>

// ---- framed protocol ----
static const uint8_t M0 = 0xAA, M1 = 0x55;
static const uint8_t T_STATE = 0x01, T_TACTILE = 0x02, T_SYNC = 0x03, T_INFO = 0x10;
static const uint8_t C_SET_RATE = 0x80, C_PULSE = 0x81, C_ZERO = 0x82;
static const char *FW_VERSION = "egogrip-gripper-arduino 0.2.0";

// ---- AS5600 ----
static const uint8_t AS5600_ADDR = 0x36;
static const uint8_t REG_RAWANGLE = 0x0C;  // 12-bit absolute within one turn
static const uint8_t REG_STATUS = 0x0B;    // MD (0x20) / ML (0x10) / MH (0x08)

// ---- config ----
#define I2C_SDA 4
#define I2C_SCL 5
static uint16_t state_hz = 200;
static int32_t trigger_counts = 200;  // |delta_counts| below this => "closed / grasping"

// ---- runtime state ----
static uint8_t seqno = 0;
static bool have_sensor = false;
static uint16_t raw_prev = 0;
static int64_t accum = 0;          // multi-turn accumulated counts
static int64_t accum_at_zero = 0;  // tare captured by ZERO at the closed reference

static uint8_t in_buf[64];
static uint8_t in_len = 0;

// ---------------------------------------------------------------- CRC + framing

static uint8_t crc8_update(uint8_t crc, const uint8_t *p, uint32_t n) {
  for (uint32_t i = 0; i < n; i++) {
    crc ^= p[i];
    for (int b = 0; b < 8; b++)
      crc = (crc & 0x80) ? (uint8_t)((crc << 1) ^ 0x07) : (uint8_t)(crc << 1);
  }
  return crc;
}

// Frame: AA 55 | type | seq | micros_u32 | len | payload | crc8(type..payload)
static void write_frame(uint8_t type, const uint8_t *payload, uint8_t len) {
  uint8_t hdr[7];
  uint32_t us = micros();
  hdr[0] = type;
  hdr[1] = seqno++;
  hdr[2] = us & 0xFF; hdr[3] = (us >> 8) & 0xFF;
  hdr[4] = (us >> 16) & 0xFF; hdr[5] = (us >> 24) & 0xFF;
  hdr[6] = len;
  uint8_t crc = crc8_update(0, hdr, 7);
  crc = crc8_update(crc, payload, len);
  Serial.write(M0); Serial.write(M1);
  Serial.write(hdr, 7);
  if (len) Serial.write(payload, len);
  Serial.write(crc);
}

// ---------------------------------------------------------------- AS5600

static bool as5600_read16(uint8_t reg, uint16_t &out) {
  Wire.beginTransmission(AS5600_ADDR);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom((int)AS5600_ADDR, 2) != 2) return false;
  uint8_t hi = Wire.read(), lo = Wire.read();
  out = (((uint16_t)hi << 8) | lo) & 0x0FFF;
  return true;
}

static bool as5600_read8(uint8_t reg, uint8_t &out) {
  Wire.beginTransmission(AS5600_ADDR);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) return false;
  if (Wire.requestFrom((int)AS5600_ADDR, 1) != 1) return false;
  out = Wire.read();
  return true;
}

static const char *magnet_status() {
  uint8_t s;
  if (!as5600_read8(REG_STATUS, s)) return "missing";
  if (!(s & 0x20)) return "missing";  // MD: magnet detected
  if (s & 0x10) return "weak";        // ML: too weak
  if (s & 0x08) return "strong";      // MH: too strong
  return "ok";
}

// ---------------------------------------------------------------- packets

static void send_info() {
  char buf[96];
  int n = snprintf(buf, sizeof(buf), "%s; width=%s; magnet=%s; state_hz=%u; tactile_ch=0",
                   FW_VERSION, have_sensor ? "as5600" : "synthetic", magnet_status(), state_hz);
  if (n < 0) n = 0;
  write_frame(T_INFO, (const uint8_t *)buf, (uint8_t)n);
}

static void send_state() {
  uint16_t raw = 0;
  if (have_sensor && as5600_read16(REG_RAWANGLE, raw)) {
    int16_t step = (int16_t)(raw - raw_prev);
    if (step > 2048) step -= 4096;
    else if (step < -2048) step += 4096;
    accum += step;
    raw_prev = raw;
  } else {
    // synthetic multi-turn sweep so a bare Pico still streams moving data
    float t = millis() / 1000.0f;
    float wn = 0.5f * (1.0f + sinf(2.0f * 3.14159265f * 0.3f * t));  // 0..1
    accum = (int64_t)(wn * 4000.0f);                                // 0..4000 counts
    raw = (uint16_t)(((accum % 4096) + 4096) % 4096);
  }
  int32_t delta = (int32_t)(accum - accum_at_zero);
  int32_t ad = delta < 0 ? -delta : delta;
  uint8_t trig = (ad < trigger_counts) ? 1 : 0;

  uint8_t p[7];
  p[0] = raw & 0xFF; p[1] = (raw >> 8) & 0xFF;
  p[2] = delta & 0xFF; p[3] = (delta >> 8) & 0xFF;
  p[4] = (delta >> 16) & 0xFF; p[5] = (delta >> 24) & 0xFF;
  p[6] = trig;
  write_frame(T_STATE, p, 7);
}

// ---------------------------------------------------------------- host commands

static void dispatch(uint8_t type, const uint8_t *p, uint8_t len) {
  if (type == C_SET_RATE && len >= 2) {
    state_hz = (uint16_t)(p[0] | (p[1] << 8));
    if (state_hz == 0) state_hz = 1;
    send_info();
  } else if (type == C_ZERO) {
    accum_at_zero = accum;  // tare at the closed reference
    send_info();
  }
  // C_PULSE intentionally ignored (Tier-1: no sync LED)
}

static void handle_commands() {
  while (Serial.available() > 0) {
    uint8_t b = (uint8_t)Serial.read();
    if (in_len < sizeof(in_buf)) {
      in_buf[in_len++] = b;
    } else {
      memmove(in_buf, in_buf + 1, sizeof(in_buf) - 1);
      in_buf[sizeof(in_buf) - 1] = b;
    }
    // try to parse one frame from the front of the buffer
    while (in_len >= 2 && !(in_buf[0] == M0 && in_buf[1] == M1)) {
      memmove(in_buf, in_buf + 1, --in_len);
    }
    if (in_len < 10) continue;  // magic(2)+type+seq+micros(4)+len = 9, need >=1 crc
    uint8_t len = in_buf[8];
    uint16_t total = 9 + len + 1;
    if (in_len < total) continue;
    uint8_t crc = crc8_update(0, in_buf + 2, 7 + len);  // type..payload
    bool ok = (crc == in_buf[total - 1]);
    if (ok) dispatch(in_buf[2], in_buf + 9, len);
    uint8_t adv = ok ? (uint8_t)total : 1;
    memmove(in_buf, in_buf + adv, in_len - adv);
    in_len -= adv;
  }
}

// ---------------------------------------------------------------- Arduino entry

void setup() {
  Serial.begin(115200);
  Wire.setSDA(I2C_SDA);
  Wire.setSCL(I2C_SCL);
  Wire.begin();
  Wire.setClock(400000);

  uint8_t s;
  have_sensor = as5600_read8(REG_STATUS, s) && (s & 0x20);
  uint16_t raw;
  if (have_sensor && as5600_read16(REG_RAWANGLE, raw)) raw_prev = raw;
  accum = 0;
  accum_at_zero = 0;
  send_info();
}

void loop() {
  handle_commands();
  static uint32_t next_us = 0;
  uint32_t now = micros();
  if ((int32_t)(now - next_us) >= 0) {
    next_us = now + 1000000UL / state_hz;
    send_state();
  }
}
