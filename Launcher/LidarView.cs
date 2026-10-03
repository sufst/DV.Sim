using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace DVSim {
    // The small MessagePack subset used by FSDS's read-only RPC responses.
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
        public object Call(string method, params string[] arguments) {
            uint id = ++sequence;
            using (var request = new MemoryStream()) {
                request.WriteByte(0x94); request.WriteByte(0); request.WriteByte(0xce);
                for (int shift = 24; shift >= 0; shift -= 8) request.WriteByte((byte)(id >> shift));
                WriteText(request, method); request.WriteByte((byte)(0x90 | arguments.Length));
                foreach (string argument in arguments) WriteText(request, argument);
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
        const int ColorCount = 128;
        readonly Brush[] colors;
        readonly Pen gridPen;
        readonly Brush background;
        public LidarPlot() {
            ClipToBounds = true;
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
            pixel = new Point();
            if (Double.IsNaN(x) || Double.IsNaN(y) || Double.IsNaN(z) || Double.IsInfinity(x) || Double.IsInfinity(y) || Double.IsInfinity(z)) return false;
            double distance = Math.Sqrt(x*x + y*y + z*z);
            if (distance > range || distance < 0.05) return false;
            double u, v;
            if (topDown) { double scale = Math.Min(width, height) * 0.45 / range; u = width/2 - y*scale; v = height/2 - x*scale; }
            else { if (x <= 0.05) return false; double focal = width * 0.5; u = width/2 - y/x*focal; v = height/2 - z/x*focal; }
            if (u < 1 || v < 1 || u >= width-1 || v >= height-1) return false;
            pixel = new Point(u, v); return true;
        }
        public static bool ProjectAngled(double x, double y, double z, double range, double width, double height, out Point pixel) {
            return ProjectAngled(x,y,z,range,width,height,1.2,15,out pixel);
        }
        public static bool ProjectAngled(double x, double y, double z, double range, double width, double height, double cameraHeight, double cameraTilt, out Point pixel) {
            pixel = new Point();
            if (Double.IsNaN(x) || Double.IsNaN(y) || Double.IsNaN(z) || Double.IsInfinity(x) || Double.IsInfinity(y) || Double.IsInfinity(z)) return false;
            double distance = Math.Sqrt(x*x + y*y + z*z);
            if (distance > range || distance < 0.05) return false;
            // Follow the sensor's heading from 2 m behind it.
            // Height and downward tilt move only the viewing camera.
            double tilt = cameraTilt * Math.PI / 180;
            double dx = x + 2, dz = z - cameraHeight;
            double forward = dx*Math.Cos(tilt) - dz*Math.Sin(tilt);
            double up = dx*Math.Sin(tilt) + dz*Math.Cos(tilt);
            if (forward <= 0.05) return false;
            double focal = width * 0.5;
            pixel = new Point(width/2 - y/forward*focal, height/2 - up/forward*focal);
            return pixel.X >= 1 && pixel.Y >= 1 && pixel.X < width-1 && pixel.Y < height-1;
        }
        protected override void OnRender(DrawingContext drawing) {
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0 || height <= 0) return;
            drawing.DrawRectangle(background, null, new Rect(0,0,width,height));
            for (int i=1; i<8; i++) {
                drawing.DrawLine(gridPen, new Point(width*i/8,0), new Point(width*i/8,height));
                drawing.DrawLine(gridPen, new Point(0,height*i/8), new Point(width,height*i/8));
            }
            var geometry = new StreamGeometry[colors.Length]; var contexts = new StreamGeometryContext[colors.Length];
            for (int i=0; i<colors.Length; i++) { geometry[i]=new StreamGeometry(); contexts[i]=geometry[i].Open(); }
            try {
                // Bound rendering work; acquisition still reads every scan point.
                int count = points.Length/3, stride = Math.Max(1, (count + 17999)/18000);
                for (int i=0; i<count; i+=stride) {
                    double x=points[i*3], y=points[i*3+1], z=points[i*3+2]; Point pixel;
                    bool visible = angled ? ProjectAngled(x,y,z,range,width,height,cameraHeight,cameraTilt,out pixel) : Project(x,y,z,topDown,range,width,height,out pixel);
                    if (!visible) continue;
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
            drawing.DrawEllipse(Brushes.White, null, new Point(width/2,height/2), 3,3);
        }
    }
}
