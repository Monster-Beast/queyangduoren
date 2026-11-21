using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Queyang.Game
{
    internal sealed class BuildingPanel
    {
        private struct Option { public string Label, Action; }
        private readonly List<Option> options = new List<Option>(6);
        private int x, y, target, page, pages;
        private string title = "", notice = "";
        private bool waiting;
        public bool IsOpen { get; private set; }

        public void Open(int x, int y, ColonyCell cell)
        {
            this.x = x; this.y = y;
            target = cell.TargetId;
            title = cell.Building ?? "";
            IsOpen = true;
            pages = 1;
            Query(0);
        }

        public void Close()
        {
            IsOpen = false;
            waiting = false;
            options.Clear();
        }

        public bool TargetExists(ColonyView view)
        {
            int dx = x - view.X, dy = y - view.Y;
            return dx >= 0 && dx < ColonyView.Width && dy >= 0 && dy < ColonyView.Height &&
                view.Cells[dy * ColonyView.Width + dx].TargetId == target;
        }

        private void Query(int requested)
        {
            page = requested;
            waiting = true;
            options.Clear();
            notice = "正在读取建筑设置…";
            CoopSession.Command(FormattableString.Invariant($"building/{target}/{page}"), x, y);
        }

        public void Receive(string result)
        {
            if (!IsOpen || string.IsNullOrEmpty(result)) return;
            if (result.StartsWith("ERROR", StringComparison.Ordinal))
            {
                if (waiting) { notice = result; waiting = false; }
                return;
            }
            if (!result.StartsWith("BLD1 ", StringComparison.Ordinal)) return;
            var lines = result.Split('\n');
            var fields = lines[0].Split(' ');
            if (fields.Length != 6 || !Number(fields[1], out int px) || !Number(fields[2], out int py) ||
                !Number(fields[3], out int id) || !Number(fields[4], out int actualPage) ||
                !Number(fields[5], out int totalPages) || px != x || py != y || id != target ||
                totalPages < 1 || actualPage != Math.Min(page, totalPages - 1)) return;
            options.Clear();
            page = actualPage;
            pages = totalPages;
            string prefix = "b/" + target.ToString(CultureInfo.InvariantCulture) + "/";
            for (int i = 1; i < lines.Length && options.Count < 6; i++)
            {
                var fields2 = lines[i].TrimEnd('\r').Split('\t');
                if (fields2.Length != 2 || !fields2[0].StartsWith(prefix, StringComparison.Ordinal) ||
                    fields2[1].Length == 0) continue;
                options.Add(new Option { Action = fields2[0], Label = fields2[1] });
            }
            waiting = false;
            notice = options.Count == 0 ? "这座建筑没有可编辑选项。" : "";
        }

        private static bool Number(string text, out int number) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);

        public void Draw(float width, float height)
        {
            float left = (width - 600) / 2, top = (height - 460) / 2;
            GUI.Label(new Rect(left, top, 480, 30), title + "  (" + x + ", " + y + ")");
            if (GUI.Button(new Rect(left + 492, top, 108, 30), "返回地图")) { Close(); return; }
            if (GUI.Button(new Rect(left, top + 48, 100, 30), "刷新")) Query(page);
            bool enabled = GUI.enabled;
            try
            {
                GUI.enabled = enabled && !waiting && page > 0;
                if (GUI.Button(new Rect(left + 148, top + 48, 92, 30), "上一页")) Query(page - 1);
                GUI.enabled = enabled;
                GUI.Label(new Rect(left + 258, top + 48, 96, 30), (page + 1) + " / " + pages);
                GUI.enabled = enabled && !waiting && page < pages - 1;
                if (GUI.Button(new Rect(left + 360, top + 48, 96, 30), "下一页")) Query(page + 1);
                GUI.enabled = enabled && !waiting;
                for (int i = 0; i < options.Count; i++)
                    if (GUI.Button(new Rect(left, top + 96 + 46 * i, 600, 40), options[i].Label))
                    {
                        waiting = true;
                        notice = "等待房主确认…";
                        CoopSession.Command(options[i].Action, x, y);
                        break;
                    }
            }
            finally { GUI.enabled = enabled; }
            GUI.Label(new Rect(left, top + 388, 600, 62), notice);
        }
    }
}
