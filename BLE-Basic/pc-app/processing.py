"""Things to compute from IMU readings. Each class is independent; use the
ones you need, or add your own following the same pattern:

    orient = Orientation()
    motion = MotionDetector()
    for r in imu.readings():
        roll, pitch, heading = orient.update(r)
        for event in motion.update(r):
            print(event)
"""

import json
import math
from collections import deque
from pathlib import Path
from typing import List, Optional, Tuple

from imu_ble import Reading, Vec3

GRAVITY = 9.80665


class CompassCalibration:
    """Removes the magnetometer's offset ("hard iron") and evens out the axes.

    Nearby metal and magnets (the battery, the laptop) add a constant offset to
    the magnetometer, which makes compass headings wrong. To calibrate, call
    start(), slowly rotate the board through every orientation for ~20 s, then
    call finish(). The result is saved and loaded automatically next time.
    """

    FILE = Path(__file__).with_name("compass_calibration.json")
    MIN_SPAN_UT = 30.0   # each axis must swing at least this much

    def __init__(self):
        self.offset = [0.0, 0.0, 0.0]
        self.scale = [1.0, 1.0, 1.0]
        self.collecting = False
        self._min = [math.inf] * 3
        self._max = [-math.inf] * 3
        self.loaded = self._load()

    def apply(self, mag: Vec3) -> Vec3:
        return tuple((m - o) * s for m, o, s in zip(mag, self.offset, self.scale))

    def start(self) -> None:
        self._min = [math.inf] * 3
        self._max = [-math.inf] * 3
        self.collecting = True

    def add(self, mag: Vec3) -> None:
        if self.collecting:
            for i in range(3):
                self._min[i] = min(self._min[i], mag[i])
                self._max[i] = max(self._max[i], mag[i])

    def progress(self) -> List[float]:
        """Swing seen on each axis so far, as a fraction of what's needed."""
        return [min(1.0, max(0.0, (hi - lo) / self.MIN_SPAN_UT))
                if hi > lo else 0.0 for lo, hi in zip(self._min, self._max)]

    def finish(self) -> str:
        self.collecting = False
        spans = [hi - lo for lo, hi in zip(self._min, self._max)]
        if min(spans) < self.MIN_SPAN_UT:
            return ("calibration failed: rotate the board through more orientations "
                    f"(axis swings {[round(s) for s in spans]} uT, need {self.MIN_SPAN_UT:.0f}+ each)")
        self.offset = [(hi + lo) / 2 for lo, hi in zip(self._min, self._max)]
        radii = [s / 2 for s in spans]
        avg = sum(radii) / 3
        self.scale = [avg / r for r in radii]
        self.FILE.write_text(json.dumps({"offset": self.offset, "scale": self.scale}, indent=2))
        return (f"compass calibrated: offset {[round(o, 1) for o in self.offset]} uT, "
                f"field strength ~{avg:.0f} uT")

    def _load(self) -> bool:
        try:
            data = json.loads(self.FILE.read_text())
            self.offset, self.scale = data["offset"], data["scale"]
            return True
        except (OSError, ValueError, KeyError):
            return False


