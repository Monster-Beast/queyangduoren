using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Klei.AI;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class SteamUpgradeVisualRange
    {
        const string Key = "qy_upgrade_";
        readonly HashSet<int> frames = new HashSet<int>();
        MinionIdentity subject;
        int id, stage, oldSpeed, navTarget;
        bool wasPaused, triggered;
        float deadline, displacement, reportAt, zoom;
        Vector3 cameraPosition, firstPosition;
        FixtureFrame photo;

        internal bool Check(CSteamID room,CSteamID peer,int round,bool host,Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_UPGRADE_VISUAL") != "1" || round != 1 || stage == 3) return true;
            var camera=CameraController.Instance;
            var speed=SpeedControlScreen.Instance;
            float now=Time.unscaledTime;
            if (deadline == 0)
            {
                deadline=now+65;
                cameraPosition=camera.transform.position;
                zoom=camera.OrthographicSize;
            }
            if (now > deadline) throw new TimeoutException($"Upgrade visual role={Role(host)} stage={stage} id={id} frames={frames.Count} displacement={displacement:F3}");
            if (host && subject == null)
            {
                if (!CoopSession.IsHosting || Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                    throw new InvalidOperationException("Upgrade range requires an isolated host");
                subject=Components.LiveMinionIdentities.Items.First(d => d != null && ClusterUtil.GetMyWorldId(d)==ClusterManager.Instance.activeWorldId);
                id=subject.GetComponent<KPrefabID>().InstanceID;
                wasPaused=speed.IsPaused;
                oldSpeed=speed.GetSpeed();
                Set(room,"id",id.ToString());
            }
            if (!host && id == 0 && !int.TryParse(Get(room,"id"),out id)) return false;
            var target=host ? subject.GetComponent<KSelectable>() : NativeWorldMirror.GetDupeSelectable(id);
            if (target == null) return false;
            var position=target.transform.position;
            var center=position;
            center.y+=.65f;
            center.z=camera.transform.position.z;
            camera.SetTargetPos(center,4.25f,false);
            camera.SetPosition(center);
            camera.OrthographicSize=4.25f;
            if (stage == 2)
            {
                if (Get(room,"done") != "1") return false;
                camera.SetTargetPos(cameraPosition,zoom,false);
                camera.SetPosition(cameraPosition);
                camera.OrthographicSize=zoom;
                stage++;
                return true;
            }
            if (!host && !triggered)
            {
                SteamMatchmaking.SetLobbyMemberData(room,Key+"ready"+stage,"1");
                triggered=Get(room,"started"+stage)=="1";
            }
            if (host && !triggered)
            {
                if (SteamMatchmaking.GetLobbyMemberData(room,peer,Key+"ready"+stage)!="1") return false;
                int origin=Grid.PosToCell(subject);
                var nav=subject.GetComponent<Navigator>();
                navTarget=Enumerable.Range(0,Grid.CellCount)
                    .Where(c=>Grid.WorldIdx[c]==ClusterManager.Instance.activeWorldId && Grid.Visible[c]!=0 &&
                        !Grid.Solid[c] && Grid.IsValidCell(Grid.CellBelow(c)) && Grid.Solid[Grid.CellBelow(c)])
                    .Where(c=>Grid.GetCellDistance(origin,c)>=4 && Grid.GetCellDistance(origin,c)<=8 && nav.CanReach(c))
                    .DefaultIfEmpty(Grid.InvalidCell).First();
                if (!Grid.IsValidCell(navTarget)) throw new InvalidDataException("No reachable upgrade test destination");
                speed.SetSpeed(0);
                while (speed.IsPaused) speed.Unpause(false);
                nav.GetSMI<MoveToLocationMonitor.Instance>().MoveToLocation(navTarget);
                if (stage==0)
                {
                    var levels=subject.GetComponent<AttributeLevels>();
                    var athletics=levels.GetAttributeLevel(Db.Get().Attributes.Athletics.Id);
                    float before=Db.Get().AttributeConverters.MovementSpeed.Lookup(subject).Evaluate();
                    athletics.LevelUp(levels);
                    float after=Db.Get().AttributeConverters.MovementSpeed.Lookup(subject).Evaluate();
                    if (after <= before) throw new InvalidDataException("Athletics did not raise the original movement speed");
                    log(FormattableString.Invariant($"UPGRADE_INPUT type=athletics movementBefore={before:R} movementAfter={after:R}"));
                }
                else
                {
                    var resume=subject.GetComponent<MinionResume>();
                    int before=resume.TotalSkillPointsGained;
                    resume.AddExperience(MinionResume.CalculateNextExperienceBar(before)-resume.TotalExperienceGained+.1f);
                    if (resume.TotalSkillPointsGained!=before+1) throw new InvalidDataException("Native experience did not award a skill point");
                    log($"UPGRADE_INPUT type=skillPoint before={before} after={resume.TotalSkillPointsGained}");
                }
                Set(room,"started"+stage,"1");
                triggered=true;
            }
            if (!triggered) return false;
            var effect=target.GetComponentsInChildren<KBatchedAnimController>(false).FirstOrDefault(a =>
                a.transform.parent==target.transform && a.GetCurrentAnim()?.animFile?.name==NativeUpgradeVisuals.Asset && a.IsVisible());
            if (effect != null)
            {
                int frame=effect.GetCurrentFrameIndex()-effect.GetFirstFrameIndex();
                if (frame>=0 && frame<effect.GetCurrentNumFrames())
                {
                    if (frames.Count==0) firstPosition=position;
                    frames.Add(frame);
                    displacement=Mathf.Max(displacement,Vector2.Distance(firstPosition,position));
                }
                if (photo==null && frames.Count>=5 && displacement>=.16f)
                {
                    string dir=Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                    photo=new FixtureFrame(Path.Combine(dir,$"upgrade-{Role(host)}-{stage}.png"));
                    photo.Capture();
                }
                return false;
            }
            if (photo?.Complete!=true)
            {
                if (now>=reportAt)
                {
                    reportAt=now+3;
                    log($"UPGRADE_WAIT role={Role(host)} stage={stage} frames={frames.Count} moved={displacement:F3} nav={navTarget}");
                }
                return false;
            }
            if (host && SteamMatchmaking.GetLobbyMemberData(room,peer,Key+"seen"+stage)!="1") return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(room,Key+"seen"+stage,"1");
            log(FormattableString.Invariant($"UPGRADE_RANGE role={Role(host)} stage={stage} id={id} frames={frames.Count} followed={displacement:R} removed=True"));
            frames.Clear(); photo=null; displacement=0; triggered=false;
            stage++;
            if (host && stage==2)
            {
                speed.SetSpeed(oldSpeed);
                if (wasPaused) speed.Pause(false);
                Set(room,"done","1");
            }
            return false;
        }

        static string Role(bool host)=>host?"host":"guest";
        static string Get(CSteamID room,string field)=>SteamMatchmaking.GetLobbyData(room,Key+field);
        static void Set(CSteamID room,string field,string value)
        {
            if (!SteamMatchmaking.SetLobbyData(room,Key+field,value)) throw new IOException("Could not publish upgrade checkpoint");
        }
    }
}
