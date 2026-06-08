import sys
import os
import math
import time

import numpy

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import Python.fsds as fsds

# ---------------------------------------------------------------------------
# Autonomous system constants
# ---------------------------------------------------------------------------
MAX_THROTTLE = 0.25  # Maximum throttle output (0–1)
MAX_BRAKE = 0.3  # Maximum brake output when overspeeding
TARGET_SPEED = 7.5  # Target vehicle speed in m/s
MAX_STEERING = 0.75  # Maximum steering angle (–1 to 1 scale)
CONES_RANGE_CUTOFF = 7  # Only consider cones within this range (metres)
POINT_GROUP_THRESHOLD = 0.1  # Points within 10 cm belong to the same cone
MIN_GROUP_SIZE = 2  # Minimum lidar points to count as a cone
LOOP_RATE = 0.05  # Main loop period in seconds (20 Hz)

# Steering PD gains
KP = 1.6  # Proportional gain – strength of correction towards midpoint
KD = 0.3  # Derivative gain – damps rapid error changes, prevents overshoot

# Lookahead: project this many seconds ahead when sampling the midpoint.
# Higher values = smoother cornering but slower reaction to tight bends.
LOOKAHEAD_TIME = 0.2  # seconds

# Speed-dependent steering cap: limit = MAX_STEERING / (1 + k * speed)
# Prevents overcorrection at high speed on corners.
SPEED_SCALING = 0.06

# ---------------------------------------------------------------------------
# Simulator connection
# ---------------------------------------------------------------------------
client = fsds.FSDSClient()
client.confirmConnection()
client.enableApiControl(True)


# ---------------------------------------------------------------------------
# Lidar / cone helpers
# ---------------------------------------------------------------------------


def pointgroup_to_cone(group):
    """Return the centroid of a list of {'x', 'y'} points."""
    n = len(group)
    return {
        "x": sum(p["x"] for p in group) / n,
        "y": sum(p["y"] for p in group) / n,
    }


def distance(x1, y1, x2, y2):
    """Euclidean distance between two 2-D points."""
    return math.sqrt((x1 - x2) ** 2 + (y1 - y2) ** 2)


def find_cones():
    """
    Parse the lidar point cloud and return detected cones as a list of
    {'x', 'y'} dicts in the vehicle's local frame.
    """
    lidardata = client.getLidarData(lidar_name="Lidar")

    if len(lidardata.point_cloud) < 3:
        return []

    points = numpy.array(lidardata.point_cloud, dtype=numpy.float32)
    points = points.reshape(-1, 3)

    cones = []
    current_group = []

    for i in range(1, len(points)):
        dist_to_prev = distance(
            points[i][0],
            points[i][1],
            points[i - 1][0],
            points[i - 1][1],
        )

        if dist_to_prev < POINT_GROUP_THRESHOLD:
            current_group.append({"x": points[i][0], "y": points[i][1]})
        else:
            if len(current_group) >= MIN_GROUP_SIZE:
                cone = pointgroup_to_cone(current_group)
                if distance(0, 0, cone["x"], cone["y"]) < CONES_RANGE_CUTOFF:
                    cones.append(cone)
            current_group = []

    # Flush the last group
    if len(current_group) >= MIN_GROUP_SIZE:
        cone = pointgroup_to_cone(current_group)
        if distance(0, 0, cone["x"], cone["y"]) < CONES_RANGE_CUTOFF:
            cones.append(cone)

    return cones


def classify_cones(cones):
    """Split cones into left (y > 0) and right (y < 0) boundary lists."""
    left = [c for c in cones if c["y"] > 0]
    right = [c for c in cones if c["y"] <= 0]
    return left, right


def get_speed():
    """Return current vehicle speed in m/s from GPS."""
    gps = client.getGpsData()
    v = gps.gnss.velocity
    return math.sqrt(v.x_val**2 + v.y_val**2)


# ---------------------------------------------------------------------------
# Midpoint / lookahead
# ---------------------------------------------------------------------------


