using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Queyang.Game
{
    internal static partial class NativeManagement
    {
        internal static NativeProxySettings SelectedProxy
        {
            get
            {
                if (SelectTool.Instance == null) return null;
                var selected = Selection.GetValue(SelectTool.Instance) as KSelectable;
                return selected == null ? null : selected.GetComponent<NativeProxySettings>();
            }
        }
        internal static void ProxyCommand(string route, int cell)
        {
            if (!Grid.IsValidCell(cell)) return;
            Grid.CellToXY(cell, out int x, out int y);
            Send(route, x, y);
        }
    }

    // Uses ONI's own selection action menu. A proxy has no Storage, Generator, Workable or ChoreProvider.
    internal sealed class NativeProxySettings : KMonoBehaviour
    {
        readonly List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();
        int cell, target, page, pages;
        string previous;

        internal void Initialize(int anchorCell, int hostTarget)
        {
            cell = anchorCell;
            target = hostTarget;
        }
        protected override void OnSpawn()
        {
            base.OnSpawn();
            Subscribe((int)GameHashes.RefreshUserMenu, PopulateMenu);
        }
        internal void Poll()
        {
            if (this != NativeManagement.SelectedProxy || target <= 0) return;
            if (GetComponent<Telepad>() != null) return;
            NativeManagement.ProxyCommand(FormattableString.Invariant($"proxy/{target}/{page}"), cell);
        }
        internal int HostTarget => target;
        internal int AnchorCell => cell;
        internal void Receive(string text)
        {
            if (this != NativeManagement.SelectedProxy || !Grid.IsValidCell(cell)) return;
            var lines = text.Split('\n');
            var h = lines[0].Split(' ');
            Grid.CellToXY(cell, out int x, out int y);
            if (h.Length != 6 || h[0] != "BLD1" || NativeManagement.Number(h[1]) != x ||
                NativeManagement.Number(h[2]) != y || NativeManagement.Number(h[3]) != target) return;
            int got = NativeManagement.Number(h[4]), count = NativeManagement.Number(h[5]);
            if (count < 1 || got != Math.Min(page, count - 1) || lines.Length > 7 || previous == text) return;
            var prefix = "setproxy/" + target.ToString(CultureInfo.InvariantCulture) + "/";
            rows.Clear();
            foreach (var line in lines)
            {
                int tab = line.IndexOf('\t');
                if (tab > 0 && tab < line.Length - 1 && line.StartsWith(prefix, StringComparison.Ordinal))
                    rows.Add(new KeyValuePair<string,string>(line.Substring(0, tab), line.Substring(tab + 1)));
            }
            pages = count; page = got; previous = text;
            global::Game.Instance.userMenu.Refresh(gameObject);
        }
        void PopulateMenu(object _)
        {
            if (!NativeGuestScene.Ready || target <= 0) return;
            int order = 0;
            foreach (var row in rows)
            {
                string route = row.Key;
                Button(row.Value, () => {
                    if (this != null && this == NativeManagement.SelectedProxy)
                        NativeManagement.ProxyCommand(route, cell);
                }, order++);
            }
            if (page > 0) Button("上一页", () => TurnPage(-1), order++);
            if (page + 1 < pages) Button("下一页", () => TurnPage(1), order);
        }
        void TurnPage(int delta)
        {
            if (this == null || this != NativeManagement.SelectedProxy) return;
            page = Math.Max(0, Math.Min(pages - 1, page + delta));
            previous = null; rows.Clear();
            Poll();
        }
        void Button(string label, System.Action click, int order)
        {
            global::Game.Instance.userMenu.AddButton(gameObject, new KIconButtonMenu.ButtonInfo("action_building_disabled",
                label, click, tooltipText: label), order);
        }
    }
}
