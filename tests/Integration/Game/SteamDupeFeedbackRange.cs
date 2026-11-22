using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using Queyang.Game;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;
using Klei.AI;

namespace Queyang.Integration
{
    internal sealed class SteamDupeFeedbackRange
    {
        const string Prefix = "qy_dupe_feedback_";
        static readonly Func<NameDisplayScreen, GameObject, NameDisplayScreen.Entry> EntryFor =
            AccessTools.MethodDelegate<Func<NameDisplayScreen, GameObject, NameDisplayScreen.Entry>>(
                AccessTools.Method(typeof(NameDisplayScreen), "GetEntry"));
        int id, cell = Grid.InvalidCell;
        float deadline, zoom, showAt;
        Vector3 cameraPosition;
        FixtureFrame photo;
        GameObject source;
        bool started, hidden, captured, complete;

        internal bool Check(CSteamID room, CSteamID peer, bool host, int round, Action<string> log)
        {
            if (Environment.GetEnvironmentVariable("QY_DUPE_FEEDBACK") != "1" || round != 1 || complete) return true;
            var camera = CameraController.Instance;
            float now = Time.realtimeSinceStartup;
            if (deadline == 0)
            {
                deadline = now + 32f;
                cameraPosition = camera.transform.position;
                zoom = camera.OrthographicSize;
            }
            if (now > deadline) throw new TimeoutException($"Duplicant feedback host={host} id={id} started={started} captured={captured} hidden={hidden}");
            if (host && source == null)
            {
                if (Util.GetKleiRootPath() != Environment.GetEnvironmentVariable("QY_SMOKE_ROOT"))
                    throw new InvalidOperationException("Duplicant feedback requires an isolated colony");
                source = Components.LiveMinionIdentities.Items.First(dupe => dupe != null &&
                    ClusterUtil.GetMyWorldId(dupe) == ClusterManager.Instance.activeWorldId).gameObject;
                id = source.GetComponent<KPrefabID>().InstanceID;
                cell = Grid.PosToCell(source);
                Publish(room, "dupe", id + "|" + cell);
            }
            if (!host && id == 0)
            {
                var value = Read(room, "dupe").Split('|');
                if (value.Length != 2) return false;
                id = int.Parse(value[0]);
                cell = int.Parse(value[1]);
            }
            var center = Grid.CellToPosCCC(cell, Grid.SceneLayer.Move);
            center.y += .8f;
            center.z = camera.transform.position.z;
            camera.SetTargetPos(center, 5f, false);
            camera.SetPosition(center);
            camera.OrthographicSize = 5f;
            var target = host ? source : NativeWorldMirror.GetDupeSelectable(id)?.gameObject;
            var screen = NameDisplayScreen.Instance;
            var popup = PopFXManager.Instance;
            var entry = target == null || screen == null ? null : EntryFor(screen, target);
            if (target == null || popup?.Ready() != true) return false;
            var thought = Db.Get().Thoughts.Encourage;
            var effect = Db.Get().effects.Get("WorkEncouraged");
            if (!started)
            {
                if (!host) SteamMatchmaking.SetLobbyMemberData(room, Prefix + "ready", "1");
                else
                {
                    if (entry?.thoughtBubble == null || SteamMatchmaking.GetLobbyMemberData(room, peer, Prefix + "ready") != "1")
                        return false;
                    // The fixture supplies the same native bubble and buff that a
                    // passersby acknowledgement displays during ordinary play.
                    screen.SetThoughtBubbleDisplay(target, true, thought.hoverText, thought.bubbleSprite, thought.sprite);
                    target.GetComponent<Effects>().Remove(effect);
                    target.GetComponent<Effects>().Add(effect, true);
                    showAt = now;
                }
                started = true;
            }
            if (!captured)
            {
                if (photo == null)
                {
                    bool bubble = entry?.thoughtBubble?.gameObject.activeInHierarchy == true &&
                        entry.thoughtBubble.GetReference<Image>("icon_sprite").sprite == thought.sprite;
                    bool text = UnityEngine.Object.FindObjectsByType<PopFX>(FindObjectsSortMode.None)
                        .Any(fx => fx.TextDisplay != null && fx.TextDisplay.isActiveAndEnabled && fx.TextDisplay.text == effect.Name);
                    if (!bubble || !text)
                    {
                        if (host && now - showAt > 2.5f) throw new InvalidDataException($"Native duplicant source did not display both cues: bubble={bubble} text={text} trigger={effect.triggerFloatingText}");
                        return false;
                    }
                    string folder = Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT"));
                    photo = new FixtureFrame(Path.Combine(folder, $"dupe-feedback-{Role(host)}.png"));
                    photo.Capture();
                    return false;
                }
                if (!photo.Complete) return false;
                log($"DUPE_FEEDBACK_VISIBLE role={Role(host)} id={id} bubble={thought.sprite.name} effect={effect.Id}");
                if (!host) SteamMatchmaking.SetLobbyMemberData(room, Prefix + "visible", "1");
                captured = true;
            }
            if (host && !hidden)
            {
                if (SteamMatchmaking.GetLobbyMemberData(room, peer, Prefix + "visible") != "1") return false;
                screen.SetThoughtBubbleDisplay(target, false, null, null, null);
                Publish(room, "hidden", "1");
                hidden = true;
            }
            if (!host)
            {
                if (Read(room, "hidden") != "1" || entry?.thoughtBubble?.gameObject.activeInHierarchy == true) return false;
                SteamMatchmaking.SetLobbyMemberData(room, Prefix + "hidden", "1");
            }
            else if (SteamMatchmaking.GetLobbyMemberData(room, peer, Prefix + "hidden") != "1") return false;
            log($"DUPE_FEEDBACK_HIDDEN role={Role(host)} id={id}");
            camera.SetTargetPos(cameraPosition, zoom, false);
            camera.SetPosition(cameraPosition);
            camera.OrthographicSize = zoom;
            complete = true;
            return true;
        }

        static string Role(bool host) => host ? "host" : "guest";
        static string Read(CSteamID room, string field) => SteamMatchmaking.GetLobbyData(room, Prefix + field);
        static void Publish(CSteamID room, string field, string value)
        {
            if (!SteamMatchmaking.SetLobbyData(room, Prefix + field, value)) throw new IOException("Could not publish duplicant feedback " + field);
        }
    }
}
