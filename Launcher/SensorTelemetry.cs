using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DVSim {
    public sealed class SensorReadings {
        public readonly string Status, Imu, Gps, Lidar;
        internal SensorReadings(string status, string imu = null, string gps = null, string lidar = null) {
            Status = status;
            Imu = imu;
            Gps = gps;
            Lidar = lidar;
        }
    }

    public sealed class SensorTelemetry : IDisposable {
        const string Header = "sensor,timestamp_ns,field,value,point_index,x_m,y_m,z_m";
        static readonly string[] Names = { "Imu", "Gps", "Lidar" };
        static readonly string[] Methods = { "getImuData", "getGpsData", "getLidarData" };
        readonly string path;
        readonly int port;
        readonly object fileLock = new object();
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Thread worker;
        volatile SensorReadings latest = new SensorReadings("Start a simulation to record sensor data.");
        int pendingRun;

        public SensorTelemetry(int port, string path) {
            this.port = port;
            this.path = path;
            worker = new Thread(Record) { IsBackground = true };
            worker.Start();
        }

        public SensorReadings Latest { get { return latest; } }
        public bool HasRecording { get { return File.Exists(path); } }
        public void BeginRun() { Interlocked.Exchange(ref pendingRun, 1); }

        static string Format(object value) {
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        static string Cell(string text) { return "\"" + text.Replace("\"", "\"\"") + "\""; }

        static void WriteFields(Dictionary<string, object> sample, string parent, List<KeyValuePair<string, string>> fields) {
            foreach (var field in sample) {
                if (field.Key == "point_cloud" || field.Key == "time_stamp") continue;
                string name = parent + field.Key;
                var child = field.Value as Dictionary<string, object>;
                if (child != null) WriteFields(child, name + ".", fields);
                else fields.Add(new KeyValuePair<string, string>(name, Format(field.Value)));
            }
        }

        static ulong SampleTimestamp(string name, Dictionary<string, object> sample) {
            object stamp;
            if (sample == null || !sample.TryGetValue("time_stamp", out stamp)) return 0;
            if (name == "Gps") {
                // The packaged simulator can return an uninitialized GNSS record during map startup.
                var gnss = (Dictionary<string, object>)sample["gnss"];
                if (Convert.ToUInt64(gnss["time_utc"]) == 0 ||
                    !(Convert.ToDouble(gnss["eph"]) >= 0) || !(Convert.ToDouble(gnss["epv"]) >= 0))
                    return 0;
            }
            return Convert.ToUInt64(stamp);
        }

        static string DescribeAndRecord(string name, Dictionary<string, object> sample, ulong stamp, ulong previous, StringBuilder rows) {
            if (stamp == 0) return "Waiting for a reading.";
            string timestamp = Format(stamp);
            bool changed = stamp != previous;
            string prefix = name + "," + timestamp + ",";
            var fields = new List<KeyValuePair<string, string>>();
            WriteFields(sample, "", fields);
            var display = new StringBuilder().AppendLine("Timestamp (ns)  " + timestamp);
            object cloud;
            if (sample.TryGetValue("point_cloud", out cloud)) {
                var points = (double[])cloud;
                fields.Add(new KeyValuePair<string, string>("point_count", Format(points.Length / 3)));
                if (changed) {
                    for (int i = 0; i < points.Length; i += 3) {
                        rows.Append(prefix).Append("point,,").Append(Format(i / 3)).Append(',')
                            .Append(Format(points[i])).Append(',').Append(Format(points[i + 1]))
                            .Append(',').AppendLine(Format(points[i + 2]));
                    }
                }
            }
            foreach (var field in fields) {
                display.Append(field.Key).Append("  ").AppendLine(field.Value);
                if (changed) rows.Append(prefix).Append(Cell(field.Key)).Append(',').Append(Cell(field.Value)).AppendLine(",,,,");
            }
            return display.ToString().TrimEnd();
        }

        void Store(StringBuilder rows, bool newRun) {
            lock (fileLock) {
                if (!newRun && File.Exists(path)) {
                    File.AppendAllText(path, rows.ToString());
                    return;
                }
                string stage = path + ".tmp";
                try {
                    File.WriteAllText(stage, Header + Environment.NewLine + rows);
                    if (File.Exists(path)) File.Replace(stage, path, null);
                    else File.Move(stage, path);
                } finally { File.Delete(stage); }
            }
        }

        void Record() {
            LidarRpc rpc = null;
            var previous = new ulong[Names.Length];
            ulong previousCar = 0;
            DateTime lastLive = DateTime.UtcNow;
            DateTime carChanged = lastLive;
            bool newRun = true;
            try {
                while (!stop.WaitOne(0)) {
                    try {
                        if (rpc == null) rpc = new LidarRpc(port);
                        var car = (Dictionary<string, object>)rpc.Call("getCarState", "FSCar");
                        ulong carTime = Convert.ToUInt64(car["timestamp"]);
                        DateTime now = DateTime.UtcNow;
                        if (Interlocked.Exchange(ref pendingRun, 0) != 0 || carTime < previousCar ||
                            (now - lastLive).TotalSeconds > 2) {
                            newRun = true;
                            Array.Clear(previous, 0, previous.Length);
                        }
                        lastLive = now;
                        if (carTime != previousCar) carChanged = now;
                        previousCar = carTime;
                        var values = new string[Names.Length];
                        var rows = new StringBuilder();
                        var timestamps = (ulong[])previous.Clone();
                        for (int i = 0; i < Names.Length && !stop.WaitOne(0); i++) {
                            try {
                                var sample = (Dictionary<string, object>)rpc.Call(Methods[i], Names[i], "FSCar");
                                ulong timestamp = SampleTimestamp(Names[i], sample);
                                values[i] = DescribeAndRecord(Names[i], sample, timestamp, previous[i], rows);
                                if (timestamp != 0) timestamps[i] = timestamp;
                            } catch (RpcException) { values[i] = "Unavailable in this run."; }
                        }
                        string status = (now - carChanged).TotalSeconds > 1.5 ? "Simulation paused." : "Recording live sensor data.";
                        if (rows.Length > 0) {
                            try {
                                if (newRun) foreach (string corner in new[] { "front_left", "front_right", "rear_left", "rear_right" })
                                    rows.Append("WheelSpeed,").Append(carTime).Append(',').Append(corner).AppendLine(",unavailable,,,,");
                                Store(rows, newRun);
                                previous = timestamps;
                                newRun = false;
                            } catch (IOException ex) { status = "Recording failed. " + ex.Message; }
                            catch (UnauthorizedAccessException ex) { status = "Recording failed. " + ex.Message; }
                        }
                        latest = new SensorReadings(status, values[0], values[1], values[2]);
                    } catch (Exception) {
                        latest = new SensorReadings("Start a simulation to record sensor data.");
                        if (rpc != null) { rpc.Dispose(); rpc = null; }
                    }
                    stop.WaitOne(200);
                }
            } finally { if (rpc != null) rpc.Dispose(); }
        }

        public Task Export(string destination) {
            if (String.Equals(Path.GetFullPath(destination), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a different path from the active recording.");
            FileStream source;
            long remaining;
            lock (fileLock) {
                source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                remaining = source.Length;
            }
            return Task.Run(() => {
                string stage = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    using (source)
                    using (var output = new FileStream(stage, FileMode.CreateNew)) {
                        var buffer = new byte[65536];
                        while (remaining > 0) {
                            int count = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                            if (count == 0) throw new EndOfStreamException("The recording ended during export.");
                            output.Write(buffer, 0, count);
                            remaining -= count;
                        }
                    }
                    if (File.Exists(destination)) File.Replace(stage, destination, null);
                    else File.Move(stage, destination);
                } finally { File.Delete(stage); }
            });
        }

        public void Dispose() {
            stop.Set();
            if (worker.Join(2500)) stop.Dispose();
        }
    }
}
