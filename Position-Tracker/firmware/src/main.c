// IMU streamer for the Adafruit Feather ESP32-S3 Reverse TFT.
//
// Auto-detects an I2C IMU on the STEMMA QT port (address 0x68 or 0x69) and
// streams samples over Bluetooth LE (see ble_imu.h) and, when a USB host is
// connected, over the USB serial port as text lines:
//
//   D,<t_us>,<ax>,<ay>,<az>,<gx>,<gy>,<gz>     accel in m/s^2, gyro in deg/s
//   E,recenter,<count>                         D1 button pressed
//   S,<buttons>,<recenter_count>,<mV>,<pct>    status, every 2 s (pct 255 = no gauge)
//   #<info>                                    status / metadata lines
//
// Pressing the D1 button asks the host to re-center the position estimate.
//
// Supported chips: ICM-20948, ICM-20649, MPU-6050/6500/9250, ICM-42688-P, BMI160.

#include <stdio.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/gpio.h"
#include "driver/i2c_master.h"
#include "esp_timer.h"
#include "esp_log.h"
#include "driver/usb_serial_jtag.h"
#include "ble_imu.h"

#define PIN_SDA           3
#define PIN_SCL           4
#define PIN_I2C_POWER     7   // powers the STEMMA QT port and TFT on this board
#define I2C_FREQ_HZ       400000
#define SAMPLE_RATE_HZ    100

#define G_TO_MS2          9.80665f

// Front buttons: D0 is pulled up (pressed = low); D1/D2 are pulled down (pressed = high).
#define PIN_BTN_D0        0
#define PIN_BTN_D1        1   // re-center button
#define PIN_BTN_D2        2
#define BTN_DEBOUNCE      3   // samples a button must stay changed

#define MAX17048_ADDR     0x36   // on-board LiPo fuel gauge
#define BATTERY_PERIOD    (2 * SAMPLE_RATE_HZ)   // read every 2 s

typedef enum { IMU_NONE, IMU_ICM20948, IMU_ICM20649, IMU_MPU6XXX, IMU_ICM42688, IMU_BMI160 } imu_type_t;

static i2c_master_bus_handle_t s_bus;
static i2c_master_dev_handle_t s_dev;
static imu_type_t s_type = IMU_NONE;
static float s_accel_lsb_per_g;
static float s_gyro_lsb_per_dps;
static TaskHandle_t s_sample_task;
static i2c_master_dev_handle_t s_gauge;   // NULL if no fuel gauge

static esp_err_t reg_write(uint8_t reg, uint8_t val)
{
    uint8_t buf[2] = { reg, val };
    return i2c_master_transmit(s_dev, buf, sizeof(buf), 50);
}

static esp_err_t reg_read(uint8_t reg, uint8_t *out, size_t len)
{
    return i2c_master_transmit_receive(s_dev, &reg, 1, out, len, 50);
}

static uint8_t reg_read_u8(uint8_t reg)
{
    uint8_t v = 0xFF;
    reg_read(reg, &v, 1);
    return v;
}

static void delay_ms(uint32_t ms)
{
    vTaskDelay(pdMS_TO_TICKS(ms) ? pdMS_TO_TICKS(ms) : 1);
}

// ---- Chip init routines ----------------------------------------------------

// ICM-20948 and ICM-20649 share a register map (banked, bank select at 0x7F).
static void icm20x_init(void)
{
    reg_write(0x7F, 0x00);          // bank 0
    reg_write(0x06, 0x80);          // PWR_MGMT_1: device reset
    delay_ms(100);
    reg_write(0x7F, 0x00);
    reg_write(0x06, 0x01);          // wake, auto-select clock
    reg_write(0x07, 0x00);          // PWR_MGMT_2: accel + gyro on
    delay_ms(20);

    reg_write(0x7F, 0x20);          // bank 2
    reg_write(0x00, 4);             // GYRO_SMPLRT_DIV: 1125/(1+4) = 225 Hz
    reg_write(0x01, (3 << 3) | (1 << 1) | 1);   // GYRO_CONFIG_1: DLPF 3, FS_SEL 1, DLPF on
    reg_write(0x10, 0);             // ACCEL_SMPLRT_DIV_1
    reg_write(0x11, 4);             // ACCEL_SMPLRT_DIV_2: 225 Hz
    reg_write(0x14, (3 << 3) | (1 << 1) | 1);   // ACCEL_CONFIG: DLPF 3, FS_SEL 1, DLPF on
    reg_write(0x7F, 0x00);          // back to bank 0

    if (s_type == IMU_ICM20948) {   // FS_SEL 1 = +/-4 g, +/-500 dps
        s_accel_lsb_per_g = 8192.0f;
        s_gyro_lsb_per_dps = 65.5f;
    } else {                        // ICM-20649 FS_SEL 1 = +/-8 g, +/-1000 dps
        s_accel_lsb_per_g = 4096.0f;
        s_gyro_lsb_per_dps = 32.8f;
    }
}

