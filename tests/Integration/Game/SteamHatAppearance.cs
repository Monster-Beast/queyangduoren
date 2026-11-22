using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamHatAppearance
    {
        int stage, opened=-1, applied=-1;
        FixtureFrame frame;
        Vector3 before; float zoom;
        bool moved;
        string stableSignature;
        float stableAt,nextProgress;
        static string Value(CSteamID lobby,string key)=>SteamMatchmaking.GetLobbyData(lobby,key);
        static void Set(CSteamID lobby,string key,string value)
        { if (!SteamMatchmaking.SetLobbyData(lobby,key,value)) throw new IOException("Could not publish hat evidence"); }
        static string Signature(KBatchedAnimController a)
        {
            var s=Db.Get().AccessorySlots; var accessories=a.GetComponent<Accessorizer>();
            var symbols=a.GetComponent<SymbolOverrideController>().GetSymbolOverrides;
            string Entry(AccessorySlot slot)
            {
                var active=symbols.Where(v=>v.targetSymbol.HashValue==slot.targetSymbolId.HashValue && v.sourceSymbol!=null)
                    .OrderByDescending(v=>v.priority).FirstOrDefault();
                return (accessories.GetAccessory(slot)?.Id??"-")+":"+(a.GetSymbolVisiblity(slot.targetSymbolId)?"1":"0")+":"+(active.sourceSymbol?.hash.HashValue??0);
            }
            return string.Join("|",new[]{Entry(s.Hat),Entry(s.Hair),Entry(s.HatHair)});
        }
        internal bool Check(CSteamID lobby,bool host,int round,int original,int newcomer,Action<string> log)
        {
            if (round!=1 || stage>=8) return true;
            int id=stage<4 ? original : newcomer;
            string role=host?"host":"guest", key="qy_hats_"+stage;
            var identity=host ? Components.LiveMinionIdentities.Items.FirstOrDefault(m=>m!=null && m.GetComponent<KPrefabID>().InstanceID==id) : null;
            var a=host ? identity?.GetComponent<KBatchedAnimController>() : NativeWorldMirror.GetDupeSelectable(id)?.GetComponent<KBatchedAnimController>();
            if (a==null) return false;
            string first=Value(lobby,"qy_person_hat");
            int change=stage%4;
            var camera=CameraController.Instance;
            if (Time.unscaledTime>=nextProgress)
            {
                nextProgress=Time.unscaledTime+5;
                log("DUAL_HAT_PROGRESS role="+role+" stage="+stage+" opened="+opened+" applied="+applied+
                    " expected="+Value(lobby,key)+" actual="+Signature(a)+" captured="+(frame!=null));
            }
            if (!moved) { before=camera.transform.position; zoom=camera.OrthographicSize; moved=true; }
            if (host && applied!=stage)
            {
                if (Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT")) throw new InvalidOperationException("Hat changes require the isolated test colony");
                var resume=identity.GetComponent<MinionResume>();
                var alternate=Db.Get().Skills.resources.First(s=>!s.deprecated && !string.IsNullOrEmpty(s.hat) && s.hat!=first &&
                    Db.Get().AccessorySlots.Hat.Lookup(s.hat)!=null);
                string want=change==2 ? null : change==1 ? alternate.hat : first;
                if (opened!=stage)
                {
                    if (change==1) resume.AddAdditionalHat("QA",alternate.hat);
                    if (!ManagementMenu.Instance.OpenSkills(identity)) return false;
                    opened=stage; return false;
                }
                var screen=(SkillsScreen)AccessTools.Field(typeof(ManagementMenu),"skillsScreen").GetValue(ManagementMenu.Instance);
                if (!screen.IsScreenActive()) return false;
                var callback=AccessTools.Method(typeof(SkillsScreen),"OnHatDropEntryClick",new[]{typeof(IListableOption),typeof(object)});
                callback.Invoke(screen,new object[]{want==null ? null : new HatListable(want,want),null});
                // Complete ONI's native hat delivery on this paused fixture after the host selected it.
                if (want!=null) resume.ApplyTargetHat();
                if (resume.CurrentHat!=want || resume.TargetHat!=null ||
                    a.GetComponent<Accessorizer>().GetAccessory(Db.Get().AccessorySlots.Hat)?.Id!=want)
                    throw new InvalidDataException("Host equipped accessory differs from selected physical hat");
                if (change==3) resume.RemoveAdditionalHat("QA",alternate.hat);
                ManagementMenu.Instance.CloseAll();
                applied=stage;stableSignature=null;
            }
            string expected=Value(lobby,key);
            string current=Signature(a);
            if (host)
            {
                // Accessorizer installs the new accessory's batch overrides on a later Unity frame.
                if (stableSignature!=current) { stableSignature=current;stableAt=Time.unscaledTime;return false; }
                if (Time.unscaledTime-stableAt<.4f) return false;
                if (expected!=current) { Set(lobby,key,current);return false; }
            }
            if (string.IsNullOrEmpty(expected) || expected!=current) return false;
            var pos=a.transform.position;
            camera.SetPosition(new Vector3(pos.x,pos.y+.8f,camera.transform.position.z));
            camera.OrthographicSize=4f;
            if (frame==null)
            {
                frame=new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),
                    "hat-"+role+"-"+stage+".png"));
                frame.Capture(); return false;
            }
            if (!frame.Complete) return false;
            var peer=SteamMatchmaking.GetLobbyMemberByIndex(lobby,0);
            if (peer==SteamUser.GetSteamID()) peer=SteamMatchmaking.GetLobbyMemberByIndex(lobby,1);
            if (host && SteamMatchmaking.GetLobbyMemberData(lobby,peer,"qy_hat_seen")!=stage.ToString()) return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(lobby,"qy_hat_seen",stage.ToString());
            log("DUAL_HAT_APPEARANCE role="+role+" stage="+stage+" id="+id+" symbols="+expected);
            stage++; frame=null;
            if (stage==8) { camera.SetTargetPos(before,zoom,false);camera.SetPosition(before);camera.OrthographicSize=zoom; }
            return stage==8;
        }
    }
}
