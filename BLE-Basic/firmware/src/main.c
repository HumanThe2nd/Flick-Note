// BLE-Basic
// =========
// Board:  Adafruit Feather ESP32-S3 Reverse TFT
// Sensor: ICM-20948 9-DoF IMU on the STEMMA QT connector (I2C address 0x69)
//
// What it does, 100 times per second:
//   1. Reads acceleration, angular velocity (gyro), magnetic field and
//      temperature from the ICM-20948.
//   2. Broadcasts the reading over Bluetooth Low Energy (BLE) to any connected
//      computer. No USB connection is needed; run it from a LiPo battery.
//   3. If a USB cable is attached to a computer, also prints a readable line
//      10 times per second (open the COM port at any baud rate to see it).
//
// The red LED (next to the USB port) is ON while a computer is receiving data.
//
// BLE layout (what the PC program connects to):
//   Device name  "IMU-Basic"
//   Service      12340000-0000-4000-8000-00805f9b0001
//   Readings     12340001-0000-4000-8000-00805f9b0001   (read + notify)
//     44 bytes, little endian, one reading per notification:
//       uint32 time_ms                 milliseconds since the board started
//       float  ax, ay, az              acceleration          m/s^2
//       float  gx, gy, gz              angular velocity      deg/s
//       float  mx, my, mz              magnetic field        microtesla (uT)
//       float  temp_c                  sensor temperature    deg C
//   All three sensors use the same axes (the ones printed on the sensor board).

#include <stdio.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "driver/gpio.h"
#include "driver/i2c_master.h"
#include "driver/usb_serial_jtag.h"
#include "esp_timer.h"
#include "esp_log.h"
#include "nvs_flash.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "host/ble_hs.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"

// ---------------------------------------------------------------------------
// Board pins
// ---------------------------------------------------------------------------
#define PIN_I2C_SDA       3
#define PIN_I2C_SCL       4
#define PIN_I2C_POWER     7    // must be HIGH to power the STEMMA QT connector
#define PIN_RED_LED       13

#define SAMPLE_PERIOD_MS  10   // 100 readings per second
#define BLE_DEVICE_NAME   "IMU-Basic"

// ---------------------------------------------------------------------------
// ICM-20948 registers (datasheet section 7). The chip has 4 register "banks";
// writing REG_BANK_SEL chooses which bank the other addresses refer to.
// ---------------------------------------------------------------------------
#define ICM_ADDR            0x69
#define ICM_REG_BANK_SEL    0x7F
// bank 0
#define ICM_WHO_AM_I        0x00   // always reads 0xEA
#define ICM_USER_CTRL       0x03
#define ICM_PWR_MGMT_1      0x06
#define ICM_PWR_MGMT_2      0x07
#define ICM_INT_PIN_CFG     0x0F
#define ICM_ACCEL_XOUT_H    0x2D   // accel x,y,z, gyro x,y,z, temp: 14 bytes in a row
// bank 2
#define ICM_GYRO_SMPLRT_DIV 0x00
#define ICM_GYRO_CONFIG_1   0x01
#define ICM_ACCEL_SMPLRT_DIV_2 0x11
#define ICM_ACCEL_CONFIG    0x14

// AK09916 magnetometer: a second chip inside the ICM-20948. With the ICM's
// "bypass" mode on, it appears directly on the I2C bus at address 0x0C.
#define MAG_ADDR            0x0C
#define MAG_WIA2            0x01   // always reads 0x09
#define MAG_ST1             0x10   // bit 0 = new data ready
#define MAG_CNTL2           0x31
#define MAG_CNTL3           0x32

// Conversion factors for the ranges configured in imu_init():
#define ACCEL_LSB_PER_G     8192.0f    // +/-4 g range
#define GYRO_LSB_PER_DPS    65.5f      // +/-500 deg/s range
#define MAG_UT_PER_LSB      0.15f      // fixed
#define STANDARD_GRAVITY    9.80665f

static const char *TAG = "imu";

// One complete reading. Sent over BLE exactly as laid out here.
typedef struct __attribute__((packed)) {
    uint32_t time_ms;
    float accel[3];   // m/s^2
    float gyro[3];    // deg/s
    float mag[3];     // uT
    float temp_c;
} imu_reading_t;

static i2c_master_bus_handle_t s_bus;
static i2c_master_dev_handle_t s_icm;
static i2c_master_dev_handle_t s_mag;

// Latest reading, shared between the sensor task and the BLE task.
static imu_reading_t s_latest;
static portMUX_TYPE s_latest_lock = portMUX_INITIALIZER_UNLOCKED;

