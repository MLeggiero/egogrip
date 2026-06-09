/*
 * egogrip gripper firmware (RP2040) — SKELETON
 * -------------------------------------------------------------
 * Streams gripper width + tactile over USB-CDC, with a per-packet
 * micros() timestamp for host-side clock alignment, plus an optional
 * sync LED. Protocol: see ../README.md.
 *
 * This is an annotated outline to implement against the Pico SDK.
 * It intentionally does not build yet (no CMakeLists / PIO program).
 */

#include <stdint.h>
/* #include "pico/stdlib.h"      */
/* #include "hardware/adc.h"     */
/* #include "hardware/pio.h"     */
/* #include "tusb.h"             // USB-CDC */

/* ---- protocol constants ---- */
#define PKT_MAGIC0 0xAA
#define PKT_MAGIC1 0x55
#define T_STATE    0x01
#define T_TACTILE  0x02
#define T_SYNC     0x03
#define T_INFO     0x10
#define C_SET_RATE 0x80
#define C_PULSE    0x81
#define C_ZERO     0x82

#define FW_VERSION "egogrip-gripper 0.1.0"

/* ---- config (overridable via SET_RATE) ---- */
static uint16_t state_hz   = 200;
static uint16_t tactile_hz = 500;

/* ---- hardware handles (TODO: init in setup) ---- */
/* PIO encoder, ADC mux channels, sync LED gpio */
#define SYNC_LED_GPIO 25
#define TACTILE_CHANNELS 4

static uint8_t crc8(const uint8_t *p, uint32_t n);                /* poly 0x07 */
static void usb_write_frame(uint8_t type, const uint8_t *payload, uint8_t len);
static int32_t read_encoder_counts(void);                        /* via PIO */
static void read_tactile(int16_t out[TACTILE_CHANNELS]);         /* via ADC  */
static uint8_t read_trigger(void);
static void handle_host_command(void);                           /* SET_RATE/PULSE/ZERO */
static uint32_t now_micros(void);                                /* time_us_32() */

/* Emit one STATE packet: width counts + trigger. */
static void send_state(void) {
    int32_t counts = read_encoder_counts();
    uint8_t trig   = read_trigger();
    uint8_t buf[5];
    /* little-endian i32 counts, then trigger */
    buf[0] = (uint8_t)(counts & 0xFF);
    buf[1] = (uint8_t)((counts >> 8) & 0xFF);
    buf[2] = (uint8_t)((counts >> 16) & 0xFF);
    buf[3] = (uint8_t)((counts >> 24) & 0xFF);
    buf[4] = trig;
    usb_write_frame(T_STATE, buf, sizeof(buf));
}

/* Emit one TACTILE packet: n channels of i16. */
static void send_tactile(void) {
    int16_t ch[TACTILE_CHANNELS];
    read_tactile(ch);
    uint8_t buf[1 + 2 * TACTILE_CHANNELS];
    buf[0] = TACTILE_CHANNELS;
    for (int i = 0; i < TACTILE_CHANNELS; i++) {
        buf[1 + 2 * i] = (uint8_t)(ch[i] & 0xFF);
        buf[2 + 2 * i] = (uint8_t)((ch[i] >> 8) & 0xFF);
    }
    usb_write_frame(T_TACTILE, buf, sizeof(buf));
}

/* Fire the sync LED and emit a paired SYNC packet at the same instant. */
static void fire_sync_pulse(uint32_t event_id) {
    /* gpio_put(SYNC_LED_GPIO, 1); */
    uint8_t buf[4] = {
        (uint8_t)(event_id & 0xFF), (uint8_t)((event_id >> 8) & 0xFF),
        (uint8_t)((event_id >> 16) & 0xFF), (uint8_t)((event_id >> 24) & 0xFF)};
    usb_write_frame(T_SYNC, buf, sizeof(buf)); /* timestamp captured inside */
    /* hold LED ~a few ms, then gpio_put(SYNC_LED_GPIO, 0) on a timer */
}

int main(void) {
    /* stdio_init_all(); init PIO encoder, ADC, LED gpio, USB-CDC */
    /* usb_write_frame(T_INFO, (const uint8_t*)FW_VERSION, ...);  on connect */

    uint64_t next_state_us   = 0;
    uint64_t next_tactile_us = 0;

    for (;;) {
        handle_host_command();
        uint32_t t = now_micros();
        if (t >= next_state_us)   { send_state();   next_state_us   = t + 1000000u / state_hz; }
        if (t >= next_tactile_us) { send_tactile(); next_tactile_us = t + 1000000u / tactile_hz; }
        /* tud_task(); */
    }
}

/*
 * usb_write_frame: build [AA 55 | type | seq | micros_u32 | len | payload | crc8]
 * with micros captured as late as possible before the CDC write so the host
 * timestamp and MCU timestamp refer to nearly the same instant.
 * (Implementation TODO.)
 */
