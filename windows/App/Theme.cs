using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace Uselim
{
    // Light and dark, read from Windows' own switches: the taskbar's ink for the tray icon, the apps' mode for the panel
    internal sealed class Palette
    {
        public Color Back, Card, CardLine, Text, Sub, Accent, Orange, Red, Line, Hover, Track;

        public static bool TaskbarLight => Read("SystemUsesLightTheme", 0) == 1;
        public static bool AppsLight => Read("AppsUseLightTheme", 1) == 1;

        private static int Read(string name, int fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue(name) is int v ? v : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        // Windows 11's flyout colours
        public static Palette Current => AppsLight
            ? new Palette
            {
                Back = Hex(0xF3F3F3), Card = Hex(0xFFFFFF), CardLine = Hex(0xE5E5E5), Text = Hex(0x1B1B1B), Sub = Hex(0x616161),
                Accent = Hex(0x005FB8), Orange = Hex(0xF7630C), Red = Hex(0xD13438), Line = Hex(0xE0E0E0),
                Hover = Color.FromArgb(10, 0, 0, 0), Track = Hex(0xE1E1E1),
            }
            : new Palette
            {
                Back = Hex(0x2C2C2C), Card = Hex(0x373737), CardLine = Hex(0x3F3F3F), Text = Hex(0xFFFFFF), Sub = Hex(0xC5C5C5),
                Accent = Hex(0x60CDFF), Orange = Hex(0xFC8B3E), Red = Hex(0xFF6B7A), Line = Hex(0x404040),
                Hover = Color.FromArgb(18, 255, 255, 255), Track = Hex(0x4A4A4A),
            };

        public Color Of(Tone tone, bool stale) =>
            stale ? Sub : tone == Tone.Critical ? Red : tone == Tone.Warning ? Orange : Accent;

        private static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
    }

    // The drawing on the taskbar side: the asterisk, Tabler's "asterisk" (MIT, the set Uselim's app icon comes from),
    // six strokes from the centre of a 24 unit box, and the readout's two lines beside it. The tray icon is the
    // asterisk alone, with the orange dot while a chat waits on an answer.
    internal static class Glyph
    {
        public static readonly Color Orange = Color.FromArgb(0xF7, 0x63, 0x0C);
        public static readonly Color Red = Color.FromArgb(0xD1, 0x34, 0x38);
        private static readonly Color redOnDark = Color.FromArgb(0xFF, 0x6B, 0x7A);
        private static readonly Color orangeOnDark = Color.FromArgb(0xFC, 0x8B, 0x3E);

        // Tabler's six strokes, end points in its 24 unit box, each from the centre (12, 12)
        private static readonly PointF[] spokes =
        {
            new PointF(20, 7.5f), new PointF(12, 21), new PointF(4, 7.5f), new PointF(20, 16.5f), new PointF(12, 3), new PointF(4, 16.5f),
        };

        public static Color Ink(bool lightTaskbar) => lightTaskbar ? Color.FromArgb(27, 27, 27) : Color.White;

        public static Color ToneColour(Tone tone, bool lightTaskbar) =>
            tone == Tone.Critical ? (lightTaskbar ? Red : redOnDark) : tone == Tone.Warning ? (lightTaskbar ? Orange : orangeOnDark) : Ink(lightTaskbar);

        public static void Asterisk(Graphics g, RectangleF box, Color colour, float strokeUnits = 2f)
        {
            var k = Math.Min(box.Width, box.Height) / 24f;
            var ox = box.X + (box.Width - 24 * k) / 2;
            var oy = box.Y + (box.Height - 24 * k) / 2;
            using (var pen = new Pen(colour, strokeUnits * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                foreach (var p in spokes)
                    g.DrawLine(pen, ox + 12 * k, oy + 12 * k, ox + p.X * k, oy + p.Y * k);
        }

        // the tray icon: the asterisk, grey while the figures are stale, and the orange dot at its corner for a waiting chat
        public static Bitmap TrayIcon(int size, bool stale, bool dot, bool lightTaskbar)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            var ink = Ink(lightTaskbar);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                Asterisk(g, new RectangleF(0, 0, size, size), stale ? Color.FromArgb(128, ink) : ink, 2.2f);
                if (dot)
                {
                    var u = size / 16f;
                    var cx = size - 3f * u;
                    var cy = 3f * u;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (var clear = new SolidBrush(Color.Transparent))
                        g.FillEllipse(clear, cx - 4.3f * u, cy - 4.3f * u, 8.6f * u, 8.6f * u);
                    g.CompositingMode = CompositingMode.SourceOver;
                    using (var fill = new SolidBrush(Orange))
                        g.FillEllipse(fill, cx - 3f * u, cy - 3f * u, 6f * u, 6f * u);
                }
            }
            return bmp;
        }
    }
}
