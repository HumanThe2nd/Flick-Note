"""Estimate the sensor's 3D position from the IMU stream.

Hold the sensor still at startup: the first second is used to calibrate the gyro
bias and the gravity direction, and that spot becomes the origin (0, 0, 0).
Re-center any time (while still) with the board's D1 button, 'r' on the
keyboard, or tracker.reset().

Library use:

    from position_tracker import PositionTracker, track
    for tracker, pose in track():
        print(pose.x, pose.y, pose.z)
        if some_condition:
            tracker.reset()        # call while the hand is still

Command line:

    python position_tracker.py            # print position; press 'r' to reset, 'q' to quit
    python position_tracker.py --usb      # same, over the USB cable instead of Bluetooth
    python position_tracker.py --plot     # live 3D trail

World frame: z points up (opposite gravity); x/y follow the sensor's heading at
the last reset. Units are meters. Position comes from double-integrating
acceleration, so it drifts; it is kept in check by zeroing velocity whenever the
sensor is still, which works best for move-then-pause motions.
"""

import argparse
import math
import sys
import time
from collections import deque
from dataclasses import dataclass
from typing import Iterator, Optional, Tuple

import numpy as np

from imu_stream import BLE_NAME, Sample, device_status, imu_samples

DEG = math.pi / 180.0


@dataclass
class Pose:
    t: float
    x: float
    y: float
    z: float
    vx: float
    vy: float
    vz: float
    roll: float     # degrees
    pitch: float
    yaw: float
    still: bool
    calibrated: bool


# ---- quaternion helpers (w, x, y, z), body -> world ---------------------------

def q_mul(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    aw, ax, ay, az = a
    bw, bx, by, bz = b
    return np.array([
        aw * bw - ax * bx - ay * by - az * bz,
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
    ])


def q_rotate(q: np.ndarray, v: np.ndarray) -> np.ndarray:
    """Rotate vector v from body to world frame."""
    w, x, y, z = q
    r = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
        [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
        [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)],
    ])
    return r @ v


def q_from_accel(a: np.ndarray) -> np.ndarray:
    """Orientation with zero yaw whose world z-axis matches the measured 'up'."""
    up = a / np.linalg.norm(a)
    z = np.array([0.0, 0.0, 1.0])
    # Rotation taking body 'up' onto world z.
    axis = np.cross(up, z)
    s = np.linalg.norm(axis)
    c = float(np.dot(up, z))
    if s < 1e-9:
        return np.array([1.0, 0, 0, 0]) if c > 0 else np.array([0.0, 1, 0, 0])
    angle = math.atan2(s, c)
    axis /= s
    return np.array([math.cos(angle / 2), *(axis * math.sin(angle / 2))])


def q_to_euler(q: np.ndarray) -> Tuple[float, float, float]:
    w, x, y, z = q
    roll = math.atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y))
    pitch = math.asin(max(-1.0, min(1.0, 2 * (w * y - z * x))))
    yaw = math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z))
    return roll / DEG, pitch / DEG, yaw / DEG


def q_remove_yaw(q: np.ndarray) -> np.ndarray:
    """Keep tilt, zero the heading."""
    _, _, yaw = q_to_euler(q)
    h = yaw * DEG / 2
    return q_mul(np.array([math.cos(h), 0, 0, -math.sin(h)]), q)


# ---- tracker -------------------------------------------------------------------

