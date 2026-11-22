using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Packet=Queyang.Core.Wire;

namespace Queyang.Game
{
    // ONI already has a UI identity for a person without a live simulation actor.
    // Keep it separate from the world renderer and route edits using the host save ID.
    internal sealed class GuestWorkerCard : MonoBehaviour
    {
        internal int HostId;
        internal readonly Dictionary<HashedString,int> Levels=new Dictionary<HashedString,int>();
        internal readonly HashSet<HashedString> Disabled=new HashSet<HashedString>();
    }

    internal static partial class NativeManagement
    {
        static readonly Dictionary<int,GuestWorkerCard> workerCards=new Dictionary<int,GuestWorkerCard>();
        static readonly HashSet<int> seenWorkers=new HashSet<int>();
        static readonly System.Reflection.MethodInfo addWorkerRow=AccessTools.Method(typeof(TableScreen),"AddRow",new[]{typeof(IAssignableIdentity)});
        static readonly System.Reflection.MethodInfo dirtyTable=AccessTools.Method(typeof(TableScreen),"MarkRowsDirty",Type.EmptyTypes);
        static int workerPage;
        static string workerVersion;

        static string WorkerPage(int page)
        {
            var live=Components.LiveMinionIdentities.Items.Where(m=>m!=null).OrderByDescending(Id).ToList();
            if (page<0 || live.Count>65535) return Conflict;
            if (page>=live.Count) page=0;
            string version=Digest(string.Join(",",live.Select(Id)));
            string header=Prefix+"workers/"+version+"/"+page+"/"+live.Count+"/";
            if (live.Count==0) return header;
            var d=live[page]; var consumer=d.GetComponent<ChoreConsumer>();
            var appearance=d.GetComponent<Accessorizer>();
            var s=d.GetComponent<Schedulable>()?.GetSchedule();
            if (consumer==null || appearance==null) return Conflict;
            var jobs=Jobs(); var slots=appearance.GetAccessories().Select(r=>r.Get()).Where(a=>a!=null).ToArray();
            if (jobs.Count>255 || slots.Length>128) return Conflict;
            using (var buf=new MemoryStream())
            using (var w=new BinaryWriter(buf))
            {
                w.Write(Id(d));w.Write(d.personalityResourceId.HashValue);w.Write(Grid.PosToCell(d));
                Packet.Text(w,DisplayText(d.GetProperName()),512);
                w.Write(s==null?0:StableId(s));w.Write((byte)jobs.Count);
                foreach(var job in jobs)
                {
                    w.Write((byte)(consumer.GetPersonalPriority(job)|(consumer.IsChoreGroupDisabled(job)?8:0)));
                    w.Write(consumer.GetAssociatedSkillLevel(job));
                }
                w.Write((byte)slots.Length);
                foreach(var a in slots) w.Write(a.IdHash.HashValue);
                return header+Convert.ToBase64String(buf.ToArray()).Replace('/','_');
            }
        }

        static void MarkJobs()
        {
            var jobs=ManagementMenu.Instance?.jobsScreen;
            if (jobs!=null && jobs.isActiveAndEnabled) dirtyTable.Invoke(jobs,null);
        }
        static void RetireWorker(GuestWorkerCard c)
        {
            if (c==null) return;
            var stored=c.GetComponent<StoredMinionIdentity>();
            if (ScheduleManager.Instance!=null && stored!=null && stored.isSpawned) stored.OnHardDelete();
            Util.KDestroyGameObject(c.gameObject);
        }
        static void ResetWorkers()
        {
            foreach(var c in workerCards.Values) RetireWorker(c);
            workerCards.Clear(); seenWorkers.Clear(); workerPage=0; workerVersion=null;
        }

        static void ApplyWorkerPage(string[] parts)
        {
            int page=Number(parts[3]),count=Number(parts[4]);
            if (page<0 || count<0 || count>65535 || page>=Math.Max(1,count) || parts[5].Length>4000) return;
            if (page==0) { seenWorkers.Clear(); workerVersion=parts[2]; workerPage=0; }
            if (page!=workerPage || parts[2]!=workerVersion) { workerPage=0;return; }
            if (count>0)
            {
                using(var r=new BinaryReader(new MemoryStream(Convert.FromBase64String(parts[5].Replace('_','/')))))
                {
                    int id=r.ReadInt32();
                    if (id<=0 || !seenWorkers.Add(id)) { workerPage=0;return; }
                    if (FindDupe(id)==null) ReadWorker(r,id);
                }
            }
            if (++workerPage<count) return;
            workerPage=0;
            foreach(int id in workerCards.Keys.Where(i=>!seenWorkers.Contains(i)).ToArray())
            {
                RetireWorker(workerCards[id]); workerCards.Remove(id); MarkJobs();
            }
        }

