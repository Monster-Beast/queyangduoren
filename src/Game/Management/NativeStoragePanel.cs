using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        static int nextStoragePage;
        static CollapsibleDetailContentPanel storagePanel;
        static NativeFacilityStorage.Snapshot storageState;
        static SortedDictionary<int,NativeFacilityStorage.Row> storageRows=new SortedDictionary<int,NativeFacilityStorage.Row>();
        static readonly SortedDictionary<int,NativeFacilityStorage.Row> pendingStorageRows=new SortedDictionary<int,NativeFacilityStorage.Row>();
        static void ResetStoragePanel()
        {
            nextStoragePage=0;storageState=null;storageRows.Clear();pendingStorageRows.Clear();statusReadout=null;
        }
        static void ReceiveStoragePanel(string packed)
        {
            var state=NativeFacilityStorage.Decode(packed);
            if(state.Page==0) pendingStorageRows.Clear();
            else if(state.Page!=nextStoragePage) return;
            foreach(var row in state.Rows) pendingStorageRows[row.Key]=row.Value;
            if(state.Page+1<state.Pages) { nextStoragePage=state.Page+1;return; }
            nextStoragePage=0;
            if(pendingStorageRows.Count!=state.Total) return;
            storageRows=new SortedDictionary<int,NativeFacilityStorage.Row>(pendingStorageRows);
            storageState=state;
            var obj=NativeWorldMirror.GetBuildingSelectable(inspectedCell,inspectedId);
            if(obj!=null && storagePanel!=null && DetailsScreen.Instance?.target==obj.gameObject) DrawStoragePanel(storagePanel);
        }
        static void DrawStoragePanel(CollapsibleDetailContentPanel panel)
        {
            var state=storageState;
            panel.gameObject.SetActive(true);
            panel.SetTitle(STRINGS.UI.DETAILTABS.DETAILS.GROUPNAME_CONTENTS);
            string Mass(float n)=>GameUtil.GetFormattedMass(n);
            panel.SetLabel("CoopStorageCapacity","已存 "+Mass(state.Stored)+" / "+Mass(state.Capacity)+" · 可用 "+Mass(state.Available),"");
            if(state.Limit<state.Capacity) panel.SetLabel("CoopStorageLimit","储存上限 "+Mass(state.Limit),"");
            if(state.Wash)
            {
                string status=!state.Operational?"停止运行":state.Active?"运行中":state.Ready?"可使用":"不可使用";
                int uses=state.PerUse>0?Mathf.FloorToInt(state.Clean/state.PerUse):0;
                panel.SetLabel("CoopWashState",status+" · 可用 "+uses+" 次"+(state.Full?" · 污水已满":""),"");
                panel.SetLabel("CoopWashWater","净水 "+Mass(state.Clean)+" · 污水 "+Mass(state.Waste),"");
            }
            foreach(var row in storageRows.Values)
            {
                var prefab=Assets.TryGetPrefab(new Tag(row.Tag));
                string name=prefab==null?TagManager.GetProperName(new Tag(row.Tag)):prefab.GetProperName();
                string quantity=row.CountUnits?GameUtil.GetFormattedUnits(row.Units):Mass(row.Mass);
                string food=row.Calories>0?" · "+GameUtil.GetFormattedCalories(row.Calories):"";
                string available=Mass(row.Available)+(row.Calories>0?" · "+GameUtil.GetFormattedCalories(row.AvailableCalories):"");
                panel.SetLabel("CoopStored"+row.Tag,name+" · "+quantity+food,"可用 "+available);
            }
            panel.Commit();
        }
        [HarmonyPatch(typeof(SimpleInfoScreen),"RefreshStoragePanel")]
        static class SelectedStorage
        {
            static bool Prefix(CollapsibleDetailContentPanel __0,GameObject __1)
            {
                if(!NativeGuestScene.Ready || !CoopSession.IsNativeClient) return true;
                storagePanel=__0;
                if(__1==null || storageState==null || inspectedId<=0 ||
                    NativeWorldMirror.GetBuildingSelectable(inspectedCell,inspectedId)?.gameObject!=__1) return true;
                DrawStoragePanel(__0); return false;
            }
        }
    }
}
