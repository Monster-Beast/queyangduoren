using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Packet = Queyang.Core.Wire;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        sealed class ScheduleId { internal int Value = checked(++nextId); }
        sealed class ScheduleRow
        {
            internal int Id;
            internal string Name;
            internal bool Alarm, Bionic;
            internal byte[] Blocks;
        }
        const int RowsPerPage = 2, MaxSchedules = 2048;
        static int nextId, nextSchedulePage;
        static readonly ConditionalWeakTable<Schedule,ScheduleId> scheduleIds = new ConditionalWeakTable<Schedule,ScheduleId>();
        static readonly Dictionary<int,Schedule> guestSchedules = new Dictionary<int,Schedule>();
        static readonly List<ScheduleRow> scheduleRows = new List<ScheduleRow>();
        static readonly FieldInfo Notifications = AccessTools.DeclaredField(typeof(NotificationManager),"notifications");
        static readonly MethodInfo ScheduleChanged = AccessTools.DeclaredMethod(typeof(Schedule),"Changed");
        static readonly MethodInfo RefreshSchedules = AccessTools.DeclaredMethod(typeof(ScheduleScreen),"OnSchedulesChanged");
        static string catalogueVersion;
        internal static bool OverviewOpen;
        internal static string OverviewText;

        static void ResetColony()
        {
            guestSchedules.Clear(); scheduleRows.Clear(); nextSchedulePage=0; catalogueVersion=null;
            OverviewOpen=false; OverviewText=null;
        }

        static int StableId(Schedule s) => scheduleIds.GetValue(s,_=>new ScheduleId()).Value;

        static string SchedulePage(int page)
        {
            var list=ScheduleManager.Instance.GetSchedules();
            if (list.Count < 1 || list.Count > MaxSchedules || page < 0) return Conflict;
            if (page > (list.Count-1)/RowsPerPage) page=0;
            string version=ScheduleCatalogueVersion(list);
            var groups=Db.Get().ScheduleGroups.allGroups;
            using (var s=new MemoryStream())
            using (var w=new BinaryWriter(s))
            {
                w.Write((ushort)list.Count); w.Write((ushort)page);
                foreach (var item in list.Skip(page*RowsPerPage).Take(RowsPerPage))
                {
                    var blocks=item.GetBlocks();
                    if (blocks.Count<1 || blocks.Count>2048 || System.Text.Encoding.UTF8.GetByteCount(item.name ?? "")>512) return Conflict;
                    w.Write(StableId(item)); Packet.Text(w,item.name ?? "",512);
                    w.Write(item.alarmActivated); w.Write(item.isDefaultForBionics);
                    w.Write((ushort)blocks.Count);
                    foreach (var block in blocks)
                    {
                        int i=groups.FindIndex(g=>g.Id==block.GroupId);
                        if ((uint)i>=255) return Conflict;
                        w.Write((byte)(i+1));
                    }
                }
                return Prefix+"sl/"+version+"/"+Convert.ToBase64String(s.ToArray()).Replace('/','_');
            }
        }

        internal static void ApplySchedulePage(string version,string data)
        {
            var groups=Db.Get().ScheduleGroups.allGroups;
            using (var s=new MemoryStream(Convert.FromBase64String(data.Replace('_','/'))))
            using (var r=new BinaryReader(s))
            {
                int total=r.ReadUInt16(), page=r.ReadUInt16();
                if (total<1 || total>MaxSchedules || page>(total-1)/RowsPerPage) throw new InvalidDataException("Invalid schedule catalogue.");
                if (page==0) { scheduleRows.Clear(); nextSchedulePage=0; catalogueVersion=version; }
                if (page!=nextSchedulePage || version!=catalogueVersion)
                { nextSchedulePage=0; scheduleRows.Clear(); return; }
                for (int i=page*RowsPerPage;i<Math.Min(total,(page+1)*RowsPerPage);++i)
                {
                    var row=new ScheduleRow { Id=r.ReadInt32(), Name=Packet.Text(r,512), Alarm=r.ReadBoolean(), Bionic=r.ReadBoolean() };
                    int count=r.ReadUInt16();
                    if (row.Id<1 || count<1 || count>2048) throw new InvalidDataException("Invalid schedule metadata.");
                    row.Blocks=Packet.Bytes(r,count);
                    if (row.Blocks.Any(g=>g==0 || g>groups.Count)) throw new InvalidDataException("Invalid schedule group.");
                    scheduleRows.Add(row);
                }
                Packet.End(r);
                if (scheduleRows.Count==total)
                {
                    if (scheduleRows.Select(x=>x.Id).Distinct().Count()!=total) throw new InvalidDataException("Duplicate schedule identity.");
                    ReconcileSchedules(groups);
                    nextSchedulePage=0; scheduleRows.Clear();
                }
                else nextSchedulePage=page+1;
            }
        }

        static void ReconcileSchedules(List<ScheduleGroup> groups)
        {
            var list=ScheduleManager.Instance.GetSchedules();
            var available=new HashSet<Schedule>(list);
            var current=new Dictionary<int,Schedule>();
            var ordered=new List<Schedule>();
            bool changed=false;
            foreach (var row in scheduleRows)
            {
                if (!guestSchedules.TryGetValue(row.Id,out var item) || !available.Contains(item))
                {
                    item=list.FirstOrDefault(s=>available.Contains(s) && s.name==row.Name && !guestSchedules.ContainsValue(s));
                    if (item==null) item=new Schedule(row.Name,groups,row.Bionic);
                }
                available.Remove(item); ordered.Add(item); current.Add(row.Id,item);
                bool dirty=item.name!=row.Name || item.alarmActivated!=row.Alarm || item.isDefaultForBionics!=row.Bionic;
                item.name=row.Name; item.alarmActivated=row.Alarm; item.isDefaultForBionics=row.Bionic;
                var blocks=item.GetBlocks();
                bool resized=blocks.Count!=row.Blocks.Length;
                if (resized)
                {
                    blocks.Clear();
                    foreach (byte id in row.Blocks) { var g=groups[id-1]; blocks.Add(new ScheduleBlock(g.Name,g.Id)); }
                    item.ProgressTimetableIdx=0; dirty=true;
                }
                else
                    for (int i=0;i<blocks.Count;++i)
                        if (blocks[i].GroupId!=groups[row.Blocks[i]-1].Id)
                        {
                            var g=groups[row.Blocks[i]-1]; blocks[i]=new ScheduleBlock(g.Name,g.Id); dirty=true;
                        }
                if (dirty)
                {
                    item.ClearNullReferences();
                    // Old row controls still hold indices from before resizing.
                    if (!resized) ScheduleChanged.Invoke(item,null);
                    changed=true;
                }
            }
            foreach (var removed in available)
                foreach (var reference in removed.GetAssigned().ToArray())
                {
                    var person=reference.Get();
                    if (person!=null) { removed.Unassign(person); ordered[0].Assign(person); }
                }
            if (!list.SequenceEqual(ordered)) { list.Clear(); list.AddRange(ordered); changed=true; }
            guestSchedules.Clear();
            foreach (var entry in current) guestSchedules.Add(entry.Key,entry.Value);
            if (changed && ScheduleScreen.Instance!=null)
                RefreshSchedules.Invoke(ScheduleScreen.Instance,new object[]{list});
        }

        internal static string ColonyOverview()
        {
            var inventory=ClusterManager.Instance?.activeWorld?.worldInventory;
            var tracker=RationTracker.Get();
            if (inventory==null || tracker==null) return Conflict;
            var calories=tracker.CountAmount(null,inventory,false);
            var alerts=NotificationManager.Instance==null ? null : Notifications.GetValue(NotificationManager.Instance) as List<Notification>;
            var groups=(alerts ?? new List<Notification>()).Where(n=>n!=null &&
                (n.Type==NotificationType.Bad || n.Type==NotificationType.BadMinor || n.Type==NotificationType.DuplicantThreatening))
                .GroupBy(n=>n.titleText ?? "").OrderBy(g=>g.Key,StringComparer.Ordinal).ToArray();
            using (var s=new MemoryStream())
            using (var w=new BinaryWriter(s))
            {
                w.Write(calories); w.Write(groups.Sum(g=>g.Count())); w.Write((byte)Math.Min(8,groups.Length));
                foreach (var group in groups.Take(8))
                {
                    string title=group.Key;
                    if (title.Length>96) title=title.Substring(0,char.IsHighSurrogate(title[95]) ? 95 : 96);
                    Packet.Text(w,title,384); w.Write(group.Count());
                }
                return Prefix+"ov/"+Convert.ToBase64String(s.ToArray()).Replace('/','_');
            }
        }

        internal static void ApplyOverview(string data)
        {
            using (var s=new MemoryStream(Convert.FromBase64String(data.Replace('_','/'))))
            using (var r=new BinaryReader(s))
            {
                float calories=r.ReadSingle(); int total=r.ReadInt32(), displayed=r.ReadByte();
                if (float.IsNaN(calories) || float.IsInfinity(calories) || calories<0 || total<0 || displayed>8)
                    throw new InvalidDataException("Invalid colony summary.");
                var lines=new List<string> { "房主当前世界食物  "+GameUtil.GetFormattedCalories(calories), "殖民地警报  "+total };
                for (int i=0;i<displayed;i++) { string title=Packet.Text(r,384); int count=r.ReadInt32();
                    if (count<1 || count>total) throw new InvalidDataException("Invalid notification count.");
                    lines.Add(title+" ×"+count); }
                Packet.End(r); OverviewText=string.Join("\n",lines);
            }
        }
    }
}
