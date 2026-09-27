"""Bridge: IMU glove (Bluetooth) -> orientation + gestures -> Unity (UDP).

    python unity_bridge.py            # then press Play in Unity

Hold the glove still for the first ~2 s so the gyro can calibrate.

Each reading from the board becomes one small JSON packet sent to
127.0.0.1:5005 (about 100 per second):

    {"connected": true, "t": 12.34,
     "q":    [x, y, z, w],      hand orientation, Unity axes (see below)
     "lin":  [x, y, z],         acceleration without gravity, m/s^2, Unity world axes
     "gyro": [x, y, z],         angular velocity, deg/s, Unity axes
     "still": true,             hand is at rest
     "events": ["flick_up"]}    gestures since the last packet: "flick_up",
                                "flick_down", "flick_left", "flick_right",
                                "shake", "flipped face down", "flipped face up"

While there's no data yet, a status packet is sent twice a second:
{"connected": false, "status": "searching"} (looking for the board) or
{"connected": false, "status": "calibrating"} (connected; hold the glove still).

Axes: Unity uses Y up, Z forward, X right. The mapping assumes the board lies
flat on the back of the hand with the sensor's +x axis pointing toward the
fingers and +z pointing up (out of the back of the hand). Any other mounting is
corrected in Unity with GloveHand's "Mount Rotation" setting.

Heading (turning left/right) uses the magnetometer only if a compass
calibration exists (made with dashboard.py). Otherwise it's gyro-only and
drifts slowly; re-center in Unity to fix it.
"""

import argparse
import json
import math
import socket
import sys
import time
from typing import List, Optional, Sequence, Tuple

from imu_ble import IMUClient, Reading
from processing import CompassCalibration, MotionDetector

GRAVITY = 9.80665
DEG = math.pi / 180

Quat = Tuple[float, float, float, float]   # (w, x, y, z)


# ---------------------------------------------------------------------------
# Madgwick orientation filter
# ---------------------------------------------------------------------------

