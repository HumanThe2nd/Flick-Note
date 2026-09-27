"""Bluetooth client for the IMU-Basic firmware.

    from imu_ble import IMUClient

    with IMUClient() as imu:
        for r in imu.readings():          # blocks until each new reading arrives
            print(r.accel, r.gyro, r.mag, r.temp_c)

The client connects in a background thread, reconnects automatically if the
board goes out of range or restarts, and disconnects cleanly on exit.
"""

import asyncio
import queue
import struct
import sys
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Iterator, Optional, Tuple

DEVICE_NAME = "IMU-Basic"
READINGS_UUID = "12340001-0000-4000-8000-00805f9b0001"

# Must match imu_reading_t in firmware/src/main.c: uint32 time_ms, then 10 floats.
_PACKET = struct.Struct("<I10f")

Vec3 = Tuple[float, float, float]


@dataclass
class Reading:
    time_s: float    # seconds since the board started
    accel: Vec3      # m/s^2
    gyro: Vec3       # deg/s
    mag: Vec3        # microtesla
    temp_c: float    # deg C

    @staticmethod
    def from_bytes(data: bytes) -> "Reading":
        t, ax, ay, az, gx, gy, gz, mx, my, mz, temp = _PACKET.unpack_from(data)
        return Reading(t / 1000, (ax, ay, az), (gx, gy, gz), (mx, my, mz), temp)


class IMUClient:
    def __init__(self, device_name: str = DEVICE_NAME, verbose: bool = True):
        self.device_name = device_name
        self.verbose = verbose
        self.connected = False
        self.status = "starting"
        self._queue: "queue.Queue[Reading]" = queue.Queue(maxsize=5000)
        self._stop = threading.Event()
        self._thread = threading.Thread(target=lambda: asyncio.run(self._run()), daemon=True)

    # -- public API -------------------------------------------------------------

    def start(self) -> "IMUClient":
        self._thread.start()
        return self

    def stop(self) -> None:
        """Disconnect. Waits briefly so Windows doesn't keep a stale connection."""
        self._stop.set()
        if self._thread.is_alive():
            self._thread.join(timeout=5)

    def readings(self, timeout: Optional[float] = None) -> Iterator[Reading]:
        """Yield readings as they arrive. With a timeout, stops after that many
        seconds without data; otherwise waits forever (through reconnects)."""
        while not self._stop.is_set():
            try:
                yield self._queue.get(timeout=timeout if timeout is not None else 0.5)
            except queue.Empty:
                if timeout is not None:
                    return

    def get_available(self) -> list:
        """Return all readings received since the last call, without waiting."""
        items = []
        while True:
            try:
                items.append(self._queue.get_nowait())
            except queue.Empty:
                return items

    def __enter__(self) -> "IMUClient":
        return self.start()

    def __exit__(self, *exc) -> None:
        self.stop()

    # -- background connection loop --------------------------------------------

    def _log(self, msg: str) -> None:
        self.status = msg
        if self.verbose:
            print(f"[imu_ble] {msg}", file=sys.stderr)

    def _on_notify(self, _handle, data: bytearray) -> None:
        if len(data) < _PACKET.size:
            return
        try:
            self._queue.put_nowait(Reading.from_bytes(data))
        except queue.Full:
            pass   # the program isn't reading fast enough; drop this one

    async def _run(self) -> None:
        from bleak import BleakClient, BleakScanner

        while not self._stop.is_set():
            try:
                self._log(f"searching for '{self.device_name}'...")
                device = await BleakScanner.find_device_by_name(self.device_name, timeout=5)
                if device is None:
                    # Windows may still hold an old connection, which hides the
                    # board from scans; try its last known address directly.
                    device = _cached_address()
                    if device is None:
                        self._log(f"'{self.device_name}' not found; is the board powered on?")
                        continue

                disconnected = asyncio.Event()
                async with BleakClient(device, timeout=10,
                                       disconnected_callback=lambda _: disconnected.set()) as client:
                    _save_address(client.address)
                    await client.start_notify(READINGS_UUID, self._on_notify)
                    self.connected = True
                    self._log(f"connected to {self.device_name} [{client.address}]")
                    while not self._stop.is_set() and not disconnected.is_set():
                        await asyncio.sleep(0.2)
                self.connected = False
                if not self._stop.is_set():
                    self._log("disconnected; reconnecting...")
            except Exception as e:   # bleak raises various OS-specific errors
                self.connected = False
                self._log(f"connection error: {e}; retrying")
                await asyncio.sleep(1)
        self.connected = False
        self.status = "stopped"


_ADDRESS_FILE = Path(__file__).with_name(".imu_ble_address")


def _cached_address() -> Optional[str]:
    try:
        return _ADDRESS_FILE.read_text().strip() or None
    except OSError:
        return None


def _save_address(address: str) -> None:
    try:
        _ADDRESS_FILE.write_text(address)
    except OSError:
        pass
