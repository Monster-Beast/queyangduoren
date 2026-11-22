using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Queyang.Core;

namespace Queyang.Game
{
    public static class CoopSession
    {
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static CoopTransport link;
        static SessionMetrics metrics;
        static bool previousBackgroundMode;
        static RoomHost host;
        static RoomClient client;
        static global::Game colony;
        static string notice = "Offline";
        static string closing;
        static string replicaDirectory;
        internal static bool CapturingJoinSave { get; private set; }
        static string closedReplica;
        static bool nativeClient, sceneAnnounced;
        internal static ColonyView NativeView { get; private set; }
        public static bool IsNativeClient => client != null && nativeClient;
        static double joinTime, lastIncoming, nextPing;
        public static bool IsHosting => host != null;
        internal static void OrderChanged(int cell)
        {
            if (host==null || !Grid.IsValidCell(cell)) return;
            Grid.CellToXY(cell,out int x,out int y);
            host.WorldCellChanged(x,y,ColonyView.Width,ColonyView.Height);
        }
        public static bool IsClient => client != null;
        public static byte[] View => client?.View;
        public static string Status => client?.Status ?? (host == null ? notice : notice + " (" + host.Guests + "/3)");
        public static string Endpoint => link?.Endpoint ?? "";
        public static event Action<string> CommandResult;
        static double Time() => Clock.Elapsed.TotalSeconds;

        static string Fingerprint()
        {
            if (DlcManager.GetActiveDLCIds().Count != 0)
                throw new InvalidOperationException("Co-op currently supports Vanilla only. Disable active DLC to play.");
            var extra = Global.Instance.modManager.mods.FirstOrDefault(m =>
                m.IsEnabledForActiveDlc() && m.staticID != ModEntry.ModId);
            if (extra != null) throw new InvalidOperationException("Unsupported active mod: " + extra.staticID);
            return typeof(global::Game).Assembly.ManifestModule.ModuleVersionId + "/" +
                typeof(CoopSession).Assembly.ManifestModule.ModuleVersionId + "/" + typeof(Queyang.Core.Wire).Assembly.ManifestModule.ModuleVersionId;
        }

        static CoopTransport Open()
        {
            previousBackgroundMode = UnityEngine.Application.runInBackground;
            UnityEngine.Application.runInBackground = true;
            return new CoopTransport(Receive, Connected, Disconnected);
        }
        static void Send(int peer, byte[] frame)
        {
            link.Send(peer,frame);
            metrics.Sent(frame.Length);
        }
        public static void Host(bool steam)
        {
            if (link != null) return;
            try
            {
                if (global::Game.Instance == null || ClusterManager.Instance?.activeWorld == null)
                    throw new InvalidOperationException("Load a colony before hosting");
                var version = Fingerprint();
                colony = global::Game.Instance;
                link = Open();
                metrics = new SessionMetrics("host",Time());
                host = new RoomHost(version, Send, link.Drop, HostWorld.Capture, HostWorld.Execute, Time, CaptureNativeSave,HostWorld.CapturePage);
                host.CaptureMotion=NativeMotionStream.Capture;
                host.IsMotionPaused=()=>SpeedControlScreen.Instance.IsPaused;
                host.ExecuteForPeer = (peer,installation,action,x,y) => OrderOwnership.Execute(link.Account(peer),link.DisplayName(peer),peer,installation,action,x,y);
                host.NativeCaptureFailed += error => UnityEngine.Debug.LogWarning("[QueyangCoop] Failed to capture colony: " + error.Message);
                link.Host(steam);
                OrderOwnership.SetName(1,link.DisplayName(0));
                notice = "Hosting";
            }
            catch (Exception e) { Close(e.Message); }
        }

