using System;
using System.IO;
using Queyang.Core;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    // Real Unity authority, production TCP room and a read-only protocol peer inside the test process.
    internal sealed class TerrainNetworks : IDisposable
    {
        enum Stage { Fog, Seed, Melt, Opening, Wires, Green, Red, Split, Restored }
        readonly Action<string> log;
        readonly RoomClient client;
        readonly CoopTransport link;
        readonly int x,y;
        readonly float started=Time.realtimeSinceStartup;
        Stage stage;
        ColonyView view;
        Door door;
        LogicTemperatureSensor sensor;
        int openAnimation;
        float lastStatus, nextPing;
        readonly global::Wire[] power=new global::Wire[5];
        readonly LogicWire[] logic=new LogicWire[5];
        int C(int dx,int dy)=>Grid.XYToCell(x+dx,y+dy);
        ColonyCell Sample(int dx,int dy)=>view.Cells[(y+dy-view.Y)*ColonyView.Width+x+dx-view.X];

        internal TerrainNetworks(Action<string> write)
        {
            if (Environment.GetEnvironmentVariable("QY_SMOKE_ROOT")!=Util.GetKleiRootPath()) throw new InvalidOperationException("Isolated colony required.");
            log=write;
            var world=ClusterManager.Instance.activeWorld;
            // Choose an unexplored rectangle well inside the simulation bounds and away from loaded buildings.
            int ox=world.WorldOffset.x, oy=world.WorldOffset.y;
            bool found=false;
            for (int cy=oy+20; cy<oy+world.WorldSize.y-30 && !found; cy+=4)
                for (int cx=ox+20; cx<ox+world.WorldSize.x-30 && !found; cx+=4)
                {
                    bool clear=true;
                    for (int dy=0;dy<9 && clear;dy++)
                        for (int dx=0;dx<14 && clear;dx++)
                        {
                            int c=Grid.XYToCell(cx+dx,cy+dy);
                            clear=Grid.WorldIdx[c]==world.id && Grid.Visible[c]==0 && HostWorld.FindBuilding(c)==null &&
                                Grid.Element[c].id!=SimHashes.Unobtanium;
                        }
                    if (clear) { x=cx; y=cy; found=true; }
                }
            if (!found) throw new InvalidDataException("No isolated unexplored terrain fixture area.");
            link=new CoopTransport((_,b)=>client.Receive(b),_=>client.Connected(),_=>throw new IOException("Terrain probe disconnected."));
            client=new RoomClient(Environment.GetEnvironmentVariable("QY_SMOKE_FINGERPRINT"),b=>link.Send(0,b));
            client.ViewReceived+=b=>view=ColonyView.Decode(b);
            client.Subscribe(x,y); link.Join(CoopSession.Endpoint,false);
            SpeedControlScreen.Instance.Unpause(false);
        }

        static void Seed(int cell,SimHashes element,float mass,float kelvin) =>
            SimMessages.ReplaceElement(cell,element,CellEventLogger.Instance.SandBoxTool,mass,kelvin);
        static GameObject Build(string id,int cell,SimHashes element) =>
            Assets.GetBuildingDef(id).Build(cell,Orientation.Neutral,null,new[]{ElementLoader.FindElementByHash(element).tag},295f,false,-1f);
        void Advance(Stage next,string line) { log("TERRAIN "+line); stage=next; }

        internal bool Tick()
        {
            link.Poll();
            if (Time.realtimeSinceStartup>=nextPing) { client.Ping(1); nextPing=Time.realtimeSinceStartup+3; }
            if (Time.realtimeSinceStartup-started>155) throw new TimeoutException("Stopped in "+stage+" "+State());
            if (Time.realtimeSinceStartup>=lastStatus+10) { log("TERRAIN_WAIT "+stage+" "+State()); lastStatus=Time.realtimeSinceStartup; }
            if (client.Rejected) throw new IOException(client.Status);
            if (view==null || view.X!=x || view.Y!=y) return false;
            if (SpeedControlScreen.Instance.IsPaused) SpeedControlScreen.Instance.Unpause(false);
            var game=global::Game.Instance;
            switch (stage)
            {
                case Stage.Fog:
                    if ((Sample(3,5).Flags & 1)!=0) throw new InvalidDataException("Unrevealed grid leaked into baseline.");
                    for (int dy=0;dy<9;dy++)
                        for(int dx=0;dx<14;dx++)
                        {
                            int c=C(dx,dy); Grid.Reveal(c,255,true);
                            Seed(c,SimHashes.Granite,1800,280);
                        }
                    Seed(C(3,5),SimHashes.Ice,1000,255);
                    Seed(C(9,5),SimHashes.Oxygen,20,294); Seed(C(11,5),SimHashes.Vacuum,0,294);
                    for(int dx=2;dx<=8;dx++) for(int dy=1;dy<=2;dy++) Seed(C(dx,dy),SimHashes.Oxygen,2,295);
                    Advance(Stage.Seed,"fog baseline hidden; requested native reveal and seeded sealed cells"); break;
                case Stage.Seed:
                    if (Sample(3,5).ElementHash!=(int)SimHashes.Ice || Sample(3,5).Visibility==0 ||
                        Sample(9,5).Mass<19 || Sample(11,5).Mass>.001 || Sample(10,5).ElementHash!=(int)SimHashes.Granite) break;
                    SimMessages.ModifyEnergy(C(3,5),200000,315,SimMessages.EnergySourceID.DebugHeat);
                    Advance(Stage.Melt,"revealed frozen ice received by TCP peer"); break;
                case Stage.Melt:
                    if (Grid.Element[C(3,5)].id!=SimHashes.Water || Sample(3,5).ElementHash!=(int)SimHashes.Water || Sample(3,5).Mass<900) break;
                    SimMessages.Dig(C(10,5));
                    Advance(Stage.Opening,"phase Ice->Water mass="+Sample(3,5).Mass); break;
                case Stage.Opening:
                    if (Sample(10,5).ElementHash!=(int)SimHashes.Oxygen || Sample(11,5).ElementHash!=(int)SimHashes.Oxygen || Sample(11,5).Mass<1) break;
                    sensor=Build(LogicTemperatureSensorConfig.ID,C(2,1),SimHashes.Copper).GetComponent<LogicTemperatureSensor>();
                    door=Build(PressureDoorConfig.ID,C(6,1),SimHashes.Cuprite).GetComponent<Door>();
                    sensor.ActivateAboveThreshold=true; sensor.Threshold=200;
                    for(int i=0;i<5;i++)
                    {
                        power[i]=Build(WireConfig.ID,C(2+i,1),SimHashes.Cuprite).GetComponent<global::Wire>();
                        logic[i]=Build(LogicWireConfig.ID,C(2+i,1),SimHashes.Copper).GetComponent<LogicWire>();
                    }
                    Advance(Stage.Wires,"opening redistributed oxygen destinationKg="+Sample(11,5).Mass); break;
                case Stage.Wires:
                    for (int i=0;i<5;i++)
                    {
                        int c=C(2+i,1);
                        var directions=(i>0 ? UtilityConnections.Left : 0) | (i<4 ? UtilityConnections.Right : 0);
                        Set(game.electricalConduitSystem,c,power[i].gameObject,directions);
                        Set(game.logicCircuitSystem,c,logic[i].gameObject,directions);
                    }
                    Advance(Stage.Green,"wired native temperature sensor to door"); break;
                case Stage.Green:
                    if (Sample(4,1).LogicValue==0 || !door.IsOpen() || !Together() || !DoorReceived()) break;
                    openAnimation=Sample(6,1).BuildingAnimation;
                    sensor.Threshold=450;
                    Advance(Stage.Red,"sensor green opened door; electrical endpoints connected"); break;
                case Stage.Red:
                    if (Sample(4,1).LogicValue!=0 || door.IsOpen() || !DoorReceived() || Sample(6,1).BuildingAnimation==openAnimation) break;
                    Util.KDestroyGameObject(power[2].gameObject);
                    Advance(Stage.Split,"sensor red closed door"); break;
                case Stage.Split:
                    if (Together() || !ValidEndpoints() || (Sample(4,1).Utilities & 1)!=0) break;
                    power[2]=Build(WireConfig.ID,C(4,1),SimHashes.Cuprite).GetComponent<global::Wire>();
                    Set(game.electricalConduitSystem,C(3,1),power[1].gameObject,UtilityConnections.Left|UtilityConnections.Right);
                    Set(game.electricalConduitSystem,C(4,1),power[2].gameObject,UtilityConnections.Left|UtilityConnections.Right);
                    Set(game.electricalConduitSystem,C(5,1),power[3].gameObject,UtilityConnections.Left|UtilityConnections.Right);
                    Advance(Stage.Restored,"wire disconnected distinct circuits TCP topology updated"); break;
                case Stage.Restored:
                    if (!Together() || (Sample(4,1).Utilities & 1)==0 || (Sample(4,1).Connections & 3)!=3) break;
                    log("TERRAIN research unlocked in stock building requirements "+NativeManagementChecks.CheckUnlockProjection());
                    log("PASS terrain and networks: native phase, fog, opening, sensor-door, circuit split/rejoin; TCP projection");
                    Dispose(); return true;
            }
            return false;
        }
        static void Set(IUtilityNetworkMgr mgr,int cell,GameObject wire,UtilityConnections value)
        {
            mgr.SetConnections(value,cell,false);
            var v=wire.GetComponent<KAnimGraphTileVisualizer>(); v.UpdateConnections(value); v.Connections=value;
        }
        bool ValidEndpoints() => global::Game.Instance.circuitManager.GetCircuitID(C(2,1))!=CircuitManager.INVALID_ID &&
            global::Game.Instance.circuitManager.GetCircuitID(C(6,1))!=CircuitManager.INVALID_ID;
        bool Together()=>ValidEndpoints() && global::Game.Instance.circuitManager.GetCircuitID(C(2,1))==global::Game.Instance.circuitManager.GetCircuitID(C(6,1));
        bool DoorReceived()=>Sample(6,1).BuildingHash==new HashedString(PressureDoorConfig.ID).HashValue &&
            Sample(6,1).BuildingAnimation==door.GetComponent<KBatchedAnimController>().currentAnim.HashValue;
        string State()=>view==null || view.X!=x || view.Y!=y ? client.Status : "ice="+Sample(3,5).ElementHash+" mass="+Sample(3,5).Mass+
            " door="+(door==null ? "-" : door.CurrentState.ToString())+" signal="+Sample(4,1).LogicValue+
            " circuits="+global::Game.Instance.circuitManager.GetCircuitID(C(2,1))+"/"+global::Game.Instance.circuitManager.GetCircuitID(C(6,1));
        public void Dispose() { link.Dispose(); client.Dispose(); }
    }
}
