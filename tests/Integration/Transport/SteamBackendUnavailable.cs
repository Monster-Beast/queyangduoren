using System;

namespace Queyang.Game
{
    // These desktop CLR fixtures run real LAN sockets. Steam sockets are tested inside Unity.
    internal sealed class SteamBackend : ConnectionBackend
    {
        internal SteamBackend(CoopTransport owner) : base(owner) =>
            throw new NotSupportedException("Run the Steam lobby fixture inside the game.");
        internal override void Start(bool hosting,string endpoint) => throw new NotSupportedException();
        internal override void Poll() => throw new NotSupportedException();
        internal override void Send(int peer,byte[] packet) => throw new NotSupportedException();
        internal override void Drop(int peer) => throw new NotSupportedException();
        public override void Dispose() { }
    }
}
