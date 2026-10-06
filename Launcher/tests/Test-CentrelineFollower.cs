using System;
using System.Collections.Generic;
using System.IO;
using DVSim;

public static class CentrelineFollowerChecks {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static Dictionary<string, object> Map(params object[] items) {
        var map = new Dictionary<string, object>();
        for (int i = 0; i < items.Length; i += 2) map[(string)items[i]] = items[i+1];
        return map;
    }
    public static object Pose(double x, double y, double yaw) {
        return Map("position", Map("x_val", x, "y_val", y), "orientation",
            Map("x_val", 0.0, "y_val", 0.0, "z_val", Math.Sin(yaw/2), "w_val", Math.Cos(yaw/2)));
    }
    static void Cone(List<object> cones, double x, double y, int color) {
        // Deliberately nonzero world origin, Unreal centimetres and opposite Y sign.
        cones.Add(Map("x", 12300 + x*100, "y", -4500 - y*100, "color", color));
    }
    static object Track(List<object> cones) {
        return Map("initial_position", Map("x", 12300.0, "y", -4500.0), "cones", cones.ToArray());
    }
    public static object StraightTrack(double offset) {
        var cones = new List<object>();
        // Reverse order ensures following does not depend on RPC array order.
        for (int i = 6; i >= 0; i--) {
            Cone(cones, i*4, offset+2, 1); Cone(cones, i*4, offset-2, 0);
        }
        Cone(cones, 1, 0, 2); // Start-line orange cones must not become waypoints.
        return Track(cones);
    }
    static object Circle(double radius, int direction = 1) {
        var cones = new List<object>();
        for (int i = 0; i < 40; i++) {
            double angle = i*2*Math.PI/40;
            double blueRadius = radius - 2*direction, yellowRadius = radius + 2*direction;
            Cone(cones, blueRadius*Math.Sin(angle), direction*(radius-blueRadius*Math.Cos(angle)), 1);
            Cone(cones, yellowRadius*Math.Sin(angle), direction*(radius-yellowRadius*Math.Cos(angle)), 0);
        }
        return Track(cones);
    }
    public static string Run() {
        double steering, speed; string reason;
        var straight = new CentrelineFollower(StraightTrack(0));
        Check(straight.WaypointCount==7 && !straight.IsClosed, "Straight track must contain seven ordered gates and stay open.");
        Check(straight.TryGetCommand(Pose(0,0,0), 1.5, 1.5, out steering, out speed, out reason), "Straight centreline must be drivable.");
        Check(Math.Abs(steering)<0.00001 && Math.Abs(speed-1.5)<0.00001, "Centred car must drive straight at requested speed.");
        Check(new CentrelineFollower(StraightTrack(0)).TryGetCommand(Pose(-2,0,0), 0, 1.5, out steering, out speed, out reason),
            "A spawn two metres before the first cones must join the centreline.");
        Check(straight.TryGetCommand(Pose(2,0.7,0), 1.5, 1.5, out steering, out speed, out reason) && steering>0, "Car left of centreline must steer right.");
        Check(straight.TryGetCommand(Pose(2,-0.7,0), 1.5, 1.5, out steering, out speed, out reason) && steering<0, "Car right of centreline must steer left.");
        Check(!straight.TryGetCommand(Pose(2,4,0), 1.5, 1.5, out steering, out speed, out reason), "Car outside corridor must stop.");
        Check(!new CentrelineFollower(Track(new List<object>())).TryGetCommand(Pose(0,0,0), 0, 1.5, out steering, out speed, out reason), "Missing boundaries must stop the car.");
        var end = new CentrelineFollower(StraightTrack(0));
        Check(end.TryGetCommand(Pose(22,0,0), 1.5, 1.5, out steering, out speed, out reason) && speed<1.5, "Open track must slow approaching its end.");
        Check(!end.TryGetCommand(Pose(23.7,0,0), 0.3, 1.5, out steering, out speed, out reason) && reason=="End of track", "Open track must stop at its end.");
        Check(!new CentrelineFollower(StraightTrack(0)).TryGetCommand(Pose(0,0,Math.PI), 0, 1.5, out steering, out speed, out reason), "Car facing against track direction must stop.");
        bool invalid = false;
        try { new CentrelineFollower(StraightTrack(0)).TryGetCommand(Pose(Double.NaN,0,0), 0, 1.5, out steering, out speed, out reason); }
        catch (InvalidDataException) { invalid = true; }
        Check(invalid, "Invalid pose must be rejected before sending controls.");
        var curve = new CentrelineFollower(Circle(12));
        Check(curve.IsClosed && curve.WaypointCount==40, "Loop must close without consuming a nearby unrelated gate.");
        Check(curve.TryGetCommand(Pose(0,0,0), 1.5, 1.5, out steering, out speed, out reason) && steering<0 && speed<1.5, "Left bend must steer left and reduce speed.");
        var right = new CentrelineFollower(Circle(12,-1));
        Check(right.TryGetCommand(Pose(12,-12,-Math.PI/2), 1.5, 1.5, out steering, out speed, out reason) && steering>0,
            "Right bend with rotated car heading must steer right.");
        // Exercise actual feedback across two laps with a bicycle model and FSDS steering sign.
        double x = 0, y = 0, yaw = 0, maxError = 0;
        for (int i = 0; i < 3000; i++) {
            Check(curve.TryGetCommand(Pose(x,y,yaw), 1.5, 1.5, out steering, out speed, out reason), "Closed loop must remain drivable across the start line.");
            x += speed*Math.Cos(yaw)*0.05; y += speed*Math.Sin(yaw)*0.05;
            yaw += speed/1.6 * Math.Tan(-steering*0.5)*0.05;
            maxError = Math.Max(maxError, Math.Abs(Math.Sqrt(x*x+(y-12)*(y-12))-12));
        }
        Check(maxError<0.6 && yaw>4*Math.PI, String.Format("Controller must follow two laps within 0.6 m of the centreline; error {0:0.000} m, heading {1:0.00} rad.", maxError, yaw));
        var tight = new CentrelineFollower(Circle(4.5,-1));
        x = 0; y = 0; yaw = 0; maxError = 0;
        for (int i = 0; i < 2200; i++) {
            Check(tight.TryGetCommand(Pose(x,y,yaw), 0.8, 1.5, out steering, out speed, out reason), "Tight right curve must remain drivable.");
            x += speed*Math.Cos(yaw)*0.05; y += speed*Math.Sin(yaw)*0.05;
            yaw += speed/1.6 * Math.Tan(-steering*0.5)*0.05;
            maxError = Math.Max(maxError, Math.Abs(Math.Sqrt(x*x+(y+4.5)*(y+4.5))-4.5));
        }
        Check(maxError<0.6 && yaw < -4*Math.PI, "Tight right curve must complete two laps within 0.6 m of centreline.");
        return "PASS: centreline coordinates, unordered cones, steering direction, corner speed, missing track, invalid pose, end braking and two closed-loop laps.";
    }
}
