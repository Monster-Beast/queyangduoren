using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using KSerialization;
using UnityEngine;

namespace Queyang.Game
{
    // This component belongs to a particular colony and is serialized by ONI alongside SaveGame.
    // Values identify specific ONI order objects, not a grid square or a transient network connection.
    [SerializationConfig(MemberSerialization.OptIn)]
    public sealed class ColonyOrderBook : KMonoBehaviour, ISaveLoadable
    {
        [Serialize] internal Dictionary<long,int> Orders = new Dictionary<long,int>();
        [Serialize] internal Dictionary<int,string> Names = new Dictionary<int,string>();
        [Serialize] internal Dictionary<string,int> Players = new Dictionary<string,int>();
    }

    [HarmonyPatch(typeof(SaveGame),"OnPrefabInit")]
    static class InstallOrderBook
    {
        static void Postfix(SaveGame __instance) => __instance.gameObject.AddOrGet<ColonyOrderBook>();
    }

    internal static class OrderOwnership
    {
        static readonly Dictionary<int,List<OrderMark>> marks = new Dictionary<int,List<OrderMark>>();
        static readonly Dictionary<int,string> guestNames = new Dictionary<int,string>();
        static readonly List<long> expired = new List<long>();
        static readonly Dictionary<int,string> legacyClients = new Dictionary<int,string>();
        static readonly List<OrderMark> empty = new List<OrderMark>();
        static int actor, lastScan = -1;
        static ColonyOrderBook Book => SaveGame.Instance == null ? null : SaveGame.Instance.GetComponent<ColonyOrderBook>();
        internal static int Actor => actor;

        internal static Guid LanInstallation()
        {
            const string key = "QueyangCoop.LanInstallation";
            if (Guid.TryParse(UnityEngine.PlayerPrefs.GetString(key),out var id) && id!=Guid.Empty) return id;
            id=Guid.NewGuid();
            UnityEngine.PlayerPrefs.SetString(key,id.ToString("N"));
            UnityEngine.PlayerPrefs.Save();
            return id;
        }

