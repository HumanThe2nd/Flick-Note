# Flick Note

Motion sensing with an **Adafruit Feather ESP32-S3 Reverse TFT** and an
**ICM-20948** 9-DoF IMU (STEMMA QT, I2C 0x69), streamed to the PC over Bluetooth LE.

| Folder | What it is | Board advertises as |
|--------|-----------|---------------------|
| [`BLE-Basic/`](BLE-Basic/README.md) | Simple, readable version. Streams accel, gyro, magnetometer and temperature. Live dashboard, CSV recording, compass calibration, shake/flip detection. **Currently on the board.** | `IMU-Basic` |
| [`Position-Tracker/`](Position-Tracker/README.md) | Advanced version. 3D position estimate, D1 re-center button, battery level. | `Glove-IMU` |
| [`Unity-Game/`](Unity-Game/README.md) | **Flick Note**, a Unity rhythm game played by pointing with the glove. Uses the BLE-Basic firmware via `BLE-Basic/pc-app/unity_bridge.py`. Build it with `Unity-Game` (or download `FlickNote.exe` from the GitHub Releases). | (uses `IMU-Basic`) |
| `Firmware-Backup/` | Full 4 MB image of the board's original firmware (before these projects), plus the original sdkconfig. | – |

Each project has a `firmware/` folder (a PlatformIO project: open it in VS Code
or run `pio run -t upload` inside it) and a `pc-app/` folder (Python programs).
Only one firmware can be on the board at a time. Flash the one whose PC app you
want to use.

PC requirements: Python 3 with `pip install bleak pyserial numpy matplotlib`.
Unity 6000.6.3f1 is installed at `D:\Unity\Editors\6000.6.3f1` (registered in Unity Hub).

## Quick start: play Flick Note

```
cd D:\Workspace\Flick-Note\BLE-Basic\pc-app
python unity_bridge.py                                   # hold the glove still ~2 s
D:\Workspace\Flick-Note\Unity-Game\Build\FlickNote.exe    # in a second window
```