static void mpu6xxx_init(void)
{
    reg_write(0x6B, 0x80);          // PWR_MGMT_1: reset
    delay_ms(100);
    reg_write(0x6B, 0x01);          // wake, PLL clock
    reg_write(0x6C, 0x00);          // PWR_MGMT_2: all axes on
    reg_write(0x19, 4);             // SMPLRT_DIV: 1 kHz/(1+4) = 200 Hz
    reg_write(0x1A, 0x03);          // CONFIG: DLPF ~44 Hz
    reg_write(0x1B, 0x08);          // GYRO_CONFIG: +/-500 dps
    reg_write(0x1C, 0x08);          // ACCEL_CONFIG: +/-4 g
    delay_ms(20);
    s_accel_lsb_per_g = 8192.0f;
    s_gyro_lsb_per_dps = 65.5f;
}

static void icm42688_init(void)
{
    reg_write(0x76, 0x00);          // REG_BANK_SEL: bank 0
    reg_write(0x11, 0x01);          // DEVICE_CONFIG: soft reset
    delay_ms(10);
    reg_write(0x4F, (2 << 5) | 0x07);   // GYRO_CONFIG0: +/-500 dps, 200 Hz
    reg_write(0x50, (2 << 5) | 0x07);   // ACCEL_CONFIG0: +/-4 g, 200 Hz
    reg_write(0x4E, 0x0F);          // PWR_MGMT0: gyro + accel low-noise mode
    delay_ms(50);
    s_accel_lsb_per_g = 8192.0f;
    s_gyro_lsb_per_dps = 65.5f;
}

static void bmi160_init(void)
{
    reg_write(0x7E, 0xB6);          // CMD: soft reset
    delay_ms(100);
    reg_read_u8(0x7F);              // dummy read (harmless on I2C)
    reg_write(0x7E, 0x11);          // accel -> normal mode
    delay_ms(10);
    reg_write(0x7E, 0x15);          // gyro -> normal mode
    delay_ms(100);
    reg_write(0x40, 0x29);          // ACC_CONF: 200 Hz, normal filter
    reg_write(0x41, 0x05);          // ACC_RANGE: +/-4 g
    reg_write(0x42, 0x29);          // GYR_CONF: 200 Hz
    reg_write(0x43, 0x02);          // GYR_RANGE: +/-500 dps
    s_accel_lsb_per_g = 8192.0f;
    s_gyro_lsb_per_dps = 65.6f;
}

// ---- Detection ---------------------------------------------------------------

static const char *imu_name(imu_type_t t)
{
    switch (t) {
    case IMU_ICM20948: return "ICM-20948";
    case IMU_ICM20649: return "ICM-20649";
    case IMU_MPU6XXX:  return "MPU-6050/6500/9250";
    case IMU_ICM42688: return "ICM-42688-P";
    case IMU_BMI160:   return "BMI160";
    default:           return "none";
    }
}

static imu_type_t identify(uint8_t *who0, uint8_t *who75)
{
    *who0 = reg_read_u8(0x00);
    *who75 = reg_read_u8(0x75);
    if (*who0 == 0xEA) return IMU_ICM20948;
    if (*who0 == 0xE1) return IMU_ICM20649;
    if (*who0 == 0xD1) return IMU_BMI160;
    if (*who75 == 0x47) return IMU_ICM42688;
    if (*who75 == 0x68 || *who75 == 0x70 || *who75 == 0x71 ||
        *who75 == 0x73 || *who75 == 0x74 || *who75 == 0x98) return IMU_MPU6XXX;
    return IMU_NONE;
}

static bool detect_imu(void)
{
    static const uint8_t addrs[] = { 0x69, 0x68 };
    for (size_t i = 0; i < sizeof(addrs); i++) {
        if (i2c_master_probe(s_bus, addrs[i], 50) != ESP_OK) {
            continue;
        }
        i2c_device_config_t cfg = {
            .dev_addr_length = I2C_ADDR_BIT_LEN_7,
            .device_address = addrs[i],
            .scl_speed_hz = I2C_FREQ_HZ,
        };
        ESP_ERROR_CHECK(i2c_master_bus_add_device(s_bus, &cfg, &s_dev));
        uint8_t w0, w75;
        s_type = identify(&w0, &w75);
        printf("#probe addr=0x%02X reg00=0x%02X reg75=0x%02X -> %s\n", addrs[i], w0, w75, imu_name(s_type));
        if (s_type != IMU_NONE) {
            printf("#imu %s addr=0x%02X rate=%d\n", imu_name(s_type), addrs[i], SAMPLE_RATE_HZ);
            return true;
        }
        i2c_master_bus_rm_device(s_dev);
        s_dev = NULL;
    }
    return false;
}