class Madgwick:
    """Sebastian Madgwick's AHRS filter (2010), as used in MadgwickAHRS.c.

    q is the body -> world rotation. The world frame is z up, and x points at
    magnetic north when the magnetometer is used (otherwise wherever the board
    faced at startup). `beta` sets how strongly accel/mag correct the gyro:
    higher values converge faster but are noisier.
    """

    def __init__(self, beta: float = 0.05):
        self.beta = beta
        self.q = [1.0, 0.0, 0.0, 0.0]

    def update(self, gyro_rad: Sequence[float], accel: Sequence[float],
               mag: Optional[Sequence[float]], dt: float) -> None:
        q0, q1, q2, q3 = self.q
        gx, gy, gz = gyro_rad
        ax, ay, az = accel

        # Rate of change of the quaternion from the gyroscope.
        qd0 = 0.5 * (-q1 * gx - q2 * gy - q3 * gz)
        qd1 = 0.5 * (q0 * gx + q2 * gz - q3 * gy)
        qd2 = 0.5 * (q0 * gy - q1 * gz + q3 * gx)
        qd3 = 0.5 * (q0 * gz + q1 * gy - q2 * gx)

        an = math.sqrt(ax * ax + ay * ay + az * az)
        if an > 0:
            ax, ay, az = ax / an, ay / an, az / an
            if mag is not None and any(mag):
                s = self._gradient_marg(ax, ay, az, *mag)
            else:
                s = self._gradient_imu(ax, ay, az)
            sn = math.sqrt(sum(v * v for v in s))
            if sn > 0:
                qd0 -= self.beta * s[0] / sn
                qd1 -= self.beta * s[1] / sn
                qd2 -= self.beta * s[2] / sn
                qd3 -= self.beta * s[3] / sn

        q0 += qd0 * dt
        q1 += qd1 * dt
        q2 += qd2 * dt
        q3 += qd3 * dt
        n = math.sqrt(q0 * q0 + q1 * q1 + q2 * q2 + q3 * q3)
        self.q = [q0 / n, q1 / n, q2 / n, q3 / n]

    def _gradient_imu(self, ax, ay, az) -> List[float]:
        q0, q1, q2, q3 = self.q
        _2q0, _2q1, _2q2, _2q3 = 2 * q0, 2 * q1, 2 * q2, 2 * q3
        _4q0, _4q1, _4q2 = 4 * q0, 4 * q1, 4 * q2
        _8q1, _8q2 = 8 * q1, 8 * q2
        q0q0, q1q1, q2q2, q3q3 = q0 * q0, q1 * q1, q2 * q2, q3 * q3
        return [
            _4q0 * q2q2 + _2q2 * ax + _4q0 * q1q1 - _2q1 * ay,
            _4q1 * q3q3 - _2q3 * ax + 4 * q0q0 * q1 - _2q0 * ay - _4q1 + _8q1 * q1q1 + _8q1 * q2q2 + _4q1 * az,
            4 * q0q0 * q2 + _2q0 * ax + _4q2 * q3q3 - _2q3 * ay - _4q2 + _8q2 * q1q1 + _8q2 * q2q2 + _4q2 * az,
            4 * q1q1 * q3 - _2q1 * ax + 4 * q2q2 * q3 - _2q2 * ay,
        ]

    def _gradient_marg(self, ax, ay, az, mx, my, mz) -> List[float]:
        q0, q1, q2, q3 = self.q
        mn = math.sqrt(mx * mx + my * my + mz * mz)
        mx, my, mz = mx / mn, my / mn, mz / mn
        _2q0mx, _2q0my, _2q0mz, _2q1mx = 2 * q0 * mx, 2 * q0 * my, 2 * q0 * mz, 2 * q1 * mx
        _2q0, _2q1, _2q2, _2q3 = 2 * q0, 2 * q1, 2 * q2, 2 * q3
        _2q0q2, _2q2q3 = 2 * q0 * q2, 2 * q2 * q3
        q0q0, q0q1, q0q2, q0q3 = q0 * q0, q0 * q1, q0 * q2, q0 * q3
        q1q1, q1q2, q1q3 = q1 * q1, q1 * q2, q1 * q3
        q2q2, q2q3, q3q3 = q2 * q2, q2 * q3, q3 * q3

        # Earth's field direction as currently estimated: horizontal (bx) + vertical (bz).
        hx = mx * q0q0 - _2q0my * q3 + _2q0mz * q2 + mx * q1q1 + _2q1 * my * q2 + _2q1 * mz * q3 - mx * q2q2 - mx * q3q3
        hy = _2q0mx * q3 + my * q0q0 - _2q0mz * q1 + _2q1mx * q2 - my * q1q1 + my * q2q2 + _2q2 * mz * q3 - my * q3q3
        _2bx = math.sqrt(hx * hx + hy * hy)
        _2bz = -_2q0mx * q2 + _2q0my * q1 + mz * q0q0 + _2q1mx * q3 - mz * q1q1 + _2q2 * my * q3 - mz * q2q2 + mz * q3q3
        _4bx, _4bz = 2 * _2bx, 2 * _2bz

        return [
            -_2q2 * (2 * q1q3 - _2q0q2 - ax) + _2q1 * (2 * q0q1 + _2q2q3 - ay)
            - _2bz * q2 * (_2bx * (0.5 - q2q2 - q3q3) + _2bz * (q1q3 - q0q2) - mx)
            + (-_2bx * q3 + _2bz * q1) * (_2bx * (q1q2 - q0q3) + _2bz * (q0q1 + q2q3) - my)
            + _2bx * q2 * (_2bx * (q0q2 + q1q3) + _2bz * (0.5 - q1q1 - q2q2) - mz),

            _2q3 * (2 * q1q3 - _2q0q2 - ax) + _2q0 * (2 * q0q1 + _2q2q3 - ay)
            - 4 * q1 * (1 - 2 * q1q1 - 2 * q2q2 - az)
            + _2bz * q3 * (_2bx * (0.5 - q2q2 - q3q3) + _2bz * (q1q3 - q0q2) - mx)
            + (_2bx * q2 + _2bz * q0) * (_2bx * (q1q2 - q0q3) + _2bz * (q0q1 + q2q3) - my)
            + (_2bx * q3 - _4bz * q1) * (_2bx * (q0q2 + q1q3) + _2bz * (0.5 - q1q1 - q2q2) - mz),

            -_2q0 * (2 * q1q3 - _2q0q2 - ax) + _2q3 * (2 * q0q1 + _2q2q3 - ay)
            - 4 * q2 * (1 - 2 * q1q1 - 2 * q2q2 - az)
            + (-_4bx * q2 - _2bz * q0) * (_2bx * (0.5 - q2q2 - q3q3) + _2bz * (q1q3 - q0q2) - mx)
            + (_2bx * q1 + _2bz * q3) * (_2bx * (q1q2 - q0q3) + _2bz * (q0q1 + q2q3) - my)
            + (_2bx * q0 - _4bz * q2) * (_2bx * (q0q2 + q1q3) + _2bz * (0.5 - q1q1 - q2q2) - mz),

            _2q1 * (2 * q1q3 - _2q0q2 - ax) + _2q2 * (2 * q0q1 + _2q2q3 - ay)
            + (-_4bx * q3 + _2bz * q1) * (_2bx * (0.5 - q2q2 - q3q3) + _2bz * (q1q3 - q0q2) - mx)
            + (-_2bx * q0 + _2bz * q2) * (_2bx * (q1q2 - q0q3) + _2bz * (q0q1 + q2q3) - my)
            + _2bx * q1 * (_2bx * (q0q2 + q1q3) + _2bz * (0.5 - q1q1 - q2q2) - mz),
        ]


