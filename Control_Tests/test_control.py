import socket
import threading
import unittest

import msgpack

from control import SimulatorCar


class ControllerChecks(unittest.TestCase):
    def test_wire_commands_and_cleanup_on_error(self):
        requests = []
        errors = []
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        listener.settimeout(3)

        def serve():
            try:
                connection, _ = listener.accept()
                with connection:
                    connection.settimeout(3)
                    reader = msgpack.Unpacker(raw=False)
                    while True:
                        chunk = connection.recv(4096)
                        if not chunk:
                            return
                        reader.feed(chunk)
                        for request in reader:
                            requests.append(request)
                            method = request[2]
                            result = {"speed": 0.75} if method == "getCarState" else None
                            response = msgpack.packb([1, request[1], None, result])
                            connection.sendall(response[:3])
                            connection.sendall(response[3:])
            except Exception as error:
                errors.append(error)

        worker = threading.Thread(target=serve, daemon=True)
        worker.start()
        try:
            with self.assertRaisesRegex(ValueError, "controller failure"):
                with SimulatorCar(listener.getsockname()[1]) as car:
                    self.assertEqual(car.get_state()["speed"], 0.75)
                    with self.assertRaises(RuntimeError):
                        car.set_controls(throttle=0.1)
                    car.enable_control()
                    with self.assertRaises(ValueError):
                        car.set_controls(steering=float("nan"))
                    car.set_controls(throttle=0.1, steering=-0.2)
                    raise ValueError("controller failure")
            worker.join(3)
            self.assertFalse(worker.is_alive())
            self.assertEqual(errors, [])
            self.assertEqual([r[2] for r in requests], [
                "getCarState", "enableApiControl", "setCarControls",
                "setCarControls", "enableApiControl",
            ])
            driving = requests[2][3][0]
            self.assertEqual(driving["manual_gear"], 1)
            self.assertTrue(driving["is_manual_gear"])
            self.assertEqual(driving["steering"], -0.2)
            stopping = requests[3][3][0]
            self.assertEqual((stopping["throttle"], stopping["brake"]), (0, 1))
            self.assertEqual(requests[4][3], [False, "FSCar"])
        finally:
            listener.close()


if __name__ == "__main__":
    unittest.main()
