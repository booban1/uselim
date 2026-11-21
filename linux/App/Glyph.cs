using System;
using System.Collections.Generic;

namespace Uselim
{
    // The tray icon, drawn pixel by pixel, the same asterisk as the Mac's bar and the Windows tray: Tabler's six strokes
    // from the centre, in the limits' tone (white, orange from 60%, red from 80%), grey while the figures are stale, with
    // an orange dot at its corner for a chat waiting on an answer. Smooth edges come from each pixel's distance to the
    // nearest stroke. Pixels go out as ARGB in network order, the tray standard's form.

    internal static class Glyph
    {
        // Tabler's six strokes, end points in its 24 unit box, each from the centre (12, 12)
        private static readonly (double X, double Y)[] spokes =
        {
            (20, 7.5), (12, 21), (4, 7.5), (20, 16.5), (12, 3), (4, 16.5),
        };

        // the tones on a dark panel, Mint's and GNOME's own; the Windows tray's colours on dark
        public static (byte R, byte G, byte B) Colour(Tone tone, bool stale)
        {
            if (stale) return (0x9A, 0x9A, 0x9A);
            return tone == Tone.Critical ? ((byte)0xFF, (byte)0x6B, (byte)0x7A)
                : tone == Tone.Warning ? ((byte)0xFC, (byte)0x8B, (byte)0x3E)
                : ((byte)0xF2, (byte)0xF2, (byte)0xF2);
        }

        private static readonly (byte R, byte G, byte B) dotColour = (0xFC, 0x8B, 0x3E);

        public static List<TrayItem.Pixmap> Icons(Tone tone, bool stale, bool dot)
        {
            var list = new List<TrayItem.Pixmap>();
            foreach (var size in new[] { 22, 32, 48 }) list.Add(Draw(size, Colour(tone, stale), dot));
            return list;
        }

        public static TrayItem.Pixmap Draw(int size, (byte R, byte G, byte B) ink, bool dot)
        {
            var k = size / 24.0;
            var half = 1.0 * k; // the stroke: 2 units wide
            var argb = new byte[size * size * 4];
            // the dot: 7 units across at the top right, the asterisk under it left whole
            var dotR = 3.5 * k;
            var dotX = size - dotR - 0.5 * k;
            var dotY = dotR + 0.5 * k;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var px = x + 0.5;
                    var py = y + 0.5;
                    var d = double.MaxValue;
                    foreach (var s in spokes) d = Math.Min(d, Segment(px, py, 12 * k, 12 * k, s.X * k, s.Y * k));
                    var a = Clamp(half + 0.5 - d);
                    double r = ink.R, g = ink.G, b = ink.B;
                    if (dot)
                    {
                        var dd = Math.Sqrt((px - dotX) * (px - dotX) + (py - dotY) * (py - dotY));
                        var da = Clamp(dotR + 0.5 - dd);
                        if (da > 0)
                        {
                            // the dot over the strokes
                            var outA = da + a * (1 - da);
                            r = (dotColour.R * da + ink.R * a * (1 - da)) / Math.Max(outA, 1e-6);
                            g = (dotColour.G * da + ink.G * a * (1 - da)) / Math.Max(outA, 1e-6);
                            b = (dotColour.B * da + ink.B * a * (1 - da)) / Math.Max(outA, 1e-6);
                            a = outA;
                        }
                    }
                    var i = (y * size + x) * 4;
                    argb[i] = (byte)Math.Round(a * 255);
                    argb[i + 1] = (byte)Math.Round(r);
                    argb[i + 2] = (byte)Math.Round(g);
                    argb[i + 3] = (byte)Math.Round(b);
                }
            return new TrayItem.Pixmap { Width = size, Height = size, Argb = argb };
        }

        private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // a point's distance to a segment
        private static double Segment(double px, double py, double ax, double ay, double bx, double by)
        {
            var dx = bx - ax;
            var dy = by - ay;
            var t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy);
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            var cx = ax + t * dx - px;
            var cy = ay + t * dy - py;
            return Math.Sqrt(cx * cx + cy * cy);
        }
    }
}