def rotate(q: Sequence[float], v: Sequence[float]) -> Tuple[float, float, float]:
    """Rotate vector v by quaternion q = (w, x, y, z)."""
    w, x, y, z = q
    vx, vy, vz = v
    # v' = v + 2w(u x v) + 2 u x (u x v), with u = (x, y, z)
    cx, cy, cz = y * vz - z * vy, z * vx - x * vz, x * vy - y * vx
    return (vx + 2 * (w * cx + y * cz - z * cy),
            vy + 2 * (w * cy + z * cx - x * cz),
            vz + 2 * (w * cz + x * cy - y * cx))


# ---------------------------------------------------------------------------
# Sensor frame -> Unity frame
# ---------------------------------------------------------------------------
# Sensor/world frames here: x forward, y left, z up (right-handed).
# Unity:                     X right, Y up, Z forward (left-handed).
# Vectors map as (x, y, z) -> (-y, z, x). Because Unity's frame is mirrored,
# rotations also flip direction: quaternion (w, x, y, z) -> Unity (x=y, y=-z,
# z=-x, w=w), and angular velocity picks up a minus sign.

def vec_to_unity(v: Sequence[float]) -> List[float]:
    return [-v[1], v[2], v[0]]


def quat_to_unity(q: Sequence[float]) -> List[float]:
    w, x, y, z = q
    return [y, -z, -x, w]            # Unity order: x, y, z, w


def gyro_to_unity(g: Sequence[float]) -> List[float]:
    return [g[1], -g[2], -g[0]]


# ---------------------------------------------------------------------------
# Gestures
# ---------------------------------------------------------------------------

