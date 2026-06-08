import sys
import os
import math
import time

import numpy
from scipy.interpolate import CubicSpline

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..")))
import Python.fsds as fsds

# =============================================================================
# CONSTANTS
# =============================================================================

# --- Vehicle / simulator -------------------------------------------------
MAX_THROTTLE = 0.25  # Maximum throttle output (0–1)
MAX_BRAKE = 0.3  # Maximum brake output (0–1)
MAX_STEERING = 0.75  # Steering angle limit (–1 to 1 scale)
TARGET_SPEED = 10.0  # Desired cruising speed (m/s)
WHEELBASE = 1.5  # Approximate vehicle wheelbase (m)
LOOP_RATE = 0.05  # Main loop period (s) — 20 Hz

# --- Lidar / cone detection ----------------------------------------------
CONES_RANGE_CUTOFF = 7.0  # Ignore cones beyond this range (m)
POINT_GROUP_THRESHOLD = 0.1  # Points within 10 cm belong to the same cone
MIN_GROUP_SIZE = 2  # Minimum lidar points to count as a cone

# --- Cone tracker --------------------------------------------------------
# Cones are matched across frames by nearest-neighbour association.
# A cone must be seen this many consecutive frames before it is trusted.
CONE_CONFIRM_FRAMES = 2
# A cone is dropped after being absent for this many consecutive frames.
CONE_DROP_FRAMES = 5
# Maximum distance (m) to associate a new detection with an existing track.
CONE_MATCH_RADIUS = 0.5

# --- Pure pursuit --------------------------------------------------------
# Adaptive lookahead: base + speed_gain * speed, clamped, then scaled by
# curvature so tight corners shorten the lookahead to the apex.
LOOKAHEAD_BASE = 1.5  # Minimum lookahead at low speed (m)
LOOKAHEAD_SPEED_GAIN = 0.3  # Extra lookahead per m/s of speed (m/(m/s))
LOOKAHEAD_MIN = 1.2  # Hard floor (m)
LOOKAHEAD_MAX = 5.0  # Hard ceiling (m)
# lookahead *= 1 / (1 + CURVATURE_LOOKAHEAD_GAIN * |curvature|)
CURVATURE_LOOKAHEAD_GAIN = 3.0

# Physical steering lock angle in radians — used to convert the pure
# pursuit geometric angle to the –1…1 simulator steering scale.
MAX_STEER_ANGLE_RAD = 0.35

# --- MPC speed control ---------------------------------------------------
MPC_HORIZON = 8  # Look-ahead steps
MPC_DT = LOOP_RATE
# Lateral acceleration limit: max safe cornering speed = sqrt(A_LAT_MAX / k)
A_LAT_MAX = 4.0  # m/s²
# Maximum deceleration used in the backward-pass feasibility check (m/s²)
MPC_MAX_DECEL = 1.5

# --- Slip / traction detection -------------------------------------------
# Expected yaw rate from bicycle model: ω = v * tan(δ) / L
# If the IMU-measured rate deviates by more than SLIP_THRESHOLD (fraction),
# throttle is cut proportionally down to SLIP_THROTTLE_CUT.
SLIP_THRESHOLD = 0.25  # 25 % deviation triggers cut
SLIP_THROTTLE_CUT = 0.6  # Minimum throttle multiplier under slip

# --- Spline path ---------------------------------------------------------
MIN_SPLINE_POINTS = 3  # Minimum midpoints to fit a spline
SPLINE_SAMPLES = 40  # Samples along the spline for evaluation


# =============================================================================
# SIMULATOR CONNECTION
# =============================================================================

client = fsds.FSDSClient()
client.confirmConnection()
client.enableApiControl(True)


# =============================================================================
# LIDAR — RAW CONE DETECTION
# =============================================================================


def distance(x1, y1, x2, y2):
    """Euclidean distance between two 2-D points."""
    return math.sqrt((x1 - x2) ** 2 + (y1 - y2) ** 2)


def pointgroup_to_cone(group):
    """Return the centroid (x, y) of a list of {'x', 'y'} point dicts."""
    n = len(group)
    return (
        sum(p["x"] for p in group) / n,
        sum(p["y"] for p in group) / n,
    )