// ---- Sampling ------------------------------------------------------------------

static inline int16_t be16(const uint8_t *p) { return (int16_t)((p[0] << 8) | p[1]); }
static inline int16_t le16(const uint8_t *p) { return (int16_t)((p[1] << 8) | p[0]); }

// Reads raw accel[3] and gyro[3].
static esp_err_t read_raw(int16_t a[3], int16_t g[3])
{
    uint8_t b[14];
    esp_err_t err;
    switch (s_type) {
    case IMU_ICM20948:
    case IMU_ICM20649:
        err = reg_read(0x2D, b, 12);
        for (int i = 0; i < 3; i++) { a[i] = be16(&b[2 * i]); g[i] = be16(&b[6 + 2 * i]); }
        return err;
    case IMU_MPU6XXX:
        err = reg_read(0x3B, b, 14);   // accel, temp, gyro
        for (int i = 0; i < 3; i++) { a[i] = be16(&b[2 * i]); g[i] = be16(&b[8 + 2 * i]); }
        return err;
    case IMU_ICM42688:
        err = reg_read(0x1F, b, 12);
        for (int i = 0; i < 3; i++) { a[i] = be16(&b[2 * i]); g[i] = be16(&b[6 + 2 * i]); }
        return err;
    case IMU_BMI160:
        err = reg_read(0x0C, b, 12);   // gyro, then accel (little endian)
        for (int i = 0; i < 3; i++) { g[i] = le16(&b[2 * i]); a[i] = le16(&b[6 + 2 * i]); }
        return err;
    default:
        return ESP_ERR_INVALID_STATE;
    }
}

// ---- Buttons and battery -------------------------------------------------------

static void buttons_init(void)
{
    gpio_config_t cfg = {
        .pin_bit_mask = 1ULL << PIN_BTN_D0,
        .mode = GPIO_MODE_INPUT,
        .pull_up_en = GPIO_PULLUP_ENABLE,
    };
    gpio_config(&cfg);
    cfg.pin_bit_mask = (1ULL << PIN_BTN_D1) | (1ULL << PIN_BTN_D2);
    cfg.pull_up_en = GPIO_PULLUP_DISABLE;
    cfg.pull_down_en = GPIO_PULLDOWN_ENABLE;
    gpio_config(&cfg);
}

// Raw pressed state, bit0 = D0, bit1 = D1, bit2 = D2.
static uint8_t buttons_read(void)
{
    return (gpio_get_level(PIN_BTN_D0) == 0 ? 1 : 0) |
           (gpio_get_level(PIN_BTN_D1) ? 2 : 0) |
           (gpio_get_level(PIN_BTN_D2) ? 4 : 0);
}

static void gauge_init(void)
{
    if (i2c_master_probe(s_bus, MAX17048_ADDR, 50) != ESP_OK) {
        printf("#battery gauge not found\n");
        return;
    }
    i2c_device_config_t cfg = {
        .dev_addr_length = I2C_ADDR_BIT_LEN_7,
        .device_address = MAX17048_ADDR,
        .scl_speed_hz = I2C_FREQ_HZ,
    };
    ESP_ERROR_CHECK(i2c_master_bus_add_device(s_bus, &cfg, &s_gauge));
}

static void gauge_read(uint16_t *mv, uint8_t *pct)
{
    uint8_t reg = 0x02, b[4];    // VCELL (0x02) and SOC (0x04), big endian
    *mv = 0;
    *pct = 0xFF;
    if (s_gauge == NULL || i2c_master_transmit_receive(s_gauge, &reg, 1, b, 4, 50) != ESP_OK) {
        return;
    }
    *mv = (uint16_t)((((b[0] << 8) | b[1]) * 78125UL) / 1000000UL);   // 78.125 uV/LSB
    *pct = b[2] > 100 ? 100 : b[2];
}

// ---- Sampling loop -------------------------------------------------------------

static void sample_timer_cb(void *arg)
{
    xTaskNotifyGive(s_sample_task);
}

