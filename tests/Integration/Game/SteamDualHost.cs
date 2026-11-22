using System;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Core;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamDualHost
    {
        enum Phase { Lobby, Invite, Joined, Dig, Hold, Excavated, Revisit, HostOrder, HostFrame, Leave, Finished }
        static SteamDualHost observing;
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
        readonly bool colonyJoin=Environment.GetEnvironmentVariable("QY_CHECK_COLONY_JOIN")=="1";
        readonly SteamMapBaseline mapBaseline=new SteamMapBaseline();
        int arrivalRound;
        int colonyVerifiedRound;
        readonly Action<string> log;
        readonly FixtureFrame screenshot, hostOrderFrame;
        readonly CSteamID owner, friend;
        readonly float started;
        CSteamID lobby;
        Phase phase;
        int round=1, cell=Grid.InvalidCell, hostCell=Grid.InvalidCell;
        float designatedAt, nextPresence;
        float nextCandidate, nextLaborLog;
        int candidate=Grid.InvalidCell, actualWorker=-1;
        MinionIdentity suggestedWorker;
        bool digSeen;
        string observationError;
        SteamMotionTrace motion;
        bool motionCompleted;
        float recordingArm;

        internal SteamDualHost(Action<string> log,string report)
        {
            this.log=log;
            screenshot=new FixtureFrame(Path.Combine(Path.GetDirectoryName(report),"dual-host.png"));
            hostOrderFrame=new FixtureFrame(Path.Combine(Path.GetDirectoryName(report),"dual-host-2.png"));
            owner=Account("QY_DUAL_SELF"); friend=Account("QY_DUAL_FRIEND");
            if(owner==friend || !SteamUser.BLoggedOn() || SteamUser.GetSteamID()!=owner)
                throw new InvalidOperationException("Steam host identity differs from the configured account");
            if(SteamUtils.GetAppID()!=new AppId_t(457140) || !SteamApps.BIsSubscribedApp(new AppId_t(457140)) ||
                SteamApps.GetAppOwner()!=owner || SteamApps.BIsSubscribedFromFamilySharing() || SteamApps.BIsSubscribedFromFreeWeekend())
                throw new InvalidOperationException("Independent Steam ownership of ONI could not be verified for the host");
            if(SteamFriends.GetFriendRelationship(friend)!=EFriendRelationship.k_EFriendRelationshipFriend)
                throw new InvalidOperationException("The configured guest is not a Steam friend of the authenticated host");
            started=Time.realtimeSinceStartup;
            log("DUAL_HOST_ACCOUNT configuredSelf=True distinctFriend=True ownsApp=True sharing=False");
        }

        static CSteamID Account(string name)
        {
            if(!ulong.TryParse(Environment.GetEnvironmentVariable(name),NumberStyles.None,CultureInfo.InvariantCulture,out var raw))
                throw new InvalidDataException("Expected SteamID64 in "+name);
            var account=new CSteamID(raw);
            if(!account.IsValid() || account.GetEAccountType()!=EAccountType.k_EAccountTypeIndividual)
                throw new InvalidDataException("Invalid individual Steam account in "+name);
            return account;
        }

        bool HasGuest()
        {
            int members=SteamMatchmaking.GetNumLobbyMembers(lobby);
            bool hasOwner=false,hasGuest=false;
            for(int i=0;i<members;i++)
            {
                var account=SteamMatchmaking.GetLobbyMemberByIndex(lobby,i);
                if(account==owner) hasOwner=true;
                else if(account==friend) hasGuest=true;
                else throw new InvalidOperationException("Unexpected third account in the two-account fixture");
            }
            if(!hasOwner || members!=(hasGuest?2:1) || SteamMatchmaking.GetLobbyOwner(lobby)!=owner)
                throw new InvalidOperationException("Steam lobby ownership or membership changed");
            return hasGuest;
        }
        static int ConnectedGuests()
        {
            var room=(RoomHost)AccessTools.DeclaredField(typeof(CoopSession),"host").GetValue(null);
            return room?.Guests ?? 0;
        }
        internal bool BeforeHosting()
        {
            if (colonyJoin)
            {
                if (!SteamMapBaseline.Enabled || !SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Pause(false);
                return true;
            }
            if(Time.realtimeSinceStartup<nextCandidate) return false;
            nextCandidate=Time.realtimeSinceStartup+1;
            if(!ChooseReachableExcavation()) { SpeedControlScreen.Instance.Unpause(false); return false; }
            SpeedControlScreen.Instance.Pause(false);
            if (!SteamCritterMotion.Prepare(candidate,log)) return false;
            ownership.PrepareLegacy(candidate,log);
            return true;
        }
        internal bool Tick()
        {
            if(Time.realtimeSinceStartup-started>900) throw new TimeoutException("Dual Steam host waited in "+phase+" round "+round);
            if(observationError!=null) throw new InvalidDataException(observationError);
            if(!CoopSession.IsHosting) throw new InvalidOperationException("Steam host stopped: "+CoopSession.Status);
            bool joined=false;
            if(phase!=Phase.Lobby) joined=HasGuest();
            switch(phase)
            {
                case Phase.Lobby:
                    if (!ulong.TryParse(CoopSession.Endpoint,out var value)) return false;
                    lobby=new CSteamID(value);
                    if(!lobby.IsLobby() || HasGuest() || SteamMatchmaking.GetLobbyMemberLimit(lobby)!=4 ||
                        SteamMatchmaking.GetLobbyData(lobby,"queyang_coop")!="1")
                        throw new InvalidOperationException("Initial Steam lobby metadata does not match a private test session");
                    if (!SteamMapBaseline.Enabled || !SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Pause(false);
                    if (colonyJoin)
                    {
                        SetData("qy_colony_buildings", ColonyRangeSurvey.BuildingSignature());
                        log("DUAL_HOST_LOBBY "+lobby);
                        phase=Phase.Invite;
                        break;
                    }
                    Grid.CellToXY(candidate,out int digX,out int digY);
                    // Fail early on an invalid default viewport before asking a second player to accept.
                    ColonyView.Decode(HostWorld.Capture(-1,-1));
                    ownership.PublishLegacy(lobby);
                    skills.Prepare(lobby,log);
                    if (SteamCritterMotion.Enabled) SetData("qy_critter_id",SteamCritterMotion.HostId.ToString());
                    SetData("qy_dual_dig_x",digX.ToString(CultureInfo.InvariantCulture));
                    SetData("qy_dual_dig_y",digY.ToString(CultureInfo.InvariantCulture));
                    log($"DUAL_HOST_CANDIDATE x={digX} y={digY} worker={suggestedWorker.GetComponent<KPrefabID>().InstanceID} nativeOffsetsReachable=True hardness={Grid.Element[candidate].hardness}");
                    observing=this;
                    log("DUAL_HOST_LOBBY "+lobby);
                    phase=Phase.Invite;
                    break;
                case Phase.Invite:
                    if(Time.realtimeSinceStartup>=nextPresence)
                    {
                        nextPresence=Time.realtimeSinceStartup+3;
                        SteamFriends.RequestFriendRichPresence(friend);
                    }
                    if(!joined && SteamFriends.GetFriendRichPresence(friend,"qy_dual_ready")!=RoundText()) return false;
                    if(SteamFriends.GetFriendRelationship(friend)!=EFriendRelationship.k_EFriendRelationshipFriend ||
                        !SteamMatchmaking.InviteUserToLobby(lobby,friend))
                        throw new InvalidOperationException("Steam rejected the targeted invitation");
                    log("DUAL_HOST_INVITE round="+round+" targetVerified=True");
                    phase=Phase.Joined;
                    break;
                case Phase.Joined:
                    if(!joined || ConnectedGuests()!=1) return false;
                    if (arrivalRound != round)
                    {
                        if (!arrival.Capture(lobby,friend,round,false,log)) return false;
                        arrivalRound = round;
                    }
                    if (colonyJoin)
                    {
                        if (SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_colony_verified")!=RoundText()) return false;
                        if (colonyVerifiedRound != round)
                        {
                            log("COLONY_JOIN role=host round="+round+" buildings="+ColonyRangeSurvey.BuildingSignature());
                            colonyVerifiedRound = round;
                        }
                        if (!health.Check(lobby,friend,true,round,log)) return false;
                        if (!mapBaseline.Check(lobby,friend,true,log)) return false;
                        SetData("qy_colony_ack", RoundText());
                        phase=Phase.Leave;
                        break;
                    }
                    log("DUAL_HOST_MEMBER round="+round+" expectedFriend=True members=2 connectedPeers=1");
                    phase=round==1?Phase.Dig:Phase.Revisit;
                    break;
                case Phase.Dig:
                    if (!facilityRange.Check(lobby,friend,true,log)) return false;
                    if (!alertRange.Check(lobby,friend,true,round,candidate,log)) return false;
                    if (!sweepRange.Check(lobby,friend,true,round,log)) return false;
                    if (!powerRange.Check(lobby,friend,true,round,log)) return false;
                    if (!researchRange.Check(lobby,friend,true,round,log)) return false;
                    if (!batteryRange.Check(lobby,friend,true,round,log)) return false;
                    if (!reservoirRange.Check(lobby,friend,true,round,log)) return false;
                    // Other fixture stages let the colony run, and a worker may finish this old order.
                    if (!ownership.Legacy(lobby,true,log) || SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_legacy_checked")!="1") return false;
                    if (!researchMotion.Check(lobby,friend,true,round,log)) return false;
                    if (!buildingMotion.Check(lobby,friend,true,round,log)) return false;
                    if (!dupeFeedback.Check(lobby,friend,true,round,log)) return false;
                    if (!upgradeVisual.Check(lobby,friend,round,true,log)) return false;
                    if (!printing.Check(lobby,friend,true,round,log)) return false;
                    if (!floorRange.Check(lobby,friend,true,round,log)) return false;
                    if (!constructionRange.Check(lobby,friend,true,round,log)) return false;
                    if (!areaDig.Check(lobby,friend,true,round,candidate,log)) return false;
                    if (!health.Check(lobby,friend,true,round,log)) return false;
                    if (!lighting.Check(lobby,friend,true,round,log)) return false;
                    if (!buriedItems.Check(lobby,friend,true,round,log)) return false;
                    if (!fallingOre.Check(lobby,friend,true,round,log)) return false;
                    if (!buildings.Check(lobby,friend,true,round,log)) return false;
                    if (!skills.Verify(lobby,round,true,log)) return false;
                    if (!person.Host(lobby,round,log)) return false;
                    if (!work.Verify(lobby,round,true,log)) return false;
                    if (!roomControls.Check(lobby,friend,round,true,log)) return false;
                    if(!joined) throw new InvalidOperationException("Guest left before issuing a dig order");
                    if(!digSeen) return false;
                    phase=Phase.Hold;
                    break;
                case Phase.Hold:
                    if(!joined) throw new InvalidOperationException("Guest left before its order was visible");
                    if(!screenshot.Requested)
                    {
                        if(Time.realtimeSinceStartup-designatedAt<1) return false;
                        if(!ownership.Initial(cell)) return false;
                        if(!DigMarkerGeometry.Publish(lobby,1,cell,log)) return false;
                        screenshot.Capture();
                    }
                    if(!screenshot.Complete || Time.realtimeSinceStartup-designatedAt<3) return false;
                    if(SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_dual_dig_photo")!="1") return false;
                    int motionId=suggestedWorker.GetComponent<KPrefabID>().InstanceID;
                    if(motion==null)
                    {
                        log("DUAL_HOST_SCREENSHOT dual-host.png");
                        SetData("qy_motion_id",motionId.ToString(CultureInfo.InvariantCulture));
                        motion=SteamMotionTrace.Create(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),motionId,true,
                            SteamCritterMotion.Enabled ? SteamCritterMotion.HostId : 0);
                    }
                    if(SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_motion_ready")!="1") return false;
                    if(recordingArm==0)
                    {
                        recordingArm=Time.realtimeSinceStartup;
                        log("DUAL_MOTION_ARM");
                    }
                    string signal=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),"motion-recording.ready");
                    if(!File.Exists(signal))
                    {
                        if(Time.realtimeSinceStartup-recordingArm>15) throw new TimeoutException("Capture processes did not signal ready");
                        return false;
                    }
                    PrepareLabor();
                    SpeedControlScreen.Instance.Unpause(false);
                    motion.Begin();
                    log("DUAL_MOTION_START id="+motionId+" speed=3");
                    phase=Phase.Excavated;
                    break;
                case Phase.Excavated:
                    if(!joined) throw new InvalidOperationException("Guest left before corroborating the completed excavation");
                    if(round==1)
                    {
                        if(!motionCompleted)
                        {
                            if(SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
                            if(motion.Seconds<35 || SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_motion_done")!="1") return false;
                            Grid.CellToXY(cell,out int mx,out int my);
                            motion.End(log,mx,my); motionCompleted=true;
                        }
                        if(Grid.Solid[cell]) MaintainLabor();
                        else if(Diggable.GetDiggable(cell)==null) ownership.PrepareHost(lobby,ChooseHostDig);
                    }
                    if(Grid.Solid[cell] || Diggable.GetDiggable(cell)!=null ||
                        SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_dual_done")!=RoundText()) return false;
                    if (!ownership.Retained(cell,true)) return false;
                    if (round==2 && !person.Death(lobby,true,log)) return false;
                    if(actualWorker<0) throw new InvalidDataException("Dig cell cleared without a native duplicant work tick");
                    if(!SteamMatchmaking.SetLobbyData(lobby,"qy_dual_ack",RoundText()))
                        throw new InvalidOperationException("Could not acknowledge corroborated excavation");
                    Grid.CellToXY(cell,out int x,out int y);
                    log($"DUAL_HOST_VERIFIED round={round} x={x} y={y} element={Grid.Element[cell].id} nativeOrderGone=True digWorker={actualWorker}");
                    phase=Phase.Leave;
                    break;
                case Phase.Revisit:
                    if (!printing.Check(lobby,friend,true,round,log)) return false;
                    if (!roomControls.Check(lobby,friend,round,true,log)) return false;
                    if (!buildings.Check(lobby,friend,true,round,log)) return false;
                    if (!skills.Verify(lobby,round,true,log)) return false;
                    if (!person.Host(lobby,round,log)) return false;
                    if (!work.Verify(lobby,round,true,log)) return false;
                    if (!joined) throw new InvalidOperationException("Guest left before verifying its second baseline");
                    if (SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_dual_revisit")!="2") return false;
                    hostCell=ChooseHostDig();
                    Grid.CellToXY(hostCell,out int hx,out int hy);
                    SetData("qy_dual_host_x",hx.ToString(CultureInfo.InvariantCulture));
                    SetData("qy_dual_host_y",hy.ToString(CultureInfo.InvariantCulture));
                    SetData("qy_dual_host_order","target");
                    phase=Phase.HostOrder;
                    break;
                case Phase.HostOrder:
                    if (!joined) throw new InvalidOperationException("Guest left before observing the host order");
                    if (SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_dual_target_clear")!="2") return false;
                    IssueHostDig();
                    SetData("qy_dual_host_order","2");
                    phase=Phase.HostFrame;
                    break;
                case Phase.HostFrame:
                    if(!hostOrderFrame.Requested)
                    {
                        if(Time.realtimeSinceStartup-designatedAt<1) return false;
                        if(!ownership.Pair(lobby,hostCell,cell,true,log) || !matched.Check(lobby,friend,hostCell,true,log)) return false;
                        if(!DigMarkerGeometry.Publish(lobby,2,hostCell,log)) return false;
                        if(!ResourceHudCheck.Publish(lobby,log)) return false;
                        hostOrderFrame.Capture();
                    }
                    if(!hostOrderFrame.Complete || !SteamDupePoseChecks.Host(lobby,friend,log)) return false;
                    log("DUAL_HOST_SCREENSHOT dual-host-2.png");
                    phase=Phase.Excavated;
                    break;
                case Phase.Leave:
                    if(joined || ConnectedGuests()!=0) return false;
                    log("DUAL_HOST_LEFT round="+round+" members=1 connectedPeers=0");
                    if(round==1 && !SteamMapBaseline.Enabled)
                    {
                        round=2;
                        SteamDupePoseChecks.StartHost();
                        SpeedControlScreen.Instance.Pause(false);
                        phase=Phase.Invite;
                    }
                    else
                    {
                        if (!colonyJoin) ownership.SaveEvidence(hostCell,cell,log);
                        phase=Phase.Finished; observing=null;
                        SpeedControlScreen.Instance.Unpause(false);
                        log(SteamMapBaseline.Enabled ? "PASS DUAL_HOST colony baseline and runtime samples collected" :
                            colonyJoin ? "PASS DUAL_HOST colony baseline verified twice" :
                            "PASS DUAL_HOST two distinct Steam members, production dig, native excavation, reconnect and leave verified");
                        return true;
                    }
                    break;
            }
            return phase==Phase.Finished;
        }

        string RoundText() => round.ToString(CultureInfo.InvariantCulture);
        bool ChooseReachableExcavation()
        {
            var placement=Assets.GetPrefab("DigPlacer").GetComponent<Diggable>();
            int best=int.MaxValue;
            foreach(var dupe in Components.LiveMinionIdentities.Items)
            {
                if(dupe==null || ClusterUtil.GetMyWorldId(dupe)!=ClusterManager.Instance.activeWorldId) continue;
                var consumer=dupe.GetComponent<ChoreConsumer>();
                var nav=dupe.GetComponent<Navigator>();
                if(consumer==null || nav==null || consumer.IsChoreGroupDisabled(Db.Get().ChoreGroups.Dig)) continue;
                int origin=Grid.PosToCell(dupe);
                Grid.CellToXY(origin,out int cx,out int cy);
                for(int x=Math.Max(1,cx-12);x<=Math.Min(Grid.WidthInCells-2,cx+12);x++)
                    for(int y=Math.Max(1,cy-12);y<=Math.Min(Grid.HeightInCells-2,cy+12);y++)
                    {
                        int c=Grid.XYToCell(x,y);
                        if(Grid.WorldIdx[c]!=Grid.WorldIdx[origin] || Grid.Visible[c]==0 || !Grid.Solid[c] ||
                            Grid.Element[c].hardness>=50 || !Grid.IsValidBuildingCell(c) || !Diggable.IsDiggable(c) ||
                            Diggable.Undiggable(Grid.Element[c]) || Diggable.GetDiggable(c)!=null || HostWorld.FindBuilding(c)!=null)
                            continue;
                        int distance=Math.Abs(x-cx)+Math.Abs(y-cy);
                        int cost=distance*100+Grid.Element[c].hardness;
                        if(cost>=best) continue;
                        // Query ONI's actual Diggable work positions for this terrain and the
                        // worker's current path probe without creating a designation.
                        var offsets=placement.GetOffsets(c);
                        if(!nav.CanReach(c,offsets) || !offsets.Any(offset=>
                        {
                            int stand=Grid.OffsetCell(c,offset);
                            return Grid.IsValidCell(stand) && Grid.IsValidCell(Grid.CellBelow(stand)) && Grid.CellBelow(stand)!=c &&
                                !Grid.Solid[stand] && Grid.Solid[Grid.CellBelow(stand)] && nav.CanReach(stand);
                        })) continue;
                        best=cost; candidate=c; suggestedWorker=dupe;
                    }
            }
            return candidate!=Grid.InvalidCell;
        }
        void PrepareLabor()
        {
            var job=Diggable.GetDiggable(cell);
            if(job==null || !string.IsNullOrEmpty(job.requiredSkillPerk) || suggestedWorker==null ||
                !suggestedWorker.GetComponent<Navigator>().CanReach(job))
                throw new InvalidDataException("The guest's real dig order is no longer reachable without a skill perk");
            suggestedWorker.GetComponent<ChoreConsumer>().SetPersonalPriority(Db.Get().ChoreGroups.Dig,5);
            job.GetComponent<Prioritizable>().SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.topPriority,9));
            SpeedControlScreen.Instance.SetSpeed(2);
            log("DUAL_HOST_LABOR_PROVISION selectedWorkerPersonalDigPriority=5 jobPriority=topPriority/9 speedIndex=2");
        }
        void MaintainLabor()
        {
            if(SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
            if(Time.realtimeSinceStartup<nextLaborLog) return;
            nextLaborLog=Time.realtimeSinceStartup+10;
            var job=Diggable.GetDiggable(cell);
            var worker=suggestedWorker;
            string chore=worker==null?null:worker.GetComponent<ChoreDriver>()?.GetCurrentChore()?.choreType.Id;
            bool reachable=job!=null && worker!=null && worker.GetComponent<Navigator>().CanReach(job);
            log($"DUAL_HOST_LABOR_PROGRESS sim={GameClock.Instance.GetTime():0.0} paused={SpeedControlScreen.Instance.IsPaused} scale={Time.timeScale:0.0} element={Grid.Element[cell].id} order={job!=null} reachable={reachable} chore={chore} digWorker={actualWorker}");
        }
        void SetData(string key,string value)
        {
            if(!SteamMatchmaking.SetLobbyData(lobby,key,value)) throw new IOException("Steam rejected metadata "+key);
        }
        int ChooseHostDig()
        {
            Grid.CellToXY(cell,out int cx,out int cy);
            for(int distance=1;distance<=8;distance++)
                for(int dx=-distance;dx<=distance;dx++)
                    for(int dy=-distance;dy<=distance;dy++)
                    {
                        if(Math.Abs(dx)+Math.Abs(dy)!=distance) continue;
                        int x=cx+dx,y=cy+dy;
                        if(x<0 || y<0 || x>=Grid.WidthInCells || y>=Grid.HeightInCells) continue;
                        int c=Grid.XYToCell(x,y);
                        if(Grid.WorldIdx[c]==Grid.WorldIdx[cell] && Grid.Visible[c]>0 && Grid.Solid[c] &&
                            Grid.IsValidBuildingCell(c) && Diggable.IsDiggable(c) && !Diggable.Undiggable(Grid.Element[c]) &&
                            HostWorld.FindBuilding(c)==null && Diggable.GetDiggable(c)==null) return c;
                    }
            throw new InvalidOperationException("No second visible diggable cell near the guest excavation");
        }
        void IssueHostDig()
        {
            if(!SpeedControlScreen.Instance.IsPaused || !Grid.Solid[hostCell] || Diggable.GetDiggable(hostCell)!=null)
                throw new InvalidDataException("Second cell changed before the host used the dig tool");
            PlayerController.Instance.ActivateTool(DigTool.Instance);
            var point=Grid.CellToPosCCC(hostCell,Grid.SceneLayer.Move);
            var target=point;
            var camera=CameraController.Instance;
            target.z=camera.transform.position.z;
            camera.SetTargetPos(target,Mathf.Max(5f,camera.OrthographicSize),false);
            DigTool.Instance.OnLeftClickDown(point);
            DigTool.Instance.OnLeftClickUp(point);
            Grid.CellToXY(hostCell,out int x,out int y);
            var view=ColonyView.Decode(HostWorld.Capture(x-ColonyView.Width/2,y-ColonyView.Height/2));
            if(Diggable.GetDiggable(hostCell)==null || (view.Cells[(y-view.Y)*ColonyView.Width+x-view.X].Flags&8)==0)
                throw new InvalidDataException("Host native drag did not create a replicated dig marker");
            designatedAt=Time.realtimeSinceStartup;
            log($"DUAL_HOST_LOCAL_ORDER round=2 x={x} y={y} source=vanilla-drag hostNative=True hostView=True");
        }
        internal void Abort()
        {
            observing=null;
            if(CoopSession.IsHosting) CoopSession.Disconnect();
        }

        [HarmonyPatch(typeof(Diggable),"OnWorkTick")]
        static class WorkWitness
        {
            static void Prefix(Diggable __instance,WorkerBase __0)
            {
                var test=observing;
                if(test==null || test.round!=1 || test.actualWorker>=0 || test.cell==Grid.InvalidCell ||
                    Grid.PosToCell(__instance)!=test.cell || __0==null || __0.GetComponent<MinionIdentity>()==null) return;
                test.actualWorker=__0.GetComponent<KPrefabID>().InstanceID;
                test.log("DUAL_HOST_NATIVE_DIG_WORK worker="+test.actualWorker+" cell="+test.cell);
            }
        }

        [HarmonyPatch(typeof(HostWorld),nameof(HostWorld.Execute))]
        static class Witness
        {
            static void Postfix(string action,int x,int y,string __result)
            {
                var test=observing;
                if(test==null || test.round!=1 || test.areaDig.Busy || test.digSeen || action!="dig" || __result!="OK Dig designated") return;
                int cell=Grid.XYToCell(x,y);
                // Observe the order while on the main thread. The actual command and placement ran in HostWorld.
                if(cell!=test.candidate || !Grid.IsValidCell(cell) || !Grid.Solid[cell] || Diggable.GetDiggable(cell)==null)
                {
                    test.observationError="Host could not corroborate the guest's excavation order";
                    return;
                }
                test.cell=cell; test.digSeen=true; test.designatedAt=Time.realtimeSinceStartup;
                var camera=CameraController.Instance;
                var target=Grid.CellToPosCCC(cell,Grid.SceneLayer.Move);
                target.z=camera.transform.position.z;
                camera.SetTargetPos(target,Mathf.Max(5f,camera.OrthographicSize),false);
                test.log($"DUAL_HOST_DIG x={x} y={y} reply={__result} nativeOrder=True");
            }
        }
    }
}