        static Stream CaptureNativeSave()
        {
            var dir = Path.Combine(Path.GetTempPath(), "QueyangCoop", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "Baseline.sav");
            var crashPath = KCrashReporter.MOST_RECENT_SAVEFILE;
            try
            {
                CapturingJoinSave = true;
                var written = SaveLoader.Instance.Save(path, false, false);
                if (string.IsNullOrEmpty(written) || !string.Equals(Path.GetFullPath(written), path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Host could not create the multiplayer save copy");
                return new SnapshotFile(path, dir);
            }
            catch { RemoveSessionDirectory(dir); throw; }
            finally { KCrashReporter.MOST_RECENT_SAVEFILE = crashPath; CapturingJoinSave = false; }
        }

        sealed class SnapshotFile : FileStream
        {
            readonly string directory;
            public SnapshotFile(string path, string directory) : base(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            { this.directory = directory; }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && Directory.Exists(directory)) RemoveSessionDirectory(directory);
            }
        }

        static void RemoveSessionDirectory(string directory)
        {
            if (directory == null) return;
            try { Directory.Delete(directory, true); }
            catch (IOException error) { UnityEngine.Debug.LogWarning("[QueyangCoop] Temporary files: " + error.Message); }
            catch (UnauthorizedAccessException error) { UnityEngine.Debug.LogWarning("[QueyangCoop] Temporary files: " + error.Message); }
        }

        public static void Join(string endpoint, bool steam) => Join(endpoint, steam, true);
        internal static void JoinDiagnostic(string endpoint, bool steam) => Join(endpoint, steam, false);
        internal static void CleanupClosedReplica()
        {
            if (closedReplica == null || (NativeGuestScene.Active && !NativeGuestScene.ApplicationQuitting)) return;
            RemoveSessionDirectory(closedReplica);
            closedReplica = null;
        }

        static void Join(string endpoint, bool steam, bool native)
        {
            if (link != null || NativeGuestScene.Active) return;
            CleanupClosedReplica();
            try
            {
                if (global::Game.Instance != null) throw new InvalidOperationException("Join from the main menu");
                var version = Fingerprint();
                link = Open();
                metrics = new SessionMetrics("guest",Time());
                nativeClient = native;
                if (native) replicaDirectory = Path.Combine(Path.GetTempPath(), "QueyangCoop", Guid.NewGuid().ToString("N"));
                client = new RoomClient(version, data => Send(0, data), replicaDirectory,
                    native ? (Action<string>)NativeGuestScene.Load : null);
                if (!steam) client.Installation = OrderOwnership.LanInstallation();
                client.CommandLatencyMs += metrics.RecordCommand;
                client.ViewReceived += bytes => {
                    if (!NativeGuestScene.Ready || !nativeClient) return;
                    ApplyNativeTile(ColonyView.Decode(bytes),true);
                    GuestDiagnostics.AppliedView();
                };
                client.MotionReceived+=bytes=>{ if(NativeGuestScene.Ready) NativeMotionStream.Apply(bytes); };
                client.PeripheralReceived += bytes => {
                    if (NativeGuestScene.Ready) ApplyNativeTile(ColonyView.Decode(bytes),false);
                };
                client.ResultReceived += result => CommandResult?.Invoke(result);
                nextPing = lastIncoming = joinTime = Time();
                GuestDiagnostics.Start();
                link.Join(endpoint, steam);
            }
            catch (Exception e) { Close(e.Message); }
        }
        public static void Invite()
        {
            try { link?.Invite(); } catch (Exception e) { notice = e.Message; }
        }
        public static void Disconnect() => Close("Offline");
        internal static void Disconnect(string reason) => Close(reason);
        static void Close(string reason)
        {
            var old = link;
            if (nativeClient) NativeGuestScene.End();
            NativeWorldMirror.Reset();
            NativeToolCommands.Reset();
            OrderOwnership.Reset();
            NativeManagement.Reset();
            if (client != null) GuestDiagnostics.End();
            client?.Dispose();
            host?.Dispose();
            if (replicaDirectory != null) closedReplica = replicaDirectory;
            CleanupClosedReplica();
            replicaDirectory = null; nativeClient = sceneAnnounced = false;
            NativeView = null;
            link = null; host = null; client = null; colony = null; closing = null;
            old?.Dispose();
            if (old != null) UnityEngine.Application.runInBackground = previousBackgroundMode;
            metrics = null;
            notice = reason;
        }
        public static void Subscribe(int x, int y) => WithClient(c => c.Subscribe(x, y));
        internal static bool RequestPeripheral(int x,int y) => client?.RequestPeripheral(x,y) == true;
        static void ApplyNativeTile(ColonyView tile, bool center)
        {
            if (!tile.Native) throw new InvalidDataException("Expected native world state");
            NativeWorldMirror.Apply(tile);
            NativeToolCommands.Observe(tile);
            OrderOwnership.Apply(tile);
            if (center) NativeView = tile;
        }
        public static void Command(string action, int x, int y) => WithClient(c => c.Command(action, x, y));
        static void WithClient(Action<RoomClient> operation)
        {
            if (client == null) return;
            try { operation(client); } catch (Exception e) { Close(e.Message); }
        }
        static void Connected(int peer)
        {
            if (host != null) host.Connect(peer);
            else client?.Connected();
        }
        static void Disconnected(int peer)
        {
            if (host != null) host.Remove(peer);
            else closing = string.IsNullOrEmpty(link?.LastError) ? "Host disconnected" : link.LastError;
        }
        static void Receive(int peer, byte[] frame)
        {
            metrics?.Received(frame.Length);
            try
            {
                if (host != null) host.Receive(peer, frame);
                else if (client != null)
                {
                    client.Receive(frame);
                    lastIncoming = Time();
                    if (client.Rejected) closing = client.Status;
                }
            }
            catch (Exception e)
            {
                if (host == null) { closing = e.Message; return; }
                host.Remove(peer); link.Drop(peer);
                notice = "Guest disconnected: " + e.Message;
            }
        }
        public static void Pump()
        {
            if (link == null) return;
            var start = Stopwatch.GetTimestamp();
            try
            {
                if (host != null && (colony == null || colony != global::Game.Instance))
                { Close("Colony closed"); return; }
                if (client != null && global::Game.Instance != null && !NativeGuestScene.Active)
                { Close("Left main menu"); return; }
                link.Poll();
                if (closing != null) { Close(closing); return; }
                host?.Tick();
                if (client?.Live == true) joinTime = Time();
                if (client == null) return;
                if (nativeClient)
                {
                    if (NativeGuestScene.Ready && !sceneAnnounced)
                    {
                        client.NativeWorldReady();
                        sceneAnnounced = true;
                        lastIncoming = joinTime = Time();
                    }
                    if (sceneAnnounced && client.Live)
                    {
                        NativeToolCommands.Tick();
                        NativeManagement.Tick();
                    }
                }
                var time = Time();
                if (time >= nextPing)
                {
                    client.Ping(Clock.ElapsedMilliseconds);
                    nextPing = time + 3;
                }
                if (time - lastIncoming > (nativeClient && !sceneAnnounced ? 180 : 30) ||
                    (!client.Live && time - joinTime > (nativeClient ? 360 : 60))) Close("Host timed out");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[QueyangCoop] " + e);
                Close(e.Message);
            }
            finally { metrics?.Tick(start,Time()); }
        }
    }
}
