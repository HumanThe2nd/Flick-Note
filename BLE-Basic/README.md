# BLE-Basic

A basic, readable project: the Feather ESP32-S3 Reverse TFT reads **everything**
from the ICM-20948 (acceleration, angular velocity, magnetic field, temperature)
100×/s and broadcasts it over **Bluetooth LE**. A PC program shows and uses it.
No USB cable is needed once the board is flashed.

```
BLE-Basic/
├── firmware/                 PlatformIO project (open this folder in VS Code/PlatformIO)
│   ├── src/main.c            the whole firmware, one file, top to bottom
│   ├── platformio.ini        build settings (ESP-IDF)
│   └── sdkconfig.*           ESP-IDF config (Bluetooth on, USB console)
└── pc-app/
    ├── imu_ble.py        Bluetooth client: connects, decodes, reconnects
    ├── print_readings.py smallest example: print every reading
    ├── processing.py     orientation, compass calibration, motion/shake/flip detection
    ├── dashboard.py      live plots + values + record to CSV
    ├── unity_bridge.py   feeds orientation + gestures to Unity (see ../../Unity-Game)
    └── recordings/       CSVs saved by the dashboard
```

## Run it

1. **Power the board without USB**: plug a LiPo battery into the JST connector
   (or use a USB power bank). The red LED next to the USB port turns on while
   a computer is receiving data.
2. On the PC (Bluetooth on; do **not** pair the board in Windows settings):

   ```
   cd pc-app
   pip install -r requirements.txt
   python dashboard.py        # live dashboard
   python print_readings.py   # or: plain text in the terminal
   ```

Using it in your own script:

```python
from imu_ble import IMUClient
from processing import Orientation, MotionDetector

orient, motion = Orientation(), MotionDetector()
with IMUClient() as imu:
    for r in imu.readings():
        roll, pitch, heading = orient.update(r)
        for event in motion.update(r):      # "shake", "flipped face down", ...
            print(event)
```

## Dashboard

- **Plots**: last 10 s of accel (m/s²), gyro (°/s), magnetic field (µT), temperature.
- **Panel**: live values and magnitudes, roll/pitch/heading, still/moving, events.
- **Pause**: freezes the plots. Data keeps being processed and recorded.
- **Record**: writes every reading, plus orientation and motion state, to
  `pc-app/recordings/imu_<date>_<time>.csv`.
- **Calibrate compass**: press it, then slowly rotate the board through every
  orientation until the x/y/z swing indicators all reach 100%, then press
  **Finish calibration**. The result is saved to `pc-app/compass_calibration.json`
  and reused. Redo it if you mount the board somewhere new (nearby metal
  changes the offset). Heading is meaningless until you do this.

## Axes and units

All three sensors use the axes printed on the ICM-20948 breakout. The firmware
flips the magnetometer's Y/Z so they match. When lying flat, face up, accel
z ≈ +9.8 m/s².

| field   | unit  | notes |
|---------|-------|-------|
| accel   | m/s²  | ±8 g range; includes gravity |
| gyro    | °/s   | ±2000 °/s range (wrist flicks exceed 500 °/s); ~±1 °/s offset at rest is normal |
| mag     | µT    | Earth's field is 25–65 µT; raw values include board offsets |
| temp_c  | °C    | chip temperature, a few degrees above room temperature |

## Bluetooth format

Device name `IMU-Basic`, service `12340000-0000-4000-8000-00805f9b0001`,
characteristic `12340001-0000-4000-8000-00805f9b0001` (read + notify). Each
notification is one 44-byte reading, little endian:
`uint32 time_ms`, then `float ax, ay, az, gx, gy, gz, mx, my, mz, temp_c`.
This is `imu_reading_t` in `main.c`, decoded in `imu_ble.py` with
`struct.Struct("<I10f")`.

## Firmware

Build and flash from the `firmware` folder with `pio run -t upload` (or with the
PlatformIO → Upload button in VS Code). Afterwards, press **Reset** on the
board. If an upload can't connect, enter the bootloader by holding **D0**,
pressing and releasing **Reset**, then releasing **D0**, and upload again.

With USB connected, a COM port shows a readable line 10×/s, e.g.

```
t=24149 ms  accel=( -1.92  -7.37   6.53) m/s2  gyro=( -0.26 -0.37 -0.84) dps  mag=( 30.8 -8.6 -102.2) uT  temp=29.5 C  ble_clients=0
```

If the sensor isn't found, the red LED blinks rapidly and the board keeps retrying.

## Troubleshooting

- **"not found"**: the board isn't powered or isn't advertising. Press Reset.
  Close other programs using it (only one PC program should connect at a time).
- **No data but "connected"**: restart the PC program. If a previous program
  crashed, Windows may hold a stale link. The board stays discoverable, so a
  fresh start reconnects.
- **Heading wrong**: run compass calibration, away from laptops and speakers.

## Other projects

`../Position-Tracker/` is the earlier, more advanced firmware (3D position tracking,
re-center button, battery reporting). It uses a different Bluetooth name and
format; flash it back with `pio run -t upload` from its `firmware` folder.
