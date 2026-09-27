#pragma once

#include <stdint.h>

// BLE GATT service that streams IMU samples to a connected central.
//
// Service  4f1e0001-8a3c-4b7e-9d52-1c0de5a7a001
//   4f1e0002-...  notify: packed samples, 16 bytes each, little endian:
//                   uint32 t_us, int16 ax, ay, az (x 1/500 m/s^2),
//                   int16 gx, gy, gz (x 1/50 deg/s)
//   4f1e0003-...  read:   text description of the sensor and format
//   4f1e0004-...  read/notify: device status, 7 bytes little endian:
//                   uint8 version (1), uint8 buttons (bit0 D0, bit1 D1, bit2 D2),
//                   uint16 recenter_count, uint16 battery_mv,
//                   uint8 battery_pct (0xFF = no fuel gauge)

#define BLE_IMU_DEVICE_NAME "Glove-IMU"

void ble_imu_init(const char *info_text);

// Queue one sample; sent as soon as possible while a central is subscribed.
void ble_imu_push(int64_t t_us, const float accel_ms2[3], const float gyro_dps[3]);

// Update the status characteristic and notify subscribers.
void ble_imu_set_status(uint8_t buttons, uint16_t recenter_count,
                        uint16_t battery_mv, uint8_t battery_pct);
