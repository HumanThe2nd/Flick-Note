"""Smallest example: connect over Bluetooth and print every reading.

    python print_readings.py

Use this as a starting point for your own scripts. Ctrl+C to stop.
"""

from imu_ble import IMUClient

with IMUClient() as imu:
    try:
        for r in imu.readings():
            ax, ay, az = r.accel
            gx, gy, gz = r.gyro
            mx, my, mz = r.mag
            print(f"t={r.time_s:9.2f}s  "
                  f"accel=({ax:+6.2f},{ay:+6.2f},{az:+6.2f}) m/s^2  "
                  f"gyro=({gx:+7.1f},{gy:+7.1f},{gz:+7.1f}) deg/s  "
                  f"mag=({mx:+6.1f},{my:+6.1f},{mz:+6.1f}) uT  "
                  f"temp={r.temp_c:.1f} C")
    except KeyboardInterrupt:
        pass
