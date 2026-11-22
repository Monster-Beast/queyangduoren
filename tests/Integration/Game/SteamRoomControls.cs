using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamRoomControls
    {
        const string Prefix = "qy_room_controls_";
        const string TestName = "联机值班员 QA";
        static readonly AccessTools.FieldRef<SpeedControlScreen, int> Pauses =
            AccessTools.FieldRefAccess<SpeedControlScreen, int>("pauseCount");
        int stage, id, originalSpeed;
        bool clicked, finished, rechecked;
        float deadline;
        Vector3 originalCamera;
        float zoom;
        Tech tech;

        internal bool Check(CSteamID lobby, CSteamID peer, int round, bool host, Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_ROOM_CONTROLS") != "1") return true;
            var camera = CameraController.Instance;
            if (round == 2)
            {
                if (rechecked) return true;
                if (id == 0 || Find(id)?.GetProperName() != TestName) return false;
                log($"ROOM_CONTROLS_REJOIN role={(host ? "host" : "guest")} id={id} nameRetained=True");
                return rechecked = true;
            }
            if (finished) return true;
            if (deadline == 0)
            {
                deadline = Time.unscaledTime + 60;
                originalCamera = camera.transform.position;
                zoom = camera.OrthographicSize;
            }
            if (Time.unscaledTime > deadline) throw new TimeoutException($"Room controls host={host} stage={stage} clicked={clicked}");
            var speed = SpeedControlScreen.Instance;
            if (id == 0 && host)
            {
                if (!CoopSession.IsHosting || Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                    throw new InvalidOperationException("Room controls require an isolated colony");
                var dupe = Components.LiveMinionIdentities.Items.First(d => d != null);
                id = dupe.GetComponent<KPrefabID>().InstanceID;
                tech = Db.Get().Techs.resources.First(t => !t.Disabled && t.FoundNode && !t.IsComplete() && t.ArePrerequisitesComplete());
                originalSpeed = speed.GetSpeed();
                Research.Instance.SetActiveResearch(null, true);
                while (Pauses(speed) > 1) speed.Unpause(false);
                if (!speed.IsPaused) speed.Pause(false);
                speed.SetSpeed(0);
                Set(lobby, "fixture", $"{id}|{tech.Id}");
            }
            if (id == 0)
            {
                var fixture = Read(lobby, "fixture").Split('|');
                if (fixture.Length != 2) return false;
                id = int.Parse(fixture[0]);
                tech = Db.Get().Techs.resources.First(t => t.Id == fixture[1]);
            }

            var target = Find(id);
            if (target == null) return false;
            var position = target.transform.position;
            position.z = camera.transform.position.z;
            camera.SetTargetPos(position, 6f, false);
            camera.SetPosition(position);
            camera.OrthographicSize = 6f;
            bool ready = host || Read(lobby, "gate") == stage.ToString();
            if (host && Read(lobby, "gate") != stage.ToString()) Set(lobby, "gate", stage.ToString());
            if (!ready) return false;
            if (stage == 0 && !host && !clicked)
            {
                SelectTool.Instance.Select(target, false);
                var details = DetailsScreen.Instance;
                if (details?.target != target.gameObject) return false;
                AccessTools.Method(typeof(DetailsScreen), "OnNameChanged").Invoke(details, new object[] { TestName });
                clicked = true;
            }
            if (stage == 1 || stage == 2)
            {
                var menu = ManagementMenu.Instance;
                var research = (ResearchScreen)AccessTools.Field(typeof(ManagementMenu), "researchScreen").GetValue(menu);
                if (!research.IsScreenActive()) { menu.ToggleResearch(); return false; }
                if (!host && !clicked)
                {
                    // Let the stock research table receive its host queue before submitting a click.
                    var entry = research.GetEntry(tech);
                    if (entry == null || Time.unscaledTime < actionAt) return false;
                    AccessTools.Method(typeof(ResearchEntry), stage == 1 ? "OnResearchClicked" : "OnResearchCanceled").Invoke(entry,null);
                    clicked = true;
                }
            }
            bool paused = host ? speed.IsPaused : CoopSession.NativeView?.Paused == true;
            int speedValue = host ? speed.GetSpeed() : CoopSession.NativeView?.Speed == 2 ? 1 : 0;
            if (!host && !clicked && stage >= 3)
            {
                if (stage == 3 && CoopSession.NativeView?.Speed == 1 && paused) speed.SetSpeed(1);
                else if (stage == 4 && paused || stage == 5 && !paused) speed.TogglePause(false);
                else return false;
                clicked = true;
            }
            bool verified;
            switch (stage)
            {
                case 0: verified = target.GetProperName() == TestName; break;
                case 1: verified = Research.Instance.GetActiveResearch()?.tech == tech; break;
                case 2: verified = Research.Instance.GetResearchQueue().All(q => q.tech != tech); break;
                case 3: verified = speedValue == 1 && paused; break;
                case 4: verified = !paused; break;
                default: verified = paused; break;
            }
            if (!verified || !host && !clicked) return false;
            string acknowledgement = Prefix + "seen_" + stage;
            if (host && SteamMatchmaking.GetLobbyMemberData(lobby,peer,acknowledgement) != "1") return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(lobby,acknowledgement,"1");
            log($"ROOM_CONTROLS_STAGE role={(host ? "host" : "guest")} stage={stage} id={id} tech={tech.Id} paused={paused} speedIndex={speedValue}");
            if (stage == 2)
            {
                ManagementMenu.Instance.CloseAll();
                SelectTool.Instance.Select(null,false);
            }
            stage++;
            actionAt = Time.unscaledTime + 1.5f;
            clicked = false;
            if (stage < 6) return false;
            if (host) speed.SetSpeed(originalSpeed);
            camera.SetTargetPos(originalCamera,zoom,false);
            camera.SetPosition(originalCamera);
            camera.OrthographicSize = zoom;
            return finished = true;
        }

        float actionAt;
        static KSelectable Find(int saveId) => CoopSession.IsHosting
            ? Components.LiveMinionIdentities.Items.FirstOrDefault(d => d != null && d.GetComponent<KPrefabID>().InstanceID == saveId)?.GetComponent<KSelectable>()
            : NativeWorldMirror.GetDupeSelectable(saveId);
        static string Read(CSteamID lobby, string key) => SteamMatchmaking.GetLobbyData(lobby,Prefix + key);
        static void Set(CSteamID lobby, string key, string value)
        {
            if (!SteamMatchmaking.SetLobbyData(lobby,Prefix + key,value)) throw new IOException("Could not publish room controls fixture");
        }
    }
}
