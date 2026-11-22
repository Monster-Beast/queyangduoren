using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;

namespace Queyang.Game
{
    internal sealed class SteamBackend : ConnectionBackend
    {
        private const int VirtualPort = 24714;
        private const string RoomKey = "queyang_coop", RoomValue = "1";
        // JoinLobby completes asynchronously even if its caller has returned to the menu.
        // Track ownership so a late completion cannot eject a newer session from the same room.
        private static readonly Dictionary<CSteamID,SteamBackend> Memberships = new Dictionary<CSteamID,SteamBackend>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private sealed class Peer
        {
            internal int Id;
            internal HSteamNetConnection Handle;
            internal CSteamID Account;
            internal bool Ready, Announced;
            internal string Failure;
            internal long Created;
        }
        private readonly Dictionary<int, Peer> peers = new Dictionary<int, Peer>();
        private readonly Dictionary<HSteamNetConnection, Peer> handles = new Dictionary<HSteamNetConnection, Peer>();
        private readonly IntPtr[] slot = new IntPtr[1];
        private Callback<SteamNetConnectionStatusChangedCallback_t> stateCallback;
        private CallResult<LobbyCreated_t> created;
        private CallResult<LobbyEnter_t> entered;
        private HSteamListenSocket listener;
        private CSteamID lobby, ownerAccount;
        private bool hosting, ready, disposed;
        private string fatalError;
        private long lastMembershipCheck;
        private int nextPeer = 1;

        internal SteamBackend(CoopTransport owner) : base(owner) { }

        internal override string DisplayName(int id) => id==0 ? SteamFriends.GetPersonaName() :
            peers.TryGetValue(id,out var peer) && peer.Ready ? SteamFriends.GetFriendPersonaName(peer.Account) : null;
        internal override string Account(int id) => peers.TryGetValue(id,out var peer) && peer.Ready ? "steam/" + peer.Account.m_SteamID : null;

