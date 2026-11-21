using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Queyang.Game
{
    public static partial class HostWorld
    {
        static void AddProxyControls(List<Setting> options, GameObject building)
        {
            var deconstruct = building.GetComponent<Deconstructable>();
            if (deconstruct != null && deconstruct.allowDeconstruction)
            {
                bool ordered = deconstruct.IsMarkedForDeconstruction();
                options.Add(new Setting("deconstruct/" + ordered, ordered ? "取消拆除建筑" : "指派拆除建筑",
                    () => EditTarget(ordered ? "cancel" : "deconstruct", building)));
            }
            var door = building.GetComponent<Door>();
            if (door != null)
            {
                foreach (var value in new[] { Door.ControlState.Auto, Door.ControlState.Opened, Door.ControlState.Locked })
                {
                    if (value == door.RequestedState) continue;
                    string label = value == Door.ControlState.Auto ? "自动门禁" : value == Door.ControlState.Opened ? "保持开启" : "锁定门禁";
                    options.Add(new Setting("door/" + door.CurrentState + "/" + door.RequestedState + "/" + value, label,
                        () => door.QueueStateChange(value)));
                }
            }
            var generator = building.GetComponent<ManualGenerator>();
            if (generator != null)
                AddRange(options, "battery", "充电阈值", "%", generator.GetSliderValue(0), generator.GetSliderMin(0),
                    generator.GetSliderMax(0), 5, value => generator.SetSliderValue(value, 0));
            var storage = building.GetComponent<IUserControlledCapacity>();
            if (storage != null && storage.ControlEnabled())
                AddRange(options, "capacity", "储存上限", storage.CapacityUnits, storage.UserMaxCapacity, storage.MinCapacity,
                    storage.MaxCapacity, Mathf.Max(1, (storage.MaxCapacity - storage.MinCapacity) / 10), value => storage.UserMaxCapacity = value);
            var priority = building.GetComponent<Prioritizable>();
            if (priority == null || !priority.IsPrioritizable()) return;
            var current = priority.GetMasterPriority();
            if (current.priority_class != PriorityScreen.PriorityClass.basic) return;
            AddRange(options, "priority", "建筑优先级", "", current.priority_value, 1, 9, 1, v =>
                priority.SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.basic, (int)v)));
        }

        static void AddRange(List<Setting> options, string id, string title, string unit, float current,
            float min, float max, float step, Action<float> set)
        {
            if (float.IsNaN(current) || float.IsInfinity(current) || max <= min) return;
            foreach (var direction in new[] { -1, 1 })
            {
                float next = Mathf.Clamp(current + step * direction, min, max);
                if (next == current) continue;
                string label = string.Format(CultureInfo.InvariantCulture, "{0} {1:g5}{2} → {3:g5}{2}", title, current, unit, next);
                options.Add(new Setting(FormattableString.Invariant($"{id}/{current:R}/{next:R}"), label, () => set(next)));
            }
        }
    }
}
