using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Loaded only in the disposable Unity guest. Both accounts exchange coordination
    // through their real Steam lobby. Each keeps its own report, saves and framebuffer.
    internal sealed class SteamDualGuest
    {
        enum Phase { Menu, Invite, Loading, Colony, Dig, Excavation, Revisit, HostTarget, HostOrder, Screenshot, Ack, Leaving, Done }
        static SteamDualGuest run;
        readonly SteamMatchedView matched=new SteamMatchedView();
        readonly SteamArrivalFrame arrival = new SteamArrivalFrame();
        readonly SteamDualPerson person=new SteamDualPerson();
        readonly SteamDualSkills skills=new SteamDualSkills();
        readonly SteamDualWork work=new SteamDualWork();
        readonly SteamRoomControls roomControls=new SteamRoomControls();
        readonly SteamDualBuildings buildings=new SteamDualBuildings();
        readonly FacilityRangeWitness facilityRange=new FacilityRangeWitness();
        readonly SteamDualFallingOre fallingOre=new SteamDualFallingOre();
        readonly SteamDualBuriedItems buriedItems=new SteamDualBuriedItems();
        readonly SteamDualLighting lighting=new SteamDualLighting();
        readonly SteamDualHealth health=new SteamDualHealth();
        readonly SteamConstructionRange constructionRange=new SteamConstructionRange();
        readonly SteamFloorRange floorRange=new SteamFloorRange();
        readonly SteamAreaDigRange areaDig=new SteamAreaDigRange();
        readonly SteamAlertRange alertRange=new SteamAlertRange();
        readonly SteamSweepSymbolRange sweepRange=new SteamSweepSymbolRange();
        readonly SteamResearchRange researchRange=new SteamResearchRange();
        readonly SteamPowerRange powerRange=new SteamPowerRange();
        readonly SteamBuildingMotionRange buildingMotion=new SteamBuildingMotionRange();
        readonly SteamResearchMotionRange researchMotion=new SteamResearchMotionRange();
        readonly SteamDupeFeedbackRange dupeFeedback=new SteamDupeFeedbackRange();
        readonly SteamUpgradeVisualRange upgradeVisual=new SteamUpgradeVisualRange();
        readonly SteamPrintingRange printing=new SteamPrintingRange();
        readonly SteamMeterRange batteryRange=new SteamMeterRange(), reservoirRange=new SteamMeterRange(true);
        readonly SteamDualOwnership ownership=new SteamDualOwnership();
        readonly Action<string> log;
        readonly FixtureDialogs dialogs;
        readonly string folder;
        readonly CSteamID self, host;
        readonly int inviteWait;
        readonly bool acceptDelivered;
        readonly bool colonyJoin=Env("QY_CHECK_COLONY_JOIN")=="1";
        Callback<LobbyInvite_t> delivered;
        Callback<GameLobbyJoinRequested_t> requested;
        CSteamID lobby;
        Phase phase;
        int round=1, x, y, loaded;
        int hostX,hostY;
        float deadline, nextDialog, inviteDeadline;
        string method, baselinePath, previousPath, firstHash, fail;
        bool gotInvite, gotRequest, accepted, sawMarker, checkedBaseline;
        FixtureFrame frame, markerFrame;
        readonly ResourceHudCheck resourceHud;
        SteamMotionTrace motion;
        bool motionCompleted;

        internal SteamDualGuest(Action<string> log)
        {
            this.log=log; dialogs=new FixtureDialogs(log);
            folder=Path.GetDirectoryName(Path.GetFullPath(Env("QY_SMOKE_REPORT")));
            markerFrame=new FixtureFrame(Path.Combine(folder,"dual-guest-dig.png"));
            resourceHud=new ResourceHudCheck(Path.Combine(folder,"dual-guest-resources.png"));
            if (!string.Equals(Util.GetKleiRootPath(),Env("QY_SMOKE_ROOT"),StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Dual guest requires the isolated ONI profile.");
            self=ReadId(Env("QY_DUAL_SELF"));
            host=ReadId(Env("QY_DUAL_HOST_ID") ?? Env("QY_DUAL_FRIEND"));
            if (!self.IsValid() || !host.IsValid() || self==host) throw new InvalidOperationException("Two distinct expected Steam users are required.");
            if (!string.IsNullOrEmpty(Env("QY_DUAL_LOBBY")))
            {
                lobby=ReadId(Env("QY_DUAL_LOBBY"));
                if (!lobby.IsLobby()) throw new ArgumentException("QY_DUAL_LOBBY must identify a Steam lobby.");
            }
            if (!int.TryParse(Env("QY_DUAL_WAIT_INVITE_SECONDS") ?? Env("QY_DUAL_INVITE_WAIT") ?? "90",out inviteWait) ||
                inviteWait<0 || inviteWait>600) throw new ArgumentException("Invite wait must be 0..600 seconds.");
            acceptDelivered=Env("QY_DUAL_ACCEPT_INVITE")=="1";
            Application.runInBackground=true;
            run=this;
            Application.logMessageReceived+=EngineLog;
            CoopSession.CommandResult+=Result;
            Set(Phase.Menu,150);
        }
        static string Env(string key) => Environment.GetEnvironmentVariable(key);
        static CSteamID ReadId(string text) => new CSteamID(ulong.Parse(text,NumberStyles.None,CultureInfo.InvariantCulture));
        bool Match(CSteamID room) => room.IsLobby() && (!lobby.IsValid() || lobby==room);
        static bool AtMenu => global::Game.Instance==null && MainMenu.Instance!=null && MainMenu.Instance.IsActive();
        int Cell => Grid.XYToCell(x,y);
        void Set(Phase next,float seconds) { phase=next; deadline=Time.realtimeSinceStartup+seconds; }
        void EngineLog(string message,string stack,LogType type)
        { if (type==LogType.Exception && fail==null) fail=message+"\n"+stack; }
        void Result(string answer)
        {
            if (phase!=Phase.Excavation) return;
            if (answer=="OK Dig designated") { accepted=true; log("DUAL_GUEST_DIG_ACK x="+x+" y="+y+" reply="+answer); }
        }
        void OnInvite(LobbyInvite_t notice)
        {
            var room=new CSteamID(notice.m_ulSteamIDLobby);
            if (phase!=Phase.Invite || notice.m_ulSteamIDUser!=(ulong)host || !Match(room)) return;
            gotInvite=true; lobby=room;
            log("DUAL_INVITE_RECEIVED round="+round+" lobby="+room+" source=Steam.LobbyInvite_t");
            if (!acceptDelivered) inviteDeadline=Math.Max(inviteDeadline,Time.realtimeSinceStartup+inviteWait);
        }
        void OnRequest(GameLobbyJoinRequested_t notice)
        {
            if (phase!=Phase.Invite || !Match(notice.m_steamIDLobby) ||
                (notice.m_steamIDFriend.IsValid() && notice.m_steamIDFriend!=host)) return;
            lobby=notice.m_steamIDLobby; gotRequest=true;
            log("DUAL_JOIN_REQUESTED round="+round+" lobby="+lobby+" source=Steam.GameLobbyJoinRequested_t");
        }
        void Advertise()
        {
            if (!SteamUser.BLoggedOn()) return;
            if (SteamUser.GetSteamID()!=self) throw new InvalidOperationException("Active Steam user differs from expected guest.");
            var app=new AppId_t(457140);
            if (SteamUtils.GetAppID()!=app || !SteamApps.BIsSubscribedApp(app) || SteamApps.GetAppOwner()!=self ||
                SteamApps.BIsSubscribedFromFamilySharing() || SteamApps.BIsSubscribedFromFreeWeekend())
                throw new InvalidOperationException("Independent Steam ownership of ONI could not be verified for the guest.");
            if (SteamFriends.GetFriendRelationship(host)!=EFriendRelationship.k_EFriendRelationshipFriend)
                throw new InvalidOperationException("Expected Steam host is not the authenticated guest's friend.");
            if (round==1) log("DUAL_GUEST_ACCOUNT configuredSelf=True distinctFriend=True ownsApp=True sharing=False");
            if (delivered==null) delivered=Callback<LobbyInvite_t>.Create(OnInvite);
            if (requested==null) requested=Callback<GameLobbyJoinRequested_t>.Create(OnRequest);
            gotInvite=gotRequest=accepted=sawMarker=checkedBaseline=false; loaded=0; baselinePath=method=null;
            if (!SteamFriends.SetRichPresence("qy_dual_ready",round.ToString(CultureInfo.InvariantCulture)))
                throw new IOException("Steam rejected guest ready presence.");
            Set(Phase.Invite,2*inviteWait+90); inviteDeadline=Time.realtimeSinceStartup+inviteWait;
            log("DUAL_GUEST_LISTEN round="+round+" self="+self+" expectedHost="+host+" waitSeconds="+inviteWait);
        }
        void Join()
        {
            if (!CoopSession.IsClient)
            {
                // The production CoopPanel owns genuine JoinRequested and +connect_lobby.
                // The fixture observes those paths. A raw notification alone has no join semantics.
                if (gotRequest && Time.realtimeSinceStartup<inviteDeadline) return;
                if (acceptDelivered && gotInvite) method="fixture-accepted-delivered-invite";
                else if (Time.realtimeSinceStartup>=inviteDeadline && lobby.IsValid())
                    method=gotRequest ? "lobby-number-after-unhandled-join-request" : "lobby-number";
                else return;
                CoopSession.Join(lobby.ToString(),true);
                if (!CoopSession.IsNativeClient) throw new IOException("Steam join failed: "+CoopSession.Status);
            }
            if (method==null)
            {
                if (gotRequest) method="steam-join-requested-callback";
                else
                {
                    string launch=SteamInvites.LaunchLobby();
                    if (round!=1 || string.IsNullOrEmpty(launch) || !Match(ReadId(launch))) return;
                    lobby=ReadId(launch); method="steam-launch-lobby";
                }
            }
            SteamFriends.SetRichPresence("qy_dual_ready",null);
            log("DUAL_GUEST_JOIN round="+round+" method="+method+" delivered="+gotInvite+" joinRequested="+gotRequest+" lobby="+lobby);
            Set(Phase.Loading,180);
        }
        [HarmonyPatch(typeof(SaveLoader),"Load",typeof(string))]
        static class LoadWitness
        {
            static void Postfix(string __0,bool __result)
            {
                var g=run;
                if (g==null || !CoopSession.IsNativeClient) return;
                if (!__result) { g.fail="Vanilla SaveLoader rejected the transferred baseline"; return; }
                g.loaded++; g.baselinePath=Path.GetFullPath(__0);
            }
        }
        [HarmonyPatch(typeof(NativeWorldMirror),"Apply")]
        static class RejoinBeforeFirstViewport
        {
            static void Prefix()
            {
                var g=run;
                if (g==null || g.colonyJoin || g.round!=2 || g.checkedBaseline || g.loaded!=1 || !NativeGuestScene.Ready) return;
                g.checkedBaseline=true;
                g.skills.Baseline(g.lobby,g.log);
                g.person.Baseline(g.lobby,g.log);
                if (!Grid.IsValidCell(g.Cell) || Grid.Solid[g.Cell]) g.fail="Previously excavated cell remained solid in the fresh ONI baseline.";
                else g.log("DUAL_GUEST_BASELINE_READBACK x="+g.x+" y="+g.y+" solid=False beforeFirstViewport=True");
            }
        }
        void WorldReady()
        {
            if (!NativeGuestScene.Ready || CoopSession.NativeView==null || CameraController.Instance==null || !global::Game.Instance.GameStarted()) return;
            if (CoopSession.Endpoint!=lobby.ToString() || SteamMatchmaking.GetLobbyOwner(lobby)!=host ||
                !Enumerable.Range(0,SteamMatchmaking.GetNumLobbyMembers(lobby)).Any(i=>SteamMatchmaking.GetLobbyMemberByIndex(lobby,i)==self))
                throw new InvalidDataException("Steam lobby ownership or membership did not match the two expected accounts.");
            if (!arrival.Capture(lobby,host,round,true,log)) return;
            if (loaded!=1 || baselinePath==previousPath || !File.Exists(baselinePath)) throw new InvalidDataException("Expected one fresh verified ONI save per join.");
            string hash;
            using(var file=File.OpenRead(baselinePath))
            using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");
            if (!colonyJoin && round==2 && hash==firstHash) throw new InvalidDataException("Steam rejoin reused the initial pre-excavation baseline.");
            if (round==1) firstHash=hash;
            log("DUAL_GUEST_BASELINE round="+round+" bytes="+new FileInfo(baselinePath).Length+" sha256="+hash+" owner="+host+" self="+self);
            if (colonyJoin)
            {
                string signature=ColonyRangeSurvey.BuildingSignature();
                if (signature!=SteamMatchmaking.GetLobbyData(lobby,"qy_colony_buildings"))
                    throw new InvalidDataException("Guest colony building IDs differ from the host baseline: "+signature);
                SteamMatchmaking.SetLobbyMemberData(lobby,"qy_colony_verified",round.ToString());
                log("COLONY_JOIN role=guest round="+round+" buildings="+signature);
                Set(Phase.Colony,55);
                return;
            }
            frame=new FixtureFrame(Path.Combine(folder,"dual-guest-"+round+".png"));
            // This phase includes two-person management edits and paired captures
            // at eight independent hat transitions before either player can dig.
            if (round==1) Set(Phase.Dig,Environment.GetEnvironmentVariable("QY_BASELINE_ONLY")=="1"?180:420);
            else { Focus(x,y); Set(Phase.Revisit,120); }
        }
        static void Focus(int tileX,int tileY)
        {
            var camera=CameraController.Instance; var p=camera.transform.position;
            camera.SetTargetPos(new Vector3(tileX+.5f,tileY+.5f,p.z),camera.OrthographicSize,false);
        }
        void Dig()
        {
            if (DigTool.Instance==null || SelectTool.Instance==null ||
                !int.TryParse(SteamMatchmaking.GetLobbyData(lobby,"qy_dual_dig_x"),out x) ||
                !int.TryParse(SteamMatchmaking.GetLobbyData(lobby,"qy_dual_dig_y"),out y)) return;
            if (x<0 || y<0 || x>=Grid.WidthInCells || y>=Grid.HeightInCells) throw new InvalidDataException("Invalid host dig coordinate");
            Focus(x,y);
            if (!Sample(x,y,out var tile)) return;
            int cell=Cell;
            if ((tile.Flags&63)!=3 || !Grid.Solid[cell] || !Diggable.IsDiggable(cell) ||
                Diggable.Undiggable(Grid.Element[cell]) || Diggable.GetDiggable(cell)!=null || NativeWorldMirror.IsDigVisible(cell))
                throw new InvalidDataException("Host reachability candidate failed guest pre-order grid checks at "+x+","+y);
            Set(Phase.Excavation,210);
            PlayerController.Instance.ActivateTool(DigTool.Instance);
            Vector3 p=Grid.CellToPosCCC(cell,Grid.SceneLayer.Move);
            DigTool.Instance.OnLeftClickDown(p); DigTool.Instance.OnLeftClickUp(p);
            if (Diggable.GetDiggable(cell)!=null) throw new InvalidOperationException("The guest created its own digging job.");
            log("DUAL_GUEST_DIG x="+x+" y="+y+" method=vanilla-drag");
        }
        static bool Sample(int tileX,int tileY,out ColonyCell sample)
        {
            var v=CoopSession.NativeView; sample=default;
            if (v==null || tileX<v.X || tileY<v.Y || tileX>=v.X+ColonyView.Width || tileY>=v.Y+ColonyView.Height) return false;
            sample=v.Cells[(tileY-v.Y)*ColonyView.Width+tileX-v.X]; return (sample.Flags&1)!=0;
        }
        void Snapshot()
        {
            var dupe=Components.LiveMinionIdentities.Items.Where(d=>d!=null && ClusterUtil.GetMyWorldId(d)==ClusterManager.Instance.activeWorldId)
                .OrderBy(d=>Grid.GetCellDistance(Grid.PosToCell(d),Cell)).First();
            PlayerController.Instance.ActivateTool(SelectTool.Instance);
            SelectTool.Instance.Select(dupe.GetComponent<KSelectable>(),false);
            if (DetailsScreen.Instance.target!=dupe.gameObject) throw new InvalidDataException("Native duplicant details failed to open.");
            Set(Phase.Screenshot,30);
        }
        void Clean()
        {
            run=null; Application.logMessageReceived-=EngineLog; CoopSession.CommandResult-=Result;
            if (delivered!=null) SteamFriends.SetRichPresence("qy_dual_ready",null);
            delivered?.Dispose(); requested?.Dispose();
        }
        internal void Tick()
        {
            if (phase==Phase.Done || dialogs.Restarting) return;
            try { Advance(); }
            catch
            {
                phase=Phase.Done; Clean();
                if (CoopSession.IsClient) CoopSession.Disconnect();
                throw;
            }
        }
        void Advance()
        {
            if (fail!=null) throw new InvalidOperationException(fail);
            if (Time.realtimeSinceStartup>deadline) throw new TimeoutException("Dual guest round="+round+" phase="+phase+" digAck="+accepted+" marker="+sawMarker+" status="+CoopSession.Status);
            if (Time.realtimeSinceStartup>=nextDialog)
            {
                nextDialog=Time.realtimeSinceStartup+.5f;
                if (dialogs.Check()) return;
            }
            if (phase>=Phase.Loading && phase<=Phase.Ack && !CoopSession.IsNativeClient) throw new IOException("Dual guest disconnected: "+CoopSession.Status);
            switch(phase)
            {
                case Phase.Menu: if (AtMenu || CoopSession.IsNativeClient) Advertise(); break;
                case Phase.Invite: Join(); break;
                case Phase.Loading: WorldReady(); break;
                case Phase.Colony:
                    if (SteamMatchmaking.GetLobbyData(lobby,"qy_colony_ack")!=round.ToString()) break;
                    CoopSession.Disconnect(); Set(Phase.Leaving,90); break;
                case Phase.Dig:
                    if (!facilityRange.Check(lobby,self,false,log)) break;
                    if (!alertRange.Check(lobby,self,false,round,Grid.InvalidCell,log)) break;
                    if (!sweepRange.Check(lobby,self,false,round,log)) break;
                    if (!powerRange.Check(lobby,self,false,round,log)) break;
                    if (!researchRange.Check(lobby,self,false,round,log)) break;
                    if (!batteryRange.Check(lobby,self,false,round,log)) break;
                    if (!reservoirRange.Check(lobby,self,false,round,log)) break;
                    if (!ownership.Legacy(lobby,false,log)) break;
                    if (SteamMatchmaking.GetLobbyMemberData(lobby,self,"qy_legacy_checked")!="1")
                        SteamMatchmaking.SetLobbyMemberData(lobby,"qy_legacy_checked","1");
                    if (!researchMotion.Check(lobby,self,false,round,log)) break;
                    if (!buildingMotion.Check(lobby,self,false,round,log)) break;
                    if (!dupeFeedback.Check(lobby,self,false,round,log)) break;
                    if (!upgradeVisual.Check(lobby,self,round,false,log)) break;
                    if (!printing.Check(lobby,self,false,round,log)) break;
                    if (!floorRange.Check(lobby,self,false,round,log)) break;
                    if (!constructionRange.Check(lobby,self,false,round,log)) break;
                    if (!areaDig.Check(lobby,self,false,round,Grid.InvalidCell,log)) break;
                    if (!health.Check(lobby,self,false,round,log)) break;
                    if (!lighting.Check(lobby,self,false,round,log)) break;
                    if (!buriedItems.Check(lobby,self,false,round,log)) break;
                    if (!fallingOre.Check(lobby,self,false,round,log)) break;
                    if (!buildings.Check(lobby,self,false,round,log)) break;
                    if (!skills.Verify(lobby,round,false,log)) break;
                    if (!person.Guest(lobby,round,log)) break;
                    if (!work.Verify(lobby,round,false,log)) break;
                    if (!roomControls.Check(lobby,self,round,false,log)) break;
                    Dig(); break;
                case Phase.Excavation:
                    if(sawMarker && !motionCompleted)
                    {
                        if(!int.TryParse(SteamMatchmaking.GetLobbyData(lobby,"qy_motion_id"),out int id)) break;
                        if(motion==null)
                        {
                            if (SteamCritterMotion.Enabled && !int.TryParse(SteamMatchmaking.GetLobbyData(lobby,"qy_critter_id"),out _)) break;
                            motion=SteamMotionTrace.Create(folder,id,false,SteamCritterMotion.Enabled ? int.Parse(SteamMatchmaking.GetLobbyData(lobby,"qy_critter_id")) : 0); motion.Begin();
                            SteamMatchmaking.SetLobbyMemberData(lobby,"qy_motion_ready","1");
                        }
                        if(motion.Seconds<35) break;
                        motion.End(log,x,y); motionCompleted=true;
                        SteamMatchmaking.SetLobbyMemberData(lobby,"qy_motion_done","1");
                    }
                    if (!Sample(x,y,out var s)) break;
                    if (!sawMarker && (s.Flags&8)!=0 && NativeWorldMirror.IsDigVisible(Cell))
                    {
                        if (!markerFrame.Requested)
                        {
                            if (!ownership.Initial(Cell)) break;
                            if (!DigMarkerGeometry.Verify(lobby,1,Cell,log)) break;
                            markerFrame.Capture(); break;
                        }
                        if (!markerFrame.Complete) break;
                        SteamMatchmaking.SetLobbyMemberData(lobby,"qy_dual_dig_photo","1");
                        sawMarker=true; log("DUAL_GUEST_DIG_MARKER x="+x+" y="+y+" file=dual-guest-dig.png");
                    }
                    if (!accepted || !sawMarker || (s.Flags&10)!=0 || Grid.Solid[Cell] || NativeWorldMirror.IsDigVisible(Cell)) break;
                    if (!ownership.IssueGuest(lobby,Cell)) break;
                    log("DUAL_GUEST_EXCAVATED x="+x+" y="+y); Snapshot(); break;
                case Phase.Revisit:
                    if (!printing.Check(lobby,self,false,round,log)) break;
                    if (!roomControls.Check(lobby,self,round,false,log)) break;
                    if (!buildings.Check(lobby,self,false,round,log)) break;
                    if (!skills.Verify(lobby,round,false,log)) break;
                    if (!person.Guest(lobby,round,log)) break;
                    if (!work.Verify(lobby,round,false,log)) break;
                    if (!checkedBaseline || !Sample(x,y,out var readback) || (readback.Flags&10)!=0 || Grid.Solid[Cell]) break;
                    if (!ownership.Retained(Cell,false)) break;
                    log("DUAL_GUEST_REJOIN_READBACK x="+x+" y="+y);
                    SteamMatchmaking.SetLobbyMemberData(lobby,"qy_dual_revisit","2");
                    Set(Phase.HostTarget,60); break;
                case Phase.HostTarget:
                    if (SteamMatchmaking.GetLobbyData(lobby,"qy_dual_host_order")!="target") break;
                    if (!int.TryParse(SteamMatchmaking.GetLobbyData(lobby,"qy_dual_host_x"),out hostX) ||
                        !int.TryParse(SteamMatchmaking.GetLobbyData(lobby,"qy_dual_host_y"),out hostY)) break;
                    if (hostX<0 || hostY<0 || hostX>=Grid.WidthInCells || hostY>=Grid.HeightInCells || (hostX==x && hostY==y))
                        throw new InvalidDataException("Unexpected host action coordinate.");
                    Focus(hostX,hostY);
                    int hostCell=Grid.XYToCell(hostX,hostY);
                    if (!Sample(hostX,hostY,out var beforeHost) || (beforeHost.Flags&10)!=2 ||
                        !Grid.Solid[hostCell] || NativeWorldMirror.IsDigVisible(hostCell)) break;
                    log("DUAL_GUEST_HOST_TARGET x="+hostX+" y="+hostY+" solid=True marker=False");
                    SteamMatchmaking.SetLobbyMemberData(lobby,"qy_dual_target_clear","2");
                    Set(Phase.HostOrder,60); break;
                case Phase.HostOrder:
                    if (SteamMatchmaking.GetLobbyData(lobby,"qy_dual_host_order")!="2" ||
                        !Sample(hostX,hostY,out var afterHost) || (afterHost.Flags&10)!=10 ||
                        !NativeWorldMirror.IsDigVisible(Grid.XYToCell(hostX,hostY))) break;
                    if(!ownership.Pair(lobby,Grid.XYToCell(hostX,hostY),Cell,false,log) ||
                        !matched.Check(lobby,host,Grid.XYToCell(hostX,hostY),false,log)) break;
                    if(!DigMarkerGeometry.Verify(lobby,2,Grid.XYToCell(hostX,hostY),log)) break;
                    log("DUAL_GUEST_HOST_ORDER x="+hostX+" y="+hostY+" liveTransition=True nativeMarker=True");
                    Snapshot(); break;
                case Phase.Screenshot:
                    if (!frame.Requested) { frame.Capture(); break; }
                    if (!frame.Complete) break;
                    if (round==2 && !SteamDupePoseChecks.Guest(lobby,log)) break;
                    if (round==2 && !resourceHud.Check(lobby,log)) break;
                    log("DUAL_GUEST_SCREENSHOT round="+round+" file=dual-guest-"+round+".png");
                    SteamMatchmaking.SetLobbyMemberData(lobby,"qy_dual_done",round.ToString(CultureInfo.InvariantCulture));
                    Set(Phase.Ack,60); break;
                case Phase.Ack:
                    if (round==2 && !person.Death(lobby,false,log)) break;
                    if (SteamMatchmaking.GetLobbyData(lobby,"qy_dual_ack")!=round.ToString(CultureInfo.InvariantCulture)) break;
                    string counters=GuestDiagnostics.Summary;
                    if (!counters.StartsWith("simStep=0 findChore=0 setChore=0 saveAttempts=0 worldStarts=0 ",StringComparison.Ordinal))
                        throw new InvalidOperationException("Unexpected local simulation: "+counters);
                    log("DUAL_GUEST_LEAVE round="+round+" hostAck=true "+counters);
                    CoopSession.Disconnect(); Set(Phase.Leaving,90); break;
                case Phase.Leaving:
                    if (!AtMenu || NativeGuestScene.Active || CoopSession.IsClient || Directory.Exists(Path.GetDirectoryName(baselinePath))) break;
                    previousPath=baselinePath;
                    if (round==1) { round=2; SteamDupePoseChecks.StartGuest(); Set(Phase.Menu,90); }
                    else { phase=Phase.Done; Clean(); log(colonyJoin ? "PASS DUAL_GUEST colony baseline verified twice" :
                        "PASS DUAL_GUEST two Steam joins, native dig and fresh baseline readback"); }
                    break;
            }
        }
    }
}
