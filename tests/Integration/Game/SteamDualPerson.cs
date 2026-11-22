using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Use the actual selected person's ONI buttons on a visual-only newcomer, and
    // the native consumption table for an original duplicant. Only the host fixture
    // supplies a patient, matching cot and XP in its private disposable colony.
    internal sealed class SteamDualPerson
    {
        GameObject printed;
        Clinic clinic;
        int step,done,loggedStep=-1;
        float lastProgress;
        string skill,hat,food;
        int id,points;
        bool originalAllowed;
        FixtureFrame screenshot;
        static readonly AccessTools.FieldRef<UserMenuScreen,List<KIconButtonMenu.ButtonInfo>> buttons=
            AccessTools.FieldRefAccess<UserMenuScreen,List<KIconButtonMenu.ButtonInfo>>("buttonInfos");
        static readonly AccessTools.FieldRef<UserMenuScreen,GameObject> menuSelection=
            AccessTools.FieldRefAccess<UserMenuScreen,GameObject>("selected");
        static readonly AccessTools.FieldRef<TableScreen,List<TableRow>> tableRows=
            AccessTools.FieldRefAccess<TableScreen,List<TableRow>>("rows");
        static readonly AccessTools.FieldRef<TableScreen,Dictionary<string,TableColumn>> tableColumns=
            AccessTools.FieldRefAccess<TableScreen,Dictionary<string,TableColumn>>("columns");
        static string Data(CSteamID lobby,string key)=>SteamMatchmaking.GetLobbyData(lobby,"qy_person_"+key);
        static void Set(CSteamID lobby,string key,string value)
        { if (!SteamMatchmaking.SetLobbyData(lobby,"qy_person_"+key,value)) throw new IOException("Steam rejected personal control metadata"); }
        static bool Early => Environment.GetEnvironmentVariable("QY_BASELINE_ONLY")=="1" || Environment.GetEnvironmentVariable("QY_SKIP_PERSON")=="1";
        static MinionIdentity Original(CSteamID lobby)
        {
            int id=int.Parse(SteamMatchmaking.GetLobbyData(lobby,"qy_skill_id"));
            return Components.LiveMinionIdentities.Items.First(d=>d!=null && d.GetComponent<KPrefabID>().InstanceID==id);
        }

        internal bool Host(CSteamID lobby,int round,Action<string> log)
        {
            if (Early || done==round) return true;
            var original=Original(lobby);
            if (round==2) { done=2; return true; }
            if (step==0)
            {
                int at=Grid.PosToCell(original); Grid.CellToXY(at,out int x,out int y);
                printed=NativePrintedDupe.Print(x,y);
                var def=Assets.GetBuildingDef(MedicalCotConfig.ID);
                clinic=Components.Clinics.Items.FirstOrDefault(c=>c!=null && c.GetComponent<Assignable>()?.assignee==null && ClusterUtil.GetMyWorldId(c)==ClusterManager.Instance.activeWorldId);
                if (clinic==null)
                {
                    int bed=Grid.InvalidCell;
                    for (int radius=1;radius<=18 && bed==Grid.InvalidCell;radius++)
                        for(int dx=-radius;dx<=radius && bed==Grid.InvalidCell;dx++)
                            for(int dy=-radius;dy<=radius;dy++)
                            {
                                if (Math.Abs(dx)+Math.Abs(dy)!=radius || x+dx<0 || y+dy<0 || x+dx>=Grid.WidthInCells || y+dy>=Grid.HeightInCells) continue;
                                int c=Grid.XYToCell(x+dx,y+dy);
                                if (Grid.Visible[c]!=0 && Grid.WorldIdx[c]==Grid.WorldIdx[at] &&
                                    def.IsValidBuildLocation(null,c,Orientation.Neutral,true,out _)) { bed=c; break; }
                            }
                    if (bed==Grid.InvalidCell) throw new InvalidDataException("The isolated test save has no suitable space for a medical cot.");
                    clinic=def.Build(bed,Orientation.Neutral,null,new[]{ElementLoader.FindElementByHash(SimHashes.SandStone).tag},295f,false,-1f).GetComponent<Clinic>();
                }
                step++; return false;
            }
            var r=printed.GetComponent<MinionResume>();
            if (!printed.GetComponent<KPrefabID>().isSpawned || !clinic.isSpawned) return false;
            if (step==1)
            {
                var hp=printed.GetComponent<Health>(); hp.Damage(hp.maxHitPoints*.85f);
                if (!clinic.GetComponent<Assignable>().CanAssignTo(printed.GetComponent<MinionIdentity>().assignableProxy.Get()))
                    throw new InvalidDataException("Clinic did not recognize the test patient as eligible");
                if (r.AvailableSkillpoints==0) r.AddExperience(MinionResume.CalculateNextExperienceBar(r.TotalSkillPointsGained)-r.TotalExperienceGained+1);
                var chosen=Db.Get().Skills.resources.First(s=>!s.deprecated && global::Game.IsCorrectDlcActiveForCurrentSave(s) &&
                    (s.requiredDuplicantModel==null || (Tag)s.requiredDuplicantModel==Db.Get().Personalities.Get(printed.GetComponent<MinionIdentity>().personalityResourceId).model) &&
                    !string.IsNullOrEmpty(s.hat) && !r.HasMasteredSkill(s.Id) && r.CanMasterSkill(r.GetSkillMasteryConditions(s.Id)));
                skill=chosen.Id; hat=chosen.hat; points=r.AvailableSkillpoints;
                var consumer=original.GetComponent<ConsumableConsumer>();
                food=EdiblesManager.GetAllLoadedFoodTypes().Cast<IConsumableUIItem>().First(f=>f.Display &&
                    ConsumerManager.instance.isDiscovered(new Tag(f.ConsumableId)) && !consumer.IsDietRestricted(f.ConsumableId)).ConsumableId;
                originalAllowed=consumer.IsPermitted(food);
                id=printed.GetComponent<KPrefabID>().InstanceID;
                Set(lobby,"skill",skill);Set(lobby,"hat",hat);Set(lobby,"food",food);Set(lobby,"allowed",originalAllowed?"1":"0");
                Grid.CellToXY(Grid.PosToCell(clinic),out int bx,out int by);Set(lobby,"bed",bx+", "+by);
                Set(lobby,"id",id.ToString());
                log("DUAL_HOST_PERSON_SETUP afterBaseline=True newId="+id+" testPatient=True cot="+bx+","+by+" skill="+skill+" points="+points+" food="+food);
                step++;return false;
            }
            if (!r.HasMasteredSkill(skill) || r.TargetHat!=hat || original.GetComponent<ConsumableConsumer>().IsPermitted(food)==originalAllowed ||
                !clinic.GetComponent<Assignable>().IsAssignedTo(printed.GetComponent<MinionIdentity>().assignableProxy.Get())) return false;
            if (r.AvailableSkillpoints!=points-1) throw new InvalidDataException("Newcomer was charged the wrong number of skill points");
            Set(lobby,"ack","1");done=1;
            log("DUAL_HOST_PERSON_CONFIRMED newMastery=True newHat=True clinicAssigned=True nativeFoodChanged=True");
            return true;
        }
        internal void Baseline(CSteamID lobby,Action<string> log)
        {
            if (Early) return;
            var newcomer=Components.LiveMinionIdentities.Items.First(d=>d!=null && d.GetComponent<KPrefabID>().InstanceID==int.Parse(Data(lobby,"id")));
            var resume=newcomer.GetComponent<MinionResume>();
            if (!resume.HasMasteredSkill(Data(lobby,"skill")) || resume.TargetHat!=Data(lobby,"hat") ||
                Original(lobby).GetComponent<ConsumableConsumer>().IsPermitted(Data(lobby,"food"))==(Data(lobby,"allowed")=="1"))
                throw new InvalidDataException("Saved newcomer skill/hat or consumption setting missing on rejoin");
            // A patient may have left after recovering while the colony ran.
            log("DUAL_GUEST_PERSON_BASELINE newNativeDuplicant=True skillAndHat=True foodPermission=True beforeFirstViewport=True");
        }
        static UserMenuScreen Menu(GameObject target) => UnityEngine.Object.FindObjectsByType<UserMenuScreen>(
            FindObjectsInactive.Include,FindObjectsSortMode.None).FirstOrDefault(m=>menuSelection(m)==target);
        static bool Click(GameObject target,Func<string,bool> label)
        {
            var menu=Menu(target);
            if (menu==null || !menu.gameObject.activeInHierarchy || DetailsScreen.Instance.target!=target) return false;
            var match=buttons(menu)?.FirstOrDefault(b=>b!=null && label(b.text));
            if (match?.onClick==null) return false;
            match.onClick(); return true;
        }
        internal bool Guest(CSteamID lobby,int round,Action<string> log)
        {
            if (Early || done==round) return true;
            if (round==2) {done=2;return true;}
            if (!int.TryParse(Data(lobby,"id"),out id)) return false;
            var original=Original(lobby);
            food=Data(lobby,"food"); originalAllowed=Data(lobby,"allowed")=="1";
            if (loggedStep!=step || Time.unscaledTime-lastProgress>8)
            {
                var screen=ManagementMenu.Instance.consumablesScreen;
                var target=NativeWorldMirror.GetDupeSelectable(id);
                var panel=target==null ? null : Menu(target.gameObject);
                log("DUAL_GUEST_PERSON_PROGRESS step="+step+" foodTable="+(screen!=null && screen.isActiveAndEnabled && screen.gameObject.activeInHierarchy)+
                    " proxy="+(target!=null)+" selected="+(target!=null && DetailsScreen.Instance.target==target.gameObject)+
                    " menuActive="+(panel!=null && panel.gameObject.activeInHierarchy)+" buttons="+(panel==null || buttons(panel)==null?"-":string.Join(",",buttons(panel).Select(b=>b.text)))+
                    " status="+CoopSession.Status);
                lastProgress=Time.unscaledTime; loggedStep=step;
            }
            if (step==0)
            {
                if (!ManagementMenu.Instance.OpenConsumables()) return false;
                step++;return false;
            }
            if (step==1)
            {
                var table=ManagementMenu.Instance.consumablesScreen;
                if (table==null || !table.isActiveAndEnabled || !table.gameObject.activeInHierarchy) return false;
                var row=tableRows(table).FirstOrDefault(item=>item.GetIdentity()==(IAssignableIdentity)original);
                var col=tableColumns(table).Values.OfType<ConsumableInfoTableColumn>().FirstOrDefault(item=>item.consumable_info.ConsumableId==food);
                var widget=row!=null && col!=null ? row.GetWidget(col) : null;
                if (widget==null || !widget.activeInHierarchy) return false;
                var click=AccessTools.DeclaredMethod(typeof(ConsumablesTableScreen),"on_click_consumable_info",new[]{typeof(GameObject)});
                click.Invoke(table,new object[]{widget});
                if (original.GetComponent<ConsumableConsumer>().IsPermitted(food)!=originalAllowed) throw new InvalidDataException("Speculative local diet change");
                step++;return false;
            }
            if (step==2)
            {
                if (original.GetComponent<ConsumableConsumer>().IsPermitted(food)==originalAllowed) return false;
                ManagementMenu.Instance.CloseAll(); step++;
            }
            var selectable=NativeWorldMirror.GetDupeSelectable(id);
            if (selectable==null) return false;
            if (selectable.GetComponent<MinionIdentity>()!=null || selectable.GetComponent<MinionResume>()!=null)
                throw new InvalidDataException("Expected a display-only newcomer");
            var obj=selectable.gameObject;
            if (DetailsScreen.Instance.target!=obj)
            {
                PlayerController.Instance.ActivateTool(SelectTool.Instance);
                SelectTool.Instance.Select(selectable,false);return false;
            }
            skill=Data(lobby,"skill");hat=Data(lobby,"hat");
            string skillLabel=STRINGS.UI.StripLinkFormatting(Db.Get().Skills.Get(skill).Name);
            switch (step)
            {
                case 3: if (Click(obj,s=>s=="学习技能")) step++;break;
                case 4: if (Click(obj,s=>s.StartsWith("学习 ") && s.Contains(skillLabel))) step++;break;
                case 5: if (Click(obj,s=>s=="返回复制人操作")) step++;break;
                case 6: if (Click(obj,s=>s=="职业帽")) step++;break;
                case 7: if (Click(obj,s=>s.StartsWith("选择职业帽") && s.Contains(skillLabel))) step++;break;
                case 8: if (Click(obj,s=>s=="返回复制人操作")) step++;break;
                case 9: if (Click(obj,s=>s=="治疗床位")) step++;break;
                case 10: if (Click(obj,s=>s.StartsWith("分配治疗床位") && s.Contains(Data(lobby,"bed")))) step++;break;
                case 11:
                    if (Data(lobby,"ack")!="1") break;
                    if (screenshot==null) screenshot=new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),"dual-guest-person.png"));
                    if (!screenshot.Requested) {screenshot.Capture();break;}
                    if (!screenshot.Complete) break;
                    log("DUAL_GUEST_PERSON_CONFIRMED newNativeUserMenu=True visualProxyOnly=True learned=True selectedHat=True medicalBed=True foodTable=True");
                    SelectTool.Instance.Select(null,false);done=1;return true;
            }
            return false;
        }
    }
}
