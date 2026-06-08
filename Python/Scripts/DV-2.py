import sys
import os
import math
import time

import numpy

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import fsds

# ---------------------------------------------------------------------------
# Autonomous system constants
# ---------------------------------------------------------------------------
MAX_THROTTLE = 0.25  # Maximum throttle output (0–1)
MAX_BRAKE = 0.3  # Maximum brake output when overspeeding
TARGET_SPEED = 10  # Target vehicle speed in m/s
MAX_STEERING = 0.75  # Maximum steering angle (–1 to 1 scale)
CONES_RANGE_CUTOFF = 7  # Only consider cones within this range (metres)
POINT_GROUP_THRESHOLD = 0.1  # Points within 10 cm belong to the same cone
MIN_GROUP_SIZE = 2  # Minimum lidar points to count as a cone
LOOP_RATE = 0.05  # Main loop period in seconds (20 Hz)

# ---------------------------------------------------------------------------
# Steering PID gains
# ---------------------------------------------------------------------------
# KP reduced from 1.6 → 1.0: less aggressive correction, reduces straight-line
#   overshoot which was the primary source of oscillation.
KP = 1.35

# KI is intentionally kept small. It corrects persistent lateral bias (e.g.
#   from a slightly off-centre lidar mount) without dominating the response.
#   Anti-windup clamp (I_MAX) prevents corner accumulation from destabilising
#   the exit straight.
KI = 0.05
I_MAX = 0.25  # Hard clamp on the integrator state

# KD pulled back slightly from 0.6 → 0.5: still strong enough to damp
#   straight-line oscillation, but no longer fighting corner turn-in.
#   D spike clamp (D_CLAMP) prevents lidar flicker from producing impulsive
#   steering inputs.
KD = 0.5
D_CLAMP = 2.0  # Maximum magnitude of the D term before scaling

# ---------------------------------------------------------------------------
# Lookahead / deadband / speed scaling
# ---------------------------------------------------------------------------
# Lookahead at 0.3 s: compromise between 0.2 (too jittery) and 0.4 (skips
#   past tight apex). Reacts to bends early without over-projecting.
LOOKAHEAD_TIME = 0.3  # seconds

# Speed-dependent deadband: tighter on straights (high speed = more damping),
#   looser in corners (low speed = more steering authority).
#   Formula: 0.03 + 0.008 * speed → ~0.03 m at rest, ~0.09 m at 7.5 m/s.
DEADBAND_BASE = 0.03
DEADBAND_SPEED_COEFF = 0.008

# Speed-dependent steering cap: limit = MAX_STEERING / (1 + k * speed).
# Relaxed from 0.10 → 0.07 so the car retains enough authority through corners.
SPEED_SCALING = 0.07

# ---------------------------------------------------------------------------
# Speed control constants
# ---------------------------------------------------------------------------
# Corner braking: target speed is scaled down proportionally to steering demand.
# At full steering lock the target drops to CORNER_SPEED_FLOOR * TARGET_SPEED.
CORNER_BRAKE_GAIN = 0.6  # How aggressively steering reduces target speed
CORNER_SPEED_FLOOR = 0.4  # Minimum fraction of TARGET_SPEED in tightest corner

# Proximity braking: target speed is also scaled down when lateral error is
# large — i.e. the car is drifting toward a cone boundary.
# At ERROR_FULL_BRAKE metres of lateral error, full braking urgency is applied.
PROXIMITY_BRAKE_GAIN = 0.5  # Maximum fractional speed reduction from proximity
ERROR_FULL_BRAKE = 1.0  # Lateral error (m) that triggers maximum urgency

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


