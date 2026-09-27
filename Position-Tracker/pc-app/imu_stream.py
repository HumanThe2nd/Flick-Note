"""Read the IMU stream from the Feather ESP32-S3 in real time.

Use as a library:

    from imu_stream import imu_samples
    for s in imu_samples():
        print(s.ax, s.ay, s.az)

Data comes over Bluetooth LE by default (device "Glove-IMU"); pass
transport="usb" / --usb to read the USB serial port instead.

Pressing the board's D1 button sets `recenter=True` on the next sample.
Battery and button state are kept in `imu_stream.device_status`.

Or from the command line:

    python imu_stream.py              # print samples (Bluetooth)
    python imu_stream.py --usb        # print samples (USB cable)
    python imu_stream.py --csv log.csv
    python imu_stream.py --plot       # live accel plot
"""

import argparse
import asyncio
import atexit
import queue
import struct
import sys
import threading
import time
from collections import deque
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator, Optional

import serial
import serial.tools.list_ports

ESPRESSIF_VID = 0x303A
ADAFRUIT_VID = 0x239A

BLE_NAME = "Glove-IMU"
BLE_DATA_UUID = "4f1e0002-8a3c-4b7e-9d52-1c0de5a7a001"
BLE_INFO_UUID = "4f1e0003-8a3c-4b7e-9d52-1c0de5a7a001"
BLE_STATUS_UUID = "4f1e0004-8a3c-4b7e-9d52-1c0de5a7a001"
# uint8 version, uint8 buttons, uint16 recenter_count, uint16 battery_mv, uint8 battery_pct
_BLE_STATUS = struct.Struct("<BBHHB")
# uint32 t_us, int16 accel xyz (1/500 m/s^2), int16 gyro xyz (1/50 deg/s)
_BLE_SAMPLE = struct.Struct("<I6h")


@dataclass
class Sample:
    t: float    # seconds since device boot
    ax: float   # m/s^2
    ay: float
    az: float
    gx: float   # deg/s
    gy: float
    gz: float
    buttons: int = 0         # bit0 = D0, bit1 = D1, bit2 = D2 (pressed)
    recenter: bool = False   # D1 was pressed since the previous sample


@dataclass
class DeviceStatus:
    buttons: int = 0
    battery_mv: int = 0
    battery_pct: Optional[int] = None   # None if the board has no fuel gauge
    recenter_count: Optional[int] = None
    _recenter_pending: bool = False

    def update(self, buttons: int, recenter_count: int, mv: int, pct: int, verbose: bool) -> None:
        pct_v = None if pct == 0xFF else pct
        if verbose and pct_v is not None and (self.battery_pct is None or abs(pct_v - self.battery_pct) >= 5):
            print(f"# battery {mv / 1000:.2f} V, {pct_v}%", file=sys.stderr)
        if self.recenter_count is not None and recenter_count != self.recenter_count:
            self._recenter_pending = True
        self.buttons, self.recenter_count = buttons, recenter_count
        self.battery_mv, self.battery_pct = mv, pct_v

    def recenter_pressed(self, recenter_count: int) -> None:
        self.recenter_count = recenter_count
        self._recenter_pending = True

    def stamp(self, s: "Sample") -> "Sample":
        """Attach button state and any pending re-center request to a sample."""
        s.buttons = self.buttons
        if self._recenter_pending:
            s.recenter = True
            self._recenter_pending = False
        return s

    def new_connection(self) -> None:
        # The count is compared across updates, not connections, so a press
        # while disconnected (or a board reboot) doesn't trigger a re-center.
        self.recenter_count = None
        self._recenter_pending = False


# Latest status of the connected board (battery, buttons).
device_status = DeviceStatus()


def find_port() -> str:
    ports = list(serial.tools.list_ports.comports())
    for vid in (ESPRESSIF_VID, ADAFRUIT_VID):
        for p in ports:
            if p.vid == vid:
                return p.device
    raise RuntimeError("Feather not found. Ports: " + ", ".join(p.device for p in ports))