        static void ReadWorker(BinaryReader r,int id)
        {
            var personality=Db.Get().Personalities.TryGet(new HashedString(r.ReadInt32()));
            int cell=r.ReadInt32();string name=Packet.Text(r,512); int schedule=r.ReadInt32();
            var jobs=Jobs();
            if (r.ReadByte()!=jobs.Count || !Grid.IsValidCell(cell) || personality==null || schedule<0) return;
            var priorities=new char[jobs.Count]; var levels=new int[jobs.Count]; var disabled=new bool[jobs.Count];
            for (int i=0;i<jobs.Count;i++)
            {
                int value=r.ReadByte();
                if ((value&7)>5 || value>13) return;
                priorities[i]=(char)('0'+(value&7));disabled[i]=(value&8)!=0;levels[i]=r.ReadInt32();
            }
            int n=r.ReadByte(); if(n>128) return;
            var assets=new List<ResourceRef<Accessory>>();
            for(int i=0;i<n;i++)
            {
                var a=Db.Get().Accessories.TryGet(new HashedString(r.ReadInt32()));
                if(a!=null) assets.Add(new ResourceRef<Accessory>(a));
            }
            Packet.End(r);
            bool created=!workerCards.TryGetValue(id,out var card) || card==null;
            if (created)
            {
                var obj=Util.KInstantiate(Assets.GetPrefab(StoredMinionConfig.ID),Grid.CellToPosCBC(cell,Grid.SceneLayer.Move));
                card=obj.AddComponent<GuestWorkerCard>();card.HostId=id;workerCards[id]=card;
                var stored=obj.GetComponent<StoredMinionIdentity>();
                stored.personalityResourceId=personality.IdHash;stored.model=personality.model;
                stored.bodyData=MinionStartingStats.CreateBodyData(personality);
                stored.traitIDs=new List<string>(); stored.accessories=assets;stored.storedName=name;
                obj.SetActive(true);
            }
            card.transform.position=Grid.CellToPosCBC(cell,Grid.SceneLayer.Move);
            card.GetComponent<StoredMinionIdentity>().storedName=name;
            card.Disabled.Clear();
            for (int i=0;i<jobs.Count;i++)
            {
                if (disabled[i]) card.Disabled.Add(jobs[i].IdHash);
                card.Levels[jobs[i].IdHash]=levels[i];
            }
            UpdateWorker(card,"i"+schedule,jobs,new string(priorities));
            if (created) MarkJobs();
        }

        static void UpdateWorker(GuestWorkerCard card,string key,List<ChoreGroup> jobs,string priorities)
        {
            var who=card.GetComponent<Schedulable>();var schedule=FindSchedule(key);
            if (schedule!=null && who.GetSchedule()!=schedule) { who.GetSchedule()?.Unassign(who);schedule.Assign(who); }
            var stored=card.GetComponent<StoredMinionIdentity>();
            bool dirty=false;
            for(int i=0;i<jobs.Count;i++)
                if(stored.GetPersonalPriority(jobs[i])!=priorities[i]-'0')
                { stored.SetPersonalPriority(jobs[i],priorities[i]-'0'); dirty=true; }
            if (dirty) MarkJobs();
        }

        [HarmonyPatch(typeof(TableScreen),"ObsoleteRows",new Type[0])]
        static class AddJobsWorkers
        {
            static void Postfix(TableScreen __instance)
            {
                if (!NativeGuestScene.Ready || !(__instance is JobsTableScreen)) return;
                foreach (var card in workerCards.Values)
                    if (card!=null) addWorkerRow.Invoke(__instance,new object[]{card.GetComponent<StoredMinionIdentity>()});
            }
        }

        [HarmonyPatch(typeof(StoredMinionIdentity),nameof(StoredMinionIdentity.SetPersonalPriority))]
        static class EditWorkerPriority
        {
            static bool Prefix(StoredMinionIdentity __instance,ChoreGroup __0,int __1)
            {
                var card=__instance.GetComponent<GuestWorkerCard>();
                if (card==null || !Guard) return true;
                if (__0!=null) Send("priority/"+card.HostId+"/"+__0.Id+"/"+__instance.GetPersonalPriority(__0)+"/"+__1);
                return false;
            }
        }
        [HarmonyPatch(typeof(StoredMinionIdentity),nameof(StoredMinionIdentity.IsChoreGroupDisabled))]
        static class DisabledWork
        {
            static bool Prefix(StoredMinionIdentity __instance,ChoreGroup __0,ref bool __result)
            {
                var card=__instance.GetComponent<GuestWorkerCard>(); if (card==null || __0==null) return true;
                __result=card.Disabled.Contains(__0.IdHash);return false;
            }
        }
        [HarmonyPatch(typeof(StoredMinionIdentity),nameof(StoredMinionIdentity.GetAssociatedSkillLevel))]
        static class WorkerLevel
        {
            static bool Prefix(StoredMinionIdentity __instance,ChoreGroup __0,ref int __result)
            {
                var card=__instance.GetComponent<GuestWorkerCard>(); if (card==null || __0==null) return true;
                card.Levels.TryGetValue(__0.IdHash,out __result);return false;
            }
        }
    }
}
