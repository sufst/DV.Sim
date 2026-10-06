using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DVSim {
    // The small MessagePack subset used by FSDS's RPC responses.
    // Protocol: https://github.com/msgpack-rpc/msgpack-rpc/blob/master/spec.md
    public sealed class MessagePackReader {
        readonly Stream stream;
        int consumed;
        public MessagePackReader(Stream stream) { this.stream = stream; }
        int Byte() {
            if (++consumed > 8 * 1024 * 1024) throw new InvalidDataException("RPC response is too large.");
            int value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            return value;
        }
        ulong Unsigned(int count) { ulong value = 0; while (count-- > 0) value = (value << 8) | (uint)Byte(); return value; }
        int Length(ulong value) {
            if (value > 1000000) throw new InvalidDataException("RPC collection is too large.");
            return (int)value;
        }
        string Text(int length) {
            byte[] bytes = new byte[Length((ulong)length)];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Byte();
            return Encoding.UTF8.GetString(bytes);
        }
        [StructLayout(LayoutKind.Explicit)] struct Number {
            [FieldOffset(0)] public ulong Bits;
            [FieldOffset(0)] public double Double;
            [FieldOffset(0)] public uint SmallBits;
            [FieldOffset(0)] public float Float;
        }
        object Numeric(int code) {
            if (code <= 0x7f) return (ulong)code;
            if (code >= 0xe0) return (long)(sbyte)code;
            switch (code) {
                case 0xca: return (double)new Number { SmallBits = (uint)Unsigned(4) }.Float;
                case 0xcb: return new Number { Bits = Unsigned(8) }.Double;
                case 0xcc: return Unsigned(1);
                case 0xcd: return Unsigned(2);
                case 0xce: return Unsigned(4);
                case 0xcf: return Unsigned(8);
                case 0xd0: return (long)(sbyte)Unsigned(1);
                case 0xd1: return (long)(short)Unsigned(2);
                case 0xd2: return (long)(int)Unsigned(4);
                case 0xd3: return unchecked((long)Unsigned(8));
                default: throw new InvalidDataException("Expected a numeric RPC value.");
            }
        }
        int ArrayLength(int code) {
            if ((code & 0xf0) == 0x90) return code & 15;
            if (code == 0xdc) return Length(Unsigned(2));
            if (code == 0xdd) return Length(Unsigned(4));
            throw new InvalidDataException("Expected an RPC array.");
        }
        object[] Array(int length, int depth) {
            object[] result = new object[length];
            for (int i = 0; i < length; i++) result[i] = Read(depth + 1);
            return result;
        }
        Dictionary<string, object> Map(int length, int depth) {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < length; i++) {
                string key = Read(depth + 1) as string;
                if (key == null) throw new InvalidDataException("Expected an RPC map key.");
                if (key == "point_cloud") {
                    int count = ArrayLength(Byte());
                    if (count % 3 != 0) throw new InvalidDataException("Lidar points must be XYZ triples.");
                    var points = new double[count];
                    for (int j = 0; j < count; j++) points[j] = Convert.ToDouble(Numeric(Byte()), CultureInfo.InvariantCulture);
                    result[key] = points;
                } else result[key] = Read(depth + 1);
            }
            return result;
        }
        object Read(int depth) {
            if (depth > 40) throw new InvalidDataException("RPC nesting is too deep.");
            int code = Byte();
            if (code <= 0x7f || code >= 0xe0 || (code >= 0xca && code <= 0xd3)) return Numeric(code);
            if ((code & 0xe0) == 0xa0) return Text(code & 31);
            if ((code & 0xf0) == 0x90) return Array(code & 15, depth);
            if ((code & 0xf0) == 0x80) return Map(code & 15, depth);
            switch (code) {
                case 0xc0: return null;
                case 0xc2: return false;
                case 0xc3: return true;
                case 0xc4: case 0xd9: return Text(Length(Unsigned(1)));
                case 0xc5: case 0xda: return Text(Length(Unsigned(2)));
                case 0xc6: case 0xdb: return Text(Length(Unsigned(4)));
                case 0xdc: return Array(Length(Unsigned(2)), depth);
                case 0xdd: return Array(Length(Unsigned(4)), depth);
                case 0xde: return Map(Length(Unsigned(2)), depth);
                case 0xdf: return Map(Length(Unsigned(4)), depth);
                default: throw new InvalidDataException("Unsupported RPC value.");
            }
        }
        public object ReadMessage() { consumed = 0; return Read(0); }
    }

    public sealed class RpcException : Exception { public RpcException(string message) : base(message) {} }
    public sealed class LidarRpc : IDisposable {
        readonly TcpClient client;
        readonly Stream input;
        readonly NetworkStream output;
        uint sequence;
        public LidarRpc(int port) {
            client = new TcpClient();
            try {
                var task = client.ConnectAsync("127.0.0.1", port);
                if (!task.Wait(700)) throw new IOException("Simulator is not connected.");
                client.NoDelay = true;
                output = client.GetStream(); output.ReadTimeout = 1500; output.WriteTimeout = 1500;
                input = new BufferedStream(output, 65536);
            } catch { client.Close(); throw; }
        }
        static void WriteText(Stream stream, string text) {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 31) throw new ArgumentException("RPC method or argument is too long.");
            stream.WriteByte((byte)(0xa0 | bytes.Length)); stream.Write(bytes, 0, bytes.Length);
        }
        static void WriteUInt(Stream stream, ulong value) {
            if (value <= 0x7f) stream.WriteByte((byte)value);
            else if (value <= 0xff) { stream.WriteByte(0xcc); stream.WriteByte((byte)value); }
            else if (value <= 0xffff) { stream.WriteByte(0xcd); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
            else {
                stream.WriteByte(0xce);
                for (int shift = 24; shift >= 0; shift -= 8) stream.WriteByte((byte)(value >> shift));
            }
        }
        static void WriteDouble(Stream stream, double value) {
            stream.WriteByte(0xcb);
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            stream.Write(bytes, 0, bytes.Length);
        }
        static void WriteMap(Stream stream, Dictionary<string, object> map) {
            if (map.Count > 15) throw new ArgumentException("RPC map argument is too large.");
            stream.WriteByte((byte)(0x80 | map.Count));
            foreach (var item in map) { WriteText(stream, item.Key); WriteValue(stream, item.Value); }
        }
        static void WriteValue(Stream stream, object value) {
            if (value == null) stream.WriteByte(0xc0);
            else if (value is string) WriteText(stream, (string)value);
            else if (value is bool) stream.WriteByte((bool)value ? (byte)0xc3 : (byte)0xc2);
            else if (value is double || value is float) WriteDouble(stream, Convert.ToDouble(value, CultureInfo.InvariantCulture));
            else if (value is int || value is uint || value is long || value is ulong)
                WriteUInt(stream, Convert.ToUInt64(value, CultureInfo.InvariantCulture));
            else if (value is Dictionary<string, object>) WriteMap(stream, (Dictionary<string, object>)value);
            else throw new ArgumentException("Unsupported RPC argument type.");
        }
        public object Call(string method, params object[] arguments) {
            uint id = ++sequence;
            using (var request = new MemoryStream()) {
                request.WriteByte(0x94); request.WriteByte(0); request.WriteByte(0xce);
                for (int shift = 24; shift >= 0; shift -= 8) request.WriteByte((byte)(id >> shift));
                WriteText(request, method); request.WriteByte((byte)(0x90 | arguments.Length));
                foreach (object argument in arguments) WriteValue(request, argument);
                byte[] bytes = request.ToArray(); output.Write(bytes, 0, bytes.Length);
            }
            object[] response = new MessagePackReader(input).ReadMessage() as object[];
            if (response == null || response.Length != 4 || Convert.ToInt32(response[0]) != 1 || Convert.ToUInt32(response[1]) != id)
                throw new InvalidDataException("Invalid RPC response.");
            if (response[2] != null) throw new RpcException(Convert.ToString(response[2], CultureInfo.InvariantCulture));
            return response[3];
        }
        public void Dispose() { client.Close(); input.Dispose(); }
    }

    public sealed class SlowAutoDriver : IDisposable {
        readonly object sync = new object();
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        readonly Thread worker;
        readonly int port;
        LidarRpc rpc;
        string status = "Stopped";
        string detail = "Stopped";
        double targetSpeed;
        public string Status { get { lock (sync) return status; } }
        public string Detail { get { lock (sync) return detail; } }
        public double TargetSpeed {
            get { lock (sync) return targetSpeed; }
            set { lock (sync) targetSpeed = Math.Max(0.5, Math.Min(4.0, value)); }
        }
        public SlowAutoDriver(int port, double targetSpeed) {
            this.port = port; TargetSpeed = targetSpeed;
            worker = new Thread(Drive); worker.IsBackground = true; worker.Start();
        }
        void Publish(string nextStatus, string nextDetail) { lock (sync) { status = nextStatus; detail = nextDetail; } }
        void Disconnect() { lock (sync) { if (rpc != null) { rpc.Dispose(); rpc = null; } } }
        static Dictionary<string, object> Controls(double throttle, double brake, double steering = 0) {
            return new Dictionary<string, object> {
                { "throttle", throttle }, { "steering", steering }, { "brake", brake },
                { "handbrake", false }, { "is_manual_gear", false },
                { "manual_gear", 0 }, { "gear_immediate", true }
            };
        }
        static double SpeedFrom(object response) {
            var state = response as Dictionary<string, object>;
            if (state == null || !state.ContainsKey("speed") || state["speed"] == null)
                throw new InvalidDataException("Car speed is unavailable.");
            double speed = Math.Abs(Convert.ToDouble(state["speed"], CultureInfo.InvariantCulture));
            if (Double.IsNaN(speed) || Double.IsInfinity(speed)) throw new InvalidDataException("Car speed is invalid.");
            return speed;
        }
        void StopCar() {
            if (rpc == null) return;
            try { rpc.Call("setCarControls", Controls(0, 1.0), "FSCar"); } catch {}
            try { rpc.Call("enableApiControl", false, "FSCar"); } catch {}
        }
        void Drive() {
            CentrelineFollower follower = null;
            ulong previousTimestamp = 0;
            DateTime changedAt = DateTime.UtcNow, trackReadAt = DateTime.MinValue;
            try {
                while (!stopping.WaitOne(0)) {
                    try {
                        if (rpc == null) {
                            Publish("Connecting", "Waiting for a running simulation");
                            var connected = new LidarRpc(port);
                            lock (sync) {
                                if (stopping.WaitOne(0)) { connected.Dispose(); break; }
                                rpc = connected;
                            }
                            rpc.Call("enableApiControl", true, "FSCar");
                            rpc.Call("setCarControls", Controls(0, 1), "FSCar");
                            follower = null; previousTimestamp = 0; trackReadAt = DateTime.MinValue;
                            changedAt = DateTime.UtcNow;
                        }
                        double target = TargetSpeed;
                        var state = rpc.Call("getCarState", "FSCar") as Dictionary<string, object>;
                        double speed = SpeedFrom(state);
                        if (!state.ContainsKey("timestamp") || state["timestamp"] == null)
                            throw new InvalidDataException("Car timestamp is unavailable.");
                        ulong timestamp = Convert.ToUInt64(state["timestamp"], CultureInfo.InvariantCulture);
                        if (timestamp < previousTimestamp) follower = null;
                        if (timestamp != previousTimestamp) { previousTimestamp = timestamp; changedAt = DateTime.UtcNow; }
                        if (follower == null || (follower.WaypointCount < 2 && (DateTime.UtcNow-trackReadAt).TotalSeconds > 1)) {
                            follower = new CentrelineFollower(rpc.Call("getRefereeState")); trackReadAt = DateTime.UtcNow;
                        }
                        double steering = 0, followingSpeed = 0;
                        string reason = "Simulation paused";
                        bool following = timestamp != 0 && (DateTime.UtcNow-changedAt).TotalSeconds < 1 &&
                            follower.TryGetCommand(rpc.Call("simGetGroundTruthKinematics", "FSCar"), speed, target,
                                out steering, out followingSpeed, out reason);
                        if (following) {
                            double throttle = speed < followingSpeed - 0.1 ? Math.Min(0.16, 0.04 + (followingSpeed - speed) * 0.08) : 0;
                            double brake = speed > followingSpeed + 0.15 ? Math.Min(0.6, (speed - followingSpeed) * 0.3) : 0;
                            rpc.Call("setCarControls", Controls(throttle, brake, steering), "FSCar");
                            Publish("Running", String.Format(CultureInfo.InvariantCulture, "Centreline  /  {0:0.0} m/s", speed));
                        } else {
                            rpc.Call("setCarControls", Controls(0, 1), "FSCar");
                            Publish(reason == "End of track" ? "Finished" : "Waiting", reason);
                        }
                    } catch (Exception ex) {
                        Publish("Waiting", ex is RpcException || ex is InvalidDataException ? ex.Message : "Start a run in FSDS");
                        StopCar();
                        Disconnect();
                        if (stopping.WaitOne(600)) break;
                        continue;
                    }
                    if (stopping.WaitOne(50)) break;
                }
            } finally {
                StopCar();
                Disconnect();
                Publish("Stopped", "Stopped");
            }
        }
        public void Dispose() {
            stopping.Set();
            if (!worker.Join(3500)) Disconnect();
            if (worker.Join(2000)) stopping.Dispose();
        }
    }

    public sealed class LidarSnapshot {
        public string Status { get; private set; }
        public double[] Points { get; private set; }
        public ulong Timestamp { get; private set; }
        public DateTime ReceivedAt { get; private set; }
        public LidarSnapshot(string status, double[] points, ulong timestamp, DateTime? receivedAt = null) {
            Status = status; Points = points ?? new double[0]; Timestamp = timestamp; ReceivedAt = receivedAt ?? DateTime.UtcNow;
        }
    }

    public sealed class LidarFeed : IDisposable {
        readonly object sync = new object();
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        readonly Thread worker;
        readonly int port;
        LidarRpc rpc;
        LidarSnapshot latest = new LidarSnapshot("StartRun", null, 0);
        public LidarSnapshot Latest { get { lock (sync) return latest; } }
        public LidarFeed(int port) { this.port = port; worker = new Thread(Poll); worker.IsBackground = true; worker.Start(); }
        void Publish(string status, double[] points, ulong timestamp, DateTime? receivedAt = null) { lock (sync) latest = new LidarSnapshot(status, points, timestamp, receivedAt); }
        void Disconnect() { lock (sync) { if (rpc != null) { rpc.Dispose(); rpc = null; } } }
        void Poll() {
            ulong previous = 0;
            DateTime changedAt = DateTime.UtcNow;
            try {
                while (!stopping.WaitOne(0)) {
                    try {
                        if (rpc == null) {
                            var connected = new LidarRpc(port);
                            lock (sync) {
                                if (stopping.WaitOne(0)) { connected.Dispose(); break; }
                                rpc = connected;
                            }
                            previous = 0; changedAt = DateTime.UtcNow;
                        }
                        // A live car response distinguishes a missing lidar from the main menu.
                        rpc.Call("getCarState", "FSCar");
                        Dictionary<string, object> scan;
                        try { scan = rpc.Call("getLidarData", "Lidar", "FSCar") as Dictionary<string, object>; }
                        catch (RpcException) { Publish("NoLidar", null, 0); if (stopping.WaitOne(500)) break; continue; }
                        if (scan == null || !scan.ContainsKey("time_stamp") || !scan.ContainsKey("point_cloud"))
                            throw new InvalidDataException("Invalid lidar response.");
                        ulong timestamp = Convert.ToUInt64(scan["time_stamp"]);
                        if (timestamp == 0) { Publish("Waiting", null, 0); }
                        else {
                            if (timestamp != previous) { previous = timestamp; changedAt = DateTime.UtcNow; }
                            if ((DateTime.UtcNow - changedAt).TotalSeconds > 1.5) Publish("Paused", null, timestamp);
                            else Publish("Live", (double[])scan["point_cloud"], timestamp, changedAt);
                        }
                    } catch (Exception) {
                        Publish("StartRun", null, 0); Disconnect(); previous = 0;
                    }
                    if (stopping.WaitOne(200)) break;
                }
            } finally { Disconnect(); }
        }
        public void Dispose() {
            stopping.Set();
            lock (sync) { if (rpc != null) rpc.Dispose(); }
            // Closing the socket interrupts an in-flight read. The worker owns final cleanup.
            if (worker.Join(2000)) stopping.Dispose();
        }
    }

    public sealed class LidarPlot : FrameworkElement {
        double[] points = new double[0];
        bool topDown;
        bool angled;
        double cameraHeight = 1.2;
        double cameraTilt = 15;
        double range = 200;
        double zoom = 1, panX, panY;
        bool panning;
        Point previousMouse;
        const int ColorCount = 128;
        readonly Brush[] colors;
        readonly Pen gridPen;
        readonly Brush background;
        public LidarPlot() {
            ClipToBounds = true;
            Focusable = true;
            Cursor = Cursors.Hand;
            colors = new Brush[ColorCount];
            for (int i=0; i<ColorCount; i++) colors[i] = new SolidColorBrush(PaletteColor(i));
            foreach (var color in colors) color.Freeze();
            gridPen = new Pen(new SolidColorBrush(Color.FromRgb(35,52,73)), 1); gridPen.Freeze();
            background = new SolidColorBrush(Color.FromRgb(9,17,29)); background.Freeze();
        }
        public bool TopDown { get { return topDown; } set { topDown = value; InvalidateVisual(); } }
        public bool Angled { get { return angled; } set { angled = value; InvalidateVisual(); } }
        public double CameraHeight { get { return cameraHeight; } set { cameraHeight = Math.Max(0, Math.Min(5, value)); InvalidateVisual(); } }
        public double CameraTilt { get { return cameraTilt; } set { cameraTilt = Math.Max(0, Math.Min(45, value)); InvalidateVisual(); } }
        public double Range { get { return range; } set { range = Math.Max(5, Math.Min(200, value)); InvalidateVisual(); } }
        public double Zoom { get { return zoom; } }
        public double PanX { get { return panX; } }
        public double PanY { get { return panY; } }
        public int RenderedPointCount { get; private set; }
        public event EventHandler ViewChanged;
        void NotifyViewChanged() {
            InvalidateVisual();
            if (ViewChanged != null) ViewChanged(this, EventArgs.Empty);
        }
        public void ResetView() { zoom = 1; panX = panY = 0; NotifyViewChanged(); }
        public void ZoomAt(double factor, Point anchor) {
            if (Double.IsNaN(factor) || Double.IsInfinity(factor) || factor <= 0) return;
            double next = Math.Max(0.25, Math.Min(32, zoom*factor));
            double ratio = next/zoom, x = anchor.X-ActualWidth/2, y = anchor.Y-ActualHeight/2;
            panX = x-(x-panX)*ratio; panY = y-(y-panY)*ratio;
            zoom = next; NotifyViewChanged();
        }
        public void PanBy(double x, double y) { panX += x; panY += y; NotifyViewChanged(); }
        protected override void OnMouseWheel(MouseWheelEventArgs e) {
            ZoomAt(Math.Pow(1.2, e.Delta/120.0), e.GetPosition(this)); e.Handled = true;
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            Focus(); previousMouse = e.GetPosition(this); panning = CaptureMouse();
            if (panning) { Cursor = Cursors.SizeAll; e.Handled = true; }
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            if (!panning) return;
            Point current = e.GetPosition(this);
            PanBy(current.X-previousMouse.X, current.Y-previousMouse.Y);
            previousMouse = current; e.Handled = true;
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
            if (!panning) return;
            ReleaseMouseCapture(); e.Handled = true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e) {
            panning = false; Cursor = Cursors.Hand; base.OnLostMouseCapture(e);
        }
        public void SetPoints(double[] value) { points = value ?? new double[0]; InvalidateVisual(); }
        // Fixed physical scale: logarithmic spacing highlights small nearby changes.
        public static int DistanceColorIndex(double distance) {
            double fraction = Math.Log(1 + Math.Max(0, Math.Min(200, distance))/2) / Math.Log(101);
            return Math.Min(ColorCount-1, (int)(fraction*(ColorCount-1)));
        }
        static Color PaletteColor(int index) {
            double hue = 285.0 * index / (ColorCount-1) / 60;
            double chroma = 0.78, secondary = chroma*(1-Math.Abs(hue%2-1)), baseValue = 1-chroma;
            double r=0, g=0, b=0;
            switch ((int)hue) {
                case 0: r=chroma; g=secondary; break;
                case 1: r=secondary; g=chroma; break;
                case 2: g=chroma; b=secondary; break;
                case 3: g=secondary; b=chroma; break;
                default: r=secondary; b=chroma; break;
            }
            return Color.FromRgb((byte)Math.Round((r+baseValue)*255), (byte)Math.Round((g+baseValue)*255), (byte)Math.Round((b+baseValue)*255));
        }
        // Sensor frame: +X forward, +Y left, +Z up. No vehicle/world transform is applied.
        public static bool Project(double x, double y, double z, bool topDown, double range, double width, double height, out Point pixel) {
            return ProjectView(x,y,z,topDown,false,range,width,height,1.2,15,1,0,0,out pixel);
        }
        public static bool ProjectAngled(double x, double y, double z, double range, double width, double height, out Point pixel) {
            return ProjectAngled(x,y,z,range,width,height,1.2,15,out pixel);
        }
        public static bool ProjectAngled(double x, double y, double z, double range, double width, double height, double cameraHeight, double cameraTilt, out Point pixel) {
            return ProjectView(x,y,z,false,true,range,width,height,cameraHeight,cameraTilt,1,0,0,out pixel);
        }
        public static bool ProjectView(double x, double y, double z, bool topDown, bool raised, double range, double width, double height,
            double cameraHeight, double cameraTilt, double zoom, double panX, double panY, out Point pixel) {
            pixel = new Point();
            if (Double.IsNaN(x) || Double.IsNaN(y) || Double.IsNaN(z) || Double.IsInfinity(x) || Double.IsInfinity(y) || Double.IsInfinity(z)) return false;
            double distance = Math.Sqrt(x*x + y*y + z*z);
            if (distance > range || distance < 0.05) return false;
            double u, v;
            if (raised) {
                // Follow the heading from 2 m behind; camera changes only affect the view.
                double tilt = cameraTilt*Math.PI/180, dx = x+2, dz = z-cameraHeight;
                double forward = dx*Math.Cos(tilt)-dz*Math.Sin(tilt), up = dx*Math.Sin(tilt)+dz*Math.Cos(tilt);
                if (forward <= 0.05) return false;
                double focal = width*0.5;
                u = width/2-y/forward*focal; v = height/2-up/forward*focal;
            } else if (topDown) {
                double scale = Math.Min(width,height)*0.45/range;
                u = width/2-y*scale; v = height/2-x*scale;
            } else {
                if (x <= 0.05) return false;
                double focal = width*0.5;
                u = width/2-y/x*focal; v = height/2-z/x*focal;
            }
            // Clip after navigation so points outside the original view can pan into sight.
            pixel = new Point(width/2+(u-width/2)*zoom+panX, height/2+(v-height/2)*zoom+panY);
            return pixel.X >= 1 && pixel.Y >= 1 && pixel.X < width-1 && pixel.Y < height-1;
        }
        protected override void OnRender(DrawingContext drawing) {
            RenderedPointCount = 0;
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0 || height <= 0) return;
            drawing.DrawRectangle(background, null, new Rect(0,0,width,height));
            double stepX = width/8*zoom, stepY = height/8*zoom;
            double firstX = ((width/2+panX)%stepX+stepX)%stepX, firstY = ((height/2+panY)%stepY+stepY)%stepY;
            for (double x=firstX; x<width; x+=stepX) drawing.DrawLine(gridPen,new Point(x,0),new Point(x,height));
            for (double y=firstY; y<height; y+=stepY) drawing.DrawLine(gridPen,new Point(0,y),new Point(width,y));
            var geometry = new StreamGeometry[colors.Length]; var contexts = new StreamGeometryContext[colors.Length];
            for (int i=0; i<colors.Length; i++) { geometry[i]=new StreamGeometry(); contexts[i]=geometry[i].Open(); }
            try {
                // Inspect every return in the current scan, including points revealed by zoom/pan.
                int count = points.Length/3;
                for (int i=0; i<count; i++) {
                    double x=points[i*3], y=points[i*3+1], z=points[i*3+2]; Point pixel;
                    bool visible = ProjectView(x,y,z,topDown,angled,range,width,height,cameraHeight,cameraTilt,zoom,panX,panY,out pixel);
                    if (!visible) continue;
                    RenderedPointCount++;
                    double distance=Math.Sqrt(x*x+y*y+z*z);
                    int bucket=DistanceColorIndex(distance);
                    var context=contexts[bucket]; double size=1.4;
                    context.BeginFigure(new Point(pixel.X-size,pixel.Y-size), true, true);
                    context.LineTo(new Point(pixel.X+size,pixel.Y-size), true, false);
                    context.LineTo(new Point(pixel.X+size,pixel.Y+size), true, false);
                    context.LineTo(new Point(pixel.X-size,pixel.Y+size), true, false);
                }
            } finally { foreach (var context in contexts) context.Close(); }
            for (int i=0; i<colors.Length; i++) { geometry[i].Freeze(); drawing.DrawGeometry(colors[i],null,geometry[i]); }
            drawing.DrawEllipse(Brushes.White, null, new Point(width/2+panX,height/2+panY), 3,3);
        }
    }
}