def imu_samples(port: Optional[str] = None, verbose: bool = True,
                transport: str = "ble", device: str = BLE_NAME) -> Iterator[Sample]:
    """Yield samples forever, reconnecting if the board resets or goes away.

    transport="ble" (default) connects over Bluetooth to `device` (name or address);
    transport="usb" (or passing `port`) reads the USB serial port instead.
    """
    if transport == "usb" or port:
        return _usb_samples(port, verbose)
    return _ble_samples(device, verbose)


def _ble_samples(device: str, verbose: bool) -> Iterator[Sample]:
    q: "queue.Queue[Sample]" = queue.Queue(maxsize=10000)
    stop = threading.Event()
    th = threading.Thread(target=lambda: asyncio.run(_ble_worker(device, q, stop, verbose)),
                          daemon=True)
    th.start()

    def shutdown() -> None:
        # Disconnect cleanly; otherwise Windows can hold the link open after we
        # exit, and the board won't advertise (or be found) until it times out.
        stop.set()
        th.join(timeout=5)

    atexit.register(shutdown)
    try:
        while True:
            try:
                yield q.get(timeout=0.5)
            except queue.Empty:
                if not th.is_alive():
                    raise RuntimeError("BLE worker stopped")
    finally:
        shutdown()
        atexit.unregister(shutdown)


async def _ble_worker(device: str, q: "queue.Queue[Sample]", stop: threading.Event,
                      verbose: bool) -> None:
    from bleak import BleakClient, BleakScanner

    def log(msg: str) -> None:
        if verbose:
            print(msg, file=sys.stderr)

    while not stop.is_set():
        try:
            if ":" in device:
                target = await BleakScanner.find_device_by_address(device, timeout=5)
            else:
                target = await BleakScanner.find_device_by_name(device, timeout=5)
            if target is None:
                # A board that is still connected (e.g. Windows kept an old link
                # alive) doesn't advertise; connecting by address still works.
                target = device if ":" in device else _load_cached_address()
                if target is None:
                    log(f"# waiting for BLE device '{device}' (not found; is it powered on?)")
                    continue
            address = target if isinstance(target, str) else target.address
            name = device if isinstance(target, str) else target.name

            disconnected = asyncio.Event()
            wrap = {"last": None, "offset": 0}
            device_status.new_connection()

            def on_status(_, data: bytearray) -> None:
                if len(data) >= _BLE_STATUS.size:
                    _, buttons, count, mv, pct = _BLE_STATUS.unpack_from(data)
                    device_status.update(buttons, count, mv, pct, verbose)

            def on_notify(_, data: bytearray) -> None:
                for off in range(0, len(data) - len(data) % _BLE_SAMPLE.size, _BLE_SAMPLE.size):
                    t, ax, ay, az, gx, gy, gz = _BLE_SAMPLE.unpack_from(data, off)
                    # The device timestamp is 32-bit microseconds (wraps every ~71 min).
                    if wrap["last"] is not None and t < wrap["last"] - (1 << 31):
                        wrap["offset"] += 1 << 32
                    wrap["last"] = t
                    s = device_status.stamp(Sample((t + wrap["offset"]) / 1e6,
                                                   ax / 500, ay / 500, az / 500,
                                                   gx / 50, gy / 50, gz / 50))
                    try:
                        q.put_nowait(s)
                    except queue.Full:
                        pass  # consumer is not keeping up; drop

            async with BleakClient(target, timeout=10,
                                   disconnected_callback=lambda _: disconnected.set()) as client:
                info = (await client.read_gatt_char(BLE_INFO_UUID)).decode(errors="replace")
                log(f"# connected over BLE to {name} [{address}] {info}")
                _save_cached_address(address)
                try:
                    on_status(None, await client.read_gatt_char(BLE_STATUS_UUID))
                    await client.start_notify(BLE_STATUS_UUID, on_status)
                except Exception:
                    log("# (firmware has no status characteristic; buttons/battery unavailable)")
                await client.start_notify(BLE_DATA_UUID, on_notify)
                while not stop.is_set() and not disconnected.is_set():
                    await asyncio.sleep(0.2)
            log("# BLE disconnected")
        except Exception as e:  # bleak raises various OS-specific errors
            log(f"# BLE error: {e!r}; retrying")
            await asyncio.sleep(1)


_ADDRESS_CACHE = Path(__file__).with_name(".glove_ble_address")