// BLE state
static uint16_t s_readings_handle;   // filled in by NimBLE when the service registers
static uint8_t s_own_addr_type;
static int s_subscribers;            // computers currently receiving notifications

// ===========================================================================
// I2C helpers
// ===========================================================================

static esp_err_t reg_write(i2c_master_dev_handle_t dev, uint8_t reg, uint8_t value)
{
    uint8_t buf[2] = { reg, value };
    return i2c_master_transmit(dev, buf, 2, 100);
}

static esp_err_t reg_read(i2c_master_dev_handle_t dev, uint8_t reg, uint8_t *out, size_t len)
{
    return i2c_master_transmit_receive(dev, &reg, 1, out, len, 100);
}

static i2c_master_dev_handle_t add_device(uint8_t addr)
{
    i2c_device_config_t cfg = {
        .dev_addr_length = I2C_ADDR_BIT_LEN_7,
        .device_address = addr,
        .scl_speed_hz = 400000,
    };
    i2c_master_dev_handle_t dev;
    ESP_ERROR_CHECK(i2c_master_bus_add_device(s_bus, &cfg, &dev));
    return dev;
}

// ===========================================================================
// Sensor setup and reading
// ===========================================================================

static void i2c_init(void)
{
    // Power-cycle the STEMMA QT port so the sensor starts fresh.
    gpio_reset_pin(PIN_I2C_POWER);
    gpio_set_direction(PIN_I2C_POWER, GPIO_MODE_OUTPUT);
    gpio_set_level(PIN_I2C_POWER, 0);
    vTaskDelay(pdMS_TO_TICKS(100));
    gpio_set_level(PIN_I2C_POWER, 1);
    vTaskDelay(pdMS_TO_TICKS(100));

    i2c_master_bus_config_t bus_cfg = {
        .i2c_port = I2C_NUM_0,
        .sda_io_num = PIN_I2C_SDA,
        .scl_io_num = PIN_I2C_SCL,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    ESP_ERROR_CHECK(i2c_new_master_bus(&bus_cfg, &s_bus));
}

// Returns true if the sensor answered correctly.
static bool imu_init(void)
{
    s_icm = add_device(ICM_ADDR);

    uint8_t who = 0;
    reg_write(s_icm, ICM_REG_BANK_SEL, 0x00);
    if (reg_read(s_icm, ICM_WHO_AM_I, &who, 1) != ESP_OK || who != 0xEA) {
        ESP_LOGE(TAG, "ICM-20948 not found at 0x%02X (WHO_AM_I=0x%02X)", ICM_ADDR, who);
        return false;
    }

    // Reset, then wake up with the best available clock.
    reg_write(s_icm, ICM_PWR_MGMT_1, 0x80);
    vTaskDelay(pdMS_TO_TICKS(100));
    reg_write(s_icm, ICM_REG_BANK_SEL, 0x00);
    reg_write(s_icm, ICM_PWR_MGMT_1, 0x01);
    reg_write(s_icm, ICM_PWR_MGMT_2, 0x00);          // all accel + gyro axes on
    vTaskDelay(pdMS_TO_TICKS(20));

    // Ranges and filters live in bank 2.
    reg_write(s_icm, ICM_REG_BANK_SEL, 0x20);
    reg_write(s_icm, ICM_GYRO_SMPLRT_DIV, 4);        // 1125 Hz / (1+4) = 225 Hz
    reg_write(s_icm, ICM_GYRO_CONFIG_1, 0x1B);       // +/-500 deg/s, low-pass filter ~50 Hz
    reg_write(s_icm, ICM_ACCEL_SMPLRT_DIV_2, 4);     // 225 Hz
    reg_write(s_icm, ICM_ACCEL_CONFIG, 0x1B);        // +/-4 g, low-pass filter ~50 Hz
    reg_write(s_icm, ICM_REG_BANK_SEL, 0x00);

    // Bypass mode: connect the magnetometer straight to our I2C bus.
    reg_write(s_icm, ICM_USER_CTRL, 0x00);           // ICM's own I2C master off
    reg_write(s_icm, ICM_INT_PIN_CFG, 0x02);         // BYPASS_EN
    vTaskDelay(pdMS_TO_TICKS(10));

    s_mag = add_device(MAG_ADDR);
    uint8_t wia2 = 0;
    if (reg_read(s_mag, MAG_WIA2, &wia2, 1) != ESP_OK || wia2 != 0x09) {
        ESP_LOGE(TAG, "magnetometer not found (WIA2=0x%02X)", wia2);
        return false;
    }
    reg_write(s_mag, MAG_CNTL3, 0x01);               // soft reset
    vTaskDelay(pdMS_TO_TICKS(10));
    reg_write(s_mag, MAG_CNTL2, 0x08);               // continuous measurement, 100 Hz
    return true;
}

static int16_t big_endian_16(const uint8_t *p)    { return (int16_t)((p[0] << 8) | p[1]); }
static int16_t little_endian_16(const uint8_t *p) { return (int16_t)((p[1] << 8) | p[0]); }

// Fills in r with a fresh reading. The magnetometer only updates when it has
// new data, so r->mag keeps its previous value otherwise.
static esp_err_t imu_read(imu_reading_t *r)
{
    uint8_t b[14];
    esp_err_t err = reg_read(s_icm, ICM_ACCEL_XOUT_H, b, sizeof(b));
    if (err != ESP_OK) {
        return err;
    }
    for (int i = 0; i < 3; i++) {
        r->accel[i] = big_endian_16(&b[2 * i]) / ACCEL_LSB_PER_G * STANDARD_GRAVITY;
        r->gyro[i] = big_endian_16(&b[6 + 2 * i]) / GYRO_LSB_PER_DPS;
    }
    r->temp_c = big_endian_16(&b[12]) / 333.87f + 21.0f;    // datasheet formula

    // Magnetometer: ST1, X, Y, Z (little endian), a dummy byte, then ST2.
    // Reading through ST2 tells the chip we're done with this sample.
    uint8_t m[9];
    if (reg_read(s_mag, MAG_ST1, m, sizeof(m)) == ESP_OK && (m[0] & 0x01)) {
        // The magnetometer's Y and Z axes point the opposite way to the
        // accel/gyro axes; flip them so all sensors agree.
        r->mag[0] = little_endian_16(&m[1]) * MAG_UT_PER_LSB;
        r->mag[1] = -little_endian_16(&m[3]) * MAG_UT_PER_LSB;
        r->mag[2] = -little_endian_16(&m[5]) * MAG_UT_PER_LSB;
    }
    r->time_ms = (uint32_t)(esp_timer_get_time() / 1000);
    return ESP_OK;
}

// ===========================================================================
// Bluetooth LE (NimBLE stack)
// ===========================================================================

// UUIDs are written byte-reversed (little endian), as NimBLE expects.
static const ble_uuid128_t SERVICE_UUID = BLE_UUID128_INIT(
    0x01, 0x00, 0x9b, 0x5f, 0x80, 0x00, 0x00, 0x80, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x34, 0x12);
static const ble_uuid128_t READINGS_UUID = BLE_UUID128_INIT(
    0x01, 0x00, 0x9b, 0x5f, 0x80, 0x00, 0x00, 0x80, 0x00, 0x40, 0x00, 0x00, 0x01, 0x00, 0x34, 0x12);

static void ble_advertise(void);

// Called by NimBLE when a computer reads the characteristic, and also to get
// the value for each notification.
static int readings_access(uint16_t conn, uint16_t attr, struct ble_gatt_access_ctxt *ctxt, void *arg)
{
    imu_reading_t copy;
    taskENTER_CRITICAL(&s_latest_lock);
    copy = s_latest;
    taskEXIT_CRITICAL(&s_latest_lock);
    return os_mbuf_append(ctxt->om, &copy, sizeof(copy)) == 0 ? 0 : BLE_ATT_ERR_INSUFFICIENT_RES;
}

static const struct ble_gatt_svc_def s_gatt_services[] = {
    {
        .type = BLE_GATT_SVC_TYPE_PRIMARY,
        .uuid = &SERVICE_UUID.u,
        .characteristics = (struct ble_gatt_chr_def[]) {
            {
                .uuid = &READINGS_UUID.u,
                .access_cb = readings_access,
                .val_handle = &s_readings_handle,
                .flags = BLE_GATT_CHR_F_READ | BLE_GATT_CHR_F_NOTIFY,
            },
            { 0 },   // end of characteristics
        },
    },
    { 0 },           // end of services
};

// Connection events.
static int ble_gap_event(struct ble_gap_event *event, void *arg)
{
    switch (event->type) {
    case BLE_GAP_EVENT_CONNECT:
    case BLE_GAP_EVENT_DISCONNECT:
    case BLE_GAP_EVENT_ADV_COMPLETE:
        // Keep advertising at all times, even while connected. If a PC program
        // crashes, Windows may keep its connection open; staying visible lets
        // the next program find the board and reuse that connection.
        ble_advertise();
        break;

    case BLE_GAP_EVENT_SUBSCRIBE:
        // A computer turned notifications on or off (also sent on disconnect).
        if (event->subscribe.attr_handle == s_readings_handle) {
            if (event->subscribe.cur_notify && !event->subscribe.prev_notify) {
                s_subscribers++;
            } else if (!event->subscribe.cur_notify && event->subscribe.prev_notify) {
                s_subscribers--;
            }
            gpio_set_level(PIN_RED_LED, s_subscribers > 0);
        }
        break;
    }
    return 0;
}

static void ble_advertise(void)
{
    struct ble_hs_adv_fields fields = { 0 };
    fields.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;
    fields.name = (uint8_t *)BLE_DEVICE_NAME;
    fields.name_len = strlen(BLE_DEVICE_NAME);
    fields.name_is_complete = 1;
    ble_gap_adv_set_fields(&fields);

    struct ble_gap_adv_params params = {
        .conn_mode = BLE_GAP_CONN_MODE_UND,   // anyone may connect
        .disc_mode = BLE_GAP_DISC_MODE_GEN,   // visible in scans
    };
    // Fails harmlessly if already advertising or if all connection slots are used.
    ble_gap_adv_start(s_own_addr_type, NULL, BLE_HS_FOREVER, &params, ble_gap_event, NULL);
}

// Called once the BLE stack is ready.
static void ble_on_sync(void)
{
    ble_hs_id_infer_auto(0, &s_own_addr_type);
    ble_advertise();
}

static void ble_host_task(void *param)
{
    nimble_port_run();               // runs until the stack is stopped
    nimble_port_freertos_deinit();
}

static void ble_init(void)
{
    // BLE needs non-volatile storage for calibration data.
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    ESP_ERROR_CHECK(nimble_port_init());
    ble_hs_cfg.sync_cb = ble_on_sync;
    ble_svc_gap_init();
    ble_svc_gatt_init();
    ESP_ERROR_CHECK(ble_gatts_count_cfg(s_gatt_services));
    ESP_ERROR_CHECK(ble_gatts_add_svcs(s_gatt_services));
    ble_svc_gap_device_name_set(BLE_DEVICE_NAME);
    nimble_port_freertos_init(ble_host_task);
}

// ===========================================================================
// Main loop
// ===========================================================================

static void sensor_task(void *arg)
{
    imu_reading_t r = { 0 };
    TickType_t last_wake = xTaskGetTickCount();
    uint32_t count = 0;

    for (;;) {
        vTaskDelayUntil(&last_wake, pdMS_TO_TICKS(SAMPLE_PERIOD_MS));

        if (imu_read(&r) != ESP_OK) {
            ESP_LOGW(TAG, "sensor read failed");
            continue;
        }

        // Publish the reading and notify every subscribed computer.
        taskENTER_CRITICAL(&s_latest_lock);
        s_latest = r;
        taskEXIT_CRITICAL(&s_latest_lock);
        ble_gatts_chr_updated(s_readings_handle);

        // Readable output on USB, 10x per second, only when a PC is attached
        // (printing with nobody listening would stall this loop).
        if (++count % 10 == 0 && usb_serial_jtag_is_connected()) {
            printf("t=%lu ms  accel=(%6.2f %6.2f %6.2f) m/s2  gyro=(%7.2f %7.2f %7.2f) dps  "
                   "mag=(%6.1f %6.1f %6.1f) uT  temp=%.1f C  ble_clients=%d\n",
                   (unsigned long)r.time_ms, r.accel[0], r.accel[1], r.accel[2],
                   r.gyro[0], r.gyro[1], r.gyro[2], r.mag[0], r.mag[1], r.mag[2],
                   r.temp_c, s_subscribers);
        }
    }
}

void app_main(void)
{
    gpio_reset_pin(PIN_RED_LED);
    gpio_set_direction(PIN_RED_LED, GPIO_MODE_OUTPUT);
    gpio_set_level(PIN_RED_LED, 0);

    i2c_init();
    while (!imu_init()) {
        // Blink the LED fast to signal "sensor not found", then retry.
        for (int i = 0; i < 10; i++) {
            gpio_set_level(PIN_RED_LED, i % 2);
            vTaskDelay(pdMS_TO_TICKS(100));
        }
    }
    ESP_LOGI(TAG, "ICM-20948 and magnetometer ready");

    ble_init();
    ESP_LOGI(TAG, "advertising over BLE as \"%s\"", BLE_DEVICE_NAME);

    xTaskCreate(sensor_task, "sensor", 4096, NULL, 5, NULL);
}
