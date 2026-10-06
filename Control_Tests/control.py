"""Editable controller entry point. The launcher supplies the current centreline."""

import argparse
import json
import math
import socket

import msgpack


class SimulatorCar:
    """Synchronous FSDS commands; entering this context does not acquire control."""

    def __init__(self, port=41451):
        self.port = port
        self.socket = None
        self.reader = msgpack.Unpacker(raw=False)
        self.sequence = 0
        self.owns_control = False

    def __enter__(self):
        self.socket = socket.create_connection(("127.0.0.1", self.port), timeout=3)
        return self

    def call(self, method, *arguments):
        self.sequence += 1
        self.socket.sendall(msgpack.packb([0, self.sequence, method, list(arguments)], use_bin_type=True))
        while True:
            for response in self.reader:
                if not isinstance(response, list) or len(response) != 4:
                    raise RuntimeError("Malformed simulator response")
                kind, request_id, error, result = response
                if kind != 1 or request_id != self.sequence:
                    raise RuntimeError("Unexpected simulator response")
                if error is not None:
                    raise RuntimeError(str(error))
                return result
            chunk = self.socket.recv(65536)
            if not chunk:
                raise ConnectionError("Simulator disconnected")
            self.reader.feed(chunk)

    def enable_control(self):
        self.call("enableApiControl", True, "FSCar")
        self.owns_control = True

    def get_state(self):
        return self.call("getCarState", "FSCar")

    def get_pose(self):
        return self.call("simGetGroundTruthKinematics", "FSCar")

    def set_controls(self, throttle=0.0, steering=0.0, brake=0.0):
        if not self.owns_control:
            raise RuntimeError("Call car.enable_control() before commanding the car")
        values = {"throttle": throttle, "steering": steering, "brake": brake}
        for key, value in values.items():
            low = -1 if key == "steering" else 0
            if not math.isfinite(value) or not low <= value <= 1:
                raise ValueError(f"{key} must be finite and between {low} and 1")
        self.call("setCarControls", {
            **values,
            "handbrake": False,
            "is_manual_gear": True,
            "manual_gear": 1,
            "gear_immediate": True,
        }, "FSCar")

    def stop(self):
        self.set_controls(brake=1.0)

    def __exit__(self, exc_type, exc_value, traceback):
        try:
            if self.owns_control:
                try:
                    self.stop()
                finally:
                    self.call("enableApiControl", False, "FSCar")
        finally:
            self.owns_control = False
            self.socket.close()


def run_control(centreline, car):
    """Put your driving logic here. See HOW_TO_CONTROL.txt for an example."""
    waypoints = centreline["waypoints"]
    print(f"control.py received {len(waypoints)} waypoints; closed loop: {centreline['closed']}")
    for point in waypoints[:5]:
        print(f"  {point['index']}: ({point['x_m']:.3f}, {point['y_m']:.3f}) m")
    state = car.get_state()
    print(f"Simulator connected; current speed: {state['speed']:.2f} m/s")
    print("Add your driving logic to run_control() in Control_Tests/control.py.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--centreline", required=True, help="Centreline JSON supplied by the launcher")
    parser.add_argument("--port", type=int, default=41451)
    args = parser.parse_args()
    with open(args.centreline, encoding="utf-8-sig") as source:
        centreline = json.load(source)
    points = centreline.get("waypoints", [])
    if len(points) < 2:
        raise ValueError("The centreline needs at least two waypoints")
    for point in points:
        if not all(math.isfinite(point[key]) for key in ("x_m", "y_m", "track_width_m")):
            raise ValueError("Invalid centreline coordinates")
    with SimulatorCar(args.port) as car:
        run_control(centreline, car)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("Controller stopped.")