def _load_cached_address() -> Optional[str]:
    try:
        return _ADDRESS_CACHE.read_text().strip() or None
    except OSError:
        return None


def _save_cached_address(address: str) -> None:
    try:
        _ADDRESS_CACHE.write_text(address)
    except OSError:
        pass


def _usb_samples(port: Optional[str], verbose: bool) -> Iterator[Sample]:
    while True:
        try:
            dev = port or find_port()
            # Leave DTR/RTS deasserted: toggling them on open resets the ESP32-S3.
            ser = serial.Serial()
            ser.port, ser.baudrate, ser.timeout = dev, 115200, 1
            ser.dtr = ser.rts = False
            ser.open()
            with ser:
                if verbose:
                    print(f"# connected to {dev}", file=sys.stderr)
                ser.reset_input_buffer()
                device_status.new_connection()
                while True:
                    line = ser.readline().decode("ascii", "replace").strip()
                    if line.startswith("D,"):
                        f = line.split(",")
                        if len(f) != 8:
                            continue
                        try:
                            v = [float(x) for x in f[1:]]
                        except ValueError:
                            continue
                        yield device_status.stamp(Sample(v[0] / 1e6, *v[1:]))
                    elif line.startswith(("S,", "E,recenter,")):
                        f = line.split(",")
                        try:
                            if f[0] == "S":
                                device_status.update(int(f[1]), int(f[2]), int(f[3]), int(f[4]), verbose)
                            else:
                                device_status.recenter_pressed(int(f[2]))
                        except (ValueError, IndexError):
                            pass
                    elif line.startswith("#") and verbose:
                        print(line, file=sys.stderr)
        except (serial.SerialException, RuntimeError, OSError) as e:
            if verbose:
                print(f"# waiting for device ({e})", file=sys.stderr)
            time.sleep(1)


def run_plot(samples: Iterator[Sample], window: int = 500) -> None:
    import matplotlib.pyplot as plt

    buf = {k: deque(maxlen=window) for k in ("t", "ax", "ay", "az")}
    plt.ion()
    fig, ax = plt.subplots()
    lines = {k: ax.plot([], [], label=k)[0] for k in ("ax", "ay", "az")}
    ax.set_xlabel("time (s)")
    ax.set_ylabel("acceleration (m/s^2)")
    ax.legend(loc="upper left")
    last_draw = 0.0
    for s in samples:
        buf["t"].append(s.t)
        for k in ("ax", "ay", "az"):
            buf[k].append(getattr(s, k))
        if time.monotonic() - last_draw > 1 / 30:
            if not plt.fignum_exists(fig.number):
                return
            for k, ln in lines.items():
                ln.set_data(buf["t"], buf[k])
            ax.relim()
            ax.autoscale_view()
            fig.canvas.flush_events()
            plt.pause(0.001)
            last_draw = time.monotonic()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--usb", action="store_true", help="read over the USB cable instead of Bluetooth")
    ap.add_argument("--port", help="USB serial port (implies --usb; auto-detected if omitted)")
    ap.add_argument("--device", default=BLE_NAME, help="BLE device name or address")
    ap.add_argument("--csv", help="also append samples to this CSV file")
    ap.add_argument("--plot", action="store_true", help="show a live acceleration plot")
    args = ap.parse_args()

    samples = imu_samples(args.port, transport="usb" if args.usb else "ble", device=args.device)
    if args.csv:
        samples = _tee_csv(samples, args.csv)
    try:
        if args.plot:
            run_plot(samples)
        else:
            for s in samples:
                print(f"{s.t:10.3f}  a=({s.ax:+8.3f},{s.ay:+8.3f},{s.az:+8.3f}) m/s^2  "
                      f"g=({s.gx:+8.2f},{s.gy:+8.2f},{s.gz:+8.2f}) deg/s")
    except KeyboardInterrupt:
        pass


def _tee_csv(samples: Iterator[Sample], path: str) -> Iterator[Sample]:
    with open(path, "a", buffering=1) as f:
        if f.tell() == 0:
            f.write("t,ax,ay,az,gx,gy,gz\n")
        for s in samples:
            f.write(f"{s.t:.6f},{s.ax},{s.ay},{s.az},{s.gx},{s.gy},{s.gz}\n")
            yield s


if __name__ == "__main__":
    main()
