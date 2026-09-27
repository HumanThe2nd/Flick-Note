"""Live dashboard for the IMU-Basic board.

    python dashboard.py

Shows the last 10 s of acceleration, angular velocity, magnetic field and
temperature, plus live values, orientation, motion state and detected events.
Buttons: Pause the plots, Record to CSV (saved in pc-app/recordings/), and
Calibrate compass (press, rotate the board every which way, press again).
"""

import csv
import math
import textwrap
import time
from collections import deque
from datetime import datetime
from pathlib import Path

import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation
from matplotlib.widgets import Button

from imu_ble import IMUClient, Reading
from processing import CompassCalibration, MotionDetector, Orientation

WINDOW_S = 10.0              # seconds of history shown in the plots
MAX_POINTS = int(WINDOW_S * 100) + 50
RECORDINGS_DIR = Path(__file__).with_name("recordings")
AXIS_COLORS = ("tab:red", "tab:green", "tab:blue")


class Dashboard:
    def __init__(self, client: IMUClient):
        self.client = client
        self.compass = CompassCalibration()
        self.orientation = Orientation(self.compass)
        self.motion = MotionDetector()

        self.times = deque(maxlen=MAX_POINTS)
        self.series = {name: [deque(maxlen=MAX_POINTS) for _ in range(3)]
                       for name in ("accel", "gyro", "mag")}
        self.temps = deque(maxlen=MAX_POINTS)
        self.latest: Reading = None
        self.events = deque(maxlen=8)
        self.arrivals = deque()          # PC arrival times, for the rate display
        self.paused = False
        self.recording = None            # (file, csv writer, path) while recording

        self._build_figure()
        self._log("waiting for board...")

    # -- layout ---------------------------------------------------------------

    def _build_figure(self) -> None:
        self.fig = plt.figure(figsize=(14, 8))
        try:
            self.fig.canvas.manager.set_window_title("IMU Dashboard")
        except AttributeError:
            pass
        gs = self.fig.add_gridspec(2, 3, width_ratios=[1, 1, 0.9],
                                   left=0.05, right=0.98, top=0.93, bottom=0.08,
                                   wspace=0.25, hspace=0.3)
        self.ax = {
            "accel": self.fig.add_subplot(gs[0, 0]),
            "gyro": self.fig.add_subplot(gs[0, 1]),
            "mag": self.fig.add_subplot(gs[1, 0]),
            "temp": self.fig.add_subplot(gs[1, 1]),
        }
        titles = {"accel": "Acceleration (m/s²)", "gyro": "Angular velocity (°/s)",
                  "mag": "Magnetic field (µT)", "temp": "Temperature (°C)"}
        self.lines = {}
        for name, ax in self.ax.items():
            ax.set_title(titles[name], fontsize=10)
            ax.set_xlim(-WINDOW_S, 0)
            ax.grid(alpha=0.3)
            ax.set_xlabel("seconds ago", fontsize=8)
            ax.tick_params(labelsize=8)
            if name == "temp":
                self.lines[name] = [ax.plot([], [], color="tab:orange")[0]]
            else:
                self.lines[name] = [ax.plot([], [], color=c, lw=1, label=l)[0]
                                    for c, l in zip(AXIS_COLORS, "xyz")]
                ax.legend(loc="upper left", fontsize=8, ncol=3)

        self.info_ax = self.fig.add_subplot(gs[:, 2])
        self.info_ax.axis("off")
        self.info_text = self.info_ax.text(0, 1, "", va="top", ha="left",
                                           family="monospace", fontsize=8.5)
        self.title = self.fig.suptitle("", fontsize=12)

        self.btn_pause = Button(self.fig.add_axes([0.70, 0.015, 0.08, 0.045]), "Pause")
        self.btn_record = Button(self.fig.add_axes([0.79, 0.015, 0.08, 0.045]), "Record")
        self.btn_compass = Button(self.fig.add_axes([0.88, 0.015, 0.10, 0.045]), "Calibrate compass")
        self.btn_pause.on_clicked(lambda _: self.toggle_pause())
        self.btn_record.on_clicked(lambda _: self.toggle_record())
        self.btn_compass.on_clicked(lambda _: self.toggle_compass())
        self.fig.canvas.mpl_connect("close_event", lambda _: self.close())

    # -- buttons ------------------------------------------------------------------

    def toggle_pause(self) -> None:
        self.paused = not self.paused
        self.btn_pause.label.set_text("Resume" if self.paused else "Pause")

    def toggle_record(self) -> None:
        if self.recording is None:
            RECORDINGS_DIR.mkdir(exist_ok=True)
            path = RECORDINGS_DIR / f"imu_{datetime.now():%Y%m%d_%H%M%S}.csv"
            f = open(path, "w", newline="")
            w = csv.writer(f)
            w.writerow(["time_s", "ax", "ay", "az", "gx", "gy", "gz", "mx", "my", "mz",
                        "temp_c", "roll", "pitch", "heading", "motion"])
            self.recording = (f, w, path)
            self.btn_record.label.set_text("Stop rec")
            self._log(f"recording {path.name}")
        else:
            f, _, path = self.recording
            f.close()
            self.recording = None
            self.btn_record.label.set_text("Record")
            self._log(f"saved {path.name}")

    def toggle_compass(self) -> None:
        if not self.compass.collecting:
            self.compass.start()
            self.btn_compass.label.set_text("Finish calibration")
            self._log("compass cal: rotate the board slowly in all directions")
        else:
            self._log(self.compass.finish())
            self.btn_compass.label.set_text("Calibrate compass")

    def close(self) -> None:
        if self.recording is not None:
            self.toggle_record()
        self.client.stop()

    # -- data -----------------------------------------------------------------

    def _log(self, msg: str) -> None:
        self.events.appendleft(f"{datetime.now():%H:%M:%S} {msg}")

    def _handle(self, r: Reading) -> None:
        self.latest = r
        self.compass.add(r.mag)
        roll, pitch, heading = self.orientation.update(r)
        for event in self.motion.update(r):
            self._log(event)

        if self.recording is not None:
            self.recording[1].writerow([f"{r.time_s:.3f}", *r.accel, *r.gyro, *r.mag,
                                        f"{r.temp_c:.2f}", f"{roll:.2f}", f"{pitch:.2f}",
                                        f"{heading:.1f}", self.motion.state])
        if not self.paused:
            self.times.append(r.time_s)
            mag_cal = self.compass.apply(r.mag)
            for name, vec in (("accel", r.accel), ("gyro", r.gyro), ("mag", mag_cal)):
                for i in range(3):
                    self.series[name][i].append(vec[i])
            self.temps.append(r.temp_c)

    def step(self, _frame=None) -> None:
        """Pull new readings from Bluetooth and redraw. Called ~20x per second."""
        new = self.client.get_available()
        now = time.monotonic()
        for r in new:
            self._handle(r)
            self.arrivals.append(now)
        while self.arrivals and now - self.arrivals[0] > 1.0:
            self.arrivals.popleft()

        if self.times and not self.paused:
            t_last = self.times[-1]
            x = [t - t_last for t in self.times]
            for name in ("accel", "gyro", "mag"):
                for line, ys in zip(self.lines[name], self.series[name]):
                    line.set_data(x, ys)
            self.lines["temp"][0].set_data(x, self.temps)
            self.ax["mag"].set_title("Magnetic field (µT, calibrated)" if self._compass_calibrated()
                                     else "Magnetic field (µT, raw)", fontsize=10)
            for ax in self.ax.values():
                ax.relim()
                ax.autoscale_view(scalex=False)
        self._update_text()

    def _update_text(self) -> None:
        c = self.client
        state = "CONNECTED" if c.connected else "not connected"
        self.title.set_text(f"IMU-Basic  —  {state}  —  {len(self.arrivals)} readings/s"
                            + ("  —  PAUSED" if self.paused else "")
                            + ("  —  ● REC" if self.recording else ""))
        self.title.set_color("black" if c.connected else "tab:red")

        lines = []
        r = self.latest
        if r is None:
            lines.append(c.status)
        else:
            def vec(v, fmt):
                return "  ".join(format(x, fmt) for x in v)
            a_mag = math.sqrt(sum(v * v for v in r.accel))
            g_mag = math.sqrt(sum(v * v for v in r.gyro))
            m_mag = math.sqrt(sum(v * v for v in self.compass.apply(r.mag)))
            o = self.orientation
            lines += [
                f"LIVE VALUES   {'x':>7}  {'y':>7}  {'z':>7}",
                f"accel  m/s²   {vec(r.accel, '7.2f')}",
                f"gyro   °/s    {vec(r.gyro, '7.1f')}",
                f"mag    µT     {vec(r.mag, '7.1f')}",
                "",
                f"|accel| {a_mag:6.2f} m/s²  ({a_mag / 9.80665:.2f} g)",
                f"|gyro|  {g_mag:6.1f} °/s",
                f"|mag|   {m_mag:6.1f} µT  (Earth: 25–65)",
                f"temp    {r.temp_c:6.1f} °C",
                f"board time {r.time_s:9.1f} s",
                "",
                "ORIENTATION",
                f"roll    {o.roll:+7.1f}°",
                f"pitch   {o.pitch:+7.1f}°",
                f"heading {o.heading:7.1f}°"
                + ("" if self._compass_calibrated() else "  (uncalibrated)"),
                "",
                f"MOTION  {self.motion.state}",
            ]
            if self.compass.collecting:
                p = self.compass.progress()
                lines += ["", "COMPASS CALIBRATION",
                          "axis swing  " + "  ".join(f"{'xyz'[i]}:{p[i] * 100:3.0f}%" for i in range(3)),
                          "rotate until all reach 100%,", "then press Finish"]
        lines += ["", "EVENTS"]
        for e in self.events:
            lines += textwrap.wrap(e, 46, subsequent_indent="         ")
        self.info_text.set_text("\n".join(lines))

    def _compass_calibrated(self) -> bool:
        return self.compass.offset != [0.0, 0.0, 0.0]

    def run(self) -> None:
        self._anim = FuncAnimation(self.fig, self.step, interval=50, cache_frame_data=False)
        plt.show()


def main() -> None:
    client = IMUClient(verbose=False).start()
    try:
        Dashboard(client).run()
    finally:
        client.stop()


if __name__ == "__main__":
    main()
