using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Queyang.Game
{
    internal sealed class BuildOptionsPanel
    {
        private readonly Dictionary<string, KeyValuePair<string, string>> choices =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.Ordinal);
        private string tool, message = "";
        private ManagementPage page;
        private Vector2 scroll;
        private float requestedAt;
        private bool waiting;
        public bool IsOpen => tool != null;
        public string ActionFor(string selected) =>
            choices.TryGetValue(selected, out var choice) ? choice.Value : selected;
        public string LabelFor(string selected) =>
            choices.TryGetValue(selected, out var choice) ? choice.Key : "默认方向 · 自动选材";

        public void Open(string selected)
        {
            tool = selected;
            page = null;
            Request("buildOptions/" + tool + "/0");
        }

        public void Close() { tool = null; page = null; waiting = false; }
        public void Reset() { Close(); choices.Clear(); }

        private void Request(string route)
        {
            waiting = true;
            requestedAt = Time.unscaledTime;
            message = "正在查询房主的建筑列表…";
            CoopSession.Command(route, 0, 0);
        }

        public void Receive(string result)
        {
            if (!IsOpen || !waiting || string.IsNullOrEmpty(result)) return;
            if (result.StartsWith("ERROR", StringComparison.Ordinal))
            { message = result; waiting = false; return; }
            if (!result.StartsWith("B1:", StringComparison.Ordinal)) return;
            try
            {
                var menu = ManagementPage.Unpack(ManagementPage.Prefix + result.Substring(3));
                foreach (var row in menu.Rows)
                    if (!IsPaging(row.Value) && !row.Value.StartsWith("build/" + tool + "/", StringComparison.Ordinal))
                        return;
                page = menu;
                message = "";
                scroll = Vector2.zero;
                waiting = false;
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is FormatException)
            { message = "房主返回的建筑选项格式无效。"; waiting = false; }
        }

        private bool IsPaging(string action) => action.StartsWith("buildOptions/" + tool + "/", StringComparison.Ordinal);

        public void Draw(float width, float height)
        {
            float left = (width - 580) / 2f, top = 30;
            GUI.Label(new Rect(left, top, 430, 30), page?.Title ?? "方向与材料");
            if (GUI.Button(new Rect(left + 460, top, 120, 30), "返回地图")) { Close(); return; }
            if (GUI.Button(new Rect(left, top + 46, 230, 32), "默认方向 · 自动选材"))
            {
                choices.Remove(tool);
                Close();
                return;
            }
            if (waiting && Time.unscaledTime - requestedAt > 10)
            { waiting = false; message = "房主回复超时。"; }
            if (GUI.Button(new Rect(left + 244, top + 46, 104, 32), "刷新")) Request("buildOptions/" + tool + "/0");
            if (!string.IsNullOrEmpty(message)) GUI.Label(new Rect(left, top + 90, 580, 36), message);
            if (page == null) return;
            var current = page;
            scroll = GUI.BeginScrollView(new Rect(left, top + 136, 580, height - top - 152), scroll,
                new Rect(0, 0, 550, current.Rows.Count * 43));
            bool enabled = GUI.enabled;
            try
            {
                GUI.enabled = enabled && !waiting;
                for (int i = 0; i < current.Rows.Count; i++)
                {
                    var row = current.Rows[i];
                    if (!GUI.Button(new Rect(0, 43 * i, 550, 38), row.Key)) continue;
                    if (IsPaging(row.Value)) Request(row.Value);
                    else { choices[tool] = row; Close(); }
                    break;
                }
            }
            finally { GUI.enabled = enabled; GUI.EndScrollView(); }
        }
    }
}