class PositionTracker:
    def __init__(self,
                 rate_hz: float = 100.0,
                 calib_seconds: float = 1.0,
                 still_window_s: float = 0.2,
                 still_accel_dev: float = 0.2,     # m/s^2, spread of raw accel vectors
                 still_lin_max: float = 0.2,       # m/s^2, gravity-free accel
                 still_gyro_max: float = 4.0,       # deg/s
                 tilt_gain: float = 5.0):
        self.rate_hz = rate_hz
        self.calib_n = int(calib_seconds * rate_hz)
        self.window = deque(maxlen=max(3, int(still_window_s * rate_hz)))
        self.still_accel_dev = still_accel_dev
        self.still_lin_max = still_lin_max
        self.still_gyro_max = still_gyro_max
        self.tilt_gain = tilt_gain
        self._reset_requested = False
        self._full_reset()

    # Public API ------------------------------------------------------------------

    def reset(self) -> None:
        """Re-center: position/velocity -> 0, heading -> 0, gyro bias re-measured.

        Call while the sensor is still. It takes effect on the next update(), using
        the most recent still window; if the sensor is moving, it waits until it is
        still before re-centering.
        """
        self._reset_requested = True

    def update(self, s: Sample) -> Pose:
        if s.recenter:   # D1 button on the board
            print("# re-center button pressed (hold still)", file=sys.stderr)
            self.reset()
        acc = np.array([s.ax, s.ay, s.az])
        gyr = np.array([s.gx, s.gy, s.gz]) * DEG

        # A timestamp jump means the board restarted or samples were lost.
        if self.last_t is not None and not (0 < s.t - self.last_t < 0.2):
            self._full_reset()
        dt = (s.t - self.last_t) if self.last_t is not None else 1.0 / self.rate_hz
        self.last_t = s.t

        if not self.calibrated:
            self.window.append((acc, gyr, np.zeros(3)))
            self.calib.append((acc, gyr))
            if len(self.calib) >= self.calib_n:
                a = np.array([c[0] for c in self.calib])
                g = np.array([c[1] for c in self.calib])
                if np.max(np.linalg.norm(a - a.mean(axis=0), axis=1)) < 2 * self.still_accel_dev:
                    self._center(a.mean(axis=0), g.mean(axis=0))
                else:
                    print("# moving during calibration, hold still...", file=sys.stderr)
                    self.calib.clear()
            return self._pose(s.t, still=True)

        # Orientation: integrate gyro. While at rest, also nudge tilt toward the
        # measured gravity direction (during motion the accelerometer can't be
        # trusted for that, even when its magnitude happens to be ~1 g).
        w = gyr - self.gyro_bias
        if self.still:
            up_meas = acc / np.linalg.norm(acc)
            q = self.q
            up_est = np.array([  # world z expressed in body frame
                2 * (q[1] * q[3] - q[0] * q[2]),
                2 * (q[0] * q[1] + q[2] * q[3]),
                q[0] ** 2 - q[1] ** 2 - q[2] ** 2 + q[3] ** 2,
            ])
            w = w + self.tilt_gain * np.cross(up_meas, up_est)
        dq = np.array([0.0, *w]) * 0.5 * dt
        self.q = self.q + q_mul(self.q, dq)
        self.q /= np.linalg.norm(self.q)

        # Linear acceleration in the world frame, gravity removed.
        lin = q_rotate(self.q, acc) - np.array([0.0, 0.0, self.g])

        self.window.append((acc, gyr, lin))
        self.still = still = self._is_still()

        if self._reset_requested and still:
            a = np.array([w[0] for w in self.window])
            g = np.array([w[1] for w in self.window])
            self._center(a.mean(axis=0), g.mean(axis=0))
            return self._pose(s.t, still=True)

        if still:
            if self.moving_time > 0:
                # Motion just ended: true velocity is zero, so whatever velocity we
                # have is drift that grew (roughly linearly) over the motion.
                # Undo its effect on position.
                self.pos -= self.vel * self.moving_time / 2
                self.moving_time = 0.0
            self.vel[:] = 0
            self.prev_lin = np.zeros(3)
            # Slowly track gyro bias drift while at rest.
            self.gyro_bias += 0.01 * (gyr - self.gyro_bias)
        else:
            v_prev = self.vel.copy()
            self.vel += 0.5 * (lin + self.prev_lin) * dt
            self.pos += 0.5 * (self.vel + v_prev) * dt
            self.prev_lin = lin
            self.moving_time += dt

        return self._pose(s.t, still)

    # Internals -------------------------------------------------------------------

    def _full_reset(self) -> None:
        self.calibrated = False
        self.calib = []
        self.window.clear()
        self.last_t: Optional[float] = None
        self.q = np.array([1.0, 0, 0, 0])
        self.g = 9.80665
        self.gyro_bias = np.zeros(3)
        self.pos = np.zeros(3)
        self.vel = np.zeros(3)
        self.prev_lin = np.zeros(3)
        self.moving_time = 0.0
        self.still = True

    def _center(self, acc_mean: np.ndarray, gyr_mean: np.ndarray) -> None:
        self.g = float(np.linalg.norm(acc_mean))
        self.gyro_bias = gyr_mean.copy()
        self.q = q_remove_yaw(q_from_accel(acc_mean))
        self.pos[:] = 0
        self.vel[:] = 0
        self.prev_lin = np.zeros(3)
        self.moving_time = 0.0
        self.still = True
        self.calibrated = True
        self._reset_requested = False
        print(f"# centered (g={self.g:.3f} m/s^2, gyro bias="
              f"{np.round(self.gyro_bias / DEG, 2).tolist()} deg/s)", file=sys.stderr)

    def _is_still(self) -> bool:
        if len(self.window) < self.window.maxlen:
            return False
        a = np.array([w[0] for w in self.window])
        g = np.array([w[1] for w in self.window]) - self.gyro_bias
        lin = np.array([w[2] for w in self.window])
        # Every sample in the window must look calm, so motion is detected on the
        # first strong sample but stillness needs the whole window.
        return (np.max(np.linalg.norm(a - a.mean(axis=0), axis=1)) < self.still_accel_dev
                and np.max(np.linalg.norm(lin, axis=1)) < self.still_lin_max
                and np.max(np.abs(g)) < self.still_gyro_max * DEG)

    def _pose(self, t: float, still: bool) -> Pose:
        r, p, y = q_to_euler(self.q)
        return Pose(t, *self.pos.tolist(), *self.vel.tolist(), r, p, y, still, self.calibrated)