static void sample_task(void *arg)
{
    const float a_scale = G_TO_MS2 / s_accel_lsb_per_g;
    const float g_scale = 1.0f / s_gyro_lsb_per_dps;
    int16_t a[3], g[3];
    float af[3], gf[3];
    uint32_t errors = 0;
    uint32_t tick = 0;
    uint8_t buttons = 0, btn_raw_prev = 0, btn_stable_n = 0;
    uint16_t recenter_count = 0, batt_mv = 0;
    uint8_t batt_pct = 0xFF;

    gauge_read(&batt_mv, &batt_pct);

    for (;;) {
        ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
        int64_t t = esp_timer_get_time();
        bool usb = usb_serial_jtag_is_connected();

        // Debounced buttons; a D1 press requests a re-center on the host.
        uint8_t raw = buttons_read();
        btn_stable_n = (raw == btn_raw_prev) ? btn_stable_n + 1 : 0;
        btn_raw_prev = raw;
        if (btn_stable_n == BTN_DEBOUNCE && raw != buttons) {
            if ((raw & 2) && !(buttons & 2)) {
                recenter_count++;
                if (usb) {
                    printf("E,recenter,%u\n", recenter_count);
                }
            }
            buttons = raw;
        }
        if (++tick % BATTERY_PERIOD == 0) {
            gauge_read(&batt_mv, &batt_pct);
        }
        ble_imu_set_status(buttons, recenter_count, batt_mv, batt_pct);

        if (read_raw(a, g) != ESP_OK) {
            if ((errors++ % SAMPLE_RATE_HZ) == 0 && usb) {
                printf("#error i2c read failed (%lu)\n", (unsigned long)errors);
            }
            continue;
        }
        for (int i = 0; i < 3; i++) {
            af[i] = a[i] * a_scale;
            gf[i] = g[i] * g_scale;
        }
        ble_imu_push(t, af, gf);
        // Only write to USB when a host is attached; otherwise writes would
        // stall waiting on the USB FIFO (e.g. when running on battery).
        if (usb) {
            if (tick % BATTERY_PERIOD == 0) {
                printf("S,%u,%u,%u,%u\n", buttons, recenter_count, batt_mv, batt_pct);
            }
            printf("D,%lld,%.4f,%.4f,%.4f,%.3f,%.3f,%.3f\n", t,
                   af[0], af[1], af[2], gf[0], gf[1], gf[2]);
            fflush(stdout);
        }
    }
}

void app_main(void)
{
    esp_log_level_set("*", ESP_LOG_WARN);   // keep the stream clean

    // Power-cycle the STEMMA QT port so the sensor starts from a known state.
    gpio_reset_pin(PIN_I2C_POWER);
    gpio_set_direction(PIN_I2C_POWER, GPIO_MODE_OUTPUT);
    gpio_set_level(PIN_I2C_POWER, 0);
    delay_ms(100);
    gpio_set_level(PIN_I2C_POWER, 1);
    delay_ms(100);

    i2c_master_bus_config_t bus_cfg = {
        .i2c_port = I2C_NUM_0,
        .sda_io_num = PIN_SDA,
        .scl_io_num = PIN_SCL,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    ESP_ERROR_CHECK(i2c_new_master_bus(&bus_cfg, &s_bus));

    while (!detect_imu()) {
        printf("#no supported IMU found at 0x68/0x69; I2C scan:");
        for (uint8_t addr = 0x08; addr < 0x78; addr++) {
            if (i2c_master_probe(s_bus, addr, 20) == ESP_OK) {
                printf(" 0x%02X", addr);
            }
        }
        printf("\n");
        fflush(stdout);
        delay_ms(2000);
    }

    switch (s_type) {
    case IMU_ICM20948:
    case IMU_ICM20649: icm20x_init(); break;
    case IMU_MPU6XXX:  mpu6xxx_init(); break;
    case IMU_ICM42688: icm42688_init(); break;
    case IMU_BMI160:   bmi160_init(); break;
    default: break;
    }
    printf("#columns t_us,ax,ay,az,gx,gy,gz (m/s^2, deg/s)\n");

    buttons_init();
    gauge_init();

    static char info[96];
    snprintf(info, sizeof(info), "imu=%s;rate=%d;accel=1/500 m/s^2;gyro=1/50 deg/s",
             imu_name(s_type), SAMPLE_RATE_HZ);
    ble_imu_init(info);
    printf("#ble advertising as %s\n", BLE_IMU_DEVICE_NAME);
    fflush(stdout);

    xTaskCreate(sample_task, "imu", 4096, NULL, 5, &s_sample_task);

    const esp_timer_create_args_t targs = { .callback = sample_timer_cb, .name = "imu_tick" };
    esp_timer_handle_t timer;
    ESP_ERROR_CHECK(esp_timer_create(&targs, &timer));
    ESP_ERROR_CHECK(esp_timer_start_periodic(timer, 1000000 / SAMPLE_RATE_HZ));
}
