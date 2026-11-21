using System;
using UnityEngine;

namespace Queyang.Game
{
    // The guest only paints an immutable sample. No game Grid or simulation objects are constructed.
    internal sealed class ColonyMap : IDisposable
    {
        internal enum Overlay { Terrain, Temperature, Oxygen, Power, Gas, Liquid, Automation }
        private const int Pixels = 8;
        private const int TextureWidth = ColonyView.Width * Pixels;
        private readonly Color32[] colors = new Color32[TextureWidth * ColonyView.Height * Pixels];
        private Texture2D texture;
        public Texture Texture => texture;

        public void Paint(ColonyView view, Overlay overlay)
        {
            if (!texture)
                texture = new Texture2D(TextureWidth, ColonyView.Height * Pixels, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Co-op viewport"
                };
            for (int y = 0; y < ColonyView.Height; y++)
                for (int x = 0; x < ColonyView.Width; x++)
                {
                    var cell = view.Cells[y * ColonyView.Width + x];
                    var baseColor = BaseColor(cell, overlay);
                    bool known = (cell.Flags & 1) != 0;
                    bool utility = overlay >= Overlay.Power;
                    int layer = (int)overlay - (int)Overlay.Power;
                    int directions = utility ? (cell.Connections >> (layer * 4)) & 15 : 0;
                    bool present = utility && (cell.Utilities & (1 << layer)) != 0;
                    var utilityColor = UtilityColor(overlay, cell);
                    for (int j = 0; j < Pixels; j++)
                        for (int i = 0; i < Pixels; i++)
                        {
                            Color32 pixel = baseColor;
                            if (known && utility)
                            {
                                if (present && UtilityPixel(i, j, directions)) pixel = utilityColor;
                            }
                            else if (known)
                            {
                                if (i == 0 || j == 0) pixel = Shade(baseColor);
                                if ((cell.Flags & (16 | 32)) != 0 && BuildingPixel(cell.BuildingStyle, i, j))
                                    pixel = (cell.BuildingStyle & 128) != 0 ? new Color32(233, 107, 99, 255) :
                                        (cell.Flags & 32) != 0 ? new Color32(126, 200, 207, 255) : new Color32(219, 207, 182, 255);
                                if ((cell.Flags & 32) != 0 && i >= 1 && i <= 6 && j >= 1 && j <= 6 &&
                                    (i == 1 || i == 6 || j == 1 || j == 6))
                                    pixel = new Color32(93, 228, 227, 255);
                                if ((cell.Flags & 8) != 0 && i > 1 && i < 6 && i == j)
                                    pixel = new Color32(253, 220, 123, 255);
                            }
                            colors[(y * Pixels + j) * TextureWidth + x * Pixels + i] = pixel;
                        }
                }
            texture.SetPixels32(colors);
            texture.Apply(false, false);
        }

        private static Color32 BaseColor(ColonyCell c, Overlay overlay)
        {
            if ((c.Flags & 1) == 0) return new Color32(16, 17, 25, 255);
            if (overlay >= Overlay.Power) return (c.Flags & 2) != 0
                ? new Color32(58, 51, 51, 255) : new Color32(40, 42, 51, 255);
            if (overlay == Overlay.Temperature)
                return Color.Lerp(new Color32(61, 100, 178, 255), new Color32(227, 98, 54, 255),
                    Mathf.InverseLerp(253.15f, 343.15f, c.Temperature));
            if (overlay == Overlay.Oxygen)
            {
                bool oxygen = (c.Flags & 64) != 0;
                return oxygen ? (Color32)Color.Lerp(new Color32(40, 59, 70, 255), new Color32(104, 211, 234, 255),
                    Mathf.Clamp01(c.Mass / 2f)) : new Color32(73, 55, 68, 255);
            }
            var substrate = new Color32(c.R, c.G, c.B, 255);
            if ((c.Flags & (2 | 4)) != 0) return substrate;
            return Color.Lerp(new Color32(37, 32, 49, 255), substrate, Mathf.Clamp01(c.Mass / 2f) * .7f);
        }

        private static bool BuildingPixel(byte style, int x, int y)
        {
            int ox = x;
            switch ((style >> 4) & 7)
            {
                case 1: x = 7 - y; y = ox; break;
                case 2: x = 7 - x; y = 7 - y; break;
                case 3: x = y; y = 7 - ox; break;
                case 5: x = 7 - x; break;
                case 6: y = 7 - y; break;
            }
            switch (style & 15)
            {
                case 1: return x == 1 || x == 6 || x > 1 && x < 6 && (y == 2 || y == 6);
                case 2: return y == 1 || y == 6 || x == (y < 4 ? 2 : 5);
                case 3: return x == 1 || x == 6 || y == 6 || x == 4 && y == 3;
                case 4: return y >= 1 && y <= 5 && (x == 1 || x == 6 || y == 1 || y == 5);
                case 5: return y == 1 || y >= 2 && y <= 5 && (x == 2 || x == 5 || y == 5);
                case 6: return x >= 1 && x <= 6 && y >= 1 && y <= 6 &&
                    (x == 1 || x == 6 || y == 1 || y == 6 || x == y);
                default: return x >= 2 && x <= 5 && y >= 2 && y <= 5;
            }
        }

        private static bool UtilityPixel(int i, int j, int directions)
        {
            bool centerY = j == 3 || j == 4, centerX = i == 3 || i == 4;
            return (centerX && centerY) ||
                centerY && (i < 3 && (directions & 1) != 0 || i > 4 && (directions & 2) != 0) ||
                centerX && (j > 4 && (directions & 4) != 0 || j < 3 && (directions & 8) != 0);
        }

        private static Color32 UtilityColor(Overlay overlay, ColonyCell c)
        {
            switch (overlay)
            {
                case Overlay.Power: return new Color32(245, 205, 103, 255);
                case Overlay.Liquid: return new Color32(96, 169, 235, 255);
                case Overlay.Gas: return new Color32(189, 187, 228, 255);
                case Overlay.Automation: return (c.Utilities & 16) != 0
                    ? new Color32(102, 224, 136, 255) : new Color32(209, 93, 98, 255);
                default: return default(Color32);
            }
        }

        private static Color32 Shade(Color32 c) =>
            new Color32((byte)(c.r * .84f), (byte)(c.g * .84f), (byte)(c.b * .84f), 255);

        public void Dispose()
        {
            if (texture) UnityEngine.Object.Destroy(texture);
            texture = null;
        }
    }
}
