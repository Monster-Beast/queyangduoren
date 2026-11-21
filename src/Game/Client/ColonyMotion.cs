using System;
using System.Collections.Generic;

namespace Queyang.Game
{
    // Only visuals interpolate. Orders and hit tests always use the latest confirmed grid.
    internal sealed class ColonyMotion
    {
        private struct Track
        {
            public float FromX, FromY, ToX, ToY;
        }

        private Dictionary<int, Track> current = new Dictionary<int, Track>(64);
        private Dictionary<int, Track> next = new Dictionary<int, Track>(64);
        private float started, duration;

        public void Apply(ColonyView view, float now)
        {
            foreach (var d in view.Dupes)
            {
                long dx = (long)d.X - view.X, dy = (long)d.Y - view.Y;
                if (dx < 0 || dy < 0 || dx >= ColonyView.Width || dy >= ColonyView.Height ||
                    (view.Cells[(int)dy * ColonyView.Width + (int)dx].Flags & 1) == 0) continue;
                float x = d.X + d.SubX / 255f, y = d.Y + d.SubY / 255f;
                if (!TryGet(d.Id, now, out float oldX, out float oldY) ||
                    Math.Abs(oldX - x) > 3 || Math.Abs(oldY - y) > 3)
                { oldX = x; oldY = y; }
                next[d.Id] = new Track { FromX = oldX, FromY = oldY, ToX = x, ToY = y };
            }
            duration = Math.Min(.3f, Math.Max(.05f, now - started));
            started = now;
            var old = current;
            current = next;
            next = old;
            next.Clear();
        }

        public bool TryGet(int id, float now, out float x, out float y)
        {
            if (!current.TryGetValue(id, out var t)) { x = y = 0; return false; }
            float f = Math.Max(0, Math.Min(1, (now - started) / duration));
            x = t.FromX + (t.ToX - t.FromX) * f;
            y = t.FromY + (t.ToY - t.FromY) * f;
            return true;
        }

        public void Clear() { current.Clear(); next.Clear(); }
    }
}