def calculate_steering(cones, speed, prev_error, integral, dt):
    """
    PID controller on lateral error to the lookahead midpoint.

    P term: corrects current offset.
    I term: eliminates persistent lateral bias (e.g. lidar misalignment).
            Clamped to I_MAX to prevent windup through corners.
    D term: opposes rapid changes in error — damps oscillation.
            Clamped to D_CLAMP to reject lidar flicker spikes.

    A speed-dependent deadband suppresses noise reactions on straights while
    preserving full steering authority in corners where speed is lower.
    The steering ceiling also shrinks with speed so high-speed inputs
    stay gentle.

    Returns: (steering, new_prev_error, new_integral)
    """
    error = compute_lookahead_error(cones, speed)
    if error is None:
        return 0.0, prev_error, integral

    # Speed-dependent deadband: wider at high speed (straights) to ignore noise,
    # narrower at low speed (corners) to preserve steering authority.
    deadband = DEADBAND_BASE + DEADBAND_SPEED_COEFF * speed
    if abs(error) < deadband:
        error = 0.0

    # Integrator with anti-windup clamp
    integral = float(numpy.clip(integral + error * dt, -I_MAX, I_MAX))

    # D term with spike clamp to reject discontinuous lidar frames
    raw_d = (error - prev_error) / max(dt, 1e-3)
    clamped_d = float(numpy.clip(raw_d, -D_CLAMP, D_CLAMP))

    p_term = KP * error
    i_term = KI * integral
    d_term = KD * clamped_d

    raw = -(p_term + i_term + d_term)

    speed_limit = MAX_STEERING / (1 + SPEED_SCALING * speed)
    steering = float(numpy.clip(raw, -speed_limit, speed_limit))

    return steering, error, integral


def calculate_throttle_and_brake(speed, steering, lateral_error):
    """
    Cone-aware throttle and brake controller.

    Two signals independently reduce the effective target speed:

    1. Steering magnitude — a large steering demand means the car is in a
       corner and needs to slow down. At full lock the target drops to
       CORNER_SPEED_FLOOR * TARGET_SPEED.

    2. Lateral error magnitude — a large lateral offset means the car is
       drifting toward a cone boundary. This fires even before steering has
       fully responded, giving earlier braking into a drift.

    The two reductions are combined and the most conservative (lowest)
    effective target is used, then standard proportional throttle/brake
    is applied against that target.
    """
    # Corner factor: 1.0 on straight, falls to CORNER_SPEED_FLOOR at full lock
    steering_fraction = abs(steering) / MAX_STEERING
    corner_factor = 1.0 - CORNER_BRAKE_GAIN * steering_fraction
    corner_factor = max(corner_factor, CORNER_SPEED_FLOOR)

    # Proximity factor: 1.0 when centred, falls as lateral error grows
    urgency = min(abs(lateral_error) / ERROR_FULL_BRAKE, 1.0)
    proximity_factor = 1.0 - PROXIMITY_BRAKE_GAIN * urgency

    # Use the more conservative of the two
    effective_target = TARGET_SPEED * min(corner_factor, proximity_factor)

    speed_error = 1 - speed / effective_target

    if speed_error >= 0:
        return MAX_THROTTLE * min(speed_error, 1.0), 0.0
    else:
        return 0.0, MAX_BRAKE * min(-speed_error, 1.0)


# ---------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------

prev_error = 0.0
integral = 0.0
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
            # No cones detected – coast and hold last steering angle.
            # Also reset the integrator to prevent windup while blind.
            integral = 0.0
            car_controls = fsds.CarControls()
            car_controls.steering = last_steering
            car_controls.throttle = 0.0
            car_controls.brake = 0.0
            client.setCarControls(car_controls)
            time.sleep(LOOP_RATE)
            continue

        steering, prev_error, integral = calculate_steering(
            cones, speed, prev_error, integral, dt
        )
        throttle, brake = calculate_throttle_and_brake(speed, steering, prev_error)

        last_steering = steering

        car_controls = fsds.CarControls()
        car_controls.steering = steering
        car_controls.throttle = throttle
        car_controls.brake = brake
        client.setCarControls(car_controls)

    except Exception as e:
        print(f"[WARN] Frame skipped: {e}")

    time.sleep(LOOP_RATE)
