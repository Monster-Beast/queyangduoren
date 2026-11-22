using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    // A falling pickup can leave its partition cell between simulation notifications.
    // Sample the transforms driven by ONI gravity, including the first settled position.
    internal static class NativeFallingOre
    {
        const int Header=-1, RowBytes=32, RowsPerPacket=32;
        sealed class Flight { internal Pickupable Ore; internal float Until; }
        static readonly Dictionary<int,Flight> flying=new Dictionary<int,Flight>();
        static readonly List<int> expired=new List<int>();
        static readonly Dictionary<int,int> lastTick=new Dictionary<int,int>();
        static int[] coverage;

        internal static IEnumerable<Pickupable> Active()
        {
            foreach (var flight in flying.Values)
                if (NativeResources.IsOre(flight.Ore) && flight.Ore.gameObject.activeInHierarchy) yield return flight.Ore;
        }

        [HarmonyPatch(typeof(GravityComponents),"Add",new[]{typeof(GameObject),typeof(Vector2),typeof(Action<Transform>)})]
        static class Began
        {
            static void Postfix(GameObject __0)
            {
                if (!CoopSession.IsHosting) return;
                var ore=__0.GetComponent<Pickupable>();
                if (NativeResources.IsOre(ore)) flying[ore.KPrefabID.InstanceID]=new Flight{Ore=ore,Until=Time.unscaledTime+1f};
            }
        }

        internal static IEnumerable<byte[]> Capture(WorldContainer world,int left,int bottom)
        {
            if (flying.Count==0) yield break;
            // Snapshot the collection before yielding. Each peer gets independent coordinates.
            var packets=new List<byte[]>();
            using (var stream=new MemoryStream())
            using (var w=new BinaryWriter(stream))
            {
                w.Write(Header);w.Write(Time.frameCount);
                expired.Clear();
                foreach (var pair in flying)
                {
                    var f=pair.Value;var p=f.Ore;
                    if (!NativeResources.IsOre(p) || !p.gameObject.activeInHierarchy) {expired.Add(pair.Key);continue;}
                    if (GameComps.Gravities.Has(p.gameObject)) f.Until=Time.unscaledTime+1f;
                    if (Time.unscaledTime>f.Until) {expired.Add(pair.Key);continue;}
                    Vector2 pos=p.transform.position;
                    int cell=Grid.PosToCell(pos);
                    if (!Grid.IsValidCell(cell) || Grid.Visible[cell]==0 || Grid.WorldIdx[cell]!=world.id ||
                        left>=0 && (pos.x<left-24 || pos.x>=left+2*ColonyView.Width || pos.y<bottom-16 || pos.y>=bottom+2*ColonyView.Height)) continue;
                    var m=NativeMaterialReadings.Capture(p.PrimaryElement);
                    w.Write(pair.Key);w.Write(pos.x);w.Write(pos.y);w.Write(m.Element);w.Write(m.Mass);w.Write(m.Temperature);
                    w.Write(p.GetComponent<KBatchedAnimController>()?.currentAnim.HashValue??0);w.Write(cell);
                    if (stream.Length!=8+RowBytes*RowsPerPacket) continue;
                    packets.Add(stream.ToArray());stream.SetLength(8);stream.Position=8;
                }
                if(stream.Length>8) packets.Add(stream.ToArray());
            }
            foreach (int id in expired) flying.Remove(id);
            foreach (var bytes in packets) yield return bytes;
        }
        internal static bool IsPacket(byte[] bytes) => bytes.Length>=4 && BitConverter.ToInt32(bytes,0)==Header;

        internal static void Apply(byte[] packet)
        {
            if(packet.Length<=8 || packet.Length>8+RowBytes*RowsPerPacket || (packet.Length-8)%RowBytes!=0)
                throw new InvalidDataException("Invalid ore movement packet");
            var root=NativeWorldMirror.DisplayRoot;
            using(var r=new BinaryReader(new MemoryStream(packet,false)))
            {
                r.ReadInt32();int tick=r.ReadInt32();
                if(tick<=0) throw new InvalidDataException("Invalid ore sample time");
                while(r.BaseStream.Position<packet.Length)
                {
                    int id=r.ReadInt32();var pos=new Vector2(r.ReadSingle(),r.ReadSingle());
                    var m=new MaterialSample {Element=r.ReadInt32(),Mass=r.ReadSingle(),Temperature=r.ReadSingle()};
                    int anim=r.ReadInt32(),cell=r.ReadInt32();
                    if(id<=0 || !(pos.x>=0 && pos.x<Grid.WidthInCells && pos.y>=-.05f && pos.y<Grid.HeightInCells) ||
                        !Grid.IsValidCell(cell) || Grid.PosToCell(pos)!=cell || !(m.Mass>=0 && m.Mass<=PrimaryElement.MAX_MASS) ||
                        !(m.Temperature>=0 && m.Temperature<=Sim.MaxTemperature)) throw new InvalidDataException("Invalid ore position or material");
                    if(root==null || Grid.WorldIdx[cell]!=ClusterManager.Instance.activeWorldId ||
                        coverage!=null && coverage[cell]>=tick || lastTick.TryGetValue(id,out int old) && old>=tick) continue;
                    var element=ElementLoader.FindElementByHash((SimHashes)m.Element);
                    if(element==null || !element.IsSolid) continue;
                    NativeResources.ApplyPile(new ColonyPile{SaveId=id,Animation=anim,Material=m},cell,pos,element,root,false);
                    lastTick[id]=tick;
                }
            }
        }
        internal static bool AcceptView(int id,int tick) => !lastTick.TryGetValue(id,out int seen) || tick>=seen;
        internal static void ObserveCoverage(ColonyView view)
        {
            if(view.DupePoseTick<=0) return;
            if(coverage==null || coverage.Length!=Grid.CellCount) coverage=new int[Grid.CellCount];
            // A paged cell may omit a moving pile purely because its ID is on another page.
            // Only a complete cell is evidence against every older motion sample there.
            if(view.PileFrom==0 && view.PileThrough==int.MaxValue)
            for(int i=0;i<view.Cells.Length;i++)
            {
                if(!view.CoversPiles(i)) continue;
                int cell=Grid.XYToCell(view.X+i%ColonyView.Width,view.Y+i/ColonyView.Width);
                if(Grid.IsValidCell(cell)) coverage[cell]=Math.Max(coverage[cell],view.DupePoseTick);
            }
            foreach(var pile in view.Piles)
                if(lastTick.TryGetValue(pile.SaveId,out int seen))
                    lastTick[pile.SaveId]=Math.Max(seen,view.DupePoseTick);
            expired.Clear();
            foreach(var pair in lastTick) if(view.DupePoseTick-pair.Value>1200) expired.Add(pair.Key);
            foreach(int id in expired) lastTick.Remove(id);
        }
        internal static void Reset(){flying.Clear();lastTick.Clear();expired.Clear();coverage=null;}
    }
}
