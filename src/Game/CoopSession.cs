using System;
using System.Diagnostics;
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
        static double joinTime, lastIncoming, nextPing;
        public static bool IsHosting => host != null;
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
                host = new RoomHost(version, Send, link.Drop, HostWorld.Capture, HostWorld.Execute, Time);
                link.Host(steam);
                notice = "Hosting";
            }
            catch (Exception e) { Close(e.Message); }
        }

        public static void Join(string endpoint, bool steam)
        {
            if (link != null) return;
            try
            {
                if (global::Game.Instance != null) throw new InvalidOperationException("Join from the main menu");
                var version = Fingerprint();
                link = Open();
                metrics = new SessionMetrics("guest",Time());
                client = new RoomClient(version, data => Send(0, data));
                client.CommandLatencyMs += metrics.RecordCommand;
                client.ResultReceived += result => CommandResult?.Invoke(result);
                nextPing = lastIncoming = joinTime = Time();
                link.Join(endpoint, steam);
            }
            catch (Exception e) { Close(e.Message); }
        }
        public static void Invite()
        {
            try { link?.Invite(); } catch (Exception e) { notice = e.Message; }
        }
        public static void Disconnect() => Close("Offline");
        static void Close(string reason)
        {
            var old = link;
            link = null; host = null; client = null; colony = null; closing = null;
            old?.Dispose();
            if (old != null) UnityEngine.Application.runInBackground = previousBackgroundMode;
            metrics = null;
            notice = reason;
        }
        public static void Subscribe(int x, int y) => WithClient(c => c.Subscribe(x, y));
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
                if (client != null && global::Game.Instance != null)
                { Close("Left main menu"); return; }
                link.Poll();
                if (closing != null) { Close(closing); return; }
                host?.Tick();
                if (client?.Live == true) joinTime = Time();
                if (client == null) return;
                var time = Time();
                if (time >= nextPing)
                {
                    client.Ping(Clock.ElapsedMilliseconds);
                    nextPing = time + 3;
                }
                if (time - lastIncoming > 30 || (!client.Live && time - joinTime > 60)) Close("Host timed out");
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
