using System;
using System.Collections.Generic;
using System.Linq;
using Queyang.Game;

namespace Queyang.Integration
{
    internal sealed class GuestScenario
    {
        private int step, digX, digY, ladderX, ladderY, sequence;
        private bool digDone, ladderDone, digSeen, ladderSeen, digOrdered, ladderOrdered;
        private readonly HashSet<int> attempted = new HashSet<int>();
        private readonly Action<string> Log;
        private readonly Action<string,int,int> send;
        public bool Done => digDone && ladderDone;
        internal GuestScenario(Action<string> log, Action<string,int,int> command) { Log=log; send=command; }
        internal void Update(byte[] bytes, string lastStatus)
        {
            var v = ColonyView.Decode(bytes);
            bool result = lastStatus.StartsWith("#" + sequence + " ", StringComparison.Ordinal);
            if (step == 0)
            {
                Log("CLIENT_BASELINE cells=" + v.Cells.Count(c => (c.Flags & 1) != 0) + " dupes=" + v.Dupes.Length);
                step = 1;
            }
            if (step == 1 && TryOrder(v,true)) { step = 2; return; }
            if (step == 2 && result && lastStatus.Contains("ERROR")) { step = 1; return; }
            if (step == 2 && result && lastStatus.Contains("OK Dig")) { digOrdered = true; attempted.Clear(); step = 3; }
            if (step == 3 && TryOrder(v,false)) { step = 4; return; }
            if (step == 4 && result && lastStatus.Contains("ERROR")) { step = 3; return; }
            if (step == 4 && result && lastStatus.Contains("OK Construction")) { ladderOrdered = true; step = 5; }
            if (digOrdered)
            {
                var c = v.Cells[(digY-v.Y)*ColonyView.Width+digX-v.X];
                if (!digSeen && (c.Flags & 8) != 0) { digSeen = true; Log("DIG_ORDER_VISIBLE"); }
                if (!digDone && (c.Flags & 3) == 1)
                { digDone = true; Log("DIG_FINISHED element=" + c.Element + " mass=" + c.Mass + " kg"); }
            }
            if (ladderOrdered)
            {
                var c = v.Cells[(ladderY-v.Y)*ColonyView.Width+ladderX-v.X];
                if (!ladderSeen && (c.Flags & 32) != 0) { ladderSeen = true; Log("LADDER_PLAN_VISIBLE"); }
                if (!ladderDone && (c.Flags & 16) != 0)
                { ladderDone = true; Log("LADDER_FINISHED"); }
            }
        }
        private bool TryOrder(ColonyView v,bool dig)
        {
            var candidates = new List<int>();
            for (int y=1;y<ColonyView.Height-1;y++)
                for (int x=1;x<ColonyView.Width-1;x++)
                {
                    int i = y*ColonyView.Width+x;
                    var c = v.Cells[i];
                    if ((c.Flags & 1) == 0 || attempted.Contains(i) || (c.Flags & (8|16|32)) != 0) continue;
                    if (dig)
                    {
                        if ((c.Flags & 2) == 0 || (c.Flags & 4) != 0) continue;
                        bool exposed = new[] {-1,1,ColonyView.Width}.Any(d=>(v.Cells[i+d].Flags & 3)==1);
                        if (!exposed) continue;
                    }
                    else if ((c.Flags & (2|4)) != 0 || (v.Cells[i-ColonyView.Width].Flags & (2|16)) == 0) continue;
                    candidates.Add(i);
                }
            if (candidates.Count == 0) return false;
            int selected = candidates.OrderBy(i => v.Dupes.Length == 0 ? Math.Abs(i%ColonyView.Width-ColonyView.Width/2) :
                v.Dupes.Min(d => Math.Abs(d.X - (v.X+i%ColonyView.Width)) + Math.Abs(d.Y - (v.Y+i/ColonyView.Width)))).First();
            attempted.Add(selected);
            int sx = v.X+selected%ColonyView.Width, sy = v.Y+selected/ColonyView.Width;
            if (dig) { digX=sx;digY=sy; } else { ladderX=sx;ladderY=sy; }
            sequence++;
            send(dig?"dig":"ladder",sx,sy);
            Log("SEND " + (dig?"dig":"ladder") + " " + sx + "," + sy);
            return true;
        }
    }
}