def detect_raw_cones():
    """
    Parse the lidar point cloud into raw (x, y) cone centroids in the
    vehicle's local frame. No tracking applied — every frame from scratch.
    """
    lidardata = client.getLidarData(lidar_name="Lidar")
    if len(lidardata.point_cloud) < 3:
        return []

    pts = numpy.array(lidardata.point_cloud, dtype=numpy.float32).reshape(-1, 3)

    cones = []
    group = []

    for i in range(1, len(pts)):
        d = distance(pts[i][0], pts[i][1], pts[i - 1][0], pts[i - 1][1])
        if d < POINT_GROUP_THRESHOLD:
            group.append({"x": pts[i][0], "y": pts[i][1]})
        else:
            if len(group) >= MIN_GROUP_SIZE:
                cx, cy = pointgroup_to_cone(group)
                if distance(0, 0, cx, cy) < CONES_RANGE_CUTOFF:
                    cones.append((cx, cy))
            group = []

    if len(group) >= MIN_GROUP_SIZE:
        cx, cy = pointgroup_to_cone(group)
        if distance(0, 0, cx, cy) < CONES_RANGE_CUTOFF:
            cones.append((cx, cy))

    return cones


# =============================================================================
# CONE TRACKER
# =============================================================================


class ConeTracker:
    """
    Maintains a persistent map of cone positions across frames using
    nearest-neighbour association and exponential position smoothing.

    Problem this solves: the previous architecture rebuilt the cone list
    from scratch every frame. Any cone that flickered in/out of the lidar
    sweep produced a discontinuous jump in the weighted midpoint, which
    fed directly into the D term as a spurious spike, causing jerky steering.

    By tracking cones across frames and smoothing their positions with an
    EMA, the path input to the controller is stable even under noisy lidar.

    Lifecycle:
      - New detection within CONE_MATCH_RADIUS of an existing track →
        update that track's position (EMA blend).
      - New detection with no nearby track → new candidate track, seen=1.
      - Track not matched this frame → missed counter incremented.
      - Track confirmed once seen >= CONE_CONFIRM_FRAMES (prevents
        noise detections from feeding into the path).
      - Track dropped once missed >= CONE_DROP_FRAMES (handles cones
        leaving the lidar field of view).
    """

    def __init__(self):
        self.tracks = []

    def update(self, raw_detections):
        """
        Associate raw detections with tracks, update positions, and return
        confirmed cone positions as a list of (x, y) tuples.
        """
        for t in self.tracks:
            t["matched"] = False

        for cx, cy in raw_detections:
            best_track = None
            best_dist = CONE_MATCH_RADIUS

            for t in self.tracks:
                d = distance(cx, cy, t["x"], t["y"])
                if d < best_dist:
                    best_dist = d
                    best_track = t

            if best_track is not None:
                alpha = 0.4  # EMA weight for new measurement
                best_track["x"] = alpha * cx + (1 - alpha) * best_track["x"]
                best_track["y"] = alpha * cy + (1 - alpha) * best_track["y"]
                best_track["seen"] += 1
                best_track["missed"] = 0
                best_track["matched"] = True
                if best_track["seen"] >= CONE_CONFIRM_FRAMES:
                    best_track["confirmed"] = True
            else:
                self.tracks.append(
                    {
                        "x": cx,
                        "y": cy,
                        "seen": 1,
                        "missed": 0,
                        "confirmed": False,
                        "matched": True,
                    }
                )

        for t in self.tracks:
            if not t["matched"]:
                t["missed"] += 1

        self.tracks = [t for t in self.tracks if t["missed"] < CONE_DROP_FRAMES]

        return [(t["x"], t["y"]) for t in self.tracks if t["confirmed"]]


# =============================================================================
# SPLINE PATH CONSTRUCTION
# =============================================================================


def build_midpoint_spline(confirmed_cones):
    """
    Pair left/right confirmed cones, compute their midpoints, sort by
    forward distance, and fit a cubic spline through them.

    Why a spline rather than a weighted average point:
      The previous controller chased a single weighted midpoint each frame.
      A spline through all visible midpoints gives a smooth, continuous
      reference curve. The pure pursuit controller can then pick any point
      along it, naturally handling curves without needing gain tuning.

    Returns (spline, path_points array) or (None, None) if insufficient data.
    """
    left = [(x, y) for x, y in confirmed_cones if y > 0]
    right = [(x, y) for x, y in confirmed_cones if y <= 0]

    if not left or not right:
        return None, None

    # For each left cone find its nearest right cone and take the midpoint
    midpoints = []
    for lx, ly in left:
        nearest = min(right, key=lambda r: distance(lx, ly, r[0], r[1]))
        midpoints.append(((lx + nearest[0]) / 2.0, (ly + nearest[1]) / 2.0))

    # Include the vehicle origin so the spline starts from under the car
    midpoints.append((0.0, 0.0))
    midpoints.sort(key=lambda p: p[0])

    # Deduplicate: CubicSpline requires strictly increasing x values
    deduped = [midpoints[0]]
    for p in midpoints[1:]:
        if p[0] > deduped[-1][0] + 0.05:
            deduped.append(p)

    if len(deduped) < MIN_SPLINE_POINTS:
        return None, None

    xs = numpy.array([p[0] for p in deduped])
    ys = numpy.array([p[1] for p in deduped])

    try:
        spline = CubicSpline(xs, ys)
    except Exception:
        return None, None

    x_samples = numpy.linspace(xs[0], xs[-1], SPLINE_SAMPLES)
    y_samples = spline(x_samples)
    path_points = numpy.column_stack([x_samples, y_samples])

    return spline, path_points