class FlickDetector:
    """A fast wrist flick in any direction: total rotation speed (about any
    axis) jumps above the threshold."""

    def __init__(self, threshold_dps: float = 350.0, cooldown_s: float = 0.25):
        self.threshold = threshold_dps
        self.cooldown_s = cooldown_s
        self._last = -math.inf
        self._above = False

    def update(self, t: float, gyro_dps: Sequence[float]) -> bool:
        speed = math.sqrt(sum(g * g for g in gyro_dps))
        self.last_speed = speed
        rising = speed > self.threshold and not self._above
        self._above = speed > self.threshold * 0.6     # re-arm once it slows down
        if rising and t - self._last > self.cooldown_s:
            self._last = t
            return True
        return False


def flick_direction(q: Sequence[float], gyro_dps: Sequence[float]) -> str:
    """Which way a flick moves the fingertips, relative to the room and to
    where the hand points: "up", "down", "left" or "right".

    The fingers lie along the sensor's +x axis, so the fingertips move along
    omega x x_hat = (0, wz, -wy) in the hand's own frame. Rotating that into the
    world frame makes the answer independent of how the wrist is rolled.
    """
    gx, gy, gz = gyro_dps
    tip = rotate(q, (0.0, gz, -gy))                  # fingertip velocity, world frame (z up)
    f = rotate(q, (1.0, 0.0, 0.0))                   # where the fingers point
    h = math.hypot(f[0], f[1])
    if h > 0.2:
        left = (-f[1] / h, f[0] / h)                 # 90 deg to the left of the pointing heading
    else:                                            # pointing nearly straight up/down:
        l = rotate(q, (0.0, 1.0, 0.0))               # use the hand's own left side instead
        n = math.hypot(l[0], l[1]) or 1.0
        left = (l[0] / n, l[1] / n)
    up = tip[2]
    lateral = tip[0] * left[0] + tip[1] * left[1]
    if abs(up) >= abs(lateral):
        return "up" if up > 0 else "down"
    return "left" if lateral > 0 else "right"


# ---------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------

class Bridge:
    CALIBRATE_S = 1.5      # hold still this long at startup

    def __init__(self, use_mag: bool, beta: float):
        self.compass = CompassCalibration()
        self.use_mag = use_mag and self.compass.loaded
        self.beta = beta
        self.reset()

    def reset(self) -> None:
        self.filter = Madgwick(beta=self.beta)
        self.motion = MotionDetector()
        self.flick = FlickDetector()
        self.gyro_bias = [0.0, 0.0, 0.0]
        self.gravity = GRAVITY       # replaced by this sensor's own reading at calibration
        self._calib: List[Reading] = []
        self.calibrated = False
        self._last_t: Optional[float] = None

    def process(self, r: Reading) -> Optional[dict]:
        if self._last_t is not None and not (0 < r.time_s - self._last_t < 0.25):
            # Board restarted or readings were lost: start over.
            self.reset()
        dt = r.time_s - self._last_t if self._last_t is not None else 0.01
        self._last_t = r.time_s

        events = self.motion.update(r)
        mag = self.compass.apply(r.mag) if self.use_mag else None

        if not self.calibrated:
            # Average the gyro while still to find its offset, and let the
            # filter settle quickly (high beta) on gravity/north meanwhile.
            self._calib.append(r)
            gyro = [g * DEG for g in r.gyro]
            self.filter.beta = 2.5
            self.filter.update(gyro, r.accel, mag, dt)
            if r.time_s - self._calib[0].time_s >= self.CALIBRATE_S:
                if self.motion.state == "still":
                    n = len(self._calib)
                    self.gyro_bias = [sum(c.gyro[i] for c in self._calib) / n for i in range(3)]
                    self.gravity = sum(math.sqrt(sum(a * a for a in c.accel)) for c in self._calib) / n
                    self.filter.beta = self.beta
                    self.calibrated = True
                    print(f"calibrated: gyro offset {[round(b, 2) for b in self.gyro_bias]} deg/s, "
                          f"gravity {self.gravity:.3f} m/s^2, "
                          f"heading from {'magnetometer' if self.use_mag else 'gyro only'}", file=sys.stderr)
                else:
                    print("hold the glove still to calibrate...", file=sys.stderr)
                self._calib.clear()
            return None

        gyro_dps = [g - b for g, b in zip(r.gyro, self.gyro_bias)]
        if self.motion.state == "still":
            # Keep tracking slow changes in the gyro offset while at rest.
            self.gyro_bias = [b + 0.01 * (g - b) for b, g in zip(self.gyro_bias, r.gyro)]
        # While the hand accelerates hard (e.g. mid-flick), the accelerometer no
        # longer shows which way is down, so don't let it correct the tilt.
        a_norm = math.sqrt(sum(a * a for a in r.accel))
        steady = abs(a_norm - self.gravity) < 0.25 * self.gravity
        self.filter.update([g * DEG for g in gyro_dps], r.accel if steady else (0.0, 0.0, 0.0), mag, dt)
        q = self.filter.q

        # Gravity-free acceleration, in the body frame and world frame.
        q_inv = (q[0], -q[1], -q[2], -q[3])
        g_body = rotate(q_inv, (0.0, 0.0, self.gravity))
        lin_body = [a - g for a, g in zip(r.accel, g_body)]
        lin_world = rotate(q, lin_body)
        if self.flick.update(r.time_s, gyro_dps):
            direction = flick_direction(q, gyro_dps)
            events.append("flick_" + direction)
            print(f"flick {direction:<5}  ({self.flick.last_speed:.0f} deg/s)", file=sys.stderr)

        return {
            "connected": True,
            "t": round(r.time_s, 3),
            "q": [round(v, 5) for v in quat_to_unity(q)],
            "lin": [round(v, 3) for v in vec_to_unity(lin_world)],
            "gyro": [round(v, 2) for v in gyro_to_unity(gyro_dps)],
            "still": self.motion.state == "still",
            "events": events,
        }


