using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Leave a second guest designation pending on the paused host. After reconnect,
    // the same guest badge must coexist with a new, otherwise identical host dig.
    internal sealed class SteamDualOwnership
    {
        int pending = Grid.InvalidCell;
        bool issued;
        bool legacyVerified;
        int legacy=Grid.InvalidCell;
        readonly Dictionary<int,int> oldDigIds=new Dictionary<int,int>();
        string firstGuestColor;
        static SteamDualOwnership hovering, running;
        readonly Dictionary<long,int> repaints=new Dictionary<long,int>();
        internal SteamDualOwnership() { running=this; }
        static long DrawKey(int cell,int author) => ((long)author<<32)|(uint)cell;
        static int LastDrawFrame(int cell,int author) =>
            running!=null && running.repaints.TryGetValue(DrawKey(cell,author),out int frame) ? frame : -1;
        readonly Rect[] badge = new Rect[3];
        int probeHost=Grid.InvalidCell, probeAuthor=1, lastTooltipFrame=-10;
        bool paired;
        readonly Dictionary<string,float> nextDiagnostic=new Dictionary<string,float>();
        static bool Wait(string reason)
        {
            var owner=running;
            if (owner!=null && (!owner.nextDiagnostic.TryGetValue(reason,out float when) || Time.realtimeSinceStartup>=when))
            {
                owner.nextDiagnostic[reason]=Time.realtimeSinceStartup+15;
                var report=Environment.GetEnvironmentVariable("QY_SMOKE_REPORT");
                if (!string.IsNullOrEmpty(report)) File.AppendAllText(report,"DUAL_OWNERS_WAIT "+reason+Environment.NewLine);
            }
            return false;
        }

        // Move only the test's IMGUI pointer over the last real badge rectangle;
        // the production hit test and actual DrawTooltip repaint still run.
        [HarmonyPatch(typeof(OrderOwnership),"DrawBadge")]
        static class BadgeWitness
        {
            static void Postfix(int __0,int __1,Rect __2)
            {
                if (running==null || Event.current.type!=EventType.Repaint || __1<1 || __1>2 || __2.width<=0 || __2.height<=0) return;
                running.repaints[DrawKey(__0,__1)]=Time.frameCount;
                var t=hovering;
                if (t==null) return;
                if (__0==(__1==1?t.probeHost:t.pending) && __2.width>0 && __2.height>0) t.badge[__1]=__2;
            }
        }
        [HarmonyPatch(typeof(OrderOwnership),"MousePosition")]
        static class HoverPointer
        {
            static bool Prefix(ref Vector2 __result)
            {
                var t=hovering;
                if (t==null || t.probeAuthor>2 || t.badge[t.probeAuthor].width<=0) return true;
                __result=t.badge[t.probeAuthor].center;
                return false;
            }
        }
        [HarmonyPatch(typeof(OrderOwnership),"DrawTooltip")]
        static class TooltipWitness
        {
            static void Postfix(int __0,string __1,Rect __2)
            {
                var t=hovering;
                if (t==null || t.probeAuthor>2 || Event.current.type!=EventType.Repaint || __2.width<=0 || __2.height<=0) return;
                int target=t.probeAuthor==1?t.probeHost:t.pending;
                if (__0!=target || string.IsNullOrEmpty(__1) || !__1.Contains(OrderOwnership.Label(t.probeAuthor))) return;
                t.lastTooltipFrame=Time.frameCount;
                t.probeAuthor++;
            }
        }
        static string Color(int author) => ColorUtility.ToHtmlStringRGBA(OrderOwnership.ColorFor(author));
        static bool Visible(int cell, int author)
        {
            var marks = OrderOwnership.At(cell).Where(mark => mark.Kind == OrderKind.Dig).ToList();
            if (marks.Count == 0) return Wait("missing-mark cell="+cell+" author="+author);
            if (marks.Count != 1 || marks[0].Author != author)
                throw new InvalidDataException("Wrong author or duplicate ownership on dig cell " + cell);
            int last = LastDrawFrame(cell, author);
            return (last >= 0 && Time.frameCount - last <= 3) || Wait("not-painted cell="+cell+" author="+author);
        }
        internal void PrepareLegacy(int anchor,Action<string> log)
        {
            Grid.CellToXY(anchor,out int x,out int y);
            int found=Grid.InvalidCell, distance=int.MaxValue;
            for (int dx=-7;dx<=7;dx++)
                for (int dy=-5;dy<=5;dy++)
                {
                    if (x+dx<0 || y+dy<0 || x+dx>=Grid.WidthInCells || y+dy>=Grid.HeightInCells) continue;
                    int c=Grid.XYToCell(x+dx,y+dy), d=Math.Abs(dx)+Math.Abs(dy);
                    if (d>=distance || c==anchor || Grid.Visible[c]==0 || Diggable.GetDiggable(c)==null) continue;
                    found=c; distance=d;
                }
            if (found==Grid.InvalidCell)
            {
                if (CoopSession.IsHosting || CoopSession.IsClient) throw new InvalidOperationException("Legacy preparation must precede room creation");
                for (int dx=-6;dx<=6 && found==Grid.InvalidCell;dx++)
                    for (int dy=-4;dy<=4;dy++)
                    {
                        if (x+dx<0 || y+dy<0 || x+dx>=Grid.WidthInCells || y+dy>=Grid.HeightInCells) continue;
                        int c=Grid.XYToCell(x+dx,y+dy);
                        if (c==anchor || Grid.Visible[c]==0 || !Grid.Solid[c] || !Diggable.IsDiggable(c) ||
                            Diggable.Undiggable(Grid.Element[c]) || HostWorld.FindBuilding(c)!=null) continue;
                        PlayerController.Instance.ActivateTool(DigTool.Instance);
                        var p=Grid.CellToPosCCC(c,Grid.SceneLayer.Move);
                        DigTool.Instance.OnLeftClickDown(p); DigTool.Instance.OnLeftClickUp(p);
                        if (Diggable.GetDiggable(c)!=null) { found=c; break; }
                    }
                if (found==Grid.InvalidCell) throw new InvalidDataException("The isolated colony has no nearby place for a pre-session vanilla order");
                log("DUAL_LEGACY_SOURCE isolatedVanillaToolBeforeRoom=True");
            }
            else log("DUAL_LEGACY_SOURCE existingInputSave=True");
            legacy=found;
            // The particular nearby order used to inspect the old baseline may be
            // completed by another worker while the colony performs the guest dig.
            foreach (var dig in UnityEngine.Object.FindObjectsByType<Diggable>(FindObjectsSortMode.None))
            {
                if (dig==null || !dig.gameObject.activeInHierarchy) continue;
                int c=Grid.PosToCell(dig);
                if (Grid.IsValidCell(c) && Diggable.GetDiggable(c)==dig)
                    oldDigIds[c]=dig.GetComponent<KPrefabID>().InstanceID;
            }
        }
        internal void PublishLegacy(CSteamID lobby)
        {
            if (!SteamMatchmaking.SetLobbyData(lobby,"qy_owner_legacy",legacy.ToString(CultureInfo.InvariantCulture)))
                throw new IOException("Could not share the legacy dig coordinate");
        }
        internal bool Legacy(CSteamID lobby,bool isHost,Action<string> log)
        {
            if (legacyVerified) return true;
            if (!int.TryParse(SteamMatchmaking.GetLobbyData(lobby,"qy_owner_legacy"),out int old) || !Grid.IsValidCell(old)) return false;
            if (isHost ? Diggable.GetDiggable(old)==null : !NativeWorldMirror.IsDigVisible(old)) return false;
            if (OrderOwnership.At(old).Any(mark=>mark.Kind==OrderKind.Dig))
                throw new InvalidDataException("An existing dig in the original save was incorrectly attributed to a player");
            legacyVerified=true;
            log("DUAL_LEGACY_ORDER existingDigVisible=True inventedOwner=False cell="+old);
            return true;
        }
        internal bool Initial(int cell)
        {
            if (!Visible(cell, 2)) return false;
            firstGuestColor = Color(2);
            return true;
        }
        bool Removed(int cell) => !OrderOwnership.At(cell).Any(mark => mark.Kind == OrderKind.Dig) &&
            (LastDrawFrame(cell,2) < 0 || Time.frameCount - LastDrawFrame(cell,2) > 3);
        internal void PrepareHost(CSteamID lobby, Func<int> choose)
        {
            if (pending != Grid.InvalidCell) return;
            SpeedControlScreen.Instance.Pause(false);
            pending = choose();
            if (!SteamMatchmaking.SetLobbyData(lobby, "qy_owner_pending", pending.ToString(CultureInfo.InvariantCulture)))
                throw new IOException("Could not publish the second guest dig coordinate");
        }
        internal bool IssueGuest(CSteamID lobby,int completed)
        {
            if (!Removed(completed)) return Wait("completed-still-visible");
            if (!issued)
            {
                if (!int.TryParse(SteamMatchmaking.GetLobbyData(lobby,"qy_owner_pending"),out pending)) return false;
                if (!Grid.IsValidCell(pending) || pending==completed || !Grid.Solid[pending] ||
                    !Diggable.IsDiggable(pending) || NativeWorldMirror.IsDigVisible(pending))
                    throw new InvalidDataException("Expected an empty visible dig site for the retained guest command");
                PlayerController.Instance.ActivateTool(DigTool.Instance);
                var p=Grid.CellToPosCCC(pending,Grid.SceneLayer.Move);
                DigTool.Instance.OnLeftClickDown(p); DigTool.Instance.OnLeftClickUp(p);
                issued=true;
            }
            return NativeWorldMirror.IsDigVisible(pending) && Visible(pending,2);
        }
        internal void SaveEvidence(int hostCell,int completed,Action<string> log)
        {
            int survivor=Grid.InvalidCell;
            foreach (var entry in oldDigIds)
            {
                var dig=Diggable.GetDiggable(entry.Key);
                if (dig==null || dig.GetComponent<KPrefabID>().InstanceID!=entry.Value) continue;
                if (OrderOwnership.At(entry.Key).Any(m=>m.Kind==OrderKind.Dig))
                    throw new InvalidDataException("A surviving legacy order acquired a spurious author");
                survivor=entry.Key;
            }
            if (survivor==Grid.InvalidCell) throw new InvalidDataException("All original native digs were completed before saving");
            HostPersistenceSave.Run(log);
            string directory=Path.Combine(Util.GetKleiRootPath(),"OxygenNotIncluded","save_files","QyPersistence");
            File.WriteAllLines(Path.Combine(directory,"owners.txt"),new[]{hostCell,pending,completed,survivor}.Select(c=>c.ToString(CultureInfo.InvariantCulture)).ToArray());
        }
        internal bool Retained(int completed,bool isHost)
        {
            if (!Removed(completed) || pending==Grid.InvalidCell || !Visible(pending,2)) return false;
            if (firstGuestColor!=Color(2) || (isHost ? Diggable.GetDiggable(pending)==null : !NativeWorldMirror.IsDigVisible(pending)))
                throw new InvalidDataException("The retained dig lost its guest color or order");
            return true;
        }
        internal bool Pair(CSteamID lobby,int hostCell,int completed,bool isHost,Action<string> log)
        {
            if (paired) return true;
            if (!Retained(completed,isHost) || !Visible(hostCell,1)) return false;
            if (hostCell==pending || Color(1)==Color(2) || OrderOwnership.Label(1)==OrderOwnership.Label(2) ||
                string.IsNullOrWhiteSpace(OrderOwnership.Label(1)) || string.IsNullOrWhiteSpace(OrderOwnership.Label(2)))
                throw new InvalidDataException("Expected two distinguishable author badges and labels");
            var friend=new CSteamID(ulong.Parse(Environment.GetEnvironmentVariable("QY_DUAL_FRIEND"),CultureInfo.InvariantCulture));
            string me=ColonyView.CleanOrderName(SteamFriends.GetPersonaName());
            string peer=ColonyView.CleanOrderName(SteamFriends.GetFriendPersonaName(friend));
            if (string.IsNullOrEmpty(me) || string.IsNullOrEmpty(peer) ||
                !OrderOwnership.Label(isHost?1:2).EndsWith(" "+me,StringComparison.Ordinal) ||
                !OrderOwnership.Label(isHost?2:1).EndsWith(" "+peer,StringComparison.Ordinal)) return Wait("steam-name-not-ready");
            probeHost=hostCell;
            if (probeAuthor<=2) { hovering=this; return Wait("hovering author="+probeAuthor); }
            hovering=null;
            if (Time.frameCount-lastTooltipFrame<3) return false;
            string signature=Color(1)+"/"+Color(2);
            if (isHost)
            {
                if (!SteamMatchmaking.SetLobbyData(lobby,"qy_owner_colors",signature))
                    throw new IOException("Could not publish host badge colors");
            }
            else
            {
                string remote=SteamMatchmaking.GetLobbyData(lobby,"qy_owner_colors");
                if (string.IsNullOrEmpty(remote)) return false;
                if (remote!=signature) throw new InvalidDataException("Badge colors disagree across the two games");
            }
            log("DUAL_ORDER_OWNERS sameKind=Dig hostSlot=1 guestSlot=2 colors="+signature+
                " bothRepainted=True rejoinRetained=True completedRemoved=True namesDistinct=True steamNamesMatched=True bothTooltipsRepainted=True");
            paired=true;
            return true;
        }
    }
}