def estimate_curvature(spline, x_eval):
    """
    Signed curvature of the spline at x_eval:
        κ = y'' / (1 + y'²)^(3/2)
    Higher magnitude = tighter corner.
    """
    dy = float(spline(x_eval, 1))
    ddy = float(spline(x_eval, 2))
    return ddy / (1.0 + dy**2) ** 1.5


# =============================================================================
# PURE PURSUIT STEERING
# =============================================================================


def pure_pursuit_steering(path_points, speed, spline):
    """
    Compute a steering command using pure pursuit geometry.

    Why pure pursuit instead of PID:
      PID on lateral error requires careful gain tuning to balance straight
      and corner response, and always lags behind the actual path. Pure
      pursuit picks a goal point on the path at a lookahead distance and
      computes the exact steering angle geometrically — no gains, no lag,
      and the lookahead distance naturally adjusts the corner/straight
      trade-off.

    Bicycle model:
        steer_angle = atan2(2 * L * sin(α), ld)
    where L = wheelbase, α = heading error to goal, ld = lookahead distance.

    Adaptive lookahead:
      - Base + speed term: longer lookahead at speed for stability.
      - Curvature term: shorter lookahead in corners so the car aims at
        the apex rather than a point past the exit.
    """
    if path_points is None or len(path_points) < 2:
        return 0.0

    # Evaluate curvature near the middle of the visible path
    x_mid = float(path_points[len(path_points) // 2, 0])
    curvature = abs(estimate_curvature(spline, x_mid))

    # Adaptive lookahead
    raw_ld = LOOKAHEAD_BASE + LOOKAHEAD_SPEED_GAIN * speed
    raw_ld = raw_ld / (1.0 + CURVATURE_LOOKAHEAD_GAIN * curvature)
    ld_dist = float(numpy.clip(raw_ld, LOOKAHEAD_MIN, LOOKAHEAD_MAX))

    # Find path point closest to the lookahead arc distance from the origin
    dists = numpy.sqrt(path_points[:, 0] ** 2 + path_points[:, 1] ** 2)
    idx = int(numpy.argmin(numpy.abs(dists - ld_dist)))
    gx, gy = path_points[idx]

    # Heading error and bicycle model steering angle
    alpha = math.atan2(gy, gx)
    ld_actual = max(math.sqrt(gx**2 + gy**2), 0.1)
    steer_angle = math.atan2(2.0 * WHEELBASE * math.sin(alpha), ld_actual)

    # Normalise to simulator's –1…1 steering scale
    return float(
        numpy.clip(
            steer_angle / MAX_STEER_ANGLE_RAD,
            -MAX_STEERING,
            MAX_STEERING,
        )
    )


# =============================================================================
# MPC SPEED CONTROL
# =============================================================================


def mpc_speed_control(speed, spline, path_points):
    """
    Model Predictive Control for throttle and brake.

    Why MPC instead of reactive speed control:
      The previous controller only knew current speed vs a fixed target.
      It couldn't brake before a corner — only react once already in it.
      MPC looks ahead along the path, computes a curvature-limited speed
      cap at each future step, then propagates braking constraints backward
      through the horizon. This gives the car exactly the braking point it
      needs for each corner with no manual tuning.

    Forward pass:
      For each of MPC_HORIZON future steps, compute the maximum lateral-
      acceleration-limited speed: v_max = sqrt(A_LAT_MAX / |κ|).

    Backward pass:
      Walk backward through the horizon. If the car cannot decelerate from
      speed[i] to speed[i+1] within one step at MPC_MAX_DECEL, clamp
      speed[i] down. This propagates corner speed limits backward so the
      car starts braking early enough.

    The first entry after the backward pass is the target speed right now.
    """
    if path_points is None or spline is None:
        return 0.0, 0.0

    x_vals = path_points[:, 0]
    x_max = float(x_vals[-1])
    step_dist = max(speed * MPC_DT, 0.1)

    # Forward pass — curvature-limited speed caps
    speed_caps = []
    for i in range(MPC_HORIZON):
        x_ahead = min(i * step_dist, x_max)
        k = abs(estimate_curvature(spline, x_ahead))
        v_max = math.sqrt(A_LAT_MAX / k) if k > 1e-4 else TARGET_SPEED
        speed_caps.append(min(v_max, TARGET_SPEED))

    # Backward pass — propagate braking feasibility
    for i in range(len(speed_caps) - 2, -1, -1):
        v_reachable = math.sqrt(speed_caps[i + 1] ** 2 + 2 * MPC_MAX_DECEL * step_dist)
        speed_caps[i] = min(speed_caps[i], v_reachable)

    target_now = speed_caps[0]
    speed_error = target_now - speed

    if speed_error > 0:
        throttle = MAX_THROTTLE * min(speed_error / max(target_now, 0.1), 1.0)
        brake = 0.0
    else:
        throttle = 0.0
        brake = MAX_BRAKE * min(-speed_error / max(target_now, 0.1), 1.0)

    return float(throttle), float(brake)


# =============================================================================
# SLIP / TRACTION DETECTION
# =============================================================================


def slip_throttle_multiplier(speed, steering, imu_yaw_rate):
    """
    Detect sliding by comparing the IMU yaw rate to the bicycle model
    prediction, and cut throttle proportionally if they diverge.

    Why this matters:
      At higher speeds, aggressive throttle on corner exit can spin the
      rear. The bicycle model gives us an expected yaw rate for any
      (speed, steering) pair. If the car is actually rotating faster than
      predicted, the rear is stepping out — cut throttle before it gets worse.

    Returns a multiplier in [SLIP_THROTTLE_CUT, 1.0].
    """
    steer_angle = steering * MAX_STEER_ANGLE_RAD
    predicted_yaw = speed * math.tan(steer_angle) / WHEELBASE if WHEELBASE > 0 else 0.0

    if abs(predicted_yaw) < 1e-3:
        return 1.0

    deviation = abs(imu_yaw_rate - predicted_yaw) / abs(predicted_yaw)

    if deviation < SLIP_THRESHOLD:
        return 1.0

    # Linear fade from 1.0 at threshold to SLIP_THROTTLE_CUT at 2× threshold
    excess = (deviation - SLIP_THRESHOLD) / SLIP_THRESHOLD
    multiplier = 1.0 - (1.0 - SLIP_THROTTLE_CUT) * min(excess, 1.0)
    return float(multiplier)


# =============================================================================
# VEHICLE STATE
# =============================================================================


def get_speed():
    """Return current vehicle speed in m/s from GPS."""
    gps = client.getGpsData()
    v = gps.gnss.velocity
    return math.sqrt(v.x_val**2 + v.y_val**2)


def get_imu_yaw_rate():
    """Return the vehicle's yaw rate in rad/s from the IMU."""
    try:
        imu = client.getImuData()
        return float(imu.angular_velocity.z_val)
    except Exception:
        return 0.0


# =============================================================================
# MAIN LOOP
# =============================================================================

tracker = ConeTracker()
last_steering = 0.0
prev_time = None

while True:
    now = time.monotonic()
    dt = (now - prev_time) if prev_time is not None else LOOP_RATE
    prev_time = now

    try:
        # 1. Sense -----------------------------------------------------------
        raw_cones = detect_raw_cones()
        speed = get_speed()
        yaw_rate = get_imu_yaw_rate()

        # 2. Track cones across frames ---------------------------------------
        confirmed = tracker.update(raw_cones)

        if len(confirmed) < 2:
            # Too few confirmed cones — coast, hold last steering
            car_controls = fsds.CarControls()
            car_controls.steering = last_steering
            car_controls.throttle = 0.0
            car_controls.brake = 0.0
            client.setCarControls(car_controls)
            time.sleep(LOOP_RATE)
            continue

        # 3. Build smooth spline through cone midpoints ----------------------
        spline, path_points = build_midpoint_spline(confirmed)

        if spline is None:
            car_controls = fsds.CarControls()
            car_controls.steering = last_steering
            car_controls.throttle = 0.0
            car_controls.brake = 0.0
            client.setCarControls(car_controls)
            time.sleep(LOOP_RATE)
            continue

        # 4. Pure pursuit steering -------------------------------------------
        steering = pure_pursuit_steering(path_points, speed, spline)

        # 5. MPC throttle / brake --------------------------------------------
        throttle, brake = mpc_speed_control(speed, spline, path_points)

        # 6. Slip detection — cut throttle if rear is stepping out -----------
        slip_mult = slip_throttle_multiplier(speed, steering, yaw_rate)
        throttle = throttle * slip_mult

        # 7. Output ----------------------------------------------------------
        last_steering = steering

        car_controls = fsds.CarControls()
        car_controls.steering = steering
        car_controls.throttle = float(throttle)
        car_controls.brake = float(brake)
        client.setCarControls(car_controls)

    except Exception as e:
        print(f"[WARN] Frame skipped: {e}")

    time.sleep(LOOP_RATE)