def euler_deg(q: Sequence[float]) -> Tuple[float, float, float]:
    w, x, y, z = q
    roll = math.atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y))
    pitch = math.asin(max(-1.0, min(1.0, 2 * (w * y - z * x))))
    yaw = math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z))
    return roll / DEG, pitch / DEG, yaw / DEG


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1", help="where Unity runs (default: this PC)")
    ap.add_argument("--port", type=int, default=5005, help="UDP port Unity listens on")
    ap.add_argument("--no-mag", action="store_true", help="ignore the magnetometer even if calibrated")
    ap.add_argument("--beta", type=float, default=0.05, help="filter correction gain (default 0.05)")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    target = (args.host, args.port)
    bridge = Bridge(use_mag=not args.no_mag, beta=args.beta)
    print(f"sending to udp://{args.host}:{args.port}  (Ctrl+C to stop)", file=sys.stderr)

    sent = 0
    last_status = last_heartbeat = time.monotonic()
    with IMUClient() as imu:
        try:
            while True:
                readings = imu.get_available()
                for r in readings:
                    packet = bridge.process(r)
                    if packet is not None:
                        sock.sendto(json.dumps(packet, separators=(",", ":")).encode(), target)
                        sent += 1
                now = time.monotonic()
                if (not imu.connected or not bridge.calibrated) and now - last_heartbeat > 0.5:
                    status = "calibrating" if imu.connected else "searching"
                    sock.sendto(json.dumps({"connected": False, "status": status}).encode(), target)
                    last_heartbeat = now
                if now - last_status >= 1.0:
                    if bridge.calibrated and imu.connected:
                        r_, p_, y_ = euler_deg(bridge.filter.q)
                        print(f"{sent:4d} packets/s  roll {r_:+6.1f}  pitch {p_:+6.1f}  yaw {y_:+6.1f}  "
                              f"{bridge.motion.state}", file=sys.stderr)
                    sent = 0
                    last_status = now
                if not readings:
                    time.sleep(0.002)
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
