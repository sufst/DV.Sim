using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DVSim {
    public sealed class CentrelineFollower {
        struct Gate {
            public double X, Y, TX, TY, Width;
        }
        readonly List<Gate> path = new List<Gate>();
        bool closed;
        int segment = -1;
        public int WaypointCount { get { return path.Count; } }
        public bool IsClosed { get { return closed; } }

        static double Number(Dictionary<string, object> map, string key) {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null)
                throw new InvalidDataException("Missing track or car pose data.");
            double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(result) || Double.IsInfinity(result)) throw new InvalidDataException("Invalid track or car pose data.");
            return result;
        }
        static double Distance(double x, double y) { return Math.Sqrt(x * x + y * y); }
        static double Clamp(double value, double low, double high) { return Math.Max(low, Math.Min(high, value)); }

        public CentrelineFollower(object response) {
            var referee = response as Dictionary<string, object>;
            object value;
            if (referee == null || !referee.TryGetValue("cones", out value) || !(value is object[]))
                throw new InvalidDataException("Track cone data is unavailable.");
            object originValue;
            if (!referee.TryGetValue("initial_position", out originValue)) throw new InvalidDataException("Track origin is unavailable.");
            var origin = originValue as Dictionary<string, object>;
            double ox = Number(origin, "x"), oy = Number(origin, "y");
            var blue = new List<Gate>(); var yellow = new List<Gate>();
            foreach (object item in (object[])value) {
                var cone = item as Dictionary<string, object>;
                double color = Number(cone, "color");
                if (color != 0 && color != 1) continue;
                // Referee coordinates are Unreal centimetres; car poses are spawn-relative ENU metres.
                var point = new Gate { X = (Number(cone, "x") - ox) * 0.01, Y = -(Number(cone, "y") - oy) * 0.01 };
                (color == 1 ? blue : yellow).Add(point);
            }
            var gates = new List<Gate>();
            AddGates(blue, yellow, true, gates);
            AddGates(yellow, blue, false, gates);
            if (gates.Count < 2) return;

            int start = 0;
            for (int i = 1; i < gates.Count; i++)
                if (Distance(gates[i].X, gates[i].Y) < Distance(gates[start].X, gates[start].Y)) start = i;
            var used = new bool[gates.Count];
            int current = start;
            while (!used[current]) {
                Gate gate = gates[current]; path.Add(gate); used[current] = true;
                int next = -1; double best = Double.MaxValue;
                for (int i = 0; i < gates.Count; i++) {
                    if (used[i] && (i != start || path.Count < 6)) continue;
                    double dx = gates[i].X - gate.X, dy = gates[i].Y - gate.Y;
                    double distance = Distance(dx, dy);
                    if (distance < 0.4 || distance > 8) continue;
                    double alignment = (dx * gate.TX + dy * gate.TY) / distance;
                    double arrival = (dx * gates[i].TX + dy * gates[i].TY) / distance;
                    if (alignment < 0.2 || arrival < 0.2) continue;
                    double cost = distance * (2 - Math.Min(alignment, arrival));
                    if (cost < best) { next = i; best = cost; }
                }
                if (next < 0) break;
                if (next == start) { closed = true; break; }
                current = next;
            }
        }

        static void AddGates(List<Gate> source, List<Gate> opposite, bool sourceIsBlue, List<Gate> gates) {
            foreach (Gate cone in source) {
                int nearest = -1; double width = Double.MaxValue;
                for (int i = 0; i < opposite.Count; i++) {
                    double distance = Distance(cone.X - opposite[i].X, cone.Y - opposite[i].Y);
                    if (distance < width) { nearest = i; width = distance; }
                }
                if (nearest < 0 || width < 2 || width > 6) continue;
                Gate other = opposite[nearest];
                double dx = (sourceIsBlue ? other.X - cone.X : cone.X - other.X);
                double dy = (sourceIsBlue ? other.Y - cone.Y : cone.Y - other.Y);
                var gate = new Gate { X = (cone.X + other.X) / 2, Y = (cone.Y + other.Y) / 2,
                    TX = -dy / width, TY = dx / width, Width = width };
                bool duplicate = false;
                foreach (Gate existing in gates)
                    if (Distance(existing.X - gate.X, existing.Y - gate.Y) < 0.4) { duplicate = true; break; }
                if (!duplicate) gates.Add(gate);
            }
        }

        public bool TryGetCommand(object poseResponse, double speed, double requestedSpeed,
            out double steering, out double targetSpeed, out string reason) {
            steering = 0; targetSpeed = 0; reason = "Track centreline unavailable";
            if (path.Count < 2) return false;
            var pose = poseResponse as Dictionary<string, object>;
            object positionValue, orientationValue;
            if (pose == null || !pose.TryGetValue("position", out positionValue) || !pose.TryGetValue("orientation", out orientationValue))
                throw new InvalidDataException("Car pose is unavailable.");
            var position = positionValue as Dictionary<string, object>;
            var orientation = orientationValue as Dictionary<string, object>;
            double x = Number(position, "x_val"), y = Number(position, "y_val");
            double qx = Number(orientation, "x_val"), qy = Number(orientation, "y_val");
            double qz = Number(orientation, "z_val"), qw = Number(orientation, "w_val");
            double norm = Math.Sqrt(qx*qx + qy*qy + qz*qz + qw*qw);
            if (norm < 0.001) throw new InvalidDataException("Car heading is unavailable.");
            qx /= norm; qy /= norm; qz /= norm; qw /= norm;
            double yaw = Math.Atan2(2 * (qw*qz + qx*qy), 1 - 2 * (qy*qy + qz*qz));
            double hx = Math.Cos(yaw), hy = Math.Sin(yaw);
            int count = closed ? path.Count : path.Count - 1;
            double best = Double.MaxValue, fraction = 0;
            int found = -1;
            for (int offset = 0; offset < (segment < 0 ? count : Math.Min(count, 8)); offset++) {
                int i = segment < 0 ? offset : segment + offset - 1;
                if (closed) i = (i + count) % count;
                else if (i < 0 || i >= count) continue;
                Gate a = path[i], b = path[(i + 1) % path.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y, length = Distance(dx, dy);
                if ((dx*hx + dy*hy) / length < 0.1) continue;
                // Extend the first straight segment to cover a spawn before the start-line cones.
                double lower = !closed && i == 0 ? -5 / length : 0;
                double t = Clamp(((x-a.X)*dx + (y-a.Y)*dy) / (length*length), lower, 1);
                double distance = Distance(x-a.X-t*dx, y-a.Y-t*dy);
                if (distance < best) { best = distance; found = i; fraction = t; }
            }
            if (found < 0 || best > Math.Min(2, path[found].Width / 2 - 0.35)) {
                reason = "Car outside the centreline corridor"; return false;
            }
            segment = found;
            Gate first = path[found], end = path[(found + 1) % path.Count];
            double tx = first.X + fraction * (end.X-first.X), ty = first.Y + fraction * (end.Y-first.Y);
            double lookahead = 2.5 + 0.5 * Math.Abs(speed), remaining = lookahead;
            int steps = 0, index = found;
            while (steps++ < path.Count) {
                end = path[(index + 1) % path.Count];
                double distance = Distance(end.X-tx, end.Y-ty);
                if (distance >= remaining) {
                    tx += (end.X-tx) * remaining/distance; ty += (end.Y-ty) * remaining/distance;
                    remaining = 0; break;
                }
                remaining -= distance; tx = end.X; ty = end.Y;
                index++;
                if (!closed && index >= path.Count - 1) break;
                index %= path.Count;
            }
            double ahead = (tx-x)*hx + (ty-y)*hy;
            if (ahead < 0.1) { reason = closed ? "Centreline target behind car" : "End of track"; return false; }
            double lateral = -(tx-x)*hy + (ty-y)*hx;
            double squaredDistance = (tx-x)*(tx-x) + (ty-y)*(ty-y);
            // Pure pursuit with a 1.6 m wheelbase. FSDS steering is positive to the right.
            steering = Clamp(-Math.Atan2(3.2 * lateral, squaredDistance) / 0.5, -0.85, 0.85);
            targetSpeed = requestedSpeed / (1 + 0.9 * Math.Abs(steering));
            if (!closed && remaining > 0) {
                double distanceToEnd = lookahead - remaining;
                if (distanceToEnd < 0.5) { reason = "End of track"; return false; }
                targetSpeed = Math.Min(targetSpeed, Math.Sqrt(1.2 * Math.Max(0, distanceToEnd - 0.5)));
            }
            reason = "Following centreline"; return true;
        }
    }
}
