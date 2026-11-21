using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using UnityEngine;

namespace Queyang.Integration
{
    // Against saved real host responses, drive the vanilla toolbar gesture used for each operation.
    internal sealed class NativeStockTools
    {
        readonly StockOrderTape[] orders;
        readonly Action<string> log;
        int index;
        bool clicked, received;
        float deadline;
        internal NativeStockTools(StockOrderTape[] orders, Action<string> log)
        {
            this.orders=orders; this.log=log;
            CoopSession.CommandResult+=Result;
            Next();
        }
        void Result(string reply) { if (clicked && reply==orders[index].Reply) received=true; }
        void Next()
        {
            received=clicked=false; deadline=Time.unscaledTime+14;
            CoopSession.Command("fixture/order/"+index,0,0);
        }
        static int DesiredPriority(string action) => action["priority".Length]-'0';
        void Click(StockOrderTape t)
        {
            DragTool tool;
            switch(index)
            {
                case 0: tool=ClearTool.Instance; break;
                case 1: tool=PrioritizeTool.Instance; break;
                case 2: tool=HarvestTool.Instance; break;
                default: tool=MopTool.Instance; break;
            }
            PlayerController.Instance.ActivateTool(tool);
            if (index==1)
            {
                var screen=ToolMenu.Instance.PriorityScreen;
                int wanted=DesiredPriority(t.Command);
                var button=screen.GetComponentsInChildren<PriorityButton>().First(b=>b.priority.priority_class==PriorityScreen.PriorityClass.basic &&
                    b.priority.priority_value==wanted && b.gameObject.activeInHierarchy);
                AccessTools.DeclaredMethod(typeof(PriorityButton),"OnClick").Invoke(button,null);
                if (screen.GetLastSelectedPriority().priority_value!=wanted) throw new InvalidDataException("Stock priority button did not set value "+wanted);
            }
            Vector3 pos=Grid.CellToPosCCC(Grid.XYToCell(t.X,t.Y),Grid.SceneLayer.Move);
            tool.OnLeftClickDown(pos); tool.OnLeftClickUp(pos);
        }
        internal bool Tick()
        {
            if (Time.unscaledTime>deadline)
            {
                CoopSession.CommandResult-=Result;
                throw new TimeoutException("Native stock tool "+orders[index].Command+" clicked="+clicked+" confirmed="+received+" status="+CoopSession.Status);
            }
            var order=orders[index]; var raw=CoopSession.View;
            if (raw==null) return false;
            if (!clicked)
            {
                if (!raw.SequenceEqual(order.Before)) return false;
                clicked=true; Click(order);
                return false;
            }
            if (!received || !raw.SequenceEqual(order.After)) return false;
            var v=CoopSession.NativeView; int i=(order.Y-v.Y)*ColonyView.Width+order.X-v.X;
            if (index==0 || index==2)
            {
                int flag=index==0 ? 2 : 16;
                var e=EntityStateCodec.Decode(v.EntityPayload).Entities.First(entity=>entity.Cell==i &&
                    (entity.Flags&flag)!=0 && (index==0 ? entity.Kind==ColonyEntityKind.Item : entity.Kind==ColonyEntityKind.Plant));
                var observed=NativeEntityDisplay.GetObservedState(e.Id);
                if(observed==null || (observed.Flags&flag)==0 || NativeEntityDisplay.GetSelectable(e.Id)==null)
                    throw new InvalidDataException("Native order missing on selectable "+e.Id);
            }
            if (index==1 && v.Cells[i].OrderPriority!=DesiredPriority(order.Command)) throw new InvalidDataException("Authoritative priority did not change");
            if (index==3 && (!v.Cells[i].MopOrdered || !NativeWorldMirror.IsMopVisible(Grid.XYToCell(order.X,order.Y))))
                throw new InvalidDataException("Missing native mop marker");
            log("NATIVE_STOCK_TOOL_CONFIRMED "+order.Command+" cell="+order.X+","+order.Y+" reply="+order.Reply);
            if (++index==orders.Length)
            {
                CoopSession.CommandResult-=Result;
                log("NATIVE_STOCK_TOOLS_GUEST sweep=true priority=true harvest=true mop=true");
                return true;
            }
            Next(); return false;
        }
    }
}