class Orientation:
    """Roll and pitch (tilt) plus compass heading, in degrees.

    Tilt uses a complementary filter: the gyro gives smooth, fast changes, and
    gravity (from the accelerometer) slowly corrects the gyro's drift.
    Heading comes from the magnetometer, compensated for tilt. It is only
    meaningful after compass calibration. 0 deg = board's +x axis pointing at
    magnetic north.
    """

    def __init__(self, compass: Optional[CompassCalibration] = None, gyro_weight: float = 0.98):
        self.compass = compass or CompassCalibration()
        self.gyro_weight = gyro_weight
        self.roll = self.pitch = self.heading = 0.0
        self._last_t: Optional[float] = None

    def update(self, r: Reading) -> Tuple[float, float, float]:
        ax, ay, az = r.accel
        gx, gy, _ = r.gyro
        accel_roll = math.degrees(math.atan2(ay, az))
        accel_pitch = math.degrees(math.atan2(-ax, math.hypot(ay, az)))

        dt = r.time_s - self._last_t if self._last_t is not None else 0.0
        self._last_t = r.time_s
        if not 0 < dt < 0.5:   # first reading, or a gap: trust the accelerometer
            self.roll, self.pitch = accel_roll, accel_pitch
        else:
            w = self.gyro_weight
            self.roll = w * _wrap(self.roll + gx * dt, accel_roll) + (1 - w) * accel_roll
            self.pitch = w * (self.pitch + gy * dt) + (1 - w) * accel_pitch
            self.roll = _wrap180(self.roll)

        # Rotate the magnetic field back to level (undo roll, then pitch), then
        # take the angle of its horizontal part. The board's z axis points up
        # (accel z = +9.8 when flat), so heading = atan2(+yh, xh), clockwise
        # from north.
        mx, my, mz = self.compass.apply(r.mag)
        phi, theta = math.radians(self.roll), math.radians(self.pitch)
        xh = mx * math.cos(theta) + my * math.sin(phi) * math.sin(theta) + mz * math.cos(phi) * math.sin(theta)
        yh = my * math.cos(phi) - mz * math.sin(phi)
        self.heading = math.degrees(math.atan2(yh, xh)) % 360
        return self.roll, self.pitch, self.heading


class MotionDetector:
    """Classifies the board as still/moving and detects simple gestures.

    update() returns a list of event strings (usually empty):
      "shake"            several hard jolts within a second
      "flipped face down" / "flipped face up"
    """

    def __init__(self, window_s: float = 0.3, shake_jolt: float = 8.0, shake_count: int = 3):
        self.window_s = window_s
        self.shake_jolt = shake_jolt      # m/s^2 away from 1 g counts as a jolt
        self.shake_count = shake_count
        self.state = "still"
        self._window: deque = deque()
        self._jolts: deque = deque()
        self._last_shake = -math.inf
        self._in_jolt = False
        self._face: Optional[str] = None

    def update(self, r: Reading) -> List[str]:
        events = []
        t = r.time_s
        a_mag = math.sqrt(sum(v * v for v in r.accel))
        g_mag = math.sqrt(sum(v * v for v in r.gyro))

        # Still vs moving: over the last window, acceleration stays near 1 g
        # and rotation stays small.
        self._window.append((t, a_mag, g_mag))
        while self._window and t - self._window[0][0] > self.window_s:
            self._window.popleft()
        calm = all(abs(a - GRAVITY) < 0.5 and g < 10 for _, a, g in self._window)
        self.state = "still" if calm else "moving"

        # Shake: count jolts (rising edges above the threshold) in the last second.
        jolt = abs(a_mag - GRAVITY) > self.shake_jolt
        if jolt and not self._in_jolt:
            self._jolts.append(t)
        self._in_jolt = jolt
        while self._jolts and t - self._jolts[0] > 1.0:
            self._jolts.popleft()
        if len(self._jolts) >= self.shake_count and t - self._last_shake > 1.0:
            events.append("shake")
            self._last_shake = t
            self._jolts.clear()

        # Face up / face down, from which way gravity points along z (only when still).
        if calm:
            az = r.accel[2]
            face = "up" if az > 7 else "down" if az < -7 else None
            if face and face != self._face:
                if self._face is not None:
                    events.append(f"flipped face {face}")
                self._face = face
        return events


def _wrap(angle: float, reference: float) -> float:
    """Shift angle by 360s so it's within 180 deg of reference (avoids jumps at +/-180)."""
    while angle - reference > 180:
        angle -= 360
    while angle - reference < -180:
        angle += 360
    return angle


def _wrap180(angle: float) -> float:
    return (angle + 180) % 360 - 180