def weighted_avg(cone_list, key):
    """
    Proximity-weighted average of a cone field.
    Closer cones get higher weight so immediate obstacles dominate.
    """
    weights = [1.0 / max(distance(0, 0, c["x"], c["y"]), 0.1) for c in cone_list]
    total = sum(weights)
    return sum(c[key] * w for c, w in zip(cone_list, weights)) / total


def compute_midpoint(left_cones, right_cones):
    """
    Return the (x, y) midpoint between left and right cone boundaries.
    Falls back gracefully when only one side is visible.
    """
    if left_cones and right_cones:
        mx = (weighted_avg(left_cones, "x") + weighted_avg(right_cones, "x")) / 2
        my = (weighted_avg(left_cones, "y") + weighted_avg(right_cones, "y")) / 2
    elif left_cones:
        mx = weighted_avg(left_cones, "x")
        my = weighted_avg(left_cones, "y") / 2
    elif right_cones:
        mx = weighted_avg(right_cones, "x")
        my = weighted_avg(right_cones, "y") / 2
    else:
        return None
    return mx, my


def compute_lookahead_error(cones, speed):
    """
    Compute lateral error to the track midpoint at a lookahead distance.

    Projects the vehicle forward by LOOKAHEAD_TIME * speed metres along +x,
    then asks where the track centre is relative to that projected position.
    This gives the controller early warning of upcoming corners so it starts
    turning before it's already past the apex, preventing outside-cone hits.
    """
    lookahead_dist = LOOKAHEAD_TIME * speed

    # Shift cone positions back by the lookahead distance
    shifted = [{"x": c["x"] - lookahead_dist, "y": c["y"]} for c in cones]

    # Prefer cones still forward of the lookahead point; fall back if none
    ahead = [c for c in shifted if c["x"] > 0] or shifted

    left_cones, right_cones = classify_cones(ahead)
    midpoint = compute_midpoint(left_cones, right_cones)
    if midpoint is None:
        return None

    # y component is lateral offset: positive = midpoint left of vehicle
    return midpoint[1]


# ---------------------------------------------------------------------------
# Control calculations
# ---------------------------------------------------------------------------


def calculate_steering(cones, speed, prev_error, dt):
    """
    PD controller on lateral error to the lookahead midpoint.

    P term: corrects current offset.
    D term: opposes rapid changes in error – damping that prevents the
            overshoot which clips the outside cone on fast corners.

    The steering ceiling also shrinks with speed so high-speed inputs
    stay gentle.
    """
    error = compute_lookahead_error(cones, speed)
    if error is None:
        return 0.0, prev_error

    p_term = KP * error
    d_term = KD * (error - prev_error) / max(dt, 1e-3)

    raw = -(p_term + d_term)

    speed_limit = MAX_STEERING / (1 + SPEED_SCALING * speed)
    steering = float(numpy.clip(raw, -speed_limit, speed_limit))

    return steering, error


def calculate_throttle_and_brake(speed):
    """
    Proportional throttle below target speed; proportional brake above it.
    """
    speed_error = 1 - speed / TARGET_SPEED

    if speed_error >= 0:
        return MAX_THROTTLE * min(speed_error, 1.0), 0.0
    else:
        return 0.0, MAX_BRAKE * min(-speed_error, 1.0)


# ---------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------

prev_error = 0.0
prev_time = None
last_steering = 0.0

while True:
    now = time.monotonic()
    dt = (now - prev_time) if prev_time is not None else LOOP_RATE
    prev_time = now

    try:
        cones = find_cones()
        speed = get_speed()

        if not cones:
            # No cones detected – coast and hold last steering angle
            car_controls = fsds.CarControls()
            car_controls.steering = last_steering
            car_controls.throttle = 0.0
            car_controls.brake = 0.0
            client.setCarControls(car_controls)
            time.sleep(LOOP_RATE)
            continue

        steering, prev_error = calculate_steering(cones, speed, prev_error, dt)
        throttle, brake = calculate_throttle_and_brake(speed)

        last_steering = steering

        car_controls = fsds.CarControls()
        car_controls.steering = steering
        car_controls.throttle = throttle
        car_controls.brake = brake
        client.setCarControls(car_controls)

    except Exception as e:
        print(f"[WARN] Frame skipped: {e}")

    time.sleep(LOOP_RATE)
