using System;
using System.IO;
using Steamworks;

namespace Queyang.Integration
{
    internal sealed class SteamArrivalFrame
    {
        FixtureFrame frame;
        int currentRound;

        internal bool Capture(CSteamID lobby,CSteamID friend,int round,bool guest,Action<string> log)
        {
            string value=round.ToString();
            string role=guest?"guest":"host";
            if (guest) SteamMatchmaking.SetLobbyMemberData(lobby,"qy_enter_world",value);
            else if (SteamMatchmaking.GetLobbyMemberData(lobby,friend,"qy_enter_world")!=value) return false;
            if (currentRound!=round)
            {
                currentRound=round;
                string name="entry-"+role+"-"+round+".png";
                frame=new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),name));
                SteamSceneLight.Record(round,role,log);
                frame.Capture();
                return false;
            }
            if (!frame.Complete) return false;
            if (guest)
            {
                if (SteamMatchmaking.GetLobbyData(lobby,"qy_enter_captured")!=value) return false;
            }
            else if (!SteamMatchmaking.SetLobbyData(lobby,"qy_enter_captured",value)) throw new IOException("Could not acknowledge entry screenshot");
            log("DUAL_ENTRY_SCREENSHOT round="+round+" role="+role);
            return true;
        }
    }
}
