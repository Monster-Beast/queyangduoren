using System;
using System.IO;
using System.Linq;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    // Only the isolated host mutates a storage object. Keep the same building selected
    // while its contents change, then compare the text actually rendered by each ONI.
    internal sealed class SteamDualBuildings
    {
        int stage,prepared=-1;
        Storage storage;
        PrimaryElement item;
        string label,amount;
        FixtureFrame photo;
        float lastReport;
        static string Key(int step,string field) => "qy_store_"+step+"_"+field;
        static string Read(CSteamID lobby,int step,string field) => SteamMatchmaking.GetLobbyData(lobby,Key(step,field));
        static void Set(CSteamID lobby,int step,string field,string value)
        {
            if (!SteamMatchmaking.SetLobbyData(lobby,Key(step,field),value)) throw new IOException("Building test metadata failed");
        }
        void Change(CSteamID lobby)
        {
            if (!CoopSession.IsHosting || Util.GetKleiRootPath()!=Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                throw new InvalidOperationException("Building fixture requires isolated host");
            int world=ClusterManager.Instance.activeWorldId;
            if (stage==0 || stage==2)
            {
                string type=stage==0 ? RationBoxConfig.ID : WashBasinConfig.ID;
                var b=Components.BuildingCompletes.Items.FirstOrDefault(o=>o!=null && o.PrefabID()==type &&
                    ClusterUtil.GetMyWorldId(o)==world && Grid.Visible[o.GetComponent<Building>().GetCell()]!=0);
                storage=(b!=null ? b.gameObject : NativeFacilityChecks.Build(type,world)).GetComponent<Storage>();
                if (stage==0)
                {
                    var prefab=new[]{"MushBar","BasicPlantBar","CookedMeat","BasicPlantFood"}
                        .Select(tag=>Assets.TryGetPrefab(new Tag(tag))).First(p=>p!=null &&
                            !storage.items.Any(o=>o!=null && o.PrefabID()==p.PrefabID()));
                    var food=Util.KInstantiate(prefab,storage.transform.position);
                    item=food.GetComponent<PrimaryElement>(); item.Mass=1.25f; food.SetActive(true);
                    storage.Store(food);
                }
                else item=storage.AddLiquid(SimHashes.Water,17.25f,295f,byte.MaxValue,0);
            }
            else item.Mass+=stage==1 ? 1.5f : 7.5f;
            if (item==null || !storage.items.Contains(item.gameObject)) throw new InvalidDataException("Fixture item missing from native storage");
            label=STRINGS.UI.StripLinkFormatting(item.gameObject.GetProperName());
            amount=item.MassPerUnit>1f ? GameUtil.GetFormattedUnits(item.Mass/item.MassPerUnit) : GameUtil.GetFormattedMass(item.Mass);
            Set(lobby,stage,"cell",storage.GetComponent<Building>().GetCell().ToString());
            Set(lobby,stage,"id",storage.GetComponent<KPrefabID>().InstanceID.ToString());
            Set(lobby,stage,"label",label);
            Set(lobby,stage,"amount",amount);
            prepared=stage;
        }
        internal bool Check(CSteamID lobby,CSteamID member,bool host,int round,Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_CHECK_BUILDINGS")!="1" || round!=1 || stage>=4) return true;
            if (host && prepared!=stage) Change(lobby);
            if (!int.TryParse(Read(lobby,stage,"cell"),out int cell) || !int.TryParse(Read(lobby,stage,"id"),out int id)) return false;
            if (!host) { label=Read(lobby,stage,"label");amount=Read(lobby,stage,"amount"); }
            if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(amount)) return false;
            var obj=host ? storage.GetComponent<KSelectable>() : NativeWorldMirror.GetBuildingSelectable(cell,id);
            CameraController.Instance.SetTargetPos(Grid.CellToPosCCC(cell,Grid.SceneLayer.Move),10f,false);
            if (obj==null) return false;
            if (DetailsScreen.Instance?.target!=obj.gameObject)
            {
                ManagementMenu.Instance.CloseAll();
                PlayerController.Instance.ActivateTool(SelectTool.Instance);
                SelectTool.Instance.Select(obj,false);
                return false;
            }
            var info=UnityEngine.Object.FindObjectsByType<SimpleInfoScreen>(FindObjectsSortMode.None)
                .FirstOrDefault(s=>s.StoragePanel!=null && s.StoragePanel.gameObject.activeInHierarchy);
            var texts=info==null ? "" : string.Join(" ",info.StoragePanel.GetComponentsInChildren<LocText>(false).Select(t=>t.text));
            if (!texts.Contains(label) || !texts.Contains(amount))
            {
                if(Time.unscaledTime-lastReport>5) { log($"DUAL_STORAGE_WAIT stage={stage} id={id} wanted={label}/{amount} actual={texts}");lastReport=Time.unscaledTime; }
                return false;
            }
            var labels=info.StoragePanel.GetComponentsInChildren<LocText>(false);
            var row=labels.FirstOrDefault(t=>t.text.Contains(label));
            if (row==null || !ShowRow(row.rectTransform)) return false;
            var amountLabel=labels.FirstOrDefault(t=>t.text.Contains(amount));
            if (amountLabel==null || !ShowRow(amountLabel.rectTransform)) return false;
            if (photo==null)
            {
                string role=host?"host":"guest";
                string path=Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),
                    $"building-{role}-{stage}.png");
                photo=new FixtureFrame(path);photo.Capture();return false;
            }
            if (!photo.Complete) return false;
            if (host && SteamMatchmaking.GetLobbyMemberData(lobby,member,Key(stage,"ok"))!="1") return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(lobby,Key(stage,"ok"),"1");
            log($"DUAL_STORAGE_RENDERED stage={stage} cell={cell} id={id} nativePanel=True item={label} amount={amount}");
            stage++;photo=null;
            if (stage==4) SelectTool.Instance.Select(null,false);
            return stage==4;
        }
        static bool ShowRow(RectTransform row)
        {
            var scroll=row.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            if (scroll==null) return true;
            var viewport=scroll.viewport??(RectTransform)scroll.transform;
            var point=viewport.InverseTransformPoint(row.TransformPoint(row.rect.center));
            if (viewport.rect.Contains(point)) return true;
            float travel=RectTransformUtility.CalculateRelativeRectTransformBounds(viewport,scroll.content).size.y-viewport.rect.height;
            if (travel>0)
            {
                scroll.StopMovement();
                scroll.verticalNormalizedPosition=Mathf.Clamp01(scroll.verticalNormalizedPosition+(point.y-viewport.rect.center.y)/travel);
            }
            return false;
        }
    }
}
