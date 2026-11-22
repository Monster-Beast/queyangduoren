using System;
using System.IO;
using System.Linq;
using Queyang.Game;
using Steamworks;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class FacilityRangeWitness
    {
        static readonly string[] Prefabs = { RationBoxConfig.ID, WashBasinConfig.ID, MedicalCotConfig.ID };
        readonly string layout = Environment.GetEnvironmentVariable("QY_FACILITY_RANGE_STATIONS");
        (int Cell, int Id)[] stations;
        int stage;
        FixtureFrame photo;
        float deadline, selectedAt;
        string readings;
        string observed;

        internal bool Check(CSteamID lobby, CSteamID member, bool host, Action<string> log)
        {
            if (string.IsNullOrEmpty(layout) || stage == 3) return true;
            if (stations == null)
            {
                stations = layout.Split(',').Select(spec =>
                {
                    var fields = spec.Split(':');
                    return (int.Parse(fields[0]), int.Parse(fields[1]));
                }).ToArray();
                if (stations.Length != Prefabs.Length) throw new InvalidDataException("Facility range needs three stations");
                deadline = Time.unscaledTime + 50;
            }
            if (Time.unscaledTime > deadline)
                throw new TimeoutException($"Timed out selecting prepared station {Prefabs[stage]} on {(host ? "host" : "guest")}: {observed}");
            var (cell, id) = stations[stage];
            var selectable = host ? HostWorld.FindBuilding(cell)?.GetComponent<KSelectable>() :
                NativeWorldMirror.GetBuildingSelectable(cell, id);
            observed = $"cell={cell} id={id} selectedObject={selectable != null}";
            if (selectable == null) return false;
            var obj = selectable.gameObject;
            if (obj.GetComponent<KPrefabID>()?.InstanceID != id || obj.PrefabID() != new Tag(Prefabs[stage]))
                throw new InvalidDataException("The selected object does not match the prepared range");
            var camera = CameraController.Instance;
            var position = Grid.CellToPosCCC(cell, Grid.SceneLayer.Move);
            position.z = camera.transform.position.z;
            camera.SetTargetPos(position, 8, false);
            if (DetailsScreen.Instance?.target != obj)
            {
                PlayerController.Instance.ActivateTool(SelectTool.Instance);
                SelectTool.Instance.Select(selectable, false);
                selectedAt = Time.unscaledTime;
                observed += " detailsTargetPending=True";
                return false;
            }
            string role = host ? "host" : "guest";
            if (photo == null)
            {
                if (Time.unscaledTime - selectedAt < .65f) return false;
                bool ready = ReadStartingState(obj, out readings);
                observed += " " + readings;
                if (!ready) return false;
                photo = new FixtureFrame(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("QY_SMOKE_REPORT")),
                    $"facility-{role}-{Prefabs[stage]}.png"));
                photo.Capture();
                return false;
            }
            if (!photo.Complete) return false;
            string key = "qy_facility_ready_" + stage;
            if (host && SteamMatchmaking.GetLobbyMemberData(lobby, member, key) != "1") return false;
            if (!host) SteamMatchmaking.SetLobbyMemberData(lobby, key, "1");
            log($"FACILITY_RANGE_STATION role={role} prefab={Prefabs[stage]} cell={cell} id={id} selected=True {readings}");
            stage++;
            photo = null;
            SelectTool.Instance.Select(null, false);
            return stage == 3;
        }

        static bool ReadStartingState(GameObject obj, out string readings)
        {
            if (obj.GetComponent<Clinic>() != null)
            {
                var assignable = obj.GetComponent<Assignable>();
                bool assigned = assignable?.assignee != null;
                readings = $"bedAssigned={assigned}";
                return assignable != null && !assigned;
            }
            var storage = obj.GetComponent<Storage>();
            var wash = obj.GetComponent<HandSanitizer>();
            if (wash != null)
            {
                float clean = storage.FindPrimaryElement(SimHashes.Water)?.Mass ?? 0;
                float waste = storage.FindPrimaryElement(SimHashes.DirtyWater)?.Mass ?? 0;
                var direction = obj.GetComponent<DirectionControl>().allowedDirection;
                readings = FormattableString.Invariant($"cleanKg={clean} wasteKg={waste} direction={direction}");
                return Mathf.Approximately(clean, 40) && Mathf.Approximately(waste, 10) &&
                    direction == WorkableReactable.AllowedDirection.Any;
            }
            float food = storage.MassStored();
            readings = FormattableString.Invariant($"foodKg={food} capacityKg={storage.capacityKg}");
            return Mathf.Approximately(food, 6) && Mathf.Approximately(storage.capacityKg, 150);
        }
    }
}
