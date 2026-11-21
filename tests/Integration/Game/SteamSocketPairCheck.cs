using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PacketType = Queyang.Core.Message;
using Steamworks;

namespace Queyang.Integration
{
    // Drives one request/reply over two real SteamNetworkingSockets handles. Poll once per Unity frame.
    internal sealed class SteamSocketPairCheck : IDisposable
    {
        private readonly Action<string> log;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly byte[] session = Guid.NewGuid().ToByteArray();
        private readonly long stamp = Stopwatch.GetTimestamp();
        private readonly IntPtr[] received = new IntPtr[1];
        private HSteamNetConnection first, second;
        private bool replied;
        private const int PeerId = 1;

        internal SteamSocketPairCheck(Action<string> log)
        {
            this.log = log;
            try
            {
                bool networkLoopback = Open(true);
                if (!networkLoopback)
                {
                    Dispose();
                    if (!Open(false)) throw new IOException("Steam CreateSocketPair failed in both loopback modes.");
                }
                log("STEAM_PAIR_OPEN nativeNetworkLoopback=" + networkLoopback);
                Send(first, PacketType.Ping);
            }
            catch { Dispose(); throw; }
        }

        private bool Open(bool networkLoopback)
        {
            var local1 = new SteamNetworkingIdentity();
            var local2 = new SteamNetworkingIdentity();
            local1.SetLocalHost();
            local2.SetLocalHost();
            return SteamNetworkingSockets.CreateSocketPair(out first, out second, networkLoopback, ref local1, ref local2);
        }

        private void Send(HSteamNetConnection from, PacketType type)
        {
            byte[] frame = Queyang.Core.Wire.Pack(type, w => { w.Write(session); w.Write(PeerId); w.Write(stamp); });
            var pin = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try
            {
                var result = SteamNetworkingSockets.SendMessageToConnection(from, pin.AddrOfPinnedObject(), (uint)frame.Length,
                    Steamworks.Constants.k_nSteamNetworkingSend_ReliableNoNagle, out _);
                if (result != EResult.k_EResultOK) throw new IOException("Steam socket pair send: " + result);
            }
            finally { pin.Free(); }
        }

        internal bool Poll()
        {
            if (clock.Elapsed.TotalSeconds > 10) throw new TimeoutException("Steam socket pair round trip timed out.");
            var receiver = replied ? first : second;
            var expected = replied ? PacketType.Pong : PacketType.Ping;
            int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(receiver, received, 1);
            if (count < 0) throw new IOException("Invalid Steam socket pair receive handle.");
            if (count == 0) return false;
            try
            {
                var native = SteamNetworkingMessage_t.FromIntPtr(received[0]);
                const int frameBytes = 16 + 16 + 4 + 8; // header, room ID, peer ID, timestamp
                if (native.m_cbSize != frameBytes) throw new InvalidDataException("Unexpected Steam frame byte count.");
                var frame = new byte[frameBytes];
                Marshal.Copy(native.m_pData, frame, 0, frame.Length);
                Queyang.Core.Wire.Read(frame, (type,r) => {
                    if (type != expected || !r.ReadBytes(16).SequenceEqual(session) ||
                        r.ReadInt32() != PeerId || r.ReadInt64() != stamp)
                        throw new InvalidDataException("Steam altered or misrouted the QYMP frame.");
                });
                log("STEAM_PAIR_RECEIVED " + expected + " bytes=" + frame.Length);
            }
            finally { SteamNetworkingMessage_t.Release(received[0]); }
            if (replied) return true;
            Send(second, PacketType.Pong);
            replied = true;
            return false;
        }

        public void Dispose()
        {
            try { Close(ref first); } finally { Close(ref second); }
        }

        private static void Close(ref HSteamNetConnection socket)
        {
            var previous = socket;
            socket = HSteamNetConnection.Invalid;
            if (previous != HSteamNetConnection.Invalid)
                SteamNetworkingSockets.CloseConnection(previous, 0, "QA finished", false);
        }
    }
}