def track(port: Optional[str] = None, transport: str = "ble", device: str = BLE_NAME,
          **kwargs) -> Iterator[Tuple[PositionTracker, Pose]]:
    """Yield (tracker, pose) for every IMU sample. Call tracker.reset() to re-center."""
    tracker = PositionTracker(**kwargs)
    for s in imu_samples(port, transport=transport, device=device):
        yield tracker, tracker.update(s)


# ---- command line --------------------------------------------------------------

def _key_pressed() -> Optional[str]:
    try:
        import msvcrt
    except ImportError:
        return None
    if msvcrt.kbhit():
        return msvcrt.getwch().lower()
    return None


def _print_loop(src: dict) -> None:
    print("# hold still to calibrate; press D1 on the board or 'r' here to re-center, 'q' to quit", file=sys.stderr)
    last = 0.0
    for tracker, pose in track(**src):
        k = _key_pressed()
        if k == "r":
            tracker.reset()
            print("# reset requested (hold still)", file=sys.stderr)
        elif k == "q":
            return
        if not pose.calibrated or time.monotonic() - last < 0.05:
            continue
        last = time.monotonic()
        print(f"pos = ({pose.x:+7.3f}, {pose.y:+7.3f}, {pose.z:+7.3f}) m   "
              f"rpy = ({pose.roll:+6.1f}, {pose.pitch:+6.1f}, {pose.yaw:+6.1f}) deg   "
              f"{'still ' if pose.still else 'MOVING'}"
              + (f"   bat {device_status.battery_pct}%" if device_status.battery_pct is not None else ""),
              flush=True)


def _plot_loop(src: dict, trail: int = 400) -> None:
    import matplotlib.pyplot as plt

    fig = plt.figure()
    ax = fig.add_subplot(projection="3d")
    ax.set_xlabel("x (m)")
    ax.set_ylabel("y (m)")
    ax.set_zlabel("z (m)")
    line, = ax.plot([], [], [], lw=1)
    dot, = ax.plot([], [], [], "o", color="C3")
    fig.suptitle("Hold still to calibrate — press 'r' in this window to re-center")
    state = {"reset": False}
    fig.canvas.mpl_connect("key_press_event",
                           lambda e: state.update(reset=True) if e.key == "r" else None)
    pts = deque(maxlen=trail)
    plt.ion()
    plt.show()
    last = 0.0
    for tracker, pose in track(**src):
        if state["reset"]:
            tracker.reset()
            pts.clear()
            state["reset"] = False
        if not pose.calibrated:
            continue
        pts.append((pose.x, pose.y, pose.z))
        if time.monotonic() - last < 1 / 30:
            continue
        last = time.monotonic()
        if not plt.fignum_exists(fig.number):
            return
        p = np.array(pts)
        line.set_data_3d(p[:, 0], p[:, 1], p[:, 2])
        dot.set_data_3d([p[-1, 0]], [p[-1, 1]], [p[-1, 2]])
        span = max(0.25, float(np.max(np.abs(p))) * 1.2)
        for setlim in (ax.set_xlim, ax.set_ylim, ax.set_zlim):
            setlim(-span, span)
        ax.set_title(f"({pose.x:+.2f}, {pose.y:+.2f}, {pose.z:+.2f}) m  "
                     f"{'still' if pose.still else 'moving'}")
        plt.pause(0.001)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--usb", action="store_true", help="read over the USB cable instead of Bluetooth")
    ap.add_argument("--port", help="USB serial port (implies --usb; auto-detected if omitted)")
    ap.add_argument("--device", default=BLE_NAME, help="BLE device name or address")
    ap.add_argument("--plot", action="store_true", help="show a live 3D trail")
    args = ap.parse_args()
    try:
        src = dict(port=args.port, transport="usb" if args.usb else "ble", device=args.device)
        (_plot_loop if args.plot else _print_loop)(src)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