        internal static string Execute(string account,string displayName,int peer,Guid installation,string action,int x,int y)
        {
            if (account==null)
            {
                if (installation!=Guid.Empty) account="lan/"+installation.ToString("N");
                else if (!legacyClients.TryGetValue(peer,out account))
                    legacyClients.Add(peer,account="guest/"+Guid.NewGuid().ToString("N"));
            }
            // The baseline also reaches guests. Retain only a digest of the Steam ID or LAN token.
            using (var sha=SHA256.Create()) account=Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(account)));
            var book=Book;
            if (book==null) return "ERROR Order author unavailable";
            if (!book.Players.TryGetValue(account,out int author))
            {
                author=checked(book.Players.Count+2);
                book.Players.Add(account,author);
            }
            SetName(author,displayName);
            int previous=actor;
            try { actor=author; return HostWorld.Execute(action,x,y); }
            finally { actor=previous; }
        }

        internal static void Reset()
        {
            marks.Clear(); guestNames.Clear(); legacyClients.Clear(); lastScan=-1; actor=0;
        }

        internal static int EnterHostInput()
        {
            int previous=actor;
            if (actor==0 && CoopSession.IsHosting) actor=1;
            return previous;
        }
        internal static void LeaveInput(int previous) => actor=previous;

        internal static void Record(GameObject target,OrderKind kind)
        {
            if (actor==0 || !CoopSession.IsHosting || target==null) return;
            var prefab=target.GetComponent<KPrefabID>();
            var book=Book;
            if (prefab==null || prefab.InstanceID<=0 || book==null) return;
            book.Orders[((long)prefab.InstanceID<<3)|(long)kind]=actor;
            lastScan=-1;
            CoopSession.OrderChanged(Grid.PosToCell(target));
        }

        internal static void Remove(Component target,OrderKind kind)
        {
            if (target==null || CoopSession.IsClient) return;
            var prefab=target.GetComponent<KPrefabID>();
            var book=Book;
            if (prefab!=null && book!=null && book.Orders.Remove(((long)prefab.InstanceID<<3)|(long)kind))
            {
                lastScan=-1;
                CoopSession.OrderChanged(Grid.PosToCell(target.gameObject));
            }
        }

        static bool Live(KPrefabID prefab,OrderKind kind)
        {
            switch (kind)
            {
                case OrderKind.Dig: return prefab.GetComponent<Diggable>()!=null;
                case OrderKind.Mop: return prefab.GetComponent<Moppable>()!=null;
                case OrderKind.Build: return prefab.GetComponent<Constructable>()!=null;
                case OrderKind.Deconstruct: return prefab.GetComponent<Deconstructable>()?.IsMarkedForDeconstruction()==true;
                case OrderKind.Sweep:
                    var pickup=prefab.GetComponent<Pickupable>();
                    return pickup!=null && pickup.storage==null && pickup.Clearable!=null && NativeEntitySampler.IsMarked(pickup.Clearable);
                case OrderKind.Harvest: return prefab.GetComponent<HarvestDesignatable>()?.HarvestWhenReady==true;
                default: return false;
            }
        }

        static void Add(int cell,OrderMark mark)
        {
            if (!marks.TryGetValue(cell,out var list)) marks.Add(cell,list=new List<OrderMark>());
            if (!list.Exists(old=>old.Author==mark.Author && old.Kind==mark.Kind)) list.Add(mark);
        }

        static void Refresh()
        {
            if (!CoopSession.IsHosting || lastScan==Time.frameCount) return;
            lastScan=Time.frameCount;
            marks.Clear(); expired.Clear();
            var book=Book;
            if (book==null) return;
            foreach (var entry in book.Orders)
            {
                var prefab=KPrefabIDTracker.Get().GetInstance((int)(entry.Key>>3));
                var kind=(OrderKind)(entry.Key&7);
                if (prefab==null || !prefab.gameObject.activeInHierarchy || !Live(prefab,kind))
                { expired.Add(entry.Key); continue; }
                var building=prefab.GetComponent<Building>();
                int cell=building!=null ? building.GetCell() : Grid.PosToCell(prefab);
                if (Grid.IsValidCell(cell)) Add(cell,new OrderMark{Cell=cell,Kind=kind,Author=entry.Value});
            }
            foreach (long key in expired) book.Orders.Remove(key);
        }

        internal static IEnumerable<OrderMark> At(int cell)
        {
            Refresh();
            return marks.TryGetValue(cell,out var list) ? list : empty;
        }

        internal static void Capture(ColonyView view,int sample)
        {
            if (!CoopSession.IsHosting) return;
            Refresh();
            var page=new List<OrderMark>();
            for (int i=0;i<view.Cells.Length;i++)
            {
                if ((view.Cells[i].Flags&1)==0) continue;
                int cell=Grid.XYToCell(view.X+i%ColonyView.Width,view.Y+i/ColonyView.Width);
                if (!marks.TryGetValue(cell,out var list)) continue;
                foreach (var m in list) page.Add(new OrderMark{Cell=i,Author=m.Author,Kind=m.Kind});
            }
            view.OrderFirstRow=0; view.OrderRows=ColonyView.Height;
            if (page.Count>256)
            {
                view.OrderFirstRow=(byte)((sample>=0 ? sample : Time.frameCount)%ColonyView.Height);
                view.OrderRows=1;
                page.RemoveAll(m=>m.Cell/ColonyView.Width!=view.OrderFirstRow);
            }
            view.OrderMarks=page.ToArray();
            var book=Book;
            foreach (var mark in page)
                if (!view.OrderNames.ContainsKey(mark.Author) && book!=null && book.Names.TryGetValue(mark.Author,out var name))
                    view.OrderNames.Add(mark.Author,name);
        }

        internal static void Apply(ColonyView view)
        {
            if (view.OrderMarks==null) return;
            foreach (var player in view.OrderNames) guestNames[player.Key]=player.Value;
            for (int row=view.OrderFirstRow;row<view.OrderFirstRow+view.OrderRows;row++)
                for (int x=0;x<ColonyView.Width;x++)
                    marks.Remove(Grid.XYToCell(view.X+x,view.Y+row));
            foreach (var m in view.OrderMarks)
            {
                int cell=Grid.XYToCell(view.X+m.Cell%ColonyView.Width,view.Y+m.Cell/ColonyView.Width);
                Add(cell,new OrderMark{Cell=cell,Author=m.Author,Kind=m.Kind});
            }
        }

        internal static void SetName(int author,string raw)
        {
            string name=ColonyView.CleanOrderName(raw);
            var book=Book;
            if (book!=null && name.Length>0) book.Names[author]=name;
        }

        internal static string Label(int author)
        {
            string role=author==1 ? "房主 P1" : "玩家 P"+author;
            var names=CoopSession.IsHosting ? Book?.Names : guestNames;
            return names!=null && names.TryGetValue(author,out var name) ? role+" "+name : role;
        }
        internal static Color ColorFor(int author)
        {
            switch (author)
            {
                case 1: return new Color(.23f,.78f,.99f);
                case 2: return new Color(1f,.64f,.15f);
                case 3: return new Color(.53f,.91f,.39f);
                case 4: return new Color(.96f,.48f,.76f);
                default: return Color.HSVToRGB((author*.618034f)%1f,.62f,1f);
            }
        }
        static readonly string[] kinds={"挖掘","拖地","建造","拆除","清扫","自动收获"};
        static GUIStyle badge,detail;
        internal static Vector2 MousePosition() => Event.current.mousePosition;

        internal static void Draw(float scale)
        {
            if (Event.current.type!=EventType.Repaint || CameraController.Instance==null || ClusterManager.Instance?.activeWorld==null) return;
            Refresh();
            var camera=CameraController.Instance.baseCamera;
            if (camera==null) return;
            if (badge==null)
            {
                badge=new GUIStyle(GUI.skin.label){alignment=TextAnchor.MiddleCenter,padding=new RectOffset(),fontStyle=FontStyle.Bold,richText=false};
                detail=new GUIStyle(GUI.skin.label){alignment=TextAnchor.MiddleLeft,padding=new RectOffset(6,6,2,2),richText=false};
                badge.normal.textColor=Color.black; detail.normal.textColor=Color.white;
            }
            var mouse=MousePosition();
            var pointer=new Vector3(mouse.x*scale,Screen.height-mouse.y*scale,0);
            int hover=Grid.PosToCell(camera.ScreenToWorldPoint(pointer));
            int world=ClusterManager.Instance.activeWorld.id;
            float tile=Mathf.Abs(camera.WorldToScreenPoint(Vector3.right).x-camera.WorldToScreenPoint(Vector3.zero).x)/scale;
            float size=Mathf.Clamp(tile*.39f,11f,22f);
            badge.fontSize=Mathf.Clamp(Mathf.RoundToInt(size*.78f),9,16);
            foreach (var entry in marks)
            {
                int cell=entry.Key;
                if (!Grid.IsValidCell(cell) || Grid.WorldIdx[cell]!=world || Grid.Visible[cell]==0) continue;
                var p=camera.WorldToScreenPoint(Grid.CellToPosCCC(cell,Grid.SceneLayer.Move));
                if (p.z<0 || p.x<0 || p.y<0 || p.x>Screen.width || p.y>Screen.height) continue;
                float left=p.x/scale-tile*.44f, top=(Screen.height-p.y)/scale-tile*.44f;
                int index=0;
                var list=entry.Value;
                for (int i=0;i<list.Count;i++)
                {
                    int author=list[i].Author;
                    bool earlier=false;
                    for (int j=0;j<i;j++) if (list[j].Author==author) { earlier=true; break; }
                    if (earlier) continue;
                    var rect=new Rect(left+(index%2)*(size+1),top+(index/2)*(size+1),size,size);
                    DrawBadge(cell,author,rect);
                    if (rect.Contains(mouse)) hover=cell;
                    index++;
                }
            }
            if (!marks.TryGetValue(hover,out var hovered) || Grid.WorldIdx[hover]!=world || Grid.Visible[hover]==0) return;
            string text=string.Join("\n",hovered.Select(m=>Label(m.Author)+" · "+kinds[(int)m.Kind]));
            var content=new GUIContent(text);
            var dims=detail.CalcSize(content);
            var tip=new Rect(Mathf.Clamp(mouse.x+16,0,Screen.width/scale-dims.x),Mathf.Clamp(mouse.y+18,0,Screen.height/scale-dims.y),dims.x,dims.y);
            DrawTooltip(hover,text,tip);
        }

        internal static void DrawBadge(int cell,int author,Rect rect)
        {
            var old=GUI.color;
            GUI.color=ColorFor(author); GUI.DrawTexture(rect,Texture2D.whiteTexture);
            GUI.color=Color.white; GUI.Label(rect,author.ToString(),badge); GUI.color=old;
        }

        internal static void DrawTooltip(int cell,string text,Rect rect)
        {
            var old=GUI.color;
            GUI.color=new Color(.06f,.07f,.09f,.96f); GUI.DrawTexture(rect,Texture2D.whiteTexture);
            GUI.color=Color.white; GUI.Label(rect,text,detail); GUI.color=old;
        }
    }
}
