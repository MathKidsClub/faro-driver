using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FaroArm;
using FaroArm.Interfaces;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FaroDriverV2
{
    internal sealed class Options
    {
        public string VerifiedBallCentreSerial;
        public string Mode = "stream", Serial, Bind = "tcp://127.0.0.1:5457", Control = "tcp://127.0.0.1:5458";
        public int Seconds = 0, DiscoverySeconds = 20, FirstSampleSeconds = 10;
        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--diagnose" || arg == "--list" || arg == "--fixture" || arg == "--self-test" || arg == "--help")
                {
                    if (options.Mode != "stream") throw new ArgumentException("Choose one mode.");
                    options.Mode = arg.Substring(2);
                    continue;
                }
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + arg);
                string value = args[i];
                switch (arg)
                {
                    case "--serial": options.Serial = value; break;
                    case "--verified-ball-centre": options.VerifiedBallCentreSerial = value; break;
                    case "--bind": options.Bind = value; break;
                    case "--control": options.Control = value; break;
                    case "--seconds": options.Seconds = Positive(value); break;
                    case "--discover-seconds": options.DiscoverySeconds = Positive(value); break;
                    case "--first-sample-seconds": options.FirstSampleSeconds = Positive(value); break;
                    default: throw new ArgumentException("Unknown option " + arg);
                }
            }
            if (!options.Bind.StartsWith("tcp://127.0.0.1:", StringComparison.Ordinal))
                throw new ArgumentException("This prototype publishes on loopback only.");
            int port;
            if (!int.TryParse(options.Bind.Substring("tcp://127.0.0.1:".Length), out port) || port < 1 || port > 65535)
                throw new ArgumentException("Invalid TCP port.");
            if (options.Mode == "fixture" && port == 5457)
                throw new ArgumentException("Fixture mode requires a separate port, e.g. --bind tcp://127.0.0.1:55457.");
            if (!options.Control.StartsWith("tcp://127.0.0.1:", StringComparison.Ordinal)
                || !int.TryParse(options.Control.Substring("tcp://127.0.0.1:".Length), out port)
                || port < 1 || port > 65535 || options.Control == options.Bind)
                throw new ArgumentException("Control must use a separate loopback TCP port.");
            return options;
        }
        private static int Positive(string value)
        {
            int number;
            if (!int.TryParse(value, out number) || number <= 0 || number > 86400)
                throw new ArgumentException("Duration must be 1..86400 seconds.");
            return number;
        }
    }

    internal sealed class Publisher : IDisposable
    {
        // The vendor callback and NetMQ socket never run on the same thread.
        private readonly BlockingCollection<string> queue = new BlockingCollection<string>(64);
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
        private readonly Thread thread;
        private volatile Exception failure;
        private long dropped, sent;
        public long Dropped => Interlocked.Read(ref dropped);
        public long Sent => Interlocked.Read(ref sent);
        public Publisher(string endpoint)
        {
            thread = new Thread(() => Run(endpoint)) { IsBackground = true, Name = "FARO publisher" };
            thread.Start();
            if (!ready.Wait(5000)) throw new TimeoutException("Publisher did not initialize.");
            Check();
        }
        public void Enqueue(Dictionary<string, object> packet)
        {
            string json = JsonConvert.SerializeObject(packet);
            if (!queue.TryAdd(json)) Interlocked.Increment(ref dropped);
        }
        public void Check()
        {
            if (failure != null) throw new InvalidOperationException("Publisher failed: " + failure.Message, failure);
        }
        private void Run(string endpoint)
        {
            try
            {
                using (var socket = new PublisherSocket())
                {
                    socket.Options.Linger = TimeSpan.Zero;
                    socket.Options.SendHighWatermark = 64;
                    socket.Bind(endpoint);
                    ready.Set();
                    foreach (string json in queue.GetConsumingEnumerable())
                    {
                        socket.SendMoreFrame("FaroArmUpdate").SendFrame(json);
                        Interlocked.Increment(ref sent);
                    }
                }
            }
            catch (Exception ex) { failure = ex; ready.Set(); }
        }
        public void Dispose()
        {
            queue.CompleteAdding();
            if (!thread.Join(3000)) Program.Log("Publisher shutdown timed out.");
            // NetMQ's process-wide cleanup belongs to the process owner.
            NetMQConfig.Cleanup(false);
        }
    }

    internal static class Program
    {
        private static readonly CancellationTokenSource Stop = new CancellationTokenSource();
        private static readonly object CaptureLock = new object();
        private static readonly string SourceId = Guid.NewGuid().ToString();
        private static long received;
        private static DateTime lastSample;
        private static DateTime selectionGraceUntil;
        private static bool accepting;
        private static Dictionary<string, object> activeProbe;
        private static long probeRevision;
        private static string positionState = "UNKNOWN";
        private static string verifiedBallCentreSerial;
        public static void Log(string message) => Console.Error.WriteLine(DateTime.UtcNow.ToString("O") + " " + message);

        [STAThread]
        public static int Main(string[] args)
        {
            Options options;
            try { options = Options.Parse(args); }
            catch (ArgumentException ex) { Log(ex.Message); return 64; }
            verifiedBallCentreSerial = options.VerifiedBallCentreSerial;
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; Stop.Cancel(); };
            if (options.Mode == "help")
            {
                Console.WriteLine("FaroDriver [--diagnose | --list | --fixture | --self-test] [--serial SERIAL] [--seconds N]\n" +
                    "  [--bind tcp://127.0.0.1:5457] [--control tcp://127.0.0.1:5458] [--discover-seconds N] [--first-sample-seconds N]\n" +
                    "  [--verified-ball-centre SERIAL] (operator-validated coordinate reference only)\nDefault: hardware stream, using the installed vendor ATL hardware server, without Manager UI.\n" +
                    "Fixture: synthetic TEST DATA; requires a separate port. No hardware connection.");
                return 0;
            }
            try
            {
                if (options.Mode == "diagnose") return Diagnose();
                if (options.Mode == "self-test") return SelfTest();
                if (options.Mode == "fixture") return Fixture(options);
                if (Process.GetProcessesByName("FaroArm.Manager").Length != 0)
                    throw new InvalidOperationException("Close FARO Manager for an independent driver test.");
                return Hardware(options);
            }
            catch (Exception ex) { Log(ex.ToString()); return 3; }
        }

        private static int Diagnose()
        {
            var files = new List<object>();
            foreach (string name in new[] { "FaroArm.Interfaces.dll", "FaroArm.Net.dll", "FaroArm.Native.dll", "NetMQ.dll" })
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
                files.Add(new { path, exists = File.Exists(path), version = File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null });
            }
            // Assembly loading validates mixed-mode CLR compatibility, without calling Connect().
            var factoryType = typeof(FaroArmInterfaceFactory);
            Console.WriteLine(JsonConvert.SerializeObject(new {
                mode = "diagnose_no_connection", x64 = Environment.Is64BitProcess, clr = Environment.Version.ToString(),
                factory = factoryType.Assembly.FullName, files,
                manager_processes = Process.GetProcessesByName("FaroArm.Manager").Select(p => p.Id).ToArray(),
                hardware_server_processes = Process.GetProcessesByName("FAROAtlServerApp5").Select(p => p.Id).ToArray()
            }, Formatting.Indented));
            return Environment.Is64BitProcess ? 0 : 3;
        }

        private static T DriverCall<T>(Func<T> call, string name, int milliseconds = 15000)
        {
            var task = Task.Run(call);
            if (!task.Wait(milliseconds))
            {
                Log(name + " exceeded its deadline. Exiting this client to avoid using a still-running native call.");
                Environment.Exit(6);
            }
            return task.GetAwaiter().GetResult();
        }

        private static int Hardware(Options options)
        {
            var factory = new FaroArmInterfaceFactory();
            var manager = factory.CreateIFaroArmManager();
            IFaroArm arm = null;
            bool connected = false, previousReference = false, referenceChanged = false;
            Publisher publisher = null;
            ResponseSocket control = null;
            EventHandler<FaroArmUpdateEventArgs> handler = null;
            try
            {
                // Bind first so an existing data source is caught before device state changes.
                if (options.Mode != "list")
                {
                    publisher = new Publisher(options.Bind);
                    control = new ResponseSocket();
                    control.Options.Linger = TimeSpan.Zero;
                    control.Options.ReceiveHighWatermark = 8;
                    control.Bind(options.Control);
                }
                var code = DriverCall(() => manager.Connect(), "ATL server connect", 65000);
                Log("Connect: " + code + " (" + (int)code + ")");
                if (code != FaroReturnCode.Success) return 3;
                connected = true;
                Tuple<string, FaroArmConnectionType>[] devices = new Tuple<string, FaroArmConnectionType>[0];
                var discovery = Stopwatch.StartNew();
                while (!Stop.IsCancellationRequested)
                {
                    devices = DriverCall(() => manager.GetFaroArmSerialNumbers(), "Enumerate arms") ?? devices;
                    if (devices.Length > 0 || discovery.Elapsed.TotalSeconds >= options.DiscoverySeconds) break;
                    Stop.Token.WaitHandle.WaitOne(250);
                }
                Console.WriteLine(JsonConvert.SerializeObject(new { devices = devices.Select(d => new { serial = d.Item1, connection = d.Item2.ToString() }) }));
                if (devices.Length == 0) { Log("No arm discovered. No coordinates were published."); return 2; }
                if (options.Mode == "list") return 0;
                if (devices.Length > 1 && string.IsNullOrEmpty(options.Serial))
                    throw new InvalidOperationException("Multiple arms found; select one with --serial.");
                string serial = options.Serial ?? devices[0].Item1;
                if (!devices.Any(d => d.Item1 == serial)) throw new InvalidOperationException("Selected arm was not discovered.");
                arm = DriverCall(() => manager.GetFaroArm(serial), "Select arm");
                if (arm == null || arm.SerialNumber != serial) throw new InvalidOperationException("Arm selection returned an uninitialized or different arm.");
                var support = DriverCall(() => arm.GetHardwareSupportLevel(), "Read hardware support");
                // Vendor lookup-table initialization, matching the installed Manager.
                DriverCall(() => {
                    uint ticks = unchecked((uint)Environment.TickCount);
                    arm.CheckLookupTable(unchecked((int)((ticks << 16) | (ticks >> 16))));
                    return true;
                }, "Initialize lookup table");
                previousReference = arm.ReferenceEncodersEnabled;
                if (!DriverCall(() => arm.EnableReferenceEncoders(true), "Enable encoder referencing"))
                    throw new InvalidOperationException("Driver refused encoder referencing.");
                referenceChanged = !previousReference;
                Log("Selected " + serial + "; support=" + support + "; trigger mode=" + arm.TriggerMode);
                RefreshProbe(arm, serial, factory);
                handler = (s, e) => {
                    lock (CaptureLock)
                    {
                        if (!accepting) return;
                        try
                        {
                            if (e.FaroArmSerialNumber != serial)
                                throw new InvalidOperationException("Callback belongs to another arm.");
                            long sequence = ++received;
                            var packet = Snapshot(e.FaroArmUpdate, serial, sequence);
                            packet["probe_contract"] = "faro_probe_v1";
                            packet["probe"] = activeProbe;
                            packet["probe_revision"] = probeRevision;
                            packet["position_state"] = positionState;
                            packet["button_mapping"] = new { capture_bit = 2, advance_bit = 1 };
                            publisher.Enqueue(packet);
                            lastSample = DateTime.UtcNow;
                        }
                        catch (Exception ex) { Log("Rejected callback: " + ex.Message); }
                    }
                };
                arm.FaroArmUpdate += handler;
                lock (CaptureLock) accepting = true;
                if (!DriverCall(() => arm.EnableFaroArmUpdates(true), "Enable arm updates"))
                    throw new InvalidOperationException("Driver refused arm updates.");
                var streaming = Stopwatch.StartNew();
                long lastReport = -1;
                while (!Stop.IsCancellationRequested && (options.Seconds == 0 || streaming.Elapsed.TotalSeconds < options.Seconds))
                {
                    publisher.Check();
                    string request;
                    if (control.TryReceiveFrameString(TimeSpan.Zero, out request))
                    {
                        object reply;
                        try { reply = HandleControl(request, factory, arm, serial); }
                        catch (Exception ex) { reply = new { ok = false, error = ex.Message }; }
                        control.SendFrame(JsonConvert.SerializeObject(reply));
                    }
                    long count; DateTime last;
                    lock (CaptureLock) { count = received; last = lastSample; }
                    if (count == 0 && streaming.Elapsed.TotalSeconds >= options.FirstSampleSeconds)
                    { Log("Arm selected but no update callbacks arrived. Hardware validation failed."); return 4; }
                    if ((long)streaming.Elapsed.TotalSeconds != lastReport)
                    {
                        lastReport = (long)streaming.Elapsed.TotalSeconds;
                        Log("callbacks=" + count + " published=" + publisher.Sent + " queue_drops=" + publisher.Dropped +
                            " age_ms=" + (last == default(DateTime) ? "none" : (DateTime.UtcNow - last).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)));
                    }
                    if (count > 0 && DateTime.UtcNow > selectionGraceUntil && (DateTime.UtcNow - last).TotalSeconds > 3)
                    { Log("Update stream stopped. Restart after checking the arm connection."); return 4; }
                    Stop.Token.WaitHandle.WaitOne(100);
                }
                return received > 0 ? 0 : 4;
            }
            finally
            {
                lock (CaptureLock) accepting = false;
                if (arm != null)
                {
                    if (handler != null) arm.FaroArmUpdate -= handler;
                    try { DriverCall(() => arm.EnableFaroArmUpdates(false), "Disable arm updates"); }
                    catch (Exception ex) { Log("Disable updates: " + ex.Message); }
                    if (referenceChanged)
                        try { DriverCall(() => arm.EnableReferenceEncoders(previousReference), "Restore referencing"); }
                        catch (Exception ex) { Log("Restore referencing: " + ex.Message); }
                }
                if (connected)
                    try { DriverCall(() => manager.Disconnect(), "Disconnect client"); }
                    catch (Exception ex) { Log("Disconnect: " + ex.Message); }
                control?.Dispose();
                publisher?.Dispose();
                GC.KeepAlive(factory);
            }
        }

        private static Dictionary<string, object> DescribeProbe(IProbe probe)
        {
            if (probe == null) return null;
            var c = probe.ActiveCompensation;
            var diameter = probe.Diameter;
            if (double.IsNaN(diameter) || double.IsInfinity(diameter) || diameter < 0)
                throw new InvalidOperationException("Vendor probe diameter is invalid.");
            var result = new Dictionary<string, object> {
                ["id"] = probe.GetID, ["name"] = probe.ProbeName, ["diameter_mm"] = diameter,
                ["radius_mm"] = diameter / 2, ["active"] = probe.IsActive,
                ["compensation_valid"] = c != null && c.IsCompensated,
                ["compensation_id"] = c == null ? (object)null : c.Id,
                ["compensation_date"] = c == null || c.ProbeCompensationDate == DateTime.MinValue
                    ? null : c.ProbeCompensationDate.ToString("O"),
                ["compensation_points"] = c == null ? 0 : c.PointsTaken,
                ["offset_mm"] = c == null ? null : new[] { c.X, c.Y, c.Z },
                ["max_error_mm"] = c == null ? (object)null : c.MaxError,
                ["two_sigma_mm"] = c == null ? (object)null : c.TwoSigma
            };
            // Don't publish fabricated/invalid calibration metadata.
            if (c != null && new[] { c.X, c.Y, c.Z, c.MaxError, c.TwoSigma }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new InvalidOperationException("Vendor probe compensation contains non-finite values.");
            return result;
        }

        private static void RefreshProbe(IFaroArm arm, string serial, FaroArmInterfaceFactory factory)
        {
            var probe = DriverCall(() => arm.GetProbe(), "Read active probe");
            var metadata = DescribeProbe(probe);
            var c = probe?.ActiveCompensation;
            var list = DriverCall(() => factory.CreateIProbesList().GetProbesList(serial), "Resolve active saved probe");
            var matches = (list.GeneralProbes ?? new IProbe[0]).Where(p =>
                p != null && probe != null && p.ProbeName == probe.ProbeName && p.Diameter == probe.Diameter
                && p.ActiveCompensation != null && c != null
                && p.ActiveCompensation.ProbeCompensationDate == c.ProbeCompensationDate
                && p.ActiveCompensation.X == c.X && p.ActiveCompensation.Y == c.Y && p.ActiveCompensation.Z == c.Z
                && p.ActiveCompensation.IsCompensated == c.IsCompensated).ToArray();
            if (metadata != null)
            {
                metadata["backend_id"] = probe.GetID;
                // GetProbe() uses a transient -1 ID on this USB arm. Resolve
                // the unique saved profile by its actual compensation, not
                // by name alone or by a guessed/current UI selection.
                if (matches.Length == 1) metadata["id"] = matches[0].GetID;
            }
            // Coordinate semantics must be physically verified for the selected arm.
            // The opt-in assertion is not a calibration or an accuracy guarantee.
            bool saved = matches.Length == 1 && c != null && c.ProbeCompensationDate != DateTime.MinValue
                && (c.X != 0 || c.Y != 0 || c.Z != 0 || probe.Diameter == 0);
            lock (CaptureLock)
            {
                activeProbe = metadata;
                positionState = !string.IsNullOrEmpty(verifiedBallCentreSerial)
                    && string.Equals(serial, verifiedBallCentreSerial, StringComparison.Ordinal)
                    && saved && c.IsCompensated && probe.IsActive ? "BALL_CENTRE" : "UNKNOWN";
                ++probeRevision;
            }
            Log("Active probe: " + JsonConvert.SerializeObject(metadata) + "; position_state=" + positionState);
        }

        private static object HandleControl(string request, FaroArmInterfaceFactory factory, IFaroArm arm, string serial)
        {
            if (request.Length > 4096) throw new ArgumentException("Control request too large.");
            var command = JObject.Parse(request);
            string action = (string)command["command"];
            if (action == "shutdown")
            {
                Stop.Cancel();
                return new { ok = true };
            }
            if (action != "list_probes" && action != "select_probe" && action != "status")
                throw new ArgumentException("Unsupported control command.");
            var list = DriverCall(() => factory.CreateIProbesList().GetProbesList(serial), "Read saved probes");
            var probes = (list.GeneralProbes ?? new IProbe[0]).Where(p => p != null && p.ProbeModel == FaroProbeModel.HardProbe).ToArray();
            if (action == "select_probe")
            {
                if (command["probe_id"]?.Type != JTokenType.Integer)
                    throw new ArgumentException("Select a saved integer probe ID.");
                int id = (int)command["probe_id"];
                var selected = probes.SingleOrDefault(p => p.GetID == id);
                if (selected == null) throw new ArgumentException("Saved hard probe not found.");
                lock (CaptureLock) accepting = false;
                try
                {
                    if (!DriverCall(() => arm.EnableFaroArmUpdates(false), "Pause updates for probe selection"))
                        throw new InvalidOperationException("Cannot pause updates.");
                    if (!DriverCall(() => arm.SetProbe(selected), "Select saved probe"))
                        throw new InvalidOperationException("Vendor refused probe selection.");
                    RefreshProbe(arm, serial, factory);
                    if (activeProbe == null || (int)activeProbe["id"] != id)
                        throw new InvalidOperationException("Vendor active probe does not match requested probe.");
                }
                finally
                {
                    // Refresh actual state even after partial/failed selection.
                    RefreshProbe(arm, serial, factory);
                    if (!DriverCall(() => arm.EnableFaroArmUpdates(true), "Resume updates"))
                        throw new InvalidOperationException("Cannot resume updates.");
                    selectionGraceUntil = DateTime.UtcNow.AddSeconds(3);
                    lock (CaptureLock) accepting = true;
                }
            }
            return new {
                ok = true, source_instance_id = SourceId, serial_number = serial,
                active_probe = activeProbe, position_state = positionState, probe_revision = probeRevision,
                probes = probes.Select(DescribeProbe).ToArray(),
                button_mapping = new { capture_bit = 2, advance_bit = 1 }
            };
        }

        private static Dictionary<string, object> Snapshot(IFaroArmUpdate u, string serial, long sequence)
        {
            var packet = new Dictionary<string, object> {
                ["schema_version"] = "legacy_flat_v0", ["producer"] = "FaroDriver_v2",
                ["source_instance_id"] = SourceId, ["serial_number"] = serial,
                ["Sequence"] = sequence, ["receive_timestamp_utc"] = DateTime.UtcNow.ToString("O"),
                ["X"] = u.X, ["Y"] = u.Y, ["Z"] = u.Z, ["A"] = u.A, ["B"] = u.B, ["C"] = u.C,
                ["J1"] = u.Angle1, ["J2"] = u.Angle2, ["J3"] = u.Angle3, ["J4"] = u.Angle4,
                ["J5"] = u.Angle5, ["J6"] = u.Angle6, ["J7"] = u.Angle7,
                ["Buttons"] = u.Buttons, ["EndStops"] = u.EndStops, ["EncodersReferenced"] = u.EncodersReferenced,
                ["ArmAtRest"] = u.ArmAtRest, ["TimeStamp"] = u.TimeStamp,
                ["Temperature"] = u.Temperature, ["Connected"] = true,
                ["coordinate_semantics"] = "unchanged_vendor_IFaroArmUpdate"
            };
            foreach (object value in packet.Values)
                if (value is double number && (double.IsNaN(number) || double.IsInfinity(number)))
                    return new Dictionary<string, object> { ["Connected"] = false, ["error"] = "non_finite_vendor_packet", ["Sequence"] = sequence };
            return packet;
        }

        private static int Fixture(Options options)
        {
            Log("TEST FIXTURE ONLY: no arm connection; test packets are deliberately rejected by the production measurement clients.");
            using (var publisher = new Publisher(options.Bind))
            {
                int seconds = options.Seconds == 0 ? 5 : options.Seconds;
                var timer = Stopwatch.StartNew();
                long sequence = 0;
                while (timer.Elapsed.TotalSeconds < seconds && !Stop.IsCancellationRequested)
                {
                    publisher.Check();
                    var packet = Snapshot(new FixtureUpdate { Buttons = sequence % 20 == 0 ? 1 : 0 }, "TEST_ONLY", ++sequence);
                    packet["schema_version"] = "driver_v2_test_fixture";
                    packet["Simulation"] = true;
                    packet["Connected"] = false;
                    publisher.Enqueue(packet);
                    Stop.Token.WaitHandle.WaitOne(10);
                }
                Log("Fixture queued " + sequence + " packets.");
            }
            return 0;
        }

        private static int SelfTest()
        {
            var input = new FixtureUpdate { Buttons = 3, EncodersReferenced = true, EndStops = 9, TimeStamp = 123 };
            var packet = Snapshot(input, "TEST_ONLY", 42);
            input.X = 999;
            if ((double)packet["X"] != 10.25 || (double)packet["Y"] != -20.5 || (double)packet["A"] != 1.0)
                throw new Exception("Snapshot changed vendor coordinates or retained mutable input.");
            if ((int)packet["Buttons"] != 3 || !(bool)packet["EncodersReferenced"] || (int)packet["EndStops"] != 9
                || (long)packet["Sequence"] != 42 || (int)packet["TimeStamp"] != 123)
                throw new Exception("Snapshot lost device status or sequence.");
            input.X = double.NaN;
            if ((bool)Snapshot(input, "TEST_ONLY", 43)["Connected"])
                throw new Exception("Invalid vendor coordinates were accepted.");
            bool blocked = false;
            try { Options.Parse(new[] { "--fixture" }); }
            catch (ArgumentException) { blocked = true; }
            if (!blocked) throw new Exception("Fixture was allowed on the production port.");
            Console.WriteLine("PASS: snapshot immutability, coordinate/status preservation, non-finite invalidation, fixture isolation.");
            return 0;
        }
    }
}