        internal override void Start(bool host, string endpoint)
        {
            hosting = host;
            if (!SteamUser.BLoggedOn()) throw new InvalidOperationException("Steam must be online.");
            SteamNetworkingUtils.InitRelayNetworkAccess();
            stateCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnState);
            SteamAPICall_t call;
            if (host)
            {
                created = CallResult<LobbyCreated_t>.Create(OnCreated);
                call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, CoopTransport.MaxGuests + 1);
                created.Set(call);
            }
            else
            {
                ulong id;
                if (!ulong.TryParse(endpoint, out id) || !new CSteamID(id).IsLobby())
                    throw new ArgumentException("Steam join requires a lobby ID from an invite.");
                lobby = new CSteamID(id);
                Memberships[lobby] = this;
                entered = CallResult<LobbyEnter_t>.Create(OnEntered);
                call = SteamMatchmaking.JoinLobby(lobby);
                entered.Set(call);
            }
            if (call == SteamAPICall_t.Invalid) throw new IOException("Steam rejected the lobby request.");
        }

        private void OnCreated(LobbyCreated_t result, bool failed)
        {
            if (disposed)
            {
                if (!failed && result.m_eResult == EResult.k_EResultOK)
                    SteamMatchmaking.LeaveLobby(new CSteamID(result.m_ulSteamIDLobby));
                created?.Dispose();
                return;
            }
            if (failed || result.m_eResult != EResult.k_EResultOK)
            {
                fatalError = "CreateLobby: " + result.m_eResult;
                return;
            }
            lobby = new CSteamID(result.m_ulSteamIDLobby);
            Memberships[lobby] = this;
            ownerAccount = SteamUser.GetSteamID();
            listener = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
            if (listener == HSteamListenSocket.Invalid || !SteamMatchmaking.SetLobbyData(lobby, RoomKey, RoomValue))
            {
                fatalError = "Steam lobby listener or metadata setup failed.";
                return;
            }
            Owner.Endpoint = lobby.ToString();
            ready = true;
        }

        private void OnEntered(LobbyEnter_t result, bool failed)
        {
            var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
            if (disposed)
            {
                var joined = new CSteamID(result.m_ulSteamIDLobby);
                if (!failed && response == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess && !Memberships.ContainsKey(joined))
                    SteamMatchmaking.LeaveLobby(joined);
                entered?.Dispose();
                return;
            }
            if (failed || response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess ||
                result.m_ulSteamIDLobby != (ulong)lobby || SteamMatchmaking.GetLobbyData(lobby, RoomKey) != RoomValue)
            {
                fatalError = "Cannot enter a compatible co-op lobby: " + response;
                return;
            }
            ownerAccount = SteamMatchmaking.GetLobbyOwner(lobby);
            if (!ownerAccount.IsValid() || ownerAccount == SteamUser.GetSteamID())
            {
                fatalError = "Lobby no longer has a remote host.";
                return;
            }
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(ownerAccount);
            var connection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
            if (connection == HSteamNetConnection.Invalid) { fatalError = "Steam ConnectP2P failed."; return; }
            Add(0, connection, ownerAccount);
            Owner.Endpoint = lobby.ToString();
            ready = true;
        }

        private Peer Add(int id, HSteamNetConnection handle, CSteamID account)
        {
            var peer = new Peer { Id = id, Handle = handle, Account = account, Created = clock.ElapsedMilliseconds };
            peers.Add(id, peer);
            handles.Add(handle, peer);
            return peer;
        }

        private bool IsMember(CSteamID account)
        {
            int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
            for (int i = 0; i < count; ++i)
                if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == account) return true;
            return false;
        }

        private void OnState(SteamNetConnectionStatusChangedCallback_t change)
        {
            if (disposed) return;
            var state = change.m_info.m_eState;
            var handle = change.m_hConn;
            Peer peer;
            if (!handles.TryGetValue(handle, out peer))
            {
                // Steam dispatches callbacks for the entire process. Only touch our own listen socket.
                if (!hosting || !ready || change.m_info.m_hListenSocket != listener ||
                    state != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting) return;
                var account = change.m_info.m_identityRemote.GetSteamID();
                if (!account.IsValid() || account == ownerAccount || !IsMember(account))
                {
                    SteamNetworkingSockets.CloseConnection(handle, 0, "Not a room member", false);
                    return;
                }
                // A reconnect may arrive before a closed callback for the old socket.
                foreach (var current in new List<Peer>(peers.Values))
                    if (current.Account == account) Drop(current.Id);
                if (disposed) return;
                if (peers.Count >= CoopTransport.MaxGuests ||
                    SteamNetworkingSockets.AcceptConnection(handle) != EResult.k_EResultOK)
                {
                    SteamNetworkingSockets.CloseConnection(handle, 0, "Cannot accept connection", false);
                    return;
                }
                Add(checked(nextPeer++), handle, account);
                return;
            }
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                if (peer.Account != change.m_info.m_identityRemote.GetSteamID()) peer.Failure = "Steam identity mismatch.";
                else peer.Ready = true;
            }
            else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer ||
                     state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
                peer.Failure = "Steam disconnected: " + change.m_info.m_eEndReason;
        }

        internal override void Poll()
        {
            if (fatalError != null || (!ready && clock.ElapsedMilliseconds > 30000))
            {
                Owner.LastError = fatalError ?? "Lobby request timed out.";
                throw new IOException(Owner.LastError);
            }
            bool checkMembers = ready && clock.ElapsedMilliseconds - lastMembershipCheck > 2000;
            if (checkMembers)
            {
                lastMembershipCheck = clock.ElapsedMilliseconds;
                if (SteamMatchmaking.GetLobbyOwner(lobby) != ownerAccount)
                    throw new IOException("Steam lobby owner left.");
            }
            foreach (var peer in new List<Peer>(peers.Values))
            {
                if (!peers.ContainsKey(peer.Id)) continue;
                if (checkMembers && !IsMember(peer.Account)) peer.Failure = "Peer left the Steam lobby.";
                if (!peer.Ready && clock.ElapsedMilliseconds - peer.Created > 30000) peer.Failure = "P2P connection timed out.";
                if (peer.Failure != null)
                {
                    Owner.LastError = peer.Failure;
                    Drop(peer.Id);
                    continue;
                }
                if (!peer.Ready) continue;
                if (!peer.Announced)
                {
                    peer.Announced = true;
                    Owner.Connected(peer.Id);
                }
                for (int n = 0; n < 16 && peers.ContainsKey(peer.Id); n++)
                {
                    int count = SteamNetworkingSockets.ReceiveMessagesOnConnection(peer.Handle, slot, 1);
                    if (count <= 0)
                    {
                        if (count < 0) peer.Failure = "Steam receive failed.";
                        break;
                    }
                    byte[] packet;
                    try
                    {
                        var native = SteamNetworkingMessage_t.FromIntPtr(slot[0]);
                        if (native.m_cbSize < 1 || native.m_cbSize > CoopTransport.MaxFrame)
                        {
                            peer.Failure = "Invalid Steam packet size.";
                            break;
                        }
                        packet = new byte[native.m_cbSize];
                        Marshal.Copy(native.m_pData, packet, 0, packet.Length);
                    }
                    finally { SteamNetworkingMessage_t.Release(slot[0]); }
                    Owner.Message(peer.Id, packet);
                }
            }
        }

        internal override void Send(int id, byte[] packet)
        {
            Peer peer;
            if (!peers.TryGetValue(id, out peer) || !peer.Ready || peer.Failure != null) return;
            var pinned = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                long number;
                bool motion=packet.Length>=16 && BitConverter.ToUInt16(packet,8)==(ushort)Queyang.Core.Message.Motion;
                var result = SteamNetworkingSockets.SendMessageToConnection(peer.Handle, pinned.AddrOfPinnedObject(),
                    (uint)packet.Length, motion ? Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoDelay :
                    Steamworks.Constants.k_nSteamNetworkingSend_Reliable, out number);
                if(motion && (result==EResult.k_EResultIgnored || result==EResult.k_EResultLimitExceeded)) return;
                // Valve caps its send buffer and reports LimitExceeded on backpressure.
                if (result != EResult.k_EResultOK) peer.Failure = "Steam send failed: " + result;
            }
            finally { pinned.Free(); }
        }

        internal override void Invite()
        {
            if (!hosting || !ready) throw new InvalidOperationException("Steam host lobby is not ready.");
            SteamFriends.ActivateGameOverlayInviteDialog(lobby);
        }

        internal override void Drop(int id)
        {
            Peer peer;
            if (!peers.TryGetValue(id, out peer)) return;
            peers.Remove(id);
            handles.Remove(peer.Handle);
            SteamNetworkingSockets.CloseConnection(peer.Handle, 0, "Session closed", false);
            if (!disposed) Owner.Disconnected(id);
        }

        public override void Dispose()
        {
            disposed = true;
            stateCallback?.Dispose();
            if (entered != null && !entered.IsActive()) entered.Dispose();
            // A pending CreateLobby may still succeed after cancellation. Its callback leaves the room.
            if (created != null && !created.IsActive()) created.Dispose();
            foreach (var id in new List<int>(peers.Keys)) Drop(id);
            if (listener != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(listener);
            if (lobby != CSteamID.Nil && Memberships.TryGetValue(lobby,out var active) && active == this)
            {
                Memberships.Remove(lobby);
                SteamMatchmaking.LeaveLobby(lobby);
            }
        }
    }

    public static class SteamInvites
    {
        public static IDisposable Listen(Action<string> joinLobby)
        {
            if (joinLobby == null) throw new ArgumentNullException(nameof(joinLobby));
            if (!SteamUser.BLoggedOn()) return null;
            return Callback<GameLobbyJoinRequested_t>.Create(request => joinLobby(request.m_steamIDLobby.ToString()));
        }

        public static string LaunchLobby()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i + 1 < args.Length; ++i)
            {
                ulong id;
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out id) && new CSteamID(id).IsLobby())
                    return args[i + 1];
            }
            return null;
        }
    }
}
