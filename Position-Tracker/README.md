# Position-Tracker

Streams accelerometer/gyro data from an Adafruit Feather ESP32-S3 Reverse TFT
with an ICM-20948 (STEMMA QT, I2C 0x69) to the PC over Bluetooth LE, and
estimates the sensor's 3D position.

```
Position-Tracker/
├── firmware/    PlatformIO project (ESP-IDF): src/main.c, src/ble_imu.c
└── pc-app/      imu_stream.py (data), position_tracker.py (3D position)
```

## Quick start

```
cd pc-app
pip install bleak pyserial numpy matplotlib
python position_tracker.py          # position, over Bluetooth
python position_tracker.py --plot   # live 3D trail
python imu_stream.py                # raw accel/gyro samples
```

Hold the sensor still for ~1 s at startup to calibrate. That spot becomes the
origin. To re-center later, hold still and press **D1** on the board (or `r` in
the terminal/plot window). Add `--usb` to any command to use the USB cable
instead of Bluetooth.

In your own code:

```python
from position_tracker import track
for tracker, pose in track():
    print(pose.x, pose.y, pose.z)        # meters, z up
    # tracker.reset() re-centers (call while still)

from imu_stream import imu_samples, device_status
for s in imu_samples():
    print(s.ax, s.ay, s.az, s.gx, s.gy, s.gz)   # m/s^2, deg/s
    # s.buttons, s.recenter; device_status.battery_pct
```

## Position accuracy

Position comes from double-integrating acceleration, which drifts quickly. It
is kept usable by zeroing velocity whenever the sensor is still and removing
the drift accumulated over each movement when it ends. Expect decent results
for move-then-pause hand motions (a few cm of error on 10–30 cm moves), not for
long continuous motion. Very slow moves (< ~0.2 m/s²) are not detected. Yaw
drifts slowly; re-centering resets it.

## Firmware

ESP-IDF via PlatformIO (`firmware/src/`). Build and flash:

```
cd firmware
pio run -t upload
```

After flashing, press **Reset** on the board. If the board won't accept an
upload, enter the bootloader by holding **D0**, pressing and releasing
**Reset**, then releasing **D0**.

- `main.c`: IMU auto-detection (ICM-20948/20649, MPU-6050/6500/9250,
  ICM-42688-P, BMI160), 100 Hz sampling, D1 button, battery gauge (MAX17048).
- `ble_imu.c`: BLE service. Advertises as `Glove-IMU` and stays discoverable
  while connected, so a new PC session can take over from a stale one.

### BLE protocol

Service `4f1e0001-8a3c-4b7e-9d52-1c0de5a7a001`:

| UUID          | Props        | Payload (little endian) |
|---------------|--------------|-------------------------|
| `4f1e0002-…`  | notify       | N × 16 bytes: `u32 t_us`, `i16 ax,ay,az` (1/500 m/s²), `i16 gx,gy,gz` (1/50 °/s) |
| `4f1e0003-…`  | read         | text: sensor name, rate, units |
| `4f1e0004-…`  | read, notify | `u8 version=1`, `u8 buttons` (bit0 D0, bit1 D1, bit2 D2), `u16 recenter_count`, `u16 battery_mV`, `u8 battery_pct` (0xFF = no gauge) |

### USB serial protocol

Text lines: `D,t_us,ax,ay,az,gx,gy,gz` (samples), `E,recenter,count` (D1
pressed), `S,buttons,recenter_count,mV,pct` (every 2 s), `#...` (info).
Opening the port with DTR/RTS asserted resets the board; `imu_stream.py`
avoids that.

## Measured performance

- 100 samples/s over BLE with no gaps.
- Latency vs USB: median ~29 ms, p99 ~100 ms.

## Recovery

`../Firmware-Backup/feather_original_flash.bin` is a full 4 MB image of what was
on the board before this project. Restore it (in bootloader mode) with:

```
python -m esptool --port COMx write-flash 0 ../Firmware-Backup/feather_original_flash.bin
```
