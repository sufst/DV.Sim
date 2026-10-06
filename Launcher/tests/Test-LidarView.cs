using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using DVSim;

public static class LidarViewChecks {
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static byte[] Hex(string text) {
        string[] parts = text.Split(' '); byte[] result = new byte[parts.Length];
        for (int i=0; i<parts.Length; i++) result[i] = Convert.ToByte(parts[i],16);
        return result;
    }
    static void Reject(string bytes) {
        bool rejected = false;
        try { new MessagePackReader(new MemoryStream(Hex(bytes))).ReadMessage(); }
        catch (IOException) { rejected = true; }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid/truncated MessagePack must be rejected.");
    }
    static void WaitFor(LidarFeed feed, string status) {
        DateTime deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline) { if (feed.Latest.Status == status) return; Thread.Sleep(25); }
        throw new Exception("Expected " + status + "; received " + feed.Latest.Status);
    }
    static void WaitUntil(Func<bool> ready, string message) {
        DateTime deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline) { if (ready()) return; Thread.Sleep(25); }
        throw new Exception(message);
    }
    sealed class Server : IDisposable {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback,0);
        readonly Thread worker;
        TcpClient active;
        volatile bool stop;
        public volatile int Mode; // 0 live, 1 missing lidar, 2 frozen, 3 empty, 4 disconnected.
        public volatile bool ApiEnabled;
        public volatile int ControlsSeen;
        public volatile int TrackMode; // 0 offset centreline, 1 no cones, 2 malformed pose.
        public double LastThrottle;
        public double LastBrake;
        public double LastSteering;
        public int Port { get; private set; }
        ulong timestamp = 9007199254740993UL;
        public Server() {
            listener.Start(); Port=((IPEndPoint)listener.LocalEndpoint).Port;
            worker=new Thread(Serve); worker.IsBackground=true; worker.Start();
        }
        static void Text(Stream stream, string value) {
            byte[] bytes=Encoding.UTF8.GetBytes(value); stream.WriteByte((byte)(0xa0|bytes.Length)); stream.Write(bytes,0,bytes.Length);
        }
        static void Integer(Stream stream, ulong value) {
            stream.WriteByte(0xcf); for(int shift=56;shift>=0;shift-=8) stream.WriteByte((byte)(value>>shift));
        }
        static void Float(Stream stream, double value) {
            stream.WriteByte(0xcb); byte[] bytes=BitConverter.GetBytes(value); if(BitConverter.IsLittleEndian) Array.Reverse(bytes); stream.Write(bytes,0,8);
        }
        static void Value(Stream stream, object value) {
            if(value==null) stream.WriteByte(0xc0);
            else if(value is System.Collections.Generic.Dictionary<string,object>) {
                var map=(System.Collections.Generic.Dictionary<string,object>)value;
                stream.WriteByte((byte)(0x80|map.Count));
                foreach(var entry in map) { Text(stream,entry.Key); Value(stream,entry.Value); }
            } else if(value is object[]) {
                var array=(object[])value;
                if(array.Length<16) stream.WriteByte((byte)(0x90|array.Length));
                else { stream.WriteByte(0xdc); stream.WriteByte((byte)(array.Length>>8)); stream.WriteByte((byte)array.Length); }
                foreach(object item in array) Value(stream,item);
            } else Float(stream,Convert.ToDouble(value));
        }
        void Serve() {
            while(!stop) {
                try {
                    using(var client=listener.AcceptTcpClient()) {
                        active=client;
                        var stream=client.GetStream(); var reader=new MessagePackReader(stream);
                        while(!stop) {
                            object[] request=(object[])reader.ReadMessage();
                            Check(request.Length==4 && Convert.ToInt32(request[0])==0,"RPC request envelope is incorrect.");
                            string method=(string)request[2]; var args=(object[])request[3];
                            Check(method=="getCarState" || method=="getLidarData" || method=="enableApiControl" || method=="setCarControls" || method=="getRefereeState" || method=="simGetGroundTruthKinematics","Unexpected RPC method.");
                            if(Mode==4) break;
                            if(method=="getCarState") {
                                Check(args.Length==1 && (string)args[0]=="FSCar","Car-state argument is incorrect.");
                            } else if(method=="getLidarData") {
                                Check(args.Length==2 && (string)args[0]=="Lidar" && (string)args[1]=="FSCar","Lidar arguments are incorrect.");
                            } else if(method=="enableApiControl") {
                                Check(args.Length==2 && args[0] is bool && (string)args[1]=="FSCar","API-control arguments are incorrect.");
                                ApiEnabled=(bool)args[0];
                            } else if(method=="getRefereeState") {
                                Check(args.Length==0,"Referee state must have no arguments.");
                            } else if(method=="simGetGroundTruthKinematics") {
                                Check(args.Length==1 && (string)args[0]=="FSCar","Ground truth pose argument is incorrect.");
                            } else {
                                Check(args.Length==2 && args[0] is System.Collections.Generic.Dictionary<string,object> && (string)args[1]=="FSCar","Car-control arguments are incorrect.");
                                var controls=(System.Collections.Generic.Dictionary<string,object>)args[0];
                                Check(controls.ContainsKey("throttle") && controls.ContainsKey("steering") && controls.ContainsKey("brake"),"Car controls must include throttle, steering and brake.");
                                Check(controls.ContainsKey("is_manual_gear") && (bool)controls["is_manual_gear"] &&
                                    controls.ContainsKey("manual_gear") && Convert.ToInt32(controls["manual_gear"])==1 &&
                                    controls.ContainsKey("gear_immediate") && (bool)controls["gear_immediate"],
                                    "Slow auto-run must select forward gear; low throttle can leave FSDS in neutral.");
                                LastThrottle=Convert.ToDouble(controls["throttle"]);
                                LastBrake=Convert.ToDouble(controls["brake"]);
                                LastSteering=Convert.ToDouble(controls["steering"]);
                                ControlsSeen++;
                            }
                            using(var response=new MemoryStream()) {
                                response.WriteByte(0x94); response.WriteByte(1); Integer(response,Convert.ToUInt64(request[1]));
                                if(Mode==1 && method=="getLidarData") { Text(response,"No lidar named Lidar"); response.WriteByte(0xc0); }
                                else {
                                    response.WriteByte(0xc0);
                                    if(method=="getCarState") { if(Mode!=2) timestamp++; response.WriteByte(0x82); Text(response,"timestamp"); Integer(response,timestamp); Text(response,"speed"); Float(response,0.25); }
                                    else if(method=="getRefereeState") {
                                        var track=(System.Collections.Generic.Dictionary<string,object>)CentrelineFollowerChecks.StraightTrack(0.7);
                                        if(TrackMode==1) track["cones"]=new object[0];
                                        Value(response,track);
                                    } else if(method=="simGetGroundTruthKinematics") {
                                        Value(response,TrackMode==2 ? new System.Collections.Generic.Dictionary<string,object>() : CentrelineFollowerChecks.Pose(0,0,0));
                                    }
                                    else {
                                        if(method=="getLidarData") {
                                            if(Mode!=2) timestamp++;
                                            response.WriteByte(0x82); Text(response,"time_stamp"); Integer(response,timestamp);
                                            Text(response,"point_cloud");
                                            if(Mode==3) response.WriteByte(0x90);
                                            else { response.WriteByte(0x99); foreach(double point in new double[]{10,0,0,10,2,1,5,-2,1}) Float(response,point); }
                                        } else response.WriteByte(0xc0);
                                    }
                                }
                                byte[] bytes=response.ToArray();
                                // Deliberately fragment the envelope to exercise TCP stream framing.
                                stream.Write(bytes,0,3); stream.Write(bytes,3,bytes.Length-3);
                            }
                        }
                    }
                } catch(Exception) { if(stop) break; }
                finally { active=null; }
            }
        }
        public void Dispose() { stop=true; listener.Stop(); if(active!=null) active.Close(); worker.Join(2000); }
    }
    public static string Run() {
        // Independent wire fixtures: float32, float64, signed ints and uint64 above 2^53.
        var numberArray=(object[])new MessagePackReader(new MemoryStream(Hex("94 ca 3f 80 00 00 cb 3f f8 00 00 00 00 00 00 fe cf 00 20 00 00 00 00 00 01"))).ReadMessage();
        Check(Convert.ToDouble(numberArray[0])==1 && Convert.ToDouble(numberArray[1])==1.5 && Convert.ToInt64(numberArray[2])==-2,"Numeric wire decoding failed.");
        Check((ulong)numberArray[3]==9007199254740993UL,"Timestamp precision was lost.");
        Reject("dc 00 02 01"); Reject("dd 7f ff ff ff"); Reject("81 ab 70 6f 69 6e 74 5f 63 6c 6f 75 64 92 01 02");
        Point pixel;
        Check(LidarPlot.DistanceColorIndex(0)==0 && LidarPlot.DistanceColorIndex(200)==127,"Colour scale must cover the sensor range.");
        Check(LidarPlot.DistanceColorIndex(3)-LidarPlot.DistanceColorIndex(2) > LidarPlot.DistanceColorIndex(101)-LidarPlot.DistanceColorIndex(100),"Nearby distance changes must have greater colour sensitivity.");
        int previousColour = -1;
        for (int metres=0; metres<=200; metres++) {
            int colour = LidarPlot.DistanceColorIndex(metres);
            Check(colour>=previousColour,"Distance colours must progress monotonically."); previousColour=colour;
        }
        Check(LidarPlot.Project(10,0,0,false,40,600,400,out pixel) && pixel.X==300 && pixel.Y==200,"Forward point should be centred.");
        Check(LidarPlot.Project(10,2,1,false,40,600,400,out pixel) && pixel.X<300 && pixel.Y<200,"Left/up sensor axes must map left/up.");
        Check(!LidarPlot.Project(-10,0,0,false,40,600,400,out pixel),"POV must exclude points behind the sensor.");
        Check(LidarPlot.Project(-10,0,0,true,40,600,400,out pixel) && pixel.Y>200,"Top-down must include rear returns.");
        Check(!LidarPlot.Project(Double.NaN,0,0,false,40,600,400,out pixel),"Non-finite points must be skipped.");
        Check(!LidarPlot.Project(50,0,0,false,40,600,400,out pixel),"View range must filter distant points.");
        Point raisedCentre, raisedLeft, raisedUp;
        Check(LidarPlot.ProjectAngled(10,0,0,40,600,400,out raisedCentre) && raisedCentre.X==300,"Raised POV must keep the sensor heading.");
        Check(LidarPlot.ProjectAngled(10,2,0,40,600,400,out raisedLeft) && raisedLeft.X<raisedCentre.X,"Raised POV must preserve the left axis.");
        Check(LidarPlot.ProjectAngled(10,0,1,40,600,400,out raisedUp) && raisedUp.Y<raisedCentre.Y,"Raised POV must preserve the up axis.");
        Check(!LidarPlot.ProjectAngled(-10,0,0,40,600,400,out pixel),"Raised POV must exclude points behind the camera.");
        Check(!LidarPlot.ProjectAngled(50,0,0,40,600,400,out pixel),"Raised POV must obey the view range.");
        Point levelView, higherView, tiltedView;
        Check(LidarPlot.ProjectAngled(10,0,0,40,600,400,0,0,out levelView) && levelView.Y==200,"Level raised camera must centre points at its height.");
        Check(LidarPlot.ProjectAngled(10,0,0,40,600,400,2,0,out higherView) && higherView.Y>levelView.Y,"Raising the camera must move lower returns down the view.");
        Check(LidarPlot.ProjectAngled(10,0,0,40,600,400,2,15,out tiltedView) && tiltedView.Y<higherView.Y,"Looking downward must move those returns up the view.");
        Check(!LidarPlot.Project(10,15,0,false,40,600,400,out pixel),"Test return must begin outside the sensor viewport.");
        Check(LidarPlot.ProjectView(10,15,0,false,false,40,600,400,1.2,15,1,450,0,out pixel) && pixel.X==300,"Panning must reveal returns outside the original viewport.");
        Check(LidarPlot.ProjectView(10,15,0,false,false,40,600,400,1.2,15,0.25,0,0,out pixel),"Zooming out must reveal more of the scan.");
        Check(!LidarPlot.ProjectView(50,0,0,true,false,40,600,400,1.2,15,0.25,0,0,out pixel),"Zooming out must still respect the sensor view range.");
        using(var server=new Server()) using(var feed=new LidarFeed(server.Port)) {
            WaitFor(feed,"Live"); Check(feed.Latest.Points.Length==9 && feed.Latest.Timestamp>9007199254740993UL,"Real RPC data must reach the feed.");
            server.Mode=2; WaitFor(feed,"Paused");
            server.Mode=0; WaitFor(feed,"Live");
            server.Mode=1; WaitFor(feed,"NoLidar");
            server.Mode=3; WaitFor(feed,"Live"); Check(feed.Latest.Points.Length==0,"Empty scans are valid live scans.");
            server.Mode=4; WaitFor(feed,"StartRun"); Check(feed.Latest.Points.Length==0,"Disconnect must clear stale dots.");
            server.Mode=0; WaitFor(feed,"Live");
        }
        using(var server=new Server()) {
            using(var driver=new SlowAutoDriver(server.Port,1.5)) {
                WaitUntil(()=>server.ApiEnabled && server.LastThrottle>0 && driver.Status=="Running","Slow auto-run must enable API control and follow the centreline.");
                Check(server.LastThrottle>0,"Slow auto-run must apply throttle below the target speed.");
                Check(server.LastSteering<0,"Slow auto-run must steer left towards an offset centreline.");
                Check(driver.Status=="Running","Slow auto-run should report a running state.");
                server.Mode=2;
                WaitUntil(()=>driver.Status=="Waiting" && server.LastThrottle==0 && server.LastBrake==1,"Paused simulation must hold the brake.");
                server.Mode=0;
                WaitUntil(()=>driver.Status=="Running" && server.LastThrottle>0,"Resuming simulation must resume following.");
            }
            WaitUntil(()=>!server.ApiEnabled,"Slow auto-run must release API control when stopped.");
            Check(server.LastThrottle==0 && server.LastBrake==1,"Stopping must apply the brake before releasing control.");
        }
        using(var server=new Server()) {
            server.TrackMode=1;
            using(var driver=new SlowAutoDriver(server.Port,1.5)) {
                WaitUntil(()=>server.ControlsSeen>1 && driver.Status=="Waiting","Missing track must report waiting.");
                Check(server.LastThrottle==0 && server.LastBrake==1,"Missing track must brake without driving straight.");
                server.TrackMode=0;
                WaitUntil(()=>driver.Status=="Running" && server.LastThrottle>0,"Track becoming available must resume following.");
                server.TrackMode=2;
                WaitUntil(()=>driver.Status=="Waiting" && server.LastThrottle==0 && server.LastBrake==1,"Malformed car pose must brake.");
            }
        }
        return "PASS: MessagePack fixtures, sensor POV, 360-degree view, real RPC polling, pause, missing lidar, empty scans, reconnection and slow auto-run controls.";
    }
}
