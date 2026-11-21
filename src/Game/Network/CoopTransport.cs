using System;
using System.IO;

namespace Queyang.Game
{
    // All entry points, including callbacks, run on the game's main thread.
    public sealed class CoopTransport : IDisposable
    {
        internal const int MaxFrame = 64 * 1024;
        internal const int QueueBudget = 4 * 1024 * 1024;
        internal const int MaxGuests = 3;
        internal readonly Action<int, byte[]> Message;
        internal readonly Action<int> Connected;
        internal readonly Action<int> Disconnected;
        internal bool IsDisposed { get; private set; }
        private ConnectionBackend backend;
        public string Endpoint { get; internal set; } = "";
        public string LastError { get; internal set; } = "";

        public CoopTransport(Action<int, byte[]> message, Action<int> connected, Action<int> disconnected)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Connected = connected ?? throw new ArgumentNullException(nameof(connected));
            Disconnected = disconnected ?? throw new ArgumentNullException(nameof(disconnected));
        }

        public void Host(bool steam) => Start(true, steam, "");
        public void Join(string endpoint, bool steam) => Start(false, steam, endpoint);

        private void Start(bool hosting, bool steam, string endpoint)
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(CoopTransport));
            if (backend != null) throw new InvalidOperationException("Transport already started.");
            backend = steam ? (ConnectionBackend)new SteamBackend(this) : new LanBackend(this);
            try { backend.Start(hosting, endpoint); }
            catch { Dispose(); throw; }
        }

        public void Poll() => backend?.Poll();
        public void Drop(int peer) => backend?.Drop(peer);
        public void Invite()
        {
            if (backend == null) throw new InvalidOperationException("No active lobby.");
            backend.Invite();
        }

        public void Send(int peer, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxFrame)
                throw new InvalidDataException("Packet length is outside 1..65536.");
            if (backend == null) throw new InvalidOperationException("Transport is offline.");
            backend.Send(peer, data);
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            var previous = backend;
            backend = null;
            previous?.Dispose();
        }
    }

    internal abstract class ConnectionBackend : IDisposable
    {
        protected readonly CoopTransport Owner;
        protected ConnectionBackend(CoopTransport owner) { Owner = owner; }
        internal abstract void Start(bool hosting, string endpoint);
        internal abstract void Poll();
        internal abstract void Send(int peer, byte[] packet);
        internal abstract void Drop(int peer);
        internal virtual void Invite() { throw new InvalidOperationException("Steam lobby required."); }
        public abstract void Dispose();
    }
}
